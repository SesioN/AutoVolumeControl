using System;
using System.Diagnostics;
using System.Windows.Forms;
using AudioSwitcher.AudioApi.CoreAudio;
using System.Text.RegularExpressions;
using System.ComponentModel;
using Microsoft.Win32;

namespace AutoVolumeControl
{
    class MenuHandler
    {
        private ContextMenuStrip contextMenuStrip;
        private event EventHandler onExitHandler;
        private AppSettings appSettings;
        private Apps apps;

        public MenuHandler(ContextMenuStrip contextMenuStrip, AppSettings appSettings, Apps apps)
        {
            this.appSettings = appSettings;
            this.apps = apps;

            this.contextMenuStrip = contextMenuStrip;
            this.contextMenuStrip.Opening += new System.ComponentModel.CancelEventHandler((sender, e) => {
                this.generate();

                e.Cancel = false;
            });
        }

        public void onExit(EventHandler handler)
        {
            this.onExitHandler = handler;
        }

        public void generate()
        {
            this.contextMenuStrip.Items.Clear();

            this.addHeader();


            foreach (var appName in this.apps.GetApps())
            {
                var checkbox = new CheckBox { Text = "App: " + appName, Name = appName };
                checkbox.Click += (sender, e) => onClickVoiceApplication(sender, e, checkbox);
                if (!this.appSettings.exists(appName)) {
                    checkbox.Checked = true;
                    this.appSettings.set(appName, Convert.ToString(checkbox.Checked));
                } else
                {
                    checkbox.Checked = Convert.ToBoolean(this.appSettings.get(appName));
                }

                var toolStripControlHost = new ToolStripControlHost(checkbox);
                toolStripControlHost.BackColor = System.Drawing.Color.Transparent;

                this.contextMenuStrip.Items.Add(toolStripControlHost);
            }

            if (this.contextMenuStrip.Items.Count > 0)
            {
                this.contextMenuStrip.Items.Add("-").Margin = new System.Windows.Forms.Padding(0, 10, 0, 0);
                this.contextMenuStrip.Items[this.contextMenuStrip.Items.Count - 1].BackColor = System.Drawing.Color.Transparent;
            }
            

            this.addAutoRun();
            this.addExit();
        }

        private void addHeader()
        {
            ToolStripMenuItem header = new ToolStripMenuItem(Application.ProductName + " (v" + Application.ProductVersion + ")");
            header.BackColor = System.Drawing.Color.Transparent;
            this.contextMenuStrip.Items.Add(header);
            this.contextMenuStrip.Items.Add("-").Margin = new System.Windows.Forms.Padding(0, 0, 0, 10); ;
        }
        private void addAutoRun()
        {
            var checkbox = new CheckBox { Text = "Start with Windows" };
            checkbox.Click += (sender, e) => onClickAutoRun(sender, e, checkbox);

            RegistryKey autoStartRegKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);

            foreach (var name in autoStartRegKey.GetValueNames())
            {
                if (name == Application.ProductName)
                {
                    checkbox.Checked = true;
                }
            } 

            var toolStripControlHost = new ToolStripControlHost(checkbox);
            toolStripControlHost.BackColor = System.Drawing.Color.Transparent;

            this.contextMenuStrip.Items.Add(toolStripControlHost);
        }

        private void addExit()
        {
            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit", null, new EventHandler(Exit));
            exitItem.Click += (sender, e) => onClickVoiceApplication(sender, e, exitItem);
            this.contextMenuStrip.Items.Add(exitItem);
        }

        private void onClickVoiceApplication(object sender, EventArgs e, CheckBox item)
        {
            item.Checked = !Convert.ToBoolean(this.appSettings.get(item.Name));
            this.appSettings.set(item.Name, Convert.ToString(item.Checked));
        }
        private void onClickVoiceApplication(object sender, EventArgs e, ToolStripMenuItem item)
        {
            item.Checked = !Convert.ToBoolean(this.appSettings.get(item.Text));
            this.appSettings.set(item.Text, Convert.ToString(item.Checked));
        }

        private void onClickAutoRun(object sender, EventArgs e, CheckBox item)
        {
            RegistryKey autoStartRegKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);

            if (item.Checked)
            {
                autoStartRegKey.SetValue(Application.ProductName, Application.ExecutablePath);
            }
            else
            {
                autoStartRegKey.DeleteValue(Application.ProductName);
            }
        }

        private void Exit(object sender, EventArgs e)
        {
            this.onExitHandler?.Invoke(sender, e);

            Application.Exit();
        }
    }
}
