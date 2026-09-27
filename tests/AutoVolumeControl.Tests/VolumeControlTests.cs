using System;
using System.Linq;
using System.Windows.Forms;
using MaterialSkin.Controls;
using Xunit;

namespace AutoVolumeControl.Tests
{
    public class VolumeControlTests : IDisposable
    {
        private readonly TestRegistryKey settingsKey = new TestRegistryKey();
        private readonly TestRegistryKey runKey = new TestRegistryKey();
        private readonly FakeAudioBackend backend = new FakeAudioBackend();

        public void Dispose()
        {
            settingsKey.Dispose();
            runKey.Dispose();
        }

        private VolumeControl Create()
        {
            return new VolumeControl(
                backend,
                new AppSettings(settingsKey.Path),
                new AutoStart("AutoVolumeControl", @"C:\Apps\AutoVolumeControl.exe", runKey.Path),
                showTrayIcon: false);
        }

        private static bool MenuShowsApp(ContextMenuStrip strip, string app)
        {
            return strip.Items.OfType<ToolStripControlHost>()
                .Select(h => h.Control)
                .OfType<FlowLayoutPanel>()
                .SelectMany(p => p.Controls.OfType<MaterialCheckbox>())
                .Any(c => c.Name == app);
        }

        [Fact]
        public void Startup_SyncsAndBuildsMenuWithApps()
        {
            Sta.Run(() =>
            {
                var chrome = new FakeSession("chrome");
                backend.SetSessions(chrome);
                backend.SetMaster(0.25f, false);

                using var app = Create();
                Assert.True(app.Started.Wait(TimeSpan.FromSeconds(10)));

                Assert.True(Sta.PumpUntil(() => MenuShowsApp(app.ContextMenu, "chrome")));
                Assert.Equal(0.25f, chrome.Volume);
                Assert.Equal("True", settingsKey.GetValue("chrome"));
            });
        }

        [Fact]
        public void NewApp_AppearsInMenuWithoutReopening()
        {
            Sta.Run(() =>
            {
                using var app = Create();
                app.Started.Wait(TimeSpan.FromSeconds(10));

                backend.SetSessions(new FakeSession("spotify"));
                backend.RaiseSessionsChanged();

                Assert.True(Sta.PumpUntil(() => MenuShowsApp(app.ContextMenu, "spotify")));
            });
        }

        [Fact]
        public void StartupWithoutDevice_DoesNotCrash()
        {
            Sta.Run(() =>
            {
                backend.AttachException = new InvalidOperationException("no device");

                using var app = Create();

                Assert.Throws<AggregateException>(() => app.Started.Wait(TimeSpan.FromSeconds(10)));
                Sta.PumpUntil(() => false, 200);
            });
        }

        [Fact]
        public void Exit_ReleasesTheAudioBackend()
        {
            Sta.Run(() =>
            {
                var app = Create();
                app.Started.Wait(TimeSpan.FromSeconds(10));

                app.Exit(null, EventArgs.Empty);

                Assert.True(backend.Disposed);
                Assert.True(app.ContextMenu.IsDisposed);
                app.Dispose();
            });
        }
    }
}
