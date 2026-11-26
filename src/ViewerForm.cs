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
            BackColor = Theme.Background;
            StartPosition = FormStartPosition.CenterScreen;
            Size resolution = Screen.PrimaryScreen.WorkingArea.Size;
            ClientSize = new Size(Math.Min(1120, resolution.Width - 60), Math.Min(770, resolution.Height - 80));
            MinimumSize = new Size(590, 300);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;

            var bar = new Panel { Dock = DockStyle.Top, Height = 57, BackColor = Theme.Panel };
            mode = Theme.Put(bar, Theme.Label("", 10, Theme.Accent, true), 16, 8, 270, 36);
            mode.TextAlign = ContentAlignment.MiddleLeft;
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Right, Width = 425, FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false, Padding = new Padding(0, 8, 8, 0)
            };
            var disconnect = Theme.Button("Отключиться");
            disconnect.ForeColor = Theme.Red;
            fullscreen = Theme.Button("На весь экран");
            input = Theme.Button("Включить ввод", true);
            foreach (var button in new[] { disconnect, fullscreen, input })
            {
                button.Size = new Size(131, 35);
                buttons.Controls.Add(button);
            }
            bar.Controls.Add(buttons);
            surface = new ScreenSurface(session) { Dock = DockStyle.Fill };
            Controls.Add(surface);
            Controls.Add(bar);

            disconnect.Click += (sender, args) => Close();
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
            int width = Math.Max(1, (int)(image.Width * scale));
            int height = Math.Max(1, (int)(image.Height * scale));
            return new Rectangle((Width - width) / 2, (Height - height) / 2, width, height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (image == null)
            {
                TextRenderer.DrawText(e.Graphics, "Ожидаем изображение…", Font, ClientRectangle, Theme.Muted,
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
            if (Focused) SendPointer(InputKind.MouseMove, e.Location, 0, heldButtons != 0);
            base.OnMouseMove(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (Focused) SendPointer(InputKind.Wheel, e.Location, Math.Max(-1200, Math.Min(1200, e.Delta)), false);
            base.OnMouseWheel(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
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
