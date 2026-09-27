using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Xunit;

namespace AutoVolumeControl.Tests
{
    [Collection(IsolatedCollection.Name)]
    public class MenuLeakTests : IDisposable
    {
        private readonly TestRegistryKey runKey = new TestRegistryKey();
        private readonly AppPreferences preferences = new AppPreferences(new InMemorySettingsStore());
        private readonly Apps apps = new Apps();
        private readonly AutoStart autoStart;

        public MenuLeakTests()
        {
            autoStart = new AutoStart("AutoVolumeControl", @"C:\Apps\AutoVolumeControl.exe", runKey.Path);
        }

        public void Dispose() => runKey.Dispose();

        [DllImport("user32.dll")]
        private static extern uint GetGuiResources(IntPtr process, uint flags);

        [Fact]
        public void RepeatedRebuilds_DoNotLeakWindowHandles()
        {
            Sta.Run(() =>
            {
                const uint GdiObjects = 0, UserObjects = 1;
                var process = System.Diagnostics.Process.GetCurrentProcess().Handle;
                using var strip = new ContextMenuStrip();
                using var icons = new AppIconCache();
                var menu = new MenuHandler(strip, preferences, apps, autoStart, () => System.Threading.Tasks.Task.CompletedTask, icons);
                // Real executables, so real icons are extracted (and must be released when their app goes away).
                var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var notepad = System.IO.Path.Combine(Environment.SystemDirectory, "notepad.exe");

                void RebuildWithHandles(int i)
                {
                    // A new name every time: each rebuild extracts a new icon and drops the previous one.
                    apps.Update(new[] { new AppInfo($"app{i}", i % 2 == 0 ? notepad : exe), new AppInfo("chrome", notepad), new AppInfo("unknown") });
                    menu.Generate();
                    // Hosted controls only allocate window handles once created, as when the menu is shown.
                    foreach (var host in strip.Items.OfType<ToolStripControlHost>())
                    {
                        host.Control.CreateControl();
                        foreach (Control child in host.Control.Controls)
                            child.CreateControl();
                    }
                }

                for (int i = 0; i < 5; i++)
                    RebuildWithHandles(i);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                uint user = GetGuiResources(process, UserObjects);
                uint gdi = GetGuiResources(process, GdiObjects);

                for (int i = 0; i < 200; i++)
                    RebuildWithHandles(i);
                GC.Collect();
                GC.WaitForPendingFinalizers();

                Assert.Equal(3, icons.CachedCount);
                // Without disposing the old items this grew by several handles per rebuild (> 1000 here).
                Assert.InRange((int)GetGuiResources(process, UserObjects) - (int)user, -50, 50);
                Assert.InRange((int)GetGuiResources(process, GdiObjects) - (int)gdi, -50, 50);
            });
        }
    }
}
