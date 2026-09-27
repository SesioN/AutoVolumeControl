using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace AutoVolumeControl
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            if (CompatLayer.RelaunchWithoutDpiLayers())
                return;

            using var instance = SingleInstance.Acquire();
            if (!instance.IsFirstInstance)
                return;

            using var log = LogFile.Start();
            Trace.WriteLine($"Starting {Application.ProductName} {Application.ProductVersion}");

            // A tray app has no window to show the default error dialog in a useful way; log and keep running.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (sender, e) => Trace.WriteLine($"Unhandled UI exception: {e.Exception}");
            AppDomain.CurrentDomain.UnhandledException += (sender, e) => Trace.WriteLine($"Unhandled exception: {e.ExceptionObject}");

            // DPI awareness is declared in app.manifest.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new VolumeControl());
            Trace.WriteLine("Exited");
        }
    }
}
