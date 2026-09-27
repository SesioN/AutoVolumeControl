using System;
using Xunit;

namespace AutoVolumeControl.Tests
{
    public class VolumeSynchronizerTests
    {
        private readonly InMemorySettingsStore store = new InMemorySettingsStore();
        private readonly AppPreferences preferences;

        public VolumeSynchronizerTests()
        {
            preferences = new AppPreferences(store);
        }

        [Fact]
        public void AppliesVolumeAndMuteToEnabledSessions()
        {
            var chrome = new FakeSession("chrome");

            int changed = VolumeSynchronizer.Sync(0.4f, true, new[] { chrome }, preferences);

            Assert.Equal(1, changed);
            Assert.Equal(0.4f, chrome.Volume);
            Assert.True(chrome.Muted);
        }

        [Fact]
        public void SkipsDisabledSessions()
        {
            store.Values["chrome"] = "False";
            var chrome = new FakeSession("chrome", volume: 0.9f);

            Assert.Equal(0, VolumeSynchronizer.Sync(0.4f, false, new[] { chrome }, preferences));
            Assert.Equal(0.9f, chrome.Volume);
            Assert.Equal(0, chrome.Writes);
        }

        [Fact]
        public void SkipsSystemSounds()
        {
            var system = new FakeSession("System", isSystemSound: true);

            VolumeSynchronizer.Sync(0.4f, false, new[] { system }, preferences);

            Assert.Equal(0, system.Writes);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void SkipsSessionsWithoutName(string name)
        {
            var session = new FakeSession(name);

            VolumeSynchronizer.Sync(0.4f, false, new[] { session }, preferences);

            Assert.Equal(0, session.Writes);
        }

        [Fact]
        public void MatchingValues_AreNotWrittenAgain()
        {
            var chrome = new FakeSession("chrome", volume: 0.5f, muted: false);

            Assert.Equal(0, VolumeSynchronizer.Sync(0.5f, false, new[] { chrome }, preferences));
            Assert.Equal(0, chrome.Writes);
        }

        [Fact]
        public void DifferencesWithinTolerance_AreNotWritten()
        {
            var chrome = new FakeSession("chrome", volume: 0.5f + VolumeSynchronizer.VolumeTolerance / 2);

            VolumeSynchronizer.Sync(0.5f, false, new[] { chrome }, preferences);

            Assert.Equal(0, chrome.VolumeWrites);
        }

        [Fact]
        public void OnlyTheDifferingValueIsWritten()
        {
            var chrome = new FakeSession("chrome", volume: 0.5f, muted: false);

            VolumeSynchronizer.Sync(0.5f, true, new[] { chrome }, preferences);

            Assert.Equal(0, chrome.VolumeWrites);
            Assert.Equal(1, chrome.MuteWrites);
        }

        [Fact]
        public void RepeatedSync_WritesOnlyOnce()
        {
            var chrome = new FakeSession("chrome");

            VolumeSynchronizer.Sync(0.3f, false, new[] { chrome }, preferences);
            VolumeSynchronizer.Sync(0.3f, false, new[] { chrome }, preferences);
            VolumeSynchronizer.Sync(0.3f, false, new[] { chrome }, preferences);

            Assert.Equal(1, chrome.Writes);
        }

        [Fact]
        public void FailingSession_DoesNotStopTheOthers()
        {
            var broken = new FakeSession("broken") { ThrowOnWrite = true };
            var spotify = new FakeSession("spotify");

            int changed = VolumeSynchronizer.Sync(0.7f, false, new[] { broken, spotify }, preferences);

            Assert.Equal(1, changed);
            Assert.Equal(0.7f, spotify.Volume);
        }

        [Fact]
        public void InvalidStoredFlag_DoesNotStopTheSync()
        {
            store.Values["chrome"] = "garbage";
            var chrome = new FakeSession("chrome");
            var spotify = new FakeSession("spotify");

            VolumeSynchronizer.Sync(0.7f, false, new[] { chrome, spotify }, preferences);

            Assert.Equal(0.7f, chrome.Volume);
            Assert.Equal(0.7f, spotify.Volume);
        }

        [Fact]
        public void UnmutesWhenMasterIsUnmuted()
        {
            var chrome = new FakeSession("chrome");
            VolumeSynchronizer.Sync(0.5f, true, new[] { chrome }, preferences);
            VolumeSynchronizer.Sync(0.5f, false, new[] { chrome }, preferences);

            Assert.False(chrome.Muted);
        }

        // ---- ratio ----

        [Fact]
        public void Ratio_IsAppliedToTheMasterVolume()
        {
            store.Ratios["spotify"] = "50";
            var spotify = new FakeSession("spotify");
            var chrome = new FakeSession("chrome");

            VolumeSynchronizer.Sync(0.8f, false, new[] { spotify, chrome }, preferences);

            Assert.Equal(0.4f, spotify.Volume, 3);
            Assert.Equal(0.8f, chrome.Volume, 3);
        }

        [Fact]
        public void ZeroRatio_SilencesTheAppWithoutMutingIt()
        {
            store.Ratios["spotify"] = "0";
            var spotify = new FakeSession("spotify");

            VolumeSynchronizer.Sync(0.8f, false, new[] { spotify }, preferences);

            Assert.Equal(0f, spotify.Volume);
            Assert.False(spotify.Muted);
        }

        [Theory]
        [InlineData("150", 0.6f)]
        [InlineData("-20", 0f)]
        [InlineData("garbage", 0.6f)]
        public void InvalidStoredRatio_IsClampedOrIgnored(string stored, float expected)
        {
            store.Ratios["spotify"] = stored;
            var spotify = new FakeSession("spotify");

            VolumeSynchronizer.Sync(0.6f, false, new[] { spotify }, preferences);

            Assert.Equal(expected, spotify.Volume, 3);
        }

        [Theory]
        [InlineData(0.5f, 0.5f, 0.25f)]
        [InlineData(1f, 1f, 1f)]
        [InlineData(0.5f, 2f, 0.5f)]
        [InlineData(0.5f, -1f, 0f)]
        [InlineData(0.5f, float.NaN, 0.5f)]
        public void TargetVolume_IsMasterTimesClampedRatio(float master, float ratio, float expected)
        {
            Assert.Equal(expected, VolumeSynchronizer.TargetVolume(master, ratio), 3);
        }

        [Fact]
        public void MatchingRatioVolume_IsNotWrittenAgain()
        {
            store.Ratios["spotify"] = "50";
            var spotify = new FakeSession("spotify", volume: 0.4f);

            Assert.Equal(0, VolumeSynchronizer.Sync(0.8f, false, new[] { spotify }, preferences));
            Assert.Equal(0, spotify.Writes);
        }

        [Fact]
        public void ChangedRatio_IsWrittenOnTheNextSync()
        {
            var spotify = new FakeSession("spotify");
            VolumeSynchronizer.Sync(0.8f, false, new[] { spotify }, preferences);

            preferences.SetRatioPercent("spotify", 25);
            VolumeSynchronizer.Sync(0.8f, false, new[] { spotify }, preferences);

            Assert.Equal(0.2f, spotify.Volume, 3);
            Assert.Equal(2, spotify.VolumeWrites);
        }

        [Fact]
        public void ChangedRatio_EndsAThrottlePause()
        {
            var now = new DateTime(2026, 1, 1);
            var throttle = new CorrectionThrottle(() => now, maxRepeatedCorrections: 2);
            var spotify = new FakeSession("spotify", id: "s1");

            // The app keeps resetting itself until its corrections are paused.
            for (int i = 0; i < 3; i++)
            {
                spotify.ChangeOwnVolume(1f);
                VolumeSynchronizer.Sync(0.5f, false, new[] { spotify }, preferences, throttle);
            }
            spotify.ChangeOwnVolume(1f);
            Assert.Equal(0, VolumeSynchronizer.Sync(0.5f, false, new[] { spotify }, preferences, throttle));

            preferences.SetRatioPercent("spotify", 50);

            Assert.Equal(1, VolumeSynchronizer.Sync(0.5f, false, new[] { spotify }, preferences, throttle));
            Assert.Equal(0.25f, spotify.Volume, 3);
        }
    }
}
