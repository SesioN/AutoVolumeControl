using System;
using Microsoft.Win32;

namespace AutoVolumeControl
{
    /// <summary>"Start with Windows" entry in HKCU\...\CurrentVersion\Run.</summary>
    sealed class AutoStart
    {
        public const string DefaultRunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        private readonly string valueName;
        private readonly string executablePath;
        private readonly string runKeyPath;

        public AutoStart(string valueName, string executablePath, string runKeyPath = DefaultRunKeyPath)
        {
            this.valueName = valueName;
            this.executablePath = executablePath;
            this.runKeyPath = runKeyPath;
        }

        /// <summary>Quoted so paths with spaces are not split by Windows when starting the app.</summary>
        public string Command => $"\"{executablePath}\"";

        /// <summary>True only if the entry points to this executable, not to a stale location.</summary>
        public bool IsEnabled
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, false);
                if (!(key?.GetValue(valueName) is string command))
                    return false;

                return string.Equals(Unquote(command), executablePath, StringComparison.OrdinalIgnoreCase);
            }
        }

        public void SetEnabled(bool enabled)
        {
            if (enabled)
            {
                using var key = Registry.CurrentUser.CreateSubKey(runKeyPath, true);
                key.SetValue(valueName, Command, RegistryValueKind.String);
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, true);
                key?.DeleteValue(valueName, false);
            }
        }

        private static string Unquote(string command) => command.Trim().Trim('"');
    }
}
