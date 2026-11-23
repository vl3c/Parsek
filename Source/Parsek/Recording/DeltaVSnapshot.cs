using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// What made the recorder read the game's delta-v (design-mission-profile.md section 8.1).
    /// Stored by name, so a new member is additive; a name this build does not know is skipped
    /// on load like any malformed line.
    /// </summary>
    internal enum DeltaVTrigger
    {
        Start,
        BurnStart,
        BurnEnd,
        Staging,
        Separation,
        Dock,
        Undock,
        Board,
        Eva,
        SwitchIn,
        Resume,
        End,
        // The vessel just before a staging: the recorder's last ready reading, taken at most
        // DeltaVSnapshotTracker.CachedReadingMaxAgeSeconds before the staging and stamped with
        // that reading's UT. Paired with the settled Staging read after it, it gives the fall
        // in Left the dropped parts caused.
        PreStaging,
    }

    /// <summary>
    /// Ok: the stock calculation was ready and its figures were read. Unknown: the game did
    /// not settle in time (or the vessel could not be read), so the snapshot carries no
    /// figures.
    /// </summary>
    internal enum DeltaVSnapshotStatus
    {
        Ok,
        Unknown,
    }

    /// <summary>One stage of the stock Delta-V readout (<c>DeltaVStageInfo</c>).</summary>
    internal struct DeltaVStageReading
    {
        public readonly int Stage;
        public readonly double VacuumDeltaV;
        public readonly double BurnTimeSeconds;

        public DeltaVStageReading(int stage, double vacuumDeltaV, double burnTimeSeconds)
        {
            Stage = stage;
            VacuumDeltaV = vacuumDeltaV;
            BurnTimeSeconds = burnTimeSeconds;
        }
    }

    /// <summary>
    /// The game's own delta-v readout at one moment of a focused recording. Immutable, so a
    /// shallow list copy is a full copy. Display data only: playback, spawning, the ledger and
    /// the optimizer never read it. The status is derived, not stored: an Unknown snapshot is
    /// one with no figures (NaN totals, no stages), so no field can disagree with it.
    /// </summary>
    internal sealed class DeltaVSnapshot
    {
        private static readonly DeltaVStageReading[] NoStages = new DeltaVStageReading[0];

        public readonly double UT;
        public readonly DeltaVTrigger Trigger;
        public readonly double TotalVacuum;
        public readonly double TotalSeaLevel;
        public readonly double TotalActual;
        public readonly IReadOnlyList<DeltaVStageReading> Stages;

        public DeltaVSnapshot(
            double ut,
            DeltaVTrigger trigger,
            DeltaVSnapshotStatus status,
            double totalVacuum,
            double totalSeaLevel,
            double totalActual,
            IList<DeltaVStageReading> stages)
        {
            UT = ut;
            Trigger = trigger;
            bool unknown = status == DeltaVSnapshotStatus.Unknown
                || !IsFinite(totalVacuum) || !IsFinite(totalSeaLevel) || !IsFinite(totalActual);
            TotalVacuum = unknown ? double.NaN : totalVacuum;
            TotalSeaLevel = unknown ? double.NaN : totalSeaLevel;
            TotalActual = unknown ? double.NaN : totalActual;
            if (unknown || stages == null || stages.Count == 0)
            {
                Stages = NoStages;
            }
            else
            {
                var copy = new DeltaVStageReading[stages.Count];
                stages.CopyTo(copy, 0);
                Stages = copy;
            }
        }

        public DeltaVSnapshotStatus Status =>
            IsFinite(TotalVacuum) ? DeltaVSnapshotStatus.Ok : DeltaVSnapshotStatus.Unknown;

        public bool IsOk => Status == DeltaVSnapshotStatus.Ok;

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>A snapshot with no figures: the game did not settle in time.</summary>
        public static DeltaVSnapshot Unknown(double ut, DeltaVTrigger trigger)
        {
            return new DeltaVSnapshot(
                ut, trigger, DeltaVSnapshotStatus.Unknown, double.NaN, double.NaN, double.NaN, null);
        }

        /// <summary>An Ok snapshot stamped with <paramref name="ut"/> from one live read.</summary>
        public static DeltaVSnapshot FromReading(double ut, DeltaVTrigger trigger, DeltaVReading reading)
        {
            return new DeltaVSnapshot(
                ut, trigger, DeltaVSnapshotStatus.Ok,
                reading.TotalVacuum, reading.TotalSeaLevel, reading.TotalActual, reading.Stages);
        }
    }

    /// <summary>The figures one live read of <c>VesselDeltaV</c> returned.</summary>
    internal struct DeltaVReading
    {
        public double TotalVacuum;
        public double TotalSeaLevel;
        public double TotalActual;
        public List<DeltaVStageReading> Stages;
    }
}
