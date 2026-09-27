using CSCore.CoreAudioAPI;
using Xunit;

namespace AutoVolumeControl.Tests
{
    public class SessionFilterTests
    {
        [Theory]
        [InlineData(AudioSessionState.AudioSessionStateActive, false, true)]
        [InlineData(AudioSessionState.AudioSessionStateInactive, false, true)]
        [InlineData(AudioSessionState.AudioSessionStateExpired, false, false)]
        [InlineData(AudioSessionState.AudioSessionStateActive, true, false)]
        [InlineData(AudioSessionState.AudioSessionStateInactive, true, false)]
        public void OnlySessionsOfRunningAppsAreKept(AudioSessionState state, bool processExited, bool expected)
        {
            Assert.Equal(expected, SessionFilter.BelongsToRunningApp(state, processExited));
        }
    }
}
