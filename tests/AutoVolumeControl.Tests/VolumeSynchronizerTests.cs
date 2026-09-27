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

            int applied = VolumeSynchronizer.Sync(0.4f, true, new[] { chrome }, preferences);

            Assert.Equal(1, applied);
            Assert.Equal(0.4f, chrome.Volume);
            Assert.True(chrome.Muted);
        }

        [Fact]
        public void SkipsDisabledSessions()
        {
            store.Values["chrome"] = "False";
            var chrome = new FakeSession("chrome");

            Assert.Equal(0, VolumeSynchronizer.Sync(0.4f, false, new[] { chrome }, preferences));
            Assert.Null(chrome.Volume);
        }

        [Fact]
        public void SkipsSystemSounds()
        {
            var system = new FakeSession("System", isSystemSound: true);

            VolumeSynchronizer.Sync(0.4f, false, new[] { system }, preferences);

            Assert.Null(system.Volume);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void SkipsSessionsWithoutName(string name)
        {
            var session = new FakeSession(name);

            VolumeSynchronizer.Sync(0.4f, false, new[] { session }, preferences);

            Assert.Null(session.Volume);
        }

        [Fact]
        public void FailingSession_DoesNotStopTheOthers()
        {
            var broken = new FakeSession("broken") { ThrowOnApply = true };
            var spotify = new FakeSession("spotify");

            int applied = VolumeSynchronizer.Sync(0.7f, false, new[] { broken, spotify }, preferences);

            Assert.Equal(1, applied);
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
    }
}
