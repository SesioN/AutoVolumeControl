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

        [Theory]
        [InlineData(1, 3)]
        [InlineData(2, 6)]
        [InlineData(3, 12)]
        [InlineData(4, 15)]
        [InlineData(1000, 15)]
        public void RetryDelay_DoublesUpTo15Seconds(int failures, int expectedSeconds)
        {
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), AutoVolumeService.RetryDelay(TimeSpan.FromSeconds(3), failures));
        }

        [Fact]
        public void WhileNoDevice_RetriesBackOff()
        {
            backend.AttachException = new InvalidOperationException("no device");
            try { service.Start().Wait(Timeout); } catch (AggregateException) { }

            // 50 ms interval: attempts at about 0, 50, 150, 350, 750 ms. Without backoff there would be ~20.
            // (The 15 s cap does not apply here because the interval is far below it.)
            Thread.Sleep(1000);

            Assert.InRange(backend.AttachCount, 2, 8);
        }

        [Fact]
        public void IsHealthy_ReflectsTheLastReconcile()
        {
            backend.AttachException = new InvalidOperationException("no device");
            try { service.Start().Wait(Timeout); } catch (AggregateException) { }
            Assert.False(service.IsHealthy);

            backend.AttachException = null;
            WaitUntil(() => service.IsHealthy, "the retry should succeed");
        }

        [Fact]
        public void Start_Twice_Throws()
        {
            StartAttached();
            // Start() throws synchronously; it does not return a faulted task.
            var error = Record.Exception(() => { service.Start(); });
            Assert.IsType<InvalidOperationException>(error);
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
        public void AppFightingOverItsVolume_IsNotCorrectedEndlessly()
        {
            var game = new FakeSession("game");
            backend.SetSessions(game);
            backend.SetMaster(0.4f, false);
            StartAttached();

            for (int i = 0; i < 50; i++)
            {
                game.ChangeOwnVolume(1f);
                backend.RaiseSessionsChanged();
                Flush();
            }

            // Every correction towards the same 0.4 counts, including the one at start.
            Assert.Equal(CorrectionThrottle.DefaultMaxRepeatedCorrections, game.VolumeWrites);
        }

        [Fact]
        public void MasterChangesAfterAFight_AreStillApplied()
        {
            var game = new FakeSession("game");
            var chrome = new FakeSession("chrome");
            backend.SetSessions(game, chrome);
            backend.SetMaster(0.4f, false);
            StartAttached();
            for (int i = 0; i < 50; i++)
            {
                game.ChangeOwnVolume(1f);
                backend.RaiseSessionsChanged();
                Flush();
            }

            backend.SetMaster(0.2f, false);
            backend.RaiseMasterVolumeChanged();
            Flush();

            Assert.Equal(0.2f, chrome.Volume);
            // The paused app follows the master too: only its own resets are ignored.
            Assert.Equal(0.2f, game.Volume);
        }

        [Fact]
        public void PausedApp_IsCorrectedAgainWhenThePauseEnds()
        {
            using var shortPause = new AutoVolumeService(backend, preferences, apps, RetryInterval,
                new CorrectionThrottle(pause: TimeSpan.FromMilliseconds(300)));
            var game = new FakeSession("game");
            backend.SetSessions(game);
            backend.SetMaster(0.4f, false);
            Assert.True(shortPause.Start().Wait(Timeout));
            for (int i = 0; i < 20; i++)
            {
                game.ChangeOwnVolume(1f);
                backend.RaiseSessionsChanged();
                Assert.True(shortPause.Flush().Wait(Timeout));
            }
            Assert.Equal(1f, game.Volume);

            // The app gave up fighting; no further event arrives, yet it is corrected after the pause.
            WaitUntil(() => Math.Abs(game.Volume - 0.4f) < 0.001f, "the pause end should trigger a correction");
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

            // Right after an attach, a new one is deferred by up to one retry interval.
            WaitUntil(() => backend.AttachCount == 2, "the re-attach should run");
            Flush();
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
            WaitUntil(() => !apps.GetApps().Any(), "the failed re-attach should clear the list");

            backend.AttachException = null;
            WaitUntil(() => apps.GetApps().Contains("chrome"), "the retry should re-attach without any event");
        }

        [Fact]
        public void RepeatedReattachRequests_CannotLoop()
        {
            // E.g. one session keeps reporting "binding lost" although the device works: at most one
            // re-attach per retry interval, not a busy loop.
            backend.SetSessions(new FakeSession("chrome"));
            StartAttached();
            backend.OnGetSessions = () => backend.RaiseReattachRequired();
            backend.RaiseReattachRequired();

            Thread.Sleep(RetryInterval.Milliseconds * 10);
            backend.OnGetSessions = null;
            Flush();

            // 500 ms at one re-attach per 50 ms: about 10. A loop would reach thousands.
            Assert.InRange(backend.AttachCount, 2, 20);
        }

        [Fact]
        public void SpacedReattachRequests_AreHandledImmediately()
        {
            StartAttached();
            Thread.Sleep(RetryInterval + RetryInterval);

            backend.RaiseReattachRequired();
            Flush();

            Assert.Equal(2, backend.AttachCount);
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
        public void RefreshAsync_ReReadsTheSessionList()
        {
            // Opening the menu corrects anything a missed notification left behind.
            StartAttached();
            int invalidations = backend.InvalidateCount;

            Assert.True(service.RefreshAsync().Wait(Timeout));

            Assert.Equal(invalidations + 1, backend.InvalidateCount);
        }

        [Fact]
        public void LastError_IsTheMostRecentFailure()
        {
            backend.AttachException = new InvalidOperationException("first");
            try { service.Start().Wait(Timeout); } catch (AggregateException) { }
            backend.AttachException = new InvalidOperationException("second");

            WaitUntil(() => service.LastError?.Message == "second", "the retry should record its own failure");

            backend.AttachException = null;
            WaitUntil(() => service.LastError == null, "a successful retry should clear the error");
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
        public void Dispose_WhenTheAudioThreadIsStuck_StillReturns()
        {
            // E.g. a COM call that never returns: exiting the app must not hang forever.
            StartAttached();
            using var gate = new ManualResetEventSlim();
            backend.GetMasterVolumeGate = gate;
            backend.GetMasterVolumeEntered.Reset();
            backend.RaiseMasterVolumeChanged();
            Assert.True(backend.GetMasterVolumeEntered.Wait(Timeout));

            var watch = Stopwatch.StartNew();
            service.Dispose();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(12), watch.Elapsed.ToString());
            gate.Set();
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
