using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AutoVolumeControl.Tests
{
    public class AutoVolumeServiceTests : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(50);

        private readonly FakeAudioBackend backend = new FakeAudioBackend();
        private readonly InMemorySettingsStore store = new InMemorySettingsStore();
        private readonly AppPreferences preferences;
        private readonly Apps apps = new Apps();
        private readonly AutoVolumeService service;

        public AutoVolumeServiceTests()
        {
            preferences = new AppPreferences(store);
            service = new AutoVolumeService(backend, preferences, apps, RetryInterval);
        }

        public void Dispose() => service.Dispose();

        private void Flush() => Assert.True(service.Flush().Wait(Timeout));

        private void StartAttached()
        {
            Assert.True(service.Start().Wait(Timeout));
        }

        private static void WaitUntil(Func<bool> condition, string because)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(watch.Elapsed < Timeout, because);
                Thread.Sleep(10);
            }
        }

        // ---- start ----

        [Fact]
        public void Start_AttachesListsAppsAndSyncs()
        {
            var chrome = new FakeSession("chrome");
            backend.SetSessions(chrome, new FakeSession("System", isSystemSound: true));
            backend.SetMaster(0.3f, false);

            StartAttached();

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
        public void Start_WhenAudioServiceNotReady_RetriesUntilItSucceeds()
        {
            // E.g. autostart at logon before the Windows audio service is up.
            backend.AttachException = new InvalidOperationException("service not running");
            var chrome = new FakeSession("chrome");
            backend.SetSessions(chrome);
            backend.SetMaster(0.6f, false);
            try { service.Start().Wait(Timeout); } catch (AggregateException) { }

            backend.AttachException = null;

            WaitUntil(() => apps.GetApps().Contains("chrome"), "the retry should attach once the service is ready");
            Assert.Equal(0.6f, chrome.Volume);
        }

        [Fact]
        public void WhileAttached_NoPeriodicWork()
        {
            backend.SetSessions(new FakeSession("chrome"));
            StartAttached();
            int reads = backend.GetSessionsCount;

            Thread.Sleep(RetryInterval + RetryInterval + RetryInterval + RetryInterval);
            Flush();

            Assert.Equal(reads, backend.GetSessionsCount);
            Assert.Equal(1, backend.AttachCount);
        }

        [Fact]
        public void WhileNoDevice_RetriesKeepGoing_ButAreNotBusy()
        {
            backend.AttachException = new InvalidOperationException("no device");
            try { service.Start().Wait(Timeout); } catch (AggregateException) { }

            Thread.Sleep(RetryInterval.Milliseconds * 10);

            // Roughly one attempt per interval, not a tight loop.
            Assert.InRange(backend.AttachCount, 3, 20);
        }

        // ---- master volume ----

        [Fact]
        public void VolumeChange_SyncsEnabledAppsWithCurrentMaster()
        {
            var chrome = new FakeSession("chrome");
            var spotify = new FakeSession("spotify", volume: 0.9f);
            store.Values["spotify"] = "False";
            backend.SetSessions(chrome, spotify);
            StartAttached();

            backend.SetMaster(0.55f, true);
            backend.RaiseMasterVolumeChanged();
            Flush();

            Assert.Equal(0.55f, chrome.Volume);
            Assert.True(chrome.Muted);
            Assert.Equal(0.9f, spotify.Volume);
        }

        [Fact]
        public void BurstOfVolumeChanges_EndsOnTheLatestValue()
        {
            var chrome = new FakeSession("chrome");
            backend.SetSessions(chrome);
            StartAttached();

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
            StartAttached();
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
        public void UnchangedVolumes_AreNotWrittenAgain()
        {
            var chrome = new FakeSession("chrome");
            backend.SetSessions(chrome);
            backend.SetMaster(0.5f, false);
            StartAttached();
            int writes = chrome.Writes;

            for (int i = 0; i < 10; i++)
            {
                backend.RaiseMasterVolumeChanged();
                Flush();
            }

            Assert.Equal(writes, chrome.Writes);
        }

        // ---- sessions ----

        [Fact]
        public void NewSession_IsListedAndSyncedRightAway()
        {
            backend.SetMaster(0.2f, false);
            StartAttached();

            var game = new FakeSession("game");
            backend.SetSessions(game);
            backend.RaiseSessionsChanged();
            Flush();

            Assert.Equal(new[] { "game" }, apps.GetApps());
            Assert.Equal(0.2f, game.Volume);
        }

        [Fact]
        public void ClosedApp_DisappearsOnSessionChange()
        {
            backend.SetSessions(new FakeSession("chrome"), new FakeSession("spotify"));
            StartAttached();

            backend.SetSessions(new FakeSession("spotify"));
            backend.RaiseSessionsChanged();
            Flush();

            Assert.Equal(new[] { "spotify" }, apps.GetApps());
        }

        [Fact]
        public void AppChangingItsOwnVolume_IsCorrected()
        {
            var chrome = new FakeSession("chrome");
            backend.SetSessions(chrome);
            backend.SetMaster(0.4f, false);
            StartAttached();

            chrome.ChangeOwnVolume(1f);
            backend.RaiseSessionsChanged();
            Flush();

            Assert.Equal(0.4f, chrome.Volume);
        }

        [Fact]
        public void SessionsAreDisposedAfterEachPass()
        {
            var chrome = new FakeSession("chrome");
            backend.SetSessions(chrome);
            StartAttached();
            service.RefreshAsync().Wait(Timeout);

            Assert.Equal(2, chrome.DisposeCount);
        }

        // ---- preferences ----

        [Fact]
        public void EnablingAnApp_SyncsItImmediately()
        {
            store.Values["chrome"] = "False";
            var chrome = new FakeSession("chrome", volume: 1f);
            backend.SetSessions(chrome);
            backend.SetMaster(0.3f, false);
            StartAttached();
            Assert.Equal(1f, chrome.Volume);

            preferences.SetEnabled("chrome", true);
            Flush();

            Assert.Equal(0.3f, chrome.Volume);
        }

        [Fact]
        public void DisablingAnApp_LeavesItsVolumeAlone()
        {
            var chrome = new FakeSession("chrome");
            backend.SetSessions(chrome);
            backend.SetMaster(0.3f, false);
            StartAttached();

            preferences.SetEnabled("chrome", false);
            backend.SetMaster(0.8f, false);
            backend.RaiseMasterVolumeChanged();
            Flush();

            Assert.Equal(0.3f, chrome.Volume);
        }

        // ---- device ----

        [Fact]
        public void ReattachRequired_ReattachesAndSyncs()
        {
            StartAttached();
            var headset = new FakeSession("discord");
            backend.SetSessions(headset);
            backend.SetMaster(0.8f, false);

            backend.RaiseReattachRequired();
            Flush();

            Assert.Equal(2, backend.AttachCount);
            Assert.Equal(new[] { "discord" }, apps.GetApps());
            Assert.Equal(0.8f, headset.Volume);
        }

        [Fact]
        public void DeviceRemoved_ClearsApps_AndRecoversWhenADeviceReturns()
        {
            backend.SetSessions(new FakeSession("chrome"));
            StartAttached();

            backend.AttachException = new InvalidOperationException("no device");
            backend.RaiseReattachRequired();
            Flush();
            Assert.Empty(apps.GetApps());

            backend.AttachException = null;
            WaitUntil(() => apps.GetApps().Contains("chrome"), "the retry should re-attach without any event");
        }

        [Fact]
        public void AudioServiceRestart_IsDetectedAndRecovered()
        {
            // All COM objects become stale: reading fails until the backend is attached again.
            var chrome = new FakeSession("chrome");
            backend.SetSessions(chrome);
            StartAttached();
            int attaches = backend.AttachCount;

            backend.SessionsException = new InvalidOperationException("RPC server unavailable");
            backend.RaiseMasterVolumeChanged();
            Flush();
            backend.SessionsException = null;

            WaitUntil(() => backend.AttachCount > attaches, "a failed read should trigger a re-attach");
            backend.SetMaster(0.25f, false);
            backend.RaiseMasterVolumeChanged();
            Flush();
            Assert.Equal(0.25f, chrome.Volume);
        }

        [Fact]
        public void FailedRead_KeepsTheLastKnownAppList()
        {
            backend.SetSessions(new FakeSession("chrome"));
            StartAttached();

            backend.SessionsException = new InvalidOperationException("transient");
            backend.RaiseSessionsChanged();
            Flush();

            Assert.Equal(new[] { "chrome" }, apps.GetApps());
        }

        // ---- menu ----

        [Fact]
        public void RefreshAsync_UpdatesAppsAndVolumes()
        {
            StartAttached();
            var chrome = new FakeSession("chrome");
            backend.SetSessions(chrome);
            backend.SetMaster(0.7f, false);

            Assert.True(service.RefreshAsync().Wait(Timeout));

            Assert.Equal(new[] { "chrome" }, apps.GetApps());
            Assert.Equal(0.7f, chrome.Volume);
        }

        [Fact]
        public void RefreshAsync_BeforeStart_AttachesOnDemand()
        {
            backend.SetSessions(new FakeSession("chrome"));

            Assert.True(service.RefreshAsync().Wait(Timeout));

            Assert.Equal(1, backend.AttachCount);
            Assert.Equal(new[] { "chrome" }, apps.GetApps());
        }

        // ---- threading and lifetime ----

        [Fact]
        public void AllBackendCallsHappenOnOneThread()
        {
            backend.SetSessions(new FakeSession("chrome"));
            StartAttached();
            backend.RaiseMasterVolumeChanged();
            backend.RaiseSessionsChanged();
            backend.RaiseReattachRequired();
            preferences.SetEnabled("chrome", false);
            service.RefreshAsync().Wait(Timeout);
            service.Dispose();

            Assert.Single(backend.CallingThreadIds);
            Assert.NotEqual(Thread.CurrentThread.ManagedThreadId, backend.CallingThreadIds.Single());
        }

        [Fact]
        public void ConcurrentEventsFromManyThreads_AreSafe()
        {
            var chrome = new FakeSession("chrome");
            backend.SetSessions(chrome);
            StartAttached();

            Parallel.For(0, 500, i =>
            {
                switch (i % 4)
                {
                    case 0: backend.RaiseMasterVolumeChanged(); break;
                    case 1: backend.RaiseSessionsChanged(); break;
                    case 2: backend.RaiseReattachRequired(); break;
                    default: preferences.SetEnabled("chrome", true); break;
                }
            });
            backend.SetMaster(0.33f, false);
            backend.RaiseMasterVolumeChanged();
            Flush();

            Assert.Equal(0.33f, chrome.Volume);
            Assert.Equal(new[] { "chrome" }, apps.GetApps());
            Assert.Single(backend.CallingThreadIds);
        }

        [Fact]
        public void Dispose_DisposesBackendAndUnsubscribes()
        {
            StartAttached();
            int handlers = 0;
            preferences.Changed += (s, e) => handlers++;

            service.Dispose();

            Assert.True(backend.Disposed);
            Assert.False(backend.HasSubscribers);
            preferences.SetEnabled("x", true);
            Assert.Equal(1, handlers);
        }

        [Fact]
        public void Dispose_StopsTheRetryTimer()
        {
            backend.AttachException = new InvalidOperationException("no device");
            try { service.Start().Wait(Timeout); } catch (AggregateException) { }

            service.Dispose();
            int attempts = backend.AttachCount;
            Thread.Sleep(RetryInterval.Milliseconds * 5);

            Assert.Equal(attempts, backend.AttachCount);
        }

        [Fact]
        public void Dispose_Twice_DoesNotThrow()
        {
            StartAttached();
            service.Dispose();
            service.Dispose();
        }

        [Fact]
        public void EventsAfterDispose_AreIgnored()
        {
            StartAttached();
            int attaches = backend.AttachCount;
            service.Dispose();

            backend.RaiseMasterVolumeChanged();
            backend.RaiseReattachRequired();
            service.RequestSync();

            Assert.Equal(attaches, backend.AttachCount);
        }
    }
}
