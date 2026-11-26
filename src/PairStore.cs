using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ScreenShow
{
    internal sealed class PairedDevice
    {
        internal readonly Invitation Invitation;
        internal readonly string Label;
        internal readonly bool Incoming;

        internal PairedDevice(Invitation invitation, string label, bool incoming)
        {
            Invitation = invitation;
            Label = label;
            Incoming = incoming;
        }

        public override string ToString() { return Label; }
    }

    internal sealed class PairStore
    {
        private readonly string folder;

        internal PairStore(string folder = null)
        {
            this.folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenShow", "Pairing");
        }

        internal X509Certificate2 OpenIdentity()
        {
            string path = Path.Combine(folder, "identity.dat");
            if (!File.Exists(path))
            {
                byte[] generated = Protocol.MakeCertificateData(3650);
                try { Save(path, generated, false); }
                finally { Array.Clear(generated, 0, generated.Length); }
            }
            byte[] identity = Load(path);
            try { return Protocol.LoadCertificate(identity); }
            finally { Array.Clear(identity, 0, identity.Length); }
        }

        internal void SaveIncoming(Invitation invite, IPAddress address, bool mobile)
        {
            SaveDevice(new PairedDevice(invite, (mobile ? "Android · " : "Windows · ") + address, true));
        }

        internal void SaveOutgoing(Invitation invite)
        {
            SaveDevice(new PairedDevice(invite, invite.Endpoint.ToString(), false));
        }

        internal bool HasIncoming(byte[] token)
        {
            return ReadDevice(DevicePath(token, true), true) != null;
        }

        internal PairedDevice[] Devices(bool incoming)
        {
            if (!Directory.Exists(folder)) return new PairedDevice[0];
            return Directory.GetFiles(folder, incoming ? "in-*.dat" : "out-*.dat")
                .Select(path => ReadDevice(path, incoming)).Where(device => device != null)
                .OrderBy(device => device.Label).ToArray();
        }

        internal void Remove(PairedDevice device)
        {
            File.Delete(DevicePath(device.Incoming ? device.Invitation.Token : device.Invitation.Fingerprint,
                device.Incoming));
        }

        internal string HostAddress
        {
            get
            {
                string path = Path.Combine(folder, "endpoint.dat");
                return File.Exists(path) ? Encoding.UTF8.GetString(Load(path)) : "";
            }
            set { Save(Path.Combine(folder, "endpoint.dat"), Encoding.UTF8.GetBytes(value), true); }
        }

        private void SaveDevice(PairedDevice device)
        {
            string path = DevicePath(device.Incoming ? device.Invitation.Token : device.Invitation.Fingerprint,
                device.Incoming);
            Save(path, Encoding.UTF8.GetBytes(device.Invitation + "\n" + device.Label), true);
        }

        private PairedDevice ReadDevice(string path, bool incoming)
        {
            try
            {
                string record = Encoding.UTF8.GetString(Load(path));
                int split = record.IndexOf('\n');
                if (split < 0) return null;
                var invite = Invitation.Parse(record.Substring(0, split));
                return new PairedDevice(invite, record.Substring(split + 1), incoming);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (CryptographicException) { return null; }
            catch (FormatException) { return null; }
        }

        private string DevicePath(byte[] key, bool incoming)
        {
            using (var hash = SHA256.Create())
                return Path.Combine(folder, (incoming ? "in-" : "out-") +
                    BitConverter.ToString(hash.ComputeHash(key)).Replace("-", "") + ".dat");
        }

        private static byte[] Load(string path)
        {
            return ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        }

        private void Save(string path, byte[] secret, bool overwrite)
        {
            Directory.CreateDirectory(folder);
            string temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temporary, ProtectedData.Protect(secret, null, DataProtectionScope.CurrentUser));
                try { File.Move(temporary, path); }
                catch (IOException)
                {
                    if (!File.Exists(path)) throw;
                    if (overwrite) File.Replace(temporary, path, null);
                }
            }
            finally { File.Delete(temporary); }
        }
    }
}
