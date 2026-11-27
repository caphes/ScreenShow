using System;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyVersion("1.5.0.0")]
[assembly: AssemblyFileVersion("1.5.0.0")]
[assembly: AssemblyInformationalVersion("1.5.0")]

namespace ScreenShow
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(Array.IndexOf(args, "--startup") >= 0));
        }
    }
}
