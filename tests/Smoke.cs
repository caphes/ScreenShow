using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;

namespace ScreenShow
{
    internal static class Smoke
    {
        private static void Check(bool result, string message)
        {
            if (!result) throw new Exception(message);
        }

        [STAThread]
        private static int Main()
        {
            try
            {
                CheckInput();
                TestHost(Decision.Denied);
                TestHost(Decision.View);
                TestHost(Decision.Control);
                Console.WriteLine("PASS: TLS identity, invitation, approval, live frame, input permission and disconnect");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        private static void CheckInput()
        {
            var command = new InputCommand(InputKind.Wheel, 12345, 54321, -120);
            using (var packet = new MemoryStream())
            using (var reader = new BinaryReader(packet))
            {
                command.Write(packet, new byte[9]);
                packet.Position = 0;
                var decoded = InputCommand.Read(reader);
                Check(decoded.Kind == command.Kind && decoded.X == command.X && decoded.Y == command.Y &&
                    decoded.Value == command.Value, "Input did not round-trip.");
            }
        }

        private static void TestHost(Decision decision)
        {
            var host = new HostSession(IPAddress.Loopback, 0);
            var ready = new TaskCompletionSource<Invitation>();
            int requests = 0;
            bool started = false;
            Task worker = Task.Run(() => host.Run(invite => ready.SetResult(invite), (address, cancellation) =>
            {
                requests++;
                return decision;
            }, access => started = true));
            try
            {
                int first = Task.WaitAny(new Task[] { ready.Task, worker }, 10000);
                if (first == 1) worker.GetAwaiter().GetResult();
                Check(first == 0, "Host did not produce an invitation.");
                var invite = Invitation.Parse(ready.Task.Result.ToString());
                Check(invite.Endpoint.Equals(ready.Task.Result.Endpoint) &&
                    Protocol.Same(invite.Token, ready.Task.Result.Token) &&
                    Protocol.Same(invite.Fingerprint, ready.Task.Result.Fingerprint), "Invitation did not round-trip.");

                if (decision == Decision.Denied) CheckInvalidInvites(invite);
                if (decision == Decision.Control)
                {
                    TestViewer(invite);
                }
                else
                {
                    using (var client = new TcpClient())
                    {
                        client.Connect(invite.Endpoint);
                        using (var stream = Protocol.OpenViewer(client, invite))
                        using (var reader = new BinaryReader(stream))
                        {
                            Protocol.WriteHello(stream, invite);
                            Check(stream.ReadByte() == (byte)decision, "Incorrect approval result.");
                            if (decision == Decision.View)
                            {
                                using (var frame = Protocol.ReadFrame(reader)) CheckFrame(frame);
                                new InputCommand(InputKind.ReleaseAll).Write(stream, new byte[9]);
                                Check(worker.Wait(10000), "View-only host did not reject guest input.");
                            }
                        }
                    }
                }
                Check(worker.Wait(10000), "Host did not stop.");
                Check(requests == 1, "Unexpected number of approval prompts.");
                Check(started == (decision != Decision.Denied), "Incorrect streaming state after approval.");
            }
            catch
            {
                if (worker.IsFaulted) worker.GetAwaiter().GetResult();
                throw;
            }
            finally { host.Stop(); }
        }

        private static void TestViewer(Invitation invite)
        {
            bool seen = false;
            var viewer = new ViewerSession(invite);
            try
            {
                viewer.Run(() => { }, access =>
                {
                    Check(access == Decision.Control, "Wrong control permission.");
                    viewer.Queue(new InputCommand(InputKind.ReleaseAll));
                }, frame =>
                {
                    using (frame) CheckFrame(frame);
                    seen = true;
                    viewer.Stop();
                });
            }
            catch (IOException) { if (!seen) throw; }
            catch (ObjectDisposedException) { if (!seen) throw; }
            Check(seen, "Viewer received no frame.");
        }

        private static void CheckInvalidInvites(Invitation invite)
        {
            var wrongPin = (byte[])invite.Fingerprint.Clone();
            wrongPin[0] ^= 1;
            var wrong = new Invitation(invite.Endpoint, invite.Token, wrongPin);
            try
            {
                new ViewerSession(wrong).Run(() => { }, access => { }, frame => frame.Dispose());
                throw new Exception("Incorrect TLS identity was accepted.");
            }
            catch (AuthenticationException) { }

            var wrongToken = (byte[])invite.Token.Clone();
            wrongToken[0] ^= 1;
            wrong = new Invitation(invite.Endpoint, wrongToken, invite.Fingerprint);
            using (var client = new TcpClient())
            {
                client.Connect(wrong.Endpoint);
                using (var stream = Protocol.OpenViewer(client, wrong))
                {
                    Protocol.WriteHello(stream, wrong);
                    Check(stream.ReadByte() == (byte)Decision.InvalidCode, "Incorrect token was accepted.");
                }
            }
        }

        private static void CheckFrame(Bitmap frame)
        {
            Check(frame.Width > 0 && frame.Width <= Protocol.FrameWidth &&
                frame.Height > 0 && frame.Height <= Protocol.FrameHeight, "Live frame resolution is out of range.");
        }
    }
}
