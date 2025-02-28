using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AutoVolumeControl
{
    static class Program
    {
        private enum ProcessDpiAwareness
        {
            Process_DPI_Unaware = 0,
            Process_System_DPI_Aware = 1,
            Process_Per_Monitor_DPI_Aware = 2
        }

        [DllImport("Shcore.dll")]
        private static extern int SetProcessDpiAwareness(ProcessDpiAwareness value);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            if (Environment.OSVersion.Version.Major >= 6 && Environment.OSVersion.Version.Build >= 9600)
            {
                int result = SetProcessDpiAwareness(ProcessDpiAwareness.Process_Per_Monitor_DPI_Aware);
                if (result != 0)
                {
                    SetProcessDPIAware();
                }
            }
            else
            {
                SetProcessDPIAware();
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new VolumeControl());
        }
    }
}
