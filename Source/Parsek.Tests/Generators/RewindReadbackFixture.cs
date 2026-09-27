using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Tests.Generators
{
    /// <summary>
    /// The <c>rewind-readback</c> injection preset: a rewindable tree whose RewindPoint lies
    /// BEFORE <c>career-science-pad</c>'s clock and whose RP quicksave carries the host's own
    /// Jumping Flea VERBATIM (same <c>persistentId</c>, same launch guid). Consumers: the
    /// <c>RB-1-rewind-readback-divergence</c> lane (the guard must flag) and
    /// <c>RB-2-rewind-readback-within-range</c> (the same shape where it must not).
    ///
    /// <para><b>What the preset is for.</b> The rewind read-back guard
    /// (<c>KspStatePatcher.RunRewindReadbackGuard</c>) flags a Re-Fly recalc target below
    /// <c>min(pre-rewind live economy, loaded quicksave economy)</c>. The one designed cause
    /// is Step 3b (<c>RewindInvoker.RetireResurrectedVesselRecoveryRows</c>): a vessel that
    /// is IN the RP quicksave, whose committed recording ended <c>Recovered</c> with its
    /// recovery credited AFTER the rewind point, is back in the world after the load, so its
    /// recovery rows are tombstoned and the funds target drops by the recovery value. With
    /// post-RP spending on top, that target sits below both witnesses. The guard reads the
    /// PRE-rewind career and the loaded quicksave, so every number it judges is real; the
    /// only thing this preset authors is the RP to rewind to.</para>
    ///
    /// <para><b>The flown half is real.</b> The lane flies the Flea (the
    /// <c>science_bench_recover</c> mission), so the recording, its <c>Recovered</c>
    /// terminal, its recovery funds row and its launch guid are all product-produced; then a
    /// real KSC hire spends funds; then the Re-Fly. The Flea the RP quicksave re-admits IS
    /// the vessel the lane flies (the host save's own VESSEL node, copied verbatim), which is
    /// what gives Step 3b its POSITIVE guid match
    /// (<c>ResurrectionRetirementEligibility.IsPositivelySameLaunch</c>).</para>
    ///
    /// <para><b>Player shape modelled.</b> The Flea sat rolled out on the pad while an
    /// earlier flight split elsewhere (the rewind point); the player later flew and
    /// recovered the Flea, spent funds at the KSC, then re-flew the earlier flight's crashed
    /// stage. The Flea is UNRELATED to the RP's slots, so the pre-load scrub preserves it
    /// and the Re-Fly resurrects it. (A recovered slot cannot be re-flown by a player:
    /// <c>UnfinishedFlightClassifier</c> admits neither a Recovered terminal nor a Recovered
    /// stash, so the resurrected vessel has to be an unrelated one.)</para>
    ///
    /// <para><b>Shape.</b> Everything in tree A is CREWLESS (ProbeShip snapshots): the Flea
    /// carries Jebediah Kerman, and a crewed tree-A snapshot would put a Parsek reservation
    /// on him before the Flea launches. Both slots end <c>Destroyed</c> before the save's
    /// clock, so nothing in tree A spawns and no tree-A ghost plays during the flight; slot 1
    /// is <c>CommittedProvisional</c>, the open Unfinished Flight the Re-Fly targets. The RP
    /// quicksave's two slot clones are the donor Flea minus its crew, parked in distinct
    /// runway stations, landed (see RunwayLatitude), clear of the resurrected Flea on the pad.</para>
    /// </summary>
    public static class RewindReadbackFixture
    {
        public const string RewindPointId = "rp_rb_root";
        public const string BranchPointId = "bp_rb_root";
        public const string RootRecordingId = "rb-stack-root";
        public const string UpperRecordingId = "rb-upper-b";
        public const string BoosterRecordingId = "rb-booster-a";
        public const string SlotVesselNamePrefix = "RB Slot ";
        public const string GroupName = "Rewind-Readback";

        public const int UpperSlotIndex = 0;
        public const int BoosterSlotIndex = 1;

        /// <summary><c>career-science-pad</c>'s clock (persistent.sfs FLIGHTSTATE UT).</summary>
        public const double HostSaveUT = 9.0599999999998957;

        /// <summary>The host's Jumping Flea (VESSEL 0 of career-science-pad).</summary>
        public const uint HostFleaPersistentId = 2905720181u;
        public const string HostFleaLaunchGuid = "f77e42072e3d4c59b04581daba628b55";

        /// <summary>Distinct runway stations for the two slot clones and the relay (degrees
        /// of longitude along the KSC runway, latitude <see cref="RunwayLatitude"/>).
        /// LANDED, never orbiting: a vessel the scene focuses in orbit earns stock progress
        /// (the `Orbit` milestone, altitude / speed / distance records), which inflates the
        /// post-RP earnings the guard's floor depends on (RB-1 reading 2026-09-27_1249).</summary>
        public const double RunwayLatitude = -0.048684738931822846;
        public const double RelayLongitude = -74.724506116829559;
        public const double UpperSlotLongitude = -74.715;
        public const double BoosterSlotLongitude = -74.705;
        public const double RunwayAltitude = 70.5;

        // Tree A flew from the runway, never from the pad the Flea stands on.
        private const double BaseLat = -0.0486;
        private const double BaseLon = -74.7245;

        /// <summary>The split UT: five seconds before the host save's clock.</summary>
        public static double SplitUTFor(double saveUT)
        {
            return saveUT - 5.0;
        }

        public static RewindPoint BuildRewindPoint(double splitUt)
        {
            return new RewindPoint
            {
                RewindPointId = RewindPointId,
                BranchPointId = BranchPointId,
                UT = splitUt,
                QuicksaveFilename = RecordingPaths.BuildRewindPointRelativePath(RewindPointId),
                FocusSlotIndex = UpperSlotIndex,
                // Durable staging RP: no CreatingSessionId, so LoadTimeSweep keeps it.
                SessionProvisional = true,
                CreatingSessionId = null,
                Corrupted = false,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = UpperSlotIndex,
                        OriginChildRecordingId = UpperRecordingId,
                        Controllable = true,
                    },
                    new ChildSlot
                    {
                        SlotIndex = BoosterSlotIndex,
                        OriginChildRecordingId = BoosterRecordingId,
                        Controllable = true,
                    },
                },
                PidSlotMap = new Dictionary<uint, int>
                {
                    [ScenarioWriter.DeriveVesselPersistentId(UpperRecordingId)] = UpperSlotIndex,
                    [ScenarioWriter.DeriveVesselPersistentId(BoosterRecordingId)] = BoosterSlotIndex,
                },
                RootPartPidMap = new Dictionary<uint, int>
                {
                    [ScenarioWriter.DeriveRootPartPersistentId(UpperRecordingId)] = UpperSlotIndex,
                    [ScenarioWriter.DeriveRootPartPersistentId(BoosterRecordingId)] = BoosterSlotIndex,
                },
            };
        }

        public static void PopulateWriter(ScenarioWriter writer, double saveUT)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            double splitUt = SplitUTFor(saveUT);
            writer.AddRecordingsAsTree(new[]
            {
                BuildRoot(splitUt),
                BuildSlot(UpperRecordingId, "RB Upper B", splitUt, MergeState.Immutable),
                BuildSlot(BoosterRecordingId, "RB Booster A", splitUt,
                    MergeState.CommittedProvisional),
            });
            writer.AddRewindPoint(BuildRewindPoint(splitUt));
            writer.RewindSlotVesselNamePrefix = SlotVesselNamePrefix;
            writer.RewindPointWorldAuthor = AuthorWorld;
        }

        /// <summary>
        /// RP-quicksave world author: runs after the two slot clones are written (and
        /// before anything is appended), strips their crew, lands them on the runway, then
        /// re-admits the donor's own Flea verbatim. Public so a test can drive it.
        /// </summary>
        public static void AuthorWorld(RewindPointQuicksaveWorld world)
        {
            if (world == null) return;

            ConfigNode flightState = world.FlightState;
            ConfigNode[] slots = flightState.GetNodes("VESSEL");
            for (int i = 0; i < slots.Length; i++)
            {
                ConfigNode slot = slots[i];
                StripCrew(slot);
                uint pid = ParsePid(slot);
                GiveClonePartsFreshIdentities(slot, "rb-slot:" + pid.ToString(CultureInfo.InvariantCulture));
                double lon = pid == ScenarioWriter.DeriveVesselPersistentId(BoosterRecordingId)
                    ? BoosterSlotLongitude
                    : UpperSlotLongitude;
                LandOnRunway(slot, lon);
            }

            // The host's real Flea, byte-for-byte: its pid and launch guid are the
            // identity the lane's own recording of it captures.
            IReadOnlyList<ConfigNode> donors = world.RemovedDonorVessels;
            for (int i = 0; i < donors.Count; i++)
            {
                ConfigNode donor = donors[i];
                if (donor == null) continue;
                if (ParsePid(donor) != HostFleaPersistentId) continue;
                world.ReadmitDonorVessel(donor);
                break;
            }
        }

        /// <summary>The runway relay the lane focuses to get back into FLIGHT after the
        /// Flea's recovery lands it at the KSC (<c>InvokeRewind</c> is a FLIGHT verb).</summary>
        public const string RelayVesselName = "RB Relay";
        public const string RelayRole = "rb-relay";

        public static uint RelayVesselPid => ScenarioWriter.DeriveVesselPersistentId(RelayRole);
        public static uint RelayRootPartPid => ScenarioWriter.DeriveRootPartPersistentId(RelayRole);

        /// <summary>
        /// Appends a crewless, runway-landed clone of the host Flea to the LIVE save's
        /// FLIGHTSTATE (never to the RP quicksave, which is already written by the time
        /// this runs): after the Flea is recovered the save still carries a focusable
        /// vessel, so a <c>SaveGame</c> + <c>LoadGame</c> pair lands in FLIGHT and the
        /// Re-Fly can be invoked. Appended AFTER the Flea, so <c>activeVessel = 0</c>
        /// still focuses the Flea on the first load. Returns false when the save carries
        /// no Flea to clone.
        /// </summary>
        public static bool AppendParkedRelayVessel(string savePath)
        {
            ConfigNode root = ConfigNode.Load(savePath);
            if (root == null) return false;
            ConfigNode game = root.GetNode("GAME") ?? root;
            ConfigNode flightState = game.GetNode("FLIGHTSTATE");
            if (flightState == null) return false;

            ConfigNode flea = null;
            foreach (ConfigNode v in flightState.GetNodes("VESSEL"))
            {
                if (ParsePid(v) == HostFleaPersistentId) { flea = v; break; }
            }
            if (flea == null) return false;

            ConfigNode relay = flightState.AddNode(flea.CreateCopy());
            GiveClonePartsFreshIdentities(relay, RelayRole);
            ScenarioWriter.StampUnrelatedVesselIdentity(
                relay, RelayVesselPid, RelayRootPartPid, RelayVesselName, "Relay", RelayRole);
            StripCrew(relay);
            LandOnRunway(relay, RelayLongitude);

            ConfigNode gameCopy = game.CreateCopy();
            gameCopy.name = "GAME";
            var outRoot = new ConfigNode();
            outRoot.AddNode(gameCopy);
            outRoot.Save(savePath);
            return true;
        }

        /// <summary>
        /// Gives every PART of a Flea clone its own flight id (<c>uid</c>) and every non-root
        /// PART its own <c>persistentId</c>, derived from <paramref name="seed"/>, and repoints
        /// the VESSEL <c>ref</c> at the new root uid. The root part's persistentId is left
        /// alone: the caller stamps it (the relay) or the RP slot map keys on it (the slots).
        ///
        /// <para>A clone that shares the Flea's part ids breaks the flight while both are
        /// loaded. KSP re-rolls a colliding persistentId on load but not the flight id, and
        /// kRPC resolves parts by flight id, so the mission read the runway relay's parts in
        /// place of the Flea's: no thrust on the ascent, then no parachute at all once the
        /// relay unloaded (RB-1 `2026-09-27_1353` and RB-2 `2026-09-27_1417`, both
        /// `set deploy_altitude=2500m on 0 parachute(s)` and a crash).</para>
        /// </summary>
        internal static void GiveClonePartsFreshIdentities(ConfigNode vessel, string seed)
        {
            if (vessel == null) return;
            var ic = CultureInfo.InvariantCulture;
            int rootIndex;
            if (!int.TryParse(vessel.GetValue("root"), NumberStyles.Integer, ic, out rootIndex))
                rootIndex = 0;
            ConfigNode[] parts = vessel.GetNodes("PART");
            string oldRootUid = rootIndex >= 0 && rootIndex < parts.Length
                ? parts[rootIndex].GetValue("uid")
                : null;
            string newRootUid = null;
            for (int i = 0; i < parts.Length; i++)
            {
                string key = (seed ?? "") + ":part" + i.ToString(ic);
                string uid = ScenarioWriter.DeriveVesselPersistentId(key + ":uid").ToString(ic);
                parts[i].SetValue("uid", uid, true);
                if (i == rootIndex)
                    newRootUid = uid;
                else
                    parts[i].SetValue("persistentId",
                        ScenarioWriter.DeriveRootPartPersistentId(key).ToString(ic), true);
            }
            if (newRootUid != null
                && (string.IsNullOrEmpty(vessel.GetValue("ref"))
                    || string.Equals(vessel.GetValue("ref"), oldRootUid, StringComparison.Ordinal)))
            {
                vessel.SetValue("ref", newRootUid, true);
            }
        }

        internal static void StripCrew(ConfigNode vessel)
        {
            if (vessel == null) return;
            foreach (ConfigNode part in vessel.GetNodes("PART"))
                part.RemoveValues("crew");
        }

        /// <summary>Stands a cloned vessel on the KSC runway at <paramref name="longitude"/>,
        /// LANDED, with ground positioning left on so KSP settles it on the surface.</summary>
        internal static void LandOnRunway(ConfigNode vessel, double longitude)
        {
            var ic = CultureInfo.InvariantCulture;
            vessel.SetValue("sit", "LANDED", true);
            vessel.SetValue("landed", "True", true);
            vessel.SetValue("splashed", "False", true);
            vessel.SetValue("landedAt", "Runway", true);
            vessel.SetValue("displaylandedAt", "Runway", true);
            vessel.SetValue("launchedFrom", "Runway", true);
            vessel.SetValue("skipGroundPositioning", "False", true);
            vessel.SetValue("lat", RunwayLatitude.ToString("R", ic), true);
            vessel.SetValue("lon", longitude.ToString("R", ic), true);
            vessel.SetValue("alt", RunwayAltitude.ToString("R", ic), true);
        }

        private static uint ParsePid(ConfigNode vessel)
        {
            uint pid;
            uint.TryParse(vessel?.GetValue("persistentId"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out pid);
            return pid;
        }

        // ---- recording builders -------------------------------------------

        // Pre-split ascent off the runway, ending at the split. Crewless.
        private static RecordingBuilder BuildRoot(double splitUt)
        {
            double t = splitUt - 3.0;
            var b = new RecordingBuilder("RB Stack")
                .WithRecordingId(RootRecordingId)
                .WithRecordingGroup(GroupName);
            b.AddPoint(t, BaseLat, BaseLon, 80);
            b.AddPoint(t + 1.0, BaseLat, BaseLon, 400);
            b.AddPoint(t + 2.0, BaseLat, BaseLon, 900);
            b.AddPoint(splitUt, BaseLat, BaseLon, 1500);
            b.WithVesselSnapshot(
                VesselSnapshotBuilder.ProbeShip("RB Stack", pid: 230001)
                    .AsLanded(BaseLat, BaseLon, 80));
            return b;
        }

        // A slot child that falls back and is destroyed before the save's clock.
        // Destroyed on BOTH slots keeps tree A from spawning anything; only slot 1 is
        // an OPEN Unfinished Flight (CommittedProvisional - see RewindB9Fixture's
        // booster note for why an injected tree must author that state directly).
        private static RecordingBuilder BuildSlot(
            string recordingId, string vesselName, double splitUt, MergeState mergeState)
        {
            double t = splitUt;
            var b = new RecordingBuilder(vesselName)
                .WithRecordingId(recordingId)
                .WithParentRecordingId(RootRecordingId)
                .WithMergeState(mergeState)
                .WithRecordedVesselGuid(ScenarioWriter.DeriveVesselLaunchGuid(recordingId))
                .WithRecordingGroup(GroupName)
                .WithTerminalState((int)TerminalState.Destroyed)
                .WithTerrainHeightAtEnd(70);
            b.AddPoint(t, BaseLat, BaseLon, 1500);
            b.AddPoint(t + 1.5, BaseLat, BaseLon, 1000);
            b.AddPoint(t + 3.0, BaseLat, BaseLon, 400);
            b.AddPoint(t + 4.0, BaseLat, BaseLon, 70);
            uint snapPid = recordingId == BoosterRecordingId ? 230003u : 230002u;
            b.WithVesselSnapshot(
                VesselSnapshotBuilder.ProbeShip(vesselName, pid: snapPid)
                    .AsLanded(BaseLat, BaseLon, 70));
            return b;
        }
    }
}
