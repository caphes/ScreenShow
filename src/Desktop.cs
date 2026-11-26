using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenShow
{
    internal sealed class DesktopCapture : IDisposable
    {
        private readonly ImageCodecInfo jpeg;
        private readonly EncoderParameters parameters;
        private readonly MemoryStream stream = new MemoryStream();
        private Bitmap source;
        private Bitmap scaled;

        internal DesktopCapture()
        {
            jpeg = Array.Find(ImageCodecInfo.GetImageEncoders(), codec => codec.FormatID == ImageFormat.Jpeg.Guid);
            parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, 63L);
        }

        internal byte[] Capture()
        {
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            if (source == null || source.Size != bounds.Size)
            {
                if (source != null) source.Dispose();
                if (scaled != null) scaled.Dispose();
                source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
                double factor = Math.Min((double)Protocol.FrameWidth / bounds.Width,
                    (double)Protocol.FrameHeight / bounds.Height);
                scaled = factor < 1 ? new Bitmap(Math.Max(1, (int)Math.Round(bounds.Width * factor)),
                    Math.Max(1, (int)Math.Round(bounds.Height * factor)), PixelFormat.Format24bppRgb) : null;
            }

            using (Graphics graphics = Graphics.FromImage(source))
            {
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size,
                    CopyPixelOperation.SourceCopy);
                Native.DrawCursor(graphics, bounds);
            }

            if (scaled != null)
            {
                using (Graphics graphics = Graphics.FromImage(scaled))
                {
                    graphics.InterpolationMode = InterpolationMode.Bilinear;
                    graphics.DrawImage(source, new Rectangle(Point.Empty, scaled.Size));
                }
            }
            stream.SetLength(0);
            (scaled ?? source).Save(stream, jpeg, parameters);
            return stream.ToArray();
        }

        public void Dispose()
        {
            if (source != null) source.Dispose();
            if (scaled != null) scaled.Dispose();
            parameters.Dispose();
            stream.Dispose();
        }
    }

    internal struct InputCommand
    {
        internal readonly InputKind Kind;
        internal readonly ushort X;
        internal readonly ushort Y;
        internal readonly int Value;

        internal InputCommand(InputKind kind, ushort x = 0, ushort y = 0, int value = 0)
        {
            Kind = kind;
            X = x;
            Y = y;
            Value = value;
        }

        internal void Write(Stream stream, byte[] buffer)
        {
            buffer[0] = (byte)Kind;
            buffer[1] = (byte)X;
            buffer[2] = (byte)(X >> 8);
            buffer[3] = (byte)Y;
            buffer[4] = (byte)(Y >> 8);
            buffer[5] = (byte)Value;
            buffer[6] = (byte)(Value >> 8);
            buffer[7] = (byte)(Value >> 16);
            buffer[8] = (byte)(Value >> 24);
            stream.Write(buffer, 0, buffer.Length);
        }

        internal static InputCommand Read(BinaryReader reader)
        {
            return new InputCommand((InputKind)reader.ReadByte(), reader.ReadUInt16(), reader.ReadUInt16(),
                reader.ReadInt32());
        }
    }

    internal sealed class DesktopInput : IDisposable
    {
        private readonly HashSet<int> keys = new HashSet<int>();
        private readonly HashSet<int> buttons = new HashSet<int>();

        internal void Apply(InputCommand input)
        {
            switch (input.Kind)
            {
                case InputKind.MouseMove:
                    Move(input.X, input.Y);
                    break;
                case InputKind.Button:
                    int button = input.Value & 255;
                    bool pressed = (input.Value & 256) != 0;
                    if (button < 1 || button > 3 || (input.Value & ~511) != 0)
                        throw new InvalidDataException("Недопустимая кнопка мыши.");
                    Move(input.X, input.Y);
                    SetButton(button, pressed);
                    break;
                case InputKind.Wheel:
                    if (input.Value < -1200 || input.Value > 1200)
                        throw new InvalidDataException("Недопустимое значение колеса мыши.");
                    Move(input.X, input.Y);
                    Native.Mouse(0, 0, unchecked((uint)input.Value), 0x800);
                    break;
                case InputKind.Key:
                    int key = input.Value & 255;
                    if (key == 0 || key == 255 || (input.Value & ~1023) != 0)
                        throw new InvalidDataException("Недопустимая клавиша.");
                    SetKey(key | (input.Value & 512), (input.Value & 256) != 0);
                    break;
                case InputKind.ReleaseAll:
                    Dispose();
                    break;
                case InputKind.Text:
                    if (input.Value < 32 || (input.Value >= 127 && input.Value <= 159) || input.Value > 0x10FFFF ||
                        (input.Value >= 0xD800 && input.Value <= 0xDFFF))
                        throw new InvalidDataException("Недопустимый символ.");
                    Native.TypeText(char.ConvertFromUtf32(input.Value));
                    break;
                default:
                    throw new InvalidDataException("Неизвестное событие управления.");
            }
        }

        private static void Move(ushort x, ushort y)
        {
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            Rectangle desktop = SystemInformation.VirtualScreen;
            int desktopX = bounds.X + (int)Math.Round(x * (bounds.Width - 1) / 65535.0);
            int desktopY = bounds.Y + (int)Math.Round(y * (bounds.Height - 1) / 65535.0);
            int absoluteX = (int)(((long)(desktopX - desktop.X) * 65536 + 32768) / desktop.Width);
            int absoluteY = (int)(((long)(desktopY - desktop.Y) * 65536 + 32768) / desktop.Height);
            Native.Mouse(absoluteX, absoluteY, 0, 0xC001);
        }

        private void SetButton(int button, bool down)
        {
            if (!down && !buttons.Contains(button)) return;
            uint flag = (uint)(2 << ((button - 1) * 2 + (down ? 0 : 1)));
            if (Native.Mouse(0, 0, 0, flag))
            {
                if (down) buttons.Add(button);
                else buttons.Remove(button);
            }
        }

        private void SetKey(int key, bool down)
        {
            if (!down && !keys.Contains(key)) return;
            if (Native.Key((ushort)(key & 255), (key & 512) != 0, down))
            {
                if (down) keys.Add(key);
                else keys.Remove(key);
            }
        }

        public void Dispose()
        {
            foreach (int button in new List<int>(buttons)) SetButton(button, false);
            foreach (int key in new List<int>(keys)) SetKey(key, false);
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MouseData
        {
            internal int X, Y;
            internal uint Data, Flags, Time;
            internal UIntPtr Extra;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyData
        {
            internal ushort VirtualKey, Scan;
            internal uint Flags, Time;
            internal UIntPtr Extra;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputData
        {
            [FieldOffset(0)] internal MouseData Mouse;
            [FieldOffset(0)] internal KeyData Key;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            internal uint Type;
            internal InputData Data;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CursorInfo
        {
            internal int Size, Flags;
            internal IntPtr Handle;
            internal int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IconInfo
        {
            internal int IsIcon, HotX, HotY;
            internal IntPtr Mask, Color;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, ref Input input, int size);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, [In] Input[] input, int size);

        [DllImport("user32.dll")]
        private static extern bool GetCursorInfo(ref CursorInfo info);

        [DllImport("user32.dll")]
        private static extern bool GetIconInfo(IntPtr handle, out IconInfo info);

        [DllImport("user32.dll")]
        private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height,
            uint step, IntPtr brush, uint flags);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        internal static extern bool UnregisterHotKey(IntPtr handle, int id);

        private static readonly int InputSize = Marshal.SizeOf(typeof(Input));

        internal static bool Mouse(int x, int y, uint data, uint flags)
        {
            var input = new Input { Data = new InputData
                { Mouse = new MouseData { X = x, Y = y, Data = data, Flags = flags } } };
            return SendInput(1, ref input, InputSize) == 1;
        }

        internal static bool Key(ushort key, bool extended, bool down)
        {
            uint flags = (extended ? 1u : 0u) | (down ? 0u : 2u);
            var input = new Input { Type = 1, Data = new InputData
                { Key = new KeyData { VirtualKey = key, Flags = flags } } };
            return SendInput(1, ref input, InputSize) == 1;
        }

        internal static void TypeText(string text)
        {
            var events = new Input[text.Length * 2];
            for (int i = 0; i < text.Length; i++)
            {
                var data = new KeyData { Scan = text[i], Flags = 4 };
                events[i * 2] = new Input { Type = 1, Data = new InputData { Key = data } };
                data.Flags |= 2;
                events[i * 2 + 1] = new Input { Type = 1, Data = new InputData { Key = data } };
            }
            SendInput((uint)events.Length, events, InputSize);
        }

        internal static void DrawCursor(Graphics graphics, Rectangle bounds)
        {
            var cursor = new CursorInfo { Size = Marshal.SizeOf(typeof(CursorInfo)) };
            IconInfo icon;
            if (!GetCursorInfo(ref cursor) || cursor.Flags != 1 ||
                !bounds.Contains(cursor.X, cursor.Y) || !GetIconInfo(cursor.Handle, out icon)) return;
            try
            {
                IntPtr dc = graphics.GetHdc();
                try
                {
                    DrawIconEx(dc, cursor.X - bounds.X - icon.HotX, cursor.Y - bounds.Y - icon.HotY,
                        cursor.Handle, 0, 0, 0, IntPtr.Zero, 3);
                }
                finally { graphics.ReleaseHdc(dc); }
            }
            finally
            {
                if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask);
                if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color);
            }
        }
    }
}
