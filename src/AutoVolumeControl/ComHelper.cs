using System;
using System.Runtime.InteropServices;

namespace AutoVolumeControl
{
    public static class ComHelper
    {
        [DllImport("ole32.dll")]
        public static extern int CoInitializeEx(IntPtr pvReserved, COINIT dwCoInit);

        [DllImport("ole32.dll")]
        public static extern void CoUninitialize();

        public enum COINIT : uint
        {
            COINIT_MULTITHREADED = 0x0,
            COINIT_APARTMENTTHREADED = 0x2,
            COINIT_DISABLE_OLE1DDE = 0x4,
            COINIT_SPEED_OVER_MEMORY = 0x8
        }

        public enum HResult : int
        {
            S_OK = 0,
            S_FALSE = 1,
            RPC_E_CHANGED_MODE = unchecked((int)0x80010106)
        }
    }
}
