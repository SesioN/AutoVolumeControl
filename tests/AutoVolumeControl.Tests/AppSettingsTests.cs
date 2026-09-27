using System;
using Microsoft.Win32;
using Xunit;

namespace AutoVolumeControl.Tests
{
    public class AppSettingsTests : IDisposable
    {
        private readonly TestRegistryKey key = new TestRegistryKey();

        public void Dispose() => key.Dispose();

        [Fact]
        public void SetThenGet_RoundTrips()
        {
            using var settings = new AppSettings(key.Path);
            settings.Set("chrome", "False");

            Assert.Equal("False", settings.Get("chrome"));
            Assert.True(settings.Exists("chrome"));
        }

        [Fact]
        public void MissingValue_GetReturnsNullAndDoesNotExist()
        {
            using var settings = new AppSettings(key.Path);

            Assert.Null(settings.Get("unknown"));
            Assert.False(settings.Exists("unknown"));
        }

        [Fact]
        public void ValuesArePersistedInTheRegistry()
        {
            using (var settings = new AppSettings(key.Path))
                settings.Set("spotify", "True");

            Assert.Equal("True", key.GetValue("spotify"));
            using var reopened = new AppSettings(key.Path);
            Assert.Equal("True", reopened.Get("spotify"));
        }

        [Fact]
        public void NonStringValue_GetReturnsNullButExists()
        {
            key.SetValue("dword", 1, RegistryValueKind.DWord);
            using var settings = new AppSettings(key.Path);

            Assert.Null(settings.Get("dword"));
            Assert.True(settings.Exists("dword"));
        }

        [Fact]
        public void CreatesMissingKey()
        {
            var path = key.Path + @"\nested";
            using var settings = new AppSettings(path);
            settings.Set("a", "b");

            Assert.Equal("b", settings.Get("a"));
        }

        [Fact]
        public void ConcurrentAccess_DoesNotThrow()
        {
            using var settings = new AppSettings(key.Path);
            System.Threading.Tasks.Parallel.For(0, 200, i =>
            {
                settings.Set($"app{i % 10}", (i % 2 == 0).ToString());
                settings.Get($"app{i % 10}");
                settings.Exists($"app{i % 10}");
            });

            Assert.True(settings.Exists("app0"));
        }

        [Fact]
        public void SubStore_IsASubkeyAndDoesNotClashWithValues()
        {
            using (var settings = new AppSettings(key.Path))
            {
                settings.Set("Ratios", "True");
                var ratios = settings.GetSubStore("Ratios");
                ratios.Set("chrome", "70");

                Assert.Equal("True", settings.Get("Ratios"));
                Assert.Null(settings.Get("chrome"));
                Assert.Equal("70", ratios.Get("chrome"));
                Assert.Same(ratios, settings.GetSubStore("Ratios"));
            }

            using var reopened = Registry.CurrentUser.OpenSubKey(key.Path + @"\Ratios", false);
            Assert.Equal("70", reopened.GetValue("chrome"));
        }

        [Fact]
        public void SubStore_IsNotCreatedByReading()
        {
            using var settings = new AppSettings(key.Path);
            var ratios = settings.GetSubStore("Ratios");

            Assert.Null(ratios.Get("chrome"));
            Assert.False(ratios.Exists("chrome"));
            using (var missing = Registry.CurrentUser.OpenSubKey(key.Path + @"\Ratios", false))
                Assert.Null(missing);

            ratios.Set("chrome", "70");
            Assert.Equal("70", ratios.Get("chrome"));
        }
    }
}
