using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AutoVolumeControl
{
    class MenuHandler
    {
        /// <summary>How long opening the menu waits for a fresh session list before showing the cached one.</summary>
        public static readonly TimeSpan RefreshTimeout = TimeSpan.FromMilliseconds(500);

        /// <summary>How often a deferred rebuild checks whether the mouse button was released.</summary>
        internal const int DeferredRebuildPollMs = 50;

        // Sizes at 96 DPI and 100 %; everything in the menu is scaled with the display and the user's menu size.
        /// <summary>
        /// The size of the menu at the default "App Scale" of 100 %, relative to the sizes below (the menu was found
        /// too small at 1.0).
        /// </summary>
        internal const float BaseScale = 1.3f;

        private const int ExitButtonWidth = 270;
        private const int ExitButtonHeight = 36;
        private const int SliderWidth = 180;
        /// <summary>Longer app names and window titles are cut off with an ellipsis (the tooltip shows them in full).</summary>
        private const int MaxNameWidth = 200;
        private const int ScaleSliderMinWidth = 160;

        private readonly ContextMenuStrip contextMenuStrip;
        private readonly AppPreferences preferences;
        private readonly Apps apps;
        private readonly AutoStart autoStart;
        private readonly Func<Task> refreshApps;
        private readonly AppIconCache icons;
        private readonly MenuScale scale;
        private readonly Func<bool> isMouseButtonDown;
        private readonly Func<AppInfo, string> findTitle;
        private readonly Timer deferredRebuildTimer;
        private readonly ToolTip toolTip;
        // Window titles by app name, read when the menu opens (or an app appears), so the rows do not change while
        // the menu is used. null: the app has no window title.
        private readonly Dictionary<string, string> titles = new Dictionary<string, string>();
        private List<AppInfo> renderedApps = new List<AppInfo>();
        private string renderedState;
        private bool draggingSlider;
        // The fonts of the current items; replaced (and the old ones disposed) together with the items.
        private MenuFonts fonts;
        private float scaleFactor = 1f;

        public event EventHandler ExitRequested;

        /// <param name="icons">Owned by the caller, which disposes it after the menu.</param>
        /// <param name="scale">The user's menu size.</param>
        /// <param name="isMouseButtonDown">For tests; null reads <see cref="Control.MouseButtons"/>.</param>
        /// <param name="findTitle">For tests; null reads the window title with <see cref="WindowTitles.Find"/>.</param>
        public MenuHandler(ContextMenuStrip contextMenuStrip, AppPreferences preferences, Apps apps, AutoStart autoStart, Func<Task> refreshApps, AppIconCache icons, MenuScale scale, Func<bool> isMouseButtonDown = null, Func<AppInfo, string> findTitle = null)
        {
            this.contextMenuStrip = contextMenuStrip;
            this.preferences = preferences;
            this.apps = apps;
            this.autoStart = autoStart;
            this.refreshApps = refreshApps;
            this.icons = icons;
            this.scale = scale;
            this.isMouseButtonDown = isMouseButtonDown ?? (() => Control.MouseButtons != MouseButtons.None);
            this.findTitle = findTitle ?? WindowTitles.Find;
            // Shown although the menu is not an active form.
            toolTip = new ToolTip { ShowAlways = true };

            contextMenuStrip.Renderer = new MenuRenderer();
            contextMenuStrip.ShowImageMargin = false;
            contextMenuStrip.ShowCheckMargin = false;
            contextMenuStrip.BackColor = MenuTheme.Background;

            deferredRebuildTimer = new Timer { Interval = DeferredRebuildPollMs };
            deferredRebuildTimer.Tick += DeferredRebuildTimer_Tick;

            this.contextMenuStrip.Opening += ContextMenuStrip_Opening;
            this.contextMenuStrip.Closing += ContextMenuStrip_Closing;
            this.contextMenuStrip.Closed += (sender, e) => EndDrag();
            this.contextMenuStrip.Disposed += (sender, e) =>
            {
                deferredRebuildTimer.Dispose();
                toolTip.Dispose();
                // The items are disposed with the menu; their fonts only now.
                fonts?.Dispose();
                fonts = null;
            };
        }

        /// <summary>True while a rebuild waits for the mouse button to be released.</summary>
        internal bool RebuildDeferred => deferredRebuildTimer.Enabled;

        /// <summary>True between pressing the mouse on a slider and releasing it (or losing the mouse capture).</summary>
        internal bool DraggingSlider => draggingSlider;

        /// <summary>Display scaling times the user's menu size, as used by the current items.</summary>
        internal float ScaleFactor => scaleFactor;

        private void ContextMenuStrip_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            EndDrag();
            // Read the window titles again: the rows show the current ones each time the menu opens.
            titles.Clear();
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
            bool visible = contextMenuStrip.Visible;
            var oldBounds = contextMenuStrip.Bounds;

            contextMenuStrip.SuspendLayout();
            try
            {
                ClearItems();
                ApplyScale();
                AddHeader();
                AddScaleItem();
                AddSeparator();
                if (AddAppItems(appList))
                    AddSeparator();
                AddAutoRunItem();
                AddSeparator();
                AddExitItem();
                FitFullWidthItems();
                icons.Retain(appList.Select(a => a.Name));
            }
            finally
            {
                contextMenuStrip.ResumeLayout();
            }

            renderedApps = appList;
            renderedState = state;
            EndDrag();
            if (visible)
                KeepOnScreen(oldBounds);
            return true;
        }

        /// <summary>
        /// Takes the scale for the new items from the display and the user's menu size. Called after the old items
        /// were disposed: the old fonts and icons are released here.
        /// </summary>
        private void ApplyScale()
        {
            scaleFactor = contextMenuStrip.DeviceDpi / 96f * BaseScale * scale.Factor;

            fonts?.Dispose();
            fonts = new MenuFonts(scaleFactor);

            var small = SystemInformation.SmallIconSize;
            icons.SetIconSize(new Size(
                (int)Math.Round(small.Width * BaseScale * scale.Factor),
                (int)Math.Round(small.Height * BaseScale * scale.Factor)));

            contextMenuStrip.Padding = new Padding(Scale(2), Scale(4), Scale(2), Scale(4));
        }

        /// <summary>
        /// A menu that changed its size while open grows from the corner nearest to where it was opened, e.g. upwards
        /// from the tray, and stays on the screen.
        /// </summary>
        private void KeepOnScreen(Rectangle oldBounds)
        {
            var size = contextMenuStrip.Size;
            if (size == oldBounds.Size)
                return;

            var area = Screen.FromRectangle(oldBounds).WorkingArea;
            int x = oldBounds.Left + oldBounds.Width / 2 > area.Left + area.Width / 2 ? oldBounds.Right - size.Width : oldBounds.Left;
            int y = oldBounds.Top + oldBounds.Height / 2 > area.Top + area.Height / 2 ? oldBounds.Bottom - size.Height : oldBounds.Top;
            x = Math.Max(area.Left, Math.Min(x, area.Right - size.Width));
            y = Math.Max(area.Top, Math.Min(y, area.Bottom - size.Height));
            contextMenuStrip.Location = new Point(x, y);
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
                $"{a.Name}={preferences.IsEnabled(a.Name)};ratio={preferences.GetRatioPercent(a.Name)};icon={a.ExecutablePath != null};title={Title(a)}");
            return string.Join("\n", appStates) + $"\nautostart={autoStart.IsEnabled}\nscale={scale.Percent};dpi={contextMenuStrip.DeviceDpi}";
        }

        /// <summary>The app's window title, read once per opening of the menu; null if it has none.</summary>
        private string Title(AppInfo app)
        {
            if (!titles.TryGetValue(app.Name, out var title))
            {
                try
                {
                    title = findTitle(app);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Reading the window title of '{app.Name}' failed: {ex.Message}");
                    title = null;
                }
                titles[app.Name] = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
                title = titles[app.Name];
            }
            return title;
        }

        /// <summary>The row text: the window title if there is one, else the app name.</summary>
        internal static string RowText(string appName, string title) => title ?? appName;

        /// <summary>The tooltip of a row: the full title and the app name, or just the app name.</summary>
        internal static string RowToolTip(string appName, string title) => title == null ? appName : $"{title}\n{appName}";

        /// <summary>The text cut off with "…" so it is at most the given width in the font.</summary>
        internal static string Ellipsize(string text, Font font, int maxWidth)
        {
            if (string.IsNullOrEmpty(text) || Measure(text, font) <= maxWidth)
                return text;

            const string ellipsis = "…";
            // The longest prefix that fits together with the ellipsis.
            int low = 0, high = text.Length;
            while (low < high)
            {
                int length = (low + high + 1) / 2;
                if (Measure(text.Substring(0, length).TrimEnd() + ellipsis, font) <= maxWidth)
                    low = length;
                else
                    high = length - 1;
            }
            return text.Substring(0, low).TrimEnd() + ellipsis;
        }

        private static int Measure(string text, Font font) =>
            TextRenderer.MeasureText(text, font, Size.Empty, MenuTheme.SingleLine).Width;

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
            // The tooltip holds on to the controls it was set for.
            toolTip.RemoveAll();
            contextMenuStrip.Items.Clear();
            foreach (var item in oldItems)
                item.Dispose();
        }

        /// <summary>Converts a size at 96 DPI and 100 % to the menu's size (e.g. 173 % at 150 % display scaling and 115 %).</summary>
        private int Scale(int logicalPixels) => MenuTheme.Scale(logicalPixels, scaleFactor);

        private Label CreateLabel(string text, Font font, Color? color = null)
        {
            return new Label
            {
                Text = text,
                Font = font,
                ForeColor = color ?? MenuTheme.Text,
                AutoSize = true,
                UseMnemonic = false,
                Margin = Padding.Empty,
                Anchor = AnchorStyles.Left,
            };
        }

        private void AddHeader()
        {
            var label = CreateLabel($"{Application.ProductName} (v{Application.ProductVersion})", fonts.Header);
            label.Name = "header";
            // Spacing inside the controls hosted directly in the menu: the menu's layout does not reliably honor margins.
            label.Padding = new Padding(Scale(10), Scale(8), Scale(12), Scale(8));
            label.BackColor = contextMenuStrip.BackColor;

            contextMenuStrip.Items.Add(new ToolStripControlHost(label) { Margin = Padding.Empty, ControlAlign = ContentAlignment.MiddleLeft });

            AddSeparator();
        }

        /// <returns>false if there are no apps.</returns>
        private bool AddAppItems(List<AppInfo> appList)
        {
            if (appList.Count == 0)
                return false;

            // No transparent BackColor here: the checkboxes and sliders fill their background with their parent's
            // BackColor, and a transparent one comes out black. The table takes the menu's color instead.
            var table = new MenuTable
            {
                BackColor = contextMenuStrip.BackColor,
                ColumnCount = 4,
                RowCount = appList.Count,
                Name = AppTableName,
                Padding = new Padding(0, Scale(4), Scale(8), Scale(4)),
                Margin = Padding.Empty,
            };
            for (int column = 0; column < table.ColumnCount; column++)
                table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            for (int row = 0; row < appList.Count; row++)
            {
                table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                AddAppRow(table, row, appList[row]);
            }

            // The rows span the menu (see FitFullWidthItems): checkbox, icon and name on the left, the slider on the
            // right, the name column takes the remaining width.
            table.ColumnStyles[2] = new ColumnStyle(SizeType.Percent, 100);

            contextMenuStrip.Items.Add(new FullWidthControlHost(table));
            return true;
        }

        /// <summary>One row: checkbox, icon, name and the slider for the app's share of the master volume.</summary>
        private void AddAppRow(TableLayoutPanel table, int row, AppInfo app)
        {
            var appName = app.Name;
            bool enabled = preferences.IsEnabled(appName);

            var checkbox = new MenuCheckBox
            {
                Text = string.Empty,
                Name = appName,
                Checked = enabled,
                Font = fonts.Body,
                ScaleFactor = scaleFactor,
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

            var title = Title(app);
            var label = CreateLabel(Ellipsize(RowText(appName, title), fonts.Body, Scale(MaxNameWidth)), fonts.Body);
            label.Name = LabelName(appName);
            label.Margin = new Padding(0, 0, Scale(12), 0);
            var tip = RowToolTip(appName, title);
            toolTip.SetToolTip(label, tip);
            toolTip.SetToolTip(icon, tip);

            var slider = new RatioSlider
            {
                Name = SliderName(appName),
                Font = fonts.Small,
                ScaleFactor = scaleFactor,
                Width = Scale(SliderWidth),
                Anchor = AnchorStyles.Right,
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
            slider.ValueChangedByUser += (sender, value) =>
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

        internal const string AppTableName = "apps";

        internal static string IconName(string appName) => "icon:" + appName;

        internal static string LabelName(string appName) => "name:" + appName;

        internal static string SliderName(string appName) => "ratio:" + appName;

        private void AddAutoRunItem()
        {
            var checkbox = new MenuCheckBox
            {
                Text = "Start with Windows",
                Name = "autostart",
                Font = fonts.Body,
                ScaleFactor = scaleFactor,
                Checked = autoStart.IsEnabled,
                BackColor = contextMenuStrip.BackColor,
            };
            checkbox.CheckedChanged += (sender, e) => ChangeInMenu(() => OnAutoRunCheckBoxChanged(checkbox));

            contextMenuStrip.Items.Add(new ToolStripControlHost(checkbox) { Margin = Padding.Empty, ControlAlign = ContentAlignment.MiddleLeft });
        }

        internal const string ScaleSliderName = "scale";
        internal const string ScaleBarName = "scale bar";

        /// <summary>The scale bar is drawn this much smaller than the rest of the menu.</summary>
        private const float ScaleBarSize = 0.8f;

        /// <summary>
        /// The menu size: a slider over <see cref="MenuScale.Steps"/> between a small and a large "A", spanning the
        /// menu. The slider marks the chosen step while it is dragged; the menu is rebuilt in the new size when it is
        /// released (a click or the mouse wheel applies at once), since a rebuild replaces the slider under the mouse.
        /// </summary>
        private void AddScaleItem()
        {
            float barScale = scaleFactor * ScaleBarSize;

            // Clicking the letters moves one step.
            var smallIcon = CreateLabel("A", fonts.ScaleIconSmall);
            var largeIcon = CreateLabel("A", fonts.ScaleIconLarge);
            smallIcon.Cursor = largeIcon.Cursor = Cursors.Hand;

            var slider = new MenuSlider
            {
                Name = ScaleSliderName,
                Font = fonts.Caption,
                ScaleFactor = barScale,
                Minimum = 0,
                Maximum = MenuScale.Steps.Length - 1,
                TickLabels = MenuScale.Steps.Select(s => $"{s}%").ToArray(),
                Value = MenuScale.IndexOf(scale.Percent),
                Width = Scale(ScaleSliderMinWidth),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            };

            void Apply()
            {
                int chosen = MenuScale.Steps[slider.Value];
                if (chosen == scale.Percent)
                    return;

                scale.SetPercent(chosen);
                // Not from within the slider's own event, which the rebuild would dispose: the deferred rebuild
                // runs right after it (and after the mouse button was released).
                deferredRebuildTimer.Start();
            }

            slider.ValueChangedByUser += (sender, index) =>
            {
                if (!slider.Dragging)
                    Apply();
            };
            slider.MouseDown += (sender, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    draggingSlider = true;
            };
            slider.MouseUp += (sender, e) =>
            {
                if (draggingSlider)
                    EndDrag();
                Apply();
            };
            slider.MouseCaptureChanged += (sender, e) =>
            {
                if (slider.Capture)
                    return;
                if (draggingSlider)
                    EndDrag();
                Apply();
            };
            smallIcon.Click += (sender, e) => slider.ChangeValue(slider.Value - 1);
            largeIcon.Click += (sender, e) => slider.ChangeValue(slider.Value + 1);

            // The letters line up with the track, above the step labels.
            int trackCenter = MenuTheme.Scale(MenuSlider.TrackCenter, barScale);
            int side = Scale(12);
            smallIcon.Anchor = largeIcon.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            smallIcon.Margin = new Padding(side, Math.Max(0, trackCenter - smallIcon.PreferredHeight / 2), Scale(2), 0);
            largeIcon.Margin = new Padding(Scale(2), Math.Max(0, trackCenter - largeIcon.PreferredHeight / 2), side, 0);

            var bar = new MenuTable
            {
                Name = ScaleBarName,
                BackColor = contextMenuStrip.BackColor,
                ColumnCount = 3,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = new Padding(0, Scale(4), 0, Scale(2)),
            };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bar.Controls.Add(smallIcon, 0, 0);
            bar.Controls.Add(slider, 1, 0);
            bar.Controls.Add(largeIcon, 2, 0);

            // Its width follows the menu (see FitFullWidthItems); the slider keeps at least its minimum width.
            contextMenuStrip.Items.Add(new FullWidthControlHost(bar));
        }

        private void OnAutoRunCheckBoxChanged(MenuCheckBox checkbox)
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

        /// <summary>The button spans the menu (see <see cref="FitFullWidthItems"/>).</summary>
        private void AddExitItem()
        {
            var button = new Button
            {
                Text = "EXIT",
                Name = "exit",
                Font = fonts.Button,
                ForeColor = Color.White,
                BackColor = MenuTheme.Primary,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                UseMnemonic = false,
                Cursor = Cursors.Hand,
                AutoSize = false,
                Dock = DockStyle.Fill,
                Height = Scale(ExitButtonHeight),
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = MenuTheme.PrimaryHover;
            button.FlatAppearance.MouseDownBackColor = MenuTheme.PrimaryHover;
            button.Click += (sender, e) => ExitRequested?.Invoke(this, EventArgs.Empty);

            int spacing = Scale(8);
            var panel = new Panel
            {
                Name = "exit panel",
                BackColor = contextMenuStrip.BackColor,
                Padding = new Padding(spacing),
                Margin = Padding.Empty,
                Width = Scale(ExitButtonWidth) + 2 * spacing,
                Height = button.Height + 2 * spacing,
            };
            // Its natural size (a panel does not measure a docked button).
            panel.MinimumSize = panel.Size;
            panel.Controls.Add(button);

            contextMenuStrip.Items.Add(new FullWidthControlHost(panel));
        }

        /// <summary>
        /// Makes the scale bar, the app rows and the Exit button as wide as the menu: the widest other item, or the
        /// widest minimum width of these.
        /// </summary>
        private void FitFullWidthItems()
        {
            var items = contextMenuStrip.Items.Cast<ToolStripItem>().ToList();
            var fullWidth = items.OfType<FullWidthControlHost>().ToList();
            // The size each control needs for its content (at least its minimum size).
            var natural = fullWidth.ToDictionary(host => host, host =>
            {
                var preferred = host.Control.GetPreferredSize(Size.Empty);
                return new Size(
                    Math.Max(preferred.Width, host.Control.MinimumSize.Width),
                    Math.Max(preferred.Height, host.Control.MinimumSize.Height));
            });
            // Separators take the menu's width, they do not set it.
            int width = items.Except(fullWidth)
                .Where(item => !(item is ToolStripSeparator))
                .Select(item => item.GetPreferredSize(Size.Empty).Width + item.Margin.Horizontal)
                .Concat(natural.Values.Select(size => size.Width))
                .DefaultIfEmpty(0)
                .Max();

            foreach (var host in fullWidth)
            {
                var size = new Size(width, natural[host].Height);
                host.Control.MinimumSize = size;
                host.Control.Size = size;
                host.Size = size;
            }
        }

        private void AddSeparator()
        {
            contextMenuStrip.Items.Add(new MenuSeparator
            {
                Margin = new Padding(0, Scale(2), 0, Scale(2)),
            });
        }
    }

    /// <summary>
    /// Hosts a control that spans the menu: the menu gives all items its width, but a plain host only aligns its
    /// control in it.
    /// </summary>
    sealed class FullWidthControlHost : ToolStripControlHost
    {
        public FullWidthControlHost(Control control) : base(control)
        {
            AutoSize = false;
            Margin = Padding.Empty;
            // The item always takes the control's height (e.g. when a MenuTable grew to its content), so the control
            // cannot cover the item below.
            control.SizeChanged += (sender, e) =>
            {
                if (Owner != null && Height != control.Height + Padding.Vertical)
                    Height = control.Height + Padding.Vertical;
            };
        }

        protected override void OnBoundsChanged()
        {
            if (Control != null && !Control.IsDisposed)
                Control.Width = Math.Max(Control.MinimumSize.Width, Width - Padding.Horizontal);
            base.OnBoundsChanged();
        }
    }
}
