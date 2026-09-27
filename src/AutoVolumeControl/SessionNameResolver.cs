using System;
using System.IO;

namespace AutoVolumeControl
{
    /// <summary>
    /// Resolves the name under which an audio session is shown in the menu and stored in the settings.
    /// Used for listing and for syncing so both always agree on the same key.
    /// </summary>
    internal static class SessionNameResolver
    {
        public static string Resolve(string processName, string displayName, string sessionIdentifier)
        {
            if (!string.IsNullOrWhiteSpace(processName))
                return processName.Trim();

            // Sessions of processes that already exited have no process, but their identifier still
            // contains the executable path. Using it keeps the name identical to the process name.
            var executableName = ExecutableNameFromSessionIdentifier(sessionIdentifier);
            if (!string.IsNullOrWhiteSpace(executableName))
                return executableName;

            if (!string.IsNullOrWhiteSpace(displayName))
                return displayName.Trim();

            return string.IsNullOrWhiteSpace(sessionIdentifier) ? null : sessionIdentifier.Trim();
        }

        /// <summary>
        /// Extracts "app" from an identifier like
        /// <c>{0.0.0.00000000}.{guid}|\Device\HarddiskVolume3\Program Files\App\app.exe%b{00000000-...}</c>.
        /// </summary>
        public static string ExecutableNameFromSessionIdentifier(string sessionIdentifier)
        {
            if (string.IsNullOrEmpty(sessionIdentifier))
                return null;

            int start = sessionIdentifier.IndexOf('|');
            if (start < 0)
                return null;

            string path = sessionIdentifier.Substring(start + 1);
            int end = path.IndexOf("%b", StringComparison.OrdinalIgnoreCase);
            if (end >= 0)
                path = path.Substring(0, end);

            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return null;

            int slash = path.LastIndexOf('\\');
            string fileName = slash >= 0 ? path.Substring(slash + 1) : path;
            string name = Path.GetFileNameWithoutExtension(fileName).Trim();
            return name.Length == 0 ? null : name;
        }
    }
}
