using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenShow
{
    internal sealed class HostSession
    {
        private readonly TcpListener listener;
        private readonly object gate = new object();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private TcpClient peer;

        internal HostSession(IPAddress address, int port)
        {
            listener = new TcpListener(address, port);
            listener.ExclusiveAddressUse = true;
        }

        internal void Run(Action<Invitation> ready, Func<IPEndPoint, CancellationToken, Decision> authorize,
            Action<Decision> started)
        {
            var stop = cancellation.Token;
            try
            {
                using (var certificate = Protocol.MakeCertificate())
                {
                    lock (gate)
                    {
                        stop.ThrowIfCancellationRequested();
                        listener.Start(4);
                    }
                    var invitation = Invitation.Create((IPEndPoint)listener.LocalEndpoint, certificate);
                    ready(invitation);
                    while (!stop.IsCancellationRequested)
                    {
                        bool valid = false;
                        using (TcpClient client = listener.AcceptTcpClient())
                        {
                            lock (gate)
                            {
                                stop.ThrowIfCancellationRequested();
                                peer = client;
                            }
                            try
                            {
                                using (var deadline = new Timer(socket => ((TcpClient)socket).Close(),
                                    client, Protocol.NetworkTimeout + 2000, Timeout.Infinite))
                                using (var stream = Protocol.OpenHost(client, certificate))
                                using (var reader = new BinaryReader(stream))
                                using (var writer = new BinaryWriter(stream))
                                {
                                    bool mobile;
                                    valid = Protocol.CheckHello(reader, invitation, out mobile);
                                    if (!valid)
                                    {
                                        writer.Write((byte)Decision.InvalidCode);
                                        continue;
                                    }
                                    deadline.Change(Timeout.Infinite, Timeout.Infinite);
                                    lock (gate) listener.Stop();
                                    var decision = authorize((IPEndPoint)client.Client.RemoteEndPoint, stop);
                                    stop.ThrowIfCancellationRequested();
                                    writer.Write((byte)decision);
                                    if (decision == Decision.View || decision == Decision.Control)
                                    {
                                        started(decision);
                                        StreamDesktop(client, reader, writer, decision == Decision.Control, mobile, stop);
                                    }
                                }
                            }
                            catch (AuthenticationException) { if (valid) throw; }
                            catch (IOException) { if (valid) throw; }
                            catch (ObjectDisposedException) { if (valid) throw; }
                            finally
                            {
                                lock (gate) peer = null;
                            }
                        }
                        if (valid) return;
                    }
                }
            }
            finally
            {
                Stop();
            }
        }

        private static void StreamDesktop(TcpClient client, BinaryReader reader, BinaryWriter writer,
            bool control, bool mobile, CancellationToken stop)
        {
            client.ReceiveTimeout = 0;
            Task input = Task.Run(delegate
            {
                try
                {
                    using (var desktop = new DesktopInput())
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            var command = InputCommand.Read(reader);
                            if (!control || (!mobile && command.Kind == InputKind.Text))
                                throw new InvalidDataException("Команда недоступна в текущем сеансе.");
                            if (!stop.IsCancellationRequested) desktop.Apply(command);
                        }
                    }
                }
                catch (IOException) { }
                catch (InvalidDataException) { }
                catch (ObjectDisposedException) { }
                finally { client.Close(); }
            });

            try
            {
                using (var capture = new DesktopCapture())
                {
                    var timer = new Stopwatch();
                    while (!stop.IsCancellationRequested && !input.IsCompleted)
                    {
                        timer.Restart();
                        Protocol.WriteFrame(writer, capture.Capture());
                        int delay = Math.Max(0, 100 - (int)timer.ElapsedMilliseconds);
                        if (stop.WaitHandle.WaitOne(delay)) break;
                    }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                client.Close();
                input.GetAwaiter().GetResult();
            }
        }

        internal void Stop()
        {
            lock (gate)
            {
                cancellation.Cancel();
                listener.Stop();
                if (peer != null) peer.Close();
            }
        }
    }

    internal sealed class ViewerSession
    {
        private readonly Invitation invitation;
        private readonly TcpClient client = new TcpClient(AddressFamily.InterNetwork);
        private readonly object gate = new object();
        private readonly LinkedList<InputCommand> pending = new LinkedList<InputCommand>();
        private bool stopped;

        internal ViewerSession(Invitation invitation)
        {
            this.invitation = invitation;
        }

        internal void Run(Action waiting, Action<Decision> connected, Action<Bitmap> frame)
        {
            try
            {
                Task connection = client.ConnectAsync(invitation.Endpoint.Address, invitation.Endpoint.Port);
                if (!connection.Wait(Protocol.NetworkTimeout))
                {
                    connection.ContinueWith(task => { var exception = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    throw new TimeoutException("Компьютер не отвечает. Проверьте сеть и адрес в приглашении.");
                }
                using (var stream = Protocol.OpenViewer(client, invitation))
                using (var reader = new BinaryReader(stream))
                {
                    Protocol.WriteHello(stream, invitation);
                    waiting();
                    stream.ReadTimeout = Protocol.ApprovalTimeout + Protocol.NetworkTimeout;
                    var decision = (Decision)reader.ReadByte();
                    if (decision == Decision.Denied) throw new InvalidOperationException("Владелец отклонил подключение.");
                    if (decision == Decision.Expired) throw new TimeoutException("Время подтверждения истекло.");
                    if (decision == Decision.InvalidCode) throw new InvalidOperationException("Приглашение недействительно.");
                    if (decision != Decision.View && decision != Decision.Control)
                        throw new InvalidDataException("Получен неизвестный ответ.");
                    stream.ReadTimeout = Protocol.NetworkTimeout;
                    connected(decision);
                    Task writer = decision == Decision.Control ? Task.Run(() => SendInput(stream)) : null;
                    try
                    {
                        while (true) frame(Protocol.ReadFrame(reader));
                    }
                    finally
                    {
                        Stop();
                        if (writer != null) writer.GetAwaiter().GetResult();
                    }
                }
            }
            finally { Stop(); }
        }

        internal void Queue(InputCommand command)
        {
            lock (gate)
            {
                if (stopped) return;
                if (command.Kind == InputKind.ReleaseAll) pending.Clear();
                if (command.Kind == InputKind.MouseMove && pending.Last != null &&
                    pending.Last.Value.Kind == InputKind.MouseMove)
                    pending.Last.Value = command;
                else if (pending.Count >= 256)
                    Stop();
                else
                    pending.AddLast(command);
                Monitor.Pulse(gate);
            }
        }

        private void SendInput(Stream stream)
        {
            var buffer = new byte[9];
            try
            {
                while (true)
                {
                    InputCommand command;
                    lock (gate)
                    {
                        while (!stopped && pending.Count == 0) Monitor.Wait(gate);
                        if (stopped) return;
                        command = pending.First.Value;
                        pending.RemoveFirst();
                    }
                    command.Write(stream, buffer);
                }
            }
            catch (IOException) { Stop(); }
            catch (ObjectDisposedException) { Stop(); }
        }

        internal void Stop()
        {
            lock (gate)
            {
                if (stopped) return;
                stopped = true;
                pending.Clear();
                client.Close();
                Monitor.PulseAll(gate);
            }
        }
    }
}
