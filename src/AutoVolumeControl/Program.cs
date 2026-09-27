using System;
using System.Windows.Forms;

namespace AutoVolumeControl
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // DPI awareness (PerMonitorV2) is configured in App.config; app.manifest declares the
            // supported Windows versions that WinForms needs for it.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new VolumeControl());
        }
    }
}
