using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using CSCore.CoreAudioAPI;

namespace AutoVolumeControl
{
    class VolumeControl : ApplicationContext
    {
        private readonly AppSettings appSettings;
        private readonly Apps apps;
        private readonly NotifyIcon notifyIcon;
        private MMDevice defaultPlaybackDevice;
        private AudioEndpointVolumeCallback volumeCallback;
        private AudioEndpointVolume defaultDeviceVolume;

        private readonly Control uiControl;

        public VolumeControl()
        {
            uiControl = new Control();
            uiControl.CreateControl();

            appSettings = new AppSettings();
            apps = new Apps();

            notifyIcon = CreateNotifyIcon();
            InitializeDefaultDevice();
        }

        private NotifyIcon CreateNotifyIcon()
        {
            var icon = new NotifyIcon
            {
                Icon = Properties.Resources.icon,
                ContextMenuStrip = new ContextMenuStrip(),
                Visible = true
            };

            var menuHandler = new MenuHandler(icon.ContextMenuStrip, appSettings, apps);
            menuHandler.OnExit(Exit);

            return icon;
        }

        private void InitializeDefaultDevice()
        {
            Task.Run(() =>
            {
                try
                {
                    InitializeCom();

                    using var enumerator = new MMDeviceEnumerator();
                    var playbackDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                    var deviceVolume = AudioEndpointVolume.FromDevice(playbackDevice);
                    var volumeCallback = new AudioEndpointVolumeCallback(playbackDevice, appSettings, apps);
                    deviceVolume.RegisterControlChangeNotify(volumeCallback);

                    AssignDeviceVariables(playbackDevice, deviceVolume, volumeCallback);
                }
                catch (Exception ex)
                {
                    ShowError(ex.Message);
                }
                finally
                {
                    ComHelper.CoUninitialize();
                }
            });
        }

        private void InitializeCom()
        {
            int hr = ComHelper.CoInitializeEx(IntPtr.Zero, ComHelper.COINIT.COINIT_MULTITHREADED);
            if (hr == (int)ComHelper.HResult.RPC_E_CHANGED_MODE)
            {
                Console.WriteLine("COM already initialized with a different threading model in VolumeControl");
            }
            else if (hr < 0)
            {
                throw new COMException("Failed to initialize COM library", hr);
            }
        }

        private void AssignDeviceVariables(MMDevice playbackDevice, AudioEndpointVolume deviceVolume, AudioEndpointVolumeCallback callback)
        {
            uiControl.BeginInvoke((Action)(() =>
            {
                defaultPlaybackDevice = playbackDevice;
                defaultDeviceVolume = deviceVolume;
                volumeCallback = callback;
            }));
        }

        private void ShowError(string message)
        {
            uiControl.BeginInvoke((Action)(() =>
            {
                notifyIcon.BalloonTipText = $"Error initializing default playback device: {message}";
                notifyIcon.ShowBalloonTip(5000);
            }));
        }

        public void Exit(object sender, EventArgs e)
        {
            Dispose();
            Application.ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                volumeCallback?.Dispose();
                defaultDeviceVolume?.UnregisterControlChangeNotify(volumeCallback);
                defaultDeviceVolume?.Dispose();
                defaultPlaybackDevice?.Dispose();
                apps?.Dispose();
                if (notifyIcon != null)
                {
                    notifyIcon.Visible = false;
                    notifyIcon.Dispose();
                }
                uiControl?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
