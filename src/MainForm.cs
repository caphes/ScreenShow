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
            Icon = Theme.AppIcon;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(836, 543);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Theme.Background;
            ForeColor = Theme.Ink;
            Font = new Font("Segoe UI", 10);

            Theme.Put(this, new Theme.Mark(), 28, 32, 62, 62);
            Theme.Put(this, Theme.Label("ScreenShow", 28, bold: true), 104, 27, 400, 48);
            Theme.Put(this, Theme.Label("Демонстрация экрана и удалённое управление", 11, Theme.Muted),
                106, 79, 500, 25);
            var networkPill = Theme.Put(this, Theme.Card(17, true), 628, 44, 180, 34);
            var network = Theme.Put(networkPill, Theme.Label("Локальная сеть / VPN", 9.5f, Theme.Accent, true), 4, 1, 172, 32);
            network.TextAlign = ContentAlignment.MiddleCenter;

            var share = Theme.Put(this, Theme.Card(), 28, 127, 380, 296);
            Theme.Put(share, Theme.Label("Показать мой экран", 17, bold: true), 20, 16, 338, 34);
            Theme.Put(share, Theme.Label("Откройте доступ для своих устройств.", 10, Theme.Muted), 20, 53, 340, 27);
            Theme.Put(share, Theme.Label("Сетевой адрес", 9.5f, Theme.Muted, true), 21, 91, 234, 21);
            Theme.Put(share, Theme.Label("Порт", 9.5f, Theme.Muted, true), 279, 91, 78, 21);
            adapter = Theme.Put(share, new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Theme.Field, ForeColor = Theme.Ink,
                FlatStyle = FlatStyle.Flat, DropDownWidth = 300, AccessibleName = "Сетевой адрес"
            }, 21, 116, 246, 27);
            Theme.StyleDropDown(adapter);
            var portField = Theme.Put(share, Theme.Card(8, true), 279, 113, 80, 30);
            port = Theme.Put(portField, new NumericUpDown
            {
                Minimum = 1024, Maximum = 65535, Value = 47210,
                BackColor = Theme.Field, ForeColor = Theme.Ink, BorderStyle = BorderStyle.None, AccessibleName = "Порт"
            }, 7, 5, 68, 23);
            Theme.Put(share, Theme.Label("Ваше приглашение", 9.5f, Theme.Muted, true), 21, 155, 300, 20);
            ownCode = Theme.CodeField(share, 21, 180, 224, 36);
            ownCode.ReadOnly = true;
            ownCode.AccessibleName = "Ваше приглашение";
            copy = Theme.Put(share, Theme.Button("Копировать"), 253, 180, 106, 36);
            create = Theme.Put(share, Theme.Button("Открыть доступ", true), 21, 235, 218, 40);
            stop = Theme.Put(share, Theme.Button("Остановить", danger: true), 247, 235, 112, 40);

            var join = Theme.Put(this, Theme.Card(), 428, 127, 380, 296);
            Theme.Put(join, Theme.Label("Подключиться", 17, bold: true), 20, 16, 338, 34);
            Theme.Put(join, Theme.Label("Вставьте приглашение, полученное от владельца.", 10, Theme.Muted),
                20, 53, 340, 29);
            Theme.Put(join, Theme.Label("Код приглашения", 9.5f, Theme.Muted, true), 21, 91, 300, 21);
            otherCode = Theme.CodeField(join, 21, 116, 233, 36);
            otherCode.MaxLength = 256;
            otherCode.AccessibleName = "Код приглашения";
            paste = Theme.Put(join, Theme.Button("Вставить"), 262, 116, 97, 36);
            Theme.Put(join, Theme.Label("Код и подтверждение нужны при первой привязке.",
                10, Theme.Muted), 21, 170, 338, 43);
            connect = Theme.Put(join, Theme.Button("Подключиться", true), 21, 235, 173, 40);
            devices = Theme.Put(join, Theme.Button("Устройства…"), 202, 235, 157, 40);

            var statusPanel = Theme.Put(this, Theme.Card(15, true), 28, 435, 780, 38);
            status = Theme.Put(statusPanel, Theme.Label("Готово к подключению", 10, Theme.Muted), 13, 0, 752, 38);
            status.TextAlign = ContentAlignment.MiddleLeft;
            approval = Theme.Put(this, Theme.Card(15), 28, 481, 780, 49);
            approval.Visible = false;
            prompt = Theme.Put(approval, Theme.Label("", 9.5f, Theme.Accent, true), 12, 3, 264, 42);
            prompt.TextAlign = ContentAlignment.MiddleLeft;
            Theme.Put(approval, Theme.Button("Только просмотр"), 283, 8, 143, 32).Click +=
                (sender, args) => Answer(Decision.View);
            Theme.Put(approval, Theme.Button("Разрешить управление", true), 435, 8, 182, 32).Click +=
                (sender, args) => Answer(Decision.Control);
            Theme.Put(approval, Theme.Button("Отклонить", danger: true), 626, 8, 140, 32).Click +=
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
                        if (deviceList != null) deviceList.Close();
                        ownCode.Clear();
                        UpdateControls();
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
                SetStatus("Выбранный режим сохранится для этого устройства.", Theme.Accent);
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
                    if (!cancelled) SetStatus("Открываем защищённый сеанс…", Theme.Accent);
                }),
                access => Post(() =>
                {
                    if (cancelled) return;
                    otherCode.Clear();
                    SetStatus("Сеанс открыт в отдельном окне.", Theme.Success);
                    window.Start(access == Decision.Control);
                }),
                window.Submit, pairs.SaveOutgoing),
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
                    window.Icon = Theme.AppIcon;
                    window.StartPosition = FormStartPosition.CenterParent;
                    window.FormBorderStyle = FormBorderStyle.FixedDialog;
                    window.ClientSize = new Size(490, 350);
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
                    var access = Theme.Put(window, new ComboBox
                    {
                        DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Разрешение связанного устройства",
                        BackColor = Theme.Field, ForeColor = Theme.Ink
                    }, 252, 259, 224, 30);
                    Theme.StyleDropDown(access);
                    access.Items.AddRange(new object[] { "Только просмотр", "Просмотр и управление" });
                    var removeIn = Theme.Put(window, Theme.Button("Отвязать"), 252, 300, 113, 36);
                    open.Enabled = removeOut.Enabled = removeIn.Enabled = false;
                    access.Enabled = false;
                    outgoing.SelectedIndexChanged += (sender, args) =>
                    {
                        removeOut.Enabled = outgoing.SelectedIndex >= 0;
                        open.Enabled = removeOut.Enabled && host == null;
                    };
                    incoming.SelectedIndexChanged += (sender, args) =>
                    {
                        var device = (PairedDevice)incoming.SelectedItem;
                        access.Enabled = removeIn.Enabled = device != null;
                        access.SelectedIndex = device == null ? -1 : device.Access == Decision.Control ? 1 : 0;
                    };
                    access.SelectionChangeCommitted += (sender, args) =>
                    {
                        var device = (PairedDevice)incoming.SelectedItem;
                        if (device == null) return;
                        try { incoming.Items[incoming.SelectedIndex] = pairs.ChangeAccess(device,
                            access.SelectedIndex == 1 ? Decision.Control : Decision.View); }
                        catch (IOException)
                        {
                            access.SelectedIndex = device.Access == Decision.Control ? 1 : 0;
                            MessageBox.Show(window, "Не удалось изменить режим доступа.", "ScreenShow");
                        }
                    };
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
