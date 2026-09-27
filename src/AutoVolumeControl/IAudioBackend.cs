using System;
using System.Collections.Generic;

namespace AutoVolumeControl
{
    /// <summary>
    /// Access to the default playback device. All members except the events must be called on the
    /// <see cref="AudioThread"/>. The events may be raised on any thread and must not call back into the backend.
    /// </summary>
    interface IAudioBackend : IDisposable
    {
        event EventHandler MasterVolumeChanged;
        event EventHandler SessionCreated;
        event EventHandler DefaultDeviceChanged;

        bool IsAttached { get; }

        /// <summary>(Re)binds to the current default playback device.</summary>
        void Attach();

        (float Volume, bool Muted) GetMasterVolume();

        /// <summary>Returns a snapshot of all sessions. The caller disposes them.</summary>
        IReadOnlyList<IAudioSession> GetSessions();
    }
}
