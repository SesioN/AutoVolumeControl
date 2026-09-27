using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoVolumeControl
{
    /// <summary>An app with an audio session: its name (the settings key) and, if known, its executable.</summary>
    sealed class AppInfo
    {
        public AppInfo(string name, string executablePath = null)
        {
            Name = name;
            ExecutablePath = string.IsNullOrEmpty(executablePath) ? null : executablePath;
        }

        public string Name { get; }

        /// <summary>Used for the icon in the menu; null if it could not be determined.</summary>
        public string ExecutablePath { get; }

        public override string ToString() => Name;
    }

    /// <summary>The apps that currently own an audio session on the default playback device.</summary>
    sealed class Apps
    {
        private readonly List<AppInfo> apps = new List<AppInfo>();
        private readonly object lockObj = new object();

        /// <summary>Raised on the thread that called <see cref="Update(IEnumerable{AppInfo})"/> when the list changed.</summary>
        public event EventHandler AppsUpdated;

        public List<AppInfo> GetApps()
        {
            lock (lockObj)
            {
                return new List<AppInfo>(apps);
            }
        }

        public List<string> GetAppNames()
        {
            lock (lockObj)
            {
                return apps.Select(a => a.Name).ToList();
            }
        }

        /// <summary>Replaces the list with apps whose executable is not known.</summary>
        public bool Update(IEnumerable<string> currentApps) => Update(currentApps.Select(name => new AppInfo(name)));

        /// <summary>
        /// Replaces the list, keeping the order of apps that are still present. Apps are unique by name; the first
        /// known executable of a name is used. An executable that becomes known for a listed app counts as a change,
        /// so the menu can show the app's icon.
        /// </summary>
        /// <returns>true if the list changed.</returns>
        public bool Update(IEnumerable<AppInfo> currentApps)
        {
            var current = new List<AppInfo>();
            foreach (var app in currentApps.Where(a => a != null && !string.IsNullOrEmpty(a.Name)))
            {
                int index = current.FindIndex(a => a.Name == app.Name);
                if (index < 0)
                    current.Add(app);
                else if (current[index].ExecutablePath == null && app.ExecutablePath != null)
                    current[index] = app;
            }

            bool isUpdated = false;
            lock (lockObj)
            {
                if (apps.RemoveAll(a => !current.Any(c => c.Name == a.Name)) > 0)
                    isUpdated = true;

                foreach (var app in current)
                {
                    int index = apps.FindIndex(a => a.Name == app.Name);
                    if (index < 0)
                    {
                        apps.Add(app);
                        isUpdated = true;
                    }
                    else if (apps[index].ExecutablePath == null && app.ExecutablePath != null)
                    {
                        // Kept once known: a later session of the same name must not make the icon flicker.
                        apps[index] = app;
                        isUpdated = true;
                    }
                }
            }

            if (isUpdated)
                AppsUpdated?.Invoke(this, EventArgs.Empty);

            return isUpdated;
        }
    }
}
