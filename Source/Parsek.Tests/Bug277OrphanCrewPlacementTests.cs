using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #277: a reserved kerbal who is on a separate EVA vessel
    /// at merge time was never replaced with their stand-in. The fix adds an
    /// orphan-placement second pass to SwapReservedCrewInFlight that walks
    /// committed recording snapshots to find where the original was originally
    /// seated, then places the stand-in into a matching part on the active vessel.
    ///
    /// These tests cover the pure helper ResolveOrphanSeatFromSnapshots and the
    /// reverse-stand-in mapping logic. The live AddCrewmember placement path
    /// requires KSP runtime and is exercised by an in-game test in RuntimeTests.cs.
    /// </summary>
    [Collection("Sequential")]
    public class Bug277OrphanCrewPlacementTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug277OrphanCrewPlacementTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
        }

        // ────────────────────────────────────────────────────────
        // Helpers
        // ────────────────────────────────────────────────────────

        private static ConfigNode BuildSnapshotWithCrew(
            uint partPid, string partName, params string[] crewNames)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("name", partName);
            part.AddValue("pid", partPid.ToString());
            for (int i = 0; i < crewNames.Length; i++)
                part.AddValue("crew", crewNames[i]);
            return snapshot;
        }

        private static ConfigNode BuildMultiPartSnapshot(
            params (uint pid, string partName, string[] crew)[] parts)
        {
            var snapshot = new ConfigNode("VESSEL");
            foreach (var p in parts)
            {
                var part = snapshot.AddNode("PART");
                part.AddValue("name", p.partName);
                part.AddValue("pid", p.pid.ToString());
                if (p.crew != null)
                    for (int i = 0; i < p.crew.Length; i++)
                        part.AddValue("crew", p.crew[i]);
            }
            return snapshot;
        }

        // ────────────────────────────────────────────────────────
        // ResolveOrphanSeatFromSnapshots — base cases
        // ────────────────────────────────────────────────────────

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_OriginalInSinglePartSnapshot_ReturnsMatch()
        {
            var snapshot = BuildSnapshotWithCrew(
                100000u, "mk1-3pod.v2",
                "Jebediah Kerman", "Bill Kerman", "Bob Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { snapshot }, null);

            Assert.True(seat.Found);
            Assert.Equal(100000u, seat.PartPid);
            Assert.Equal("mk1-3pod.v2", seat.PartName);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_OriginalNotInSnapshot_ReturnsNotFound()
        {
            var snapshot = BuildSnapshotWithCrew(
                100000u, "mk1-3pod.v2",
                "Jebediah Kerman", "Bill Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { snapshot }, null);

            Assert.False(seat.Found);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_NullOriginal_ReturnsNotFound()
        {
            var snapshot = BuildSnapshotWithCrew(100000u, "mk1pod", "Jeb");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                null, new[] { snapshot }, null);

            Assert.False(seat.Found);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_EmptyOriginal_ReturnsNotFound()
        {
            var snapshot = BuildSnapshotWithCrew(100000u, "mk1pod", "Jeb");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "", new[] { snapshot }, null);

            Assert.False(seat.Found);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_NullSnapshotEnumerable_ReturnsNotFound()
        {
            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", null, null);

            Assert.False(seat.Found);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_NullSnapshotInList_SkippedNotCrash()
        {
            var snapshot = BuildSnapshotWithCrew(100000u, "mk1-3pod.v2", "Bob Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new ConfigNode[] { null, snapshot, null }, null);

            Assert.True(seat.Found);
            Assert.Equal(100000u, seat.PartPid);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_PartWithNoCrewValues_SkippedAndContinues()
        {
            var snapshot = new ConfigNode("VESSEL");
            // Part with no crew values at all (e.g., a fuel tank).
            var fuelTank = snapshot.AddNode("PART");
            fuelTank.AddValue("name", "fuelTank");
            fuelTank.AddValue("pid", "100001");
            // Part with the kerbal we're looking for.
            var pod = snapshot.AddNode("PART");
            pod.AddValue("name", "mk1pod");
            pod.AddValue("pid", "100002");
            pod.AddValue("crew", "Bob Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { snapshot }, null);

            Assert.True(seat.Found);
            Assert.Equal(100002u, seat.PartPid);
            Assert.Equal("mk1pod", seat.PartName);
        }

        // ────────────────────────────────────────────────────────
        // Multi-part / multi-snapshot cases
        // ────────────────────────────────────────────────────────

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_MultiPartSnapshot_ReturnsCorrectPartContainingOriginal()
        {
            // Vessel has two pods: a Mk1 (Jeb) and a Hitchhiker (Bob, Bill)
            var snapshot = BuildMultiPartSnapshot(
                (100000u, "mk1pod", new[] { "Jebediah Kerman" }),
                (100001u, "crewCabin", new[] { "Bob Kerman", "Bill Kerman" })
            );

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { snapshot }, null);

            Assert.True(seat.Found);
            Assert.Equal(100001u, seat.PartPid);
            Assert.Equal("crewCabin", seat.PartName);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_MultipleSnapshots_FirstSnapshotWins()
        {
            // Two recordings; both have Bob in different parts. First one wins.
            var first = BuildSnapshotWithCrew(100000u, "mk1pod", "Bob Kerman");
            var second = BuildSnapshotWithCrew(200000u, "mk1-3pod.v2", "Bob Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { first, second }, null);

            Assert.True(seat.Found);
            Assert.Equal(100000u, seat.PartPid);
            Assert.Equal("mk1pod", seat.PartName);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_OnlySecondSnapshotContainsOriginal_FindsIt()
        {
            var first = BuildSnapshotWithCrew(100000u, "mk1pod", "Jebediah Kerman");
            var second = BuildSnapshotWithCrew(200000u, "mk1-3pod.v2",
                "Jebediah Kerman", "Bill Kerman", "Bob Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { first, second }, null);

            Assert.True(seat.Found);
            Assert.Equal(200000u, seat.PartPid);
            Assert.Equal("mk1-3pod.v2", seat.PartName);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_PartWithoutPidValue_ReturnsZeroPid()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("name", "mk1pod");
            // No 'pid' value
            part.AddValue("crew", "Bob Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { snapshot }, null);

            Assert.True(seat.Found);
            Assert.Equal(0u, seat.PartPid);
            Assert.Equal("mk1pod", seat.PartName);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_PartWithoutNameValue_ReturnsEmptyName()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            // No 'name' value
            part.AddValue("pid", "100000");
            part.AddValue("crew", "Bob Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { snapshot }, null);

            Assert.True(seat.Found);
            Assert.Equal(100000u, seat.PartPid);
            Assert.Equal("", seat.PartName);
        }

        // ────────────────────────────────────────────────────────
        // Reverse-stand-in mapping (for snapshots from later recordings
        // whose crew lists already contain stand-in names from earlier ones)
        // ────────────────────────────────────────────────────────

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_ReverseMapsStandinToOriginal_FindsMatch()
        {
            // The snapshot contains 'Zelsted Kerman' (the stand-in) instead of
            // 'Jebediah Kerman' (the original) because an earlier recording
            // committed and replaced Jeb with Zelsted in subsequent snapshots.
            var snapshot = BuildSnapshotWithCrew(
                100000u, "mk1-3pod.v2",
                "Zelsted Kerman", "Bill Kerman", "Bob Kerman");

            var reverseMap = new Dictionary<string, string>
            {
                { "Jebediah Kerman", "Zelsted Kerman" }
            };

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Jebediah Kerman", new[] { snapshot }, reverseMap);

            Assert.True(seat.Found);
            Assert.Equal(100000u, seat.PartPid);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_ReverseMapMissingEntry_DoesNotFalselyMatch()
        {
            // Reverse map has Jeb→Zelsted, but we're looking for Bob.
            // Snapshot has Zelsted but no Bob. Must NOT return a match.
            var snapshot = BuildSnapshotWithCrew(100000u, "mk1-3pod.v2", "Zelsted Kerman");
            var reverseMap = new Dictionary<string, string>
            {
                { "Jebediah Kerman", "Zelsted Kerman" }
            };

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { snapshot }, reverseMap);

            Assert.False(seat.Found);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_NullReverseMap_DirectMatchStillWorks()
        {
            var snapshot = BuildSnapshotWithCrew(100000u, "mk1pod", "Bob Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { snapshot }, null);

            Assert.True(seat.Found);
        }

        // ────────────────────────────────────────────────────────
        // Regression: the exact 2026-04-09 playtest scenario
        // ────────────────────────────────────────────────────────

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_KerbalXBobOnEvaScenario_FindsMk1_3PodSeat()
        {
            // Kerbal X recording (id 8124d4445c) has GhostVisualSnapshot from
            // recording start with all 3 crew in the Mk1-3 pod. Bob EVA'd
            // mid-recording but the start-of-recording snapshot still lists him.
            var kerbalXStartSnapshot = BuildSnapshotWithCrew(
                100000u, "mk1-3pod.v2",
                "Jebediah Kerman", "Bill Kerman", "Bob Kerman");

            // No reverse-map needed — this is the first recording in the timeline,
            // its snapshot contains the raw original names.
            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bob Kerman", new[] { kerbalXStartSnapshot }, null);

            Assert.True(seat.Found);
            Assert.Equal(100000u, seat.PartPid);
            Assert.Equal("mk1-3pod.v2", seat.PartName);
        }

        // ────────────────────────────────────────────────────────
        // Spawned vessel guard — SwapReservedCrewInFlight must
        // skip both passes for Parsek-spawned vessels (#BugC)
        // ────────────────────────────────────────────────────────

        [Fact]
        public void BuildSpawnedVesselPidSet_MatchesSpawnedPid()
        {
            var rec = new Recording { SpawnedVesselPersistentId = 847060085 };
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var pids = CrewReservationManager.BuildSpawnedVesselPidSet(
                RecordingStore.CommittedRecordings);

            Assert.Contains(847060085u, pids);
        }

        [Fact]
        public void BuildSpawnedVesselPidSet_ZeroPid_NotIncluded()
        {
            var rec = new Recording { SpawnedVesselPersistentId = 0 };
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var pids = CrewReservationManager.BuildSpawnedVesselPidSet(
                RecordingStore.CommittedRecordings);

            Assert.DoesNotContain(0u, pids);
        }

        [Fact]
        public void BuildSpawnedVesselPidSet_NullRecordings_ReturnsEmpty()
        {
            var pids = CrewReservationManager.BuildSpawnedVesselPidSet(null);

            Assert.Empty(pids);
        }

        [Fact]
        public void BuildSpawnedVesselPidSet_DistinguishesSpawnedFromNonSpawned()
        {
            var rec = new Recording { SpawnedVesselPersistentId = 12345 };
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var pids = CrewReservationManager.BuildSpawnedVesselPidSet(
                RecordingStore.CommittedRecordings);

            Assert.True(pids.Contains(12345u),
                "Guard should detect pid=12345 as a Parsek-spawned vessel");
            Assert.False(pids.Contains(99999u),
                "Guard should NOT detect pid=99999 as spawned");
        }

        // --- ActiveVesselIsParsekSpawned - F2 whole-swap-skip guard, guid-aware ---

        private const string GuidA = "2b6e6a60d2c947489753371317fa067e";
        private const string GuidB = "a424011b746440baae6030e225c9de31";
        private const uint BakedPid = 2708531065u;

        [Fact]
        public void ActiveVesselIsParsekSpawned_AdoptionStampGuidMismatch_NotSpawned()
        {
            // Relaunch of the same craft: the prior adoption-stamped recording (SpawnedVesselPersistentId
            // == baked pid) shares the relaunch's pid but is a different launch -> not the spawned vessel.
            var recs = new List<Recording>
            {
                new Recording { VesselPersistentId = BakedPid, SpawnedVesselPersistentId = BakedPid, RecordedVesselGuid = GuidA }
            };
            Assert.False(CrewReservationManager.ActiveVesselIsParsekSpawned(recs, BakedPid, GuidB));
        }

        [Fact]
        public void ActiveVesselIsParsekSpawned_AdoptionStampGuidMatch_IsSpawned()
        {
            var recs = new List<Recording>
            {
                new Recording { VesselPersistentId = BakedPid, SpawnedVesselPersistentId = BakedPid, RecordedVesselGuid = GuidA }
            };
            Assert.True(CrewReservationManager.ActiveVesselIsParsekSpawned(recs, BakedPid, GuidA));
        }

        [Fact]
        public void ActiveVesselIsParsekSpawned_AdoptionStampGuidUnknown_FallsBackToPid()
        {
            var recs = new List<Recording>
            {
                new Recording { VesselPersistentId = BakedPid, SpawnedVesselPersistentId = BakedPid, RecordedVesselGuid = null }
            };
            Assert.True(CrewReservationManager.ActiveVesselIsParsekSpawned(recs, BakedPid, GuidB));
        }

        [Fact]
        public void ActiveVesselIsParsekSpawned_RealSpawnDistinctPid_StaysPidOnly()
        {
            // A genuine Parsek spawn uses a KSP-unique spawn pid distinct from the baked pid, so the
            // guid (fresh spawned-ghost guid != recorded launch guid) must NOT defeat the pid match.
            var recs = new List<Recording>
            {
                new Recording { VesselPersistentId = BakedPid, SpawnedVesselPersistentId = 555000u, RecordedVesselGuid = GuidA }
            };
            Assert.True(CrewReservationManager.ActiveVesselIsParsekSpawned(recs, 555000u, "ffffffffffffffffffffffffffffffff"));
        }

        [Fact]
        public void ActiveVesselIsParsekSpawned_NoMatchingPid_False()
        {
            var recs = new List<Recording>
            {
                new Recording { VesselPersistentId = BakedPid, SpawnedVesselPersistentId = BakedPid, RecordedVesselGuid = GuidA }
            };
            Assert.False(CrewReservationManager.ActiveVesselIsParsekSpawned(recs, 999u, GuidA));
            Assert.False(CrewReservationManager.ActiveVesselIsParsekSpawned(null, BakedPid, GuidA));
            Assert.False(CrewReservationManager.ActiveVesselIsParsekSpawned(recs, 0u, GuidA));
        }

        // ────────────────────────────────────────────────────────
        // Bug #413 — KSP ProtoPartSnapshot writes the part pid under
        // `persistentId`, not `pid`. The matcher must read the real
        // KSP field name; otherwise every orphan placement reports
        // `pid=0` and silently fails.
        // ────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a PART node with the real KSP field name (`persistentId`),
        /// matching how `ProtoPartSnapshot.Save` serializes the part pid in
        /// stock `.sfs` files (verified in `Fixtures/DefaultCareer/persistent.sfs`).
        /// </summary>
        private static ConfigNode BuildKspFormatSnapshotWithCrew(
            uint partPid, string partName, params string[] crewNames)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("name", partName);
            part.AddValue("persistentId", partPid.ToString());
            for (int i = 0; i < crewNames.Length; i++)
                part.AddValue("crew", crewNames[i]);
            return snapshot;
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_KspFormat_ReadsPersistentIdField()
        {
            // Real KSP format: PART.persistentId, not PART.pid.
            var snapshot = BuildKspFormatSnapshotWithCrew(
                3717834180u, "mk1pod.v2", "Bill Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bill Kerman", new[] { snapshot }, null);

            Assert.True(seat.Found, "seat lookup must succeed on KSP-format snapshots");
            Assert.Equal(3717834180u, seat.PartPid);
            Assert.Equal("mk1pod.v2", seat.PartName);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_KspFormat_BillReplacementScenario_ReproducesLogLine()
        {
            // Exact 2026-04-16 log-line scenario:
            //   [CrewReservation] Orphan placement: no matching part with free seat
            //   in active vessel for 'Bill Kerman' -> 'Gus Kerman'
            //   (snapshot pid=0 name='mk1pod.v2')
            // With the fix, the seat must now resolve to the real pid, not 0.
            var snapshot = BuildKspFormatSnapshotWithCrew(
                3717834180u, "mk1pod.v2",
                "Jebediah Kerman", "Bill Kerman", "Bob Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bill Kerman", new[] { snapshot }, null);

            Assert.True(seat.Found);
            Assert.NotEqual(0u, seat.PartPid);
            Assert.Equal(3717834180u, seat.PartPid);
            Assert.Equal("mk1pod.v2", seat.PartName);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_PrefersPersistentIdWhenBothPresent()
        {
            // Defensive: if a snapshot has both keys (theoretical — not
            // something KSP does, but cheap to lock in), `persistentId` wins
            // because that's the real runtime field.
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("name", "mk1pod.v2");
            part.AddValue("persistentId", "7777");
            part.AddValue("pid", "1111"); // legacy/test-only writer
            part.AddValue("crew", "Bill Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bill Kerman", new[] { snapshot }, null);

            Assert.True(seat.Found);
            Assert.Equal(7777u, seat.PartPid);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_LegacyPidFieldStillWorksAsFallback()
        {
            // Keeps the existing test-authored snapshots (and any pre-fix
            // recordings that happened to write `pid`) working. This exercises
            // the fallback branch in the capture code.
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("name", "mk1pod.v2");
            // Only the legacy key — no `persistentId`.
            part.AddValue("pid", "12345");
            part.AddValue("crew", "Bill Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bill Kerman", new[] { snapshot }, null);

            Assert.True(seat.Found);
            Assert.Equal(12345u, seat.PartPid);
            Assert.Equal("mk1pod.v2", seat.PartName);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_KspFormat_MultiPartVessel()
        {
            // A Mk1-3 pod (root) + a fuel tank + a passenger cabin, all saved
            // using KSP's `persistentId` key. The matcher must return the part
            // where the kerbal is actually seated, not the first part.
            var snapshot = new ConfigNode("VESSEL");
            var pod = snapshot.AddNode("PART");
            pod.AddValue("name", "mk1-3pod.v2");
            pod.AddValue("persistentId", "100000");
            pod.AddValue("crew", "Jebediah Kerman");
            var tank = snapshot.AddNode("PART");
            tank.AddValue("name", "fuelTank");
            tank.AddValue("persistentId", "100001");
            var cabin = snapshot.AddNode("PART");
            cabin.AddValue("name", "crewCabin");
            cabin.AddValue("persistentId", "100002");
            cabin.AddValue("crew", "Bob Kerman");
            cabin.AddValue("crew", "Bill Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bill Kerman", new[] { snapshot }, null);

            Assert.True(seat.Found);
            Assert.Equal(100002u, seat.PartPid);
            Assert.Equal("crewCabin", seat.PartName);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_MissingBothFields_ReturnsZeroPidButStillFound()
        {
            // Neither `persistentId` nor `pid` in the snapshot. Match should
            // still fire (the crew name is the key) but PartPid=0 must surface
            // so the FindTargetPartForOrphan name-fallback tier kicks in.
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("name", "mk1pod.v2");
            part.AddValue("crew", "Bill Kerman");

            var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                "Bill Kerman", new[] { snapshot }, null);

            Assert.True(seat.Found);
            Assert.Equal(0u, seat.PartPid);
            Assert.Equal("mk1pod.v2", seat.PartName);
        }

        [Fact]
        public void ResolveOrphanSeatFromSnapshots_KspFormat_InvariantCultureParsing()
        {
            // Defensive: `persistentId` values in stock saves are decimal
            // uints with no separators, but guard against a thread
            // CurrentCulture that tries to interpret digits as grouped.
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE"); // comma decimal
                var snapshot = BuildKspFormatSnapshotWithCrew(
                    4000000000u, "mk1pod.v2", "Bill Kerman");

                var seat = CrewReservationManager.ResolveOrphanSeatFromSnapshots(
                    "Bill Kerman", new[] { snapshot }, null);

                Assert.True(seat.Found);
                Assert.Equal(4000000000u, seat.PartPid);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        // ────────────────────────────────────────────────────────
        // ShouldSuppressOrphanPlacementForFreshRollout — fresh-launch gate.
        // Regression for the crew-auto-add bug: a fresh VAB/SPH launch whose
        // command pod reuses an old recording's part persistentId must NOT have
        // orphaned reserved crew's stand-ins placed onto it (Pass 2 suppressed).
        // ────────────────────────────────────────────────────────

        [Fact]
        public void ShouldSuppressOrphanPlacement_ActiveIsFreshRollout_Suppresses()
        {
            // Same pid as the captured fresh-rollout vessel → suppress orphan placement.
            Assert.True(CrewReservationManager.ShouldSuppressOrphanPlacementForFreshRollout(
                activeVesselPid: 57152010u, freshRolloutVesselPid: 57152010u));
        }

        [Fact]
        public void ShouldSuppressOrphanPlacement_ActivePidDiffersFromRollout_DoesNotSuppress()
        {
            // Mid-scene switch to a different (already-spawned) vessel: orphan
            // placement should still run for that continuation/merge target.
            Assert.False(CrewReservationManager.ShouldSuppressOrphanPlacementForFreshRollout(
                activeVesselPid: 2708531065u, freshRolloutVesselPid: 57152010u));
        }

        [Fact]
        public void ShouldSuppressOrphanPlacement_NoRolloutCaptured_DoesNotSuppress()
        {
            // freshRolloutVesselPid == 0 is the merge / chain-commit / resumed-save
            // call sites where orphan placement is the intended behaviour.
            Assert.False(CrewReservationManager.ShouldSuppressOrphanPlacementForFreshRollout(
                activeVesselPid: 57152010u, freshRolloutVesselPid: 0u));
        }

        [Fact]
        public void ShouldSuppressOrphanPlacement_ActivePidZero_DoesNotSuppress()
        {
            // Defensive: an unknown active pid never counts as a fresh rollout,
            // even if the captured rollout pid is also 0 (no false 0==0 match).
            Assert.False(CrewReservationManager.ShouldSuppressOrphanPlacementForFreshRollout(
                activeVesselPid: 0u, freshRolloutVesselPid: 0u));
            Assert.False(CrewReservationManager.ShouldSuppressOrphanPlacementForFreshRollout(
                activeVesselPid: 0u, freshRolloutVesselPid: 57152010u));
        }
    }
}
