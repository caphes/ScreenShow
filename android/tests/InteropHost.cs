using System;
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
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
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
                    var invite = Invitation.Create((IPEndPoint)listener.LocalEndpoint, certificate, !legacy);
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
