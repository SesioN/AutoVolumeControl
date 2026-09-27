using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AutoVolumeControl.Tests
{
    public class AppsTests
    {
        [Fact]
        public void StartsEmpty()
        {
            Assert.Empty(new Apps().GetAppNames());
        }

        [Fact]
        public void Update_AddsAppsAndRaisesEvent()
        {
            var apps = new Apps();
            int raised = 0;
            apps.AppsUpdated += (s, e) => raised++;

            Assert.True(apps.Update(new[] { "chrome", "spotify" }));

            Assert.Equal(new[] { "chrome", "spotify" }, apps.GetAppNames());
            Assert.Equal(1, raised);
        }

        [Fact]
        public void Update_WithSameApps_DoesNotRaise()
        {
            var apps = new Apps();
            apps.Update(new[] { "chrome", "spotify" });
            int raised = 0;
            apps.AppsUpdated += (s, e) => raised++;

            Assert.False(apps.Update(new[] { "spotify", "chrome" }));
            Assert.Equal(0, raised);
        }

        [Fact]
        public void Update_RemovesClosedApps()
        {
            var apps = new Apps();
            apps.Update(new[] { "chrome", "spotify" });

            Assert.True(apps.Update(new[] { "spotify" }));
            Assert.Equal(new[] { "spotify" }, apps.GetAppNames());
        }

        [Fact]
        public void Update_KeepsOrderOfExistingAndAppendsNew()
        {
            var apps = new Apps();
            apps.Update(new[] { "b", "a" });
            apps.Update(new[] { "c", "a", "b" });

            Assert.Equal(new[] { "b", "a", "c" }, apps.GetAppNames());
        }

        [Fact]
        public void Update_IgnoresDuplicatesAndEmptyNames()
        {
            var apps = new Apps();
            apps.Update(new[] { "a", null, "", "a" });

            Assert.Equal(new[] { "a" }, apps.GetAppNames());
        }

        [Fact]
        public void Update_WithEmptyList_ClearsAll()
        {
            var apps = new Apps();
            apps.Update(new[] { "a" });

            Assert.True(apps.Update(Enumerable.Empty<string>()));
            Assert.Empty(apps.GetAppNames());
        }

        [Fact]
        public void GetApps_ReturnsCopy()
        {
            var apps = new Apps();
            apps.Update(new[] { "a" });

            apps.GetAppNames().Add("b");

            Assert.Equal(new[] { "a" }, apps.GetAppNames());
        }

        [Fact]
        public void ConcurrentUpdates_KeepListConsistent()
        {
            var apps = new Apps();
            Parallel.For(0, 500, i =>
            {
                apps.Update(i % 2 == 0 ? new[] { "a", "b" } : new[] { "b", "c" });
                apps.GetAppNames();
            });

            var result = apps.GetAppNames();
            Assert.Equal(2, result.Count);
            Assert.Contains("b", result);
            Assert.Equal(result.Distinct().Count(), result.Count);
        }

        // ---- executable paths ----

        [Fact]
        public void Update_KeepsTheExecutablePath()
        {
            var apps = new Apps();
            apps.Update(new[] { new AppInfo("chrome", @"C:\Chrome\chrome.exe") });

            Assert.Equal(@"C:\Chrome\chrome.exe", apps.GetApps().Single().ExecutablePath);
        }

        [Fact]
        public void Update_UsesTheFirstKnownPathOfAName()
        {
            var apps = new Apps();
            apps.Update(new[] { new AppInfo("chrome"), new AppInfo("chrome", @"C:\a\chrome.exe"), new AppInfo("chrome", @"C:\b\chrome.exe") });

            var chrome = Assert.Single(apps.GetApps());
            Assert.Equal(@"C:\a\chrome.exe", chrome.ExecutablePath);
        }

        [Fact]
        public void Update_PathArrivingLater_CountsAsChange()
        {
            var apps = new Apps();
            apps.Update(new[] { new AppInfo("chrome") });
            int raised = 0;
            apps.AppsUpdated += (s, e) => raised++;

            Assert.True(apps.Update(new[] { new AppInfo("chrome", @"C:\Chrome\chrome.exe") }));

            Assert.Equal(1, raised);
            Assert.Equal(@"C:\Chrome\chrome.exe", apps.GetApps().Single().ExecutablePath);
        }

        [Fact]
        public void Update_KnownPathIsKept_WhenLaterSessionsHaveNoneOrAnother()
        {
            var apps = new Apps();
            apps.Update(new[] { new AppInfo("chrome", @"C:\a\chrome.exe") });

            Assert.False(apps.Update(new[] { new AppInfo("chrome") }));
            Assert.False(apps.Update(new[] { new AppInfo("chrome", @"C:\b\chrome.exe") }));
            Assert.Equal(@"C:\a\chrome.exe", apps.GetApps().Single().ExecutablePath);
        }

        [Fact]
        public void EmptyPath_IsTreatedAsUnknown()
        {
            Assert.Null(new AppInfo("chrome", "").ExecutablePath);
        }

        [Fact]
        public void NewProcessIds_AreKept_WithoutAChangeEvent()
        {
            var apps = new Apps();
            apps.Update(new[] { new AppInfo("chrome", @"C:\chrome.exe", new[] { 11 }) });
            int raised = 0;
            apps.AppsUpdated += (s, e) => raised++;

            Assert.False(apps.Update(new[] { new AppInfo("chrome", null, new[] { 12 }) }));

            var chrome = apps.GetApps().Single();
            Assert.Equal(new[] { 12 }, chrome.ProcessIds);
            Assert.Equal(@"C:\chrome.exe", chrome.ExecutablePath);
            Assert.Equal(0, raised);
        }

        [Fact]
        public void SessionsOfOneApp_CombineTheirProcessIds()
        {
            var apps = new Apps();

            apps.Update(new[] { new AppInfo("chrome", null, new[] { 11 }), new AppInfo("chrome", @"C:\chrome.exe", new[] { 12, 11 }) });

            var chrome = apps.GetApps().Single();
            Assert.Equal(new[] { 11, 12 }, chrome.ProcessIds);
            Assert.Equal(@"C:\chrome.exe", chrome.ExecutablePath);
        }
    }
}
