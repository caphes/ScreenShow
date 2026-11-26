using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ScreenShow
{
    internal sealed class ViewerForm : Form
    {
        private readonly ScreenSurface surface;
        private readonly Button input;
        private readonly Button fullscreen;
        private readonly Label mode;
        private readonly object gate = new object();
        private Bitmap waiting;
        private bool posted;
        private bool closed;
        private bool isFullscreen;
        private FormWindowState previousState;
        private Rectangle previousBounds;

        internal ViewerForm(string endpoint, ViewerSession session)
        {
            Text = "ScreenShow — " + endpoint;
            Icon = Theme.AppIcon;
            BackColor = Theme.Background;
            StartPosition = FormStartPosition.CenterScreen;
            Size resolution = Screen.PrimaryScreen.WorkingArea.Size;
            ClientSize = new Size(Math.Min(1120, resolution.Width - 60), Math.Min(770, resolution.Height - 80));
            MinimumSize = new Size(780, 300);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;

            var bar = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom, Height = 64, BackColor = Theme.Background,
                ColumnCount = 3, RowCount = 1, Padding = new Padding(12, 0, 12, 0)
            };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 164));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 354));
            bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var indicator = Theme.Card(18, true);
            indicator.Size = new Size(160, 38);
            indicator.Anchor = AnchorStyles.Left;
            indicator.Margin = Padding.Empty;
            Theme.Put(indicator, new Theme.StatusDot(), 7, 11, 16, 16);
            mode = Theme.Put(indicator, Theme.Label("", 9, Theme.Accent, true), 26, 0, 132, 38);
            mode.TextAlign = ContentAlignment.MiddleLeft;
            bar.Controls.Add(indicator, 0, 0);

            var zoomGroup = Theme.Card(14);
            zoomGroup.Size = new Size(208, 44);
            zoomGroup.Anchor = AnchorStyles.None;
            zoomGroup.Margin = Padding.Empty;
            var zoomOut = Theme.Put(zoomGroup, Theme.Button("−"), 5, 5, 34, 34);
            var zoomIn = Theme.Put(zoomGroup, Theme.Button("+"), 86, 5, 34, 34);
            var zoomAmount = Theme.Put(zoomGroup, Theme.Label("100%", 9, Theme.Muted, true), 40, 5, 45, 34);
            zoomAmount.TextAlign = ContentAlignment.MiddleCenter;
            var reset = Theme.Put(zoomGroup, Theme.Button("По размеру"), 123, 5, 80, 34);
            reset.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            zoomOut.AccessibleName = "Уменьшить экран";
            zoomIn.AccessibleName = "Приблизить экран";
            bar.Controls.Add(zoomGroup, 1, 0);

            var buttons = new FlowLayoutPanel
            {
                Anchor = AnchorStyles.Right, Width = 354, Height = 42, FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false, Margin = Padding.Empty
            };
            var disconnect = Theme.Button("Отключиться", danger: true);
            fullscreen = Theme.Button("На весь экран");
            input = Theme.Button("Включить ввод", true);
            foreach (var button in new[] { disconnect, fullscreen, input })
            {
                button.Size = new Size(112, 35);
                button.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
                buttons.Controls.Add(button);
            }
            bar.Controls.Add(buttons, 2, 0);
            var viewport = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 0) };
            surface = new ScreenSurface(session) { Dock = DockStyle.Fill };
            viewport.Controls.Add(surface);
            Controls.Add(viewport);
            Controls.Add(bar);

            surface.ZoomChanged += (sender, args) => zoomAmount.Text = surface.ZoomPercentage + "%";
            disconnect.Click += (sender, args) => Close();
            zoomOut.Click += (sender, args) => surface.Zoom(1 / 1.25);
            zoomIn.Click += (sender, args) => surface.Zoom(1.25);
            reset.Click += (sender, args) => surface.ResetZoom();
            fullscreen.Click += (sender, args) => ToggleFullscreen();
            input.Click += (sender, args) =>
            {
                surface.Controlling = !surface.Controlling;
                input.Text = surface.Controlling ? "Выключить ввод" : "Включить ввод";
                mode.Text = surface.Controlling ? "Управление активно" : "Ввод выключен";
                if (surface.Controlling) surface.Focus();
            };
            Deactivate += (sender, args) => surface.Release();
        }

        internal void Start(bool control)
        {
            input.Visible = control;
            mode.Text = control ? "Ввод выключен" : "Только просмотр";
            Show();
            Activate();
            ShowFrame();
        }

        internal void Submit(Bitmap frame)
        {
            lock (gate)
            {
                if (closed)
                {
                    frame.Dispose();
                    return;
                }
                if (waiting != null) waiting.Dispose();
                waiting = frame;
                if (posted || !IsHandleCreated) return;
                posted = true;
                try { BeginInvoke(new Action(ShowFrame)); }
                catch (InvalidOperationException) { posted = false; }
            }
        }

        private void ShowFrame()
        {
            Bitmap frame;
            lock (gate)
            {
                frame = waiting;
                waiting = null;
                posted = false;
            }
            if (frame != null) surface.SetImage(frame);
        }

        private void ToggleFullscreen()
        {
            if (isFullscreen)
            {
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.Sizable;
                Bounds = previousBounds;
                WindowState = previousState;
            }
            else
            {
                previousState = WindowState;
                previousBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                Screen screen = Screen.FromControl(this);
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.None;
                Bounds = screen.Bounds;
            }
            isFullscreen = !isFullscreen;
            fullscreen.Text = isFullscreen ? "Выйти из режима" : "На весь экран";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (gate)
                {
                    closed = true;
                    if (waiting != null) waiting.Dispose();
                    waiting = null;
                }
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class ScreenSurface : Control
    {
        private readonly ViewerSession session;
        private Bitmap image;
        private int heldButtons;
        private bool controlling;
        private double zoom = 1, panX, panY;
        private bool panning;
        private Point dragPoint;
        internal event EventHandler ZoomChanged;
        internal int ZoomPercentage { get { return (int)Math.Round(zoom * 100); } }

        internal ScreenSurface(ViewerSession session)
        {
            this.session = session;
            SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.ViewerBackground;
            AccessibleName = "Удалённый экран";
            TabStop = true;
        }

        internal bool Controlling
        {
            get { return controlling; }
            set
            {
                if (!value) Release();
                controlling = value;
                Cursor = value ? Cursors.Cross : Cursors.Default;
            }
        }

        internal void SetImage(Bitmap frame)
        {
            if (image != null) image.Dispose();
            image = frame;
            Invalidate();
        }

        private Rectangle ImageBounds()
        {
            if (image == null || Width == 0 || Height == 0) return Rectangle.Empty;
            double scale = Math.Min((double)Width / image.Width, (double)Height / image.Height);
            int width = Math.Max(1, (int)(image.Width * scale * zoom));
            int height = Math.Max(1, (int)(image.Height * scale * zoom));
            double limitX = Math.Max(0, (width - Width) / 2.0);
            double limitY = Math.Max(0, (height - Height) / 2.0);
            panX = Math.Max(-limitX, Math.Min(limitX, panX));
            panY = Math.Max(-limitY, Math.Min(limitY, panY));
            return new Rectangle((int)Math.Round((Width - width) / 2.0 + panX),
                (int)Math.Round((Height - height) / 2.0 + panY), width, height);
        }

        internal void Zoom(double factor, Point? anchor = null)
        {
            Rectangle before = ImageBounds();
            double next = Math.Min(5, Math.Max(1, zoom * factor));
            if (before.IsEmpty || next == zoom) return;
            Point focus = anchor ?? new Point(Width / 2, Height / 2);
            zoom = next;
            Rectangle after = ImageBounds();
            panX += focus.X - (after.X + (focus.X - before.X) * (double)after.Width / before.Width);
            panY += focus.Y - (after.Y + (focus.Y - before.Y) * (double)after.Height / before.Height);
            Invalidate();
            if (ZoomChanged != null) ZoomChanged(this, EventArgs.Empty);
        }

        internal void ResetZoom()
        {
            zoom = 1;
            panX = panY = 0;
            Invalidate();
            if (ZoomChanged != null) ZoomChanged(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (image == null)
            {
                TextRenderer.DrawText(e.Graphics, "Ожидаем изображение…", Font, ClientRectangle, Theme.ViewerHint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            e.Graphics.InterpolationMode = InterpolationMode.Bilinear;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.DrawImage(image, ImageBounds());
        }

        private bool SendPointer(InputKind kind, Point location, int value, bool clamp)
        {
            Rectangle area = ImageBounds();
            if (!controlling || area.IsEmpty || (!clamp && !area.Contains(location))) return false;
            int x = Math.Min(area.Width - 1, Math.Max(0, location.X - area.X));
            int y = Math.Min(area.Height - 1, Math.Max(0, location.Y - area.Y));
            session.Queue(new InputCommand(kind, (ushort)(x * 65535L / Math.Max(1, area.Width - 1)),
                (ushort)(y * 65535L / Math.Max(1, area.Height - 1)), value));
            return true;
        }

        private static int ButtonNumber(MouseButtons button)
        {
            switch (button)
            {
                case MouseButtons.Left: return 1;
                case MouseButtons.Right: return 2;
                case MouseButtons.Middle: return 3;
                default: return 0;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left && zoom > 1 && (!controlling || (ModifierKeys & Keys.Shift) != 0))
            {
                Release();
                panning = true;
                dragPoint = e.Location;
                Capture = true;
                return;
            }
            int button = ButtonNumber(e.Button);
            if (button != 0 && SendPointer(InputKind.Button, e.Location, button | 256, false))
            {
                heldButtons |= 1 << button;
                Capture = true;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (panning && e.Button == MouseButtons.Left)
            {
                panning = false;
                Capture = false;
                return;
            }
            int button = ButtonNumber(e.Button);
            if ((heldButtons & (1 << button)) != 0)
            {
                SendPointer(InputKind.Button, e.Location, button, true);
                heldButtons &= ~(1 << button);
                if (heldButtons == 0) Capture = false;
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (panning)
            {
                panX += e.X - dragPoint.X;
                panY += e.Y - dragPoint.Y;
                dragPoint = e.Location;
                Invalidate();
                return;
            }
            if (Focused) SendPointer(InputKind.MouseMove, e.Location, 0, heldButtons != 0);
            base.OnMouseMove(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!controlling || (ModifierKeys & Keys.Control) != 0)
            {
                Zoom(Math.Pow(1.25, e.Delta / 120.0), e.Location);
                return;
            }
            if (Focused) SendPointer(InputKind.Wheel, e.Location, Math.Max(-1200, Math.Min(1200, e.Delta)), false);
            base.OnMouseWheel(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (!Capture) panning = false;
            if (!Capture && heldButtons != 0) Release();
            base.OnMouseCaptureChanged(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return controlling || base.IsInputKey(keyData);
        }

        protected override void WndProc(ref Message m)
        {
            if (controlling && (m.Msg == 0x0100 || m.Msg == 0x0101 || m.Msg == 0x0104 || m.Msg == 0x0105))
            {
                int key = m.WParam.ToInt32();
                if (key > 0 && key < 255)
                {
                    int flags = unchecked((int)m.LParam.ToInt64());
                    int scan = (flags >> 16) & 255;
                    bool extended = (flags & (1 << 24)) != 0;
                    if (key == (int)Keys.ShiftKey) key = scan == 0x36 ? (int)Keys.RShiftKey : (int)Keys.LShiftKey;
                    if (key == (int)Keys.ControlKey) key = extended ? (int)Keys.RControlKey : (int)Keys.LControlKey;
                    if (key == (int)Keys.Menu) key = extended ? (int)Keys.RMenu : (int)Keys.LMenu;
                    bool down = m.Msg == 0x0100 || m.Msg == 0x0104;
                    session.Queue(new InputCommand(InputKind.Key, value: key | (down ? 256 : 0) | (extended ? 512 : 0)));
                }
                return;
            }
            base.WndProc(ref m);
        }

        internal void Release()
        {
            if (controlling) session.Queue(new InputCommand(InputKind.ReleaseAll));
            heldButtons = 0;
            panning = false;
            if (Capture) Capture = false;
        }

        protected override void OnLostFocus(EventArgs e)
        {
            Release();
            base.OnLostFocus(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && image != null)
            {
                image.Dispose();
                image = null;
            }
            base.Dispose(disposing);
        }
    }
}
