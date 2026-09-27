using CSCore.CoreAudioAPI;
using Xunit;

namespace AutoVolumeControl.Tests
{
    public class AudioEventRulesTests
    {
        [Theory]
        [InlineData(DataFlow.Render, Role.Multimedia, true)]
        [InlineData(DataFlow.Render, Role.Console, false)]
        [InlineData(DataFlow.Render, Role.Communications, false)]
        [InlineData(DataFlow.Capture, Role.Multimedia, false)]
        public void OnlyTheDefaultPlaybackDeviceMatters(DataFlow flow, Role role, bool expected)
        {
            Assert.Equal(expected, AudioEventRules.IsWatchedDefaultDevice(flow, role));
        }

        [Theory]
        [InlineData(AudioSessionDisconnectReason.DisconnectReasonDeviceRemoval, true)]
        [InlineData(AudioSessionDisconnectReason.DisconnectReasonServerShutdown, true)]
        [InlineData(AudioSessionDisconnectReason.DisconnectReasonFormatChanged, false)]
        [InlineData(AudioSessionDisconnectReason.DisconnectReasonSessionLogoff, false)]
        [InlineData(AudioSessionDisconnectReason.DisconnectReasonSessionDisconnected, false)]
        [InlineData(AudioSessionDisconnectReason.DisconnectReasonExclusiveModeOverride, false)]
        public void DeviceRemovalAndServiceShutdown_RequireReattach(AudioSessionDisconnectReason reason, bool expected)
        {
            Assert.Equal(expected, AudioEventRules.RequiresReattach(reason));
        }
    }
}
