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
                // Open crashed Unfinished Flight -> CommittedProvisional tip
                // (open) after promotion (collapse-seal-into-mergestate).
                MergeState = MergeState.CommittedProvisional,
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

        // Hide_UnfinishedFlight_WarnsAndDoesNotFlip, Hide_NonUnfinishedRecording_FlipsNormally
        // and Hide_NormalListUnfinishedFlight_RefusesWithoutVirtualGroup were deleted here
        // (audit F-catchall-036-01). All three rebuilt the hide policy expression in the test
        // body and emitted the Warn plus the ScreenMessage themselves, so they asserted
        // test-authored output; the expression they modelled (depth AND classifier) is not the
        // shipped one either, which gates on DIRECTION through
        // RecordingsTableUI.IsArchiveRefusedForUnfinishedFlight. The shipped predicate is
        // covered behaviourally by ArchiveRefusal_AppliesToHidingOnly below (all four
        // requestedHidden / isUnfinishedFlight combinations) and at wiring level by
        // Hide_PolicyGate_IsClassifierOnly_NoDepthCheck and
        // TheGroupHideAllScanRedsWhenTheRoutingExistsOnlyInAComment in this same file.

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

            // Comments blanked before the needle checks (StripCSharpComments is
            // length-preserving, so indexes taken from the raw text address the same
            // characters). Without it both needles read the large comment block inside
            // this window: the classifier call surviving only in a comment would pass,
            // and a comment mentioning the depth check would trip the negative.
            string preparedUiSrc = SourceScanText.StripCSharpComments(uiSrc);
            int hideAnchor = uiSrc.IndexOf("// Hide checkbox", StringComparison.Ordinal);
            Assert.True(hideAnchor >= 0, "Hide checkbox anchor should exist in RecordingsTableUI.cs");
            int endAnchor = preparedUiSrc.IndexOf("GUILayout.EndHorizontal();", hideAnchor + 1, StringComparison.Ordinal);
            Assert.True(endAnchor > hideAnchor, "Expected closing EndHorizontal after Hide checkbox block");
            // Capture enough of the branch to include both if-branches.
            int branchEnd = preparedUiSrc.IndexOf("rec.Hidden = hidden;", endAnchor, StringComparison.Ordinal);
            Assert.True(branchEnd > hideAnchor, "Expected hide flip branch in source");
            string hideBlock = preparedUiSrc.Substring(hideAnchor, branchEnd - hideAnchor);

            Assert.DoesNotContain("unfinishedFlightRowDepth > 0", hideBlock);
            Assert.Contains("EffectiveState.IsUnfinishedFlight(rec)", hideBlock);
        }

        // catches: the Archive refusal losing its DIRECTION. Refusing exists to keep rewind
        // access visible, so it can only apply to HIDING; the undirected check the per-row
        // branch used to carry also refused the UN-hide, which would keep a flight that is
        // already buried buried - and un-hiding is the only way back for a row an older
        // build's ungated group hide-all wrote Hidden over (finding P17, mirror half).
        [Fact]
        public void ArchiveRefusal_AppliesToHidingOnly()
        {
            Assert.True(RecordingsTableUI.IsArchiveRefusedForUnfinishedFlight(
                requestedHidden: true, isUnfinishedFlight: true));
            Assert.False(RecordingsTableUI.IsArchiveRefusedForUnfinishedFlight(
                requestedHidden: false, isUnfinishedFlight: true));
            Assert.False(RecordingsTableUI.IsArchiveRefusedForUnfinishedFlight(
                requestedHidden: true, isUnfinishedFlight: false));
            Assert.False(RecordingsTableUI.IsArchiveRefusedForUnfinishedFlight(
                requestedHidden: false, isUnfinishedFlight: false));
        }

        // catches: a refused folder archive becoming a dead click. The message must name the
        // folder and the count so the player knows what to resolve, and singular / plural
        // must agree (the count is the only number in it).
        [Fact]
        public void GroupArchiveRefusedMessage_NamesTheFolderAndTheCount()
        {
            string one = RecordingsTableUI.BuildGroupArchiveRefusedMessage("Munshots", 1);
            Assert.Contains("Munshots", one);
            Assert.Contains("1 Unfinished Flight inside", one);

            string many = RecordingsTableUI.BuildGroupArchiveRefusedMessage("Munshots", 3);
            Assert.Contains("3 Unfinished Flights inside", many);
        }

        // catches: the GROUP hide-all going back to writing Hidden over every descendant
        // with no Unfinished-Flight check, which is what let archiving a folder bury the
        // re-flyable flight the per-row control refuses to bury (finding P17). Source
        // inspection for the same reason the depth-gate cell above uses it: the branch lives
        // inside an IMGUI draw method that cannot be driven headlessly.
        [Fact]
        public void GroupHideAll_RoutesThroughTheSharedArchiveRefusal()
        {
            string srcRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "Parsek"));
            string uiSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(srcRoot, "UI", "RecordingsTableUI.cs"));

            List<string> missing = FindMissingGroupHideAllRoutingTokens(uiSrc);

            Assert.True(missing.Count == 0,
                "the group hide-all block must route through the shared refusal. Missing: "
                + string.Join("; ", missing.ToArray()));
        }

        // anti-vacuity for the scan above: the block is WRAPPED in a comment that names the
        // per-row refusal it now shares, so a scan over raw source text answers "routed" for
        // a block that only talks about routing. Decoy with the three needles present in the
        // comment and nowhere else; every one of them must still read as missing.
        [Fact]
        public void TheGroupHideAllScanRedsWhenTheRoutingExistsOnlyInAComment()
        {
            string decoy =
                "            bool newAllHidden = GUILayout.Toggle(allHidden, Content);\n"
                + "            if (newAllHidden != allHidden)\n            {\n"
                + "                // Same guard as the per-row control: this used to skip\n"
                + "                // IsArchiveRefusedForUnfinishedFlight(newAllHidden, ...) and the\n"
                + "                // EffectiveState.IsUnfinishedFlight(committed[idx]) count behind it,\n"
                + "                // and never reached BuildGroupArchiveRefusedMessage(groupName, n).\n"
                + "                foreach (int idx in descendants)\n"
                + "                    committed[idx].Hidden = newAllHidden;\n"
                + "                ParsekLog.Info(\"UI\", $\"Group '{groupName}' hide-all={newAllHidden}\");\n"
                + "            }\n";

            List<string> missing = FindMissingGroupHideAllRoutingTokens(decoy);

            Assert.Equal(3, missing.Count);
        }

        /// <summary>
        /// The routing tokens the group hide-all block does not actually contain, comments
        /// excluded. Empty list = the block routes through the shared refusal.
        /// <para>Anchored on CODE at both ends (the toggle assignment and the log line's
        /// literal), not on the leading comment the block used to be found by: stripping
        /// comments is what makes the needles mean something, and it also removes the old
        /// anchor.</para>
        /// </summary>
        internal static List<string> FindMissingGroupHideAllRoutingTokens(string src)
        {
            string prepared = SourceScanText.StripCSharpComments(src);

            int anchor = prepared.IndexOf(
                "bool newAllHidden = GUILayout.Toggle(", StringComparison.Ordinal);
            Assert.True(anchor >= 0,
                "the group hide-all toggle assignment should exist in RecordingsTableUI.cs");
            int blockEnd = prepared.IndexOf("hide-all={newAllHidden}", anchor, StringComparison.Ordinal);
            Assert.True(blockEnd > anchor, "Expected the group hide-all log line after the anchor");
            string groupHideBlock = prepared.Substring(anchor, blockEnd - anchor);

            var missing = new List<string>();
            foreach (string token in new[]
                     {
                         "IsArchiveRefusedForUnfinishedFlight(newAllHidden",
                         "EffectiveState.IsUnfinishedFlight(committed[idx])",
                         "BuildGroupArchiveRefusedMessage(groupName",
                     })
            {
                if (groupHideBlock.IndexOf(token, StringComparison.Ordinal) < 0)
                    missing.Add(token);
            }
            return missing;
        }
    }
}
