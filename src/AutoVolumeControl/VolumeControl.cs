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
        private readonly AppSettings appSettings;
        private readonly Apps apps;
        private readonly NotifyIcon notifyIcon;
        private readonly ContextMenuStrip contextMenuStrip;
        private readonly AutoVolumeService service;
        private readonly Control uiControl;
        private readonly MenuHandler menuHandler;
        private bool disposed;

        public VolumeControl()
            : this(new CoreAudioBackend(), new AppSettings(), new AutoStart(Application.ProductName, Application.ExecutablePath), showTrayIcon: true)
        {
        }

        internal VolumeControl(IAudioBackend backend, AppSettings appSettings, AutoStart autoStart, bool showTrayIcon)
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

            service = new AutoVolumeService(backend, preferences, apps);
            menuHandler = new MenuHandler(contextMenuStrip, preferences, apps, autoStart, service.RefreshAsync);
            menuHandler.ExitRequested += Exit;
            apps.AppsUpdated += OnAppsUpdated;

            Started = service.Start();
            Started.ContinueWith(t => ShowError(t.Exception.GetBaseException().Message), TaskContinuationOptions.OnlyOnFaulted);
        }

        internal Task Started { get; }

        internal MenuHandler Menu => menuHandler;

        internal ContextMenuStrip ContextMenu => contextMenuStrip;

        private static Icon LoadIcon()
        {
            using var stream = typeof(VolumeControl).Assembly.GetManifestResourceStream("AutoVolumeControl.icon.ico");
            return new Icon(stream);
        }

        private void ShowError(string message)
        {
            RunOnUiThread(() =>
            {
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
                notifyIcon.Dispose();
                contextMenuStrip.Dispose();
                appSettings.Dispose();
                uiControl.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
