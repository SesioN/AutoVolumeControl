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

        public MenuHandlerTests()
        {
            preferences = new AppPreferences(store);
            autoStart = new AutoStart("AutoVolumeControl", @"C:\Apps\AutoVolumeControl.exe", runKey.Path);
        }

        public void Dispose() => runKey.Dispose();

        private MenuHandler CreateMenu(ContextMenuStrip strip, Func<Task> refresh = null)
        {
            return new MenuHandler(strip, preferences, apps, autoStart, refresh ?? (() => Task.CompletedTask));
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
                Assert.Equal("App: chrome", FindCheckbox(strip, "chrome").Text);
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
                Assert.DoesNotContain(strip.Items.OfType<ToolStripControlHost>(), h => h.Control is FlowLayoutPanel);
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

        [DllImport("user32.dll")]
        private static extern uint GetGuiResources(IntPtr process, uint flags);

        [Fact]
        public void RepeatedRebuilds_DoNotLeakWindowHandles()
        {
            Sta.Run(() =>
            {
                const uint GdiObjects = 0, UserObjects = 1;
                var process = System.Diagnostics.Process.GetCurrentProcess().Handle;
                using var strip = new ContextMenuStrip();
                var menu = CreateMenu(strip);

                void RebuildWithHandles(int i)
                {
                    apps.Update(new[] { $"app{i % 2}", "chrome" });
                    menu.Generate();
                    // Hosted controls only allocate window handles once created, as when the menu is shown.
                    foreach (var host in strip.Items.OfType<ToolStripControlHost>())
                    {
                        host.Control.CreateControl();
                        foreach (Control child in host.Control.Controls)
                            child.CreateControl();
                    }
                }

                for (int i = 0; i < 5; i++)
                    RebuildWithHandles(i);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                uint user = GetGuiResources(process, UserObjects);
                uint gdi = GetGuiResources(process, GdiObjects);

                for (int i = 0; i < 200; i++)
                    RebuildWithHandles(i);
                GC.Collect();
                GC.WaitForPendingFinalizers();

                // Without disposing the old items this grew by several handles per rebuild (> 1000 here).
                Assert.InRange((int)GetGuiResources(process, UserObjects) - (int)user, -50, 50);
                Assert.InRange((int)GetGuiResources(process, GdiObjects) - (int)gdi, -50, 50);
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
    }
}
