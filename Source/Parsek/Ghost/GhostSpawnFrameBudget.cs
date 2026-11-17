namespace Parsek
{
    /// <summary>
    /// The per-frame cap on throttle-eligible ghost spawns (bug #414), as an object a
    /// <see cref="GhostPlaybackEngine"/> reads instead of a private counter, so two engines in
    /// one scene can draw on ONE cap. An engine that builds its own budget owns it and resets
    /// it every frame; a budget passed into an engine is reset by whoever passed it in, once
    /// per frame before the first engine runs (<see cref="FrameContext"/> carries no frame
    /// number, so a budget cannot reset itself). Not to be confused with a ghost-count budget:
    /// this one counts spawns started in the current frame only. The throttle decision stays
    /// with the engine (<see cref="GhostPlaybackLogic.ShouldThrottleSpawn"/> over
    /// <see cref="Used"/> and <see cref="MaxPerFrame"/>), so this type references no playback
    /// logic and stays out of the type knot.
    /// </summary>
    internal sealed class GhostSpawnFrameBudget
    {
        private int used;

        /// <summary>The cap, <see cref="GhostPlayback.MaxSpawnsPerFrame"/>.</summary>
        internal int MaxPerFrame { get; } = GhostPlayback.MaxSpawnsPerFrame;

        /// <summary>Spawns started since the last <see cref="ResetForFrame"/>.</summary>
        internal int Used => used;

        /// <summary>One spawn started (throttle-eligible or not, as the engine's own count).</summary>
        internal void Consume()
        {
            used++;
        }

        /// <summary>A new frame: nothing spawned yet.</summary>
        internal void ResetForFrame()
        {
            used = 0;
        }
    }
}
