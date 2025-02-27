using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AutoVolumeControl
{
    class MenuHandler
    {
        private readonly ContextMenuStrip contextMenuStrip;
        private EventHandler onExitHandler;
        private readonly AppSettings appSettings;
        private readonly Apps apps;

        public MenuHandler(ContextMenuStrip contextMenuStrip, AppSettings appSettings, Apps apps)
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
        }

        public void Generate()
        {
            contextMenuStrip.Items.Clear();
            AddHeader();
            AddAppItems();
            AddSeparator();
            AddAutoRunItem();
            AddExitItem();

            Console.WriteLine("Context menu generated with items:");
            foreach (ToolStripItem item in contextMenuStrip.Items)
            {
                Console.WriteLine($"- {item.Text}");
            }
        }

        private void AddHeader()
        {
            var header = new ToolStripMenuItem($"{Application.ProductName} (v{Application.ProductVersion})")
            {
                BackColor = Color.Transparent
            };
            contextMenuStrip.Items.Add(header);
            AddSeparator(0, 0, 0, 10);
        }

        private void AddAppItems()
        {
            var appList = apps.GetApps();
            Console.WriteLine($"Number of apps retrieved: {appList.Count}");
            foreach (var appName in appList)
            {
                var checkbox = CreateAppCheckBox(appName);
                var toolStripControlHost = new ToolStripControlHost(checkbox)
                {
                    BackColor = Color.Transparent
                };
                contextMenuStrip.Items.Add(toolStripControlHost);
                Console.WriteLine($"Added app to menu: {appName}");
            }
        }

        private CheckBox CreateAppCheckBox(string appName)
        {
            var checkbox = new CheckBox
            {
                Text = $"App: {appName}",
                Name = appName,
                Checked = GetAppSetting(appName)
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

        private void OnAppCheckBoxChanged(CheckBox checkbox)
        {
            appSettings.Set(checkbox.Name, checkbox.Checked.ToString());
        }

        private void AddAutoRunItem()
        {
            var checkbox = new CheckBox { Text = "Start with Windows" };
            checkbox.Checked = IsAutoRunEnabled();
            checkbox.CheckedChanged += (sender, e) => OnAutoRunCheckBoxChanged(checkbox);
            var toolStripControlHost = new ToolStripControlHost(checkbox)
            {
                BackColor = Color.Transparent
            };
            contextMenuStrip.Items.Add(toolStripControlHost);
        }

        private bool IsAutoRunEnabled()
        {
            using var autoStartRegKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
            return autoStartRegKey?.GetValue(Application.ProductName) != null;
        }

        private void OnAutoRunCheckBoxChanged(CheckBox checkbox)
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
            var exitItem = new ToolStripMenuItem("Exit")
            {
                BackColor = Color.Transparent
            };
            exitItem.Click += Exit;
            contextMenuStrip.Items.Add(exitItem);
        }

        private void Exit(object sender, EventArgs e)
        {
            onExitHandler?.Invoke(sender, e);
            Application.Exit();
        }

        private void AddSeparator(int left = 0, int top = 10, int right = 0, int bottom = 0)
        {
            contextMenuStrip.Items.Add(new ToolStripSeparator
            {
                Margin = new Padding(left, top, right, bottom),
                BackColor = Color.Transparent
            });
        }
    }
}
