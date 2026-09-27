using AutoVolumeControl.Interop;

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
            return reason == AudioSessionDisconnectReason.DeviceRemoval
                || reason == AudioSessionDisconnectReason.ServerShutdown;
        }

        private const int AudclntDeviceInvalidated = unchecked((int)0x88890004);
        private const int AudclntServiceNotRunning = unchecked((int)0x88890010);
        private const int RpcDisconnected = unchecked((int)0x80010108);
        private const int RpcServerUnavailable = unchecked((int)0x800706BA);

        /// <summary>
        /// Errors of a call on a cached audio object that mean the whole binding is stale (device invalidated,
        /// audio service stopped or restarted), as opposed to a problem with a single session.
        /// </summary>
        public static bool IsBindingLost(int hresult)
        {
            return hresult == AudclntDeviceInvalidated
                || hresult == AudclntServiceNotRunning
                || hresult == RpcDisconnected
                || hresult == RpcServerUnavailable;
        }
    }
}
