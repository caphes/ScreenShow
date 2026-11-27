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
        private readonly PairStore pairs;
        private readonly NetworkAddress relay;
        private readonly NetworkAddress publicAddress;
        private TcpClient peer;

        internal HostSession(IPAddress address, int port, PairStore pairs = null, NetworkAddress publicAddress = null)
        {
            listener = new TcpListener(address, port);
            listener.ExclusiveAddressUse = true;
            this.pairs = pairs;
            this.publicAddress = publicAddress;
        }

        internal HostSession(NetworkAddress relay, PairStore pairs)
        {
            this.relay = relay;
            this.pairs = pairs;
        }

        internal void Run(Action<Invitation> ready, Func<IPEndPoint, CancellationToken, Decision> authorize,
            Action<Decision> started)
        {
            var stop = cancellation.Token;
            byte[] relayKey = null;
            try
            {
                using (var certificate = pairs == null ? Protocol.MakeCertificate() : pairs.OpenIdentity())
                {
                    lock (gate)
                    {
                        stop.ThrowIfCancellationRequested();
                        if (listener != null) listener.Start(4);
                    }
                    if (relay != null) relayKey = pairs.GetRelayKey();
                    var invitation = relay != null ? Invitation.CreateRelay(relay, RelayTransport.Route(relayKey), certificate)
                        : publicAddress != null ? Invitation.CreatePublic(publicAddress, certificate)
                        : Invitation.Create((IPEndPoint)listener.LocalEndpoint, certificate, pairs != null, pairs != null);
                    if (relay == null) ready(invitation);
                    while (!stop.IsCancellationRequested)
                    {
                        bool valid = false;
                        bool transportReady = relay == null;
                        using (TcpClient client = relay == null ? listener.AcceptTcpClient() : new TcpClient())
                        {
                            lock (gate)
                            {
                                stop.ThrowIfCancellationRequested();
                                peer = client;
                            }
                            try
                            {
                                if (relay != null)
                                {
                                    RelayTransport.Connect(client, relay, relayKey, true);
                                    ready(invitation);
                                    RelayTransport.WaitForViewer(client);
                                    transportReady = true;
                                }
                                using (var deadline = new Timer(socket => ((TcpClient)socket).Close(),
                                    client, Protocol.NetworkTimeout + 2000, Timeout.Infinite))
                                using (var stream = Protocol.OpenHost(client, certificate))
                                using (var reader = new BinaryReader(stream))
                                using (var writer = new BinaryWriter(stream))
                                {
                                    bool mobile;
                                    bool privatePairToken;
                                    var token = Protocol.ReadHello(reader, out mobile, out privatePairToken);
                                    bool newPair = token != null && privatePairToken == invitation.PrivatePairToken &&
                                        Protocol.Same(token, invitation.Token);
                                    var device = token != null && !newPair && pairs != null ? pairs.FindIncoming(token) : null;
                                    valid = newPair || (device != null && device.Invitation.PrivatePairToken == privatePairToken &&
                                        device.Invitation.NetworkMode == invitation.NetworkMode);
                                    if (!valid)
                                    {
                                        writer.Write((byte)Decision.InvalidCode);
                                        continue;
                                    }
                                    deadline.Change(Timeout.Infinite, Timeout.Infinite);
                                    var endpoint = relay == null ? (IPEndPoint)client.Client.RemoteEndPoint : null;
                                    client.ReceiveTimeout = 0;
                                    using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(stop))
                                    {
                                        Task<byte> first = Task.Run(() => reader.ReadByte());
                                        first.ContinueWith(failure =>
                                        {
                                            if (failure.Exception != null)
                                                try { waiting.Cancel(); } catch (ObjectDisposedException) { }
                                        }, TaskContinuationOptions.ExecuteSynchronously);
                                        Decision decision;
                                        try
                                        {
                                            decision = newPair ? authorize(endpoint, waiting.Token) : device.Access;
                                            waiting.Token.ThrowIfCancellationRequested();
                                        }
                                        catch (OperationCanceledException)
                                        {
                                            stop.ThrowIfCancellationRequested();
                                            decision = Decision.Denied;
                                        }
                                        if (!waiting.IsCancellationRequested)
                                        {
                                            if (!newPair)
                                            {
                                                device = pairs.FindIncoming(token);
                                                decision = device == null ? Decision.InvalidCode : device.Access;
                                            }
                                            bool approved = decision == Decision.View || decision == Decision.Control;
                                            byte[] savedToken = token;
                                            if (approved && newPair && pairs != null)
                                            {
                                                savedToken = Invitation.NewToken();
                                                try { pairs.SaveIncoming(invitation.WithToken(savedToken), endpoint == null ? null : endpoint.Address, mobile, decision); }
                                                catch (IOException error)
                                                {
                                                    throw new InvalidOperationException("Не удалось сохранить привязку устройства.", error);
                                                }
                                                invitation = invitation.WithToken(Invitation.NewToken());
                                            }
                                            if (approved && privatePairToken)
                                            {
                                                var response = new byte[1 + Invitation.TokenSize];
                                                response[0] = (byte)decision;
                                                savedToken.CopyTo(response, 1);
                                                writer.Write(response);
                                            }
                                            else writer.Write((byte)decision);
                                            if (approved)
                                            {
                                                started(decision);
                                                StreamDesktop(client, reader, writer, first, decision == Decision.Control, mobile, stop);
                                            }
                                        }
                                        client.Close();
                                    }
                                }
                            }
                            catch (AuthenticationException) { if (valid) throw; }
                            catch (IOException) { if ((!transportReady || (valid && pairs == null)) && !stop.IsCancellationRequested) throw; }
                            catch (ObjectDisposedException) { if (valid || !transportReady) throw; }
                            finally
                            {
                                lock (gate) peer = null;
                            }
                        }
                        if (valid)
                        {
                            if (pairs == null) return;
                            if (relay == null && !stop.IsCancellationRequested) ready(invitation);
                        }
                    }
                }
            }
            finally
            {
                if (relayKey != null) Array.Clear(relayKey, 0, relayKey.Length);
                Stop();
            }
        }

        private static void StreamDesktop(TcpClient client, BinaryReader reader, BinaryWriter writer,
            Task<byte> first, bool control, bool mobile, CancellationToken stop)
        {
            Task input = Task.Run(delegate
            {
                try
                {
                    using (var desktop = new DesktopInput())
                    {
                        byte? kind = first.GetAwaiter().GetResult();
                        while (!stop.IsCancellationRequested)
                        {
                            var command = InputCommand.Read(reader, kind);
                            kind = null;
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
                if (listener != null) listener.Stop();
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

        internal void Run(Action waiting, Action<Decision> connected, Action<Bitmap> frame, Action<Invitation> linked = null)
        {
            try
            {
                if (invitation.Relay != null)
                    RelayTransport.Connect(client, invitation.Relay, invitation.Route, false);
                else
                {
                    Task connection = invitation.PublicAddress == null
                        ? client.ConnectAsync(invitation.Endpoint.Address, invitation.Endpoint.Port)
                        : client.ConnectAsync(invitation.PublicAddress.Host, invitation.PublicAddress.Port);
                    if (!connection.Wait(Protocol.NetworkTimeout))
                    {
                        connection.ContinueWith(task => { var exception = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                        throw new TimeoutException("Компьютер не отвечает. Проверьте сеть и адрес в приглашении.");
                    }
                }
                using (var stream = Protocol.OpenViewer(client, invitation))
                using (var reader = new BinaryReader(stream))
                {
                    Protocol.WriteHello(stream, invitation);
                    waiting();
                    stream.ReadTimeout = Timeout.Infinite;
                    var decision = (Decision)reader.ReadByte();
                    if (decision == Decision.Denied) throw new InvalidOperationException("Владелец отклонил подключение.");
                    if (decision == Decision.Expired) throw new TimeoutException("Время подтверждения истекло.");
                    if (decision == Decision.InvalidCode) throw new InvalidOperationException("Приглашение недействительно.");
                    if (decision != Decision.View && decision != Decision.Control)
                        throw new InvalidDataException("Получен неизвестный ответ.");
                    stream.ReadTimeout = Protocol.NetworkTimeout;
                    var link = invitation.PrivatePairToken
                        ? invitation.WithToken(Protocol.ReadExact(reader, Invitation.TokenSize)) : invitation;
                    if (linked != null && link.Paired) linked(link);
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
