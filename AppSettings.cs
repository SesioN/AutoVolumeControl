using System.Windows.Forms;
using Microsoft.Win32;

namespace AutoVolumeControl
{
    class AppSettings
    {
        private static readonly string appPath = $"SOFTWARE\\{Application.ProductName}";
        private readonly RegistryKey appRegistryRoot;

        public AppSettings()
        {
            appRegistryRoot = Registry.CurrentUser.OpenSubKey(appPath, true)
                ?? Registry.CurrentUser.CreateSubKey(appPath, true);
        }

        public void Set(string name, string value)
        {
            appRegistryRoot.SetValue(name, value);
        }

        public string Get(string name)
        {
            return appRegistryRoot.GetValue(name) as string;
        }

        public bool Exists(string name)
        {
            return appRegistryRoot.GetValue(name) != null;
        }
    }
}