using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>A part-local axis-aligned box, min and max corner, metres.</summary>
    internal sealed class PartParityAabb
    {
        internal readonly double[] Min = { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        internal readonly double[] Max = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };

        internal bool IsEmpty => Min[0] > Max[0] || Min[1] > Max[1] || Min[2] > Max[2];

        internal static PartParityAabb FromMinMax(double[] min, double[] max)
        {
            var b = new PartParityAabb();
            if (min == null || max == null || min.Length != 3 || max.Length != 3) return b;
            for (int i = 0; i < 3; i++)
            {
                if (double.IsNaN(min[i]) || double.IsNaN(max[i]) || double.IsInfinity(min[i]) || double.IsInfinity(max[i]))
                    return new PartParityAabb();
                b.Min[i] = Math.Min(min[i], max[i]);
                b.Max[i] = Math.Max(min[i], max[i]);
            }
            return b;
        }

        internal void Encapsulate(PartParityAabb other)
        {
            if (other == null || other.IsEmpty) return;
            for (int i = 0; i < 3; i++)
            {
                if (other.Min[i] < Min[i]) Min[i] = other.Min[i];
                if (other.Max[i] > Max[i]) Max[i] = other.Max[i];
            }
        }

        internal bool Contains(PartParityAabb inner)
        {
            if (IsEmpty || inner == null || inner.IsEmpty) return false;
            for (int i = 0; i < 3; i++)
                if (inner.Min[i] < Min[i] || inner.Max[i] > Max[i]) return false;
            return true;
        }

        internal double[] Center()
            => IsEmpty ? null : new[] { (Min[0] + Max[0]) / 2, (Min[1] + Max[1]) / 2, (Min[2] + Max[2]) / 2 };

        internal double[] Extents()
            => IsEmpty ? null : new[] { (Max[0] - Min[0]) / 2, (Max[1] - Min[1]) / 2, (Max[2] - Min[2]) / 2 };

        internal double[] Size()
            => IsEmpty ? null : new[] { Max[0] - Min[0], Max[1] - Min[1], Max[2] - Min[2] };

        internal PartParityBox ToBox()
            => IsEmpty ? null : new PartParityBox { Center = Center(), Extents = Extents() };

        internal PartParityAabb Copy()
        {
            var b = new PartParityAabb();
            b.Encapsulate(this);
            return b;
        }
    }

    /// <summary>A real renderer the framing rule left out of the real box.</summary>
    internal sealed class PartParityDroppedRenderer
    {
        internal string Key;
        internal double[] Size;
    }

    /// <summary>What <see cref="PartParityFramingRule.Decide"/> found.</summary>
    internal sealed class PartParityFramingDecision
    {
        /// <summary>The structural diff the pairing came from (the sidecar's <c>structure</c>).</summary>
        internal PartParityStructureDiff Structure;

        /// <summary>Rendered PAIRED real renderers only (the reference of the 2x rule).</summary>
        internal PartParityAabb PairedReal = new PartParityAabb();

        /// <summary>Paired real plus every kept unpaired real renderer.</summary>
        internal PartParityAabb Real = new PartParityAabb();
        internal PartParityAabb Ghost = new PartParityAabb();
        internal PartParityAabb Shared = new PartParityAabb();
        internal List<PartParityDroppedRenderer> Dropped = new List<PartParityDroppedRenderer>();

        /// <summary>The ghost box exceeds the real one by more than
        /// <see cref="PartParityFramingRule.GhostOutsideTolerance"/> of the real extent.</summary>
        internal bool GhostOutsideReal;
    }

    /// <summary>
    /// The framing rule of one case (design-part-ghost-parity.md 4.2 steps 1 and 4).
    /// Pure. Only RENDERED records with bounds frame.
    ///
    /// <para>Rule: pair the two sides' renderer records first
    /// (<see cref="PartParityStructure.Diff"/>). The reference box is the union of the
    /// rendered PAIRED real renderers (when none paired, the ghost box; when that is empty
    /// too, nothing is dropped). An UNPAIRED real renderer joins the real box only when its
    /// bounds lie inside the reference box grown to twice its extent about its centre (per
    /// axis at least the reference's largest half-extent, so a flat reference does not drop
    /// everything off its plane). Every other unpaired real renderer is dropped and named:
    /// on the modded install a ReStock LV-T45 carries 18 extra mesh renderers (most likely
    /// Waterfall plume meshes, invisible at zero throttle) that blew a 2 m part's box to
    /// 10 m (P0, Appendix B finding 2). The shared box is the union of the ghost box and
    /// the real box; one box frames both sides, nothing re-centred.</para>
    /// </summary>
    internal static class PartParityFramingRule
    {
        /// <summary>Reference growth factor for an unpaired real renderer.</summary>
        internal const double UnpairedReachFactor = 2.0;

        /// <summary>Fraction of the real box's largest extent the ghost box may exceed it by
        /// before <c>ghost-outside-real-bounds</c> (design 4.2 step 4: 2 %).</summary>
        internal const double GhostOutsideTolerance = 0.02;

        internal const string FlagGhostOutsideRealBounds = "ghost-outside-real-bounds";
        internal const string FlagDroppedPrefix = "framing-dropped:";

        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        internal static PartParityAabb BoundsOf(PartParityRendererRecord r)
            => r == null ? new PartParityAabb() : PartParityAabb.FromMinMax(r.BoundsMin, r.BoundsMax);

        internal static PartParityFramingDecision Decide(
            IList<PartParityRendererRecord> real, IList<PartParityRendererRecord> ghost)
        {
            var d = new PartParityFramingDecision();
            d.Structure = PartParityStructure.Diff(real, ghost);
            var missing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in d.Structure.MissingOnGhost) missing.Add(m.Key);

            var unpaired = new List<KeyValuePair<string, PartParityRendererRecord>>();
            foreach (var kv in PartParityStructure.Keyed(real))
            {
                if (!kv.Value.Rendered) continue;
                if (missing.Contains(kv.Key)) unpaired.Add(kv);
                else d.PairedReal.Encapsulate(BoundsOf(kv.Value));
            }
            if (ghost != null)
                foreach (var g in ghost)
                    if (g != null && g.Rendered) d.Ghost.Encapsulate(BoundsOf(g));

            PartParityAabb reference = !d.PairedReal.IsEmpty ? d.PairedReal : d.Ghost;
            PartParityAabb limit = reference.IsEmpty ? null : Grow(reference, UnpairedReachFactor);
            d.Real.Encapsulate(d.PairedReal);
            foreach (var kv in unpaired)
            {
                PartParityAabb b = BoundsOf(kv.Value);
                if (b.IsEmpty) continue;
                if (limit == null || limit.Contains(b))
                    d.Real.Encapsulate(b);
                else
                    d.Dropped.Add(new PartParityDroppedRenderer { Key = kv.Key, Size = b.Size() });
            }
            d.Shared.Encapsulate(d.Real);
            d.Shared.Encapsulate(d.Ghost);
            d.GhostOutsideReal = IsGhostOutside(d.Real, d.Ghost);
            return d;
        }

        /// <summary><paramref name="box"/> scaled by <paramref name="factor"/> about its
        /// centre, each half-extent at least the box's largest half-extent.</summary>
        internal static PartParityAabb Grow(PartParityAabb box, double factor)
        {
            double[] c = box.Center(), e = box.Extents();
            double emax = Math.Max(e[0], Math.Max(e[1], e[2]));
            var g = new PartParityAabb();
            for (int i = 0; i < 3; i++)
            {
                double h = Math.Max(e[i] * factor, emax);
                g.Min[i] = c[i] - h;
                g.Max[i] = c[i] + h;
            }
            return g;
        }

        /// <summary>True when a non-empty ghost box pokes out of the real box by more than
        /// the tolerance (an empty real box with a ghost counts as outside).</summary>
        internal static bool IsGhostOutside(PartParityAabb real, PartParityAabb ghost)
        {
            if (ghost == null || ghost.IsEmpty) return false;
            if (real == null || real.IsEmpty) return true;
            double[] e = real.Extents();
            double tol = GhostOutsideTolerance * Math.Max(e[0], Math.Max(e[1], e[2])) * 2.0;
            for (int i = 0; i < 3; i++)
                if (ghost.Min[i] < real.Min[i] - tol || ghost.Max[i] > real.Max[i] + tol) return true;
            return false;
        }

        /// <summary>The sidecar flag naming one dropped renderer:
        /// <c>framing-dropped:&lt;key&gt;=&lt;sx&gt;x&lt;sy&gt;x&lt;sz&gt;</c> (metres, 3 decimals).</summary>
        internal static string DroppedFlag(PartParityDroppedRenderer d)
        {
            string size = d.Size == null ? "?" :
                d.Size[0].ToString("0.000", Ic) + "x" + d.Size[1].ToString("0.000", Ic) + "x" + d.Size[2].ToString("0.000", Ic);
            return FlagDroppedPrefix + (d.Key ?? string.Empty) + "=" + size;
        }
    }
}
