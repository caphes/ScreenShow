using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenShow
{
    internal static class Theme
    {
        internal static readonly Color Background = Color.FromArgb(239, 246, 241);
        internal static readonly Color Panel = Color.White;
        internal static readonly Color Field = Color.FromArgb(230, 241, 234);
        internal static readonly Color Border = Color.FromArgb(218, 232, 222);
        internal static readonly Color ViewerBackground = Color.FromArgb(12, 26, 20);
        internal static readonly Color ViewerHint = Color.FromArgb(174, 202, 187);
        internal static readonly Color Ink = Color.FromArgb(20, 44, 34);
        internal static readonly Color Muted = Color.FromArgb(73, 99, 86);
        internal static readonly Color Accent = Color.FromArgb(23, 104, 72);
        internal static readonly Color Success = Color.FromArgb(17, 105, 60);
        internal static readonly Color Red = Color.FromArgb(159, 42, 57);
        internal static readonly Color DangerFill = Color.FromArgb(252, 234, 236);
        private static readonly System.Lazy<Icon> markIcon = new System.Lazy<Icon>(CreateIcon);

        internal static Icon AppIcon { get { return markIcon.Value; } }

        internal static Label Label(string text, float size = 10, Color? color = null, bool bold = false)
        {
            return new Label
            {
                Text = text,
                ForeColor = color ?? Ink,
                Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
                AutoSize = false
            };
        }

        internal static Button Button(string text, bool primary = false, bool danger = false)
        {
            return new RoundedButton
            {
                Text = text,
                BackColor = primary ? Accent : danger ? DangerFill : Field,
                ForeColor = primary ? Color.White : danger ? Red : Ink,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand
            };
        }

        internal static TextBox TextBox()
        {
            return new TextBox
            {
                BackColor = Field,
                ForeColor = Ink,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 11),
                ShortcutsEnabled = true
            };
        }

        internal static Panel Card(int radius = 20, bool tinted = false)
        {
            return new RoundedPanel(radius) { BackColor = tinted ? Field : Panel };
        }

        internal static TextBox CodeField(Panel parent, int x, int y, int width, int height)
        {
            var field = Put(parent, Card(10, true), x, y, width, height);
            return Put(field, TextBox(), 10, 9, width - 20, height - 18);
        }

        internal static void StyleDropDown(ComboBox box)
        {
            box.DrawMode = DrawMode.OwnerDrawFixed;
            box.DrawItem += (sender, args) =>
            {
                bool selected = (args.State & DrawItemState.ComboBoxEdit) == 0 &&
                    (args.State & DrawItemState.Selected) != 0;
                using (var background = new SolidBrush(selected ? Accent : Field))
                    args.Graphics.FillRectangle(background, args.Bounds);
                if (args.Index < 0) return;
                var textBounds = Rectangle.Inflate(args.Bounds, -6, 0);
                TextRenderer.DrawText(args.Graphics, box.GetItemText(box.Items[args.Index]), box.Font, textBounds,
                    selected ? Color.White : Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
        }

        private static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            float d = 2 * System.Math.Min(radius, System.Math.Min(bounds.Width, bounds.Height) / 2);
            var shape = new GraphicsPath();
            shape.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            shape.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            shape.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            shape.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            shape.CloseFigure();
            return shape;
        }

        private sealed class RoundedPanel : Panel
        {
            private readonly int radius;

            internal RoundedPanel(int radius)
            {
                this.radius = radius;
                DoubleBuffered = true;
                ResizeRedraw = true;
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Parent == null ? Background : Parent.BackColor);
                if (Width < 2 || Height < 2) return;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), radius))
                using (var fill = new SolidBrush(BackColor))
                using (var outline = new Pen(Border))
                {
                    e.Graphics.FillPath(fill, path);
                    e.Graphics.DrawPath(outline, path);
                }
            }
        }

        private sealed class RoundedButton : Button
        {
            private bool hover, pressed;

            internal RoundedButton()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
                FlatAppearance.BorderSize = 0;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Parent == null ? Background : Parent.BackColor);
                if (Width < 3 || Height < 3) return;
                Color fill = Enabled ? BackColor : Field;
                if (Enabled && (hover || pressed)) fill = ControlPaint.Dark(fill, pressed ? 0.09f : 0.04f);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Rounded(new RectangleF(1, 1, Width - 2, Height - 2), 11))
                using (var brush = new SolidBrush(fill))
                {
                    e.Graphics.FillPath(brush, path);
                    if (Focused && ShowFocusCues)
                        using (var focus = new Pen(Accent, 1.5f)) e.Graphics.DrawPath(focus, path);
                }
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }

            protected override void OnMouseEnter(System.EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
            protected override void OnMouseLeave(System.EventArgs e) { base.OnMouseLeave(e); hover = pressed = false; Invalidate(); }
            protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = e.Button == MouseButtons.Left; Invalidate(); }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
        }

        internal sealed class Mark : Control
        {
            internal Mark() { DoubleBuffered = true; TabStop = false; }

            protected override void OnPaint(PaintEventArgs e)
            {
                DrawMark(e.Graphics, Width, Height);
            }
        }

        private static void DrawMark(Graphics graphics, int width, int height)
        {
            var state = graphics.Save();
            try
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.ScaleTransform(width / 64f, height / 64f);
                using (var shape = Rounded(new RectangleF(0.5f, 0.5f, 63, 63), 17))
                using (var fill = new LinearGradientBrush(new RectangleF(0, 0, 64, 64), Accent,
                    Color.FromArgb(29, 141, 96), 62f))
                using (var screen = Rounded(new RectangleF(14, 15, 36, 26), 5))
                using (var line = new Pen(Color.White, 2.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    graphics.FillPath(fill, shape);
                    graphics.DrawPath(line, screen);
                    graphics.DrawLine(line, 32, 42, 32, 49);
                    graphics.DrawLine(line, 24, 49, 40, 49);
                    graphics.DrawEllipse(line, 28.5f, 24.5f, 7, 7);
                }
            }
            finally { graphics.Restore(state); }
        }

        private static Icon CreateIcon()
        {
            using (var bitmap = new Bitmap(64, 64))
            {
                using (var graphics = Graphics.FromImage(bitmap)) DrawMark(graphics, bitmap.Width, bitmap.Height);
                System.IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (var icon = Icon.FromHandle(handle)) return (Icon)icon.Clone();
                }
                finally { DestroyIcon(handle); }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(System.IntPtr handle);

        internal static T Put<T>(Control parent, T control, int x, int y, int width, int height) where T : Control
        {
            control.SetBounds(x, y, width, height);
            parent.Controls.Add(control);
            return control;
        }
    }
}
