using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 6 of Rewind-to-Staging (design §6.4 step 4): guards
    /// <see cref="PostLoadStripper.Strip(RewindPoint, int, IVesselEnumeration)"/>.
    /// Uses stub <see cref="IStrippableVessel"/>s so the tests drive identifier
    /// correlation without a live KSP scene.
    /// </summary>
    [Collection("Sequential")]
    public class PostLoadStripperTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly HashSet<uint> ghostPidsBackup;

        public PostLoadStripperTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            // Snapshot + clear GhostMapPresence so per-test injection is hermetic.
            ghostPidsBackup = new HashSet<uint>(GhostMapPresence.ghostMapVesselPids);
            GhostMapPresence.ghostMapVesselPids.Clear();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;

            GhostMapPresence.ghostMapVesselPids.Clear();
            foreach (var pid in ghostPidsBackup)
                GhostMapPresence.ghostMapVesselPids.Add(pid);
        }

        private sealed class StubVessel : IStrippableVessel
        {
            public uint PersistentId { get; set; }
            public uint RootPartPersistentId { get; set; }
            public string VesselName { get; set; }
            public VesselType VesselType { get; set; } = VesselType.Ship;
            public Vessel LiveVessel => null; // tests do not construct live Unity objects
            public bool Died { get; private set; }
            public bool ThrowOnDie { get; set; }
            public Action OnDie { get; set; }
            // Captures GameStateRecorder.SuppressCrewEvents at the moment
            // Die() runs so the StripVessel_DiePathIsGuarded test can verify
            // the suppression guard is active across the silent-removal path.
            public bool? SuppressCrewObservedDuringDie { get; private set; }
            public void Die()
            {
                SuppressCrewObservedDuringDie = GameStateRecorder.SuppressCrewEvents;
                if (ThrowOnDie) throw new InvalidOperationException("simulated");
                OnDie?.Invoke();
                Died = true;
            }
        }

        private sealed class StubEnumeration : IVesselEnumeration
        {
            public readonly List<IStrippableVessel> Vessels;
            public StubEnumeration(IEnumerable<IStrippableVessel> v)
            {
                Vessels = new List<IStrippableVessel>(v ?? Array.Empty<IStrippableVessel>());
            }
            public IEnumerable<IStrippableVessel> EnumerateVessels() => Vessels;
        }

        private static RewindPoint MakeRp(
            Dictionary<uint, int> pidSlotMap = null,
            Dictionary<uint, int> rootPartPidMap = null)
        {
            return new RewindPoint
            {
                RewindPointId = "rp_test",
                BranchPointId = "bp_test",
                ChildSlots = new List<ChildSlot>(),
                PidSlotMap = pidSlotMap ?? new Dictionary<uint, int>(),
                RootPartPidMap = rootPartPidMap ?? new Dictionary<uint, int>(),
            };
        }

        [Fact]
        public void PrimaryMatchViaPidSlotMap()
        {
            var rp = MakeRp(new Dictionary<uint, int>
            {
                { 100u, 0 }, // selected
                { 101u, 1 }, // stripped
            });

            var v0 = new StubVessel { PersistentId = 100, VesselName = "V0" };
            var v1 = new StubVessel { PersistentId = 101, VesselName = "V1" };
            var source = new StubEnumeration(new IStrippableVessel[] { v0, v1 });

            var result = PostLoadStripper.Strip(rp, selectedSlotIndex: 0, source);

            Assert.Equal(100u, result.SelectedPid);
            Assert.False(v0.Died);
            Assert.True(v1.Died);
            Assert.Contains(101u, result.StrippedPids);
            Assert.DoesNotContain(100u, result.StrippedPids);
            Assert.Equal(0, result.FallbackMatches);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("Strip stripped=[101]") &&
                l.Contains("selected=100"));
        }

        [Fact]
        public void FallbackMatchViaRootPartPidMap()
        {
            // PidSlotMap does not contain the vessel pid; RootPartPidMap does.
            var rp = MakeRp(
                pidSlotMap: new Dictionary<uint, int>(),
                rootPartPidMap: new Dictionary<uint, int>
                {
                    { 5000u, 0 },
                    { 5001u, 1 },
                });

            var v0 = new StubVessel
            {
                PersistentId = 200,
                RootPartPersistentId = 5000,
                VesselName = "V0",
            };
            var v1 = new StubVessel
            {
                PersistentId = 201,
                RootPartPersistentId = 5001,
                VesselName = "V1",
            };
            var source = new StubEnumeration(new IStrippableVessel[] { v0, v1 });

            var result = PostLoadStripper.Strip(rp, selectedSlotIndex: 0, source);

            Assert.Equal(200u, result.SelectedPid);
            Assert.True(v1.Died);
            Assert.False(v0.Died);
            Assert.Equal(2, result.FallbackMatches);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[Rewind]") &&
                l.Contains("Fallback match"));
        }

        [Fact]
        public void GhostProtoVesselGuard_NotStripped()
        {
            // Ghost pid is in the guard set; even if the RP's maps claim it, we do not strip.
            GhostMapPresence.ghostMapVesselPids.Add(9000u);

            var rp = MakeRp(new Dictionary<uint, int>
            {
                { 300u, 0 }, // selected
                { 9000u, 1 }, // ghost guard wins
            });
            var v0 = new StubVessel { PersistentId = 300 };
            var vg = new StubVessel { PersistentId = 9000, VesselName = "GHOST" };
            var source = new StubEnumeration(new IStrippableVessel[] { v0, vg });

            var result = PostLoadStripper.Strip(rp, selectedSlotIndex: 0, source);

            Assert.Equal(300u, result.SelectedPid);
            Assert.False(vg.Died);
            Assert.Equal(1, result.GhostsGuarded);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("Strip guard: ghost-ProtoVessel") &&
                l.Contains("9000"));
        }

        [Fact]
        public void UnrelatedVesselLeftAlone()
        {
            // Vessel not in either slot map - do nothing.
            var rp = MakeRp(new Dictionary<uint, int>
            {
                { 400u, 0 },
            });
            var v0 = new StubVessel { PersistentId = 400 };
            var unrelated = new StubVessel { PersistentId = 999, VesselName = "Unrelated" };
            var source = new StubEnumeration(new IStrippableVessel[] { v0, unrelated });

            var result = PostLoadStripper.Strip(rp, selectedSlotIndex: 0, source);

            Assert.Equal(400u, result.SelectedPid);
            Assert.False(unrelated.Died);
            Assert.Equal(1, result.LeftAlone);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("Strip leaveAlone") &&
                l.Contains("unrelated v=999"));
        }

        [Fact]
        public void StrictStrip_StripsUnmatchedVessels()
        {
            var rp = MakeRp(new Dictionary<uint, int>
            {
                { 400u, 0 },
            });
            var selected = new StubVessel { PersistentId = 400, VesselName = "Selected" };
            var unrelated = new StubVessel { PersistentId = 999, VesselName = "Upper Stage" };
            var source = new StubEnumeration(new IStrippableVessel[] { selected, unrelated });

            var result = PostLoadStripper.Strip(
                rp,
                selectedSlotIndex: 0,
                source,
                stripUnmatchedVessels: true);

            Assert.Equal(400u, result.SelectedPid);
            Assert.False(selected.Died);
            Assert.True(unrelated.Died);
            Assert.Contains(999u, result.StrippedPids);
            Assert.Equal(0, result.LeftAlone);
            Assert.Empty(result.LeftAlonePidNames);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[Rewind]") &&
                l.Contains("Strip strict: stripping 1 unmatched vessel") &&
                l.Contains("999:Upper Stage"));
        }

        [Fact]
        public void StrictStrip_SnapshotsBeforeDieMutatesSource()
        {
            var rp = MakeRp(new Dictionary<uint, int>
            {
                { 400u, 0 },
            });
            var selected = new StubVessel { PersistentId = 400, VesselName = "Selected" };
            var unrelatedA = new StubVessel { PersistentId = 901, VesselName = "A" };
            var unrelatedB = new StubVessel { PersistentId = 902, VesselName = "B" };
            var source = new StubEnumeration(new IStrippableVessel[]
            {
                selected,
                unrelatedA,
                unrelatedB,
            });
            unrelatedA.OnDie = () => source.Vessels.Remove(unrelatedB);

            var result = PostLoadStripper.Strip(
                rp,
                selectedSlotIndex: 0,
                source,
                stripUnmatchedVessels: true);

            Assert.Equal(400u, result.SelectedPid);
            Assert.False(selected.Died);
            Assert.True(unrelatedA.Died);
            Assert.True(unrelatedB.Died);
            Assert.Contains(901u, result.StrippedPids);
            Assert.Contains(902u, result.StrippedPids);
        }

        [Fact]
        public void SelectedSlotVessel_NotStripped()
        {
            var rp = MakeRp(new Dictionary<uint, int>
            {
                { 500u, 0 },
                { 501u, 1 },
                { 502u, 2 },
            });
            var v0 = new StubVessel { PersistentId = 500 };
            var v1 = new StubVessel { PersistentId = 501 };
            var v2 = new StubVessel { PersistentId = 502 };
            var source = new StubEnumeration(new IStrippableVessel[] { v0, v1, v2 });

            var result = PostLoadStripper.Strip(rp, selectedSlotIndex: 1, source);

            Assert.Equal(501u, result.SelectedPid);
            Assert.True(v0.Died);
            Assert.False(v1.Died);
            Assert.True(v2.Died);
        }

        [Fact]
        public void NoMatchingVessels_LogsAndContinues()
        {
            var rp = MakeRp(new Dictionary<uint, int>
            {
                { 600u, 0 },
            });
            // Only an unrelated vessel.
            var unrelated = new StubVessel { PersistentId = 700 };
            var source = new StubEnumeration(new IStrippableVessel[] { unrelated });

            var result = PostLoadStripper.Strip(rp, selectedSlotIndex: 0, source);

            Assert.Equal(0u, result.SelectedPid);
            Assert.Null(result.SelectedVessel);
            Assert.Empty(result.StrippedPids);
            Assert.Equal(1, result.LeftAlone);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("Strip stripped=[]") &&
                l.Contains("selected=none"));
        }

        [Fact]
        public void NullRewindPoint_ReturnsEmptyResult()
        {
            var result = PostLoadStripper.Strip(null, 0, new StubEnumeration(Array.Empty<IStrippableVessel>()));
            Assert.Equal(0u, result.SelectedPid);
            Assert.NotNull(result.StrippedPids);
            Assert.Empty(result.StrippedPids);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[Rewind]") && l.Contains("null rp"));
        }

        [Fact]
        public void Strip_CapturesLeftAlonePidNamesForCollisionDetection()
        {
            var rp = MakeRp(new Dictionary<uint, int>
            {
                { 600u, 0 },
            });
            var selected = new StubVessel { PersistentId = 600u, VesselName = "Kerbal X Probe" };
            var preexistingOrbiter = new StubVessel { PersistentId = 700u, VesselName = "Kerbal X" };
            var asteroid = new StubVessel { PersistentId = 701u, VesselName = "Ast. ABC-123" };
            var source = new StubEnumeration(new IStrippableVessel[]
            {
                selected, preexistingOrbiter, asteroid,
            });

            var result = PostLoadStripper.Strip(rp, selectedSlotIndex: 0, source);

            Assert.Equal(2, result.LeftAlone);
            Assert.NotNull(result.LeftAlonePidNames);
            Assert.Equal(2, result.LeftAlonePidNames.Count);
            // PR #577 P2: pids are paired with names so the resurvey can
            // scope to the pre-existing left-alone set.
            Assert.Contains(result.LeftAlonePidNames, t => t.pid == 700u && t.name == "Kerbal X");
            Assert.Contains(result.LeftAlonePidNames, t => t.pid == 701u && t.name == "Ast. ABC-123");
        }

        [Fact]
        public void FindTreeNameCollisions_ReturnsIntersectionDedupedOrdinal()
        {
            var leftAlone = new[] { "Kerbal X", "Kerbal X", "Ast. ABC-123", "Kerbal X Debris" };
            var treeNames = new[] { "Kerbal X", "Kerbal X Probe", "Kerbal X Debris" };

            var collisions = PostLoadStripper.FindTreeNameCollisions(leftAlone, treeNames);

            Assert.Equal(2, collisions.Count);
            Assert.Contains("Kerbal X", collisions);
            Assert.Contains("Kerbal X Debris", collisions);
            // Dedup: duplicate "Kerbal X" in input produces a single output entry.
            Assert.Single(collisions.FindAll(s => s == "Kerbal X"));
        }

        [Fact]
        public void FindTreeNameCollisions_NullInputsReturnEmpty()
        {
            Assert.Empty(PostLoadStripper.FindTreeNameCollisions(null, new[] { "X" }));
            Assert.Empty(PostLoadStripper.FindTreeNameCollisions(new[] { "X" }, null));
            Assert.Empty(PostLoadStripper.FindTreeNameCollisions(null, null));
        }

        [Fact]
        public void FindTreeNameCollisions_NoOverlapReturnsEmpty()
        {
            var collisions = PostLoadStripper.FindTreeNameCollisions(
                new[] { "Ast. ABC-123", "UnknownComet" },
                new[] { "Kerbal X", "Kerbal X Probe" });
            Assert.Empty(collisions);
        }

        [Fact]
        public void FindTreeNameCollisions_CaseSensitive()
        {
            // Ordinal comparison: "Kerbal X" != "kerbal x".
            var collisions = PostLoadStripper.FindTreeNameCollisions(
                new[] { "kerbal x" }, new[] { "Kerbal X" });
            Assert.Empty(collisions);
        }

        [Fact]
        public void ShouldPreserveVesselType_FlagOnly()
        {
            // Contract: only VesselType.Flag is preserved. Asteroids/comets
            // (SpaceObject) are left alone by the existing unmatched-vessel
            // branch, not by this preserve predicate.
            Assert.True(PostLoadStripper.ShouldPreserveVesselType(VesselType.Flag));
            Assert.False(PostLoadStripper.ShouldPreserveVesselType(VesselType.Ship));
            Assert.False(PostLoadStripper.ShouldPreserveVesselType(VesselType.Probe));
            Assert.False(PostLoadStripper.ShouldPreserveVesselType(VesselType.Debris));
            Assert.False(PostLoadStripper.ShouldPreserveVesselType(VesselType.EVA));
            Assert.False(PostLoadStripper.ShouldPreserveVesselType(VesselType.SpaceObject));
            Assert.False(PostLoadStripper.ShouldPreserveVesselType(VesselType.Unknown));
        }

        [Fact]
        public void Strip_FlagVessel_PreservedEvenUnderStrictStrip()
        {
            // Repro of the user-reported bug: planted flag is one of the
            // unmatched vessels in strict-strip mode; without the preserve
            // branch it would be stripped along with sibling debris. The
            // flag's PID is not in any slot map and it has no Parsek recording.
            var rp = MakeRp(new Dictionary<uint, int>
            {
                { 100u, 0 }, // probe (selected)
                { 101u, 1 }, // capsule (sibling slot)
            });

            var probe = new StubVessel { PersistentId = 100u, VesselName = "Kerbal X Probe", VesselType = VesselType.Probe };
            var capsule = new StubVessel { PersistentId = 101u, VesselName = "Kerbal X", VesselType = VesselType.Ship };
            var flag = new StubVessel { PersistentId = 200u, VesselName = "Flag", VesselType = VesselType.Flag };
            var source = new StubEnumeration(new IStrippableVessel[] { probe, capsule, flag });

            var result = PostLoadStripper.Strip(
                rp,
                selectedSlotIndex: 0,
                source,
                stripUnmatchedVessels: true);

            Assert.Equal(100u, result.SelectedPid);
            Assert.False(probe.Died);
            Assert.True(capsule.Died);
            Assert.False(flag.Died);
            Assert.Contains(101u, result.StrippedPids);
            // Pin the bypass placement: the flag's pid is NOT stripped, NOT
            // counted in LeftAlone, and IS surfaced via PreservedFlagPids.
            Assert.DoesNotContain(200u, result.StrippedPids);
            Assert.Equal(0, result.LeftAlone);
            Assert.DoesNotContain(result.LeftAlonePidNames, t => t.pid == 200u);
            Assert.NotNull(result.PreservedFlagPids);
            Assert.Single(result.PreservedFlagPids);
            Assert.Contains(200u, result.PreservedFlagPids);

            // Per-vessel verbose preserve log.
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("Strip preserve: flag vessel") &&
                l.Contains("v=200") && l.Contains("'Flag'"));
            // Summary log line.
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("Strip preserved 1 flag vessel(s)") &&
                l.Contains("[200]"));
            // Strip-stripped summary includes preservedFlags count.
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("Strip stripped=[101]") &&
                l.Contains("preservedFlags=1"));
            // Flag must NOT appear in the strict-unmatched WARN message.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("Strip strict") && l.Contains("200:Flag"));
        }

        [Fact]
        public void Strip_FlagOnly_PreservedAlongsideSelected()
        {
            // Sanity: only active vessel + flag in scene. Flag is preserved,
            // selected vessel kept, nothing stripped.
            var rp = MakeRp(new Dictionary<uint, int>
            {
                { 100u, 0 },
            });
            var probe = new StubVessel { PersistentId = 100u, VesselName = "Probe", VesselType = VesselType.Probe };
            var flag = new StubVessel { PersistentId = 300u, VesselName = "Flag", VesselType = VesselType.Flag };
            var source = new StubEnumeration(new IStrippableVessel[] { probe, flag });

            var result = PostLoadStripper.Strip(
                rp,
                selectedSlotIndex: 0,
                source,
                stripUnmatchedVessels: true);

            Assert.Equal(100u, result.SelectedPid);
            Assert.False(probe.Died);
            Assert.False(flag.Died);
            Assert.Empty(result.StrippedPids);
            Assert.Single(result.PreservedFlagPids);
            Assert.Contains(300u, result.PreservedFlagPids);
        }

        [Fact]
        public void Strip_FlagPreserved_RegardlessOfSlotMapMembership()
        {
            // Defense-in-depth pin: VesselType.Flag is checked BEFORE the
            // slot-map match, so a flag pid is preserved regardless of
            // slot-map membership.
            //
            // In production this collision is impossible by construction
            // — slot maps are built from recorded Parsek vessels, and
            // planted flags are never tracked by the recorder — but the
            // ordering invariant is the load-bearing claim: the preserve
            // bypass cannot be reordered after slot-map matching without
            // breaking the "flag is durable career milestone, not a re-fly
            // sibling" contract. This test fails if a future refactor
            // accidentally folds the preserve branch into the
            // already-matched-vessel iteration loop and ends up stripping
            // flag pids that happen to share a numeric value with an
            // unrelated rewind-point slot entry.
            var rp = MakeRp(new Dictionary<uint, int>
            {
                { 100u, 0 }, // selected probe
                { 999u, 1 }, // contrived: flag PID is registered as a sibling slot
            });
            var probe = new StubVessel { PersistentId = 100u, VesselName = "Probe", VesselType = VesselType.Probe };
            var flag = new StubVessel { PersistentId = 999u, VesselName = "Flag", VesselType = VesselType.Flag };
            var source = new StubEnumeration(new IStrippableVessel[] { probe, flag });

            var result = PostLoadStripper.Strip(
                rp,
                selectedSlotIndex: 0,
                source,
                stripUnmatchedVessels: true);

            Assert.Equal(100u, result.SelectedPid);
            Assert.False(flag.Died);
            Assert.DoesNotContain(999u, result.StrippedPids);
            Assert.Contains(999u, result.PreservedFlagPids);
        }

        [Fact]
        public void Strip_NoFlags_PreservedFlagPidsEmpty_NoSummaryLog()
        {
            // Without any VesselType.Flag candidates the preserve list is
            // empty and the dedicated "Strip preserved N flag vessel(s)"
            // summary line must NOT be emitted (only the standard summary
            // with preservedFlags=0 is logged).
            var rp = MakeRp(new Dictionary<uint, int>
            {
                { 100u, 0 },
                { 101u, 1 },
            });
            var v0 = new StubVessel { PersistentId = 100u, VesselType = VesselType.Probe };
            var v1 = new StubVessel { PersistentId = 101u, VesselType = VesselType.Ship };
            var source = new StubEnumeration(new IStrippableVessel[] { v0, v1 });

            var result = PostLoadStripper.Strip(rp, selectedSlotIndex: 0, source);

            Assert.NotNull(result.PreservedFlagPids);
            Assert.Empty(result.PreservedFlagPids);
            Assert.Contains(logLines, l => l.Contains("preservedFlags=0"));
            Assert.DoesNotContain(logLines, l => l.Contains("Strip preserved") && l.Contains("flag vessel"));
        }

        [Fact]
        public void StripVessel_DiePathIsGuarded()
        {
            // §6.4 step 4 contract: Strip is a SILENT vessel removal. The
            // SuppressionGuard wrapped around v.Die() inside StripVessel
            // raises GameStateRecorder.SuppressCrewEvents for the duration
            // of the call so any CrewKilled / CrewRemoved fanout from
            // Vessel.Die() does not leak into the ledger as a player-driven
            // kerbal death. This test pins that contract by capturing the
            // suppression flag inside the stub's Die() and asserting it
            // was true at call time and false afterwards (Dispose runs).
            bool priorSuppress = GameStateRecorder.SuppressCrewEvents;
            try
            {
                GameStateRecorder.SuppressCrewEvents = false;

                var rp = MakeRp(new Dictionary<uint, int>
                {
                    { 800u, 0 }, // selected (not stripped)
                    { 801u, 1 }, // stripped — Die() must run inside the guard
                });
                var selected = new StubVessel { PersistentId = 800u };
                var stripped = new StubVessel { PersistentId = 801u, VesselName = "Sib" };
                var source = new StubEnumeration(new IStrippableVessel[] { selected, stripped });

                var result = PostLoadStripper.Strip(rp, selectedSlotIndex: 0, source);

                Assert.True(stripped.Died);
                Assert.True(stripped.SuppressCrewObservedDuringDie.HasValue);
                Assert.True(
                    stripped.SuppressCrewObservedDuringDie.Value,
                    "SuppressCrewEvents must be true while StripVessel runs Die()");

                // Selected vessel was not stripped, so its Die() never ran.
                Assert.False(selected.Died);
                Assert.Null(selected.SuppressCrewObservedDuringDie);

                // Guard's Dispose must reset the flag back to false after
                // StripVessel returns; nothing leaks past the using-block.
                Assert.False(
                    GameStateRecorder.SuppressCrewEvents,
                    "SuppressionGuard.Dispose must clear SuppressCrewEvents after Strip");

                Assert.Contains(801u, result.StrippedPids);
            }
            finally
            {
                GameStateRecorder.SuppressCrewEvents = priorSuppress;
            }
        }
    }
}
