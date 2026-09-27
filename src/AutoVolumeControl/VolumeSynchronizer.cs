using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace AutoVolumeControl
{
    /// <summary>One audio session of a running app on the playback device.</summary>
    interface IAudioSession : IDisposable
    {
        /// <summary>Unique per session instance (several sessions can share one app name).</summary>
        string Id { get; }
        string Name { get; }
        /// <summary>Full path of the app's executable, or null if it could not be determined.</summary>
        string ExecutablePath { get; }
        bool IsSystemSound { get; }
        float Volume { get; set; }
        bool Muted { get; set; }
    }

    static class VolumeSynchronizer
    {
        /// <summary>Differences below this are rounding noise of the audio API and not written.</summary>
        public const float VolumeTolerance = 0.001f;

        /// <summary>
        /// Brings every enabled app session to the master volume times the app's ratio (see
        /// <see cref="AppPreferences.GetRatio"/>) and to the master mute state.
        /// Values that already match are not written again, so repeated syncs cost nothing and do not
        /// flood other apps (e.g. the Windows mixer) with change notifications.
        /// A failing session is skipped so the others are still synced.
        /// </summary>
        /// <returns>The number of sessions that were changed.</returns>
        public static int Sync(float masterVolume, bool muted, IEnumerable<IAudioSession> sessions, AppPreferences preferences, CorrectionThrottle throttle = null)
        {
            int changed = 0;
            foreach (var session in sessions)
            {
                try
                {
                    if (session.IsSystemSound || string.IsNullOrEmpty(session.Name))
                        continue;

                    if (!preferences.IsEnabled(session.Name))
                        continue;

                    float target = TargetVolume(masterVolume, preferences.GetRatio(session.Name));
                    bool volumeDiffers = Math.Abs(session.Volume - target) > VolumeTolerance;
                    bool muteDiffers = session.Muted != muted;
                    if (!volumeDiffers && !muteDiffers)
                        continue;

                    // The throttle sees the per-app target, so a new ratio ends a pause like a new master value.
                    if (throttle != null && !throttle.Allow(session.Id ?? session.Name, target, muted))
                        continue;

                    if (volumeDiffers)
                        session.Volume = target;
                    if (muteDiffers)
                        session.Muted = muted;
                    changed++;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Failed to sync session '{session.Name}': {ex.Message}");
                }
            }
            return changed;
        }

        /// <summary>master × ratio, kept within the 0.0 to 1.0 the audio API accepts.</summary>
        public static float TargetVolume(float masterVolume, float ratio)
        {
            if (float.IsNaN(ratio))
                ratio = 1f;
            float target = masterVolume * Math.Max(0f, Math.Min(1f, ratio));
            return Math.Max(0f, Math.Min(1f, target));
        }
    }
}
