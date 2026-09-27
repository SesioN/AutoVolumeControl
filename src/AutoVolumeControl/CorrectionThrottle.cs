using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace AutoVolumeControl
{
    /// <summary>
    /// Stops correcting an app that keeps resetting its own volume, so the two apps do not fight in an
    /// endless loop. Only repeated corrections towards the same target count: when the master volume
    /// changes, the target changes and the count starts over, so moving the volume slider is never throttled.
    /// Not thread safe; used on the audio thread only.
    /// </summary>
    sealed class CorrectionThrottle
    {
        public const int MaxRepeatedCorrections = 10;
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan Pause = TimeSpan.FromSeconds(60);

        private readonly Func<DateTime> clock;
        private readonly Dictionary<string, State> states = new Dictionary<string, State>();

        public CorrectionThrottle(Func<DateTime> clock = null)
        {
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>Called before correcting an app; false while the app is paused.</summary>
        public bool Allow(string app, float targetVolume, bool targetMuted)
        {
            var now = clock();
            if (!states.TryGetValue(app, out var state))
            {
                state = new State();
                states.Add(app, state);
            }

            if (now < state.PausedUntil)
                return false;

            bool sameTarget = state.HasTarget
                && Math.Abs(state.Volume - targetVolume) <= VolumeSynchronizer.VolumeTolerance
                && state.Muted == targetMuted;
            if (!sameTarget)
            {
                state.Corrections.Clear();
                state.HasTarget = true;
                state.Volume = targetVolume;
                state.Muted = targetMuted;
            }

            while (state.Corrections.Count > 0 && now - state.Corrections.Peek() > Window)
                state.Corrections.Dequeue();

            state.Corrections.Enqueue(now);
            if (state.Corrections.Count <= MaxRepeatedCorrections)
                return true;

            Trace.WriteLine($"'{app}' keeps changing its own volume; not correcting it for {Pause.TotalSeconds} s.");
            state.PausedUntil = now + Pause;
            state.Corrections.Clear();
            return false;
        }

        private sealed class State
        {
            public bool HasTarget;
            public float Volume;
            public bool Muted;
            public DateTime PausedUntil;
            public readonly Queue<DateTime> Corrections = new Queue<DateTime>();
        }
    }
}
