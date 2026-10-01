using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Where a spawn-blocked chain tip's held ghost is drawn each frame (design 12.9.2 / 13.5).
    /// </summary>
    internal enum BlockedChainTipPoseSource
    {
        /// <summary>
        /// The tip spawns from its recorded terminal orbit, so the ghost follows that orbit
        /// propagated to the current UT: the position the spawn would use right now.
        /// </summary>
        RecordedTerminalOrbit,

        /// <summary>
        /// Everything else (landed, splashed, an orbit with no resolvable terminal orbit):
        /// the ghost stays at its recorded end pose, held body-fixed.
        /// </summary>
        BodyFixedHold,
    }

    /// <summary>
    /// Pure decisions for the ghost of a ghost-chain tip whose real-vessel spawn is blocked by
    /// a collision (phase 6b-4, design 12.9.2 and 13.5). The policy holds the tip recording's
    /// own playback ghost past its end UT while the chain retries the spawn; these helpers
    /// decide where that ghost is drawn and when the scene lets go of it. No Unity or KSP calls.
    /// </summary>
    internal static class ChainTipBlockedGhost
    {
        internal const string Tag = "ChainTipGhost";

        /// <summary>The tip recording now has a real vessel (spawned, walked back or adopted).</summary>
        internal const string ReleaseSpawned = "spawned";

        /// <summary>The chain left the active set (KSC retirement, rebuilt or cleared chains).</summary>
        internal const string ReleaseChainClosed = "chain-closed";

        /// <summary>The chain is still active but no longer collision-blocked.</summary>
        internal const string ReleaseUnblocked = "unblocked";

        /// <summary>The committed list shifted, so the stored index is no longer the tip.</summary>
        internal const string ReleaseIndexShifted = "index-shifted";

        /// <summary>
        /// The policy no longer holds the ghost (superseded, rewind-retired, invalid index, a
        /// permanent cannot-spawn-safely verdict, or the ghost was destroyed externally).
        /// </summary>
        internal const string ReleaseHoldEnded = "hold-ended";

        /// <summary>The engine slot no longer carries the ghost this hold captured.</summary>
        internal const string ReleaseGhostGone = "ghost-gone";

        /// <summary>
        /// Mirrors the spawn's own choice in <c>VesselGhoster.TrySpawnBlockedChain</c>: an
        /// Orbiting tip with a recorded terminal orbit spawns from that orbit propagated to the
        /// current UT, so the ghost follows it. Any other tip spawns at a fixed end position, so
        /// the ghost holds its end pose.
        /// </summary>
        internal static BlockedChainTipPoseSource ResolvePoseSource(
            bool spawnUsesRecordedTerminalOrbit, bool terminalOrbitBuilt)
        {
            return spawnUsesRecordedTerminalOrbit && terminalOrbitBuilt
                ? BlockedChainTipPoseSource.RecordedTerminalOrbit
                : BlockedChainTipPoseSource.BodyFixedHold;
        }

        /// <summary>
        /// Why a blocked-tip ghost must stop being drawn, or null to keep drawing it. The
        /// spawned check comes first so a held ghost never outlives its real vessel; the
        /// remaining checks cover every way the hold can lose its subject.
        /// </summary>
        internal static string DecideReleaseReason(
            bool tipSpawned,
            bool chainActive,
            bool chainSpawnBlocked,
            bool indexStillTip,
            bool heldByPolicy,
            bool engineGhostIntact)
        {
            if (tipSpawned)
                return ReleaseSpawned;
            if (!chainActive)
                return ReleaseChainClosed;
            if (!chainSpawnBlocked)
                return ReleaseUnblocked;
            if (!indexStillTip)
                return ReleaseIndexShifted;
            if (!heldByPolicy)
                return ReleaseHoldEnded;
            if (!engineGhostIntact)
                return ReleaseGhostGone;
            return null;
        }

        /// <summary>
        /// A held ghost becomes a blocked-tip ghost when nothing would release it at once:
        /// the same predicate as <see cref="DecideReleaseReason"/>, so a capture can never be
        /// followed by an immediate release in the same frame.
        /// </summary>
        internal static bool ShouldCapture(
            bool alreadyCaptured,
            bool tipSpawned,
            bool chainActive,
            bool chainSpawnBlocked,
            bool heldByPolicy,
            bool engineHasGhost)
        {
            return !alreadyCaptured
                && DecideReleaseReason(
                    tipSpawned,
                    chainActive,
                    chainSpawnBlocked,
                    indexStillTip: true,
                    heldByPolicy: heldByPolicy,
                    engineGhostIntact: engineHasGhost) == null;
        }

        /// <summary>
        /// The held-ghost timeout does not apply to a collision-blocked chain tip: the blocker
        /// can move away at any time, and the design keeps the ghost until the overlap clears
        /// (design 12.9.2). Permanent refusals still release it through their own checks.
        /// </summary>
        internal static bool IsExemptFromHeldGhostTimeout(bool spawnBlockedChainTip)
        {
            return spawnBlockedChainTip;
        }

        internal static string FormatPoseSource(BlockedChainTipPoseSource source)
        {
            return source == BlockedChainTipPoseSource.RecordedTerminalOrbit ? "orbit" : "hold";
        }

        internal static string BuildHeldMessage(
            int index, string recordingId, string vesselName, uint chainPid,
            BlockedChainTipPoseSource source, string bodyName, double currentUT,
            double blockedSinceUT)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Blocked chain tip ghost held: #{0} \"{1}\" rec={2} chainPid={3} source={4} body={5} " +
                "blockedSince={6:F1} UT={7:F1} - the ghost follows the tip until the spawn clears",
                index,
                vesselName ?? "?",
                recordingId ?? "(none)",
                chainPid,
                FormatPoseSource(source),
                string.IsNullOrEmpty(bodyName) ? "(none)" : bodyName,
                blockedSinceUT,
                currentUT);
        }

        internal static string BuildReleasedMessage(
            int index, string recordingId, string vesselName, uint chainPid,
            string reason, double heldSinceUT, double currentUT)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Blocked chain tip ghost released: #{0} \"{1}\" rec={2} chainPid={3} reason={4} " +
                "heldSince={5:F1} UT={6:F1} heldFor={7:F1}s",
                index,
                vesselName ?? "?",
                recordingId ?? "(none)",
                chainPid,
                reason ?? "(none)",
                heldSinceUT,
                currentUT,
                currentUT - heldSinceUT);
        }
    }
}
