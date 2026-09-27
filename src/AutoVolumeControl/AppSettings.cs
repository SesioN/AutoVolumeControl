using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AutoVolumeControl
{
    interface ISettingsStore
    {
        string Get(string name);
        void Set(string name, string value);
        bool Exists(string name);
    }

    /// <summary>String values stored under HKCU\SOFTWARE\&lt;product name&gt;.</summary>
    sealed class AppSettings : ISettingsStore, IDisposable
    {
        private readonly RegistryKey appRegistryRoot;
        private readonly object lockObj = new object();

        public AppSettings() : this($"SOFTWARE\\{Application.ProductName}")
        {
        }

        public AppSettings(string keyPath)
        {
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

        public void Dispose()
        {
            lock (lockObj)
            {
                appRegistryRoot.Dispose();
            }
        }
    }
}
