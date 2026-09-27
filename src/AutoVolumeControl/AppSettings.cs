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
        private readonly RegistryKey appRegistryRoot;
        private readonly string keyPath;
        private readonly Dictionary<string, AppSettings> subStores = new Dictionary<string, AppSettings>(StringComparer.OrdinalIgnoreCase);
        private readonly object lockObj = new object();

        public AppSettings() : this($"SOFTWARE\\{Application.ProductName}")
        {
        }

        public AppSettings(string keyPath)
        {
            this.keyPath = keyPath;
            appRegistryRoot = Registry.CurrentUser.CreateSubKey(keyPath, true)
                ?? throw new InvalidOperationException($"Unable to open registry key HKCU\\{keyPath}");
        }

        public void Set(string name, string value)
        {
            lock (lockObj)
            {
                appRegistryRoot.SetValue(name, value, RegistryValueKind.String);
            }
        }

        public string Get(string name)
        {
            lock (lockObj)
            {
                return appRegistryRoot.GetValue(name) as string;
            }
        }

        public bool Exists(string name)
        {
            lock (lockObj)
            {
                return appRegistryRoot.GetValue(name) != null;
            }
        }

        /// <summary>The subkey HKCU\SOFTWARE\&lt;product name&gt;\&lt;name&gt;; disposed together with this store.</summary>
        public ISettingsStore GetSubStore(string name)
        {
            lock (lockObj)
            {
                if (!subStores.TryGetValue(name, out var subStore))
                {
                    subStore = new AppSettings($"{keyPath}\\{name}");
                    subStores.Add(name, subStore);
                }
                return subStore;
            }
        }

        public void Dispose()
        {
            lock (lockObj)
            {
                foreach (var subStore in subStores.Values)
                    subStore.Dispose();
                subStores.Clear();
                appRegistryRoot.Dispose();
            }
        }
    }
}
