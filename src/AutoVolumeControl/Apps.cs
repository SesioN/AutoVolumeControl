using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoVolumeControl
{
    /// <summary>The apps that currently own an audio session on the default playback device.</summary>
    sealed class Apps
    {
        private readonly List<string> apps = new List<string>();
        private readonly object lockObj = new object();

        /// <summary>Raised on the thread that called <see cref="Update"/> when the list changed.</summary>
        public event EventHandler AppsUpdated;

        public List<string> GetApps()
        {
            lock (lockObj)
            {
                return new List<string>(apps);
            }
        }

        /// <summary>Replaces the list, keeping the order of apps that are still present.</summary>
        /// <returns>true if the list changed.</returns>
        public bool Update(IEnumerable<string> currentApps)
        {
            var current = currentApps
                .Where(a => !string.IsNullOrEmpty(a))
                .Distinct()
                .ToList();

            bool isUpdated = false;
            lock (lockObj)
            {
                if (apps.RemoveAll(a => !current.Contains(a)) > 0)
                    isUpdated = true;

                foreach (var app in current)
                {
                    if (!apps.Contains(app))
                    {
                        apps.Add(app);
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
