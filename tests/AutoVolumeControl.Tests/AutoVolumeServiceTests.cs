using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AutoVolumeControl.Tests
{
    public class AutoVolumeServiceTests : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private readonly FakeAudioBackend backend = new FakeAudioBackend();
        private readonly InMemorySettingsStore store = new InMemorySettingsStore();
        private readonly AppPreferences preferences;
        private readonly Apps apps = new Apps();
        private readonly AutoVolumeService service;

        public AutoVolumeServiceTests()
        {
            preferences = new AppPreferences(store);
            service = new AutoVolumeService(backend, preferences, apps);
        }

        public void Dispose() => service.Dispose();

        private void Flush() => Assert.True(service.Flush().Wait(Timeout));

        [Fact]
        public void Start_AttachesListsAppsAndSyncs()
        {
            var chrome = new FakeSession("chrome");
            backend.SetSessions(chrome, new FakeSession("System", isSystemSound: true));
            backend.SetMaster(0.3f, false);

            Assert.True(service.Start().Wait(Timeout));

            Assert.Equal(1, backend.AttachCount);
            Assert.Equal(new[] { "chrome" }, apps.GetApps());
            Assert.Equal("True", store.Values["chrome"]);
            Assert.Equal(0.3f, chrome.Volume);
        }

        [Fact]
        public void Start_WhenNoDevice_FaultsAndClearsApps()
        {
            apps.Update(new[] { "stale" });
            backend.AttachException = new InvalidOperationException("no device");

            var start = service.Start();

            Assert.Throws<AggregateException>(() => start.Wait(Timeout));
            Assert.Empty(apps.GetApps());
        }

        [Fact]
        public void AfterFailedStart_VolumeEventsAreIgnored()
        {
            backend.AttachException = new InvalidOperationException("no device");
            try { service.Start().Wait(Timeout); } catch (AggregateException) { }

            backend.RaiseMasterVolumeChanged();
            Flush();

            Assert.Equal(0, backend.GetMasterVolumeCount);
        }

        [Fact]
        public void VolumeChange_SyncsEnabledAppsWithCurrentMaster()
        {
            var chrome = new FakeSession("chrome");
            var spotify = new FakeSession("spotify");
            store.Values["spotify"] = "False";
            backend.SetSessions(chrome, spotify);
            service.Start().Wait(Timeout);

            backend.SetMaster(0.55f, true);
            backend.RaiseMasterVolumeChanged();
            Flush();

            Assert.Equal(0.55f, chrome.Volume);
            Assert.True(chrome.Muted);
            Assert.Null(spotify.Volume);
        }

        [Fact]
        public void BurstOfVolumeChanges_EndsOnTheLatestValue()
        {
            var chrome = new FakeSession("chrome");
            backend.SetSessions(chrome);
            service.Start().Wait(Timeout);

            Parallel.For(0, 200, i =>
            {
                backend.SetMaster(i / 200f, false);
                backend.RaiseMasterVolumeChanged();
            });
            backend.SetMaster(0.42f, false);
            backend.RaiseMasterVolumeChanged();
            Flush();

            Assert.Equal(0.42f, chrome.Volume);
        }

        [Fact]
        public void VolumeChangesWhileSyncing_AreMergedIntoOneSync()
        {
            backend.SetSessions(new FakeSession("chrome"));
            service.Start().Wait(Timeout);
            int before = backend.GetMasterVolumeCount;

            using var gate = new ManualResetEventSlim();
            backend.GetMasterVolumeGate = gate;
            backend.GetMasterVolumeEntered.Reset();
            backend.RaiseMasterVolumeChanged();
            Assert.True(backend.GetMasterVolumeEntered.Wait(Timeout));

            for (int i = 0; i < 50; i++)
                backend.RaiseMasterVolumeChanged();

            gate.Set();
            Flush();

            // The running sync plus exactly one merged follow-up.
            Assert.Equal(before + 2, backend.GetMasterVolumeCount);
        }

        [Fact]
        public void NewSession_IsListedAndSyncedRightAway()
        {
            backend.SetMaster(0.2f, false);
            service.Start().Wait(Timeout);

            var game = new FakeSession("game");
            backend.SetSessions(game);
            backend.RaiseSessionCreated();
            Flush();

            Assert.Equal(new[] { "game" }, apps.GetApps());
            Assert.Equal(0.2f, game.Volume);
        }

        [Fact]
        public void DefaultDeviceChange_ReattachesAndSyncs()
        {
            service.Start().Wait(Timeout);
            var headset = new FakeSession("discord");
            backend.SetSessions(headset);
            backend.SetMaster(0.8f, false);

            backend.RaiseDefaultDeviceChanged();
            Flush();

            Assert.Equal(2, backend.AttachCount);
            Assert.Equal(new[] { "discord" }, apps.GetApps());
            Assert.Equal(0.8f, headset.Volume);
        }

        [Fact]
        public void DefaultDeviceRemoved_ClearsApps_AndRecoversWhenADeviceReturns()
        {
            backend.SetSessions(new FakeSession("chrome"));
            service.Start().Wait(Timeout);

            backend.AttachException = new InvalidOperationException("no device");
            backend.RaiseDefaultDeviceChanged();
            Flush();
            Assert.Empty(apps.GetApps());

            backend.AttachException = null;
            backend.RaiseDefaultDeviceChanged();
            Flush();
            Assert.Equal(new[] { "chrome" }, apps.GetApps());
        }

        [Fact]
        public void RefreshAsync_UpdatesAppsWithoutChangingVolumes()
        {
            service.Start().Wait(Timeout);
            var chrome = new FakeSession("chrome");
            backend.SetSessions(chrome);

            Assert.True(service.RefreshAsync().Wait(Timeout));

            Assert.Equal(new[] { "chrome" }, apps.GetApps());
            Assert.Equal("True", store.Values["chrome"]);
            Assert.Equal(0, chrome.ApplyCount);
        }

        [Fact]
        public void RefreshAsync_BeforeAttach_DoesNothing()
        {
            Assert.True(service.RefreshAsync().Wait(Timeout));
            Assert.Equal(0, backend.AttachCount);
        }

        [Fact]
        public void ClosedApps_DisappearFromTheList()
        {
            backend.SetSessions(new FakeSession("chrome"), new FakeSession("spotify"));
            service.Start().Wait(Timeout);

            backend.SetSessions(new FakeSession("spotify"));
            service.RefreshAsync().Wait(Timeout);

            Assert.Equal(new[] { "spotify" }, apps.GetApps());
        }

        [Fact]
        public void SessionsAreDisposedAfterEachPass()
        {
            var chrome = new FakeSession("chrome");
            backend.SetSessions(chrome);
            service.Start().Wait(Timeout);
            service.RefreshAsync().Wait(Timeout);

            Assert.Equal(2, chrome.DisposeCount);
        }

        [Fact]
        public void AllBackendCallsHappenOnOneThread()
        {
            backend.SetSessions(new FakeSession("chrome"));
            service.Start().Wait(Timeout);
            backend.RaiseMasterVolumeChanged();
            backend.RaiseSessionCreated();
            backend.RaiseDefaultDeviceChanged();
            service.RefreshAsync().Wait(Timeout);
            service.Dispose();

            Assert.Single(backend.CallingThreadIds);
            Assert.NotEqual(Thread.CurrentThread.ManagedThreadId, backend.CallingThreadIds.Single());
        }

        [Fact]
        public void Dispose_DisposesBackendAndUnsubscribes()
        {
            service.Start().Wait(Timeout);

            service.Dispose();

            Assert.True(backend.Disposed);
            Assert.False(backend.HasSubscribers);
        }

        [Fact]
        public void Dispose_Twice_DoesNotThrow()
        {
            service.Start().Wait(Timeout);
            service.Dispose();
            service.Dispose();
        }

        [Fact]
        public void EventsAfterDispose_AreIgnored()
        {
            service.Start().Wait(Timeout);
            int attaches = backend.AttachCount;
            service.Dispose();

            backend.RaiseMasterVolumeChanged();
            backend.RaiseDefaultDeviceChanged();

            Assert.Equal(attaches, backend.AttachCount);
        }
    }
}
