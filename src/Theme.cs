using System.Drawing;
using System.Windows.Forms;

namespace ScreenShow
{
    internal static class Theme
    {
        internal static readonly Color Background = Color.FromArgb(12, 26, 20);
        internal static readonly Color Panel = Color.FromArgb(22, 42, 32);
        internal static readonly Color Field = Color.FromArgb(34, 60, 46);
        internal static readonly Color ViewerBackground = Color.FromArgb(8, 17, 13);
        internal static readonly Color Ink = Color.FromArgb(239, 249, 242);
        internal static readonly Color Muted = Color.FromArgb(177, 204, 186);
        internal static readonly Color Accent = Color.FromArgb(113, 223, 151);
        internal static readonly Color Success = Color.FromArgb(156, 238, 180);
        internal static readonly Color Red = Color.FromArgb(255, 170, 180);

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

        internal static Button Button(string text, bool primary = false)
        {
            var button = new Button
            {
                Text = text,
                BackColor = primary ? Accent : Field,
                ForeColor = primary ? Background : Ink,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderSize = 0;
            return button;
        }

        internal static TextBox TextBox()
        {
            return new TextBox
            {
                BackColor = Field,
                ForeColor = Ink,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 12),
                ShortcutsEnabled = true
            };
        }

        internal static T Put<T>(Control parent, T control, int x, int y, int width, int height) where T : Control
        {
            control.SetBounds(x, y, width, height);
            parent.Controls.Add(control);
            return control;
        }
    }
}
