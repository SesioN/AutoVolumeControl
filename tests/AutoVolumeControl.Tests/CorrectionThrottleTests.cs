using System;
using Xunit;

namespace AutoVolumeControl.Tests
{
    public class CorrectionThrottleTests
    {
        private DateTime now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        private readonly CorrectionThrottle throttle;

        public CorrectionThrottleTests()
        {
            throttle = new CorrectionThrottle(() => now);
        }

        private int AllowedOf(int attempts, string app = "game", float target = 0.5f)
        {
            int allowed = 0;
            for (int i = 0; i < attempts; i++)
                if (throttle.Allow(app, target, false))
                    allowed++;
            return allowed;
        }

        [Fact]
        public void RepeatedCorrectionsToTheSameTarget_ArePausedAfterTheLimit()
        {
            Assert.Equal(CorrectionThrottle.MaxRepeatedCorrections, AllowedOf(50));
        }

        [Fact]
        public void ChangingTarget_NeverThrottles()
        {
            // The user moving the master slider: every correction has a new target.
            for (int i = 0; i < 200; i++)
                Assert.True(throttle.Allow("game", i / 200f, false));
        }

        [Fact]
        public void ToggledMute_CountsAsNewTarget()
        {
            for (int i = 0; i < 50; i++)
                Assert.True(throttle.Allow("game", 0.5f, i % 2 == 0));
        }

        [Fact]
        public void CorrectionsSpreadOverTime_AreNotThrottled()
        {
            for (int i = 0; i < 50; i++)
            {
                Assert.True(throttle.Allow("game", 0.5f, false));
                now += CorrectionThrottle.Window;
                now += TimeSpan.FromMilliseconds(1);
            }
        }

        [Fact]
        public void Pause_EndsAfterItsDuration()
        {
            AllowedOf(CorrectionThrottle.MaxRepeatedCorrections + 1);
            Assert.False(throttle.Allow("game", 0.5f, false));

            now += CorrectionThrottle.Pause;
            Assert.True(throttle.Allow("game", 0.5f, false));
        }

        [Fact]
        public void Apps_AreThrottledIndependently()
        {
            AllowedOf(CorrectionThrottle.MaxRepeatedCorrections + 1, "game");

            Assert.True(throttle.Allow("chrome", 0.5f, false));
            Assert.False(throttle.Allow("game", 0.5f, false));
        }

        [Fact]
        public void SynchronizerSkipsPausedApps_ButSyncsOthers()
        {
            var preferences = new AppPreferences(new InMemorySettingsStore());
            var game = new FakeSession("game");
            var chrome = new FakeSession("chrome");
            AllowedOf(CorrectionThrottle.MaxRepeatedCorrections + 1, "game", 0.5f);

            VolumeSynchronizer.Sync(0.5f, false, new[] { game, chrome }, preferences, throttle);

            Assert.Equal(1f, game.Volume);
            Assert.Equal(0.5f, chrome.Volume);
        }

        [Fact]
        public void MatchingValues_DoNotCount()
        {
            var preferences = new AppPreferences(new InMemorySettingsStore());
            var game = new FakeSession("game", volume: 0.5f);

            for (int i = 0; i < 100; i++)
                VolumeSynchronizer.Sync(0.5f, false, new[] { game }, preferences, throttle);

            Assert.True(throttle.Allow("game", 0.5f, false));
        }
    }
}
