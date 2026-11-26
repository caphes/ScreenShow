using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ScreenShow
{
    internal sealed class Invitation
    {
        private const string Prefix = "SS1-";
        private const string PairPrefix = "SS2-";
        private const string PrivatePairPrefix = "SS3-";
        private const string RelayPrefix = "SSR-";
        internal const int TokenSize = 24;
        internal const int FingerprintSize = 32;
        private const int Size = 4 + 2 + TokenSize + FingerprintSize;
        private const int RelayHeader = 2 + RelayTransport.KeySize + TokenSize + FingerprintSize;
        internal readonly IPEndPoint Endpoint;
        internal readonly byte[] Token;
        internal readonly byte[] Fingerprint;
        internal readonly bool Paired;
        internal readonly bool PrivatePairToken;
        internal readonly RelayAddress Relay;
        internal readonly byte[] Route;

        internal string DisplayAddress
        {
            get
            {
                return Relay == null ? Endpoint.ToString() : "Интернет · " + Relay.Host + " · " +
                    BitConverter.ToString(Fingerprint, 0, 3).Replace("-", "");
            }
        }

        internal Invitation(IPEndPoint endpoint, byte[] token, byte[] fingerprint, bool paired = false,
            bool privatePairToken = false)
        {
            Endpoint = endpoint;
            Token = token;
            Fingerprint = fingerprint;
            Paired = paired;
            PrivatePairToken = privatePairToken;
        }

        private Invitation(RelayAddress relay, byte[] route, byte[] token, byte[] fingerprint)
        {
            Relay = relay;
            Route = route;
            Token = token;
            Fingerprint = fingerprint;
            Paired = PrivatePairToken = true;
        }

        internal static Invitation CreateRelay(RelayAddress relay, byte[] route, X509Certificate2 certificate)
        {
            return new Invitation(relay, route, NewToken(), Protocol.Fingerprint(certificate));
        }

        internal static Invitation Create(IPEndPoint endpoint, X509Certificate2 certificate, bool paired = false,
            bool privatePairToken = false)
        {
            return new Invitation(endpoint, NewToken(), Protocol.Fingerprint(certificate), paired, privatePairToken);
        }

        internal static byte[] NewToken()
        {
            var token = new byte[TokenSize];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(token);
            return token;
        }

        internal Invitation WithToken(byte[] token)
        {
            return Relay == null ? new Invitation(Endpoint, token, Fingerprint, Paired, PrivatePairToken) :
                new Invitation(Relay, Route, token, Fingerprint);
        }

        public override string ToString()
        {
            if (Relay != null)
            {
                byte[] host = Encoding.ASCII.GetBytes(Relay.Host);
                var relayed = new byte[RelayHeader + host.Length];
                relayed[0] = (byte)(Relay.Port >> 8);
                relayed[1] = (byte)Relay.Port;
                Route.CopyTo(relayed, 2);
                Token.CopyTo(relayed, 2 + RelayTransport.KeySize);
                Fingerprint.CopyTo(relayed, 2 + RelayTransport.KeySize + TokenSize);
                host.CopyTo(relayed, RelayHeader);
                return RelayPrefix + Encode(relayed);
            }
            var data = new byte[Size];
            Endpoint.Address.GetAddressBytes().CopyTo(data, 0);
            data[4] = (byte)(Endpoint.Port >> 8);
            data[5] = (byte)Endpoint.Port;
            Token.CopyTo(data, 6);
            Fingerprint.CopyTo(data, 6 + TokenSize);
            string prefix = PrivatePairToken ? PrivatePairPrefix : Paired ? PairPrefix : Prefix;
            return prefix + Encode(data);
        }

        private static string Encode(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static byte[] Decode(string text)
        {
            string data = text.Substring(Prefix.Length).Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(data.PadRight((data.Length + 3) / 4 * 4, '='));
        }

        internal static Invitation Parse(string text)
        {
            text = text.Trim();
            if (text.StartsWith(RelayPrefix, StringComparison.Ordinal)) return ParseRelay(text);
            int encodedLength = (Size * 8 + 5) / 6;
            bool privatePairToken = text.StartsWith(PrivatePairPrefix, StringComparison.Ordinal);
            bool paired = privatePairToken || text.StartsWith(PairPrefix, StringComparison.Ordinal);
            if (text.Length != Prefix.Length + encodedLength || (!paired && !text.StartsWith(Prefix, StringComparison.Ordinal)))
                throw new FormatException("Проверьте код приглашения ScreenShow.");

            byte[] data;
            try
            {
                data = Decode(text);
                if (data.Length != Size) throw new FormatException();
            }
            catch (FormatException)
            {
                throw new FormatException("Проверьте код приглашения. В нём есть недопустимые символы.");
            }

            var ip = new IPAddress(new byte[] { data[0], data[1], data[2], data[3] });
            int port = (data[4] << 8) | data[5];
            if (port == 0 || data[0] == 0 || data[0] >= 224)
                throw new FormatException("В приглашении указан недопустимый адрес.");

            var token = new byte[TokenSize];
            var fingerprint = new byte[FingerprintSize];
            Array.Copy(data, 6, token, 0, token.Length);
            Array.Copy(data, 6 + token.Length, fingerprint, 0, fingerprint.Length);
            return new Invitation(new IPEndPoint(ip, port), token, fingerprint, paired, privatePairToken);
        }

        private static Invitation ParseRelay(string text)
        {
            if (text.Length > 256) throw new FormatException("Слишком длинный код приглашения.");
            byte[] data = Decode(text);
            if (data.Length <= RelayHeader || data.Length > RelayHeader + 96)
                throw new FormatException("Неверный код подключения через интернет.");
            int port = (data[0] << 8) | data[1];
            var address = RelayAddress.Parse(Encoding.ASCII.GetString(data, RelayHeader, data.Length - RelayHeader) + ":" + port);
            var route = new byte[RelayTransport.KeySize];
            var token = new byte[TokenSize];
            var fingerprint = new byte[FingerprintSize];
            Array.Copy(data, 2, route, 0, route.Length);
            Array.Copy(data, 2 + route.Length, token, 0, token.Length);
            Array.Copy(data, 2 + route.Length + token.Length, fingerprint, 0, fingerprint.Length);
            return new Invitation(address, route, token, fingerprint);
        }
    }

    internal enum Decision : byte { Denied, View, Control, InvalidCode, Expired }
    internal enum InputKind : byte { MouseMove = 1, Button, Wheel, Key, ReleaseAll, Text }

    internal static class Protocol
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SSH1");
        private static readonly byte[] MobileMagic = Encoding.ASCII.GetBytes("SSM1");
        private static readonly byte[] PrivateMagic = Encoding.ASCII.GetBytes("SSW2");
        private static readonly byte[] PrivateMobileMagic = Encoding.ASCII.GetBytes("SSM2");
        internal const int NetworkTimeout = 8000;
        internal const int MaximumFrame = 4 * 1024 * 1024;
        internal const int FrameWidth = 1600;
        internal const int FrameHeight = 1000;

        internal static X509Certificate2 MakeCertificate()
        {
            var pfx = MakeCertificateData(2);
            try { return LoadCertificate(pfx); }
            finally { Array.Clear(pfx, 0, pfx.Length); }
        }

        internal static X509Certificate2 LoadCertificate(byte[] pfx)
        {
            return new X509Certificate2(pfx, (string)null, X509KeyStorageFlags.UserKeySet);
        }

        internal static byte[] MakeCertificateData(int days)
        {
            using (var key = new RSACng(2048))
            {
                var request = new CertificateRequest("CN=ScreenShow", key, HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                using (var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                    DateTimeOffset.UtcNow.AddDays(days)))
                    return temporary.Export(X509ContentType.Pfx);
            }
        }

        internal static byte[] Fingerprint(X509Certificate certificate)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(certificate.GetRawCertData());
        }

        internal static bool Same(byte[] first, byte[] second)
        {
            if (first.Length != second.Length) return false;
            int difference = 0;
            for (int i = 0; i < first.Length; i++)
                difference |= first[i] ^ second[i];
            return difference == 0;
        }

        internal static SslStream OpenHost(TcpClient socket, X509Certificate2 certificate)
        {
            var stream = Configure(socket, null);
            try
            {
                stream.AuthenticateAsServer(certificate, false, SslProtocols.Tls12, false);
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }

        internal static SslStream OpenViewer(TcpClient socket, Invitation invitation)
        {
            var stream = Configure(socket, delegate(object sender, X509Certificate cert, X509Chain chain,
                SslPolicyErrors errors) { return cert != null && Same(Fingerprint(cert), invitation.Fingerprint); });
            try
            {
                stream.AuthenticateAsClient("ScreenShow", new X509CertificateCollection(), SslProtocols.Tls12, false);
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }

        private static SslStream Configure(TcpClient socket, RemoteCertificateValidationCallback validate)
        {
            socket.NoDelay = true;
            socket.ReceiveTimeout = NetworkTimeout;
            socket.SendTimeout = NetworkTimeout;
            return new SslStream(socket.GetStream(), true, validate);
        }

        internal static void WriteHello(SslStream stream, Invitation invitation)
        {
            var hello = new byte[Magic.Length + Invitation.TokenSize];
            (invitation.PrivatePairToken ? PrivateMagic : Magic).CopyTo(hello, 0);
            invitation.Token.CopyTo(hello, Magic.Length);
            stream.Write(hello);
        }

        internal static bool CheckHello(BinaryReader reader, Invitation invitation, out bool mobile)
        {
            bool privatePairToken;
            var token = ReadHello(reader, out mobile, out privatePairToken);
            return token != null && privatePairToken == invitation.PrivatePairToken && Same(token, invitation.Token);
        }

        internal static byte[] ReadHello(BinaryReader reader, out bool mobile, out bool privatePairToken)
        {
            var version = ReadExact(reader, Magic.Length);
            mobile = Same(version, MobileMagic) || Same(version, PrivateMobileMagic);
            privatePairToken = Same(version, PrivateMagic) || Same(version, PrivateMobileMagic);
            return mobile || privatePairToken || Same(version, Magic) ? ReadExact(reader, Invitation.TokenSize) : null;
        }

        internal static void WriteFrame(BinaryWriter writer, byte[] jpeg)
        {
            if (jpeg.Length == 0 || jpeg.Length > MaximumFrame)
                throw new InvalidDataException("Недопустимый размер кадра.");
            writer.Write(jpeg.Length);
            writer.Write(jpeg);
        }

        internal static byte[] ReadExact(BinaryReader reader, int length)
        {
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return bytes;
        }

        internal static Bitmap ReadFrame(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length <= 0 || length > MaximumFrame)
                throw new InvalidDataException("Недопустимый размер кадра.");
            var bytes = ReadExact(reader, length);
            if (!ValidJpegSize(bytes))
                throw new InvalidDataException("Недопустимое разрешение кадра.");
            using (var memory = new MemoryStream(bytes, false))
            using (var image = Image.FromStream(memory, false, true))
                return new Bitmap(image);
        }

        private static bool ValidJpegSize(byte[] data)
        {
            if (data.Length < 4 || data[0] != 255 || data[1] != 216) return false;
            int offset = 2;
            while (offset + 4 <= data.Length && data[offset++] == 255)
            {
                while (offset < data.Length && data[offset] == 255) offset++;
                if (offset + 3 > data.Length) return false;
                int marker = data[offset++];
                if (marker == 218 || marker == 217) return false;
                int size = (data[offset] << 8) | data[offset + 1];
                if (size < 2 || size > data.Length - offset) return false;
                if (marker == 192 || marker == 194)
                {
                    if (size < 8) return false;
                    int height = (data[offset + 3] << 8) | data[offset + 4];
                    int width = (data[offset + 5] << 8) | data[offset + 6];
                    return width > 0 && width <= FrameWidth && height > 0 && height <= FrameHeight;
                }
                offset += size;
            }
            return false;
        }
    }
}
