using System;
using System.Linq;
using Xunit;

namespace AutoVolumeControl.Tests
{
    public class CorrectionThrottleTests
    {
        private const int Max = CorrectionThrottle.DefaultMaxRepeatedCorrections;
        private DateTime now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        private readonly CorrectionThrottle throttle;

        public CorrectionThrottleTests()
        {
            throttle = new CorrectionThrottle(() => now);
        }

        private int AllowedOf(int attempts, string session = "s1", float target = 0.5f)
        {
            int allowed = 0;
            for (int i = 0; i < attempts; i++)
                if (throttle.Allow(session, target, false))
                    allowed++;
            return allowed;
        }

        private void PauseSession(string session = "s1", float target = 0.5f) => AllowedOf(Max + 1, session, target);

        [Fact]
        public void RepeatedCorrectionsToTheSameTarget_ArePausedAfterTheLimit()
        {
            Assert.Equal(Max, AllowedOf(50));
        }

        [Fact]
        public void ChangingTarget_NeverThrottles()
        {
            // The user moving the master slider: every correction has a new target.
            for (int i = 0; i < 200; i++)
                Assert.True(throttle.Allow("s1", i / 200f, false));
        }

        [Fact]
        public void ToggledMute_CountsAsNewTarget()
        {
            for (int i = 0; i < 50; i++)
                Assert.True(throttle.Allow("s1", 0.5f, i % 2 == 0));
        }

        [Fact]
        public void CorrectionsSpreadOverTime_AreNotThrottled()
        {
            for (int i = 0; i < 50; i++)
            {
                Assert.True(throttle.Allow("s1", 0.5f, false));
                now += CorrectionThrottle.DefaultWindow + TimeSpan.FromMilliseconds(1);
            }
        }

        [Fact]
        public void Pause_EndsAfterItsDuration()
        {
            PauseSession();
            Assert.False(throttle.Allow("s1", 0.5f, false));

            now += CorrectionThrottle.DefaultPause;
            Assert.True(throttle.Allow("s1", 0.5f, false));
        }

        [Fact]
        public void NewMasterValue_EndsAPauseImmediately()
        {
            PauseSession(target: 0.5f);

            Assert.True(throttle.Allow("s1", 0.3f, false));
        }

        [Fact]
        public void Sessions_AreThrottledIndependently()
        {
            PauseSession("s1");

            Assert.True(throttle.Allow("s2", 0.5f, false));
            Assert.False(throttle.Allow("s1", 0.5f, false));
        }

        [Fact]
        public void TimeUntilNextResume_IsNullWithoutPauses()
        {
            AllowedOf(3);
            Assert.Null(throttle.TimeUntilNextResume());
        }

        [Fact]
        public void TimeUntilNextResume_ReportsTheEarliestPauseEnd()
        {
            PauseSession("s1");
            now += TimeSpan.FromSeconds(20);
            PauseSession("s2");

            Assert.Equal(CorrectionThrottle.DefaultPause - TimeSpan.FromSeconds(20), throttle.TimeUntilNextResume());
        }

        [Fact]
        public void Retain_ForgetsEndedSessions()
        {
            AllowedOf(1, "s1");
            AllowedOf(1, "s2");

            throttle.Retain(new[] { "s2" });

            Assert.Equal(1, throttle.TrackedSessionCount);
        }

        [Fact]
        public void Synchronizer_SkipsPausedSessions_ButSyncsOthers()
        {
            var preferences = new AppPreferences(new InMemorySettingsStore());
            var fighting = new FakeSession("game", id: "s1");
            var other = new FakeSession("game", id: "s2");
            PauseSession("s1", 0.5f);

            VolumeSynchronizer.Sync(0.5f, false, new[] { fighting, other }, preferences, throttle);

            Assert.Equal(1f, fighting.Volume);
            Assert.Equal(0.5f, other.Volume);
        }

        [Fact]
        public void ManySessionsOfOneApp_AreAllCorrectedAtOnce()
        {
            // E.g. at startup or when enabling an app with many sessions: not a fight.
            var preferences = new AppPreferences(new InMemorySettingsStore());
            var sessions = Enumerable.Range(0, 3 * Max).Select(i => new FakeSession("chrome")).ToList();

            int changed = VolumeSynchronizer.Sync(0.5f, false, sessions, preferences, throttle);

            Assert.Equal(sessions.Count, changed);
            Assert.All(sessions, s => Assert.Equal(0.5f, s.Volume));
        }

        [Fact]
        public void MatchingValues_DoNotCount()
        {
            var preferences = new AppPreferences(new InMemorySettingsStore());
            var game = new FakeSession("game", volume: 0.5f, id: "s1");

            for (int i = 0; i < 100; i++)
                VolumeSynchronizer.Sync(0.5f, false, new[] { game }, preferences, throttle);

            Assert.True(throttle.Allow("s1", 0.5f, false));
        }
    }
}
