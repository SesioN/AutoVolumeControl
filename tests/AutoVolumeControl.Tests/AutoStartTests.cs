using System;
using Xunit;

namespace AutoVolumeControl.Tests
{
    public class AutoStartTests : IDisposable
    {
        private const string ExePath = @"C:\Program Files\AutoVolumeControl\AutoVolumeControl.exe";
        private readonly TestRegistryKey runKey = new TestRegistryKey();
        private readonly AutoStart autoStart;

        public AutoStartTests()
        {
            autoStart = new AutoStart("AutoVolumeControl", ExePath, runKey.Path);
        }

        public void Dispose() => runKey.Dispose();

        [Fact]
        public void DisabledWhenNoEntry()
        {
            Assert.False(autoStart.IsEnabled);
        }

        [Fact]
        public void Enable_WritesQuotedPath()
        {
            autoStart.SetEnabled(true);

            Assert.Equal($"\"{ExePath}\"", runKey.GetValue("AutoVolumeControl"));
            Assert.True(autoStart.IsEnabled);
        }

        [Fact]
        public void Disable_RemovesEntry()
        {
            autoStart.SetEnabled(true);
            autoStart.SetEnabled(false);

            Assert.Null(runKey.GetValue("AutoVolumeControl"));
            Assert.False(autoStart.IsEnabled);
        }

        [Fact]
        public void Disable_WithoutEntry_DoesNotThrow()
        {
            autoStart.SetEnabled(false);
            Assert.False(autoStart.IsEnabled);
        }

        [Fact]
        public void Disable_WithMissingRunKey_DoesNotThrow()
        {
            var missing = new AutoStart("AutoVolumeControl", ExePath, runKey.Path + @"\does-not-exist");
            missing.SetEnabled(false);
            Assert.False(missing.IsEnabled);
        }

        [Fact]
        public void EntryForOtherLocation_IsNotEnabled()
        {
            runKey.SetValue("AutoVolumeControl", @"""D:\Old\AutoVolumeControl.exe""");
            Assert.False(autoStart.IsEnabled);
        }

        [Fact]
        public void Enable_RepairsStaleEntry()
        {
            runKey.SetValue("AutoVolumeControl", @"D:\Old\AutoVolumeControl.exe");
            autoStart.SetEnabled(true);

            Assert.True(autoStart.IsEnabled);
            Assert.Equal($"\"{ExePath}\"", runKey.GetValue("AutoVolumeControl"));
        }

        [Theory]
        [InlineData(ExePath)]
        [InlineData("\"" + ExePath + "\"")]
        [InlineData(@"c:\program files\autovolumecontrol\AUTOVOLUMECONTROL.exe")]
        public void LegacyUnquotedOrDifferentCaseEntry_IsRecognized(string stored)
        {
            runKey.SetValue("AutoVolumeControl", stored);
            Assert.True(autoStart.IsEnabled);
        }

        [Fact]
        public void Command_IsQuoted()
        {
            Assert.Equal($"\"{ExePath}\"", autoStart.Command);
        }
    }
}
