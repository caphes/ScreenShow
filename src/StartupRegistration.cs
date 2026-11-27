using Microsoft.Win32;
using System.Windows.Forms;

namespace ScreenShow
{
    internal static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Name = "ScreenShow";
        private static string Command { get { return "\"" + Application.ExecutablePath + "\" --startup"; } }

        internal static bool IsEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && string.Equals(key.GetValue(Name) as string, Command,
                    System.StringComparison.OrdinalIgnoreCase);
        }

        internal static void SetEnabled(bool enabled)
        {
            if (enabled && Command.Length > 260)
                throw new System.InvalidOperationException("Для автозапуска разместите ScreenShow.exe в папке с более коротким путём.");
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(Name, Command, RegistryValueKind.String);
                else if (string.Equals(key.GetValue(Name) as string, Command, System.StringComparison.OrdinalIgnoreCase))
                    key.DeleteValue(Name, false);
            }
        }
    }
}
