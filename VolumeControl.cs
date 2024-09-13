using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using CSCore.CoreAudioAPI;
using System.Runtime.InteropServices;

namespace AutoVolumeControl
{
    class VolumeControl : ApplicationContext
    {
        private AppSettings appSettings;
        private Apps apps;
        private NotifyIcon notifyIcon;
        private MMDevice defaultPlaybackDevice;
        private AudioEndpointVolumeCallback volumeCallback;
        private AudioEndpointVolume defaultDeviceVolume;

        public VolumeControl()
        {
            this.apps = new Apps();
            this.appSettings = new AppSettings();

            this.notifyIcon = new NotifyIcon
            {
                Icon = AutoVolumeControl.Properties.Resources.icon,
                ContextMenuStrip = new ContextMenuStrip()
            };

            MenuHandler menuHandler = new MenuHandler(this.notifyIcon.ContextMenuStrip, this.appSettings, this.apps);
            menuHandler.onExit(new EventHandler(Exit));

            this.notifyIcon.Visible = true;

            InitializeDefaultDevice();
        }

        private void InitializeDefaultDevice()
        {
            try
            {
                // Initialize COM on the current thread
                CoInitializeEx(IntPtr.Zero, COINIT.COINIT_MULTITHREADED);

                // Create an MMDeviceEnumerator instance
                using (var enumerator = new MMDeviceEnumerator())
                {
                    // Get the default playback device
                    defaultPlaybackDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                    // Get the AudioEndpointVolume interface from the device
                    defaultDeviceVolume = AudioEndpointVolume.FromDevice(defaultPlaybackDevice);

                    // Create a new callback object and register it
                    volumeCallback = new AudioEndpointVolumeCallback(defaultPlaybackDevice, this.appSettings, this.apps);
                    defaultDeviceVolume.RegisterControlChangeNotify(volumeCallback);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing default playback device: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Import CoInitializeEx from the ole32.dll
        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pvReserved, COINIT dwCoInit);

        // Define the COINIT enumeration
        private enum COINIT : uint
        {
            COINIT_MULTITHREADED = 0x0,
            COINIT_APARTMENTTHREADED = 0x2,
            COINIT_DISABLE_OLE1DDE = 0x4,
            COINIT_SPEED_OVER_MEMORY = 0x8
        }

        public void Exit(object sender, EventArgs e)
        {
            if (defaultDeviceVolume != null)
            {
                defaultDeviceVolume.UnregisterControlChangeNotify(volumeCallback);
            }
            this.notifyIcon.Visible = false;
            Application.ExitThread(); // Ensure proper application exit
        }
    }
}
