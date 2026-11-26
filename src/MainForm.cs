using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ScreenShow
{
    internal sealed class MainForm : Form
    {
        private sealed class Adapter
        {
            internal string Name;
            internal IPAddress Address;
            internal int Priority;
            public override string ToString() { return Address + "   ·   " + Name; }
        }

        private const int StopHotkey = 81;
        private readonly ComboBox adapter;
        private readonly NumericUpDown port;
        private readonly Button create;
        private readonly Button copy;
        private readonly Button stop;
        private readonly TextBox ownCode;
        private readonly TextBox otherCode;
        private readonly Button connect;
        private readonly Button paste;
        private readonly Button devices;
        private readonly PairStore pairs = new PairStore();
        private readonly Label status;
        private readonly Panel approval;
        private readonly Label prompt;
        private HostSession host;
        private ViewerSession guest;
        private ViewerForm viewer;
        private Form deviceList;
        private TaskCompletionSource<Decision> pendingApproval;
        private bool hotkey;
        private bool cancelled;
        private bool exitAfterSession;

        internal MainForm()
        {
            SuspendLayout();
            Text = "ScreenShow";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(800, 515);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Theme.Background;
            ForeColor = Theme.Ink;
            Font = new Font("Segoe UI", 10);

            var badge = Theme.Put(this, Theme.Label("▣", 23, Theme.Accent, true), 24, 23, 38, 47);
            badge.TextAlign = ContentAlignment.MiddleLeft;
            Theme.Put(this, Theme.Label("ScreenShow", 26, bold: true), 67, 22, 315, 49);
            Theme.Put(this, Theme.Label("Демонстрация экрана и удалённое управление", 10.5f, Theme.Muted),
                69, 76, 550, 25);
            var network = Theme.Put(this, Theme.Label("Локальная сеть / VPN", 10, Theme.Accent, true), 603, 37, 172, 25);
            network.TextAlign = ContentAlignment.MiddleRight;

            var share = Theme.Put(this, new Panel { BackColor = Theme.Panel }, 24, 123, 366, 270);
            Theme.Put(share, Theme.Label("Показать мой экран", 15, bold: true), 17, 13, 332, 32);
            Theme.Put(share, Theme.Label("Откройте доступ для своих устройств.", 9.5f, Theme.Muted), 18, 51, 334, 24);
            Theme.Put(share, Theme.Label("Сетевой адрес", 9, Theme.Muted), 18, 84, 230, 21);
            Theme.Put(share, Theme.Label("Порт", 9, Theme.Muted), 268, 84, 80, 21);
            adapter = Theme.Put(share, new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Theme.Field, ForeColor = Theme.Ink,
                FlatStyle = FlatStyle.Flat, DropDownWidth = 300, AccessibleName = "Сетевой адрес"
            }, 18, 109, 239, 27);
            port = Theme.Put(share, new NumericUpDown
            {
                Minimum = 1024, Maximum = 65535, Value = 47210,
                BackColor = Theme.Field, ForeColor = Theme.Ink, AccessibleName = "Порт"
            }, 268, 109, 80, 27);
            Theme.Put(share, Theme.Label("Ваше приглашение", 9, Theme.Muted), 18, 150, 300, 20);
            ownCode = Theme.Put(share, Theme.TextBox(), 18, 173, 222, 27);
            ownCode.ReadOnly = true;
            ownCode.AccessibleName = "Ваше приглашение";
            copy = Theme.Put(share, Theme.Button("Копировать"), 249, 171, 99, 31);
            create = Theme.Put(share, Theme.Button("Открыть доступ", true), 18, 216, 211, 37);
            stop = Theme.Put(share, Theme.Button("Остановить"), 238, 216, 110, 37);
            stop.ForeColor = Theme.Red;

            var join = Theme.Put(this, new Panel { BackColor = Theme.Panel }, 410, 123, 366, 270);
            Theme.Put(join, Theme.Label("Подключиться", 15, bold: true), 17, 13, 332, 32);
            Theme.Put(join, Theme.Label("Вставьте приглашение, полученное от владельца.", 9.5f, Theme.Muted),
                18, 51, 330, 40);
            Theme.Put(join, Theme.Label("Код приглашения", 9, Theme.Muted), 18, 101, 300, 21);
            otherCode = Theme.Put(join, Theme.TextBox(), 18, 125, 236, 27);
            otherCode.MaxLength = 256;
            otherCode.AccessibleName = "Код приглашения";
            paste = Theme.Put(join, Theme.Button("Вставить"), 262, 123, 86, 31);
            Theme.Put(join, Theme.Label("Код нужен при первой привязке. Каждый сеанс подтверждается на ПК.",
                9.5f, Theme.Muted), 18, 164, 330, 43);
            connect = Theme.Put(join, Theme.Button("Подключиться", true), 18, 216, 168, 37);
            devices = Theme.Put(join, Theme.Button("Устройства…"), 196, 216, 152, 37);

            status = Theme.Put(this, Theme.Label("Готово к подключению", 10, Theme.Muted), 25, 411, 750, 31);
            status.TextAlign = ContentAlignment.MiddleLeft;
            approval = Theme.Put(this, new Panel { BackColor = Theme.Panel, Visible = false }, 24, 451, 752, 48);
            prompt = Theme.Put(approval, Theme.Label("", 9.5f, Theme.Accent, true), 12, 3, 264, 42);
            prompt.TextAlign = ContentAlignment.MiddleLeft;
            Theme.Put(approval, Theme.Button("Только просмотр"), 283, 8, 143, 32).Click +=
                (sender, args) => Answer(Decision.View);
            Theme.Put(approval, Theme.Button("Разрешить управление", true), 435, 8, 182, 32).Click +=
                (sender, args) => Answer(Decision.Control);
            Theme.Put(approval, Theme.Button("Отклонить"), 626, 8, 114, 32).Click +=
                (sender, args) => Answer(Decision.Denied);

            foreach (var item in NetworkAddresses()) adapter.Items.Add(item);
            if (adapter.Items.Count != 0) adapter.SelectedIndex = 0;
            RestoreHostAddress();
            create.Click += (sender, args) => StartHost();
            copy.Click += (sender, args) => UseClipboard(() => Clipboard.SetText(ownCode.Text), "Приглашение скопировано.");
            stop.Click += (sender, args) => StopSession();
            connect.Click += (sender, args) => Join();
            devices.Click += (sender, args) => ShowDevices();
            paste.Click += (sender, args) => UseClipboard(() => { if (Clipboard.ContainsText()) otherCode.Text = Clipboard.GetText().Trim(); });
            otherCode.TextChanged += (sender, args) => UpdateControls();
            otherCode.KeyDown += (sender, args) =>
            {
                if (args.KeyCode == Keys.Enter && connect.Enabled)
                {
                    args.SuppressKeyPress = true;
                    Join();
                }
            };
            UpdateControls();
            ResumeLayout(false);
        }

        private static Adapter[] NetworkAddresses()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses
                    .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(item => new Adapter
                    {
                        Address = item.Address, Name = IPAddress.IsLoopback(item.Address) ? "Этот компьютер" : nic.Name,
                        Priority = IPAddress.IsLoopback(item.Address) ? 3 :
                            nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 :
                            nic.GetIPProperties().GatewayAddresses.Any(gateway =>
                                gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                                !gateway.Address.Equals(IPAddress.Any)) ? 1 : 2
                    }))
                .OrderBy(item => item.Priority).ThenBy(item => item.Name).ToArray();
        }

        private void UseClipboard(Action action, string message = null)
        {
            try
            {
                action();
                if (message != null) SetStatus(message, Theme.Success);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                SetStatus("Буфер обмена занят. Повторите действие.", Theme.Red);
            }
        }

        private void SetStatus(string message, Color color)
        {
            status.Text = message;
            status.ForeColor = color;
        }

        private void UpdateControls()
        {
            bool idle = host == null && guest == null;
            adapter.Enabled = port.Enabled = idle;
            devices.Enabled = idle || (host != null && ownCode.TextLength != 0 && !cancelled);
            create.Enabled = idle && adapter.SelectedItem != null;
            copy.Enabled = ownCode.TextLength != 0 && !cancelled;
            stop.Enabled = !idle && !cancelled;
            otherCode.Enabled = paste.Enabled = idle;
            connect.Enabled = idle && otherCode.Text.Trim().Length != 0;
        }

        private void Post(Action action)
        {
            try { BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        private void StartHost()
        {
            var address = (Adapter)adapter.SelectedItem;
            var endpoint = new IPEndPoint(address.Address, (int)port.Value);
            var session = new HostSession(endpoint.Address, endpoint.Port, pairs);
            host = session;
            cancelled = false;
            SetStatus("Создаём защищённое приглашение…", Theme.Accent);
            UpdateControls();
            Run(
                () =>
                {
                    pairs.HostAddress = endpoint.ToString();
                    session.Run(invitation => Post(() =>
                    {
                        if (cancelled) return;
                        Answer(Decision.Denied);
                        TopMost = false;
                        if (hotkey) Native.UnregisterHotKey(Handle, StopHotkey);
                        hotkey = false;
                        ownCode.Text = invitation.ToString();
                        SetStatus("Доступ открыт. Связанные устройства могут подключиться.", Theme.Success);
                        UpdateControls();
                    }),
                    RequestApproval,
                    access => Post(() =>
                    {
                        if (cancelled) return;
                        TopMost = true;
                        WindowState = FormWindowState.Normal;
                        hotkey = Native.RegisterHotKey(Handle, StopHotkey, 0x4003, (uint)Keys.F12);
                        string mode = access == Decision.Control ? "Просмотр и управление" : "Только просмотр";
                        SetStatus("Экран транслируется   ·   " + mode +
                            (hotkey ? "   ·   Ctrl+Alt+F12 для остановки" : "   ·   Нажмите «Остановить» для выхода"), Theme.Success);
                    }));
                },
                error =>
                {
                    host = null;
                    TopMost = false;
                    if (hotkey) Native.UnregisterHotKey(Handle, StopHotkey);
                    hotkey = false;
                    approval.Visible = false;
                    ownCode.Clear();
                    Finish(error);
                });
        }

        private Decision RequestApproval(IPEndPoint peer, CancellationToken cancellation)
        {
            var request = new TaskCompletionSource<Decision>();
            Post(() =>
            {
                if (cancelled || cancellation.IsCancellationRequested || request.Task.IsCompleted) return;
                if (deviceList != null) deviceList.Close();
                pendingApproval = request;
                ownCode.Clear();
                prompt.Text = "Запрос на доступ от " + peer.Address;
                approval.Visible = true;
                WindowState = FormWindowState.Normal;
                Activate();
                SetStatus("Подтвердите подключение на этом компьютере.", Theme.Accent);
                UpdateControls();
            });
            request.Task.Wait(cancellation);
            return request.Task.Result;
        }

        private void Answer(Decision decision)
        {
            approval.Visible = false;
            if (pendingApproval != null) pendingApproval.TrySetResult(decision);
            pendingApproval = null;
        }

        private void Join()
        {
            Invitation invitation;
            try { invitation = Invitation.Parse(otherCode.Text); }
            catch (FormatException error)
            {
                SetStatus(error.Message, Theme.Red);
                return;
            }
            Join(invitation);
        }

        private void Join(Invitation invitation)
        {
            var session = new ViewerSession(invitation);
            guest = session;
            cancelled = false;
            SetStatus("Подключаемся к " + invitation.Endpoint + "…", Theme.Accent);
            UpdateControls();
            var window = new ViewerForm(invitation.Endpoint.ToString(), session);
            viewer = window;
            window.FormClosed += (sender, args) => { if (guest == session && !cancelled) StopSession(); };
            Run(() => session.Run(
                () => Post(() =>
                {
                    if (!cancelled) SetStatus("Ожидаем подтверждение на втором компьютере…", Theme.Accent);
                }),
                access =>
                {
                    if (invitation.Paired) pairs.SaveOutgoing(invitation);
                    Post(() =>
                    {
                        if (cancelled) return;
                        otherCode.Clear();
                        SetStatus("Сеанс открыт в отдельном окне.", Theme.Success);
                        window.Start(access == Decision.Control);
                    });
                },
                window.Submit),
                error =>
                {
                    guest = null;
                    viewer = null;
                    window.Dispose();
                    Finish(error);
                });
        }

        private void RestoreHostAddress()
        {
            try
            {
                string[] endpoint = pairs.HostAddress.Split(':');
                int value;
                if (endpoint.Length != 2 || !int.TryParse(endpoint[1], out value) ||
                    value < port.Minimum || value > port.Maximum) return;
                port.Value = value;
                foreach (Adapter item in adapter.Items)
                    if (item.Address.ToString() == endpoint[0]) { adapter.SelectedItem = item; break; }
            }
            catch (IOException) { }
            catch (System.Security.Cryptography.CryptographicException) { }
        }

        private void ShowDevices()
        {
            try
            {
                using (var window = new Form())
                {
                    window.Text = "Связанные устройства";
                    window.StartPosition = FormStartPosition.CenterParent;
                    window.FormBorderStyle = FormBorderStyle.FixedDialog;
                    window.ClientSize = new Size(490, 309);
                    window.MinimizeBox = window.MaximizeBox = false;
                    window.BackColor = Theme.Background;
                    Theme.Put(window, Theme.Label("Компьютеры для подключения", bold: true), 14, 14, 220, 24);
                    Theme.Put(window, Theme.Label("Устройства с привязкой к этому ПК", bold: true), 252, 14, 225, 24);
                    var outgoing = Theme.Put(window, new ListBox { BackColor = Theme.Field, ForeColor = Theme.Ink }, 14, 43, 224, 205);
                    var incoming = Theme.Put(window, new ListBox { BackColor = Theme.Field, ForeColor = Theme.Ink }, 252, 43, 224, 205);
                    outgoing.Items.AddRange(pairs.Devices(false));
                    incoming.Items.AddRange(pairs.Devices(true));
                    var open = Theme.Put(window, Theme.Button("Подключиться", true), 14, 259, 119, 36);
                    var removeOut = Theme.Put(window, Theme.Button("Забыть"), 142, 259, 96, 36);
                    var removeIn = Theme.Put(window, Theme.Button("Отвязать"), 252, 259, 113, 36);
                    open.Enabled = removeOut.Enabled = removeIn.Enabled = false;
                    outgoing.SelectedIndexChanged += (sender, args) =>
                    {
                        removeOut.Enabled = outgoing.SelectedIndex >= 0;
                        open.Enabled = removeOut.Enabled && host == null;
                    };
                    incoming.SelectedIndexChanged += (sender, args) => removeIn.Enabled = incoming.SelectedIndex >= 0;
                    PairedDevice selected = null;
                    open.Click += (sender, args) => { selected = (PairedDevice)outgoing.SelectedItem; window.Close(); };
                    Action<ListBox> remove = list =>
                    {
                        var device = (PairedDevice)list.SelectedItem;
                        if (device == null) return;
                        try { pairs.Remove(device); list.Items.Remove(device); }
                        catch (IOException) { MessageBox.Show(window, "Не удалось удалить привязку.", "ScreenShow"); }
                    };
                    removeOut.Click += (sender, args) => remove(outgoing);
                    removeIn.Click += (sender, args) => remove(incoming);
                    deviceList = window;
                    try { window.ShowDialog(this); }
                    finally { deviceList = null; }
                    if (selected != null) Join(selected.Invitation);
                }
            }
            catch (IOException error) { SetStatus(error.Message, Theme.Red); }
        }

        private void Run(Action action, Action<Exception> finished)
        {
            Task.Run(delegate
            {
                Exception error = null;
                try { action(); }
                catch (Exception failure) { error = failure; }
                Post(() => finished(error));
            });
        }

        private void StopSession()
        {
            cancelled = true;
            Answer(Decision.Denied);
            if (host != null) host.Stop();
            if (guest != null) guest.Stop();
            if (viewer != null && viewer.Visible) viewer.Hide();
            SetStatus("Завершаем сеанс…", Theme.Muted);
            UpdateControls();
        }

        private void Finish(Exception error)
        {
            pendingApproval = null;
            if (cancelled || error == null || error is EndOfStreamException)
                SetStatus("Сеанс завершён.", Theme.Muted);
            else
                SetStatus(Describe(error.GetBaseException()), Theme.Red);
            cancelled = false;
            UpdateControls();
            if (exitAfterSession) Close();
        }

        private static string Describe(Exception error)
        {
            var socket = error as SocketException;
            if (socket != null && socket.SocketErrorCode == SocketError.AddressAlreadyInUse)
                return "Порт занят. Укажите другой порт и создайте приглашение заново.";
            if (socket != null && socket.SocketErrorCode == SocketError.ConnectionRefused)
                return "Компьютер отклонил соединение. Проверьте приглашение и брандмауэр.";
            if (socket != null) return "Ошибка сети. Проверьте адрес, сетевое подключение и брандмауэр.";
            if (error is AuthenticationException)
                return "Не удалось подтвердить подлинность компьютера. Запросите новое приглашение.";
            if (error is IOException) return "Передача прервалась. Создайте новое приглашение.";
            return error.Message;
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0312 && message.WParam.ToInt32() == StopHotkey && host != null)
                StopSession();
            base.WndProc(ref message);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel || (host == null && guest == null)) return;
            StopSession();
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                exitAfterSession = true;
            }
        }
    }
}
