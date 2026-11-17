using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.Tests.Race.Sim
{
    /// <summary>What a random draw of the simulator decides.</summary>
    internal enum SimDrawPurpose
    {
        Delay,
        Reorder,
        Partial,
        PartialLength,
        Placeholder,
    }

    /// <summary>One recorded random draw: what it decided, its range and the value drawn.</summary>
    internal struct SimDraw
    {
        public SimDrawPurpose Purpose;
        public int Bound;
        public int Value;

        public string ToText()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}/{1}={2}", Purpose, Bound, Value);
        }
    }

    /// <summary>
    /// Everything that happened in a simulator run, in order. The kinds named in
    /// <see cref="SimSchedule.ControlKinds"/> are the fake store's controls: a run over a seed
    /// set can count each to prove it fired at least once.
    /// </summary>
    internal enum SimEventKind
    {
        LocalWrite,
        LocalDelete,
        Coalesced,
        UploadQueued,
        DownloadQueued,
        Delayed,
        Reordered,
        UploadApplied,
        DeleteApplied,
        ConflictCopy,
        DeleteLost,
        Downloaded,
        DownloadDeleted,
        PartialLanded,
        RestLanded,
        PlaceholderLanded,
        PlaceholderOpened,
        SkippedStale,
        SkippedDirty,
        DroppedOffline,
        MachineOffline,
        MachineOnline,
        ForeignEdit,
        ForeignRename,
        ForeignDelete,
    }

    internal sealed class SimEvent
    {
        public int Index;
        public long Tick;
        public SimEventKind Kind;
        public string Machine;
        public string Path;
        public long Version;
        public long Detail;

        public string ToText()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0} t={1} {2} m={3} p={4} v={5} d={6}",
                Index, Tick, Kind, Machine ?? "-", Path ?? "-", Version, Detail);
        }
    }

    /// <summary>
    /// The seeded source of every random decision of the fake synced store, and the log of
    /// every draw and every event. Same seed and same operations give the same draws and the
    /// same events on every host: the generator is SplitMix64 written out here (not
    /// <see cref="System.Random"/>, whose sequence is a runtime detail), nothing reads a
    /// clock, a culture or a thread, and nothing enumerates a hash-ordered collection.
    /// <see cref="Replay"/> runs the same operations again from the recorded draws, and
    /// throws at the first draw or event that differs from the record; a run that stops
    /// short does not throw, so a caller checks <see cref="ReplayComplete"/> at the end.
    /// </summary>
    internal sealed class SimSchedule
    {
        /// <summary>The event kinds that witness one fake-store control having fired.</summary>
        public static readonly SimEventKind[] ControlKinds =
        {
            SimEventKind.Delayed,
            SimEventKind.Reordered,
            SimEventKind.DroppedOffline,
            SimEventKind.PartialLanded,
            SimEventKind.PlaceholderLanded,
            SimEventKind.ConflictCopy,
            SimEventKind.ForeignEdit,
            SimEventKind.ForeignRename,
            SimEventKind.LocalDelete,
        };

        private ulong state;
        private readonly List<SimDraw> draws = new List<SimDraw>();
        private readonly List<SimEvent> events = new List<SimEvent>();
        private readonly List<SimDraw> replaySource;
        private readonly List<SimEvent> replayEvents;
        private int replayPosition;

        private SimSchedule(long seed, List<SimDraw> replaySource, List<SimEvent> replayEvents)
        {
            Seed = seed;
            state = unchecked((ulong)seed);
            this.replaySource = replaySource;
            this.replayEvents = replayEvents;
        }

        public long Seed { get; }

        public bool IsReplay => replaySource != null;

        public IReadOnlyList<SimDraw> Draws => draws;

        public IReadOnlyList<SimEvent> Events => events;

        /// <summary>Draws a replay has not consumed yet (0 for a seeded schedule).</summary>
        public int ReplayRemaining => replaySource == null ? 0 : replaySource.Count - replayPosition;

        /// <summary>True when a replay has consumed every recorded draw and event (always true for a seeded schedule).</summary>
        public bool ReplayComplete => replaySource == null
            || (replayPosition == replaySource.Count && events.Count == replayEvents.Count);

        public static SimSchedule FromSeed(long seed)
        {
            return new SimSchedule(seed, null, null);
        }

        public static SimSchedule Replay(SimSchedule recorded)
        {
            if (recorded == null)
                throw new ArgumentNullException(nameof(recorded));
            return new SimSchedule(recorded.Seed, new List<SimDraw>(recorded.draws), new List<SimEvent>(recorded.events));
        }

        /// <summary>A value in [0, bound), recorded under its purpose.</summary>
        internal int Next(SimDrawPurpose purpose, int bound)
        {
            if (bound < 1)
                throw new ArgumentOutOfRangeException(nameof(bound), "bound must be at least 1");
            int value;
            if (replaySource != null)
            {
                if (replayPosition >= replaySource.Count)
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                        "replay diverged at draw {0}: the run asked for {1}/{2} after the recorded draws ran out",
                        replayPosition, purpose, bound));
                SimDraw recorded = replaySource[replayPosition];
                if (recorded.Purpose != purpose || recorded.Bound != bound)
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                        "replay diverged at draw {0}: the run asked for {1}/{2}, the record holds {3}",
                        replayPosition, purpose, bound, recorded.ToText()));
                replayPosition++;
                value = recorded.Value;
            }
            else
            {
                value = (int)(NextUInt64() % (ulong)bound);
            }
            draws.Add(new SimDraw { Purpose = purpose, Bound = bound, Value = value });
            return value;
        }

        /// <summary>True with the given chance in thousandths. 0 and 1000 draw nothing.</summary>
        internal bool Chance(SimDrawPurpose purpose, int perMille)
        {
            if (perMille <= 0)
                return false;
            if (perMille >= 1000)
                return true;
            return Next(purpose, 1000) < perMille;
        }

        /// <summary>A value in [min, max]. Equal bounds draw nothing.</summary>
        internal int Between(SimDrawPurpose purpose, int min, int max)
        {
            if (max <= min)
                return min;
            return min + Next(purpose, max - min + 1);
        }

        internal void Record(long tick, SimEventKind kind, string machine, string path, long version, long detail)
        {
            var e = new SimEvent
            {
                Index = events.Count,
                Tick = tick,
                Kind = kind,
                Machine = machine,
                Path = path,
                Version = version,
                Detail = detail,
            };
            if (replayEvents != null)
            {
                if (e.Index >= replayEvents.Count)
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                        "replay diverged at event {0}: the run recorded '{1}' after the recorded events ran out",
                        e.Index, e.ToText()));
                string expected = replayEvents[e.Index].ToText();
                if (!string.Equals(expected, e.ToText(), StringComparison.Ordinal))
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                        "replay diverged at event {0}: the run recorded '{1}', the record holds '{2}'",
                        e.Index, e.ToText(), expected));
            }
            events.Add(e);
        }

        public int Count(SimEventKind kind)
        {
            int count = 0;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].Kind == kind)
                    count++;
            }
            return count;
        }

        /// <summary>The event log, one line per event, invariant text.</summary>
        public string ToText()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < events.Count; i++)
                sb.Append(events[i].ToText()).Append('\n');
            return sb.ToString();
        }

        /// <summary>The seed and the event log, for a failure message.</summary>
        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "seed={0} replay={1} draws={2} events={3}\n{4}",
                Seed, IsReplay, draws.Count, events.Count, ToText());
        }

        private ulong NextUInt64()
        {
            unchecked
            {
                state += 0x9E3779B97F4A7C15UL;
                ulong z = state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }
    }
}
