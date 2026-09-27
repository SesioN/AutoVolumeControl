using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace AutoVolumeControl
{
    /// <summary>
    /// The title of the window belonging to an app's audio, e.g. "shroud - Twitch" for Chrome. Browsers (and apps
    /// built on them, like Spotify) play audio from a helper process without a window, so the processes' ancestors
    /// with the same executable name are searched too. Of the matching windows the topmost, i.e. most recently
    /// active, one wins; for a browser that is its active tab, which is not necessarily the one playing.
    /// </summary>
    static class WindowTitles
    {
        /// <summary>How many levels of parent processes are searched.</summary>
        private const int MaxAncestors = 4;

        /// <summary>Window title suffixes naming the browser, removed from the title.</summary>
        private static readonly string[] BrowserNames =
        {
            "Google Chrome", "Chromium", "Microsoft Edge", "Mozilla Firefox", "Firefox", "Brave", "Opera", "Vivaldi",
        };

        private static readonly string[] Separators = { " - ", " — ", " – " };

        /// <summary>The cleaned title of the app's window, or null if it has none (or it cannot be read).</summary>
        public static string Find(AppInfo app)
        {
            if (app == null || app.ProcessIds.Count == 0)
                return null;

            try
            {
                var processes = FindProcessIds(app.ProcessIds, ReadProcessTree());
                var title = FindTopWindowTitle(processes);
                return title == null ? null : Clean(title);
            }
            catch (Exception ex) when (ex is ExternalException || ex is InvalidOperationException || ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                Trace.WriteLine($"Reading the window title of '{app.Name}' failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The processes and their ancestors running the same executable (e.g. the Chrome audio service and the
        /// Chrome browser process that owns the windows).
        /// </summary>
        internal static HashSet<int> FindProcessIds(IEnumerable<int> processIds, IReadOnlyDictionary<int, (int Parent, string Name)> tree)
        {
            var result = new HashSet<int>();
            foreach (int id in processIds)
            {
                result.Add(id);
                if (!tree.TryGetValue(id, out var current))
                    continue;

                for (int level = 0; level < MaxAncestors && current.Parent > 0 && !result.Contains(current.Parent); level++)
                {
                    if (!tree.TryGetValue(current.Parent, out var parent) || !string.Equals(parent.Name, current.Name, StringComparison.OrdinalIgnoreCase))
                        break;
                    result.Add(current.Parent);
                    current = parent;
                }
            }
            return result;
        }

        /// <summary>
        /// Removes a trailing browser name ("shroud - Twitch - Google Chrome" becomes "shroud - Twitch"), invisible
        /// characters and surrounding white space; null if nothing is left.
        /// </summary>
        internal static string Clean(string title)
        {
            if (title == null)
                return null;

            // Edge writes its name with a zero-width space.
            title = title.Replace("​", string.Empty).Replace("‎", string.Empty).Trim();
            foreach (var separator in Separators)
            {
                int index = title.LastIndexOf(separator, StringComparison.Ordinal);
                if (index < 0)
                    continue;

                var suffix = title.Substring(index + separator.Length).Trim();
                if (BrowserNames.Any(name => string.Equals(name, suffix, StringComparison.OrdinalIgnoreCase)))
                {
                    title = title.Substring(0, index).Trim();
                    break;
                }
            }
            return title.Length == 0 ? null : title;
        }

        private static IReadOnlyDictionary<int, (int Parent, string Name)> ReadProcessTree()
        {
            var tree = new Dictionary<int, (int Parent, string Name)>();
            var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
            if (snapshot == InvalidHandle)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            try
            {
                var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
                if (!Process32First(snapshot, ref entry))
                    return tree;
                do
                {
                    tree[(int)entry.ProcessId] = ((int)entry.ParentProcessId, entry.ExeFile);
                }
                while (Process32Next(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }
            return tree;
        }

        /// <summary>The title of the topmost visible, unowned window of one of the processes.</summary>
        private static string FindTopWindowTitle(HashSet<int> processIds)
        {
            string found = null;
            // EnumWindows lists the top-level windows in z-order, the topmost first.
            EnumWindows((window, _) =>
            {
                if (!IsWindowVisible(window) || GetWindow(window, GwOwner) != IntPtr.Zero)
                    return true;
                if ((GetWindowLong(window, GwlExStyle) & WsExToolWindow) != 0 || IsCloaked(window))
                    return true;

                GetWindowThreadProcessId(window, out uint processId);
                if (!processIds.Contains((int)processId))
                    return true;

                int length = GetWindowTextLength(window);
                if (length == 0)
                    return true;

                var text = new StringBuilder(length + 1);
                GetWindowText(window, text, text.Capacity);
                if (string.IsNullOrWhiteSpace(text.ToString()))
                    return true;

                found = text.ToString();
                return false;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>Windows of suspended store apps and on other virtual desktops are "visible" but cloaked.</summary>
        private static bool IsCloaked(IntPtr window)
        {
            try
            {
                return DwmGetWindowAttribute(window, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        private const uint SnapProcess = 0x2;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);
        private const uint GwOwner = 4;
        private const int GwlExStyle = -20;
        private const int WsExToolWindow = 0x80;
        private const int DwmwaCloaked = 14;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExeFile;
        }

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
        private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
        private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    }
}
