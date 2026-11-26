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
        internal readonly Decision Access;

        internal PairedDevice(Invitation invitation, string label, bool incoming, Decision access = Decision.View)
        {
            Invitation = invitation;
            Label = label;
            Incoming = incoming;
            Access = access;
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

        internal void SaveIncoming(Invitation invite, IPAddress address, bool mobile, Decision access)
        {
            SaveDevice(new PairedDevice(invite, (mobile ? "Android · " : "Windows · ") + address, true, access));
        }

        internal void SaveOutgoing(Invitation invite)
        {
            SaveDevice(new PairedDevice(invite, invite.Endpoint.ToString(), false));
        }

        internal PairedDevice FindIncoming(byte[] token)
        {
            return ReadDevice(DevicePath(token, true), true);
        }

        internal PairedDevice ChangeAccess(PairedDevice device, Decision access)
        {
            var updated = new PairedDevice(device.Invitation, device.Label, true, access);
            SaveDevice(updated, true);
            return updated;
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

        private void SaveDevice(PairedDevice device, bool replaceOnly = false)
        {
            string path = DevicePath(device.Incoming ? device.Invitation.Token : device.Invitation.Fingerprint,
                device.Incoming);
            string record = device.Invitation + "\n" + device.Label + "\n" +
                (device.Access == Decision.Control ? "control" : "view");
            Save(path, Encoding.UTF8.GetBytes(record), true, replaceOnly);
        }

        private PairedDevice ReadDevice(string path, bool incoming)
        {
            try
            {
                string record = Encoding.UTF8.GetString(Load(path));
                string[] fields = record.Split('\n');
                if (fields.Length < 2) return null;
                var invite = Invitation.Parse(fields[0]);
                Decision access = fields.Length > 2 && fields[2] == "control" ? Decision.Control : Decision.View;
                return new PairedDevice(invite, fields[1], incoming, access);
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

        private void Save(string path, byte[] secret, bool overwrite, bool replaceOnly = false)
        {
            Directory.CreateDirectory(folder);
            string temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temporary, ProtectedData.Protect(secret, null, DataProtectionScope.CurrentUser));
                if (replaceOnly)
                {
                    File.Replace(temporary, path, null);
                    return;
                }
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
