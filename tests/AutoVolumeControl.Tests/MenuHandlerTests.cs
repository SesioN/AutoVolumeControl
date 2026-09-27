using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;

namespace AutoVolumeControl.Tests
{
    public class MenuHandlerTests : IDisposable
    {
        private readonly TestRegistryKey runKey = new TestRegistryKey();
        private readonly InMemorySettingsStore store = new InMemorySettingsStore();
        private readonly AppPreferences preferences;
        private readonly Apps apps = new Apps();
        private readonly AutoStart autoStart;
        private readonly AppIconCache icons = new AppIconCache();
        private readonly MenuScale scale;
        private bool mouseButtonDown;
        private readonly System.Collections.Generic.Dictionary<string, string> windowTitles = new System.Collections.Generic.Dictionary<string, string>();
        private int titleLookups;

        public MenuHandlerTests()
        {
            preferences = new AppPreferences(store);
            scale = new MenuScale(store);
            autoStart = new AutoStart("AutoVolumeControl", @"C:\Apps\AutoVolumeControl.exe", runKey.Path);
        }

        public void Dispose()
        {
            icons.Dispose();
            runKey.Dispose();
        }

        private MenuHandler CreateMenu(ContextMenuStrip strip, Func<Task> refresh = null)
        {
            return new MenuHandler(strip, preferences, apps, autoStart, refresh ?? (() => Task.CompletedTask), icons, scale, () => mouseButtonDown, FindTitle);
        }

        private string FindTitle(AppInfo app)
        {
            titleLookups++;
            return windowTitles.TryGetValue(app.Name, out var title) ? title : null;
        }

        private static T FindControl<T>(ContextMenuStrip strip, string name) where T : Control
        {
            return strip.Items.OfType<ToolStripControlHost>()
                .SelectMany(h => new[] { h.Control }.Concat(h.Control.Controls.Cast<Control>()))
                .OfType<T>()
                .SingleOrDefault(c => c.Name == name);
        }

        private static T FindNested<T>(ContextMenuStrip strip, string name) where T : Control
        {
            return strip.Items.OfType<ToolStripControlHost>()
                .SelectMany(h => Descendants(h.Control))
                .OfType<T>()
                .SingleOrDefault(c => c.Name == name);
        }

        private static System.Collections.Generic.IEnumerable<Control> Descendants(Control control)
        {
            yield return control;
            foreach (Control child in control.Controls)
                foreach (var descendant in Descendants(child))
                    yield return descendant;
        }

        private static RatioSlider FindSlider(ContextMenuStrip strip, string app) => FindControl<RatioSlider>(strip, MenuHandler.SliderName(app));

        private static MenuSlider FindScaleSlider(ContextMenuStrip strip) => FindNested<MenuSlider>(strip, MenuHandler.ScaleSliderName);

        private static void Invoke(Control control, string method, EventArgs args)
        {
            typeof(Control).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(control, new object[] { args });
        }

        private static ToolStripDropDownClosingEventArgs RaiseClosing(ContextMenuStrip strip, ToolStripDropDownCloseReason reason)
        {
            var args = new ToolStripDropDownClosingEventArgs(reason);
            typeof(ToolStripDropDown).GetMethod("OnClosing", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(strip, new object[] { args });
            return args;
        }

        /// <summary>Changes the slider's value the way dragging does (setting Value does not raise the event).</summary>
        private static void Drag(MenuSlider slider, int value) => slider.ChangeValue(value);

        private static MenuCheckBox FindCheckbox(ContextMenuStrip strip, string name)
        {
            return strip.Items.OfType<ToolStripControlHost>()
                .SelectMany(h => new[] { h.Control }.Concat(h.Control.Controls.Cast<Control>()))
                .OfType<MenuCheckBox>()
                .SingleOrDefault(c => c.Name == name);
        }

        private static Button FindExitButton(ContextMenuStrip strip)
        {
            return FindControl<Button>(strip, "exit");
        }

        /// <summary>
        /// Raises Opening like WinForms does: when the menu has no items at the time of the click,
        /// the event arrives already cancelled.
        /// </summary>
        private static CancelEventArgs RaiseOpening(ContextMenuStrip strip)
        {
            var args = new CancelEventArgs(strip.Items.Count == 0);
            var onOpening = typeof(ToolStripDropDown).GetMethod("OnOpening", BindingFlags.Instance | BindingFlags.NonPublic);
            onOpening.Invoke(strip, new object[] { args });
            return args;
        }

        [Fact]
        public void Generate_ShowsOneCheckboxPerApp_WithStoredState()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome", "spotify" });
                store.Values["spotify"] = "False";

                Assert.True(CreateMenu(strip).Generate());

                Assert.True(FindCheckbox(strip, "chrome").Checked);
                Assert.False(FindCheckbox(strip, "spotify").Checked);
                Assert.Equal("", FindCheckbox(strip, "chrome").Text);
                Assert.Equal("chrome", FindControl<Label>(strip, MenuHandler.LabelName("chrome")).Text);
            });
        }

        [Fact]
        public void Generate_WithoutApps_StillShowsAutostartAndExit()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();

                CreateMenu(strip).Generate();

                Assert.NotNull(FindCheckbox(strip, "autostart"));
                Assert.NotNull(FindExitButton(strip));
                Assert.Null(FindControl<TableLayoutPanel>(strip, MenuHandler.AppTableName));
            });
        }

        [Fact]
        public void TogglingAppCheckbox_StoresPreference()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome" });
                CreateMenu(strip).Generate();

                FindCheckbox(strip, "chrome").Checked = false;
                Assert.Equal("False", store.Values["chrome"]);

                FindCheckbox(strip, "chrome").Checked = true;
                Assert.Equal("True", store.Values["chrome"]);
            });
        }

        [Fact]
        public void Generate_WhenNothingChanged_KeepsTheExistingItems()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome" });
                var menu = CreateMenu(strip);
                menu.Generate();
                var before = strip.Items.Cast<ToolStripItem>().ToList();

                Assert.False(menu.Generate());
                Assert.Equal(before, strip.Items.Cast<ToolStripItem>().ToList());
            });
        }

        [Fact]
        public void Generate_WhenAppsChanged_DisposesTheOldItems()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome" });
                var menu = CreateMenu(strip);
                menu.Generate();
                var oldItems = strip.Items.Cast<ToolStripItem>().ToList();
                var oldCheckbox = FindCheckbox(strip, "chrome");
                // ToolStripItem.IsDisposed is not reliable on .NET Framework, so count Disposed events.
                int disposedItems = 0;
                foreach (var item in oldItems)
                    item.Disposed += (s, e) => disposedItems++;

                apps.Update(new[] { "chrome", "spotify" });
                Assert.True(menu.Generate());

                Assert.Equal(oldItems.Count, disposedItems);
                Assert.True(oldCheckbox.IsDisposed);
                Assert.NotNull(FindCheckbox(strip, "spotify"));
            });
        }

        [Fact]
        public void Generate_WhenPreferenceChangedElsewhere_Rebuilds()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome" });
                var menu = CreateMenu(strip);
                menu.Generate();

                store.Values["chrome"] = "False";

                Assert.True(menu.Generate());
                Assert.False(FindCheckbox(strip, "chrome").Checked);
            });
        }

        [Fact]
        public void RepeatedRebuilds_DoNotAccumulateItems()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                var menu = CreateMenu(strip);
                apps.Update(new[] { "a" });
                menu.Generate();
                int count = strip.Items.Count;

                for (int i = 0; i < 50; i++)
                {
                    apps.Update(new[] { i % 2 == 0 ? "b" : "a" });
                    menu.Generate();
                }

                Assert.Equal(count, strip.Items.Count);
            });
        }

        [Fact]
        public void AutostartCheckbox_ReflectsAndChangesRegistry()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                CreateMenu(strip).Generate();
                var checkbox = FindCheckbox(strip, "autostart");
                Assert.False(checkbox.Checked);

                checkbox.Checked = true;
                Assert.True(autoStart.IsEnabled);

                checkbox.Checked = false;
                Assert.False(autoStart.IsEnabled);
            });
        }

        [Fact]
        public void ExitButton_RaisesExitRequested()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                var menu = CreateMenu(strip);
                int exits = 0;
                menu.ExitRequested += (s, e) => exits++;
                menu.Generate();

                FindExitButton(strip).PerformClick();

                Assert.Equal(1, exits);
            });
        }

        [Fact]
        public void FirstOpening_OfAnEmptyMenu_IsNotCancelled()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                CreateMenu(strip);
                Assert.Empty(strip.Items.Cast<ToolStripItem>());

                var args = RaiseOpening(strip);

                Assert.False(args.Cancel);
                Assert.NotEmpty(strip.Items.Cast<ToolStripItem>());
            });
        }

        [Fact]
        public void EmptyMenu_OpensOnTheFirstClickForReal()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                CreateMenu(strip);

                strip.Show(0, 0);
                bool visible = strip.Visible;
                strip.Close();

                Assert.True(visible);
            });
        }

        [Fact]
        public void Opening_RefreshesAppsBeforeBuilding()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                CreateMenu(strip, () => Task.Run(() => apps.Update(new[] { "vlc" })));

                RaiseOpening(strip);

                Assert.NotNull(FindCheckbox(strip, "vlc"));
            });
        }

        [Fact]
        public void Opening_WithSlowRefresh_ShowsCachedListWithoutWaitingForever()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome" });
                using var never = new ManualResetEventSlim();
                CreateMenu(strip, () => Task.Run(() => never.Wait(TimeSpan.FromSeconds(10))));

                var watch = System.Diagnostics.Stopwatch.StartNew();
                RaiseOpening(strip);

                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
                Assert.NotNull(FindCheckbox(strip, "chrome"));
            });
        }

        [Fact]
        public void Opening_WithFailingRefresh_StillBuildsMenu()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome" });
                CreateMenu(strip, () => Task.Run(() => throw new InvalidOperationException("no device")));

                RaiseOpening(strip);

                Assert.NotNull(FindCheckbox(strip, "chrome"));
            });
        }

        // ---- icons ----

        [Fact]
        public void EachRow_ShowsAnIcon_AlsoWithoutKnownExecutable()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                var notepad = System.IO.Path.Combine(Environment.SystemDirectory, "notepad.exe");
                apps.Update(new[] { new AppInfo("notepad", notepad), new AppInfo("unknown") });

                CreateMenu(strip).Generate();

                var known = FindControl<PictureBox>(strip, MenuHandler.IconName("notepad"));
                var unknown = FindControl<PictureBox>(strip, MenuHandler.IconName("unknown"));
                Assert.NotNull(known.Image);
                Assert.NotNull(unknown.Image);
                var small = System.Windows.Forms.SystemInformation.SmallIconSize;
                Assert.Equal(new System.Drawing.Size((int)Math.Round(small.Width * MenuHandler.BaseScale), (int)Math.Round(small.Height * MenuHandler.BaseScale)), known.Image.Size);
                Assert.NotSame(known.Image, unknown.Image);
            });
        }

        [Fact]
        public void Generate_WhenAnExecutableBecomesKnown_Rebuilds()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { new AppInfo("notepad") });
                var menu = CreateMenu(strip);
                menu.Generate();
                var generic = FindControl<PictureBox>(strip, MenuHandler.IconName("notepad")).Image;

                apps.Update(new[] { new AppInfo("notepad", System.IO.Path.Combine(Environment.SystemDirectory, "notepad.exe")) });

                Assert.True(menu.Generate());
                Assert.NotSame(generic, FindControl<PictureBox>(strip, MenuHandler.IconName("notepad")).Image);
            });
        }

        [Fact]
        public void ClickingIconOrName_TogglesTheApp()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome" });
                CreateMenu(strip).Generate();

                InvokeOnClick(FindControl<Label>(strip, MenuHandler.LabelName("chrome")));
                Assert.False(FindCheckbox(strip, "chrome").Checked);
                Assert.Equal("False", store.Values["chrome"]);

                InvokeOnClick(FindControl<PictureBox>(strip, MenuHandler.IconName("chrome")));
                Assert.True(FindCheckbox(strip, "chrome").Checked);
                Assert.Equal("True", store.Values["chrome"]);
            });
        }

        private static void InvokeOnClick(Control control)
        {
            typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(control, new object[] { EventArgs.Empty });
        }

        // ---- ratio slider ----

        [Fact]
        public void Slider_ShowsTheStoredRatio()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome", "spotify" });
                store.Ratios["spotify"] = "70";

                CreateMenu(strip).Generate();

                Assert.Equal(100, FindSlider(strip, "chrome").Value);
                Assert.Equal(70, FindSlider(strip, "spotify").Value);
                Assert.Equal(0, FindSlider(strip, "spotify").Minimum);
                Assert.Equal(100, FindSlider(strip, "spotify").Maximum);
                Assert.Equal("%", FindSlider(strip, "spotify").ValueSuffix);
                Assert.True(FindSlider(strip, "spotify").ShowValue);
            });
        }

        [Fact]
        public void MovingTheSlider_StoresTheRatio_WithoutRebuildingTheMenu()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "spotify" });
                var menu = CreateMenu(strip);
                menu.Generate();
                int changes = 0;
                preferences.Changed += (s, e) => changes++;

                var slider = FindSlider(strip, "spotify");
                Drag(slider, 60);
                Drag(slider, 45);

                Assert.Equal("45", store.Ratios["spotify"]);
                Assert.Equal(0.45f, preferences.GetRatio("spotify"), 3);
                Assert.Equal(2, changes);
                // The menu already shows the new value; rebuilding would destroy the slider being dragged.
                Assert.False(menu.Generate());
                Assert.Same(slider, FindSlider(strip, "spotify"));
            });
        }

        [Fact]
        public void Slider_IsDisabledWhileTheAppIsUnchecked()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome", "spotify" });
                store.Values["spotify"] = "False";
                CreateMenu(strip).Generate();

                Assert.True(FindSlider(strip, "chrome").Enabled);
                Assert.False(FindSlider(strip, "spotify").Enabled);

                FindCheckbox(strip, "spotify").Checked = true;
                Assert.True(FindSlider(strip, "spotify").Enabled);

                FindCheckbox(strip, "chrome").Checked = false;
                Assert.False(FindSlider(strip, "chrome").Enabled);
            });
        }

        [Fact]
        public void Generate_WhenRatioChangedElsewhere_Rebuilds()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome" });
                var menu = CreateMenu(strip);
                menu.Generate();

                store.Ratios["chrome"] = "30";

                Assert.True(menu.Generate());
                Assert.Equal(30, FindSlider(strip, "chrome").Value);
            });
        }

        // ---- rebuild while the mouse button is down ----

        [Fact]
        public void Rebuild_WaitsWhileTheMouseButtonIsDownOnTheOpenMenu()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "spotify" });
                var menu = CreateMenu(strip);
                strip.Show(0, 0);
                try
                {
                    var slider = FindSlider(strip, "spotify");
                    mouseButtonDown = true;
                    apps.Update(new[] { "spotify", "chrome" });

                    Assert.False(menu.Generate());
                    Assert.True(menu.RebuildDeferred);
                    Assert.Same(slider, FindSlider(strip, "spotify"));
                    Assert.False(slider.IsDisposed);

                    // Still held: the poll keeps waiting.
                    Sta.PumpUntil(() => false, MenuHandler.DeferredRebuildPollMs * 4);
                    Assert.True(menu.RebuildDeferred);
                    Assert.False(slider.IsDisposed);

                    // Released anywhere (no mouse-up event reaches the menu, e.g. over a separator or outside):
                    // the poll runs the deferred rebuild.
                    mouseButtonDown = false;

                    Assert.True(Sta.PumpUntil(() => FindCheckbox(strip, "chrome") != null));
                    Assert.False(menu.RebuildDeferred);
                    Assert.True(slider.IsDisposed);
                }
                finally
                {
                    strip.Close();
                }
            });
        }

        [Fact]
        public void Rebuild_IsNotDeferredWhileTheMenuIsClosed()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                var menu = CreateMenu(strip);
                mouseButtonDown = true;
                apps.Update(new[] { "chrome" });

                Assert.True(menu.Generate());
                Assert.NotNull(FindCheckbox(strip, "chrome"));
            });
        }

        [Fact]
        public void Opening_AlwaysBuilds_EvenWithTheMouseButtonDown()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                CreateMenu(strip);
                mouseButtonDown = true;
                apps.Update(new[] { "chrome" });

                RaiseOpening(strip);

                Assert.NotNull(FindCheckbox(strip, "chrome"));
            });
        }

        // ---- dragging ----

        [Fact]
        public void Dragging_TakesEffectImmediately_ButWritesOnlyOnRelease()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "spotify" });
                CreateMenu(strip).Generate();
                int changes = 0;
                preferences.Changed += (s, e) => changes++;
                var slider = FindSlider(strip, "spotify");

                Invoke(slider, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 0, 20, 0));
                Drag(slider, 30);
                Drag(slider, 20);

                Assert.Equal(20, preferences.GetRatioPercent("spotify"));
                Assert.True(changes >= 2);
                Assert.False(store.Ratios.ContainsKey("spotify"));

                Invoke(slider, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 0, 20, 0));

                Assert.Equal("20", store.Ratios["spotify"]);
                Assert.Equal(20, preferences.GetRatioPercent("spotify"));
            });
        }

        [Fact]
        public void Closing_IsCancelledOnlyWhileASliderIsDragged()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "spotify" });
                var menu = CreateMenu(strip);
                menu.Generate();
                var slider = FindSlider(strip, "spotify");
                mouseButtonDown = true;

                Assert.False(RaiseClosing(strip, ToolStripDropDownCloseReason.AppClicked).Cancel);

                Invoke(slider, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 0, 20, 0));
                Assert.True(menu.DraggingSlider);
                Assert.True(RaiseClosing(strip, ToolStripDropDownCloseReason.AppClicked).Cancel);
                Assert.False(RaiseClosing(strip, ToolStripDropDownCloseReason.Keyboard).Cancel);
                Assert.False(RaiseClosing(strip, ToolStripDropDownCloseReason.AppFocusChange).Cancel);

                mouseButtonDown = false;
                Assert.False(RaiseClosing(strip, ToolStripDropDownCloseReason.AppClicked).Cancel);
            });
        }

        [Fact]
        public void LostMouseUp_DoesNotKeepTheMenuFromClosing()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "spotify" });
                var menu = CreateMenu(strip);
                menu.Generate();
                var slider = FindSlider(strip, "spotify");
                mouseButtonDown = true;
                Invoke(slider, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 0, 20, 0));
                Drag(slider, 40);

                // E.g. Escape while the button is still down: no mouse-up ever reaches the slider.
                RaiseOpening(strip);

                Assert.False(menu.DraggingSlider);
                Assert.Equal("40", store.Ratios["spotify"]);
                Assert.False(RaiseClosing(strip, ToolStripDropDownCloseReason.AppClicked).Cancel);
            });
        }

        [Fact]
        public void MouseWheelUp_RaisesTheRatio()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "spotify" });
                store.Ratios["spotify"] = "50";
                CreateMenu(strip).Generate();
                var slider = FindSlider(strip, "spotify");

                Invoke(slider, "OnMouseWheel", new HandledMouseEventArgs(MouseButtons.None, 0, 0, 0, 120));
                Assert.Equal(50 + RatioSlider.WheelStepPercent, slider.Value);
                Assert.Equal("55", store.Ratios["spotify"]);

                Invoke(slider, "OnMouseWheel", new HandledMouseEventArgs(MouseButtons.None, 0, 0, 0, -240));
                Assert.Equal("50", store.Ratios["spotify"]);
            });
        }

        [Fact]
        public void MouseWheel_DoesNothingOnADisabledSlider()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "spotify" });
                store.Values["spotify"] = "False";
                CreateMenu(strip).Generate();

                Invoke(FindSlider(strip, "spotify"), "OnMouseWheel", new HandledMouseEventArgs(MouseButtons.None, 0, 0, 0, 120));

                Assert.False(store.Ratios.ContainsKey("spotify"));
            });
        }

        // ---- layout ----

        [Fact]
        public void AppRows_UseTheMenuBackground_NotTransparent()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip { BackColor = System.Drawing.Color.White };
                apps.Update(new[] { "chrome" });
                CreateMenu(strip).Generate();

                // The checkboxes and sliders fill their background with the parent's color; transparent renders black.
                var table = FindControl<TableLayoutPanel>(strip, MenuHandler.AppTableName);
                Assert.Equal(System.Drawing.Color.White, table.BackColor);
                Assert.Equal(System.Drawing.Color.White, FindControl<Label>(strip, MenuHandler.LabelName("chrome")).Parent.BackColor);
            });
        }

        [Fact]
        public void ExitButton_IsAtLeastAsWideAsTheAppRows()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "an-app-with-a-rather-long-executable-name" });
                CreateMenu(strip).Generate();

                var table = FindControl<TableLayoutPanel>(strip, MenuHandler.AppTableName);
                var exitHost = strip.Items.OfType<ToolStripControlHost>().Single(h => h.Control.Controls.Contains(FindExitButton(strip)));
                Assert.True(exitHost.Width >= table.GetPreferredSize(System.Drawing.Size.Empty).Width);
                Assert.Equal(DockStyle.Fill, FindExitButton(strip).Dock);
            });
        }

        [Fact]
        public void AutostartToggle_DoesNotRebuildTheMenu()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome" });
                var menu = CreateMenu(strip);
                menu.Generate();

                FindCheckbox(strip, "autostart").Checked = true;

                Assert.False(menu.Generate());
            });
        }

        [Fact]
        public void ChangeInMenu_DoesNotHideAChangeMadeElsewhere()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome", "spotify" });
                var menu = CreateMenu(strip);
                menu.Generate();

                store.Values["spotify"] = "False";
                FindCheckbox(strip, "chrome").Checked = false;

                Assert.True(menu.Generate());
                Assert.False(FindCheckbox(strip, "spotify").Checked);
            });
        }

        // ---- menu scale ----

        [Fact]
        public void ScaleSection_ShowsTheStoredScale()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                CreateMenu(strip).Generate();

                var slider = FindScaleSlider(strip);
                Assert.Equal(1, slider.Value);
                Assert.Equal(0, slider.Minimum);
                Assert.Equal(3, slider.Maximum);
                Assert.Equal(new[] { "85%", "100%", "115%", "130%" }, slider.TickLabels);
            });
        }

        [Fact]
        public void ScaleBar_IsBelowTheTitle_AboveTheApps()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome" });
                CreateMenu(strip).Generate();

                var controls = strip.Items.Cast<ToolStripItem>()
                    .Select(item => item is ToolStripControlHost host ? host.Control.Name : "---")
                    .ToList();
                Assert.Equal(new[] { "header", "---", MenuHandler.ScaleBarName, "---", MenuHandler.AppTableName, "---", "autostart", "---", "exit panel" }, controls);
                Assert.Null(FindNested<Label>(strip, "scale caption"));
            });
        }

        [Fact]
        public void WithoutApps_ThereIsNoDoubleSeparator()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                CreateMenu(strip).Generate();

                var items = strip.Items.Cast<ToolStripItem>().ToList();
                for (int i = 1; i < items.Count; i++)
                    Assert.False(items[i] is ToolStripSeparator && items[i - 1] is ToolStripSeparator);
            });
        }

        [Fact]
        public void ScaleBar_SpansTheMenu_LikeTheExitButton()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "an-app-with-a-rather-long-executable-name" });
                CreateMenu(strip).Generate();

                var bar = FindNested<TableLayoutPanel>(strip, MenuHandler.ScaleBarName);
                var exitPanel = FindExitButton(strip).Parent;
                var table = FindControl<TableLayoutPanel>(strip, MenuHandler.AppTableName);
                Assert.Equal(exitPanel.Width, bar.Width);
                Assert.True(bar.Width >= table.GetPreferredSize(System.Drawing.Size.Empty).Width);
                // The slider takes the space between the letters.
                var slider = FindScaleSlider(strip);
                Assert.Equal(AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, slider.Anchor);
            });
        }

        [Fact]
        public void AppRows_SpanTheMenu_WithTheSliderOnTheRight()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome", "an-app-with-a-rather-long-executable-name" });
                CreateMenu(strip).Generate();

                var table = FindControl<TableLayoutPanel>(strip, MenuHandler.AppTableName);
                var bar = FindNested<TableLayoutPanel>(strip, MenuHandler.ScaleBarName);
                Assert.Equal(bar.Width, table.Width);
                Assert.Equal(FindExitButton(strip).Parent.Width, table.Width);
                Assert.Equal(SizeType.Percent, table.ColumnStyles[2].SizeType);
                Assert.Equal(AnchorStyles.Right, FindSlider(strip, "chrome").Anchor);
                Assert.Equal(AnchorStyles.Left, FindControl<Label>(strip, MenuHandler.LabelName("chrome")).Anchor);
                Assert.Equal(AnchorStyles.Left, FindCheckbox(strip, "chrome").Anchor);
            });
        }

        [Theory]
        [InlineData(85)]
        [InlineData(100)]
        [InlineData(115)]
        [InlineData(130)]
        public void EveryItem_FitsInItsPlace_SoNothingCoversASeparator(int percent)
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome", "spotify", "discord" });
                scale.SetPercent(percent);
                CreateMenu(strip).Generate();
                strip.Show(0, 0);
                try
                {
                    Sta.PumpUntil(() => false, 100);
                    foreach (var host in strip.Items.OfType<ToolStripControlHost>())
                    {
                        var control = host.Control;
                        Assert.True(control.Height <= host.Height, $"{control.Name}: {control.Height} is taller than its item ({host.Height})");
                        Assert.True(control.Top >= host.Bounds.Top && control.Bottom <= host.Bounds.Bottom,
                            $"{control.Name}: {control.Bounds} is outside its item {host.Bounds}");
                        foreach (Control child in control.Controls)
                            Assert.True(child.Bottom <= control.ClientSize.Height, $"{child.Name}: bottom {child.Bottom} is below {control.Name} ({control.ClientSize.Height})");
                    }
                }
                finally
                {
                    strip.Close();
                }
            });
        }

        [Fact]
        public void Rebuilds_DoNotMakeTheMenuWider()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome", "spotify" });
                var menu = CreateMenu(strip);
                strip.Show(0, 0);
                try
                {
                    int width = strip.Width;
                    foreach (var percent in new[] { 130, 85, 115, 100, 130, 100 })
                    {
                        scale.SetPercent(percent);
                        Assert.True(menu.Generate());
                    }

                    // Back at 100 %: exactly as wide as the first time.
                    Assert.Equal(width, strip.Width);

                    scale.SetPercent(85);
                    menu.Generate();
                    Assert.True(strip.Width < width, $"At 85 % the menu ({strip.Width}) is not narrower than at 100 % ({width})");
                }
                finally
                {
                    strip.Close();
                }
            });
        }

        [Fact]
        public void Separators_DoNotAskForTheMenusWidth()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                CreateMenu(strip).Generate();
                strip.Width = 2000;

                Assert.All(strip.Items.OfType<ToolStripSeparator>(), separator =>
                    Assert.True(separator.GetPreferredSize(System.Drawing.Size.Empty).Width < 10));
            });
        }

        [Fact]
        public void ScaleBar_IsSmallerThanTheRestOfTheMenu()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome" });
                CreateMenu(strip).Generate();

                Assert.True(FindScaleSlider(strip).ScaleFactor < FindSlider(strip, "chrome").ScaleFactor);
                Assert.True(FindScaleSlider(strip).Font.Size < FindSlider(strip, "chrome").Font.Size);
            });
        }

        [Fact]
        public void LargerScale_EnlargesTheWholeMenu()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { new AppInfo("chrome") });
                var menu = CreateMenu(strip);
                menu.Generate();
                float dpi = strip.DeviceDpi / 96f;
                // The default of 100 % is the base size.
                Assert.Equal(100, scale.Percent);
                Assert.Equal(dpi * MenuHandler.BaseScale, menu.ScaleFactor, 3);
                var before = Measure(strip);

                scale.SetPercent(130);
                Assert.True(menu.Generate());

                Assert.Equal(dpi * MenuHandler.BaseScale * 1.3f, menu.ScaleFactor, 3);
                var after = Measure(strip);
                for (int i = 0; i < before.Length; i++)
                    Assert.True(after[i] > before[i], $"Measure {i}: {after[i]} is not larger than {before[i]}");
                Assert.Equal(1.3, after[0] / before[0], 1);
                Assert.Equal(3, FindScaleSlider(strip).Value);
            });
        }

        [Fact]
        public void SmallerScale_ShrinksTheWholeMenu()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { new AppInfo("chrome") });
                var menu = CreateMenu(strip);
                menu.Generate();
                var before = Measure(strip);

                scale.SetPercent(85);
                Assert.True(menu.Generate());

                var after = Measure(strip);
                for (int i = 0; i < before.Length; i++)
                    Assert.True(after[i] < before[i], $"Measure {i}: {after[i]} is not smaller than {before[i]}");
            });
        }

        /// <summary>Sizes of everything in the menu: icon, checkbox, font, sliders, button and the menu itself.</summary>
        private static double[] Measure(ContextMenuStrip strip)
        {
            var icon = FindControl<PictureBox>(strip, MenuHandler.IconName("chrome"));
            var checkbox = FindCheckbox(strip, "chrome");
            var label = FindControl<Label>(strip, MenuHandler.LabelName("chrome"));
            var slider = FindSlider(strip, "chrome");
            var scaleSlider = FindScaleSlider(strip);
            var exit = FindExitButton(strip);
            return new double[]
            {
                icon.Image.Width,
                icon.Width,
                checkbox.Height,
                checkbox.Width,
                label.Font.Size,
                slider.Width,
                slider.Height,
                scaleSlider.Width,
                scaleSlider.Height,
                exit.Height,
                exit.Font.Size,
                FindCheckbox(strip, "autostart").Width,
                strip.GetPreferredSize(System.Drawing.Size.Empty).Width,
                strip.GetPreferredSize(System.Drawing.Size.Empty).Height,
            };
        }

        [Fact]
        public void DraggingTheScaleSlider_AppliesOnlyWhenReleased()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                var menu = CreateMenu(strip);
                menu.Generate();
                var slider = FindScaleSlider(strip);

                Invoke(slider, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 0, 10, 0));
                Assert.True(slider.Dragging);
                Drag(slider, 2);
                Drag(slider, 3);

                Assert.Equal(3, slider.Value);
                Assert.Equal(100, scale.Percent);
                Assert.Same(slider, FindScaleSlider(strip));

                Invoke(slider, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 0, 10, 0));

                Assert.Equal(130, scale.Percent);
                Assert.Equal("130", ((InMemorySettingsStore)store.GetSubStore(MenuScale.StoreName)).Values[MenuScale.ValueName]);
                Assert.True(Sta.PumpUntil(() => FindScaleSlider(strip) != slider));
                Assert.Equal(3, FindScaleSlider(strip).Value);
                Assert.False(menu.DraggingSlider);
            });
        }

        [Fact]
        public void MouseWheel_OnTheScaleSlider_AppliesImmediately()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                var menu = CreateMenu(strip);
                menu.Generate();
                var slider = FindScaleSlider(strip);

                Invoke(slider, "OnMouseWheel", new HandledMouseEventArgs(MouseButtons.None, 0, 0, 0, -120));

                Assert.Equal(85, scale.Percent);
                Assert.True(Sta.PumpUntil(() => FindScaleSlider(strip) != slider));
                Assert.Equal(0, FindScaleSlider(strip).Value);
            });
        }

        [Fact]
        public void ScaleChangedElsewhere_Rebuilds()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                var menu = CreateMenu(strip);
                menu.Generate();

                scale.SetPercent(115);

                Assert.True(menu.Generate());
                Assert.Equal(2, FindScaleSlider(strip).Value);
            });
        }

        [Fact]
        public void ScaleChange_KeepsTheOpenMenuOnTheScreen()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome" });
                var menu = CreateMenu(strip);
                var area = Screen.PrimaryScreen.WorkingArea;
                menu.Generate();
                // Opened like from the tray: bottom right, just above the taskbar.
                var size = strip.GetPreferredSize(System.Drawing.Size.Empty);
                strip.Show(area.Right - size.Width, area.Bottom - size.Height);
                // Some platforms adjust where a menu opens; put it where the tray menu is.
                strip.Location = new System.Drawing.Point(area.Right - strip.Width, area.Bottom - strip.Height);
                try
                {
                    int bottom = strip.Bounds.Bottom;
                    int right = strip.Bounds.Right;

                    scale.SetPercent(130);
                    Assert.True(menu.Generate());

                    Assert.True(area.Contains(strip.Bounds), $"{strip.Bounds} is not within {area}");
                    Assert.Equal(bottom, strip.Bounds.Bottom);
                    Assert.Equal(right, strip.Bounds.Right);
                }
                finally
                {
                    strip.Close();
                }
            });
        }

        // ---- window titles ----

        private static string ToolTipOf(MenuHandler menu, Control control)
        {
            var toolTip = (ToolTip)typeof(MenuHandler).GetField("toolTip", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(menu);
            return toolTip.GetToolTip(control);
        }

        [Fact]
        public void Row_ShowsTheWindowTitle_AndTheFullTitleAndNameAsTooltip()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome", "spotify" });
                windowTitles["chrome"] = "shroud - Twitch";
                var menu = CreateMenu(strip);
                menu.Generate();

                var chrome = FindControl<Label>(strip, MenuHandler.LabelName("chrome"));
                Assert.Equal("shroud - Twitch", chrome.Text);
                Assert.Equal("shroud - Twitch\nchrome", ToolTipOf(menu, chrome));
                Assert.Equal("shroud - Twitch\nchrome", ToolTipOf(menu, FindControl<PictureBox>(strip, MenuHandler.IconName("chrome"))));

                // Without a window title: the app name, as before.
                var spotify = FindControl<Label>(strip, MenuHandler.LabelName("spotify"));
                Assert.Equal("spotify", spotify.Text);
                Assert.Equal("spotify", ToolTipOf(menu, spotify));

                // The settings stay keyed by the app name.
                Assert.NotNull(FindCheckbox(strip, "chrome"));
                FindCheckbox(strip, "chrome").Checked = false;
                Assert.Equal("False", store.Values["chrome"]);
            });
        }

        [Fact]
        public void LongTitle_IsCutOff_ButTheTooltipShowsItInFull()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome" });
                var longTitle = "A very long stream title that goes on and on - with even more words - Twitch";
                windowTitles["chrome"] = longTitle;
                var menu = CreateMenu(strip);
                menu.Generate();

                var label = FindControl<Label>(strip, MenuHandler.LabelName("chrome"));
                Assert.EndsWith("…", label.Text);
                Assert.StartsWith(label.Text.TrimEnd('…').TrimEnd(), longTitle);
                Assert.True(label.Text.Length < longTitle.Length);
                Assert.Equal(longTitle + "\nchrome", ToolTipOf(menu, label));
            });
        }

        [Fact]
        public void Titles_AreReadWhenTheMenuOpens_NotWhileItIsUsed()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "spotify" });
                windowTitles["spotify"] = "Artist - Song 1";
                var menu = CreateMenu(strip);
                RaiseOpening(strip);
                Assert.Equal("Artist - Song 1", FindControl<Label>(strip, MenuHandler.LabelName("spotify")).Text);

                windowTitles["spotify"] = "Artist - Song 2";
                int lookups = titleLookups;
                Assert.False(menu.Generate());
                Assert.Equal(lookups, titleLookups);

                RaiseOpening(strip);
                Assert.Equal("Artist - Song 2", FindControl<Label>(strip, MenuHandler.LabelName("spotify")).Text);
            });
        }

        [Fact]
        public void FailingTitleLookup_ShowsTheAppName()
        {
            Sta.Run(() =>
            {
                using var strip = new ContextMenuStrip();
                apps.Update(new[] { "chrome" });
                var menu = new MenuHandler(strip, preferences, apps, autoStart, () => Task.CompletedTask, icons, scale, () => false,
                    app => throw new InvalidOperationException("no window"));

                menu.Generate();

                Assert.Equal("chrome", FindControl<Label>(strip, MenuHandler.LabelName("chrome")).Text);
            });
        }

        [Fact]
        public void Ellipsize_KeepsShortTextsAndCutsLongOnes()
        {
            using var font = MenuTheme.CreateFont(15, 1f);
            Assert.Equal("chrome", MenuHandler.Ellipsize("chrome", font, 200));

            var cut = MenuHandler.Ellipsize(new string('x', 200), font, 100);
            Assert.EndsWith("…", cut);
            Assert.True(TextRenderer.MeasureText(cut, font, System.Drawing.Size.Empty, MenuTheme.SingleLine).Width <= 100);
        }
    }
}
