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
        internal const int TokenSize = 24;
        internal const int FingerprintSize = 32;
        private const int Size = 4 + 2 + TokenSize + FingerprintSize;
        internal readonly IPEndPoint Endpoint;
        internal readonly byte[] Token;
        internal readonly byte[] Fingerprint;

        internal Invitation(IPEndPoint endpoint, byte[] token, byte[] fingerprint)
        {
            Endpoint = endpoint;
            Token = token;
            Fingerprint = fingerprint;
        }

        internal static Invitation Create(IPEndPoint endpoint, X509Certificate2 certificate)
        {
            var token = new byte[TokenSize];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(token);
            return new Invitation(endpoint, token, Protocol.Fingerprint(certificate));
        }

        public override string ToString()
        {
            var data = new byte[Size];
            Endpoint.Address.GetAddressBytes().CopyTo(data, 0);
            data[4] = (byte)(Endpoint.Port >> 8);
            data[5] = (byte)Endpoint.Port;
            Token.CopyTo(data, 6);
            Fingerprint.CopyTo(data, 6 + TokenSize);
            return Prefix + Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        internal static Invitation Parse(string text)
        {
            text = text.Trim();
            int encodedLength = (Size * 8 + 5) / 6;
            if (text.Length != Prefix.Length + encodedLength || !text.StartsWith(Prefix, StringComparison.Ordinal))
                throw new FormatException("Проверьте код приглашения. Он начинается с SS1-.");

            byte[] data;
            try
            {
                data = Convert.FromBase64String(text.Substring(Prefix.Length).Replace('-', '+').Replace('_', '/') + "=");
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
            return new Invitation(new IPEndPoint(ip, port), token, fingerprint);
        }
    }

    internal enum Decision : byte { Denied, View, Control, InvalidCode, Expired }
    internal enum InputKind : byte { MouseMove = 1, Button, Wheel, Key, ReleaseAll, Text }

    internal static class Protocol
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SSH1");
        private static readonly byte[] MobileMagic = Encoding.ASCII.GetBytes("SSM1");
        internal const int NetworkTimeout = 8000;
        internal const int ApprovalTimeout = 30000;
        internal const int MaximumFrame = 4 * 1024 * 1024;
        internal const int FrameWidth = 1600;
        internal const int FrameHeight = 1000;

        internal static X509Certificate2 MakeCertificate()
        {
            using (var key = new RSACng(2048))
            {
                var request = new CertificateRequest("CN=ScreenShow", key, HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                using (var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                    DateTimeOffset.UtcNow.AddDays(2)))
                {
                    var pfx = temporary.Export(X509ContentType.Pfx);
                    try
                    {
                        return new X509Certificate2(pfx, (string)null, X509KeyStorageFlags.UserKeySet);
                    }
                    finally { Array.Clear(pfx, 0, pfx.Length); }
                }
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
            Magic.CopyTo(hello, 0);
            invitation.Token.CopyTo(hello, Magic.Length);
            stream.Write(hello);
        }

        internal static bool CheckHello(BinaryReader reader, Invitation invitation, out bool mobile)
        {
            var version = ReadExact(reader, Magic.Length);
            mobile = Same(version, MobileMagic);
            return (mobile || Same(version, Magic)) &&
                Same(ReadExact(reader, Invitation.TokenSize), invitation.Token);
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
