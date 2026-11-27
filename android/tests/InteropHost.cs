using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace ScreenShow
{
    internal static class InteropHost
    {
        private const int Timeout = 15000;

        private static int Main(string[] arguments)
        {
            try
            {
                var run = new Action<Invitation, string>((invite, scenario) =>
                    RunJava(arguments[0], arguments[1], invite, scenario));
                LiveHost(run);
                ProtocolFixture(run, false);
                ProtocolFixture(run, true);
                RelayFixture(run);
                CheckPublic(run);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        private static void CheckPublic(Action<Invitation, string> run)
        {
            var reserve = new TcpListener(IPAddress.Loopback, 0);
            reserve.Start();
            int port = ((IPEndPoint)reserve.LocalEndpoint).Port;
            reserve.Stop();
            var address = NetworkAddress.Parse("127.0.0.1:" + port);
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "public-pairs");
            var store = new PairStore(folder);
            using (var ready = new BlockingCollection<Invitation>())
            {
                var host = new HostSession(IPAddress.Loopback, port, store, address);
                int approvals = 0;
                Task worker = Task.Run(() => host.Run(ready.Add, (endpoint, cancellation) =>
                {
                    if (endpoint == null) throw new InvalidOperationException("Unexpected relay.");
                    approvals++;
                    return Decision.View;
                }, decision => { }));
                try
                {
                    Invitation code;
                    if (!ready.TryTake(out code, Timeout)) throw new TimeoutException("Direct listener did not start.");
                    code = Invitation.Parse(code.ToString());
                    if (code.Endpoint != null || code.PublicAddress == null || code.Relay != null)
                        throw new InvalidOperationException("Direct Internet invitation did not round trip.");
                    var linked = ViewNetwork(code);
                    if (!ready.TryTake(out code, Timeout)) throw new TimeoutException("Missing Android invitation.");
                    run(code, "public");
                    if (!ready.TryTake(out code, Timeout)) throw new TimeoutException("Direct listener failed after Android.");
                    ViewNetwork(linked);
                    if (approvals != 2) throw new InvalidOperationException("The direct reconnect asked for approval.");
                    Console.WriteLine("PASS: direct Internet protocol on Windows and Android, including trusted reconnect");
                }
                catch
                {
                    if (worker.IsFaulted) worker.GetAwaiter().GetResult();
                    throw;
                }
                finally { StopHostAndClean(host, worker, folder); }
            }
        }

        private static void RelayFixture(Action<Invitation, string> run)
        {
            string script = Path.GetFullPath(Path.Combine("relay", "server.js"));
            var start = new ProcessStartInfo("node.exe", "\"" + script + "\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            start.EnvironmentVariables["SCREENSHOW_RELAY_HOST"] = "127.0.0.1";
            start.EnvironmentVariables["SCREENSHOW_RELAY_PORT"] = "0";
            using (var relay = Process.Start(start))
            {
                try
                {
                    Task<string> line = Task.Run(() => relay.StandardOutput.ReadLine());
                    const string prefix = "ScreenShow relay listening on ";
                    if (!line.Wait(Timeout) || line.Result == null || !line.Result.StartsWith(prefix, StringComparison.Ordinal))
                        throw new InvalidOperationException("The relay did not start.");
                    CheckRelay(NetworkAddress.Parse(line.Result.Substring(prefix.Length)), run);
                }
                finally
                {
                    if (!relay.HasExited) relay.Kill();
                    relay.WaitForExit(Timeout);
                }
            }
        }

        private static void CheckRelay(NetworkAddress address, Action<Invitation, string> run)
        {
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "relay-pairs");
            var store = new PairStore(folder);
            using (var ready = new BlockingCollection<Invitation>())
            {
                var host = new HostSession(address, store);
                int approvals = 0;
                Task worker = Task.Run(() => host.Run(ready.Add, (endpoint, cancellation) =>
                {
                    if (endpoint != null) throw new InvalidOperationException("Expected a relayed connection.");
                    approvals++;
                    return Decision.View;
                }, decision => { }));
                try
                {
                    Invitation code;
                    if (!ready.TryTake(out code, Timeout)) throw new TimeoutException("Windows did not register with the relay.");
                    using (var unregistered = new TcpClient())
                    {
                        bool refused = false;
                        try { RelayTransport.Connect(unregistered, address, new byte[RelayTransport.KeySize], false); }
                        catch (InvalidOperationException) { refused = true; }
                        if (!refused) throw new InvalidOperationException("The relay accepted an unregistered route.");
                    }
                    var linked = ViewNetwork(Invitation.Parse(code.ToString()));
                    if (!ready.TryTake(out code, Timeout)) throw new TimeoutException("Windows did not return to the relay.");
                    run(code, "relay");
                    if (!ready.TryTake(out code, Timeout)) throw new TimeoutException("Windows did not return after Android.");
                    ViewNetwork(linked);
                    if (approvals != 2) throw new InvalidOperationException("The relayed reconnect asked for approval.");
                    Console.WriteLine("PASS: Windows and Android through relay, including discovery and trusted reconnect");
                }
                catch
                {
                    if (worker.IsFaulted) worker.GetAwaiter().GetResult();
                    throw;
                }
                finally { StopHostAndClean(host, worker, folder); }
            }
        }

        private static void StopHostAndClean(HostSession host, Task worker, string folder)
        {
            host.Stop();
            try
            {
                if (!worker.Wait(Timeout)) throw new TimeoutException("The host did not stop.");
            }
            catch (AggregateException error)
            {
                if (!(error.GetBaseException() is IOException) && !(error.GetBaseException() is ObjectDisposedException)
                    && !(error.GetBaseException() is SocketException) && !(error.GetBaseException() is OperationCanceledException))
                    throw;
            }
            finally
            {
                if (Directory.Exists(folder))
                {
                    foreach (string file in Directory.GetFiles(folder)) File.Delete(file);
                    Directory.Delete(folder);
                }
            }
        }

        private static Invitation ViewNetwork(Invitation code)
        {
            var viewer = new ViewerSession(code);
            Invitation linked = null;
            bool seen = false;
            try
            {
                viewer.Run(() => { }, decision =>
                {
                    if (decision != Decision.View) throw new InvalidOperationException("Wrong permission.");
                }, image =>
                {
                    using (image) seen = image.Width > 0;
                    viewer.Stop();
                }, result => linked = result);
            }
            catch (IOException) { if (!seen) throw; }
            catch (ObjectDisposedException) { if (!seen) throw; }
            if (!seen || linked == null || linked.NetworkMode != code.NetworkMode)
                throw new InvalidOperationException("No frame or saved pairing in the selected mode.");
            return linked;
        }

        private static void LiveHost(Action<Invitation, string> run)
        {
            var host = new HostSession(IPAddress.Loopback, 0);
            var ready = new TaskCompletionSource<Invitation>();
            int requests = 0;
            var worker = Task.Run(() => host.Run(ready.SetResult, (address, cancellation) =>
            {
                requests++;
                return Decision.View;
            }, decision => { }));
            try
            {
                int first = Task.WaitAny(new Task[] { ready.Task, worker }, Timeout);
                if (first == 1) worker.GetAwaiter().GetResult();
                if (first != 0) throw new TimeoutException("No invitation from Windows.");
                var invite = ready.Task.Result;
                var wrong = (byte[])invite.Fingerprint.Clone();
                wrong[0] ^= 1;
                run(new Invitation(invite.Endpoint, invite.Token, wrong), "bad-pin");
                run(invite, "view");
                if (!worker.Wait(Timeout) || requests != 1)
                    throw new InvalidOperationException("Incorrect number of window approval requests.");
            }
            finally { host.Stop(); }
        }

        private static void ProtocolFixture(Action<Invitation, string> run, bool legacy)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                using (var certificate = Protocol.MakeCertificate())
                {
                    var invite = Invitation.Create((IPEndPoint)listener.LocalEndpoint, certificate, !legacy, !legacy);
                    Task worker = Task.Run(delegate
                    {
                        if (legacy)
                            Serve(listener, invite, certificate, true, true);
                        Serve(listener, invite, certificate, !legacy, false);
                    });
                    run(invite, legacy ? "legacy" : "text");
                    if (!worker.Wait(Timeout)) throw new TimeoutException("Windows fixture did not finish.");
                }
            }
            finally { listener.Stop(); }
        }

        private static void Serve(TcpListener listener, Invitation invite,
            System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, bool mobile, bool reject)
        {
            using (var client = listener.AcceptTcpClient())
            using (var stream = Protocol.OpenHost(client, certificate))
            using (var reader = new BinaryReader(stream))
            using (var writer = new BinaryWriter(stream))
            {
                bool greeting;
                if (!Protocol.CheckHello(reader, invite, out greeting) || greeting != mobile)
                    throw new InvalidDataException("Wrong Android greeting.");
                writer.Write((byte)(reject ? Decision.InvalidCode : Decision.Control));
                if (reject) return;
                if (mobile)
                {
                    writer.Write(Invitation.NewToken());
                    var input = InputCommand.Read(reader);
                    if (input.Kind != InputKind.Text || input.Value != 0x416)
                        throw new InvalidDataException("The Cyrillic text input packet was incorrect.");
                }
                using (var frame = new Bitmap(32, 16))
                using (var encoded = new MemoryStream())
                {
                    frame.Save(encoded, ImageFormat.Jpeg);
                    Protocol.WriteFrame(writer, encoded.ToArray());
                }
            }
        }

        private static void RunJava(string java, string classes, Invitation invite, string scenario)
        {
            var start = new ProcessStartInfo(java, "-cp \"" + classes + "\" app.screenshow.Interop " + scenario)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };
            using (var process = Process.Start(start))
            {
                process.StandardInput.WriteLine(invite.ToString());
                process.StandardInput.Close();
                if (!process.WaitForExit(Timeout + 5000))
                {
                    process.Kill();
                    throw new TimeoutException("The mobile client did not finish.");
                }
                if (process.ExitCode != 0) throw new InvalidOperationException("Mobile client failed: " + scenario);
            }
        }
    }
}
