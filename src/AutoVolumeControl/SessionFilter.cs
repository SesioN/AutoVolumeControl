using System;
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

        /// <summary>
        /// Guards against reused process IDs: a session of an exited app can outlive its process, and Windows
        /// may give the ID to a new, unrelated process. The session identifier names the original executable.
        /// </summary>
        /// <returns>False only if both names are known and differ.</returns>
        public static bool IsSameProcess(string processName, string sessionIdentifier)
        {
            var executable = SessionNameResolver.ExecutableNameFromSessionIdentifier(sessionIdentifier);
            if (string.IsNullOrEmpty(processName) || executable == null)
                return true;

            return string.Equals(processName, executable, StringComparison.OrdinalIgnoreCase);
        }
    }
}
