using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AutoVolumeControl
{
    interface ISettingsStore
    {
        string Get(string name);
        void Set(string name, string value);
        bool Exists(string name);

        /// <summary>A separate store nested in this one, so its names cannot clash with this store's values.</summary>
        ISettingsStore GetSubStore(string name);
    }

    /// <summary>String values stored under HKCU\SOFTWARE\&lt;product name&gt;.</summary>
    sealed class AppSettings : ISettingsStore, IDisposable
    {
        private readonly string keyPath;
        private readonly Dictionary<string, AppSettings> subStores = new Dictionary<string, AppSettings>(StringComparer.OrdinalIgnoreCase);
        private readonly object lockObj = new object();
        // Null while a lazily created key does not exist yet.
        private RegistryKey appRegistryRoot;

        public AppSettings() : this($"SOFTWARE\\{Application.ProductName}")
        {
        }

        public AppSettings(string keyPath) : this(keyPath, createNow: true)
        {
        }

        /// <param name="createNow">false: the key is only created by the first <see cref="Set"/>, so reading never writes.</param>
        private AppSettings(string keyPath, bool createNow)
        {
            this.keyPath = keyPath;
            if (createNow)
                appRegistryRoot = CreateKey();
        }

        public void Set(string name, string value)
        {
            lock (lockObj)
            {
                (appRegistryRoot ??= CreateKey()).SetValue(name, value, RegistryValueKind.String);
            }
        }

        public string Get(string name)
        {
            lock (lockObj)
            {
                return OpenIfExists()?.GetValue(name) as string;
            }
        }

        public bool Exists(string name)
        {
            lock (lockObj)
            {
                return OpenIfExists()?.GetValue(name) != null;
            }
        }

        /// <summary>
        /// The subkey HKCU\SOFTWARE\&lt;product name&gt;\&lt;name&gt;, created on its first write; disposed together
        /// with this store.
        /// </summary>
        public ISettingsStore GetSubStore(string name)
        {
            lock (lockObj)
            {
                if (!subStores.TryGetValue(name, out var subStore))
                {
                    subStore = new AppSettings($"{keyPath}\\{name}", createNow: false);
                    subStores.Add(name, subStore);
                }
                return subStore;
            }
        }

        private RegistryKey CreateKey()
        {
            return Registry.CurrentUser.CreateSubKey(keyPath, true)
                ?? throw new InvalidOperationException($"Unable to open registry key HKCU\\{keyPath}");
        }

        private RegistryKey OpenIfExists() => appRegistryRoot ??= Registry.CurrentUser.OpenSubKey(keyPath, true);

        public void Dispose()
        {
            lock (lockObj)
            {
                foreach (var subStore in subStores.Values)
                    subStore.Dispose();
                subStores.Clear();
                appRegistryRoot?.Dispose();
                appRegistryRoot = null;
            }
        }
    }
}
