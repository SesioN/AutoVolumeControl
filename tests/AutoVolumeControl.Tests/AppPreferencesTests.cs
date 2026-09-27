using Xunit;

namespace AutoVolumeControl.Tests
{
    public class AppPreferencesTests
    {
        private readonly InMemorySettingsStore store = new InMemorySettingsStore();
        private readonly AppPreferences preferences;

        public AppPreferencesTests()
        {
            preferences = new AppPreferences(store);
        }

        [Fact]
        public void UnknownApp_IsEnabledByDefault_WithoutWriting()
        {
            Assert.True(preferences.IsEnabled("chrome"));
            Assert.Empty(store.Values);
        }

        [Theory]
        [InlineData("True", true)]
        [InlineData("False", false)]
        [InlineData("false", false)]
        [InlineData("TRUE", true)]
        public void ReadsStoredFlag(string stored, bool expected)
        {
            store.Values["chrome"] = stored;
            Assert.Equal(expected, preferences.IsEnabled("chrome"));
        }

        [Theory]
        [InlineData("yes")]
        [InlineData("")]
        [InlineData("1")]
        public void InvalidStoredValue_DoesNotThrowAndFallsBackToEnabled(string stored)
        {
            store.Values["chrome"] = stored;
            Assert.True(preferences.IsEnabled("chrome"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void EmptyName_IsNeverEnabled(string name)
        {
            Assert.False(preferences.IsEnabled(name));
        }

        [Fact]
        public void SetEnabled_StoresFlag()
        {
            preferences.SetEnabled("chrome", false);
            Assert.Equal("False", store.Values["chrome"]);
            Assert.False(preferences.IsEnabled("chrome"));

            preferences.SetEnabled("chrome", true);
            Assert.Equal("True", store.Values["chrome"]);
        }

        [Fact]
        public void SetEnabled_IgnoresEmptyName()
        {
            preferences.SetEnabled("", false);
            Assert.Empty(store.Values);
        }

        [Fact]
        public void Register_WritesDefaultForNewApp()
        {
            preferences.Register("chrome");
            Assert.Equal("True", store.Values["chrome"]);
        }

        [Fact]
        public void Register_KeepsExistingChoice()
        {
            store.Values["chrome"] = "False";
            preferences.Register("chrome");
            Assert.Equal("False", store.Values["chrome"]);
        }

        [Fact]
        public void Register_IgnoresEmptyName()
        {
            preferences.Register(null);
            preferences.Register("");
            Assert.Empty(store.Values);
        }

        [Fact]
        public void SetEnabled_RaisesChanged()
        {
            int raised = 0;
            preferences.Changed += (s, e) => raised++;

            preferences.SetEnabled("chrome", false);

            Assert.Equal(1, raised);
        }

        [Fact]
        public void Register_DoesNotRaiseChanged()
        {
            int raised = 0;
            preferences.Changed += (s, e) => raised++;

            preferences.Register("chrome");

            Assert.Equal(0, raised);
        }

        // ---- ratio ----

        [Fact]
        public void UnknownApp_HasFullRatio_WithoutWriting()
        {
            Assert.Equal(1f, preferences.GetRatio("chrome"));
            Assert.Equal(100, preferences.GetRatioPercent("chrome"));
            Assert.Empty(store.Ratios);
        }

        [Fact]
        public void SetRatio_RoundTripsAsInvariantPercent()
        {
            preferences.SetRatio("chrome", 0.7f);

            Assert.Equal("70", store.Ratios["chrome"]);
            Assert.Equal(70, preferences.GetRatioPercent("chrome"));
            Assert.Equal(0.7f, preferences.GetRatio("chrome"), 3);
        }

        [Fact]
        public void Ratios_AreKeptApartFromTheFlags()
        {
            preferences.SetEnabled("chrome", false);
            preferences.SetRatioPercent("chrome", 40);

            Assert.Equal("False", store.Values["chrome"]);
            Assert.Equal("40", store.Ratios["chrome"]);
            Assert.False(preferences.IsEnabled("chrome"));
            Assert.Equal(40, preferences.GetRatioPercent("chrome"));
        }

        [Theory]
        [InlineData(150, "100")]
        [InlineData(-5, "0")]
        [InlineData(0, "0")]
        [InlineData(100, "100")]
        public void SetRatioPercent_ClampsOutOfRangeValues(int percent, string stored)
        {
            preferences.SetRatioPercent("chrome", percent);
            Assert.Equal(stored, store.Ratios["chrome"]);
        }

        [Theory]
        [InlineData(1.5f, "100")]
        [InlineData(-0.5f, "0")]
        [InlineData(0.004f, "0")]
        [InlineData(0.005f, "1")]
        public void SetRatio_ClampsAndRounds(float ratio, string stored)
        {
            preferences.SetRatio("chrome", ratio);
            Assert.Equal(stored, store.Ratios["chrome"]);
        }

        [Fact]
        public void SetRatio_IgnoresNaN()
        {
            preferences.SetRatio("chrome", float.NaN);
            Assert.Empty(store.Ratios);
        }

        [Theory]
        [InlineData("abc", 100)]
        [InlineData("", 100)]
        [InlineData("0.5", 100)]
        [InlineData("250", 100)]
        [InlineData("-1", 0)]
        [InlineData(" 30 ", 30)]
        public void InvalidStoredRatio_DoesNotThrow(string stored, int expected)
        {
            store.Ratios["chrome"] = stored;
            Assert.Equal(expected, preferences.GetRatioPercent("chrome"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void EmptyName_HasFullRatioAndIsNotStored(string name)
        {
            preferences.SetRatioPercent(name, 20);

            Assert.Equal(100, preferences.GetRatioPercent(name));
            Assert.Empty(store.Ratios);
        }

        [Fact]
        public void SetRatio_RaisesChangedOnlyWhenTheValueChanges()
        {
            int raised = 0;
            preferences.Changed += (s, e) => raised++;

            preferences.SetRatioPercent("chrome", 60);
            preferences.SetRatioPercent("chrome", 60);
            preferences.SetRatio("chrome", 0.601f);
            preferences.SetRatioPercent("chrome", 61);

            Assert.Equal(2, raised);
        }

        [Fact]
        public void SetRatio_OverwritesAnInvalidStoredValue()
        {
            store.Ratios["chrome"] = "garbage";

            preferences.SetRatioPercent("chrome", 100);

            Assert.Equal("100", store.Ratios["chrome"]);
        }
    }
}
