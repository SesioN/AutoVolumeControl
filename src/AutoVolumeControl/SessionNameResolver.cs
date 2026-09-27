using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
            string path = ExecutableFromSessionIdentifier(sessionIdentifier);
            if (path == null)
                return null;

            int slash = path.LastIndexOf('\\');
            string fileName = slash >= 0 ? path.Substring(slash + 1) : path;
            string name = Path.GetFileNameWithoutExtension(fileName).Trim();
            return name.Length == 0 ? null : name;
        }

        /// <summary>
        /// Turns the executable in the session identifier into a normal path, e.g.
        /// <c>\Device\HarddiskVolume3\Program Files\App\app.exe</c> into <c>C:\Program Files\App\app.exe</c>.
        /// </summary>
        /// <param name="driveDevices">Drive letters and their device names, e.g. ("C:", <c>\Device\HarddiskVolume3</c>).</param>
        /// <returns>null if the identifier names no executable or its device has no drive letter.</returns>
        public static string ExecutablePathFromSessionIdentifier(string sessionIdentifier, IEnumerable<KeyValuePair<string, string>> driveDevices)
        {
            string path = ExecutableFromSessionIdentifier(sessionIdentifier);
            return path == null ? null : DevicePathToDosPath(path, driveDevices);
        }

        /// <summary>Replaces the device prefix of an NT path by its drive letter; drive paths are returned as they are.</summary>
        public static string DevicePathToDosPath(string path, IEnumerable<KeyValuePair<string, string>> driveDevices)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\')
                return path;

            // Network shares: \Device\Mup\server\share\app.exe is \\server\share\app.exe.
            const string Mup = @"\Device\Mup\";
            if (path.StartsWith(Mup, StringComparison.OrdinalIgnoreCase))
                return @"\\" + path.Substring(Mup.Length);

            foreach (var pair in driveDevices ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                var device = pair.Value?.TrimEnd('\\');
                if (string.IsNullOrEmpty(device) || string.IsNullOrEmpty(pair.Key))
                    continue;

                // The device name must end at a separator: HarddiskVolume1 is no prefix of HarddiskVolume10.
                if (path.Length > device.Length
                    && path[device.Length] == '\\'
                    && path.StartsWith(device, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Key + path.Substring(device.Length);
                }
            }
            return null;
        }

        /// <summary>
        /// The file of an icon path as set with IAudioSessionControl::SetIconPath, e.g.
        /// <c>@%SystemRoot%\System32\app.dll,-101</c> gives <c>C:\Windows\System32\app.dll</c>.
        /// </summary>
        public static string FileFromIconPath(string iconPath)
        {
            if (string.IsNullOrWhiteSpace(iconPath))
                return null;

            var path = iconPath.Trim().TrimStart('@');
            int comma = path.LastIndexOf(',');
            if (comma > 0 && int.TryParse(path.Substring(comma + 1).Trim(), out _))
                path = path.Substring(0, comma);

            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            return path.Length == 0 ? null : path;
        }

        /// <summary>
        /// The executable of an icon path, or null if it names another file. An icon path may name a DLL with an icon
        /// resource index; the shell would show the icon of the DLL file type for it, which is worse than the generic
        /// application icon.
        /// </summary>
        public static string ExecutableFromIconPath(string iconPath)
        {
            var file = FileFromIconPath(iconPath);
            return file != null && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file : null;
        }

        /// <summary>The raw executable path of the identifier (possibly an NT device path), or null.</summary>
        private static string ExecutableFromSessionIdentifier(string sessionIdentifier)
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

            return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null;
        }
    }
}
