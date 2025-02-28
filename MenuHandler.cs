using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using MaterialSkin;
using MaterialSkin.Controls;
using System.Drawing.Printing;

namespace AutoVolumeControl
{
    class MenuHandler
    {
        private readonly MaterialContextMenuStrip contextMenuStrip;
        private EventHandler onExitHandler;
        private readonly AppSettings appSettings;
        private readonly Apps apps;

        public MenuHandler(MaterialContextMenuStrip contextMenuStrip, AppSettings appSettings, Apps apps)
        {
            this.appSettings = appSettings;
            this.apps = apps;
            this.contextMenuStrip = contextMenuStrip;
            this.contextMenuStrip.Opening += ContextMenuStrip_Opening;
        }

        public void OnExit(EventHandler handler)
        {
            onExitHandler = handler;
        }

        private void ContextMenuStrip_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            apps.Refresh();
            Generate();
        }

        public void Generate()
        {
            contextMenuStrip.Items.Clear();
            AddHeader();
            AddAppItems();
            AddSeparator();
            AddAutoRunItem();
            AddSeparator();
            AddExitItem();

            Console.WriteLine("Context menu generated with items:");
            foreach (ToolStripItem item in contextMenuStrip.Items)
            {
                Console.WriteLine($"- {item.Text}");
            }
        }

        private void AddHeader()
        {
            var label = new MaterialLabel
            {
                Text = $"{Application.ProductName} (v{Application.ProductVersion})",
                AutoSize = true,
                FontType = MaterialSkinManager.fontType.H6
            };

            var host = new ToolStripControlHost(label)
            {
                BackColor = Color.Transparent,
                Margin = new Padding(0, 5, 0, 5)
            };
            contextMenuStrip.Items.Add(host);

            AddSeparator(0, 0, 0, 0);
        }

        private void AddAppItems()
        {
            var appList = apps.GetApps();
            Console.WriteLine($"Number of apps retrieved: {appList.Count}");
            foreach (var appName in appList)
            {
                var checkbox = CreateAppCheckBox(appName);
                var host = new ToolStripControlHost(checkbox)
                {
                    BackColor = Color.Transparent
                };
                contextMenuStrip.Items.Add(host);
                Console.WriteLine($"Added app to menu: {appName}");
            }
        }

        private MaterialCheckbox CreateAppCheckBox(string appName)
        {
            var checkbox = new MaterialCheckbox
            {
                Text = $"App: {appName}",
                Name = appName,
                Checked = GetAppSetting(appName),
                AutoSize = true
            };
            checkbox.CheckedChanged += (sender, e) => OnAppCheckBoxChanged(checkbox);
            return checkbox;
        }

        private bool GetAppSetting(string appName)
        {
            if (!appSettings.Exists(appName))
            {
                appSettings.Set(appName, "True");
                return true;
            }
            return Convert.ToBoolean(appSettings.Get(appName));
        }

        private void OnAppCheckBoxChanged(MaterialCheckbox checkbox)
        {
            appSettings.Set(checkbox.Name, checkbox.Checked.ToString());
        }

        private void AddAutoRunItem()
        {
            var checkbox = new MaterialCheckbox
            {
                Text = "Start with Windows",
                AutoSize = true,
            };
            checkbox.Checked = IsAutoRunEnabled();
            checkbox.CheckedChanged += (sender, e) => OnAutoRunCheckBoxChanged(checkbox);
            var host = new ToolStripControlHost(checkbox)
            {
                BackColor = Color.Transparent
            };
            contextMenuStrip.Items.Add(host);
        }

        private bool IsAutoRunEnabled()
        {
            using var autoStartRegKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
            return autoStartRegKey?.GetValue(Application.ProductName) != null;
        }

        private void OnAutoRunCheckBoxChanged(MaterialCheckbox checkbox)
        {
            using var autoStartRegKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (checkbox.Checked)
            {
                autoStartRegKey?.SetValue(Application.ProductName, Application.ExecutablePath);
            }
            else
            {
                autoStartRegKey?.DeleteValue(Application.ProductName, false);
            }
        }

        private void AddExitItem()
        {
            var button = new MaterialButton
            {
                Text = "Exit",
                AutoSize = false,
               
                Dock = DockStyle.Fill,
                Height = 36
            };
            button.Click += (sender, e) => Exit(sender, e);

            var host = new ToolStripControlHost(button)
            {
                AutoSize = false,
                Margin = new Padding(0, 10, 0, 10),
                BackColor = Color.Transparent,
                Width = 270,
                Height = button.Height
            };

            contextMenuStrip.Items.Add(host);
        }

        private void Exit(object sender, EventArgs e)
        {
            onExitHandler?.Invoke(sender, e);
            Application.Exit();
        }

        private void AddSeparator(int left = 0, int top = 0, int right = 0, int bottom = 0)
        {
            contextMenuStrip.Items.Add(new ToolStripSeparator
            {
                Margin = new Padding(left, top, right, bottom),
                BackColor = Color.Transparent
            });
        }
    }
}
