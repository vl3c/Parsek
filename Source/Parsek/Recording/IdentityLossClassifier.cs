using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Decides whether a background-tracked recording has lost its recorded
    /// controllable identity by the time the surviving vessel goes on rails.
    /// KSP sets <see cref="Vessel.Situations.LANDED"/> purely from terrain
    /// proximity, so a destructive breakup that leaves a tiny remnant (e.g. a
    /// 1-part decoupler) at non-trivial surface speed gets <c>situation=LANDED</c>
    /// for free. Without this check the BG cache refresh accepts that as a
    /// terminal "Landed" verdict, and the recording is permanently
    /// mis-classified (docs/dev/done/todo-and-known-bugs-v6.md, "BG-tracked vessel
    /// mis-classified as Landed after destructive crash").
    /// </summary>
    internal static class IdentityLossClassifier
    {
        /// <summary>
        /// Pure predicate, no Unity types. Returns <c>true</c> when the
        /// surviving remnant carries none of the recorded controller part PIDs
        /// AND is not classified by the live trackability contract as
        /// controllable in its own right (SpaceObject, EVA kerbal, or any
        /// surviving <c>ModuleCommand</c> part). Used by the BG go-on-rails
        /// seam to flip the recording to <see cref="TerminalState.Destroyed"/>
        /// before the FinalizerCache short-circuit can write a false Landed.
        ///
        /// <para>
        /// Forward-only on legacy: if a recording was captured before
        /// controller-identity tracking existed, <paramref name="recordedControllerPids"/>
        /// is empty/null and the predicate returns <c>false</c> — preserving
        /// today's behavior on pre-existing recordings. The fix is forward-only
        /// by design (same todo entry, "Forward-only by design").
        /// </para>
        /// </summary>
        /// <param name="isDebris">
        /// <see cref="Recording.IsDebris"/>: debris recordings opt out — they
        /// were never expected to carry controllable identity.
        /// </param>
        /// <param name="recordedControllerPids">
        /// Persistent IDs of the controller parts captured at recording-start
        /// time. <c>null</c> or empty disables the override.
        /// </param>
        /// <param name="liveIsTrackable">
        /// Result of <see cref="ParsekFlight.IsTrackableVessel(Vessel)"/>
        /// applied to the surviving vessel: <c>true</c> for SpaceObjects, EVA
        /// kerbals, or any vessel carrying at least one <see cref="ModuleCommand"/>
        /// part. When the live remnant is still trackable, the identity is
        /// preserved by the contract that already governs split classification
        /// elsewhere in the mod; the override does not fire.
        /// </param>
        /// <param name="livePartPids">
        /// Persistent IDs of the parts remaining on the surviving vessel.
        /// </param>
        internal static bool ShouldClassifyRecordedIdentityLost(
            bool isDebris,
            IReadOnlyList<uint> recordedControllerPids,
            bool liveIsTrackable,
            IReadOnlyList<uint> livePartPids)
        {
            if (isDebris) return false;
            if (recordedControllerPids == null || recordedControllerPids.Count == 0)
                return false;
            if (liveIsTrackable) return false;
            if (livePartPids == null || livePartPids.Count == 0)
                return true; // no parts survive at all → definitely no recorded identity

            for (int i = 0; i < recordedControllerPids.Count; i++)
            {
                uint cpid = recordedControllerPids[i];
                if (cpid == 0) continue;
                for (int j = 0; j < livePartPids.Count; j++)
                {
                    if (livePartPids[j] == cpid)
                        return false;
                }
            }
            return true;
        }

        /// <summary>Where the identity check read the surviving vessel's parts from.</summary>
        internal enum PartSource
        {
            /// <summary>The loaded vessel's live <c>Part</c> list.</summary>
            Loaded,
            /// <summary>An unloaded vessel's <c>ProtoVessel</c> part snapshots.</summary>
            Proto,
            /// <summary>Neither source holds a part: the parts cannot be read.</summary>
            Unreadable
        }

        /// <summary>
        /// Picks the part source for a found vessel. An unloaded (on-rails, out of physics
        /// range) vessel has no live parts at all, only its proto snapshots, so reading
        /// <c>v.parts</c> for it reads "no part survived" off a living vessel
        /// (SURVIVOR-DESTROYED-AT-SCENE-EXIT). A loaded vessel reads its live parts.
        /// </summary>
        internal static PartSource SelectPartSource(
            bool vesselLoaded, int loadedPartCount, int protoPartCount)
        {
            if (vesselLoaded && loadedPartCount > 0) return PartSource.Loaded;
            if (protoPartCount > 0) return PartSource.Proto;
            if (loadedPartCount > 0) return PartSource.Loaded;
            return PartSource.Unreadable;
        }

        /// <summary>
        /// Pure verdict over both part sources. <paramref name="typeTrackable"/> is the
        /// part-free half of <see cref="ParsekFlight.IsTrackableVessel"/> (space object, EVA
        /// kerbal); each source carries its own "has a command part" read. A vessel whose
        /// parts cannot be read is never sealed Destroyed: there is no evidence it lost
        /// anything.
        /// </summary>
        internal static bool ShouldClassifyRecordedIdentityLostFromSources(
            bool isDebris,
            IReadOnlyList<uint> recordedControllerPids,
            bool typeTrackable,
            bool vesselLoaded,
            IReadOnlyList<uint> loadedPartPids,
            bool loadedHasCommand,
            IReadOnlyList<uint> protoPartPids,
            bool protoHasCommand,
            out PartSource source)
        {
            source = SelectPartSource(
                vesselLoaded,
                loadedPartPids?.Count ?? 0,
                protoPartPids?.Count ?? 0);
            switch (source)
            {
                case PartSource.Loaded:
                    return ShouldClassifyRecordedIdentityLost(
                        isDebris, recordedControllerPids,
                        typeTrackable || loadedHasCommand, loadedPartPids);
                case PartSource.Proto:
                    return ShouldClassifyRecordedIdentityLost(
                        isDebris, recordedControllerPids,
                        typeTrackable || protoHasCommand, protoPartPids);
                default:
                    return false;
            }
        }

        /// <summary>
        /// Reads an unloaded vessel's part pids and whether any part carries a
        /// <c>ModuleCommand</c> from its proto snapshots. Null-safe; plain data, no Unity.
        /// </summary>
        internal static List<uint> CollectProtoPartIdentity(ProtoVessel pv, out bool hasCommand)
        {
            hasCommand = false;
            if (pv == null || pv.protoPartSnapshots == null || pv.protoPartSnapshots.Count == 0)
                return null;
            var pids = new List<uint>(pv.protoPartSnapshots.Count);
            for (int i = 0; i < pv.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                if (pps == null) continue;
                pids.Add(pps.persistentId);
                if (hasCommand || pps.modules == null) continue;
                for (int m = 0; m < pps.modules.Count; m++)
                {
                    ProtoPartModuleSnapshot mod = pps.modules[m];
                    if (mod != null && mod.moduleName == "ModuleCommand")
                    {
                        hasCommand = true;
                        break;
                    }
                }
            }
            return pids;
        }

        /// <summary>
        /// Live wrapper for the BG go-on-rails seam and the finalization identity check.
        /// Adapts the live KSP types to the pure predicate: a loaded vessel through its live
        /// parts and <see cref="ParsekFlight.IsTrackableVessel"/>, an unloaded one through
        /// its proto part snapshots. Null-safe on both inputs.
        /// </summary>
        internal static bool IsRecordedIdentityLost(Recording rec, Vessel v)
        {
            if (rec == null || v == null)
                return false;

            List<uint> recordedPids = null;
            if (rec.Controllers != null && rec.Controllers.Count > 0)
            {
                recordedPids = new List<uint>(rec.Controllers.Count);
                for (int i = 0; i < rec.Controllers.Count; i++)
                    recordedPids.Add(rec.Controllers[i].partPersistentId);
            }

            bool vesselLoaded = v.loaded;
            List<uint> livePids = null;
            if (vesselLoaded && v.parts != null && v.parts.Count > 0)
            {
                livePids = new List<uint>(v.parts.Count);
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p != null) livePids.Add(p.persistentId);
                }
            }
            bool loadedHasCommand = livePids != null && livePids.Count > 0
                && ParsekFlight.IsTrackableVessel(v);

            bool protoHasCommand = false;
            List<uint> protoPids = (livePids == null || livePids.Count == 0)
                ? CollectProtoPartIdentity(v.protoVessel, out protoHasCommand)
                : null;

            bool typeTrackable = v.vesselType == VesselType.SpaceObject
                || v.isEVA || v.vesselType == VesselType.EVA;

            PartSource source;
            bool lost = ShouldClassifyRecordedIdentityLostFromSources(
                rec.IsDebris,
                recordedPids,
                typeTrackable,
                vesselLoaded,
                livePids,
                loadedHasCommand,
                protoPids,
                protoHasCommand,
                out source);
            if (source != PartSource.Loaded)
            {
                ParsekLog.Verbose("Recording",
                    $"IsRecordedIdentityLost: rec={rec.RecordingId ?? "(null)"} pid={v.persistentId} " +
                    $"loaded={vesselLoaded} partSource={source} protoParts={protoPids?.Count ?? 0} " +
                    $"protoCommand={protoHasCommand} recordedControllers={recordedPids?.Count ?? 0} lost={lost}");
            }
            return lost;
        }
    }
}
