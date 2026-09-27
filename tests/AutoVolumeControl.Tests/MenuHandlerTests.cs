using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MaterialSkin.Controls;
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
        private bool mouseButtonDown;

        public MenuHandlerTests()
        {
            preferences = new AppPreferences(store);
            autoStart = new AutoStart("AutoVolumeControl", @"C:\Apps\AutoVolumeControl.exe", runKey.Path);
        }

        public void Dispose()
        {
            icons.Dispose();
            runKey.Dispose();
        }

        private MenuHandler CreateMenu(ContextMenuStrip strip, Func<Task> refresh = null)
        {
            return new MenuHandler(strip, preferences, apps, autoStart, refresh ?? (() => Task.CompletedTask), icons, () => mouseButtonDown);
        }

        private static T FindControl<T>(ContextMenuStrip strip, string name) where T : Control
        {
            return strip.Items.OfType<ToolStripControlHost>()
                .SelectMany(h => new[] { h.Control }.Concat(h.Control.Controls.Cast<Control>()))
                .OfType<T>()
                .SingleOrDefault(c => c.Name == name);
        }

        private static MaterialSlider FindSlider(ContextMenuStrip strip, string app) => FindControl<MaterialSlider>(strip, MenuHandler.SliderName(app));

        /// <summary>Raises the slider's value event the way dragging does (setting Value does not raise it).</summary>
        private static void Drag(MaterialSlider slider, int value)
        {
            slider.Value = value;
            var field = typeof(MaterialSlider).GetField("onValueChanged", BindingFlags.Instance | BindingFlags.NonPublic);
            ((MaterialSlider.ValueChanged)field.GetValue(slider))?.Invoke(slider, value);
        }

        private static MaterialCheckbox FindCheckbox(ContextMenuStrip strip, string name)
        {
            return strip.Items.OfType<ToolStripControlHost>()
                .SelectMany(h => new[] { h.Control }.Concat(h.Control.Controls.Cast<Control>()))
                .OfType<MaterialCheckbox>()
                .SingleOrDefault(c => c.Name == name);
        }

        private static MaterialButton FindExitButton(ContextMenuStrip strip)
        {
            return strip.Items.OfType<ToolStripControlHost>().Select(h => h.Control).OfType<MaterialButton>().Single();
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
                Assert.Equal("chrome", FindControl<MaterialLabel>(strip, MenuHandler.LabelName("chrome")).Text);
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
                Assert.DoesNotContain(strip.Items.OfType<ToolStripControlHost>(), h => h.Control is TableLayoutPanel);
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
                Assert.Equal(System.Windows.Forms.SystemInformation.SmallIconSize, known.Image.Size);
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

                InvokeOnClick(FindControl<MaterialLabel>(strip, MenuHandler.LabelName("chrome")));
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
                Assert.Equal(0, FindSlider(strip, "spotify").RangeMin);
                Assert.Equal(100, FindSlider(strip, "spotify").RangeMax);
                Assert.Equal("%", FindSlider(strip, "spotify").ValueSuffix);
                Assert.False(FindSlider(strip, "spotify").ShowText);
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

                    // Releasing the button (anywhere in the menu) runs the deferred rebuild.
                    mouseButtonDown = false;
                    typeof(Control).GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(slider, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 5, 5, 0) });

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
    }
}
