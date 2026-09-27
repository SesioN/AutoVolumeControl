using Xunit;

namespace AutoVolumeControl.Tests
{
    public class MenuScaleTests
    {
        private readonly InMemorySettingsStore store = new InMemorySettingsStore();

        private System.Collections.Generic.Dictionary<string, string> Stored =>
            ((InMemorySettingsStore)store.GetSubStore(MenuScale.StoreName)).Values;

        [Fact]
        public void Default_Is100Percent_WithoutWriting()
        {
            var scale = new MenuScale(store);

            Assert.Equal(100, scale.Percent);
            Assert.Equal(1f, scale.Factor);
            Assert.Empty(Stored);
        }

        [Fact]
        public void SetPercent_IsStoredInItsOwnSubStore()
        {
            var scale = new MenuScale(store);
            int changes = 0;
            scale.Changed += (s, e) => changes++;

            scale.SetPercent(115);

            Assert.Equal("115", Stored[MenuScale.ValueName]);
            Assert.Empty(store.Values);
            Assert.Equal(115, new MenuScale(store).Percent);
            Assert.Equal(1, changes);
        }

        [Fact]
        public void SetPercent_Unchanged_DoesNotWriteOrNotify()
        {
            var scale = new MenuScale(store);
            int changes = 0;
            scale.Changed += (s, e) => changes++;

            scale.SetPercent(100);

            Assert.Empty(Stored);
            Assert.Equal(0, changes);
        }

        [Theory]
        [InlineData(0, 85)]
        [InlineData(90, 85)]
        [InlineData(93, 100)]
        [InlineData(120, 115)]
        [InlineData(500, 130)]
        public void SetPercent_SnapsToTheNearestStep(int value, int expected)
        {
            var scale = new MenuScale(store);

            scale.SetPercent(value);

            Assert.Equal(expected, scale.Percent);
        }

        [Theory]
        [InlineData("garbage", 100)]
        [InlineData("", 100)]
        [InlineData(" 130 ", 130)]
        [InlineData("112", 115)]
        [InlineData("-5", 85)]
        public void HandEditedValues_AreTolerated(string value, int expected)
        {
            Stored[MenuScale.ValueName] = value;

            Assert.Equal(expected, new MenuScale(store).Percent);
        }

        [Fact]
        public void Steps_AreTheOfferedSizes()
        {
            Assert.Equal(new[] { 85, 100, 115, 130 }, MenuScale.Steps);
            Assert.Equal(1, MenuScale.IndexOf(100));
        }
    }
}
