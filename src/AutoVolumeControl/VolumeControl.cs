using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using MaterialSkin.Controls;

namespace AutoVolumeControl
{
    /// <summary>The tray application: owns the tray icon, the menu and the volume service.</summary>
    class VolumeControl : ApplicationContext
    {
        /// <summary>
        /// A start failure is only reported if it persists this long; at logon the audio service is often not
        /// ready yet and the automatic retry succeeds a few seconds later.
        /// </summary>
        public static readonly TimeSpan DefaultErrorNotificationDelay = TimeSpan.FromSeconds(15);

        private readonly AppSettings appSettings;
        private readonly Apps apps;
        private readonly NotifyIcon notifyIcon;
        private readonly ContextMenuStrip contextMenuStrip;
        private readonly AutoVolumeService service;
        private readonly Control uiControl;
        private readonly MenuHandler menuHandler;
        private readonly AppIconCache icons;
        private bool disposed;

        public VolumeControl()
            : this(new CoreAudioBackend(), new AppSettings(), new AutoStart(Application.ProductName, Application.ExecutablePath), showTrayIcon: true, DefaultErrorNotificationDelay, retryInterval: null)
        {
        }

        internal VolumeControl(IAudioBackend backend, AppSettings appSettings, AutoStart autoStart, bool showTrayIcon, TimeSpan errorNotificationDelay, TimeSpan? retryInterval)
        {
            uiControl = new Control();
            uiControl.CreateControl();

            this.appSettings = appSettings;
            var preferences = new AppPreferences(appSettings);
            apps = new Apps();

            contextMenuStrip = new MaterialContextMenuStrip();
            notifyIcon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = Application.ProductName,
                ContextMenuStrip = contextMenuStrip,
                Visible = showTrayIcon
            };

            service = new AutoVolumeService(backend, preferences, apps, retryInterval);
            icons = new AppIconCache();
            menuHandler = new MenuHandler(contextMenuStrip, preferences, apps, autoStart, service.RefreshAsync, icons);
            // Exit from the button's click handler would dispose the menu while it is still processing the click.
            menuHandler.ExitRequested += (sender, e) => RunOnUiThread(() => Exit(sender, e));
            apps.AppsUpdated += OnAppsUpdated;
            menuHandler.Generate();

            Started = service.Start();
            Started.ContinueWith(
                t => Task.Delay(errorNotificationDelay).ContinueWith(_ =>
                {
                    if (!service.IsHealthy)
                        ShowError((service.LastError ?? t.Exception.GetBaseException()).Message);
                }),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        internal Task Started { get; }

        internal MenuHandler Menu => menuHandler;

        internal ContextMenuStrip ContextMenu => contextMenuStrip;

        /// <summary>The last error shown as a balloon tip, for tests.</summary>
        internal string ShownError { get; private set; }

        /// <summary>
        /// Picks the image matching the tray size (e.g. 24x24 at 150 %). Without a size, the 48x48 image
        /// would be loaded and downscaled by Windows, which looks blurry.
        /// </summary>
        internal static Icon LoadIcon()
        {
            using var stream = typeof(VolumeControl).Assembly.GetManifestResourceStream("AutoVolumeControl.icon.ico");
            return new Icon(stream, SystemInformation.SmallIconSize);
        }

        private void ShowError(string message)
        {
            RunOnUiThread(() =>
            {
                ShownError = message;
                notifyIcon.BalloonTipText = $"Error initializing default playback device: {message}";
                notifyIcon.ShowBalloonTip(5000);
            });
        }

        private void OnAppsUpdated(object sender, EventArgs e)
        {
            RunOnUiThread(() => menuHandler.Generate());
        }

        private void RunOnUiThread(Action action)
        {
            if (uiControl.IsDisposed || !uiControl.IsHandleCreated)
                return;

            try
            {
                uiControl.BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // The handle was destroyed while exiting.
            }
        }

        public void Exit(object sender, EventArgs e)
        {
            Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                apps.AppsUpdated -= OnAppsUpdated;
                service.Dispose();
                notifyIcon.Visible = false;
                var icon = notifyIcon.Icon;
                notifyIcon.Dispose();
                icon?.Dispose();
                contextMenuStrip.Dispose();
                // After the menu: its picture boxes show the cached icons.
                icons.Dispose();
                appSettings.Dispose();
                uiControl.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
