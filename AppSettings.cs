using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AutoVolumeControl
{
    class AppSettings
    {
        private static string appPath = "SOFTWARE\\" + Application.ProductName;
        private const string regKeySep = "\\";
        private RegistryKey appRegistryRoot;
        public AppSettings()
        {
            init();
        }

        public void set(string name, string value)
        {
            this.appRegistryRoot.SetValue(name, value);
        }

        public string get(string name)
        {
            return this.appRegistryRoot.GetValue(name) as string;
        }

        public bool exists(string name)
        {
            foreach (var valueName in this.appRegistryRoot.GetValueNames())
            {
                if (valueName == name)
                {
                    return true;
                }
            }

            return false;
        }

        private void init()
        {
            this.appRegistryRoot = Registry.CurrentUser.OpenSubKey(appPath, RegistryKeyPermissionCheck.ReadWriteSubTree);
            
            if (this.appRegistryRoot == null)
            {
                this.appRegistryRoot = Registry.CurrentUser.CreateSubKey(appPath, RegistryKeyPermissionCheck.ReadWriteSubTree);
            }
        }
    }
}
