using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;

namespace AutoVolumeControl
{
    /// <summary>
    /// Windows passes compatibility settings to child processes through the __COMPAT_LAYER environment
    /// variable. A launcher configured with "Override high DPI scaling: System (Enhanced)" (e.g. a file
    /// manager) therefore forces every app it starts to be DPI unaware, which makes the menu large and
    /// blurry regardless of our manifest. Such inherited DPI layers are removed by restarting once.
    /// </summary>
    static class CompatLayer
    {
        public const string VariableName = "__COMPAT_LAYER";
        public const string RelaunchMarker = "AUTOVOLUMECONTROL_DPI_RELAUNCHED";

        private static readonly string[] DpiLayers = { "DPIUNAWARE", "GDIDPISCALING" };

        public static bool HasDpiLayers(string value)
        {
            return Tokens(value).Any(IsDpiLayer);
        }

        /// <returns>The value without DPI layers, or null if nothing meaningful is left.</returns>
        public static string RemoveDpiLayers(string value)
        {
            var remaining = Tokens(value).Where(t => !IsDpiLayer(t)).ToList();
            if (remaining.All(t => t == "~"))
                return null;
            return string.Join(" ", remaining);
        }

        /// <summary>Restarts the app without inherited DPI layers. Returns true if the caller should exit.</summary>
        public static bool RelaunchWithoutDpiLayers()
        {
            var value = Environment.GetEnvironmentVariable(VariableName);
            if (!HasDpiLayers(value) || Environment.GetEnvironmentVariable(RelaunchMarker) != null)
                return false;

            try
            {
                var startInfo = new ProcessStartInfo(Application.ExecutablePath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Environment.CurrentDirectory
                };
                var cleaned = RemoveDpiLayers(value);
                if (cleaned == null)
                    startInfo.EnvironmentVariables.Remove(VariableName);
                else
                    startInfo.EnvironmentVariables[VariableName] = cleaned;
                startInfo.EnvironmentVariables[RelaunchMarker] = "1";

                Process.Start(startInfo)?.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                // Keep running, just not sharp.
                Trace.WriteLine($"Restart without DPI compatibility layers failed: {ex.Message}");
                return false;
            }
        }

        private static string[] Tokens(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? Array.Empty<string>()
                : value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool IsDpiLayer(string token)
        {
            return DpiLayers.Contains(token, StringComparer.OrdinalIgnoreCase);
        }
    }
}
