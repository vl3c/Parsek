using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live proof, inside a real KSP process, that an INVOKED Re-Fly leaves the
    /// unrelated world standing (REFLY-DELETES-NON-SLOT-WORLD, audit item C1).
    ///
    /// <para>
    /// WHY THIS CATEGORY EXISTS AT ALL, given the fix already carries xUnit
    /// coverage. Every headless cell is ConfigNode-level or pure-predicate: it
    /// proves the scrub REWRITES the temp save correctly and that the #587 survey
    /// admits only debris. None of them proves that KSP, having loaded that
    /// rewritten save, actually has the fleet in
    /// <c>FlightGlobals.Vessels</c> - the whole bug was that TWO independent
    /// layers deleted the world (the pre-load scrub AND the post-load strict
    /// strip), so a green pre-load assertion was exactly what the original defect
    /// looked like. These cells read the LIVE scene after the load and compare it
    /// against the RP quicksave that produced it.
    /// </para>
    ///
    /// <para>
    /// THE GROUND TRUTH IS THE RP QUICKSAVE ON DISK, not a fixture-persisted
    /// expectation and not a log line. <c>Parsek/RewindPoints/&lt;rpId&gt;.sfs</c>
    /// is the world as it stood at the rewind point; the temp copy the re-fly
    /// loaded is deleted on completion but the source sidecar survives until the
    /// merge reaps it. So each cell can name, per vessel, what the world HELD and
    /// check what the world HAS. That makes the whole category
    /// fixture-independent: it is just as valid on a player's career save as on
    /// the harness fixture, and it cannot be satisfied by a fixture that happens
    /// to carry nothing.
    /// </para>
    ///
    /// <para>
    /// Classification routes through <c>RewindInvoker.BuildSlotPidSets</c> +
    /// <c>ClassifySlotAffinity</c> - the SAME pair the pre-load scrub uses - so
    /// the guard cannot drift from the predicate that produced the world it is
    /// checking.
    /// </para>
    ///
    /// <para>
    /// EVERY cell self-skips with a named requirement when no Re-Fly session is
    /// live, so the category is safe to run in any scene and any batch. A skip is
    /// the honest verdict for "the precondition this asserts about does not
    /// exist"; asserting against an assumed context is what
    /// <c>InGameAssert.Skip</c> exists to prevent.
    /// </para>
    /// </summary>
    public class ReFlyWorldPreservationTests
    {
        private const string Category = "ReFlyWorldPreservation";

        // ------------------------------------------------------------------
        // Session + ground-truth resolution
        // ------------------------------------------------------------------

        /// <summary>
        /// One quicksave <c>VESSEL</c> node reduced to what the preservation
        /// invariant needs: identity, display name, KSP vessel type token and the
        /// slot affinity the production classifier assigned it.
        /// </summary>
        private struct WorldVessel
        {
            public uint Pid;
            public string Name;
            public string TypeToken;
            public RewindInvoker.ReFlySlotAffinity Affinity;

            public bool IsDebris =>
                string.Equals(TypeToken, "Debris", StringComparison.Ordinal);

            public string Describe() =>
                $"'{Name ?? "<unnamed>"}' pid={Pid.ToString(CultureInfo.InvariantCulture)} " +
                $"type={TypeToken ?? "<none>"}";
        }

        /// <summary>
        /// The live Re-Fly session plus the world its rewind point recorded.
        /// Resolution is all-or-nothing: a cell either gets a complete picture or
        /// a named skip reason.
        /// </summary>
        private struct SessionWorld
        {
            public ReFlySessionMarker Marker;
            public RewindPoint Rp;
            public int SelectedSlotIndex;
            public List<WorldVessel> QuicksaveVessels;

            /// <summary>
            /// Names carried by MORE THAN ONE vessel in the quicksave census. The
            /// name fallback in <see cref="IsPresent"/> is refused for these, and
            /// that is load-bearing rather than tidy: the #587 regression case is a
            /// Probe and a Debris under the SAME name, one of which is expected to
            /// survive and the other to be removed. A name-based presence check
            /// would let the survivor vouch for the victim and vice versa, so the
            /// cell that matters most would be the one that could not fail.
            /// </summary>
            public HashSet<string> AmbiguousNames;
        }

        /// <summary>
        /// Resolves the live marker, its rewind point, the selected slot index and
        /// the RP quicksave's vessel census. Returns false with a
        /// requirement-naming <paramref name="skipReason"/> that every cell hands
        /// straight to <see cref="InGameAssert.Skip"/>.
        /// </summary>
        private static bool TryResolveSessionWorld(out SessionWorld world, out string skipReason)
        {
            world = default(SessionWorld);
            skipReason = null;

            ParsekScenario scenario = ParsekScenario.Instance;
            if (scenario == null)
            {
                skipReason = "REQUIRES: a live ParsekScenario (none in this scene).";
                return false;
            }

            ReFlySessionMarker marker = scenario.ActiveReFlySessionMarker;
            if (marker == null)
            {
                skipReason = "REQUIRES an active Re-Fly session - invoke a "
                    + "Rewind-to-Separation before running this category "
                    + "(scenario.ActiveReFlySessionMarker is null).";
                return false;
            }

            RewindPoint rp = FindRewindPoint(scenario, marker.RewindPointId);
            if (rp == null)
            {
                skipReason = "REQUIRES the session's RewindPoint to still resolve in "
                    + $"scenario.RewindPoints (rp='{marker.RewindPointId ?? "<null>"}' not found; "
                    + "a merge that already reaped it leaves nothing to compare against).";
                return false;
            }

            int selectedSlotIndex;
            if (!TryResolveSelectedSlotIndex(rp, marker, out selectedSlotIndex))
            {
                skipReason = "REQUIRES the selected slot to be resolvable from the marker "
                    + $"(rp='{rp.RewindPointId ?? "<null>"}' origin="
                    + $"'{marker.OriginChildRecordingId ?? "<null>"}' rootPartPid="
                    + marker.SelectedRootPartPersistentId.ToString(CultureInfo.InvariantCulture) + ").";
                return false;
            }

            List<WorldVessel> census;
            if (!TryReadRewindPointWorld(rp, selectedSlotIndex, out census, out string readError))
            {
                skipReason = "REQUIRES the RP quicksave on disk as the pre-rewind world "
                    + $"ground truth: {readError}";
                return false;
            }

            world = new SessionWorld
            {
                Marker = marker,
                Rp = rp,
                SelectedSlotIndex = selectedSlotIndex,
                QuicksaveVessels = census,
                AmbiguousNames = BuildAmbiguousNameSet(census),
            };
            return true;
        }

        private static HashSet<string> BuildAmbiguousNameSet(List<WorldVessel> census)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            if (census == null) return ambiguous;
            for (int i = 0; i < census.Count; i++)
            {
                string name = census[i].Name;
                if (string.IsNullOrEmpty(name)) continue;
                if (!seen.Add(name)) ambiguous.Add(name);
            }
            return ambiguous;
        }

        private static RewindPoint FindRewindPoint(ParsekScenario scenario, string rewindPointId)
        {
            if (scenario == null || scenario.RewindPoints == null
                || string.IsNullOrEmpty(rewindPointId))
            {
                return null;
            }
            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                RewindPoint candidate = scenario.RewindPoints[i];
                if (candidate == null) continue;
                if (string.Equals(candidate.RewindPointId, rewindPointId, StringComparison.Ordinal))
                    return candidate;
            }
            return null;
        }

        /// <summary>
        /// The marker names the slot two ways and neither is a slot index: the
        /// origin recording id (matched against <see cref="ChildSlot.OriginChildRecordingId"/>)
        /// and the selected ROOT PART pid (a <see cref="RewindPoint.RootPartPidMap"/>
        /// key). Origin id first because it survives a root-part pid KSP
        /// regenerated on load.
        /// </summary>
        private static bool TryResolveSelectedSlotIndex(
            RewindPoint rp, ReFlySessionMarker marker, out int selectedSlotIndex)
        {
            selectedSlotIndex = -1;
            if (rp == null || marker == null) return false;

            if (!string.IsNullOrEmpty(marker.OriginChildRecordingId) && rp.ChildSlots != null)
            {
                for (int i = 0; i < rp.ChildSlots.Count; i++)
                {
                    ChildSlot slot = rp.ChildSlots[i];
                    if (slot == null) continue;
                    if (string.Equals(slot.OriginChildRecordingId,
                            marker.OriginChildRecordingId, StringComparison.Ordinal))
                    {
                        selectedSlotIndex = slot.SlotIndex;
                        return true;
                    }
                }
            }

            if (marker.SelectedRootPartPersistentId != 0u && rp.RootPartPidMap != null
                && rp.RootPartPidMap.TryGetValue(marker.SelectedRootPartPersistentId, out int mapped))
            {
                selectedSlotIndex = mapped;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Reads the RP quicksave's <c>FLIGHTSTATE</c> vessel census and classifies
        /// each node with the production scrub's own predicate.
        /// </summary>
        private static bool TryReadRewindPointWorld(
            RewindPoint rp, int selectedSlotIndex,
            out List<WorldVessel> census, out string error)
        {
            census = null;
            error = null;

            string path = RewindInvoker.ResolveAbsoluteQuicksavePath(rp);
            if (string.IsNullOrEmpty(path))
            {
                error = $"rp='{rp.RewindPointId ?? "<null>"}' has no resolvable quicksave path.";
                return false;
            }
            if (!File.Exists(path))
            {
                error = $"quicksave sidecar missing on disk: '{path}'.";
                return false;
            }

            ConfigNode root = ConfigNode.Load(path);
            ConfigNode game = root != null && root.HasNode("GAME") ? root.GetNode("GAME") : root;
            ConfigNode flightState = game != null ? game.GetNode("FLIGHTSTATE") : null;
            if (flightState == null)
            {
                error = $"quicksave has no FLIGHTSTATE node: '{path}'.";
                return false;
            }

            RewindInvoker.ReFlySlotPidSets slotPids =
                RewindInvoker.BuildSlotPidSets(rp, selectedSlotIndex);

            ConfigNode[] vessels = flightState.GetNodes("VESSEL");
            var result = new List<WorldVessel>(vessels.Length);
            int selected = 0, other = 0, unrelated = 0;
            for (int i = 0; i < vessels.Length; i++)
            {
                ConfigNode vessel = vessels[i];
                if (vessel == null) continue;

                RewindInvoker.ReFlySlotAffinity affinity =
                    RewindInvoker.ClassifySlotAffinity(vessel, slotPids);
                switch (affinity)
                {
                    case RewindInvoker.ReFlySlotAffinity.SelectedSlot: selected++; break;
                    case RewindInvoker.ReFlySlotAffinity.OtherSlot: other++; break;
                    default: unrelated++; break;
                }

                uint pid = 0u;
                uint.TryParse(vessel.GetValue("persistentId"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out pid);
                result.Add(new WorldVessel
                {
                    Pid = pid,
                    Name = vessel.GetValue("name"),
                    TypeToken = vessel.GetValue("type"),
                    Affinity = affinity,
                });
            }

            ParsekLog.Verbose("ReFlyWorldPreservation",
                $"RP quicksave census: rp={rp.RewindPointId ?? "<null>"} slot=" +
                selectedSlotIndex.ToString(CultureInfo.InvariantCulture) +
                $" vessels={result.Count} selected={selected} otherSlot={other} " +
                $"unrelated={unrelated} path='{path}'");

            census = result;
            return true;
        }

        // ------------------------------------------------------------------
        // Live-scene reads
        // ------------------------------------------------------------------

        /// <summary>
        /// Live vessels by <c>persistentId</c>, ghost-map ProtoVessels excluded (a
        /// Parsek map ghost is not a preserved world object and must never satisfy
        /// a preservation assertion).
        /// </summary>
        private static Dictionary<uint, Vessel> LiveRealVesselsByPid()
        {
            var byPid = new Dictionary<uint, Vessel>();
            IList<Vessel> vessels;
            try { vessels = FlightGlobals.Vessels; }
            catch { vessels = null; }
            if (vessels == null) return byPid;

            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel v = vessels[i];
                if (v == null) continue;
                uint pid = v.persistentId;
                if (pid == 0u) continue;
                if (GhostMapPresence.IsGhostMapVessel(pid)) continue;
                byPid[pid] = v;
            }
            return byPid;
        }

        /// <summary>
        /// Live real-vessel names (ghosts excluded), for the name fallback a pid
        /// KSP regenerated on load would otherwise defeat.
        /// </summary>
        private static HashSet<string> LiveRealVesselNames()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            IList<Vessel> vessels;
            try { vessels = FlightGlobals.Vessels; }
            catch { vessels = null; }
            if (vessels == null) return names;

            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel v = vessels[i];
                if (v == null) continue;
                if (v.persistentId != 0u && GhostMapPresence.IsGhostMapVessel(v.persistentId))
                    continue;
                string name = null;
                try { name = v.vesselName; }
                catch { name = null; }
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
            return names;
        }

        /// <summary>
        /// A quicksave vessel counts as present when its pid is live, or - when KSP
        /// regenerated the pid on load - when a live real vessel carries its exact
        /// name AND that name is unambiguous in the quicksave census. Generous on
        /// the pid (this predicate decides PRESENCE and the bug guarded is deletion,
        /// so the false negative to avoid is "reported deleted because its pid
        /// moved"), strict on the name (see
        /// <see cref="SessionWorld.AmbiguousNames"/> - a shared name must not let
        /// one vessel vouch for another).
        /// </summary>
        private static bool IsPresent(
            WorldVessel expected, SessionWorld world,
            Dictionary<uint, Vessel> livePids, HashSet<string> liveNames)
        {
            if (expected.Pid != 0u && livePids.ContainsKey(expected.Pid)) return true;
            if (string.IsNullOrEmpty(expected.Name)) return false;
            if (world.AmbiguousNames != null && world.AmbiguousNames.Contains(expected.Name))
                return false;
            return liveNames.Contains(expected.Name);
        }

        private static List<WorldVessel> Unrelated(SessionWorld world)
        {
            var list = new List<WorldVessel>();
            for (int i = 0; i < world.QuicksaveVessels.Count; i++)
            {
                if (world.QuicksaveVessels[i].Affinity
                        == RewindInvoker.ReFlySlotAffinity.Unrelated)
                {
                    list.Add(world.QuicksaveVessels[i]);
                }
            }
            return list;
        }

        private static string Join(List<WorldVessel> vessels)
        {
            var parts = new List<string>(vessels.Count);
            for (int i = 0; i < vessels.Count; i++) parts.Add(vessels[i].Describe());
            return parts.Count == 0 ? "<none>" : string.Join("; ", parts.ToArray());
        }

        /// <summary>
        /// Vessel names the #587 name-matched kill can be eligible to remove: every
        /// recording in the session's tree. Superset of the production predicate
        /// (which additionally requires Destroyed-terminal / suppressed-subtree /
        /// parent-chain membership and excludes the active target), and a superset
        /// is the correct side for the two cells that use it: cell 3 asserts a
        /// non-debris name-match SURVIVES, so over-collecting names can only make
        /// that assertion stricter, never vacuous.
        /// </summary>
        private static HashSet<string> KillEligibleNameSuperset(ReFlySessionMarker marker)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (marker == null || string.IsNullOrEmpty(marker.TreeId)) return names;

            IReadOnlyList<RecordingTree> trees = RecordingStore.CommittedTrees;
            if (trees == null) return names;
            for (int i = 0; i < trees.Count; i++)
            {
                RecordingTree tree = trees[i];
                if (tree == null || tree.Recordings == null) continue;
                if (!string.Equals(tree.Id, marker.TreeId, StringComparison.Ordinal)) continue;
                foreach (Recording rec in tree.Recordings.Values)
                {
                    if (rec == null || string.IsNullOrEmpty(rec.VesselName)) continue;
                    names.Add(rec.VesselName);
                }
            }
            return names;
        }

        // ------------------------------------------------------------------
        // Cell 1: the preserved-world invariant
        // ------------------------------------------------------------------

        [InGameTest(Category = Category, Scene = GameScenes.FLIGHT,
            Description = "After an invoked Re-Fly, every vessel the RP quicksave held that is in NEITHER slot map and is not Debris is still in FlightGlobals - nothing unrelated was lost")]
        public void PreservedWorldSurvivesReFlyLoad()
        {
            if (!TryResolveSessionWorld(out SessionWorld world, out string skip))
                InGameAssert.Skip(skip);

            List<WorldVessel> unrelated = Unrelated(world);
            var expected = new List<WorldVessel>();
            var debrisExempt = new List<WorldVessel>();
            for (int i = 0; i < unrelated.Count; i++)
            {
                // Debris is DELIBERATELY exempt from the presence assertion: the
                // #587 name-matched pass is debris-only and legitimately removes
                // prior-career debris that would trip stock patched conics. Cell 5
                // asserts that direction; asserting presence here would contradict
                // it.
                if (unrelated[i].IsDebris) debrisExempt.Add(unrelated[i]);
                else expected.Add(unrelated[i]);
            }

            if (expected.Count == 0)
            {
                InGameAssert.Skip(
                    "REQUIRES the RP quicksave to hold at least one non-Debris vessel outside "
                    + "this rewind point's slot maps (an unrelated station / probe / asteroid / "
                    + $"flag). Census: {world.QuicksaveVessels.Count} vessel(s), "
                    + $"{unrelated.Count} unrelated of which {debrisExempt.Count} Debris. "
                    + "A single-launch save has no unrelated fleet to preserve.");
            }

            Dictionary<uint, Vessel> livePids = LiveRealVesselsByPid();
            HashSet<string> liveNames = LiveRealVesselNames();

            var missing = new List<WorldVessel>();
            for (int i = 0; i < expected.Count; i++)
            {
                if (!IsPresent(expected[i], world, livePids, liveNames)) missing.Add(expected[i]);
            }

            ParsekLog.Info("ReFlyWorldPreservation",
                $"Preserved-world check: rp={world.Rp.RewindPointId ?? "<null>"} " +
                $"expectedPresent={expected.Count} missing={missing.Count} " +
                $"debrisExempt={debrisExempt.Count} liveRealVessels={livePids.Count}");

            InGameAssert.IsTrue(missing.Count == 0,
                $"Re-Fly deleted {missing.Count} unrelated vessel(s) the RP quicksave held "
                + $"(REFLY-DELETES-NON-SLOT-WORLD): {Join(missing)}. "
                + $"Live real vessels: {livePids.Count}. Expected present: {expected.Count}.");
        }

        // ------------------------------------------------------------------
        // Cell 2: the scrub still does its job (the complement)
        // ------------------------------------------------------------------

        [InGameTest(Category = Category, Scene = GameScenes.FLIGHT,
            Description = "Preservation did not widen into keep-everything: the selected slot's vessel is live and every OTHER slot of this rewind point is gone from the scene")]
        public void SiblingSlotsRemovedAndSelectedSlotSurvives()
        {
            if (!TryResolveSessionWorld(out SessionWorld world, out string skip))
                InGameAssert.Skip(skip);

            var selected = new List<WorldVessel>();
            var siblings = new List<WorldVessel>();
            for (int i = 0; i < world.QuicksaveVessels.Count; i++)
            {
                WorldVessel wv = world.QuicksaveVessels[i];
                if (wv.Affinity == RewindInvoker.ReFlySlotAffinity.SelectedSlot) selected.Add(wv);
                else if (wv.Affinity == RewindInvoker.ReFlySlotAffinity.OtherSlot) siblings.Add(wv);
            }

            if (siblings.Count == 0)
            {
                InGameAssert.Skip(
                    "REQUIRES a rewind point with at least one MAPPED sibling slot besides the "
                    + $"selected one (rp='{world.Rp.RewindPointId ?? "<null>"}' slot="
                    + world.SelectedSlotIndex.ToString(CultureInfo.InvariantCulture)
                    + $", quicksave holds {selected.Count} selected-slot and 0 other-slot "
                    + "vessel(s)); with no sibling there is no removal to assert.");
            }

            Dictionary<uint, Vessel> livePids = LiveRealVesselsByPid();
            HashSet<string> liveNames = LiveRealVesselNames();

            // The selected slot must be PRESENT - it is the craft the player is
            // flying. Its absence means the re-fly loaded a world without its own
            // target, which the scrub's SelectedActiveIndex guard exists to refuse.
            var missingSelected = new List<WorldVessel>();
            for (int i = 0; i < selected.Count; i++)
            {
                if (!IsPresent(selected[i], world, livePids, liveNames)) missingSelected.Add(selected[i]);
            }
            InGameAssert.IsTrue(missingSelected.Count == 0,
                $"the selected Re-Fly slot is not in the live scene: {Join(missingSelected)}");

            // Siblings must be ABSENT by pid. Name is deliberately NOT used here:
            // the fixture / a player career can carry an unrelated craft sharing a
            // sibling's name, and a name hit would report a correct removal as a
            // failure.
            var survivingSiblings = new List<WorldVessel>();
            for (int i = 0; i < siblings.Count; i++)
            {
                if (siblings[i].Pid != 0u && livePids.ContainsKey(siblings[i].Pid))
                    survivingSiblings.Add(siblings[i]);
            }

            ParsekLog.Info("ReFlyWorldPreservation",
                $"Sibling-removal check: rp={world.Rp.RewindPointId ?? "<null>"} " +
                $"selectedInScene={selected.Count - missingSelected.Count}/{selected.Count} " +
                $"siblings={siblings.Count} survivingSiblings={survivingSiblings.Count}");

            InGameAssert.IsTrue(survivingSiblings.Count == 0,
                $"{survivingSiblings.Count} sibling-slot vessel(s) survived the Re-Fly scrub and "
                + $"are in the scene as real craft AND replayed as ghosts: {Join(survivingSiblings)}");
        }

        // ------------------------------------------------------------------
        // Cell 3: the #587 constraint, asserted by presence
        // ------------------------------------------------------------------

        [InGameTest(Category = Category, Scene = GameScenes.FLIGHT,
            Description = "#587 debris-only constraint: a live NON-Debris vessel whose name matches a kill-eligible recording in the session's tree was not Die()d by the name-matched pass")]
        public void NameCollidingNonDebrisVesselSurvivesTheBug587Kill()
        {
            if (!TryResolveSessionWorld(out SessionWorld world, out string skip))
                InGameAssert.Skip(skip);

            HashSet<string> killEligible = KillEligibleNameSuperset(world.Marker);
            if (killEligible.Count == 0)
            {
                InGameAssert.Skip(
                    "REQUIRES the session's tree to resolve in RecordingStore.CommittedTrees with "
                    + $"at least one named recording (treeId='{world.Marker.TreeId ?? "<null>"}'); "
                    + "without recording names there is no name-collision to test.");
            }

            List<WorldVessel> unrelated = Unrelated(world);
            var collisions = new List<WorldVessel>();
            for (int i = 0; i < unrelated.Count; i++)
            {
                WorldVessel wv = unrelated[i];
                if (wv.IsDebris) continue;
                if (string.IsNullOrEmpty(wv.Name)) continue;
                if (!killEligible.Contains(wv.Name)) continue;
                collisions.Add(wv);
            }

            if (collisions.Count == 0)
            {
                InGameAssert.Skip(
                    "REQUIRES the RP quicksave to hold a NON-Debris vessel outside the slot maps "
                    + "whose name matches a recording in the session's tree - the #587 regression "
                    + $"case. Checked {unrelated.Count} unrelated vessel(s) against "
                    + $"{killEligible.Count} recording name(s) and found no collision.");
            }

            Dictionary<uint, Vessel> livePids = LiveRealVesselsByPid();
            HashSet<string> liveNames = LiveRealVesselNames();

            var killed = new List<WorldVessel>();
            for (int i = 0; i < collisions.Count; i++)
            {
                if (!IsPresent(collisions[i], world, livePids, liveNames)) killed.Add(collisions[i]);
            }

            ParsekLog.Info("ReFlyWorldPreservation",
                $"#587 non-debris survival check: rp={world.Rp.RewindPointId ?? "<null>"} " +
                $"nameCollisions={collisions.Count} removed={killed.Count} " +
                $"killEligibleNames={killEligible.Count}");

            InGameAssert.IsTrue(killed.Count == 0,
                $"the #587 name-matched kill removed {killed.Count} NON-Debris vessel(s) "
                + "(IsDebrisKillSurveyCandidate must admit VesselType.Debris only; a real craft "
                + "Die()d inside SuppressionGuard.Crew leaves no ledger row): "
                + Join(killed));
        }

        // ------------------------------------------------------------------
        // Cell 4: flags and SpaceObjects specifically
        // ------------------------------------------------------------------

        [InGameTest(Category = Category, Scene = GameScenes.FLIGHT,
            Description = "The two unrecoverable populations survive: every unrelated VesselType.Flag and VesselType.SpaceObject (tracked asteroid / comet) the RP quicksave held is still in the scene")]
        public void PreservedFlagsAndSpaceObjectsSurviveReFlyLoad()
        {
            if (!TryResolveSessionWorld(out SessionWorld world, out string skip))
                InGameAssert.Skip(skip);

            List<WorldVessel> unrelated = Unrelated(world);
            var flags = new List<WorldVessel>();
            var spaceObjects = new List<WorldVessel>();
            for (int i = 0; i < unrelated.Count; i++)
            {
                WorldVessel wv = unrelated[i];
                if (string.Equals(wv.TypeToken, "Flag", StringComparison.Ordinal)) flags.Add(wv);
                else if (string.Equals(wv.TypeToken, "SpaceObject", StringComparison.Ordinal))
                    spaceObjects.Add(wv);
            }

            if (flags.Count == 0 && spaceObjects.Count == 0)
            {
                InGameAssert.Skip(
                    "REQUIRES the RP quicksave to hold an unrelated VesselType.Flag or "
                    + "VesselType.SpaceObject. These are the two populations the original bug "
                    + "destroyed UNRECOVERABLY (a planted-flag milestone and a discovered "
                    + $"asteroid/comet cannot be re-created), and this save carries neither of "
                    + $"them among {unrelated.Count} unrelated vessel(s).");
            }

            Dictionary<uint, Vessel> livePids = LiveRealVesselsByPid();
            HashSet<string> liveNames = LiveRealVesselNames();

            var missing = new List<WorldVessel>();
            for (int i = 0; i < flags.Count; i++)
                if (!IsPresent(flags[i], world, livePids, liveNames)) missing.Add(flags[i]);
            for (int i = 0; i < spaceObjects.Count; i++)
                if (!IsPresent(spaceObjects[i], world, livePids, liveNames)) missing.Add(spaceObjects[i]);

            ParsekLog.Info("ReFlyWorldPreservation",
                $"Flag/SpaceObject check: rp={world.Rp.RewindPointId ?? "<null>"} " +
                $"flags={flags.Count} spaceObjects={spaceObjects.Count} missing={missing.Count}");

            InGameAssert.IsTrue(missing.Count == 0,
                $"Re-Fly destroyed {missing.Count} unrecoverable world object(s) "
                + "(PostLoadStripper.ShouldPreserveVesselType covers Flag; SpaceObject survives "
                + $"by being in no slot map): {Join(missing)}");
        }

        // ------------------------------------------------------------------
        // Cell 5: the #587 kill is still armed (the positive control)
        // ------------------------------------------------------------------

        [InGameTest(Category = Category, Scene = GameScenes.FLIGHT,
            Description = "The debris-only narrowing did not neuter #587: name-matching Debris outside the slot maps is still removed on an in-place continuation re-fly")]
        public void NameCollidingDebrisIsStillRemovedByTheBug587Kill()
        {
            if (!TryResolveSessionWorld(out SessionWorld world, out string skip))
                InGameAssert.Skip(skip);

            // The pass is a documented NO-OP on a placeholder (non-in-place)
            // re-fly, by its own design - ResolveInPlaceContinuationDebrisToKill
            // returns at its IsInPlaceContinuation guard. Asserting a removal
            // there would assert a behaviour the code deliberately does not have.
            if (!ReFlySessionMarker.IsInPlaceContinuation(world.Marker))
            {
                InGameAssert.Skip(
                    "REQUIRES an IN-PLACE continuation Re-Fly session (the #587 name-matched "
                    + "kill returns early at ResolveInPlaceContinuationDebrisToKill's "
                    + "IsInPlaceContinuation guard on a placeholder re-fly, so nothing is "
                    + $"removed by design). marker.InPlaceContinuation="
                    + world.Marker.InPlaceContinuation.ToString()
                    + $" active='{world.Marker.ActiveReFlyRecordingId ?? "<null>"}' "
                    + $"origin='{world.Marker.OriginChildRecordingId ?? "<null>"}'.");
            }

            HashSet<string> killEligible = KillEligibleNameSuperset(world.Marker);
            List<WorldVessel> unrelated = Unrelated(world);
            var candidates = new List<WorldVessel>();
            for (int i = 0; i < unrelated.Count; i++)
            {
                WorldVessel wv = unrelated[i];
                if (!wv.IsDebris) continue;
                if (string.IsNullOrEmpty(wv.Name)) continue;
                if (!killEligible.Contains(wv.Name)) continue;
                candidates.Add(wv);
            }

            if (candidates.Count == 0)
            {
                InGameAssert.Skip(
                    "REQUIRES the RP quicksave to hold VesselType.Debris outside the slot maps "
                    + "whose name matches a recording in the session's tree - the positive "
                    + $"control for the #587 kill. Checked {unrelated.Count} unrelated vessel(s) "
                    + $"against {killEligible.Count} recording name(s).");
            }

            Dictionary<uint, Vessel> livePids = LiveRealVesselsByPid();

            // Pid-only, and by DELETION: a name check would be satisfied by the
            // preserved non-debris craft that shares the same name (the fixture
            // carries exactly that pair on purpose).
            var survivors = new List<WorldVessel>();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Pid != 0u && livePids.ContainsKey(candidates[i].Pid))
                    survivors.Add(candidates[i]);
            }

            ParsekLog.Info("ReFlyWorldPreservation",
                $"#587 debris-kill positive control: rp={world.Rp.RewindPointId ?? "<null>"} " +
                $"debrisNameMatches={candidates.Count} survivors={survivors.Count}");

            InGameAssert.IsTrue(survivors.Count == 0,
                $"{survivors.Count} name-matching Debris vessel(s) survived the #587 pass, so "
                + "the mechanism that clears prior-career debris tripping stock patched conics "
                + $"is no longer firing: {Join(survivors)}");
        }

        // ------------------------------------------------------------------
        // Cell 6: the pre-invoke advisory, composed over LIVE state
        // ------------------------------------------------------------------

        [InGameTest(Category = Category, Scene = GameScenes.FLIGHT,
            Description = "The pre-invoke advisory composes and emits against the LIVE RewindPoint + RecordingStore: sibling slots resolve to their origin recordings' craft names and the body states the preservation guarantee")]
        public void AdvisoryComposesFromLiveRewindPointAndStore()
        {
            if (!TryResolveSessionWorld(out SessionWorld world, out string skip))
                InGameAssert.Skip(skip);

            // RAW committed list on purpose, matching the production call site:
            // a sibling slot superseded by an EARLIER re-fly is hidden from the
            // ERS while its vessel is still in the quicksave and still gets
            // removed, so the raw list is the correct source for "what leaves the
            // world". [ERS-exempt] via the InGameTests/ directory allowlist entry.
            IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;

            List<string> siblingNames = RewindInvoker.ResolveReFlySiblingSlotNames(
                world.Rp, world.SelectedSlotIndex, committed);
            InGameAssert.IsNotNull(siblingNames, "ResolveReFlySiblingSlotNames returned null");

            // MAPPED non-selected slots are exactly the population the advisory
            // may name: an UNMAPPED slot's vessel cannot be matched by the scrub,
            // so promising the player it is put away would be a false statement.
            int mappedSiblingSlots = CountMappedNonSelectedSlots(world.Rp, world.SelectedSlotIndex);
            InGameAssert.AreEqual(mappedSiblingSlots, siblingNames.Count,
                "the advisory must name exactly the MAPPED non-selected slots "
                + $"(mapped={mappedSiblingSlots} named={siblingNames.Count})");

            string body = RewindInvoker.ComposeReFlyAdvisoryBody(siblingNames);
            InGameAssert.Contains(body, RewindInvoker.PatchedCareerFacetsSummary,
                "the advisory body must state the carried-forward career facets");
            InGameAssert.Contains(body, "tracked asteroids and comets, and planted flags",
                "the advisory body must state the post-C1 preservation guarantee");

            // Emit through the production emitter so a session log carries what a
            // player would have been told. The seam's InvokeRewind calls
            // StartInvoke directly and never opens ShowDialog, so this cell is the
            // only unattended path that exercises the emit.
            RewindInvoker.LogReFlyAdvisory(
                world.Rp.RewindPointId, world.SelectedSlotIndex, world.Rp.UT, siblingNames);

            string line = RewindInvoker.FormatReFlyAdvisoryLogLine(
                world.Rp.RewindPointId, world.SelectedSlotIndex, world.Rp.UT, siblingNames);
            InGameAssert.Contains(line,
                "preserved=unrelated-vessels+stations+asteroids+comets+flags",
                "the advisory log line must carry the grep-stable preserved= token");
            InGameAssert.Contains(line,
                "siblingSlotsPutAway=" + siblingNames.Count.ToString(CultureInfo.InvariantCulture),
                "the advisory log line must carry the sibling count it named");
        }

        private static int CountMappedNonSelectedSlots(RewindPoint rp, int selectedSlotIndex)
        {
            if (rp == null || rp.ChildSlots == null) return 0;

            var mapped = new HashSet<int>();
            AddMappedSlots(rp.PidSlotMap, mapped);
            AddMappedSlots(rp.RootPartPidMap, mapped);

            int count = 0;
            for (int i = 0; i < rp.ChildSlots.Count; i++)
            {
                ChildSlot slot = rp.ChildSlots[i];
                if (slot == null) continue;
                if (slot.SlotIndex == selectedSlotIndex) continue;
                if (!mapped.Contains(slot.SlotIndex)) continue;
                count++;
            }
            return count;
        }

        private static void AddMappedSlots(Dictionary<uint, int> map, HashSet<int> into)
        {
            if (map == null) return;
            foreach (KeyValuePair<uint, int> kv in map)
            {
                if (kv.Key == 0u) continue;
                into.Add(kv.Value);
            }
        }
    }
}
