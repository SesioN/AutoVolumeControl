using System;
using System.Collections.Generic;
using System.Globalization;

namespace AutoVolumeControl
{
    /// <summary>
    /// Per-app settings: the "sync this app with the master volume" flag (new apps are enabled by default) and
    /// the ratio of the master volume the app is set to (100 % by default).
    /// </summary>
    sealed class AppPreferences
    {
        /// <summary>Name of the sub-store holding the ratios, so they cannot clash with the flags.</summary>
        public const string RatiosStoreName = "Ratios";
        public const int DefaultRatioPercent = 100;

        private readonly ISettingsStore store;
        private readonly ISettingsStore ratioStore;
        // Ratios set while a slider is dragged: already used by the sync, written to the store on commit.
        private readonly Dictionary<string, int> pendingRatios = new Dictionary<string, int>();
        private readonly object pendingLock = new object();

        public AppPreferences(ISettingsStore store)
        {
            this.store = store;
            ratioStore = store.GetSubStore(RatiosStoreName);
        }

        /// <summary>Raised on the calling thread after the user changed an app's flag or ratio.</summary>
        public event EventHandler Changed;

        public bool IsEnabled(string appName)
        {
            if (string.IsNullOrEmpty(appName))
                return false;

            var value = store.Get(appName);
            if (value == null)
                return true;

            // A hand-edited or corrupted value must not crash the menu or stop the sync.
            return bool.TryParse(value, out bool enabled) ? enabled : true;
        }

        public void SetEnabled(string appName, bool enabled)
        {
            if (string.IsNullOrEmpty(appName))
                return;

            store.Set(appName, enabled.ToString());
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The app's volume as a share of the master volume, 0.0 to 1.0.</summary>
        public float GetRatio(string appName) => GetRatioPercent(appName) / 100f;

        /// <summary>The app's volume in percent of the master volume, 0 to 100.</summary>
        public int GetRatioPercent(string appName)
        {
            if (string.IsNullOrEmpty(appName))
                return DefaultRatioPercent;

            lock (pendingLock)
            {
                if (pendingRatios.TryGetValue(appName, out int pending))
                    return pending;
            }

            var value = ratioStore.Get(appName);
            // Like the flag, a hand-edited or corrupted value is tolerated: unreadable means the default,
            // out of range is clamped.
            if (value == null || !int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent))
                return DefaultRatioPercent;

            return ClampPercent(percent);
        }

        /// <summary>Stores the ratio (0.0 to 1.0, rounded to whole percent); values outside the range are clamped.</summary>
        public void SetRatio(string appName, float ratio)
        {
            if (float.IsNaN(ratio))
                return;

            SetRatioPercent(appName, (int)Math.Round(Math.Max(0f, Math.Min(1f, ratio)) * 100f, MidpointRounding.AwayFromZero));
        }

        /// <summary>Stores the ratio in percent; values outside 0 to 100 are clamped. Writes only on a change.</summary>
        /// <param name="persist">
        /// false while the user drags a slider: the value takes effect immediately (and raises <see cref="Changed"/>),
        /// but is only written by <see cref="CommitRatios"/>, so a drag costs one write instead of one per step.
        /// </param>
        public void SetRatioPercent(string appName, int percent, bool persist = true)
        {
            if (string.IsNullOrEmpty(appName))
                return;

            percent = ClampPercent(percent);
            // The slider reports every step while dragging; unchanged values cost neither a write nor a sync.
            bool changed = GetRatioPercent(appName) != percent;
            if (persist)
            {
                lock (pendingLock)
                {
                    pendingRatios.Remove(appName);
                }
                Write(appName, percent);
            }
            else if (changed)
            {
                lock (pendingLock)
                {
                    pendingRatios[appName] = percent;
                }
            }

            if (changed)
                Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Writes the ratios set with <c>persist: false</c>.</summary>
        public void CommitRatios()
        {
            List<KeyValuePair<string, int>> pending;
            lock (pendingLock)
            {
                pending = new List<KeyValuePair<string, int>>(pendingRatios);
                pendingRatios.Clear();
            }
            // The values were already in effect, so nothing changes for the sync.
            foreach (var ratio in pending)
                Write(ratio.Key, ratio.Value);
        }

        private void Write(string appName, int percent)
        {
            var value = percent.ToString(CultureInfo.InvariantCulture);
            if (ratioStore.Get(appName) != value)
                ratioStore.Set(appName, value);
        }

        /// <summary>Persists the default for an app that has not been seen before.</summary>
        public void Register(string appName)
        {
            if (string.IsNullOrEmpty(appName) || store.Exists(appName))
                return;

            store.Set(appName, bool.TrueString);
        }

        private static int ClampPercent(int percent) => Math.Max(0, Math.Min(100, percent));
    }
}
