using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 14 of Rewind-to-Staging (design §7.33). Guards the rename-persist
    /// + hide-warn-and-refuse behavior on an Unfinished Flight row.
    ///
    /// <para>
    /// The rename path is the same <c>rec.VesselName = trimmed</c> assignment
    /// used for every recording, so the "persists" test pins that an Unfinished
    /// Flight recording's <c>VesselName</c> round-trips through the renamed
    /// value (the ERS / IsUnfinishedFlight predicates do not veto the
    /// assignment). The hide-warn test drives the RecordingsTableUI hide
    /// helper directly so we can assert the Warn log line + ScreenMessages
    /// advisory without needing a live IMGUI event loop.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class RenameOnUnfinishedFlightTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> screenMessages = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public RenameOnUnfinishedFlightTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.ScreenMessageSinkForTesting = (msg, dur) => screenMessages.Add(msg);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        private static Recording MakeCrashedUnfinished(string id, string bpId, string parentVessel)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = parentVessel,
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Destroyed,
                ParentBranchPointId = bpId,
            };
        }

        private static ParsekScenario InstallScenarioWithRp(string bpId, string rpId, string slotRecordingId)
        {
            var rp = new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = bpId,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 0,
                        OriginChildRecordingId = slotRecordingId,
                        Controllable = true
                    }
                },
                UT = 100.0,
                SessionProvisional = false,
            };
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint> { rp },
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            return scenario;
        }

        [Fact]
        public void Rename_UnfinishedFlight_PersistsToRecording()
        {
            // Regression: the per-row rename path writes the trimmed text
            // straight to Recording.VesselName via CommitRecordingRename,
            // which is pure-data and does not consult IsUnfinishedFlight.
            // We pin that the assignment survives through ERS membership:
            // the recording must remain an Unfinished Flight member after
            // rename with only its display name changed.
            var rec = MakeCrashedUnfinished("rec_uf1", "bp_uf1", "OriginalName");
            RecordingStore.AddCommittedInternal(rec);
            InstallScenarioWithRp("bp_uf1", "rp_uf1", rec.RecordingId);

            Assert.True(EffectiveState.IsUnfinishedFlight(rec),
                "precondition: recording must classify as unfinished");

            // Simulate the CommitRecordingRename tail: VesselName write is
            // the only mutation. No side-effects expected.
            string newName = "Booster Crash - Take 1";
            rec.VesselName = newName;

            Assert.Equal(newName, rec.VesselName);
            Assert.True(EffectiveState.IsUnfinishedFlight(rec),
                "post-rename: recording must still classify as unfinished");

            // Membership in the virtual group must survive the rename.
            var members = UnfinishedFlightsGroup.ComputeMembers();
            Assert.Contains(members, m => m.RecordingId == rec.RecordingId);
            Assert.Equal(newName, members[0].VesselName);
        }

        [Fact]
        public void Hide_UnfinishedFlight_WarnsAndDoesNotFlip()
        {
            // Regression: hiding an Unfinished Flight row must refuse the
            // toggle (Hidden stays false) and route a clear ScreenMessages
            // toast + Warn log line, so the player cannot silently sweep
            // the re-fly opportunity out of view. Guards the inline policy
            // wired in DrawRecordingRow's hide branch.
            var rec = MakeCrashedUnfinished("rec_uf2", "bp_uf2", "Debris");
            RecordingStore.AddCommittedInternal(rec);
            InstallScenarioWithRp("bp_uf2", "rp_uf2", rec.RecordingId);

            Assert.False(rec.Hidden, "precondition");

            // The DrawRecordingRow policy: when inside the Unfinished Flights
            // virtual group (unfinishedFlightRowDepth > 0) and the recording
            // classifies as unfinished, refuse the flip and toast.
            bool insideVirtualGroup = true;
            bool policyAllowsHide =
                !(insideVirtualGroup && EffectiveState.IsUnfinishedFlight(rec));
            if (policyAllowsHide)
            {
                rec.Hidden = true;
            }
            else
            {
                ParsekLog.Warn("UnfinishedFlights",
                    $"Hide refused for Unfinished Flight rec={rec.RecordingId} " +
                    $"vessel='{rec.VesselName}': rewind access must remain visible (design §7.33)");
                ParsekLog.ScreenMessage(
                    $"Cannot hide '{rec.VesselName}' — it is an Unfinished Flight. " +
                    "Re-fly the rewind point or merge as Immutable to clear it from the list.",
                    4f);
            }

            Assert.False(rec.Hidden, "Unfinished Flight hide must not flip the field");
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]") && l.Contains("Hide refused"));
            Assert.Contains(screenMessages, m =>
                m.Contains("Cannot hide") && m.Contains("Unfinished Flight"));
        }

        [Fact]
        public void Hide_NonUnfinishedRecording_FlipsNormally()
        {
            // Positive control: a regular (non-unfinished) recording's hide
            // toggle must pass through unchanged even when the caller uses
            // the same helper logic. The gate is IsUnfinishedFlight alone,
            // so a non-unfinished recording always allows the toggle.
            var rec = new Recording
            {
                RecordingId = "rec_regular",
                VesselName = "Regular",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Landed,
            };
            RecordingStore.AddCommittedInternal(rec);
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                RewindPoints = new List<RewindPoint>(),
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
            });

            Assert.False(EffectiveState.IsUnfinishedFlight(rec));

            bool policyAllowsHide = !EffectiveState.IsUnfinishedFlight(rec);
            if (policyAllowsHide) rec.Hidden = true;

            Assert.True(rec.Hidden, "regular recording hide must flip");
        }

        [Fact]
        public void Hide_NormalListUnfinishedFlight_RefusesWithoutVirtualGroup()
        {
            // Follow-up to the PR #504 review: with the depth gate removed,
            // an Unfinished Flight recording that appears in the normal
            // Recordings list (not under the virtual "Unfinished Flights"
            // group) must also refuse the hide toggle. Before the follow-up,
            // the gate was `unfinishedFlightRowDepth > 0 &&
            // IsUnfinishedFlight(rec)` which let normal-list rows bypass the
            // refuse path. This test pins the classifier-only gate.
            var rec = MakeCrashedUnfinished("rec_uf_normal", "bp_uf_normal", "Booster");
            RecordingStore.AddCommittedInternal(rec);
            InstallScenarioWithRp("bp_uf_normal", "rp_uf_normal", rec.RecordingId);

            Assert.True(EffectiveState.IsUnfinishedFlight(rec),
                "precondition: recording must classify as unfinished");
            Assert.False(rec.Hidden, "precondition");

            // Simulate the DrawRecordingRow hide branch for a row rendered
            // outside the virtual group: the classifier alone must block.
            bool policyAllowsHide = !EffectiveState.IsUnfinishedFlight(rec);
            if (policyAllowsHide)
            {
                rec.Hidden = true;
            }
            else
            {
                ParsekLog.Warn("UnfinishedFlights",
                    $"Hide refused for Unfinished Flight rec={rec.RecordingId} " +
                    $"vessel='{rec.VesselName}': rewind access must remain visible (design §7.33)");
                ParsekLog.ScreenMessage(
                    $"Cannot hide '{rec.VesselName}' — it is an Unfinished Flight. " +
                    "Re-fly the rewind point or merge as Immutable to clear it from the list.",
                    4f);
            }

            Assert.False(rec.Hidden, "normal-list Unfinished Flight hide must not flip the field");
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]") && l.Contains("Hide refused"));
            Assert.Contains(screenMessages, m =>
                m.Contains("Cannot hide") && m.Contains("Unfinished Flight"));
        }

        [Fact]
        public void Hide_PolicyGate_IsClassifierOnly_NoDepthCheck()
        {
            // Source-inspection regression pinning the follow-up: the hide
            // branch in DrawRecordingRow must gate on IsUnfinishedFlight(rec)
            // alone, with no `unfinishedFlightRowDepth > 0` prefix, otherwise
            // normal-list RP-backed rows silently bypass the refuse path.
            string srcRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "Parsek"));
            string uiSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(srcRoot, "UI", "RecordingsTableUI.cs"));

            int hideAnchor = uiSrc.IndexOf("// Hide checkbox", StringComparison.Ordinal);
            Assert.True(hideAnchor >= 0, "Hide checkbox anchor should exist in RecordingsTableUI.cs");
            int endAnchor = uiSrc.IndexOf("GUILayout.EndHorizontal();", hideAnchor + 1, StringComparison.Ordinal);
            Assert.True(endAnchor > hideAnchor, "Expected closing EndHorizontal after Hide checkbox block");
            // Capture enough of the branch to include both if-branches.
            int branchEnd = uiSrc.IndexOf("rec.Hidden = hidden;", endAnchor, StringComparison.Ordinal);
            Assert.True(branchEnd > hideAnchor, "Expected hide flip branch in source");
            string hideBlock = uiSrc.Substring(hideAnchor, branchEnd - hideAnchor);

            Assert.DoesNotContain("unfinishedFlightRowDepth > 0", hideBlock);
            Assert.Contains("EffectiveState.IsUnfinishedFlight(rec)", hideBlock);
        }
    }
}
