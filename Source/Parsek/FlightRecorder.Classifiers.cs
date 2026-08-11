using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    public partial class FlightRecorder
    {
        /// <summary>
        /// The four parachute visual states the recorder tracks, as stored in the
        /// <c>parachuteStates</c> maps. Stowed is the map's implicit default: a pid absent from the
        /// map reads as <see cref="ParachuteStateStowed"/>, and reaching Stowed removes the entry,
        /// so the map only ever holds chutes that are NOT in their build-time pose.
        ///
        /// These are Parsek's states, not a 1:1 copy of KSP's five-member
        /// <c>ModuleParachute.deploymentStates</c>: STOWED and ACTIVE both map to Stowed because an
        /// armed-but-undeployed chute is visually identical to a packed one (see
        /// <see cref="ClassifyParachuteState"/>).
        /// </summary>
        internal const int ParachuteStateStowed = 0;
        internal const int ParachuteStateSemiDeployed = 1;
        internal const int ParachuteStateDeployed = 2;
        internal const int ParachuteStateCut = 3;

        /// <summary>
        /// Maps a stock <c>ModuleParachute.deploymentStates</c> onto Parsek's four tracked states.
        /// The single source of truth for this mapping — the foreground recorder
        /// (<c>CheckParachuteState</c>), the background recorder (<c>BackgroundRecorder</c>'s
        /// own <c>CheckParachuteState</c>) and <c>PartStateSeeder.SeedParachutes</c> all route
        /// through here so their shared <c>parachuteStates</c> dictionary carries one encoding.
        ///
        /// CUT is kept DISTINCT from STOWED (it used to collapse into it). That distinction is the
        /// whole point: without it the CUT -> STOWED transition a stock <c>Repack()</c> performs is
        /// indistinguishable from DEPLOYED -> CUT, and the recorder emitted ParachuteCut for both —
        /// which playback rendered as a permanently empty can.
        /// ACTIVE stays folded into Stowed — it is the armed-but-undeployed state, which has no
        /// distinct visual (canopy still hidden, cap still on).
        /// </summary>
        internal static int ClassifyParachuteState(ModuleParachute.deploymentStates deployState)
        {
            switch (deployState)
            {
                case ModuleParachute.deploymentStates.SEMIDEPLOYED:
                    return ParachuteStateSemiDeployed;
                case ModuleParachute.deploymentStates.DEPLOYED:
                    return ParachuteStateDeployed;
                case ModuleParachute.deploymentStates.CUT:
                    return ParachuteStateCut;
                default:
                    // STOWED and ACTIVE — no canopy, cap on.
                    return ParachuteStateStowed;
            }
        }

        /// <summary>
        /// True when the tracked state is one where a canopy is actually out (semi or full), which
        /// is what makes a part death an aero-destroyed chute rather than an ordinary part loss.
        ///
        /// Deliberately NOT <c>state &gt; 0</c>: <see cref="ParachuteStateCut"/> is 3, so the old
        /// truthy test would classify a part that dies carrying an already-CUT chute as
        /// ParachuteDestroyed. Before the four-state split a cut chute was erased from the map
        /// entirely, so <c>state &gt; 0</c> happened to be right; it is not right any more.
        /// </summary>
        internal static bool IsDeployedParachuteState(int state)
        {
            return state == ParachuteStateSemiDeployed || state == ParachuteStateDeployed;
        }

        internal static PartEventType ClassifyPartDeath(
            uint partPersistentId, bool hasParachuteModule, Dictionary<uint, int> parachuteStates)
        {
            int state;
            if (hasParachuteModule && parachuteStates.TryGetValue(partPersistentId, out state) &&
                IsDeployedParachuteState(state))
            {
                parachuteStates.Remove(partPersistentId);
                return PartEventType.ParachuteDestroyed;
            }
            return PartEventType.Destroyed;
        }

        internal static void ClassifyLadderState(float animTime, out bool isExtended, out bool isRetracted)
        {
            isExtended = animTime >= 0.99f;
            isRetracted = animTime <= 0.01f;
        }

        internal static bool TryClassifyLadderStateFromEventActivity(
            bool canExtend, bool canRetract, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = false;
            isRetracted = false;

            // For ladders, mutually-exclusive UI event activity indicates current state:
            // - can retract => currently deployed
            // - can extend  => currently retracted
            if (canExtend == canRetract)
                return false;

            isDeployed = canRetract;
            isRetracted = canExtend;
            return true;
        }

        internal static bool TryClassifyRetractableLadderStateName(
            string stateName, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = false;
            isRetracted = false;
            if (string.IsNullOrWhiteSpace(stateName))
                return false;

            string normalized = stateName.Trim();
            // Stock uses Extended/Retracted; Deployed/Stowed covers modded ladder variants.
            if (string.Equals(normalized, "Extended", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Deployed", StringComparison.OrdinalIgnoreCase))
            {
                isDeployed = true;
                return true;
            }

            if (string.Equals(normalized, "Retracted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Stowed", StringComparison.OrdinalIgnoreCase))
            {
                isRetracted = true;
                return true;
            }

            return false;
        }

        internal static bool IsRetractableLadderTransientStateName(string stateName)
        {
            if (string.IsNullOrWhiteSpace(stateName))
                return false;

            string normalized = stateName.Trim();
            return string.Equals(normalized, "Extending", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Retracting", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Pure keyword classifier for an aero-surface event: marks an event as deploy when its
        /// lowercased name or gui name contains deploy/extend/open/brake/enable, and as retract
        /// when it contains retract/close/stow/disable. Both outputs can be set independently
        /// (an event matching neither set leaves both false). Inputs are expected lowercased.
        /// </summary>
        internal static void ClassifyAeroEventName(
            string evtName, string guiName, out bool isDeploy, out bool isRetract)
        {
            isDeploy =
                evtName.Contains("deploy") || guiName.Contains("deploy") ||
                evtName.Contains("extend") || guiName.Contains("extend") ||
                evtName.Contains("open") || guiName.Contains("open") ||
                evtName.Contains("brake") || guiName.Contains("brake") ||
                evtName.Contains("enable") || guiName.Contains("enable");
            isRetract =
                evtName.Contains("retract") || guiName.Contains("retract") ||
                evtName.Contains("close") || guiName.Contains("close") ||
                evtName.Contains("stow") || guiName.Contains("stow") ||
                evtName.Contains("disable") || guiName.Contains("disable");
        }

        internal static void ClassifyGearState(string stateString, out bool isDeployed, out bool isRetracted)
        {
            isDeployed = stateString == "Deployed";
            isRetracted = stateString == "Retracted";
        }

        internal static void ClassifyCargoBayState(
            float animTime, float closedPosition, out bool isOpen, out bool isClosed)
        {
            bool atStart = animTime <= 0.01f;
            bool atEnd = animTime >= 0.99f;

            if (closedPosition > 0.9f)
            {
                // closedPosition near 1 → closed at animTime≈1, open at animTime≈0
                isClosed = atEnd;
                isOpen = atStart;
            }
            else if (closedPosition < 0.1f)
            {
                // closedPosition near 0 → closed at animTime≈0, open at animTime≈1
                isClosed = atStart;
                isOpen = atEnd;
            }
            else
            {
                // Non-standard closedPosition (modded part) — skip
                isClosed = false;
                isOpen = false;
            }
        }

        /// <summary>
        /// What the per-frame poll should do with one entry of a cached module list
        /// (<c>cachedEngines</c> / <c>cachedRcsModules</c> / <c>cachedRoboticModules</c>).
        /// </summary>
        internal enum CachedModulePollDecision
        {
            /// <summary>Entry is live and still on the recorded vessel — poll it.</summary>
            Poll,
            /// <summary>The Part or the PartModule has been destroyed — nothing to read.</summary>
            SkipNullEntry,
            /// <summary>
            /// The Part survives but no longer belongs to the recorded vessel (staged away,
            /// undocked, decoupled, or mid-transition with a null <c>Part.vessel</c>).
            /// </summary>
            SkipForeignVessel
        }

        /// <summary>
        /// Pure decision core for the cached-module ownership guard.
        ///
        /// <para>
        /// The caches are built once per <c>ResetPartEventTrackingState</c> (and now refreshed from
        /// <c>onVesselWasModified</c>) and hold direct <c>Part</c>/<c>PartModule</c> references. A
        /// staged-away booster's <c>Part</c> is NOT destroyed — it moves to a brand-new debris
        /// <c>Vessel</c> and keeps burning. The old poll only checked <c>part == null</c>, so those
        /// still-live modules kept writing EngineIgnited / EngineThrottle / EngineShutdown (and the
        /// RCS / robotic equivalents) into the PARENT recording under the booster's own part pid.
        /// </para>
        /// <para>
        /// The comparison the live caller feeds this predicate is a LIVE OBJECT comparison
        /// (<c>ReferenceEquals(part.vessel, recordedVessel)</c>), not a persistent-id or guid
        /// comparison, so the CLAUDE.md "persistentId is craft-baked, not launch-unique" rule does
        /// not apply here: there is exactly one in-memory <c>Vessel</c> instance per live vessel
        /// within a session, and nothing is compared across saves or against stored recordings.
        /// </para>
        /// </summary>
        /// <param name="hasPart">False when the cached <c>Part</c> reference is null/destroyed.</param>
        /// <param name="hasModule">False when the cached <c>PartModule</c> reference is null/destroyed.</param>
        /// <param name="hasPartVessel">False when <c>part.vessel</c> is null (mid-split frame).</param>
        /// <param name="partVesselIsRecordedVessel">
        /// True when <c>part.vessel</c> is the same live <c>Vessel</c> instance the recorder is
        /// recording.
        /// </param>
        internal static CachedModulePollDecision DecideCachedModulePoll(
            bool hasPart, bool hasModule, bool hasPartVessel, bool partVesselIsRecordedVessel)
        {
            if (!hasPart || !hasModule)
                return CachedModulePollDecision.SkipNullEntry;

            // A null Part.vessel is the one-frame window between a decoupler firing and KSP
            // reparenting the part onto its new vessel. It is not our vessel right now, and
            // guessing "probably still ours" is exactly the bug this guard closes.
            if (!hasPartVessel)
                return CachedModulePollDecision.SkipForeignVessel;

            return partVesselIsRecordedVessel
                ? CachedModulePollDecision.Poll
                : CachedModulePollDecision.SkipForeignVessel;
        }
    }
}
