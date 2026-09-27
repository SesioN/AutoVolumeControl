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
        private readonly AppIconCache icons;
        private readonly Func<bool> isMouseButtonDown;
        private List<AppInfo> renderedApps = new List<AppInfo>();
        private string renderedState;
        private bool rebuildDeferred;
        private bool draggingSlider;

        public event EventHandler ExitRequested;

        /// <param name="icons">Owned by the caller, which disposes it after the menu.</param>
        /// <param name="isMouseButtonDown">For tests; null reads <see cref="Control.MouseButtons"/>.</param>
        public MenuHandler(ContextMenuStrip contextMenuStrip, AppPreferences preferences, Apps apps, AutoStart autoStart, Func<Task> refreshApps, AppIconCache icons, Func<bool> isMouseButtonDown = null)
        {
            this.contextMenuStrip = contextMenuStrip;
            this.preferences = preferences;
            this.apps = apps;
            this.autoStart = autoStart;
            this.refreshApps = refreshApps;
            this.icons = icons;
            this.isMouseButtonDown = isMouseButtonDown ?? (() => Control.MouseButtons != MouseButtons.None);
            this.contextMenuStrip.Opening += ContextMenuStrip_Opening;
            this.contextMenuStrip.Closing += ContextMenuStrip_Closing;
            this.contextMenuStrip.MouseUp += OnMouseUpInMenu;
        }

        /// <summary>True while a rebuild waits for the mouse button to be released.</summary>
        internal bool RebuildDeferred => rebuildDeferred;

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
            Generate(deferWhileMouseDown: false);
            // WinForms pre-cancels opening a menu that had no items when the click arrived.
            e.Cancel = false;
        }

        /// <summary>
        /// Rebuilds the menu if anything it shows has changed. While a mouse button is held down on the open menu
        /// (e.g. while dragging a slider) the rebuild waits until it is released, so the control under the mouse
        /// is not destroyed.
        /// </summary>
        /// <returns>true if the menu was rebuilt.</returns>
        public bool Generate() => Generate(deferWhileMouseDown: true);

        private bool Generate(bool deferWhileMouseDown)
        {
            var appList = apps.GetApps();
            var state = DescribeState(appList);
            if (state == renderedState)
            {
                rebuildDeferred = false;
                return false;
            }

            if (deferWhileMouseDown && contextMenuStrip.Visible && isMouseButtonDown())
            {
                rebuildDeferred = true;
                return false;
            }

            ClearItems();
            AddHeader();
            AddAppItems(appList);
            AddSeparator();
            AddAutoRunItem();
            AddSeparator();
            AddExitItem();
            foreach (var host in contextMenuStrip.Items.OfType<ToolStripControlHost>())
                WatchMouseUp(host.Control);
            icons.Retain(appList.Select(a => a.Name));

            renderedApps = appList;
            renderedState = state;
            rebuildDeferred = false;
            draggingSlider = false;
            return true;
        }

        private string DescribeState(List<AppInfo> appList)
        {
            var appStates = appList.Select(a =>
                $"{a.Name}={preferences.IsEnabled(a.Name)};ratio={preferences.GetRatioPercent(a.Name)};icon={a.ExecutablePath != null}");
            return string.Join("\n", appStates) + $"\nautostart={autoStart.IsEnabled}";
        }

        /// <summary>A change made in the menu itself is already shown; it must not cause a rebuild.</summary>
        private void OnChangedInMenu()
        {
            renderedState = DescribeState(renderedApps);
        }

        private void OnMouseUpInMenu(object sender, MouseEventArgs e)
        {
            draggingSlider = false;
            if (!rebuildDeferred || !contextMenuStrip.IsHandleCreated)
                return;

            // Not from within the handler: the rebuild disposes the control that raised the event.
            contextMenuStrip.BeginInvoke((Action)(() =>
            {
                if (rebuildDeferred && !contextMenuStrip.IsDisposed)
                    Generate();
            }));
        }

        /// <summary>
        /// Keeps the menu open while a slider is dragged and the pointer leaves the menu; the slider holds the mouse
        /// capture then, and a menu closing under it would end the drag.
        /// </summary>
        private void ContextMenuStrip_Closing(object sender, ToolStripDropDownClosingEventArgs e)
        {
            bool closedByPointer = e.CloseReason == ToolStripDropDownCloseReason.AppClicked
                || e.CloseReason == ToolStripDropDownCloseReason.AppFocusChange;
            if (closedByPointer && draggingSlider && isMouseButtonDown())
                e.Cancel = true;
        }

        /// <summary>Hosted controls report mouse-ups to themselves only; every one of them ends a deferral.</summary>
        private void WatchMouseUp(Control control)
        {
            control.MouseUp += OnMouseUpInMenu;
            foreach (Control child in control.Controls)
                WatchMouseUp(child);
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

        private void AddAppItems(List<AppInfo> appList)
        {
            if (appList.Count == 0)
                return;

            var table = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 4,
                RowCount = appList.Count,
                Padding = new Padding(0, 10, 0, 10),
                BackColor = Color.Transparent,
            };
            for (int column = 0; column < table.ColumnCount; column++)
                table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            for (int row = 0; row < appList.Count; row++)
            {
                table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                AddAppRow(table, row, appList[row]);
            }

            var host = new ToolStripControlHost(table)
            {
                AutoSize = true,
                BackColor = Color.Transparent,
            };

            contextMenuStrip.Items.Add(host);
        }

        /// <summary>One row: checkbox, icon, name and the slider for the app's share of the master volume.</summary>
        private void AddAppRow(TableLayoutPanel table, int row, AppInfo app)
        {
            var appName = app.Name;
            bool enabled = preferences.IsEnabled(appName);

            var checkbox = new MaterialCheckbox
            {
                Text = string.Empty,
                Name = appName,
                Checked = enabled,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
            };

            var icon = new PictureBox
            {
                Name = IconName(appName),
                Image = icons.GetIcon(app),
                Size = icons.IconSize,
                SizeMode = PictureBoxSizeMode.CenterImage,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 0, 8, 0),
                BackColor = Color.Transparent,
            };

            var label = new MaterialLabel
            {
                Name = LabelName(appName),
                Text = appName,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 0, 12, 0),
            };

            var slider = new MaterialSlider
            {
                Name = SliderName(appName),
                ShowText = false,
                Text = string.Empty,
                ShowValue = true,
                ValueSuffix = "%",
                RangeMin = 0,
                RangeMax = 100,
                Width = 180,
                Anchor = AnchorStyles.Left,
                Enabled = enabled,
            };
            slider.Value = preferences.GetRatioPercent(appName);

            checkbox.CheckedChanged += (sender, e) =>
            {
                preferences.SetEnabled(appName, checkbox.Checked);
                slider.Enabled = checkbox.Checked;
                OnChangedInMenu();
            };
            // The row reads as one item: clicking the icon or the name toggles the app too.
            icon.Click += (sender, e) => checkbox.Checked = !checkbox.Checked;
            label.Click += (sender, e) => checkbox.Checked = !checkbox.Checked;
            // Raised for every step while dragging, so the app's volume follows the slider live. Unchanged values
            // are not written, and the service merges the resulting sync requests.
            slider.onValueChanged += (sender, value) =>
            {
                preferences.SetRatioPercent(appName, value);
                OnChangedInMenu();
            };
            slider.MouseDown += (sender, e) => draggingSlider = true;

            table.Controls.Add(checkbox, 0, row);
            table.Controls.Add(icon, 1, row);
            table.Controls.Add(label, 2, row);
            table.Controls.Add(slider, 3, row);
        }

        internal static string IconName(string appName) => "icon:" + appName;

        internal static string LabelName(string appName) => "name:" + appName;

        internal static string SliderName(string appName) => "ratio:" + appName;

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
