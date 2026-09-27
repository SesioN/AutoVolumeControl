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
        /// <summary>The master volume or mute state of the device changed.</summary>
        event EventHandler MasterVolumeChanged;

        /// <summary>
        /// A session was created, changed its state, changed its own volume, or its process exited.
        /// Changes made by this app itself are not reported.
        /// </summary>
        event EventHandler SessionsChanged;

        /// <summary>The default device changed or the current one went away; <see cref="Attach"/> again.</summary>
        event EventHandler ReattachRequired;

        /// <summary>True when bound to a device and all notifications are registered.</summary>
        bool IsAttached { get; }

        /// <summary>
        /// (Re)binds to the current default playback device. Either succeeds completely or throws and
        /// leaves the backend detached.
        /// </summary>
        void Attach();

        (float Volume, bool Muted) GetMasterVolume();

        /// <summary>
        /// Returns a snapshot of the sessions of running apps (expired sessions and sessions of exited
        /// processes are left out) and watches them for changes. The caller disposes the snapshot.
        /// </summary>
        IReadOnlyList<IAudioSession> GetSessions();
    }
}
