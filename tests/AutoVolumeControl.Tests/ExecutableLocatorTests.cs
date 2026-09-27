using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace AutoVolumeControl.Tests
{
    public class ExecutableLocatorTests
    {
        private static string OwnExecutable()
        {
            using var process = Process.GetCurrentProcess();
            return process.MainModule.FileName;
        }

        [Fact]
        public void RunningProcess_GivesItsImagePath()
        {
            var path = ExecutableLocator.Find(Process.GetCurrentProcess().Id, null, null);

            Assert.Equal(OwnExecutable(), path, ignoreCase: true);
        }

        [Fact]
        public void WithoutProcess_UsesTheDevicePathOfTheSessionIdentifier()
        {
            var exe = OwnExecutable();
            var drive = exe.Substring(0, 2);
            var device = new StringBuilder(1024);
            Assert.NotEqual(0, QueryDosDevice(drive, device, device.Capacity));
            var identifier = $@"{{0.0.0.00000000}}.{{guid}}|{device}{exe.Substring(2)}%b{{00000000-0000-0000-0000-000000000000}}";

            var path = ExecutableLocator.Find(0, identifier, () => throw new InvalidOperationException("not needed"));

            Assert.Equal(exe, path, ignoreCase: true);
        }

        [Fact]
        public void LastResort_IsAnExecutableIconPath()
        {
            var exe = OwnExecutable();

            Assert.Equal(exe, ExecutableLocator.Find(0, "x|#%b{guid}", () => exe + ",0"), ignoreCase: true);
        }

        [Fact]
        public void IconPathOfADll_IsNotUsed()
        {
            var dll = Path.Combine(Environment.SystemDirectory, "shell32.dll");

            Assert.Null(ExecutableLocator.Find(0, null, () => "@" + dll + ",-3"));
        }

        [Fact]
        public void MissingFiles_GiveNull()
        {
            Assert.Null(ExecutableLocator.Find(0, @"x|\Device\HarddiskVolume999\nothing\app.exe%b{guid}", () => @"C:\does\not\exist.exe"));
            Assert.Null(ExecutableLocator.Find(0, null, null));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryDosDeviceW")]
        private static extern int QueryDosDevice(string deviceName, StringBuilder targetPath, int max);
    }
}
