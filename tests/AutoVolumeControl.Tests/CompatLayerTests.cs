using Xunit;

namespace AutoVolumeControl.Tests
{
    public class CompatLayerTests
    {
        [Theory]
        [InlineData("~ GDIDPISCALING DPIUNAWARE")]
        [InlineData("DPIUNAWARE")]
        [InlineData("~ gdidpiscaling")]
        [InlineData("~ RUNASINVOKER DPIUNAWARE")]
        public void DetectsDpiLayers(string value)
        {
            Assert.True(CompatLayer.HasDpiLayers(value));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("~ RUNASINVOKER")]
        [InlineData("~ HIGHDPIAWARE")]
        [InlineData("WIN7RTM")]
        public void IgnoresOtherLayers(string value)
        {
            Assert.False(CompatLayer.HasDpiLayers(value));
        }

        [Theory]
        [InlineData("~ GDIDPISCALING DPIUNAWARE", null)]
        [InlineData("DPIUNAWARE", null)]
        [InlineData("~ RUNASINVOKER GDIDPISCALING DPIUNAWARE", "~ RUNASINVOKER")]
        [InlineData("WIN7RTM DPIUNAWARE", "WIN7RTM")]
        [InlineData("~  RUNASINVOKER   DPIUNAWARE ", "~ RUNASINVOKER")]
        public void RemovesOnlyDpiLayers(string value, string expected)
        {
            Assert.Equal(expected, CompatLayer.RemoveDpiLayers(value));
        }
    }
}
