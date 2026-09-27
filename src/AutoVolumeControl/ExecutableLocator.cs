using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AutoVolumeControl
{
    /// <summary>
    /// Finds the executable of an audio session's app, so the menu can show its icon. Tries, in this order:
    /// the running process (works for most processes, including many elevated ones), the path inside the session
    /// identifier, and the icon path the app may have set on its session.
    /// </summary>
    static class ExecutableLocator
    {
        private const int ProcessQueryLimitedInformation = 0x1000;

        /// <param name="iconPath">Reads the session's icon path; only called when nothing else worked.</param>
        /// <returns>The path of an existing file, or null.</returns>
        public static string Find(int processId, string sessionIdentifier, Func<string> iconPath)
        {
            return ExistingFile(FromProcess(processId))
                ?? ExistingFile(SessionNameResolver.ExecutablePathFromSessionIdentifier(sessionIdentifier, DriveDevices()))
                ?? ExistingFile(SessionNameResolver.FileFromIconPath(iconPath?.Invoke()));
        }

        /// <summary>QueryFullProcessImageName needs less access than Process.MainModule, so it also works for many elevated apps.</summary>
        private static string FromProcess(int processId)
        {
            if (processId == 0)
                return null;

            var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero)
                return null;

            try
            {
                int size = 1024;
                var buffer = new StringBuilder(size);
                return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        /// <summary>Pairs like ("C:", @"\Device\HarddiskVolume3") for all drive letters in use.</summary>
        private static IEnumerable<KeyValuePair<string, string>> DriveDevices()
        {
            string[] drives;
            try
            {
                drives = Environment.GetLogicalDrives();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                yield break;
            }

            var buffer = new StringBuilder(1024);
            foreach (var root in drives)
            {
                var drive = root.TrimEnd('\\');
                buffer.Clear();
                if (QueryDosDevice(drive, buffer, buffer.Capacity) == 0)
                    continue;

                // The result is a list of strings separated by \0; the first entry is the current mapping.
                var device = buffer.ToString().Split('\0')[0];
                if (device.Length > 0)
                    yield return new KeyValuePair<string, string>(drive, device);
            }
        }

        private static string ExistingFile(string path) => !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryDosDeviceW")]
        private static extern int QueryDosDevice(string deviceName, StringBuilder targetPath, int max);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
