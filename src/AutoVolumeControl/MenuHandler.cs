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

        /// <summary>How often a deferred rebuild checks whether the mouse button was released.</summary>
        internal const int DeferredRebuildPollMs = 50;

        private const int ExitButtonWidth = 270;
        private const int SliderWidth = 180;

        private readonly ContextMenuStrip contextMenuStrip;
        private readonly AppPreferences preferences;
        private readonly Apps apps;
        private readonly AutoStart autoStart;
        private readonly Func<Task> refreshApps;
        private readonly AppIconCache icons;
        private readonly Func<bool> isMouseButtonDown;
        private readonly Timer deferredRebuildTimer;
        private List<AppInfo> renderedApps = new List<AppInfo>();
        private string renderedState;
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

            deferredRebuildTimer = new Timer { Interval = DeferredRebuildPollMs };
            deferredRebuildTimer.Tick += DeferredRebuildTimer_Tick;

            this.contextMenuStrip.Opening += ContextMenuStrip_Opening;
            this.contextMenuStrip.Closing += ContextMenuStrip_Closing;
            this.contextMenuStrip.Closed += (sender, e) => EndDrag();
            this.contextMenuStrip.Disposed += (sender, e) => deferredRebuildTimer.Dispose();
        }

        /// <summary>True while a rebuild waits for the mouse button to be released.</summary>
        internal bool RebuildDeferred => deferredRebuildTimer.Enabled;

        /// <summary>True between pressing the mouse on a slider and releasing it (or losing the mouse capture).</summary>
        internal bool DraggingSlider => draggingSlider;

        private void ContextMenuStrip_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            EndDrag();
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
                deferredRebuildTimer.Stop();
                return false;
            }

            if (deferWhileMouseDown && contextMenuStrip.Visible && isMouseButtonDown())
            {
                // Polled rather than tied to a mouse-up event: the button may be released over a separator, outside
                // the menu or after the capture was lost, and none of these reach the hosted controls.
                deferredRebuildTimer.Start();
                return false;
            }

            deferredRebuildTimer.Stop();
            ClearItems();
            AddHeader();
            var appTable = AddAppItems(appList);
            AddSeparator();
            AddAutoRunItem();
            AddSeparator();
            AddExitItem(appTable);
            icons.Retain(appList.Select(a => a.Name));

            renderedApps = appList;
            renderedState = state;
            EndDrag();
            return true;
        }

        /// <summary>A drag only changes the ratio in memory; the final value is written when it ends.</summary>
        private void EndDrag()
        {
            draggingSlider = false;
            preferences.CommitRatios();
        }

        private void DeferredRebuildTimer_Tick(object sender, EventArgs e)
        {
            if (contextMenuStrip.IsDisposed)
            {
                deferredRebuildTimer.Stop();
                return;
            }
            if (isMouseButtonDown())
                return;

            deferredRebuildTimer.Stop();
            Generate();
        }

        private string DescribeState(List<AppInfo> appList)
        {
            var appStates = appList.Select(a =>
                $"{a.Name}={preferences.IsEnabled(a.Name)};ratio={preferences.GetRatioPercent(a.Name)};icon={a.ExecutablePath != null}");
            return string.Join("\n", appStates) + $"\nautostart={autoStart.IsEnabled}";
        }

        /// <summary>
        /// Applies a change the user made in the menu. The menu already shows it, so it must not cause a rebuild;
        /// but a change made elsewhere that the menu does not show yet still does.
        /// </summary>
        private void ChangeInMenu(Action change)
        {
            bool upToDate = DescribeState(renderedApps) == renderedState;
            change();
            if (upToDate)
                renderedState = DescribeState(renderedApps);
        }

        /// <summary>
        /// Keeps the menu open when the pointer leaves it while a slider is dragged and the button is released
        /// outside; the slider holds the mouse capture then.
        /// </summary>
        private void ContextMenuStrip_Closing(object sender, ToolStripDropDownClosingEventArgs e)
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.AppClicked && draggingSlider && isMouseButtonDown())
                e.Cancel = true;
        }

        /// <summary>Items.Clear() does not dispose the removed items; their window handles would leak.</summary>
        private void ClearItems()
        {
            var oldItems = contextMenuStrip.Items.Cast<ToolStripItem>().ToList();
            contextMenuStrip.Items.Clear();
            foreach (var item in oldItems)
                item.Dispose();
        }

        /// <summary>Converts a size at 96 DPI to the menu's DPI (e.g. 150 % at 144 DPI).</summary>
        private int Scale(int logicalPixels) => (int)Math.Round(logicalPixels * contextMenuStrip.DeviceDpi / 96.0);

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

        /// <returns>The table of app rows, or null if there are no apps.</returns>
        private TableLayoutPanel AddAppItems(List<AppInfo> appList)
        {
            if (appList.Count == 0)
                return null;

            // No transparent BackColor here: the Material controls fill their background with their parent's
            // BackColor, and a transparent one comes out black. The table inherits the menu's color instead.
            var table = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 4,
                RowCount = appList.Count,
                Padding = new Padding(0, 10, 0, 10),
            };
            for (int column = 0; column < table.ColumnCount; column++)
                table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            for (int row = 0; row < appList.Count; row++)
            {
                table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                AddAppRow(table, row, appList[row]);
            }

            contextMenuStrip.Items.Add(new ToolStripControlHost(table) { AutoSize = true });
            return table;
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
                Margin = new Padding(0, 0, Scale(8), 0),
                BackColor = Color.Transparent,
            };

            var label = new MaterialLabel
            {
                Name = LabelName(appName),
                Text = appName,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 0, Scale(12), 0),
            };

            var slider = new RatioSlider
            {
                Name = SliderName(appName),
                Width = Scale(SliderWidth),
                Anchor = AnchorStyles.Left,
                Enabled = enabled,
            };
            slider.Value = preferences.GetRatioPercent(appName);

            checkbox.CheckedChanged += (sender, e) => ChangeInMenu(() =>
            {
                preferences.SetEnabled(appName, checkbox.Checked);
                slider.Enabled = checkbox.Checked;
            });
            // The row reads as one item: clicking the icon or the name toggles the app too.
            icon.Click += (sender, e) => checkbox.Checked = !checkbox.Checked;
            label.Click += (sender, e) => checkbox.Checked = !checkbox.Checked;
            // Raised for every step while dragging, so the app's volume follows the slider live; the service merges
            // the resulting sync requests. While dragging, the value is written only when the drag ends.
            slider.RatioChanged += (sender, value) =>
                ChangeInMenu(() => preferences.SetRatioPercent(appName, value, persist: !draggingSlider));
            slider.MouseDown += (sender, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    draggingSlider = true;
            };
            slider.MouseUp += (sender, e) =>
            {
                if (draggingSlider)
                    EndDrag();
            };
            // Also raised when the capture is lost without a mouse-up (e.g. Escape, Alt+Tab).
            slider.MouseCaptureChanged += (sender, e) =>
            {
                if (!slider.Capture && draggingSlider)
                    EndDrag();
            };

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
            checkbox.CheckedChanged += (sender, e) => ChangeInMenu(() => OnAutoRunCheckBoxChanged(checkbox));

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

        /// <param name="appTable">The app rows, if any; the button is at least as wide, so it spans the menu.</param>
        private void AddExitItem(TableLayoutPanel appTable)
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
                Width = Math.Max(Scale(ExitButtonWidth), appTable?.GetPreferredSize(Size.Empty).Width ?? 0),
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
