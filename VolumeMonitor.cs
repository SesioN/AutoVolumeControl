using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;
using AudioSwitcher.AudioApi.Observables;

namespace AutoVolumeControl
{
    class VolumeMonitor : IObserver<DeviceVolumeChangedArgs>
    {
        CoreAudioDevice defaultPlaybackDevice = new CoreAudioController().DefaultPlaybackDevice;

        public void OnCompleted()
        {
            throw new NotImplementedException();
        }

        public void OnError(Exception error)
        {
            throw new NotImplementedException();
        }

        public void OnNext(DeviceVolumeChangedArgs value)
        {
            foreach (var activeAudioSession in defaultPlaybackDevice.SessionController.ActiveSessions())
            {
                activeAudioSession.Volume = defaultPlaybackDevice.Volume;
            }
        }
    }
}
