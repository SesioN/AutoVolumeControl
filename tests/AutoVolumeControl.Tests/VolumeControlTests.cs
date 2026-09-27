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

        private VolumeControl Create(TimeSpan? errorDelay = null, TimeSpan? retryInterval = null)
        {
            return new VolumeControl(
                backend,
                new AppSettings(settingsKey.Path),
                new AutoStart("AutoVolumeControl", @"C:\Apps\AutoVolumeControl.exe", runKey.Path),
                showTrayIcon: false,
                errorDelay ?? TimeSpan.FromMilliseconds(200),
                retryInterval ?? TimeSpan.FromMilliseconds(50));
        }

        private static bool MenuShowsApp(ContextMenuStrip strip, string app)
        {
            return strip.Items.OfType<ToolStripControlHost>()
                .Select(h => h.Control)
                .OfType<TableLayoutPanel>()
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
        public void Menu_HasItemsRightAfterStartup()
        {
            Sta.Run(() =>
            {
                backend.AttachException = new InvalidOperationException("no device");
                using var app = Create();

                Assert.NotEmpty(app.ContextMenu.Items.Cast<ToolStripItem>());
            });
        }

        [Fact]
        public void PersistentStartFailure_ShowsError()
        {
            Sta.Run(() =>
            {
                backend.AttachException = new InvalidOperationException("no device");
                using var app = Create(TimeSpan.FromMilliseconds(100));

                Assert.True(Sta.PumpUntil(() => app.ShownError != null));
                Assert.Contains("no device", app.ShownError);
            });
        }

        [Fact]
        public void StartFailureThatRecovers_ShowsNoError()
        {
            // E.g. at logon the audio service becomes ready a moment later.
            Sta.Run(() =>
            {
                backend.AttachException = new InvalidOperationException("service not ready");
                using var app = Create(errorDelay: TimeSpan.FromSeconds(1), retryInterval: TimeSpan.FromMilliseconds(50));
                Assert.Throws<AggregateException>(() => app.Started.Wait(TimeSpan.FromSeconds(10)));

                // The service retries every ~50 ms and succeeds long before the 1 s error delay ends.
                backend.AttachException = null;
                Sta.PumpUntil(() => false, 1500);

                Assert.Null(app.ShownError);
            });
        }

        [Fact]
        public void ExitButton_ExitsAfterTheClickWasProcessed()
        {
            Sta.Run(() =>
            {
                var app = Create();
                app.Started.Wait(TimeSpan.FromSeconds(10));
                var exit = app.ContextMenu.Items.OfType<ToolStripControlHost>().Select(h => h.Control).OfType<MaterialButton>().Single();

                exit.PerformClick();
                Assert.False(backend.Disposed);

                Assert.True(Sta.PumpUntil(() => backend.Disposed));
                app.Dispose();
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
