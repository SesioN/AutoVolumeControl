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
            Assert.Empty(new Apps().GetApps());
        }

        [Fact]
        public void Update_AddsAppsAndRaisesEvent()
        {
            var apps = new Apps();
            int raised = 0;
            apps.AppsUpdated += (s, e) => raised++;

            Assert.True(apps.Update(new[] { "chrome", "spotify" }));

            Assert.Equal(new[] { "chrome", "spotify" }, apps.GetApps());
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
            Assert.Equal(new[] { "spotify" }, apps.GetApps());
        }

        [Fact]
        public void Update_KeepsOrderOfExistingAndAppendsNew()
        {
            var apps = new Apps();
            apps.Update(new[] { "b", "a" });
            apps.Update(new[] { "c", "a", "b" });

            Assert.Equal(new[] { "b", "a", "c" }, apps.GetApps());
        }

        [Fact]
        public void Update_IgnoresDuplicatesAndEmptyNames()
        {
            var apps = new Apps();
            apps.Update(new[] { "a", null, "", "a" });

            Assert.Equal(new[] { "a" }, apps.GetApps());
        }

        [Fact]
        public void Update_WithEmptyList_ClearsAll()
        {
            var apps = new Apps();
            apps.Update(new[] { "a" });

            Assert.True(apps.Update(Enumerable.Empty<string>()));
            Assert.Empty(apps.GetApps());
        }

        [Fact]
        public void GetApps_ReturnsCopy()
        {
            var apps = new Apps();
            apps.Update(new[] { "a" });

            apps.GetApps().Add("b");

            Assert.Equal(new[] { "a" }, apps.GetApps());
        }

        [Fact]
        public void ConcurrentUpdates_KeepListConsistent()
        {
            var apps = new Apps();
            Parallel.For(0, 500, i =>
            {
                apps.Update(i % 2 == 0 ? new[] { "a", "b" } : new[] { "b", "c" });
                apps.GetApps();
            });

            var result = apps.GetApps();
            Assert.Equal(2, result.Count);
            Assert.Contains("b", result);
            Assert.Equal(result.Distinct().Count(), result.Count);
        }
    }
}
