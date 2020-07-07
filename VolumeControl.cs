using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using AudioSwitcher.AudioApi.CoreAudio;


namespace AutoVolumeControl
{
    class VolumeControl : ApplicationContext
    {
        private NotifyIcon notifyIcon;
        private IDisposable subsciption;

        public VolumeControl()
        {
            MenuItem exitMenuItem = new MenuItem("Exit", new EventHandler(Exit));

            this.notifyIcon = new NotifyIcon();
            this.notifyIcon.Icon = AutoVolumeControl.Properties.Resources.icon;
            this.notifyIcon.ContextMenu = new ContextMenu(new MenuItem[] { exitMenuItem });
            this.notifyIcon.Visible = true;

            CoreAudioDevice defaultPlaybackDevice = new CoreAudioController().DefaultPlaybackDevice;
            this.subsciption = defaultPlaybackDevice.VolumeChanged.Subscribe(new VolumeMonitor());
        }
        public void Exit(object sender, EventArgs e)
        {
            this.subsciption.Dispose();
            this.notifyIcon.Visible = false;        
            Application.Exit();
        }
}
}
