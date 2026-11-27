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
            internal bool Public;
            public override string ToString() { return Address == null ? Name : Address + "   ·   " + Name; }
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
        private readonly Theme.StatusDot statusDot;
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
        private string relayServer = "";
        private string publicServer = "";
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip trayMenu;
        private readonly ToolStripMenuItem stopFromTray;
        private readonly CheckBox startup;
        private bool changingStartup;

        internal MainForm(bool launchAtLogin = false)
        {
            SuspendLayout();
            Text = "ScreenShow";
            Icon = Theme.AppIcon;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(836, 576);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Theme.Background;
            ForeColor = Theme.Ink;
            Font = new Font("Segoe UI", 10);

            var hero = Theme.Put(this, Theme.Hero(), 26, 22, 784, 126);
            Theme.Put(hero, new Theme.ConnectionArt(), 506, 1, 254, 124);
            Theme.Put(hero, Theme.Label("ScreenShow", 35, bold: true), 21, 10, 465, 63);
            Theme.Put(hero, Theme.Label("Демонстрация экрана и удалённое управление", 11, Theme.Muted),
                26, 75, 447, 22);
            var network = Theme.Put(hero, Theme.Label("Локальная сеть / VPN", 9, Theme.Accent, true), 26, 101, 440, 18);

            var share = Theme.Put(this, Theme.Card(), 26, 163, 382, 264);
            Theme.Put(share, Theme.Label("Показать мой экран", 17, bold: true), 20, 14, 338, 34);
            Theme.Put(share, Theme.Label("Откройте доступ для своих устройств.", 10, Theme.Muted), 20, 51, 340, 24);
            Theme.Put(share, Theme.Label("Способ подключения", 9.5f, Theme.Muted, true), 21, 82, 234, 21);
            Theme.Put(share, Theme.Label("Порт", 9.5f, Theme.Muted, true), 279, 82, 78, 21);
            adapter = Theme.Put(share, new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Theme.Field, ForeColor = Theme.Ink,
                FlatStyle = FlatStyle.Flat, DropDownWidth = 300, AccessibleName = "Сетевой адрес"
            }, 21, 107, 246, 27);
            Theme.StyleDropDown(adapter);
            var portField = Theme.Put(share, Theme.Card(8, true), 279, 104, 80, 30);
            port = Theme.Put(portField, new NumericUpDown
            {
                Minimum = 1024, Maximum = 65535, Value = 47210,
                BackColor = Theme.Field, ForeColor = Theme.Ink, BorderStyle = BorderStyle.None, AccessibleName = "Порт"
            }, 7, 5, 68, 23);
            Theme.Put(share, Theme.Label("Ваше приглашение", 9.5f, Theme.Muted, true), 21, 139, 300, 20);
            ownCode = Theme.CodeField(share, 21, 163, 224, 36);
            ownCode.ReadOnly = true;
            ownCode.AccessibleName = "Ваше приглашение";
            copy = Theme.Put(share, Theme.Button("Копировать"), 253, 163, 106, 36);
            create = Theme.Put(share, Theme.Button("Открыть доступ", true), 21, 211, 218, 39);
            stop = Theme.Put(share, Theme.Button("Остановить", danger: true), 247, 211, 112, 39);

            var join = Theme.Put(this, Theme.Card(), 428, 163, 382, 264);
            Theme.Put(join, Theme.Label("Подключиться", 17, bold: true), 20, 14, 338, 34);
            Theme.Put(join, Theme.Label("Вставьте приглашение, полученное от владельца.", 10, Theme.Muted),
                20, 51, 340, 24);
            Theme.Put(join, Theme.Label("Код приглашения", 9.5f, Theme.Muted, true), 21, 82, 300, 21);
            otherCode = Theme.CodeField(join, 21, 105, 233, 36);
            otherCode.MaxLength = 256;
            otherCode.AccessibleName = "Код приглашения";
            paste = Theme.Put(join, Theme.Button("Вставить"), 262, 105, 97, 36);
            Theme.Put(join, Theme.Label("Код и подтверждение нужны при первой привязке.",
                10, Theme.Muted), 21, 155, 338, 42);
            connect = Theme.Put(join, Theme.Button("Подключиться", true), 21, 211, 173, 39);
            devices = Theme.Put(join, Theme.Button("Устройства…"), 202, 211, 157, 39);

            var statusPanel = Theme.Put(this, Theme.Card(19, true), 26, 441, 784, 38);
            statusDot = Theme.Put(statusPanel, new Theme.StatusDot(), 13, 11, 16, 16);
            status = Theme.Put(statusPanel, Theme.Label("Готово к подключению", 10, Theme.Muted), 36, 0, 730, 38);
            status.TextAlign = ContentAlignment.MiddleLeft;
            approval = Theme.Put(this, Theme.Card(16), 26, 487, 784, 48);
            approval.Visible = false;
            prompt = Theme.Put(approval, Theme.Label("", 9.5f, Theme.Accent, true), 12, 3, 264, 42);
            prompt.TextAlign = ContentAlignment.MiddleLeft;
            Theme.Put(approval, Theme.Button("Только просмотр"), 283, 8, 143, 32).Click +=
                (sender, args) => Answer(Decision.View);
            Theme.Put(approval, Theme.Button("Разрешить управление", true), 435, 8, 182, 32).Click +=
                (sender, args) => Answer(Decision.Control);
            Theme.Put(approval, Theme.Button("Отклонить", danger: true), 626, 8, 140, 32).Click +=
                (sender, args) => Answer(Decision.Denied);

            startup = Theme.Put(this, new CheckBox
            {
                Text = "Открывать доступ при входе в Windows",
                ForeColor = Theme.Ink,
                Checked = ReadStartup()
            }, 30, 541, 560, 24);
            startup.CheckedChanged += (sender, args) => ApplyStartup();
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Открыть ScreenShow", null, (sender, args) => RestoreWindow());
            stopFromTray = new ToolStripMenuItem("Остановить доступ", null, (sender, args) => StopSession());
            trayMenu.Items.Add(stopFromTray);
            trayMenu.Items.Add("Выход", null, (sender, args) => Close());
            tray = new NotifyIcon { Icon = Theme.AppIcon, Text = "ScreenShow", ContextMenuStrip = trayMenu, Visible = true };
            tray.DoubleClick += (sender, args) => RestoreWindow();

            foreach (var item in NetworkAddresses()) adapter.Items.Add(item);
            adapter.Items.Add(new Adapter { Name = "Интернет через сервер" });
            adapter.Items.Add(new Adapter { Name = "Интернет напрямую", Public = true });
            if (adapter.Items.Count != 0) adapter.SelectedIndex = 0;
            bool savedHostAvailable = RestoreHostAddress();
            Action updateNetwork = () =>
            {
                var selected = (Adapter)adapter.SelectedItem;
                network.Text = selected != null && selected.Address == null ? selected.Name : "Локальная сеть / VPN";
                UpdateControls();
            };
            adapter.SelectedIndexChanged += (sender, args) => updateNetwork();
            updateNetwork();
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
            if (launchAtLogin && startup.Checked)
            {
                Shown += (sender, args) =>
                {
                    if (!savedHostAvailable)
                    {
                        SetStatus("Автозапуск: сохранённый сетевой адрес недоступен. Выберите способ подключения.", Theme.Red);
                        return;
                    }
                    WindowState = FormWindowState.Minimized;
                    StartHost(true);
                };
            }
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
            statusDot.ForeColor = color == Theme.Red ? Theme.Red : Theme.LiveDot;
            statusDot.Invalidate();
        }

        private void UpdateControls()
        {
            bool idle = host == null && guest == null;
            adapter.Enabled = idle;
            var network = (Adapter)adapter.SelectedItem;
            port.Enabled = idle && network != null && (network.Address != null || network.Public);
            devices.Enabled = idle || (host != null && ownCode.TextLength != 0 && !cancelled);
            create.Enabled = idle && adapter.SelectedItem != null;
            copy.Enabled = ownCode.TextLength != 0 && !cancelled;
            stop.Enabled = !idle && !cancelled;
            otherCode.Enabled = paste.Enabled = idle;
            connect.Enabled = idle && otherCode.Text.Trim().Length != 0;
            startup.Enabled = startup.Checked || (host != null && ownCode.TextLength != 0 && !cancelled);
            stopFromTray.Enabled = !idle && !cancelled;
        }

        private void Post(Action action)
        {
            try { BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        private void StartHost(bool automatic = false)
        {
            var address = (Adapter)adapter.SelectedItem;
            HostSession session;
            string setting;
            if (address.Public)
            {
                NetworkAddress external = automatic ? NetworkAddress.Parse(publicServer) : AskNetworkAddress(true);
                if (external == null) return;
                publicServer = external.ToString();
                setting = "public:" + publicServer + "|" + port.Value;
                session = new HostSession(IPAddress.Any, (int)port.Value, pairs, external);
            }
            else if (address.Address == null)
            {
                NetworkAddress relay = automatic ? NetworkAddress.Parse(relayServer) : AskNetworkAddress(false);
                if (relay == null) return;
                relayServer = relay.ToString();
                setting = "relay:" + relayServer;
                session = new HostSession(relay, pairs);
            }
            else
            {
                var endpoint = new IPEndPoint(address.Address, (int)port.Value);
                setting = endpoint.ToString();
                session = new HostSession(endpoint.Address, endpoint.Port, pairs);
            }
            host = session;
            cancelled = false;
            SetStatus("Создаём защищённое приглашение…", Theme.Accent);
            UpdateControls();
            Run(
                () =>
                {
                    pairs.HostAddress = setting;
                    session.Run(invitation => Post(() =>
                    {
                        if (cancelled) return;
                        Answer(Decision.Denied);
                        TopMost = false;
                        if (hotkey) Native.UnregisterHotKey(Handle, StopHotkey);
                        hotkey = false;
                        ownCode.Text = invitation.ToString();
                        SetStatus(invitation.Relay != null ? "Доступ через интернет открыт. Сервер-посредник подключён." :
                            invitation.PublicAddress != null ? "Прямой доступ готов · " + invitation.PublicAddress :
                            "Доступ открыт. Связанные устройства могут подключиться.", Theme.Success);
                        tray.Text = "ScreenShow — доступ открыт";
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
                        RestoreWindow();
                        tray.Text = "ScreenShow — идёт сеанс";
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
                    tray.Text = "ScreenShow — доступ закрыт";
                    if (automatic && error != null && !cancelled) RestoreWindow();
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
                prompt.Text = peer == null ? "Запрос на доступ через интернет" : "Запрос на доступ от " + peer.Address;
                approval.Visible = true;
                RestoreWindow();
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
            SetStatus(invitation.Relay == null ? "Подключаемся к " + invitation.DisplayAddress + "…" :
                "Подключаемся через интернет…", Theme.Accent);
            UpdateControls();
            var window = new ViewerForm(invitation.DisplayAddress, session);
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

        private bool RestoreHostAddress()
        {
            try
            {
                string saved = pairs.HostAddress;
                if (saved.StartsWith("relay:", StringComparison.Ordinal))
                {
                    relayServer = NetworkAddress.Parse(saved.Substring(6)).ToString();
                    adapter.SelectedIndex = adapter.Items.Count - 2;
                    return true;
                }
                if (saved.StartsWith("public:", StringComparison.Ordinal))
                {
                    string[] settings = saved.Substring(7).Split('|');
                    int local;
                    if (settings.Length != 2 || !int.TryParse(settings[1], out local) ||
                        local < port.Minimum || local > port.Maximum) return false;
                    publicServer = NetworkAddress.Parse(settings[0]).ToString();
                    port.Value = local;
                    adapter.SelectedIndex = adapter.Items.Count - 1;
                    return true;
                }
                string[] endpoint = saved.Split(':');
                int value;
                if (endpoint.Length != 2 || !int.TryParse(endpoint[1], out value) ||
                    value < port.Minimum || value > port.Maximum) return false;
                port.Value = value;
                foreach (Adapter item in adapter.Items)
                    if (item.Address != null && item.Address.ToString() == endpoint[0])
                    {
                        adapter.SelectedItem = item;
                        return true;
                    }
            }
            catch (IOException) { }
            catch (System.Security.Cryptography.CryptographicException) { }
            catch (FormatException) { }
            return false;
        }

        private NetworkAddress AskNetworkAddress(bool direct)
        {
            using (var dialog = new Form())
            {
                dialog.Text = direct ? "Прямое подключение через интернет" : "Подключение через интернет";
                dialog.Icon = Theme.AppIcon;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MinimizeBox = dialog.MaximizeBox = false;
                dialog.ClientSize = new Size(448, 205);
                dialog.BackColor = Theme.Background;
                var card = Theme.Put(dialog, Theme.Card(), 14, 14, 420, 177);
                string label = direct ? "Публичный адрес и внешний порт" : "Адрес сервера-посредника";
                Theme.Put(card, Theme.Label(label, 12, bold: true), 15, 13, 390, 24);
                var field = Theme.CodeField(card, 15, 43, 390, 38);
                field.AccessibleName = label;
                field.Text = direct ? publicServer : relayServer;
                string description = direct ? "Укажите публичный IPv4 или DDNS в формате имя:порт. Настройте переадресацию TCP на порт " + port.Value + "." :
                    "Введите публичное имя или IP и порт, например relay.example.org:47211.";
                var hint = Theme.Put(card, Theme.Label(description,
                    9.5f, Theme.Muted), 15, 90, 390, 34);
                var confirm = Theme.Put(card, Theme.Button("Открыть доступ", true), 15, 129, 180, 35);
                var cancel = Theme.Put(card, Theme.Button("Отмена"), 203, 129, 96, 35);
                cancel.DialogResult = DialogResult.Cancel;
                dialog.AcceptButton = confirm;
                dialog.CancelButton = cancel;
                NetworkAddress relay = null;
                confirm.Click += (sender, args) =>
                {
                    try
                    {
                        relay = NetworkAddress.Parse(field.Text);
                        dialog.Close();
                    }
                    catch (FormatException error) { hint.Text = error.Message; hint.ForeColor = Theme.Red; }
                };
                dialog.ShowDialog(this);
                return relay;
            }
        }

        private static bool ReadStartup()
        {
            try { return StartupRegistration.IsEnabled(); }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
            catch (IOException) { return false; }
        }

        private void ApplyStartup()
        {
            if (changingStartup) return;
            try
            {
                StartupRegistration.SetEnabled(startup.Checked);
                SetStatus(startup.Checked ? "Доступ будет открываться при входе в Windows." : "Автозапуск отключён.", Theme.Success);
            }
            catch (Exception error)
            {
                if (!(error is UnauthorizedAccessException) && !(error is System.Security.SecurityException) &&
                    !(error is IOException) && !(error is InvalidOperationException)) throw;
                changingStartup = true;
                startup.Checked = ReadStartup();
                changingStartup = false;
                SetStatus(error.Message, Theme.Red);
            }
            UpdateControls();
        }

        private void RestoreWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
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

        protected override void Dispose(bool disposing)
        {
            if (disposing && tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                trayMenu.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
