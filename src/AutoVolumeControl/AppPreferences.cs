using System;

namespace AutoVolumeControl
{
    /// <summary>Per-app "sync this app with the master volume" flag. New apps are enabled by default.</summary>
    sealed class AppPreferences
    {
        private readonly ISettingsStore store;

        public AppPreferences(ISettingsStore store)
        {
            this.store = store;
        }

        /// <summary>Raised on the calling thread after the user changed an app's flag.</summary>
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

        /// <summary>Persists the default for an app that has not been seen before.</summary>
        public void Register(string appName)
        {
            if (string.IsNullOrEmpty(appName) || store.Exists(appName))
                return;

            store.Set(appName, bool.TrueString);
        }
    }
}
