using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>
    /// Why a vessel died. KSP raises the same <c>onVesselWillDestroy</c> signal for a
    /// crash and for the EVA-construction pocket, so Parsek has to tell them apart at
    /// the destroy seam or seal both as <see cref="TerminalState.Destroyed"/>.
    /// </summary>
    internal enum VesselDeathKind
    {
        Destroyed = 0,
        Disassembled = 1,
    }

    /// <summary>
    /// The same-frame evidence read off the live KSP objects at
    /// <c>onVesselWillDestroy</c>. Extracted so the decision itself stays pure.
    /// </summary>
    internal struct VesselDeathEvidence
    {
        public bool EvaConstructionModeOpen;
        public int PartCount;
        public bool DyingPartIsCurrentCargoPart;
    }

    /// <summary>
    /// Detects the one death that is not a loss: the last remaining part of a
    /// <c>DroppedPart</c> / <c>Debris</c> vessel being stored into an inventory during
    /// EVA construction.
    ///
    /// <para><b>The KSP path (decompiled 1.12.5).</b>
    /// <c>EVAConstructionModeEditor.PickupPart()</c> is gated on the hovered part's
    /// vessel being <c>VesselType.DroppedPart</c> or <c>VesselType.Debris</c>. It
    /// builds a fresh <c>ProtoPartSnapshot</c> for the hovered part, assigns
    /// <c>UIPartActionControllerInventory.Instance.CurrentCargoPart =
    /// CreatePartFromInventory(snapshot)</c> and only THEN calls
    /// <c>hoveredPart.vessel.Die()</c>, which is the <c>onVesselWillDestroy</c>
    /// source. So at the destroy seam the held cargo part already exists and the
    /// snapshot's <c>partRef</c> points at it.</para>
    ///
    /// <para><b>Why the other candidate witnesses do not work.</b> The multi-part
    /// detach branch cannot reach <c>PickupPart</c> at all (the grab handler returns
    /// early when the hovered part IS the vessel root), so "last part pocketed" is
    /// always the single-part case. <c>persistentId</c> matching is unusable:
    /// <c>ProtoPartSnapshot.ConfigurePart</c> re-pids the created cargo part against
    /// the still-live original, so the two pids differ BY CONSTRUCTION.
    /// <c>GameEvents.OnEVAConstructionModePartDetached</c> never fires on this path,
    /// and <c>ConstructionEventType.PartPicked</c> fires AFTER <c>Die()</c>. Object
    /// identity between the dying part's <c>protoPartSnapshot.partRef</c> and
    /// <c>CurrentCargoPart</c> is the only synchronous positive, and it needs no
    /// Harmony patch.</para>
    /// </summary>
    internal static class VesselDisassemblyClassifier
    {
        /// <summary>
        /// PURE decision over the extracted evidence. All three conjuncts are
        /// required:
        ///
        /// <list type="bullet">
        ///   <item><description><paramref name="evaConstructionModeOpen"/> - the pickup
        ///   path only runs while the EVA construction panel is open, so a crash that
        ///   happens to coincide with a stale cargo reference cannot pass.</description></item>
        ///   <item><description><paramref name="partCount"/> == 1 - this is the LAST
        ///   part. A multi-part vessel losing one part is not a vessel ending, and
        ///   the KSP path cannot produce it anyway.</description></item>
        ///   <item><description><paramref name="dyingPartIsCurrentCargoPart"/> - the
        ///   dying part's proto snapshot points at the part KSP just handed to the
        ///   kerbal, which is the positive evidence that this death IS the pickup.</description></item>
        /// </list>
        ///
        /// Anything else is <see cref="VesselDeathKind.Destroyed"/>: the classifier
        /// fails CLOSED, so an unreadable or unexpected state keeps today's behavior.
        /// </summary>
        internal static VesselDeathKind ClassifyVesselDeath(
            bool evaConstructionModeOpen,
            int partCount,
            bool dyingPartIsCurrentCargoPart)
        {
            if (!evaConstructionModeOpen) return VesselDeathKind.Destroyed;
            if (partCount != 1) return VesselDeathKind.Destroyed;
            if (!dyingPartIsCurrentCargoPart) return VesselDeathKind.Destroyed;
            return VesselDeathKind.Disassembled;
        }

        /// <summary>Convenience overload over a captured <see cref="VesselDeathEvidence"/>.</summary>
        internal static VesselDeathKind ClassifyVesselDeath(VesselDeathEvidence evidence)
        {
            return ClassifyVesselDeath(
                evidence.EvaConstructionModeOpen,
                evidence.PartCount,
                evidence.DyingPartIsCurrentCargoPart);
        }

        /// <summary>
        /// Live read of the two KSP UI singletons plus the dying vessel's own parts.
        /// <see cref="MethodImplOptions.NoInlining"/> keeps the static-field touches
        /// out of the caller's JIT unit: mono runs a failing type initializer at JIT
        /// of the CALLING method, so an inlined read would take the headless suite
        /// down with it (same contract as
        /// <c>RecordingStore.ReadUnityApplicationIsPlayingCore</c>).
        ///
        /// Returns false when the evidence could not be read at all; the caller then
        /// treats the death as an ordinary destruction.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool TryReadVesselDeathEvidenceCore(Vessel v, out VesselDeathEvidence evidence)
        {
            evidence = default(VesselDeathEvidence);
            if (v == null) return false;

            try
            {
                var modeController = EVAConstructionModeController.Instance;
                evidence.EvaConstructionModeOpen = modeController != null && modeController.IsOpen;

                evidence.PartCount = v.parts != null ? v.parts.Count : 0;

                // Only the single-part case can be the pickup, and reading parts[0]
                // for any other shape would be a guess about which part died.
                if (evidence.PartCount == 1)
                {
                    var inventoryController = UIPartActionControllerInventory.Instance;
                    Part heldCargo = inventoryController != null
                        ? inventoryController.CurrentCargoPart
                        : null;
                    Part dyingPart = v.parts[0];
                    evidence.DyingPartIsCurrentCargoPart =
                        heldCargo != null
                        && dyingPart != null
                        && dyingPart.protoPartSnapshot != null
                        && ReferenceEquals(dyingPart.protoPartSnapshot.partRef, heldCargo);
                }

                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Disassembly",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Death-evidence read failed for pid={0} vessel='{1}' - treating as Destroyed: {2}",
                        v.persistentId,
                        v.vesselName ?? "(null)",
                        ex.Message));
                evidence = default(VesselDeathEvidence);
                return false;
            }
        }

        /// <summary>
        /// Live classification: read the evidence, then decide. Fails closed to
        /// <see cref="VesselDeathKind.Destroyed"/> on an unreadable state.
        /// </summary>
        internal static VesselDeathKind ClassifyLiveVesselDeath(Vessel v, out VesselDeathEvidence evidence)
        {
            if (!TryReadVesselDeathEvidenceCore(v, out evidence))
                return VesselDeathKind.Destroyed;
            return ClassifyVesselDeath(evidence);
        }
    }
}
