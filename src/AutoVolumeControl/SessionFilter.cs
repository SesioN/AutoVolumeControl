using CSCore.CoreAudioAPI;

namespace AutoVolumeControl
{
    static class SessionFilter
    {
        /// <summary>Windows keeps sessions of closed apps around for a while; they are neither listed nor synced.</summary>
        public static bool BelongsToRunningApp(AudioSessionState state, bool processExited)
        {
            return state != AudioSessionState.AudioSessionStateExpired && !processExited;
        }
    }
}
