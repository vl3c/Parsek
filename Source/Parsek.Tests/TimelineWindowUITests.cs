using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class TimelineWindowUITests : IDisposable
    {
        public TimelineWindowUITests()
        {
            ParsekScenario.ResetInstanceForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekTimeFormat.KerbinTimeOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekScenario.ResetInstanceForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekTimeFormat.ResetForTesting();
        }

        [Fact]
        public void GetRowActionButtonWidth_ShortTimelineActionsShareWidth_AndGoToStaysWider()
        {
            float watch = TimelineWindowUI.GetRowActionButtonWidth(TimelineWindowUI.TimelineRowActionButtonKind.Watch);
            float fastForward = TimelineWindowUI.GetRowActionButtonWidth(TimelineWindowUI.TimelineRowActionButtonKind.FastForward);
            float rewind = TimelineWindowUI.GetRowActionButtonWidth(TimelineWindowUI.TimelineRowActionButtonKind.Rewind);
            float loop = TimelineWindowUI.GetRowActionButtonWidth(TimelineWindowUI.TimelineRowActionButtonKind.Loop);
            float goTo = TimelineWindowUI.GetRowActionButtonWidth(TimelineWindowUI.TimelineRowActionButtonKind.GoTo);

            Assert.Equal(40f, watch);
            Assert.Equal(watch, fastForward);
            Assert.Equal(watch, rewind);
            Assert.Equal(watch, loop);
            Assert.Equal(48f, goTo);
            Assert.True(goTo > watch);
        }

        [Fact]
        public void TryConsumeCountdownTimeLabel_OnlyFirstVisibleFutureRow_ReturnsTrue()
        {
            bool countdownRowDrawn = false;

            Assert.False(TimelineWindowUI.TryConsumeCountdownTimeLabel(
                entryUT: 90, currentUT: 100, entryVisible: true, ref countdownRowDrawn));
            Assert.False(countdownRowDrawn);

            Assert.False(TimelineWindowUI.TryConsumeCountdownTimeLabel(
                entryUT: 100, currentUT: 100, entryVisible: true, ref countdownRowDrawn));
            Assert.False(countdownRowDrawn);

            Assert.True(TimelineWindowUI.TryConsumeCountdownTimeLabel(
                entryUT: 130, currentUT: 100, entryVisible: true, ref countdownRowDrawn));
            Assert.True(countdownRowDrawn);

            Assert.False(TimelineWindowUI.TryConsumeCountdownTimeLabel(
                entryUT: 160, currentUT: 100, entryVisible: true, ref countdownRowDrawn));
            Assert.True(countdownRowDrawn);
        }

        [Fact]
        public void TryConsumeCountdownTimeLabel_HiddenFutureRow_DoesNotConsumeLatch()
        {
            bool countdownRowDrawn = false;

            Assert.False(TimelineWindowUI.TryConsumeCountdownTimeLabel(
                entryUT: 130, currentUT: 100, entryVisible: false, ref countdownRowDrawn));
            Assert.False(countdownRowDrawn);

            Assert.True(TimelineWindowUI.TryConsumeCountdownTimeLabel(
                entryUT: 160, currentUT: 100, entryVisible: true, ref countdownRowDrawn));
            Assert.True(countdownRowDrawn);
        }

        [Fact]
        public void FormatTimelineEntryTimeLabel_CountdownRow_UsesTMinusCountdown()
        {
            string label = TimelineWindowUI.FormatTimelineEntryTimeLabel(
                entryUT: 430, currentUT: 100, showCountdownTime: true);

            Assert.Equal("T-5m 30s", label);
        }

        [Fact]
        public void ShouldShowLoopToggle_FutureRecording_ReturnsFalse()
        {
            var rec = new Recording { LaunchSiteName = "LaunchPad" };

            Assert.False(TimelineWindowUI.ShouldShowLoopToggle(rec, isFuture: true));
        }

        [Fact]
        public void ShouldShowLoopToggle_PastLoopableRecording_ReturnsTrue()
        {
            var rec = new Recording { SegmentPhase = "atmo" };

            Assert.True(TimelineWindowUI.ShouldShowLoopToggle(rec, isFuture: false));
        }

        [Fact]
        public void ShouldShowLoopToggle_PastActiveLoopOutsideHeuristic_ReturnsTrue()
        {
            var rec = new Recording { LoopPlayback = true };

            Assert.True(TimelineWindowUI.ShouldShowLoopToggle(rec, isFuture: false));
        }

        [Fact]
        public void ShouldShowLoopToggle_PastNonLoopableInactiveRecording_ReturnsFalse()
        {
            var rec = new Recording();

            Assert.False(TimelineWindowUI.ShouldShowLoopToggle(rec, isFuture: false));
        }

        [Fact]
        public void ShouldShowLoopToggle_PastLoopableUnfinishedFlight_ReturnsFalse()
        {
            var rec = new Recording { SegmentPhase = "atmo" };

            Assert.False(TimelineWindowUI.ShouldShowLoopToggle(
                rec, isFuture: false, isUnfinishedFlight: true));
        }

        [Fact]
        public void ShouldShowLoopToggle_PastActiveLoopUnfinishedFlight_ReturnsFalse()
        {
            var rec = new Recording { LoopPlayback = true };

            Assert.False(TimelineWindowUI.ShouldShowLoopToggle(
                rec, isFuture: false, isUnfinishedFlight: true));
        }

        [Fact]
        public void ShouldShowFastForwardButton_FutureRecording_ReturnsTrue()
        {
            var rec = new Recording();

            Assert.True(TimelineWindowUI.ShouldShowFastForwardButton(rec, isFuture: true));
        }

        [Fact]
        public void ShouldShowRewindButton_PastRecordingWithSave_ReturnsTrue()
        {
            var rec = new Recording { RewindSaveFileName = "rewind.sfs" };

            Assert.True(TimelineWindowUI.ShouldShowRewindButton(rec, isFuture: false));
        }

        [Fact]
        public void ShouldShowRewindButton_ActiveParentUnfinishedFlightWithLaunchSave_ReturnsTrue()
        {
            const string branchPointId = "bp-breakup";
            var rec = new Recording
            {
                RecordingId = "rec-active-parent",
                VesselName = "Kerbal X",
                // Open crashed Unfinished Flight -> CommittedProvisional tip.
                MergeState = MergeState.CommittedProvisional,
                TerminalStateValue = TerminalState.Destroyed,
                ChildBranchPointId = branchPointId,
                RewindSaveFileName = "rewind.sfs"
            };
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint>
                {
                    new RewindPoint
                    {
                        RewindPointId = "rp-breakup",
                        BranchPointId = branchPointId,
                        SessionProvisional = false,
                        ChildSlots = new List<ChildSlot>
                        {
                            new ChildSlot
                            {
                                SlotIndex = 0,
                                OriginChildRecordingId = rec.RecordingId,
                                Controllable = true
                            }
                        }
                    }
                }
            };
            ParsekScenario.SetInstanceForTesting(scenario);

            Assert.True(EffectiveState.IsUnfinishedFlight(rec));
            Assert.True(TimelineWindowUI.ShouldShowRewindButton(rec, isFuture: false));
        }

        [Fact]
        public void ShouldShowWatchButton_InFlightRecording_ReturnsTrue()
        {
            Assert.True(TimelineWindowUI.ShouldShowWatchButton(
                inFlightMode: true, rec: new Recording()));
        }

        [Fact]
        public void ShouldShowWatchButton_NonFlightRecording_ReturnsFalse()
        {
            Assert.False(TimelineWindowUI.ShouldShowWatchButton(
                inFlightMode: false, rec: new Recording()));
        }

        [Fact]
        public void BuildWatchButtonDescriptor_EligibleRow_IsEnabledForEnter()
        {
            var descriptor = TimelineWindowUI.BuildWatchButtonDescriptor(
                isWatching: false, hasGhost: true, sameBody: true, inRange: true, isDebris: false);

            Assert.Equal("W", descriptor.Label);
            Assert.Equal("Follow ghost in watch mode", descriptor.Tooltip);
            Assert.True(descriptor.Enabled);
            Assert.True(descriptor.CanWatch);
            Assert.Equal(TimelineWindowUI.TimelineWatchButtonAction.Enter, descriptor.Action);
        }

        [Fact]
        public void BuildFlySealButtonDescriptor_ResolvedSlot_EnablesBothActions()
        {
            var descriptor = TimelineWindowUI.BuildFlySealButtonDescriptor(
                resolvable: true, canInvoke: true, unavailableReason: null);

            Assert.True(descriptor.FlyEnabled);
            Assert.Equal("Re-fly from the separation moment.", descriptor.FlyTooltip);
            Assert.True(descriptor.SealEnabled);
            Assert.Equal("Close this slot without changing the recording.", descriptor.SealTooltip);
        }

        [Fact]
        public void BuildFlySealButtonDescriptor_ResolvedButNotInvokable_OnlyDisablesFly()
        {
            var descriptor = TimelineWindowUI.BuildFlySealButtonDescriptor(
                resolvable: true, canInvoke: false, unavailableReason: "Rewind point save missing.");

            Assert.False(descriptor.FlyEnabled);
            Assert.Equal("Rewind point save missing.", descriptor.FlyTooltip);
            Assert.True(descriptor.SealEnabled);
            Assert.Equal("Close this slot without changing the recording.", descriptor.SealTooltip);
        }

        [Fact]
        public void BuildFlySealButtonDescriptor_UnresolvedSlot_DisablesBothActions()
        {
            var descriptor = TimelineWindowUI.BuildFlySealButtonDescriptor(
                resolvable: false, canInvoke: false, unavailableReason: "Rewind point slot not found.");

            Assert.False(descriptor.FlyEnabled);
            Assert.Equal("Rewind point slot not found.", descriptor.FlyTooltip);
            Assert.False(descriptor.SealEnabled);
            Assert.Equal("Rewind point slot not found.", descriptor.SealTooltip);
        }

        [Fact]
        public void BuildFlySealButtonDescriptor_UnresolvedWithoutReason_UsesGenericUnavailableCopy()
        {
            var descriptor = TimelineWindowUI.BuildFlySealButtonDescriptor(
                resolvable: false, canInvoke: false, unavailableReason: null);

            Assert.False(descriptor.FlyEnabled);
            Assert.Equal("Action unavailable.", descriptor.FlyTooltip);
            Assert.False(descriptor.SealEnabled);
            Assert.Equal("Action unavailable.", descriptor.SealTooltip);
        }

        [Fact]
        public void BuildRecordingIndexLookup_MapsRecordingIdsToIndices()
        {
            var lookup = TimelineWindowUI.BuildRecordingIndexLookup(new[]
            {
                new Recording { RecordingId = "rec-a" },
                new Recording { RecordingId = string.Empty },
                new Recording { RecordingId = "rec-b" }
            });

            Assert.Equal(0, lookup["rec-a"]);
            Assert.Equal(2, lookup["rec-b"]);
            Assert.False(lookup.ContainsKey(string.Empty));
        }

        [Fact]
        public void BuildRecordingIndexLookup_NullRecordings_ReturnsEmpty()
        {
            var lookup = TimelineWindowUI.BuildRecordingIndexLookup(null);

            Assert.Empty(lookup);
        }

        // Regression for the May-2026 "missing W button" bug: TimelineWindowUI
        // used to feed an ERS-scoped index into ghost-engine APIs that key on
        // the RAW CommittedRecordings list. ERS skips superseded entries, so
        // the same recording can sit at different positions in the two lists.
        // The fix maintains a second `committedIndexById` lookup off the raw
        // committed list; this test documents the invariant the two lookups
        // are built to preserve.
        [Fact]
        public void BuildRecordingIndexLookup_ErsAndCommittedIndicesDivergeAfterSupersede()
        {
            // ERS-style list — superseded rec-b is filtered out.
            var ersList = new[]
            {
                new Recording { RecordingId = "rec-a" }, // ERS 0 / Committed 0
                new Recording { RecordingId = "rec-c" }  // ERS 1 / Committed 2
            };

            // Raw CommittedRecordings list — rec-b sits between rec-a and rec-c.
            var committedList = new[]
            {
                new Recording { RecordingId = "rec-a" }, // Committed 0
                new Recording { RecordingId = "rec-b" }, // Committed 1 (superseded; ERS-hidden)
                new Recording { RecordingId = "rec-c" }  // Committed 2
            };

            var ersLookup = TimelineWindowUI.BuildRecordingIndexLookup(ersList);
            var committedLookup = TimelineWindowUI.BuildRecordingIndexLookup(committedList);

            // rec-a sits at index 0 in both lists — the no-supersede happy path.
            Assert.Equal(0, ersLookup["rec-a"]);
            Assert.Equal(0, committedLookup["rec-a"]);

            // rec-c is at ERS index 1 but Committed index 2 — the bug scenario.
            // Passing ersLookup["rec-c"] to ghostStates[] would hit rec-b's slot.
            Assert.Equal(1, ersLookup["rec-c"]);
            Assert.Equal(2, committedLookup["rec-c"]);
            Assert.NotEqual(ersLookup["rec-c"], committedLookup["rec-c"]);

            // rec-b only exists in the raw committed list (ERS filters it out).
            Assert.False(ersLookup.ContainsKey("rec-b"));
            Assert.Equal(1, committedLookup["rec-b"]);
        }

        // Pins the W-button code path's resolver contract: given a CommittedRecordings-
        // shaped lookup, FindCommittedRecordingIndexById returns the committed-list
        // index for every recording, INCLUDING ones that would be filtered out of ERS.
        // A future refactor that swapped this back to an ERS-shaped lookup would still
        // pass BuildRecordingIndexLookup_*; this test catches the actual regression.
        [Fact]
        public void FindCommittedRecordingIndexById_ReturnsCommittedIndex_IncludingForErsFilteredRecordings()
        {
            var committedList = new[]
            {
                new Recording { RecordingId = "rec-a" }, // Committed 0
                new Recording { RecordingId = "rec-b" }, // Committed 1 (would be ERS-filtered)
                new Recording { RecordingId = "rec-c" }  // Committed 2
            };
            var committedLookup = TimelineWindowUI.BuildRecordingIndexLookup(committedList);

            Assert.Equal(0, TimelineWindowUI.FindCommittedRecordingIndexById(committedLookup, "rec-a"));
            Assert.Equal(1, TimelineWindowUI.FindCommittedRecordingIndexById(committedLookup, "rec-b"));
            Assert.Equal(2, TimelineWindowUI.FindCommittedRecordingIndexById(committedLookup, "rec-c"));
            Assert.Equal(-1, TimelineWindowUI.FindCommittedRecordingIndexById(committedLookup, "missing"));
            Assert.Equal(-1, TimelineWindowUI.FindCommittedRecordingIndexById(null, "rec-a"));
            Assert.Equal(-1, TimelineWindowUI.FindCommittedRecordingIndexById(committedLookup, null));
            Assert.Equal(-1, TimelineWindowUI.FindCommittedRecordingIndexById(committedLookup, string.Empty));
        }

        [Fact]
        public void BuildWatchButtonDescriptor_WatchedUnavailableRow_UsesExitTooltipAndStaysEnabled()
        {
            var descriptor = TimelineWindowUI.BuildWatchButtonDescriptor(
                isWatching: true, hasGhost: false, sameBody: false, inRange: false, isDebris: false);

            Assert.Equal("W*", descriptor.Label);
            Assert.Equal("Exit watch mode", descriptor.Tooltip);
            Assert.True(descriptor.Enabled);
            Assert.False(descriptor.CanWatch);
            Assert.Equal(TimelineWindowUI.TimelineWatchButtonAction.Exit, descriptor.Action);
        }

        [Fact]
        public void GetWatchButtonAction_TogglesWatchedRowsOff()
        {
            Assert.Equal(TimelineWindowUI.TimelineWatchButtonAction.Enter,
                TimelineWindowUI.GetWatchButtonAction(isWatching: false));
            Assert.Equal(TimelineWindowUI.TimelineWatchButtonAction.Exit,
                TimelineWindowUI.GetWatchButtonAction(isWatching: true));
        }

        [Fact]
        public void ApplyWatchButtonAction_ExitAction_CallsExitOnly()
        {
            bool exitCalled = false;
            int enteredIndex = -1;

            TimelineWindowUI.ApplyWatchButtonAction(
                TimelineWindowUI.TimelineWatchButtonAction.Exit,
                recIndex: 7,
                exitWatchMode: () => exitCalled = true,
                enterWatchMode: index => enteredIndex = index);

            Assert.True(exitCalled);
            Assert.Equal(-1, enteredIndex);
        }

        [Fact]
        public void ApplyWatchButtonAction_EnterAction_CallsEnterWithRecordingIndex()
        {
            bool exitCalled = false;
            int enteredIndex = -1;

            TimelineWindowUI.ApplyWatchButtonAction(
                TimelineWindowUI.TimelineWatchButtonAction.Enter,
                recIndex: 7,
                exitWatchMode: () => exitCalled = true,
                enterWatchMode: index => enteredIndex = index);

            Assert.False(exitCalled);
            Assert.Equal(7, enteredIndex);
        }

        [Fact]
        public void HasActionableRewindOrFastForwardButton_FutureRecordingStart_ReturnsTrue()
        {
            var entry = new TimelineEntry
            {
                Type = TimelineEntryType.RecordingStart,
                UT = 200,
                RecordingId = "rec-1"
            };
            var rec = new Recording { RecordingId = "rec-1" };

            Assert.True(TimelineWindowUI.HasActionableRewindOrFastForwardButton(
                entry, rec, currentUT: 100, canFastForward: true, canRewind: false));
        }

        [Fact]
        public void HasActionableRewindOrFastForwardButton_FutureRecordingStartWithoutAvailableFastForward_ReturnsFalse()
        {
            var entry = new TimelineEntry
            {
                Type = TimelineEntryType.RecordingStart,
                UT = 200,
                RecordingId = "rec-1"
            };
            var rec = new Recording { RecordingId = "rec-1" };

            Assert.False(TimelineWindowUI.HasActionableRewindOrFastForwardButton(
                entry, rec, currentUT: 100, canFastForward: false, canRewind: false));
        }

        [Fact]
        public void HasActionableRewindOrFastForwardButton_PastRecordingWithoutRewindSave_ReturnsFalse()
        {
            var entry = new TimelineEntry
            {
                Type = TimelineEntryType.RecordingStart,
                UT = 100,
                RecordingId = "rec-1"
            };
            var rec = new Recording { RecordingId = "rec-1" };

            Assert.False(TimelineWindowUI.HasActionableRewindOrFastForwardButton(
                entry, rec, currentUT: 200, canFastForward: false, canRewind: true));
        }

        [Fact]
        public void HasActionableRewindOrFastForwardButton_PastRecordingWithDisabledRewind_ReturnsFalse()
        {
            var entry = new TimelineEntry
            {
                Type = TimelineEntryType.RecordingStart,
                UT = 100,
                RecordingId = "rec-1"
            };
            var rec = new Recording
            {
                RecordingId = "rec-1",
                RewindSaveFileName = "rewind.sfs"
            };

            Assert.False(TimelineWindowUI.HasActionableRewindOrFastForwardButton(
                entry, rec, currentUT: 200, canFastForward: false, canRewind: false));
        }

        [Fact]
        public void HasActionableFlyOrSealButton_UnfinishedFlightSeparationResolvedSlot_ReturnsTrue()
        {
            var entry = new TimelineEntry
            {
                Type = TimelineEntryType.UnfinishedFlightSeparation,
                UT = 100,
                RecordingId = "rec-1"
            };
            var rec = new Recording { RecordingId = "rec-1" };
            var rp = new RewindPoint
            {
                ChildSlots = new System.Collections.Generic.List<ChildSlot>
                {
                    new ChildSlot()
                }
            };

            Assert.True(TimelineWindowUI.HasActionableFlyOrSealButton(
                entry,
                rec,
                RecordingsTableUI.UnfinishedFlightRewindRoute.Resolved,
                rp,
                slotListIndex: 0));
        }

        [Fact]
        public void HasActionableFlyOrSealButton_UnresolvedSlot_ReturnsFalse()
        {
            var entry = new TimelineEntry
            {
                Type = TimelineEntryType.UnfinishedFlightSeparation,
                UT = 100,
                RecordingId = "rec-1"
            };
            var rec = new Recording { RecordingId = "rec-1" };

            Assert.False(TimelineWindowUI.HasActionableFlyOrSealButton(
                entry,
                rec,
                RecordingsTableUI.UnfinishedFlightRewindRoute.MissingSlot,
                rp: null,
                slotListIndex: -1));
        }

        [Fact]
        public void HasActionableFlyOrSealButton_RecordingStart_ReturnsFalse()
        {
            var entry = new TimelineEntry
            {
                Type = TimelineEntryType.RecordingStart,
                UT = 100,
                RecordingId = "rec-1"
            };
            var rec = new Recording { RecordingId = "rec-1" };
            var rp = new RewindPoint
            {
                ChildSlots = new System.Collections.Generic.List<ChildSlot>
                {
                    new ChildSlot()
                }
            };

            Assert.False(TimelineWindowUI.HasActionableFlyOrSealButton(
                entry,
                rec,
                RecordingsTableUI.UnfinishedFlightRewindRoute.Resolved,
                rp,
                slotListIndex: 0));
        }
    }
}
