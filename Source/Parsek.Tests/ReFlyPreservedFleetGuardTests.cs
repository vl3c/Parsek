using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Two guards that only became necessary once the Re-Fly load stopped emptying the world
    /// (#16 items 6 and 7). Both used to be carried, silently, by "the loaded world contains
    /// exactly the selected slot's vessel and nothing else".
    ///
    /// <para>
    /// Item 6 - readiness: <c>FlightGlobals.Vessels.Count &gt; 0</c> was a proxy for "the
    /// selected slot's vessel is loaded". The preserved population makes the list non-empty
    /// before that vessel exists, so Strip runs early and the invocation bails with "selected
    /// vessel not present on reload". <c>SelectedSlotVesselIsPresent</c> is the pure half of the
    /// replacement.
    /// </para>
    /// <para>
    /// Item 7 - the partially-populated RP: a child slot the author could not correlate to a
    /// live vessel is marked <c>Disabled</c> with <c>DisabledReason="no-live-vessel"</c> and gets
    /// NO <c>PidSlotMap</c> entry, so <see cref="PostLoadStripper"/> classifies its craft as
    /// unrelated and (correctly, by its own rules) leaves it. The old strict strip removed it as
    /// collateral; now it stays as a real vessel while its recording also replays as a ghost -
    /// the #587 third-facet shape. <c>ResolveDisabledSlotVesselsToStrip</c> restores the
    /// every-non-selected-slot invariant for disabled slots, matching on launch identity (never
    /// on name, which would delete a player's unrelated same-named craft).
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public sealed class ReFlyPreservedFleetGuardTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorSuppressLogging;

        public ReFlyPreservedFleetGuardTests()
        {
            priorSuppressLogging = ParsekLog.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorSuppressLogging;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        // ------------------------------------------------------------------
        // Item 6: readiness is about the SELECTED SLOT, not about list size.
        // ------------------------------------------------------------------

        private static RewindInvoker.ReFlySlotPidSets SlotPids(
            uint[] selectedVesselPids, uint[] selectedRootPartPids)
        {
            return new RewindInvoker.ReFlySlotPidSets
            {
                SelectedVesselPids = new HashSet<uint>(selectedVesselPids ?? new uint[0]),
                SelectedRootPartPids = new HashSet<uint>(selectedRootPartPids ?? new uint[0]),
                OtherSlotVesselPids = new HashSet<uint>(),
                OtherSlotRootPartPids = new HashSet<uint>(),
            };
        }

        [Fact]
        public void SelectedSlotVesselIsPresent_MatchesOnVesselPid()
        {
            var pids = SlotPids(new uint[] { 9u }, new uint[0]);

            Assert.True(RewindInvoker.SelectedSlotVesselIsPresent(9u, 0u, pids));
            Assert.False(RewindInvoker.SelectedSlotVesselIsPresent(2708531065u, 0u, pids));
        }

        [Fact]
        public void SelectedSlotVesselIsPresent_MatchesOnRootPartPidWhenTheVesselPidWasRegenerated()
        {
            // KSP can regenerate a vessel pid on load; the strip resolves the selected vessel
            // through RootPartPidMap in that case, so readiness must accept the same key or the
            // deferral runs its full 300-frame budget for a slot that IS present.
            var pids = SlotPids(new uint[] { 9u }, new uint[] { 4242u });

            Assert.True(RewindInvoker.SelectedSlotVesselIsPresent(777u, 4242u, pids));
        }

        [Fact]
        public void SelectedSlotVesselIsPresent_ZeroPidsNeverMatch()
        {
            var pids = SlotPids(new uint[] { 0u }, new uint[] { 0u });

            Assert.False(RewindInvoker.SelectedSlotVesselIsPresent(0u, 0u, pids));
        }

        [Fact]
        public void SelectedSlotVesselIsPresent_PreservedUnrelatedVesselIsNotTheSelectedSlot()
        {
            // The exact shape item 6 exists for: the preserved fleet is live, the slot is not.
            var pids = SlotPids(new uint[] { 9u }, new uint[] { 4242u });

            Assert.False(RewindInvoker.SelectedSlotVesselIsPresent(3130558916u, 555u, pids));
            Assert.False(RewindInvoker.SelectedSlotVesselIsPresent(2708531065u, 556u, pids));
        }

        [Fact]
        public void SelectedSlotIsUnmapped_IsTrueOnlyWhenNeitherKeyMapsAnything()
        {
            Assert.True(SlotPids(new uint[0], new uint[0]).SelectedSlotIsUnmapped);
            Assert.False(SlotPids(new uint[] { 9u }, new uint[0]).SelectedSlotIsUnmapped);
            Assert.False(SlotPids(new uint[0], new uint[] { 4242u }).SelectedSlotIsUnmapped);
        }

        // ------------------------------------------------------------------
        // Item 7: the disabled slot's vessel.
        // ------------------------------------------------------------------

        private const string DisabledSlotRecId = "rec-disabled-sibling";
        private const string SelectedSlotRecId = "rec-selected";
        private const uint DisabledSlotCraftPid = 2708531065u;
        private const string DisabledSlotLaunchGuid = "11111111-1111-1111-1111-111111111111";

        private static RewindPoint MakeRewindPoint(bool disabledSiblingSlot = true)
        {
            var rp = new RewindPoint
            {
                RewindPointId = "rp-partial",
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = SelectedSlotRecId },
                    new ChildSlot
                    {
                        SlotIndex = 1,
                        OriginChildRecordingId = DisabledSlotRecId,
                        Disabled = disabledSiblingSlot,
                        DisabledReason = disabledSiblingSlot ? "no-live-vessel" : null,
                    },
                },
                // Only the selected slot is mapped - that is what "partially populated" means.
                PidSlotMap = new Dictionary<uint, int> { { 9u, 0 } },
                RootPartPidMap = new Dictionary<uint, int> { { 4242u, 0 } },
            };
            return rp;
        }

        private static List<Recording> MakeRecordings()
        {
            return new List<Recording>
            {
                new Recording
                {
                    RecordingId = SelectedSlotRecId,
                    VesselName = "WP Upper",
                    VesselPersistentId = 9u,
                },
                new Recording
                {
                    RecordingId = DisabledSlotRecId,
                    VesselName = "WP Booster A",
                    VesselPersistentId = DisabledSlotCraftPid,
                    RecordedVesselGuid = DisabledSlotLaunchGuid,
                },
            };
        }

        [Fact]
        public void ResolveDisabledSlotVesselsToStrip_DisabledSlotsOwnVessel_IsStripped()
        {
            var kill = RewindInvoker.ResolveDisabledSlotVesselsToStrip(
                MakeRewindPoint(), selectedSlotIndex: 0, MakeRecordings(), supersedes: null,
                liveVessels: new List<(uint pid, string guid)>
                {
                    (9u, null),                                        // selected slot
                    (DisabledSlotCraftPid, DisabledSlotLaunchGuid),     // the disabled slot's craft
                });

            Assert.Single(kill);
            Assert.Contains(DisabledSlotCraftPid, kill);
        }

        [Fact]
        public void ResolveDisabledSlotVesselsToStrip_SameCraftDifferentLaunch_IsNeverStripped()
        {
            // The whole point of matching on launch identity: the player's OTHER Booster A,
            // launched separately, shares the craft-baked pid and must survive the rewind.
            var kill = RewindInvoker.ResolveDisabledSlotVesselsToStrip(
                MakeRewindPoint(), selectedSlotIndex: 0, MakeRecordings(), supersedes: null,
                liveVessels: new List<(uint pid, string guid)>
                {
                    (DisabledSlotCraftPid, "22222222-2222-2222-2222-222222222222"),
                });

            Assert.Empty(kill);
        }

        [Fact]
        public void ResolveDisabledSlotVesselsToStrip_EnabledSlot_IsLeftToTheNormalStrip()
        {
            var kill = RewindInvoker.ResolveDisabledSlotVesselsToStrip(
                MakeRewindPoint(disabledSiblingSlot: false), selectedSlotIndex: 0,
                MakeRecordings(), supersedes: null,
                liveVessels: new List<(uint pid, string guid)>
                {
                    (DisabledSlotCraftPid, DisabledSlotLaunchGuid),
                });

            Assert.Empty(kill);
        }

        [Fact]
        public void ResolveDisabledSlotVesselsToStrip_MappedPid_IsLeftToTheNormalStrip()
        {
            // A disabled slot whose pid IS in the map (belt-and-braces: the author disabled it
            // for some other reason) is PostLoadStripper's business, not this pass's.
            var rp = MakeRewindPoint();
            rp.PidSlotMap[DisabledSlotCraftPid] = 1;

            var kill = RewindInvoker.ResolveDisabledSlotVesselsToStrip(
                rp, selectedSlotIndex: 0, MakeRecordings(), supersedes: null,
                liveVessels: new List<(uint pid, string guid)>
                {
                    (DisabledSlotCraftPid, DisabledSlotLaunchGuid),
                });

            Assert.Empty(kill);
        }

        [Fact]
        public void ResolveDisabledSlotVesselsToStrip_SelectedSlotIsNeverACandidate()
        {
            // Even if the selected slot is itself flagged Disabled, the vessel the player is
            // about to fly must never be a strip candidate.
            var rp = MakeRewindPoint();
            rp.ChildSlots[0].Disabled = true;
            rp.ChildSlots[0].DisabledReason = "no-live-vessel";
            rp.PidSlotMap.Clear();

            var kill = RewindInvoker.ResolveDisabledSlotVesselsToStrip(
                rp, selectedSlotIndex: 0, MakeRecordings(), supersedes: null,
                liveVessels: new List<(uint pid, string guid)> { (9u, null) });

            Assert.Empty(kill);
        }

        [Fact]
        public void ResolveDisabledSlotVesselsToStrip_UnresolvableOriginRecording_YieldsNothing()
        {
            var rp = MakeRewindPoint();
            rp.ChildSlots[1].OriginChildRecordingId = "rec-that-does-not-exist";

            var kill = RewindInvoker.ResolveDisabledSlotVesselsToStrip(
                rp, selectedSlotIndex: 0, MakeRecordings(), supersedes: null,
                liveVessels: new List<(uint pid, string guid)>
                {
                    (DisabledSlotCraftPid, DisabledSlotLaunchGuid),
                });

            Assert.Empty(kill);
        }

        [Fact]
        public void ResolveDisabledSlotVesselsToStrip_FollowsTheSupersedeWalkToTheEffectiveTip()
        {
            // A slot re-flown earlier resolves to the recording that actually stands, so the
            // vessel stripped is the effective tip's, not the retired origin's.
            const uint tipCraftPid = 3130558916u;
            const string tipGuid = "33333333-3333-3333-3333-333333333333";

            var recordings = MakeRecordings();
            recordings.Add(new Recording
            {
                RecordingId = "rec-disabled-sibling-fork",
                VesselName = "WP Booster A",
                VesselPersistentId = tipCraftPid,
                RecordedVesselGuid = tipGuid,
            });
            var supersedes = new List<RecordingSupersedeRelation>
            {
                new RecordingSupersedeRelation
                {
                    RelationId = "rsr_test",
                    OldRecordingId = DisabledSlotRecId,
                    NewRecordingId = "rec-disabled-sibling-fork",
                },
            };

            var kill = RewindInvoker.ResolveDisabledSlotVesselsToStrip(
                MakeRewindPoint(), selectedSlotIndex: 0, recordings, supersedes,
                liveVessels: new List<(uint pid, string guid)>
                {
                    (DisabledSlotCraftPid, DisabledSlotLaunchGuid), // retired origin's craft
                    (tipCraftPid, tipGuid),                          // the effective tip's craft
                });

            Assert.Single(kill);
            Assert.Contains(tipCraftPid, kill);
        }

        [Fact]
        public void ResolveDisabledSlotVesselsToStrip_NullInputs_YieldNothing()
        {
            Assert.Empty(RewindInvoker.ResolveDisabledSlotVesselsToStrip(
                null, 0, MakeRecordings(), null,
                new List<(uint pid, string guid)> { (DisabledSlotCraftPid, DisabledSlotLaunchGuid) }));
            Assert.Empty(RewindInvoker.ResolveDisabledSlotVesselsToStrip(
                MakeRewindPoint(), 0, null, null,
                new List<(uint pid, string guid)> { (DisabledSlotCraftPid, DisabledSlotLaunchGuid) }));
            Assert.Empty(RewindInvoker.ResolveDisabledSlotVesselsToStrip(
                MakeRewindPoint(), 0, MakeRecordings(), null, null));
        }
    }
}
