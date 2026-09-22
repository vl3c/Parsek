- harness-seam-009: TestCommandEvaExitTests.Standoff_NoOtherLoadedVessel_ReadsClear (~line 343) missing from manifest; agent reviewed ok, not emitted. Check inventory_scan coverage before merge.
## Supervisor spot-check of High findings (2026-09-15, Fable, by reading test + SUT at 4aedb0a)
All 7 High T1 findings CONFIRMED on reading (each cell replays the production behaviour inline or asserts fixture state it set itself; no production call reaches the named line):
- F-recorder-events-003-01 PartEventTests.RemoveDuplicateCrew_RemovesDuplicate_LogsWarning (dedup loop replayed, VesselSpawner.cs:3262-3297 never called)
- F-recorder-events-013-01 BackgroundSplitTests.Bug285_* (4 cells mutate the tree by hand; HandleBackgroundVesselSplit never called)
- F-recorder-events-024-01 BackgroundPartEventAuditTests.PollPartEvents_CoversAllPolledEventTypes_MatchingFlightRecorder (reflection existence check only)
- F-recording-tree-050-04 / -07 CommittedRecordingImmutabilityTests (sets VesselDestroyed itself / asserts fixture snapshot non-null; destroy handler and CommitChainSegment never called)
- F-spawn-vessel-004-01 IdentityLossClassifierTests.ActiveRootBackgrounded_* (test calls AdoptControllersIfEmpty itself; FlightRecorder.cs:6983 backstop never reached)
- F-trajectory-orbit-015-01 RelativeRecordingTests.RecorderContract_LiveAnchorPositionMustMatchPlaybackAnchorPosition (pure-math identity over both branches; recorder anchor-pose seam FlightRecorder.cs:9334 never reached)
Phase 2: all 7 go to adversarial refutation + bounded mutation (T1 recipe: stub the named production line; GREEN = holds).
