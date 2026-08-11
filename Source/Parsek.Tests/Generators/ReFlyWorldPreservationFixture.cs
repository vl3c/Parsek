using System;
using System.Collections.Generic;

namespace Parsek.Tests.Generators
{
    /// <summary>
    /// The world-preservation rewind fixture (scenario `S4.2-refly-world-preservation`,
    /// injection preset `refly-world-preservation`): a sibling of
    /// <see cref="RewindB9Fixture"/> - same rewindable-tree shape, same RP-usability
    /// prerequisites - with one addition that is the entire point of it.
    ///
    /// <para>
    /// ITS RP QUICKSAVE CARRIES AN UNRELATED FLEET. Every other rewind fixture's
    /// sidecar holds exactly one VESSEL per child slot, so a Re-Fly against it can
    /// never demonstrate anything about the non-slot world: the world it loads has
    /// none. That is precisely the shape under which REFLY-DELETES-NON-SLOT-WORLD
    /// hid - the pre-load scrub deleted the fleet, and with no fleet in the fixture
    /// nothing red. This fixture puts one there.
    /// </para>
    ///
    /// <para>
    /// The fleet, and why each member is in it:
    /// </para>
    /// <list type="number">
    ///   <item><description><b>A Station</b> (<see cref="StationVesselName"/>) - the
    ///     ordinary "my station is gone" case, in orbit so it is never unpacked.</description></item>
    ///   <item><description><b>Every SpaceObject the donor save carries</b>,
    ///     re-admitted VERBATIM. Asteroids and comets were the worst of the original
    ///     blast radius (unrecoverable, unlike a station), and a re-admitted donor
    ///     node brings its real <c>PotatoRoid</c> part and real <c>DISCOVERY</c>
    ///     block, which a hand-authored node would not. See the SCOPE note.</description></item>
    ///   <item><description><b>A Flag</b> (<see cref="FlagVesselName"/>) - the
    ///     <c>PostLoadStripper.ShouldPreserveVesselType</c> carve-out that could
    ///     never fire while the scrub removed the node before the bypass was
    ///     consulted.</description></item>
    ///   <item><description><b>A Probe whose name COLLIDES with the re-flown
    ///     recording's craft name</b> (<see cref="BoosterVesselName"/>) - the #587
    ///     regression case. Non-debris, so <c>IsDebrisKillSurveyCandidate</c> must
    ///     keep it out of the name-matched kill set entirely.</description></item>
    ///   <item><description><b>Debris under that SAME colliding name</b> - the
    ///     POSITIVE control. The debris-only narrowing must not have neutered #587:
    ///     this one is expected to be <c>Die()</c>d on an in-place continuation
    ///     re-fly, which is what makes the Probe's survival a discrimination rather
    ///     than a blanket "nothing is ever removed".</description></item>
    /// </list>
    ///
    /// <para>
    /// SCOPE, stated rather than papered over. The fleet is built by CLONING the
    /// donor save's own command vessel and re-stamping identity + <c>type</c> (the
    /// mechanism the per-slot vessels already use and which S4.1 has flown many
    /// times), so a Station / Flag / Probe / Debris here is a real loadable craft
    /// wearing a different type token, NOT a part-accurate station or a stock
    /// flagpole. The preservation invariant is about a vessel's EXISTENCE and
    /// <c>VesselType</c>, both of which survive that faithfully. The one member NOT
    /// synthesised this way is the asteroid: a procedural <c>SpaceObject</c> wants a
    /// <c>PotatoRoid</c> part plus a <c>DISCOVERY</c> block whose exact field set is
    /// not worth guessing, so the fixture re-admits the host save's own instead and
    /// carries NO asteroid at all when the host has none (the in-game cell then
    /// skips, naming that).
    /// </para>
    /// </summary>
    public static class ReFlyWorldPreservationFixture
    {
        /// <summary>Fixed, known RP id the scenario spec cites: <c>InvokeRewind rp=rp_wp_root</c>.</summary>
        public const string RewindPointId = "rp_wp_root";

        /// <summary>Weak link to the split BranchPoint (diagnostic only).</summary>
        public const string BranchPointId = "bp_wp_root";

        /// <summary>Pre-split ascent recording id (the tree root).</summary>
        public const string RootRecordingId = "wp-stack-root";

        /// <summary>Surviving upper-stage sibling: slot 0, the focus slot at the split.</summary>
        public const string UpperRecordingId = "wp-upper-b";

        /// <summary>Crashed booster sibling: slot 1, the re-fly target (<c>slot=1</c>).</summary>
        public const string BoosterRecordingId = "wp-booster-a";

        /// <summary>Slot index of the surviving upper stage.</summary>
        public const int UpperSlotIndex = 0;

        /// <summary>Slot index of the crashed booster - the <c>InvokeRewind slot=1</c> target.</summary>
        public const int BoosterSlotIndex = 1;

        /// <summary>
        /// Craft name of the crashed booster recording. The colliding Probe and the
        /// colliding Debris both carry it VERBATIM, which is what makes them a
        /// name-match against a Destroyed-terminal recording in the session's tree.
        /// </summary>
        public const string BoosterVesselName = "WP Booster A";

        /// <summary>Display-name prefix for the per-slot sidecar vessels.</summary>
        public const string SlotVesselNamePrefix = "WP Slot ";

        // The unrelated fleet's names. Each doubles as the seed for its pids (see
        // UnrelatedVesselPid) so the fixture and its assertions share one source.
        public const string StationVesselName = "WP Orbital Station";
        public const string FlagVesselName = "WP Pad Flag";

        /// <summary>
        /// The colliding Probe's name. EQUAL to <see cref="BoosterVesselName"/> on
        /// purpose - identity is the pid, and the name collision is the subject.
        /// </summary>
        public const string CollidingProbeVesselName = BoosterVesselName;

        /// <summary>
        /// The colliding Debris's name, also equal to <see cref="BoosterVesselName"/>.
        /// Distinguished from the Probe only by its pid pair and its <c>type</c>.
        /// </summary>
        public const string CollidingDebrisVesselName = BoosterVesselName;

        // Circular-orbit radii (metres from Kerbin's centre; R = 600 km). Distinct
        // per vessel so nothing shares an orbit, all well clear of the 2.25 km
        // physics bubble around the re-flown craft on the pad.
        private const double StationOrbitSma = 800000.0;
        private const double CollidingProbeOrbitSma = 900000.0;
        private const double CollidingDebrisOrbitSma = 950000.0;

        // KSC-relative launch coordinates (flat pad terrain), as B9.
        private const double BaseLat = -0.0972;
        private const double BaseLon = -74.5577;

        /// <summary>
        /// Vessel-level <c>persistentId</c> for an unrelated fleet member. Salted
        /// distinctly from <see cref="ScenarioWriter.DeriveVesselPersistentId"/> so a
        /// fleet pid can NEVER collide with a slot pid - a collision would silently
        /// turn a preserved vessel into a slot vessel and invert the whole test.
        /// </summary>
        public static uint UnrelatedVesselPid(string role)
            => ScenarioWriter.DeriveVesselPersistentId("wp-unrelated:" + (role ?? ""));

        /// <summary>Root-part <c>persistentId</c> for an unrelated fleet member.</summary>
        public static uint UnrelatedRootPartPid(string role)
            => ScenarioWriter.DeriveRootPartPersistentId("wp-unrelated:" + (role ?? ""));

        /// <summary>Role keys for <see cref="UnrelatedVesselPid"/>.</summary>
        public const string StationRole = "station";
        public const string FlagRole = "flag";
        public const string CollidingProbeRole = "colliding-probe";
        public const string CollidingDebrisRole = "colliding-debris";

        /// <summary>
        /// Builds the world-preservation <see cref="RewindPoint"/> for the split at
        /// <paramref name="splitUt"/>. Structurally identical to
        /// <see cref="RewindB9Fixture.BuildRewindPoint"/> - two controllable child
        /// slots, both pid maps populated, <see cref="RewindPoint.CreatingSessionId"/>
        /// null so <c>LoadTimeSweep</c> keeps it - with this fixture's own ids.
        /// </summary>
        public static RewindPoint BuildRewindPoint(double splitUt)
        {
            return new RewindPoint
            {
                RewindPointId = RewindPointId,
                BranchPointId = BranchPointId,
                UT = splitUt,
                QuicksaveFilename = RecordingPaths.BuildRewindPointRelativePath(RewindPointId),
                FocusSlotIndex = UpperSlotIndex,
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

        /// <summary>
        /// Populates a v3 <see cref="ScenarioWriter"/> with the committed tree, the
        /// split RewindPoint, this fixture's slot-name prefix AND the unrelated-fleet
        /// world author. The caller injects the writer into the fixture save; the RP
        /// quicksave sidecar (fleet included) is written self-referentially at inject
        /// time.
        /// </summary>
        public static void PopulateWriter(ScenarioWriter writer, double baseUT)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            double splitUt = baseUT + 60.0;

            writer.AddRecordingsAsTree(new[]
            {
                BuildRoot(baseUT),
                BuildUpperStage(splitUt),
                BuildBooster(splitUt),
            });

            writer.AddRewindPoint(BuildRewindPoint(splitUt));
            writer.RewindSlotVesselNamePrefix = SlotVesselNamePrefix;
            writer.RewindPointWorldAuthor = AuthorUnrelatedWorld;
        }

        /// <summary>
        /// Appends the unrelated fleet to an RP quicksave. Public so a test can drive
        /// it directly against a synthetic FLIGHTSTATE.
        /// </summary>
        public static void AuthorUnrelatedWorld(RewindPointQuicksaveWorld world)
        {
            if (world == null) return;

            // Real asteroids/comets first, verbatim from the host save (DISCOVERY
            // block and PotatoRoid part intact). Zero on a host with none - the
            // in-game Flag/SpaceObject cell skips rather than asserts in that case.
            world.ReadmitDonorVesselsOfType("SpaceObject");

            // Every member seeds its identity from its ROLE, never its display name:
            // the colliding Probe and Debris SHARE a name by design, and a
            // name-seeded launch guid would give two VESSEL nodes one KSP `pid`.
            world.AddUnrelatedClonedVessel(
                StationVesselName, "Station", StationRole,
                UnrelatedVesselPid(StationRole), UnrelatedRootPartPid(StationRole),
                StationOrbitSma);

            // The flag stays LANDED (a flag is a surface marker), keeping the donor's
            // own pad situation - the one member whose situation is not rewritten.
            world.AddUnrelatedClonedVessel(
                FlagVesselName, "Flag", FlagRole,
                UnrelatedVesselPid(FlagRole), UnrelatedRootPartPid(FlagRole));

            world.AddUnrelatedClonedVessel(
                CollidingProbeVesselName, "Probe", CollidingProbeRole,
                UnrelatedVesselPid(CollidingProbeRole), UnrelatedRootPartPid(CollidingProbeRole),
                CollidingProbeOrbitSma);

            world.AddUnrelatedClonedVessel(
                CollidingDebrisVesselName, "Debris", CollidingDebrisRole,
                UnrelatedVesselPid(CollidingDebrisRole), UnrelatedRootPartPid(CollidingDebrisRole),
                CollidingDebrisOrbitSma);
        }

        // ---- recording builders -------------------------------------------
        // Mirrors RewindB9Fixture's three builders (see that file for why the
        // booster carries MergeState.CommittedProvisional and a derived
        // RecordedVesselGuid); only the ids and craft names differ.

        private static RecordingBuilder BuildRoot(double baseUT)
        {
            double t = baseUT;
            var b = new RecordingBuilder("WP Stack")
                .WithRecordingId(RootRecordingId)
                .WithRecordingGroup("Rewind-WorldPreservation");
            b.AddPoint(t, BaseLat, BaseLon, 80);
            b.AddPoint(t + 15, BaseLat, BaseLon, 2400);
            b.AddPoint(t + 30, BaseLat, BaseLon, 9000);
            b.AddPoint(t + 45, BaseLat, BaseLon, 22000);
            b.AddPoint(t + 60, BaseLat, BaseLon, 41000);
            b.WithVesselSnapshot(
                VesselSnapshotBuilder.FleaRocket("WP Stack", "Jebediah Kerman", pid: 210001)
                    .AsLanded(BaseLat, BaseLon, 80));
            return b;
        }

        private static RecordingBuilder BuildUpperStage(double splitUt)
        {
            double t = splitUt;
            var b = new RecordingBuilder("WP Upper B")
                .WithRecordingId(UpperRecordingId)
                .WithParentRecordingId(RootRecordingId)
                .WithRecordedVesselGuid(
                    ScenarioWriter.DeriveVesselLaunchGuid(UpperRecordingId))
                .WithRecordingGroup("Rewind-WorldPreservation")
                .WithTerminalState((int)TerminalState.Orbiting);
            b.AddPoint(t, BaseLat, BaseLon, 41000);
            b.AddPoint(t + 20, BaseLat, BaseLon, 63000);
            b.AddPoint(t + 40, BaseLat, BaseLon, 78000);
            b.WithVesselSnapshot(
                VesselSnapshotBuilder.ProbeShip("WP Upper B", pid: 210002)
                    .AsOrbiting(700000, 0.01, 6.0, 0, 0, 0));
            return b;
        }

        // The re-fly target. TerminalState.Destroyed is load-bearing TWICE here: it
        // is what makes the slot a "crashed sibling" a re-fly targets, and it is what
        // puts BoosterVesselName into the #587 kill-eligible name set that the
        // colliding Probe must survive and the colliding Debris must not.
        private static RecordingBuilder BuildBooster(double splitUt)
        {
            double t = splitUt;
            var b = new RecordingBuilder(BoosterVesselName)
                .WithRecordingId(BoosterRecordingId)
                .WithParentRecordingId(RootRecordingId)
                .WithMergeState(MergeState.CommittedProvisional)
                .WithRecordedVesselGuid(
                    ScenarioWriter.DeriveVesselLaunchGuid(BoosterRecordingId))
                .WithRecordingGroup("Rewind-WorldPreservation")
                .WithTerminalState((int)TerminalState.Destroyed)
                .WithTerrainHeightAtEnd(75);
            b.AddPoint(t, BaseLat, BaseLon, 41000);
            b.AddPoint(t + 25, BaseLat, BaseLon, 18000);
            b.AddPoint(t + 45, BaseLat, BaseLon, 3000);
            b.AddPoint(t + 55, BaseLat, BaseLon, 75);
            b.WithVesselSnapshot(
                VesselSnapshotBuilder.ProbeShip(BoosterVesselName, pid: 210003)
                    .AsLanded(BaseLat, BaseLon, 75));
            return b;
        }
    }
}
