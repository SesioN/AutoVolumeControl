using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using MaterialSkin;
using MaterialSkin.Controls;

namespace AutoVolumeControl
{
    class MenuHandler
    {
        /// <summary>How long opening the menu waits for a fresh session list before showing the cached one.</summary>
        public static readonly TimeSpan RefreshTimeout = TimeSpan.FromMilliseconds(500);

        private readonly ContextMenuStrip contextMenuStrip;
        private readonly AppPreferences preferences;
        private readonly Apps apps;
        private readonly AutoStart autoStart;
        private readonly Func<Task> refreshApps;
        private string renderedState;

        public event EventHandler ExitRequested;

        public MenuHandler(ContextMenuStrip contextMenuStrip, AppPreferences preferences, Apps apps, AutoStart autoStart, Func<Task> refreshApps)
        {
            this.contextMenuStrip = contextMenuStrip;
            this.preferences = preferences;
            this.apps = apps;
            this.autoStart = autoStart;
            this.refreshApps = refreshApps;
            this.contextMenuStrip.Opening += ContextMenuStrip_Opening;
        }

        private void ContextMenuStrip_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                refreshApps?.Invoke().Wait(RefreshTimeout);
            }
            catch (AggregateException ex)
            {
                Trace.WriteLine($"Refreshing apps failed: {ex.GetBaseException().Message}");
            }
            Generate();
            // WinForms pre-cancels opening a menu that had no items when the click arrived.
            e.Cancel = false;
        }

        /// <summary>Rebuilds the menu if anything it shows has changed.</summary>
        /// <returns>true if the menu was rebuilt.</returns>
        public bool Generate()
        {
            var appList = apps.GetApps();
            var state = DescribeState(appList);
            if (state == renderedState)
                return false;

            ClearItems();
            AddHeader();
            AddAppItems(appList);
            AddSeparator();
            AddAutoRunItem();
            AddSeparator();
            AddExitItem();

            renderedState = state;
            return true;
        }

        private string DescribeState(List<string> appList)
        {
            var appStates = appList.Select(a => $"{a}={preferences.IsEnabled(a)}");
            return string.Join("\n", appStates) + $"\nautostart={autoStart.IsEnabled}";
        }

        /// <summary>Items.Clear() does not dispose the removed items; their window handles would leak.</summary>
        private void ClearItems()
        {
            var oldItems = contextMenuStrip.Items.Cast<ToolStripItem>().ToList();
            contextMenuStrip.Items.Clear();
            foreach (var item in oldItems)
                item.Dispose();
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

            AddSeparator();
        }

        private void AddAppItems(List<string> appList)
        {
            if (appList.Count == 0)
                return;

            var panel = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = true,
                Padding = new Padding(0, 10, 0, 10),
            };

            foreach (var appName in appList)
            {
                panel.Controls.Add(CreateAppCheckBox(appName));
            }

            var host = new ToolStripControlHost(panel)
            {
                AutoSize = true,
            };

            contextMenuStrip.Items.Add(host);
        }

        private MaterialCheckbox CreateAppCheckBox(string appName)
        {
            var checkbox = new MaterialCheckbox
            {
                Text = $"App: {appName}",
                Name = appName,
                Checked = preferences.IsEnabled(appName),
                AutoSize = true
            };
            checkbox.CheckedChanged += (sender, e) => preferences.SetEnabled(appName, checkbox.Checked);
            return checkbox;
        }

        private void AddAutoRunItem()
        {
            var checkbox = new MaterialCheckbox
            {
                Text = "Start with Windows",
                Name = "autostart",
                AutoSize = true,
                Checked = autoStart.IsEnabled
            };
            checkbox.CheckedChanged += (sender, e) => OnAutoRunCheckBoxChanged(checkbox);

            var host = new ToolStripControlHost(checkbox)
            {
                BackColor = Color.Transparent
            };
            contextMenuStrip.Items.Add(host);
        }

        private void OnAutoRunCheckBoxChanged(MaterialCheckbox checkbox)
        {
            try
            {
                autoStart.SetEnabled(checkbox.Checked);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is System.IO.IOException)
            {
                Trace.WriteLine($"Changing autostart failed: {ex.Message}");
                checkbox.Checked = autoStart.IsEnabled;
            }
        }

        private void AddExitItem()
        {
            var button = new MaterialButton
            {
                Text = "Exit",
                Name = "exit",
                AutoSize = false,
                Dock = DockStyle.Fill,
                Height = 36
            };
            button.Click += (sender, e) => ExitRequested?.Invoke(this, EventArgs.Empty);

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

        private void AddSeparator()
        {
            contextMenuStrip.Items.Add(new ToolStripSeparator
            {
                Margin = new Padding(0),
                BackColor = Color.Transparent
            });
        }
    }
}
