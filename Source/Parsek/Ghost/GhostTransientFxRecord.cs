using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// What kind of one-shot effect Parsek started (design-part-ghost-parity.md G.2.1).
    /// </summary>
    internal enum GhostTransientFxKind
    {
        /// <summary>A stock FXMonger explosion Parsek queued (no GameObject until stock's LateUpdate).</summary>
        StockExplosion = 0,
        /// <summary>Parsek's own fireball (<c>GhostVisualBuilder.SpawnExplosionFx</c>).</summary>
        CustomExplosion = 1,
        /// <summary>Parsek's part puff (<c>GhostVisualBuilder.SpawnPartPuffFx</c>).</summary>
        PartPuff = 2,
    }

    /// <summary>One effect Parsek started, as it was started.</summary>
    internal struct GhostTransientFxEntry
    {
        /// <summary>Position in the record, from 0, in start order.</summary>
        public int Sequence;
        public GhostTransientFxKind Kind;
        /// <summary>The world position the effect was started at.</summary>
        public Vector3 Position;
        /// <summary>The power passed to <c>FXMonger.Explode</c>; NaN for Parsek's own effects.</summary>
        public double Power;
        /// <summary>The size argument of Parsek's own effects (vessel length for the fireball,
        /// part scale before the clamp for the puff); NaN for a stock explosion.</summary>
        public float Size;
        /// <summary>The effect's GameObject; null for a stock explosion.</summary>
        public GameObject Object;
    }

    /// <summary>
    /// A read-only, insert-only record of every explosion and part puff Parsek starts, for tests
    /// and the part parity burst driver (G.2.1's schedule and attribution). It records only while
    /// armed (<see cref="Arm"/> opens a fresh record, <see cref="Disarm"/> drops it), because an
    /// always-on list holding GameObjects would grow for the whole session; unarmed, a start costs
    /// one null check. Readers get the entries in start order and cannot change them, and noting
    /// an entry changes nothing about what is spawned.
    /// </summary>
    internal static class GhostTransientFxRecord
    {
        private static List<GhostTransientFxEntry> entries;
        private static ReadOnlyCollection<GhostTransientFxEntry> view;
        private static readonly ReadOnlyCollection<GhostTransientFxEntry> Empty =
            new ReadOnlyCollection<GhostTransientFxEntry>(new GhostTransientFxEntry[0]);

        internal static bool IsArmed => entries != null;

        /// <summary>The entries since the last <see cref="Arm"/>, in start order; empty unarmed.</summary>
        internal static IReadOnlyList<GhostTransientFxEntry> Entries => view ?? Empty;

        /// <summary>Opens a fresh, empty record; an open one is dropped.</summary>
        internal static void Arm()
        {
            int dropped = entries?.Count ?? -1;
            entries = new List<GhostTransientFxEntry>();
            view = entries.AsReadOnly();
            ParsekLog.Verbose("TransientFx",
                dropped < 0
                    ? "Transient FX record armed"
                    : $"Transient FX record re-armed (dropped {dropped.ToString(CultureInfo.InvariantCulture)} entries)");
        }

        /// <summary>Stops recording and drops the record.</summary>
        internal static void Disarm()
        {
            if (entries == null) return;
            int count = entries.Count;
            entries = null;
            view = null;
            ParsekLog.Verbose("TransientFx",
                $"Transient FX record disarmed after {count.ToString(CultureInfo.InvariantCulture)} entries");
        }

        /// <summary>Appends one started effect while armed; does nothing unarmed.</summary>
        internal static void Note(
            GhostTransientFxKind kind, Vector3 position, double power, float size, GameObject obj)
        {
            if (entries == null) return;
            var entry = new GhostTransientFxEntry
            {
                Sequence = entries.Count,
                Kind = kind,
                Position = position,
                Power = power,
                Size = size,
                Object = obj,
            };
            entries.Add(entry);
            ParsekLog.Verbose("TransientFx",
                $"Transient FX #{entry.Sequence.ToString(CultureInfo.InvariantCulture)} {kind} at (" +
                position.x.ToString("F2", CultureInfo.InvariantCulture) + "," +
                position.y.ToString("F2", CultureInfo.InvariantCulture) + "," +
                position.z.ToString("F2", CultureInfo.InvariantCulture) + ") power=" +
                power.ToString("R", CultureInfo.InvariantCulture) + " size=" +
                size.ToString("R", CultureInfo.InvariantCulture) +
                " object=" + (ReferenceEquals(obj, null) ? "no" : "yes"));
        }
    }
}
