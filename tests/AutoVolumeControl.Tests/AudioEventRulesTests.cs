using AutoVolumeControl.Interop;
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
        [InlineData(AudioSessionDisconnectReason.DeviceRemoval, true)]
        [InlineData(AudioSessionDisconnectReason.ServerShutdown, true)]
        [InlineData(AudioSessionDisconnectReason.FormatChanged, false)]
        [InlineData(AudioSessionDisconnectReason.SessionLogoff, false)]
        [InlineData(AudioSessionDisconnectReason.SessionDisconnected, false)]
        [InlineData(AudioSessionDisconnectReason.ExclusiveModeOverride, false)]
        public void DeviceRemovalAndServiceShutdown_RequireReattach(AudioSessionDisconnectReason reason, bool expected)
        {
            Assert.Equal(expected, AudioEventRules.RequiresReattach(reason));
        }

        [Theory]
        [InlineData(unchecked((int)0x88890004), true)]  // AUDCLNT_E_DEVICE_INVALIDATED
        [InlineData(unchecked((int)0x88890010), true)]  // AUDCLNT_E_SERVICE_NOT_RUNNING
        [InlineData(unchecked((int)0x80010108), true)]  // RPC_E_DISCONNECTED
        [InlineData(unchecked((int)0x800706BA), true)]  // RPC_S_SERVER_UNAVAILABLE
        [InlineData(unchecked((int)0x80070005), false)] // E_ACCESSDENIED
        [InlineData(unchecked((int)0x80004005), false)] // E_FAIL
        public void OnlyBindingErrors_RequireReattach(int hresult, bool expected)
        {
            Assert.Equal(expected, AudioEventRules.IsBindingLost(hresult));
        }
    }
}
