namespace Parsek
{
    /// <summary>
    /// IDisposable guard for GameStateRecorder suppression flags.
    /// Use via <c>using (SuppressionGuard.Crew()) { ... }</c> to ensure
    /// the flag is always cleared, even on exceptions.
    /// Struct to avoid GC allocation.
    /// </summary>
    internal struct SuppressionGuard : System.IDisposable
    {
        private readonly bool crew;
        private readonly bool resource;
        private readonly bool replay;

        private SuppressionGuard(bool crew, bool resource, bool replay)
        {
            this.crew = crew;
            this.resource = resource;
            this.replay = replay;
        }

        /// <summary>Suppresses crew events for the scope of a using block.</summary>
        internal static SuppressionGuard Crew()
        {
            GameStateRecorder.SuppressCrewEvents = true;
            return new SuppressionGuard(crew: true, resource: false, replay: false);
        }

        /// <summary>Suppresses resource events for the scope of a using block.</summary>
        internal static SuppressionGuard Resources()
        {
            GameStateRecorder.SuppressResourceEvents = true;
            return new SuppressionGuard(crew: false, resource: true, replay: false);
        }

        /// <summary>Suppresses resource events and marks replay mode for the scope of a using block.</summary>
        internal static SuppressionGuard ResourcesAndReplay()
        {
            GameStateRecorder.SuppressResourceEvents = true;
            GameStateRecorder.IsReplayingActions = true;
            return new SuppressionGuard(crew: false, resource: true, replay: true);
        }

        public void Dispose()
        {
            if (crew) GameStateRecorder.SuppressCrewEvents = false;
            if (resource) GameStateRecorder.SuppressResourceEvents = false;
            if (replay) GameStateRecorder.IsReplayingActions = false;
        }
    }
}
