using System.Collections.Generic;

namespace Parsek
{
    /// <summary>What the live side read before <see cref="DeltaVSnapshotTracker.Decide"/> runs.</summary>
    internal struct DeltaVReadiness
    {
        /// <summary><c>GameSettings.DELTAV_CALCULATIONS_ENABLED</c>.</summary>
        public bool CalculationsEnabled;
        /// <summary>The recorded vessel is the loaded active vessel, the only one stock computes for.</summary>
        public bool VesselEligible;
        /// <summary><c>VesselDeltaV.IsReady</c> and no simulation in flight.</summary>
        public bool Ready;
        /// <summary><c>GameSettings.DELTAV_VESSEL_EVENT_DELAY_SECS</c>, in <c>Time.time</c> seconds.</summary>
        public double EventDelaySeconds;
    }

    internal enum DeltaVRequestOutcome
    {
        Wait,
        Complete,
        TimedOut,
        Dropped,
    }

    /// <summary>One request's decision on a tick.</summary>
    internal struct DeltaVDecision
    {
        public DeltaVTrigger Trigger;
        public DeltaVRequestOutcome Outcome;
        public int SettleTicks;
        public double WaitedSeconds;
    }

    /// <summary>
    /// The pure pending / settle / timeout state machine behind the focused recorder's delta-v
    /// snapshots (design-mission-profile.md section 8.1). A trigger files a request; each
    /// physics tick the recorder hands the live readiness to <see cref="Decide"/>, which
    /// completes, times out, drops or keeps each request. The stock calculation runs a few
    /// frames after a change and, after a vessel event, only once
    /// <c>DELTAV_VESSEL_EVENT_DELAY_SECS</c> of <c>Time.time</c> has passed
    /// (<c>VesselDeltaV.CheckDirtyAndRun</c>), so a request for a state AFTER an event waits
    /// that delay plus <see cref="SettleMarginSeconds"/> before it reads, and a request for
    /// the state BEFORE an event (a burn start) reads on the first ready tick, before the new
    /// figures replace the old ones. A request still not readable
    /// <see cref="TimeoutSeconds"/> after its wait becomes an Unknown snapshot. All times are
    /// <c>Time.time</c> seconds: the clock stock's own delay runs on, which stands still while
    /// the game is paused.
    /// </summary>
    internal sealed class DeltaVSnapshotTracker
    {
        internal const double SettleMarginSeconds = 0.25;
        internal const double TimeoutSeconds = 3.0;
        internal const int MaxPendingRequests = 8;

        private struct PendingRequest
        {
            public DeltaVTrigger Trigger;
            public double RequestedAt;
            public int Ticks;
        }

        private readonly List<PendingRequest> pending = new List<PendingRequest>();
        private bool burningKnown;
        private bool lastBurning;

        internal int PendingCount => pending.Count;

        internal bool HasPending(DeltaVTrigger trigger)
        {
            for (int i = 0; i < pending.Count; i++)
                if (pending[i].Trigger == trigger)
                    return true;
            return false;
        }

        /// <summary>
        /// True for the triggers whose snapshot must describe the vessel AFTER the event, so
        /// they wait for the stock recalculation. A burn start describes the vessel before the
        /// burn, a fresh recording start follows no vessel event (waiting would let a launch
        /// burn run first and read the vessel after it), and the end of a recording has no
        /// later frame to wait for.
        /// </summary>
        internal static bool RequiresSettle(DeltaVTrigger trigger)
        {
            switch (trigger)
            {
                case DeltaVTrigger.Start:
                case DeltaVTrigger.BurnStart:
                case DeltaVTrigger.End:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Pure: whether a trigger may file a request. Null = accepted, otherwise the reason.
        /// No snapshots with the stock calculation off, none for an EVA kerbal (stock computes
        /// no useful figures for one, design 8.1), none once the recorder stopped.
        /// </summary>
        internal static string ClassifyRequest(bool isRecording, bool calculationsEnabled, bool isEva)
        {
            if (!isRecording)
                return "not-recording";
            if (!calculationsEnabled)
                return "calculations-disabled";
            if (isEva)
                return "eva-vessel";
            return null;
        }

        /// <summary>
        /// Files a request. A second request of a kind already pending keeps the first (its
        /// wait is already running); past <see cref="MaxPendingRequests"/> a request is refused.
        /// A burn start while a burn end is still pending means the engines came back before
        /// the end was read (a throttle flicker): the burn never ended, so the pending end is
        /// withdrawn and no start is filed ("burn-resumed"). Returns null when filed,
        /// otherwise the reason.
        /// </summary>
        internal string Request(DeltaVTrigger trigger, double now)
        {
            if (trigger == DeltaVTrigger.BurnStart && RemovePending(DeltaVTrigger.BurnEnd))
                return "burn-resumed";
            if (HasPending(trigger))
                return "coalesced";
            if (pending.Count >= MaxPendingRequests)
                return "pending-full";
            pending.Add(new PendingRequest { Trigger = trigger, RequestedAt = now, Ticks = 0 });
            return null;
        }

        private bool RemovePending(DeltaVTrigger trigger)
        {
            for (int i = 0; i < pending.Count; i++)
            {
                if (pending[i].Trigger == trigger)
                {
                    pending.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Seeds the engine state at recording start: a running engine there is no burn start.</summary>
        internal void InitializeEngineActivity(bool burning)
        {
            burningKnown = true;
            lastBurning = burning;
        }

        /// <summary>
        /// Threshold crossing of "any engine thrusting": returns BurnStart when engines go from
        /// none to some, BurnEnd when they go from some to none, null otherwise.
        /// </summary>
        internal DeltaVTrigger? NoteEngineActivity(bool burning)
        {
            if (!burningKnown)
            {
                InitializeEngineActivity(burning);
                return null;
            }
            if (burning == lastBurning)
                return null;
            lastBurning = burning;
            return burning ? DeltaVTrigger.BurnStart : DeltaVTrigger.BurnEnd;
        }

        /// <summary>Pure: one request's outcome at <paramref name="now"/>.</summary>
        internal static DeltaVRequestOutcome DecideOne(
            DeltaVTrigger trigger, double requestedAt, double now, DeltaVReadiness readiness)
        {
            if (!readiness.CalculationsEnabled)
                return DeltaVRequestOutcome.Dropped;

            double delay = readiness.EventDelaySeconds;
            if (double.IsNaN(delay) || double.IsInfinity(delay) || delay < 0.0)
                delay = 0.0;
            double minWait = RequiresSettle(trigger) ? delay + SettleMarginSeconds : 0.0;
            double elapsed = now - requestedAt;

            if (elapsed >= minWait && readiness.VesselEligible && readiness.Ready)
                return DeltaVRequestOutcome.Complete;
            if (elapsed >= minWait + TimeoutSeconds)
                return DeltaVRequestOutcome.TimedOut;
            return DeltaVRequestOutcome.Wait;
        }

        /// <summary>
        /// Decides every pending request and removes the ones that complete, time out or drop.
        /// The caller reads the live figures once when any decision is Complete.
        /// </summary>
        internal List<DeltaVDecision> Decide(double now, DeltaVReadiness readiness)
        {
            var decisions = new List<DeltaVDecision>(pending.Count);
            int write = 0;
            for (int i = 0; i < pending.Count; i++)
            {
                PendingRequest request = pending[i];
                DeltaVRequestOutcome outcome = DecideOne(request.Trigger, request.RequestedAt, now, readiness);
                if (outcome == DeltaVRequestOutcome.Wait)
                {
                    request.Ticks++;
                    pending[write++] = request;
                    continue;
                }
                decisions.Add(new DeltaVDecision
                {
                    Trigger = request.Trigger,
                    Outcome = outcome,
                    SettleTicks = request.Ticks,
                    WaitedSeconds = now - request.RequestedAt,
                });
            }
            pending.RemoveRange(write, pending.Count - write);
            return decisions;
        }

        /// <summary>
        /// The end of a recording reads at once: Complete when the game has figures for the
        /// recorded vessel, TimedOut (an Unknown snapshot) otherwise, Dropped with the stock
        /// calculation off.
        /// </summary>
        internal static DeltaVRequestOutcome DecideAtStop(DeltaVReadiness readiness)
        {
            if (!readiness.CalculationsEnabled)
                return DeltaVRequestOutcome.Dropped;
            return readiness.VesselEligible && readiness.Ready
                ? DeltaVRequestOutcome.Complete
                : DeltaVRequestOutcome.TimedOut;
        }

        /// <summary>Pure: a live read is usable only when its three totals are finite.</summary>
        internal static bool IsUsableReading(DeltaVReading reading)
        {
            return IsFinite(reading.TotalVacuum)
                && IsFinite(reading.TotalSeaLevel)
                && IsFinite(reading.TotalActual);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
