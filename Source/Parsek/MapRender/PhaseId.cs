using System;
using System.Globalization;

namespace Parsek.MapRender
{
    /// <summary>
    /// Phase 1 / design §6: a stable RUNTIME render-layer identity for a <see cref="TrajectoryPhase"/>.
    ///
    /// <para><b>This is render-layer identity ONLY.</b> It is explicitly NOT the persisted
    /// <c>Mission.ExcludedIntervalKeys</c> / <c>&lt;head&gt;</c>/<c>segN</c> selection keys — those
    /// remain a composition-layer persistence concern (design §4 / §6 / §12). A <see cref="PhaseId"/>
    /// is never serialized; it identifies a phase within an in-memory <see cref="PhaseChain"/> for
    /// debugging, seam wiring (a seam can name its neighbour phase), and trace lines.</para>
    ///
    /// <para>Built from the owning recording id + an ordinal within the chain + an instance-key
    /// discriminator so overlapping self-loop instances (design §10.8 / the
    /// <see cref="PhaseChain.InstanceKey"/>) of one recording stay distinct. Value-equatable so it can
    /// key a dictionary / be asserted in tests.</para>
    /// </summary>
    internal readonly struct PhaseId : IEquatable<PhaseId>
    {
        /// <summary>The owning recording's id (matches <see cref="PhaseChain.RecordingId"/>).</summary>
        internal string RecordingId { get; }

        /// <summary>The instance-key discriminator (matches <see cref="PhaseChain.InstanceKey"/>).</summary>
        internal int InstanceKey { get; }

        /// <summary>The phase's ordinal within its chain (0-based, ascending by StartUt).</summary>
        internal int Ordinal { get; }

        internal PhaseId(string recordingId, int instanceKey, int ordinal)
        {
            RecordingId = recordingId;
            InstanceKey = instanceKey;
            Ordinal = ordinal;
        }

        /// <summary>True for the default / unset id.</summary>
        internal bool IsEmpty => RecordingId == null && InstanceKey == 0 && Ordinal == 0;

        public bool Equals(PhaseId other)
            => string.Equals(RecordingId, other.RecordingId, StringComparison.Ordinal)
               && InstanceKey == other.InstanceKey
               && Ordinal == other.Ordinal;

        public override bool Equals(object obj) => obj is PhaseId other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = RecordingId == null ? 0 : RecordingId.GetHashCode();
                h = (h * 397) ^ InstanceKey;
                h = (h * 397) ^ Ordinal;
                return h;
            }
        }

        public override string ToString()
            => string.Format(
                CultureInfo.InvariantCulture,
                "{0}#i{1}#p{2}",
                RecordingId ?? "?", InstanceKey, Ordinal);
    }
}
