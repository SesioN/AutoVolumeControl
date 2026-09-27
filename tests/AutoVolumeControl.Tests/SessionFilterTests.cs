using AutoVolumeControl.Interop;
using Xunit;

namespace AutoVolumeControl.Tests
{
    public class SessionFilterTests
    {
        private const string SpotifyIdentifier =
            @"{0.0.0.00000000}.{guid}|\Device\HarddiskVolume3\Users\me\AppData\Roaming\Spotify\Spotify.exe%b{00000000-0000-0000-0000-000000000000}";

        [Theory]
        [InlineData(AudioSessionState.Active, false, true)]
        [InlineData(AudioSessionState.Inactive, false, true)]
        [InlineData(AudioSessionState.Expired, false, false)]
        [InlineData(AudioSessionState.Active, true, false)]
        [InlineData(AudioSessionState.Inactive, true, false)]
        public void OnlySessionsOfRunningAppsAreKept(AudioSessionState state, bool processExited, bool expected)
        {
            Assert.Equal(expected, SessionFilter.BelongsToRunningApp(state, processExited));
        }

        [Theory]
        [InlineData("Spotify", true)]
        [InlineData("spotify", true)]
        [InlineData("svchost", false)]
        public void IsSameProcess_ComparesWithTheExecutableOfTheSession(string processName, bool expected)
        {
            Assert.Equal(expected, SessionFilter.IsSameProcess(processName, SpotifyIdentifier));
        }

        [Theory]
        [InlineData(null, SpotifyIdentifier)]
        [InlineData("", SpotifyIdentifier)]
        [InlineData("chrome", null)]
        [InlineData("chrome", @"{0.0.0.00000000}.{guid}|#%b{guid}")]
        public void IsSameProcess_UnknownNames_AreNotTreatedAsReused(string processName, string identifier)
        {
            Assert.True(SessionFilter.IsSameProcess(processName, identifier));
        }
    }
}
