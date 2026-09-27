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
    }
}
