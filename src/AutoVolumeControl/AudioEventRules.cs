using CSCore.CoreAudioAPI;

namespace AutoVolumeControl
{
    /// <summary>Decisions about Core Audio notifications, kept free of COM so they can be tested directly.</summary>
    static class AudioEventRules
    {
        /// <summary>Only the device that <see cref="CoreAudioBackend"/> binds to (render, multimedia) matters.</summary>
        public static bool IsWatchedDefaultDevice(DataFlow dataFlow, Role role)
        {
            return dataFlow == DataFlow.Render && role == Role.Multimedia;
        }

        /// <summary>
        /// After these the whole device binding is invalid (device gone, audio service stopped) and must be
        /// re-created; any other reason only affects the one session.
        /// </summary>
        public static bool RequiresReattach(AudioSessionDisconnectReason reason)
        {
            return reason == AudioSessionDisconnectReason.DisconnectReasonDeviceRemoval
                || reason == AudioSessionDisconnectReason.DisconnectReasonServerShutdown;
        }
    }
}
