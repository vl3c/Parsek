namespace Parsek.Logistics
{
    /// <summary>
    /// Lifecycle state of a Supply Route. Ordinal values are part of the
    /// save format — never reorder or insert entries in the middle; append
    /// new values at the end.
    /// </summary>
    /// <remarks>
    /// Mirrors design doc §4.5. The enum is serialized by name (see codec)
    /// but tests pin the ordinal positions to catch silent reorderings that
    /// would corrupt older saves that may have used numeric encoding.
    /// </remarks>
    internal enum RouteStatus
    {
        /// <summary>Dispatching on schedule.</summary>
        Active = 0,

        /// <summary>Dispatched, waiting for transit duration to elapse.</summary>
        InTransit = 1,

        /// <summary>Origin exists but lacks resources — delayed.</summary>
        WaitingForResources = 2,

        /// <summary>Career KSC-origin route lacks dispatch funds — delayed.</summary>
        WaitingForFunds = 3,

        /// <summary>Destination can't accept delivery — waiting for capacity.</summary>
        DestinationFull = 4,

        /// <summary>Destination/origin vessel gone (orbital PID miss or no surface vessels).</summary>
        EndpointLost = 5,

        /// <summary>Route source recording chain is gone; route cannot dispatch.</summary>
        MissingSourceRecording = 6,

        /// <summary>Source recording exists but no longer matches the route proof fingerprint.</summary>
        SourceChanged = 7,

        /// <summary>Player manually paused.</summary>
        Paused = 8
    }
}
