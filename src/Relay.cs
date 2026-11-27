using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ScreenShow
{
    internal sealed class NetworkAddress
    {
        internal readonly string Host;
        internal readonly int Port;

        private NetworkAddress(string host, int port)
        {
            Host = host;
            Port = port;
        }

        internal static NetworkAddress Parse(string value)
        {
            string[] parts = value.Trim().Split(':');
            int port;
            if (parts.Length != 2 || !int.TryParse(parts[1], out port) || port < 1 || port > 65535 ||
                parts[0].Length < 1 || parts[0].Length > 96 ||
                !System.Text.RegularExpressions.Regex.IsMatch(parts[0], @"\A[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?\z"))
                throw new FormatException("Укажите адрес и порт в формате имя:порт.");
            return new NetworkAddress(parts[0].ToLowerInvariant(), port);
        }

        public override string ToString() { return Host + ":" + Port; }
    }

    internal static class RelayTransport
    {
        internal const int KeySize = 32;
        internal const int DefaultPort = 47211;

        internal static byte[] Route(byte[] key)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(key);
        }

        internal static void Connect(TcpClient client, NetworkAddress address, byte[] key, bool host)
        {
            Task connection = client.ConnectAsync(address.Host, address.Port);
            if (!connection.Wait(Protocol.NetworkTimeout))
            {
                connection.ContinueWith(task => { var exception = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                throw new TimeoutException("Сервер-посредник не отвечает.");
            }
            client.NoDelay = true;
            client.ReceiveTimeout = Protocol.NetworkTimeout;
            client.SendTimeout = Protocol.NetworkTimeout;
            var stream = client.GetStream();
            byte[] packet = new byte[5 + KeySize];
            Encoding.ASCII.GetBytes(host ? "SSR1H" : "SSR1V").CopyTo(packet, 0);
            key.CopyTo(packet, 5);
            stream.Write(packet, 0, packet.Length);
            int response = stream.ReadByte();
            if (response == 2)
                throw new InvalidOperationException("Компьютер не в сети или занят. Откройте доступ на ПК.");
            if (response == 3)
                throw new InvalidOperationException("Этот компьютер уже подключён к серверу-посреднику.");
            if (response != 1) throw new IOException("Сервер-посредник отклонил подключение.");
        }

        internal static void WaitForViewer(TcpClient client)
        {
            client.ReceiveTimeout = 0;
            if (client.GetStream().ReadByte() != 1)
                throw new IOException("Соединение с сервером-посредником прервалось.");
        }
    }
}
