using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pre-invoke advisory (part-action audit item C2): before a Re-Fly is
    /// confirmed, the player is told what the revert takes back, which of this
    /// rewind point's other craft become replays, and what is preserved.
    ///
    /// <para>These cells pin the exact player-facing strings, because the whole
    /// point of the feature is the CONTENT. A refactor that keeps the advisory
    /// present but drops the sibling list, or stops naming the concrete
    /// player-visible examples of what reverts, turns an informed choice back
    /// into a silent one — which is the regression this feature exists to
    /// prevent.</para>
    ///
    /// <para>Post-P1 framing note: the advisory deliberately does NOT warn about
    /// fleet deletion. Unrelated vessels, stations, asteroids and flags are
    /// preserved as of the REFLY-DELETES-NON-SLOT-WORLD fix, and a stale warning
    /// about losing them would be worse than none.</para>
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyPreInvokeAdvisoryTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ReFlyPreInvokeAdvisoryTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Fixture helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds an RP whose slots are all MAPPED (pid -> slotIndex), i.e. the
        /// shape the scrub can actually match and therefore actually removes.
        /// </summary>
        private static RewindPoint BuildMappedRp(params (int slotIndex, string originId)[] slots)
        {
            var rp = new RewindPoint
            {
                RewindPointId = "rp_advisory_fixture",
                UT = 12345.678,
            };
            uint pid = 5000u;
            foreach (var s in slots)
            {
                rp.ChildSlots.Add(new ChildSlot
                {
                    SlotIndex = s.slotIndex,
                    OriginChildRecordingId = s.originId,
                });
                rp.PidSlotMap[pid++] = s.slotIndex;
            }
            return rp;
        }

        private static Recording Rec(string id, string vesselName)
            => new Recording { RecordingId = id, VesselName = vesselName };

        // ------------------------------------------------------------------
        // ResolveReFlySiblingSlotNames
        // ------------------------------------------------------------------

        [Fact]
        public void ResolveSiblingSlotNames_OtherMappedSlots_AreNamedFromTheirOriginRecordings()
        {
            var rp = BuildMappedRp((0, "rec_booster"), (1, "rec_probe"), (2, "rec_capsule"));
            var recordings = new List<Recording>
            {
                Rec("rec_booster", "Kerbal X Booster"),
                Rec("rec_probe", "Kerbal X Probe"),
                Rec("rec_capsule", "Kerbal X"),
            };

            var names = RewindInvoker.ResolveReFlySiblingSlotNames(rp, 1, recordings);

            Assert.Equal(2, names.Count);
            Assert.Contains("Kerbal X Booster", names);
            Assert.Contains("Kerbal X", names);
            // The re-flown slot is the one the player keeps flying, not a replay.
            Assert.DoesNotContain("Kerbal X Probe", names);
        }

        [Fact]
        public void ResolveSiblingSlotNames_ExcludesBySlotIndexNotListIndex()
        {
            // Deliberately out of order: list index 0 carries SlotIndex 7.
            // The scrub compares against slot-map VALUES (SlotIndex), so a
            // helper that excluded by list position would name the craft the
            // player is about to fly and omit one that really does leave.
            var rp = BuildMappedRp((7, "rec_seven"), (0, "rec_zero"));
            var recordings = new List<Recording>
            {
                Rec("rec_seven", "Seven"),
                Rec("rec_zero", "Zero"),
            };

            var names = RewindInvoker.ResolveReFlySiblingSlotNames(rp, 7, recordings);

            Assert.Single(names);
            Assert.Equal("Zero", names[0]);
        }

        [Fact]
        public void ResolveSiblingSlotNames_SlotInNoMap_IsOmitted()
        {
            // A slot the split-time correlation never mapped: the scrub cannot
            // match its vessel, so nothing of it is removed. Claiming otherwise
            // in a player-facing dialog would be a false statement.
            var rp = BuildMappedRp((0, "rec_booster"), (1, "rec_probe"));
            rp.ChildSlots.Add(new ChildSlot { SlotIndex = 2, OriginChildRecordingId = "rec_unmapped" });
            var recordings = new List<Recording>
            {
                Rec("rec_booster", "Kerbal X Booster"),
                Rec("rec_probe", "Kerbal X Probe"),
                Rec("rec_unmapped", "Kerbal X Lander"),
            };

            var names = RewindInvoker.ResolveReFlySiblingSlotNames(rp, 1, recordings);

            Assert.Single(names);
            Assert.Equal("Kerbal X Booster", names[0]);
            Assert.DoesNotContain("Kerbal X Lander", names);
        }

        [Fact]
        public void ResolveSiblingSlotNames_RootPartPidMapAlone_StillCountsAsMapped()
        {
            // The scrub matches on EITHER vessel pid OR root-part pid, so a slot
            // present only in RootPartPidMap is still removed.
            var rp = new RewindPoint { RewindPointId = "rp_rootpart", UT = 1.0 };
            rp.ChildSlots.Add(new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_a" });
            rp.ChildSlots.Add(new ChildSlot { SlotIndex = 1, OriginChildRecordingId = "rec_b" });
            rp.PidSlotMap[100u] = 1;
            rp.RootPartPidMap[200u] = 0;

            var names = RewindInvoker.ResolveReFlySiblingSlotNames(
                rp, 1, new List<Recording> { Rec("rec_a", "Alpha"), Rec("rec_b", "Beta") });

            Assert.Single(names);
            Assert.Equal("Alpha", names[0]);
        }

        [Fact]
        public void ResolveSiblingSlotNames_UnresolvableOrigin_FallsBackToSlotLabel()
        {
            var rp = BuildMappedRp((0, "rec_missing"), (1, "rec_probe"));
            var recordings = new List<Recording> { Rec("rec_probe", "Kerbal X Probe") };

            var names = RewindInvoker.ResolveReFlySiblingSlotNames(rp, 1, recordings);

            Assert.Single(names);
            Assert.Equal("slot 0", names[0]);
        }

        [Fact]
        public void ResolveSiblingSlotNames_SupersededOriginStillResolvesByRawId()
        {
            // A sibling slot superseded by an earlier Re-Fly is hidden from the
            // ERS while its vessel is still in the quicksave and still gets
            // removed. The raw id lookup must therefore still find the name -
            // this cell pins that the resolver is NOT ERS-filtered.
            var rp = BuildMappedRp((0, "rec_old_booster"), (1, "rec_probe"));
            var recordings = new List<Recording>
            {
                Rec("rec_old_booster", "Kerbal X Booster"),
                Rec("rec_probe", "Kerbal X Probe"),
                Rec("rec_new_booster", "Kerbal X Booster"),
            };

            var names = RewindInvoker.ResolveReFlySiblingSlotNames(rp, 1, recordings);

            Assert.Single(names);
            Assert.Equal("Kerbal X Booster", names[0]);
        }

        [Fact]
        public void ResolveSiblingSlotNames_NullOrEmptyInputs_ReturnEmpty()
        {
            Assert.Empty(RewindInvoker.ResolveReFlySiblingSlotNames(null, 0, null));
            Assert.Empty(RewindInvoker.ResolveReFlySiblingSlotNames(
                new RewindPoint { RewindPointId = "rp_empty" }, 0, new List<Recording>()));

            var soloRp = BuildMappedRp((0, "rec_solo"));
            Assert.Empty(RewindInvoker.ResolveReFlySiblingSlotNames(
                soloRp, 0, new List<Recording> { Rec("rec_solo", "Solo") }));
        }

        [Fact]
        public void ResolveSiblingSlotNames_NullSlotEntry_IsSkippedNotThrown()
        {
            var rp = BuildMappedRp((0, "rec_a"), (1, "rec_b"));
            rp.ChildSlots.Add(null);

            var names = RewindInvoker.ResolveReFlySiblingSlotNames(
                rp, 1, new List<Recording> { Rec("rec_a", "Alpha"), Rec("rec_b", "Beta") });

            Assert.Single(names);
            Assert.Equal("Alpha", names[0]);
        }

        // ------------------------------------------------------------------
        // ComposeReFlyAdvisoryBody
        // ------------------------------------------------------------------

        [Fact]
        public void ComposeAdvisoryBody_NamesTheSevenPatchedFacets()
        {
            string body = RewindInvoker.ComposeReFlyAdvisoryBody(new List<string> { "Kerbal X Booster" });

            // The seven KspStatePatcher facets, in plain English. If a facet
            // leaves the patch set this string is the player-facing statement
            // that has to change with it.
            Assert.Contains("science", body);
            Assert.Contains("tech tree", body);
            Assert.Contains("funds", body);
            Assert.Contains("reputation", body);
            Assert.Contains("facility upgrades", body);
            Assert.Contains("milestones", body);
            Assert.Contains("contracts", body);
            Assert.Contains("carried forward", body);
        }

        [Fact]
        public void ComposeAdvisoryBody_NamesTheConcretePlayerVisibleRevertExamples()
        {
            string body = RewindInvoker.ComposeReFlyAdvisoryBody(new List<string>());

            // "world state reverts" is unactionable on its own; the examples are
            // the load-bearing part of the statement.
            Assert.Contains("kerbal experience", body);
            Assert.Contains("resource-survey unlocks", body);
            Assert.Contains("contract waypoint progress", body);
            Assert.Contains("deployed-science", body);
        }

        [Fact]
        public void ComposeAdvisoryBody_StatesThatRecoveredCraftComeBackAndGiveTheirRewardsUp()
        {
            // #15: the rewind resurrects craft the player had already recovered, and the
            // matching recovery funds/science/crew rows are retired at invoke. That is a
            // funds-visible consequence, so the player has to be told BEFORE they confirm.
            // Fails if the sentence is dropped or reworded away from the two facts it
            // carries — the craft comes back, AND its rewards go back.
            string body = RewindInvoker.ComposeReFlyAdvisoryBody(new List<string>());

            Assert.Contains("Craft you recovered after this point are back in flight", body);
            Assert.Contains("recovery rewards are returned to the ledger", body);
        }

        [Fact]
        public void ComposeAdvisoryBody_ListsTheSiblingCraftThatBecomeReplays()
        {
            string body = RewindInvoker.ComposeReFlyAdvisoryBody(
                new List<string> { "Kerbal X Booster", "Kerbal X Lander" });

            Assert.Contains("Kerbal X Booster", body);
            Assert.Contains("Kerbal X Lander", body);
            Assert.Contains("replayed as ghosts", body);
        }

        [Fact]
        public void ComposeAdvisoryBody_NoSiblings_SaysSoInsteadOfAnEmptyList()
        {
            string body = RewindInvoker.ComposeReFlyAdvisoryBody(new List<string>());

            Assert.Contains("No other craft from this separation are put away", body);
            Assert.DoesNotContain("[]", body);
        }

        [Fact]
        public void ComposeAdvisoryBody_RendersNamesAsProseNotAsABracketedLogList()
        {
            // The dialog body is prose the player reads; the bracketed form is
            // the LOG rendering. Leaking the log form into the dialog is the
            // small ugliness this cell exists to prevent.
            string body = RewindInvoker.ComposeReFlyAdvisoryBody(
                new List<string> { "Kerbal X Booster", "Kerbal X Lander" });

            Assert.Contains("Kerbal X Booster, Kerbal X Lander", body);
            Assert.DoesNotContain("[Kerbal X Booster", body);
        }

        [Fact]
        public void ComposeAdvisoryBody_NullSiblings_IsTreatedAsNone()
        {
            string body = RewindInvoker.ComposeReFlyAdvisoryBody(null);

            Assert.Contains("No other craft from this separation are put away", body);
        }

        [Fact]
        public void ComposeAdvisoryBody_ReassuresThatUnrelatedWorldIsPreserved()
        {
            string body = RewindInvoker.ComposeReFlyAdvisoryBody(new List<string> { "Kerbal X Booster" });

            // Post-P1: the fleet is PRESERVED, so the advisory must say so
            // rather than warn about deletion.
            Assert.Contains("Everything else stays where it is", body);
            Assert.Contains("asteroids", body);
            Assert.Contains("comets", body);
            Assert.Contains("planted flags", body);
            Assert.DoesNotContain("deleted", body);
            Assert.DoesNotContain("removed from your save", body);
        }

        [Fact]
        public void ComposeAdvisoryBody_MoreSiblingsThanTheCap_CapsAndReportsTheOverflow()
        {
            var many = new List<string>();
            for (int i = 0; i < RewindInvoker.MaxAdvisorySlotNamesShown + 3; i++)
                many.Add("Craft " + i.ToString());

            string body = RewindInvoker.ComposeReFlyAdvisoryBody(many);

            Assert.Contains("Craft 0", body);
            Assert.Contains(
                "Craft " + (RewindInvoker.MaxAdvisorySlotNamesShown - 1).ToString(), body);
            Assert.DoesNotContain(
                "Craft " + RewindInvoker.MaxAdvisorySlotNamesShown.ToString(), body);
            Assert.Contains("(+3 more)", body);
        }

        // ------------------------------------------------------------------
        // FormatCappedNameList
        // ------------------------------------------------------------------

        [Fact]
        public void FormatCappedProseList_MirrorsTheLogListsCapAndOverflowWithoutBrackets()
        {
            Assert.Equal("", RewindInvoker.FormatCappedProseList(null, 8));
            Assert.Equal("", RewindInvoker.FormatCappedProseList(new List<string>(), 8));
            Assert.Equal("A, B", RewindInvoker.FormatCappedProseList(new List<string> { "A", "B" }, 8));
            Assert.Equal(
                "A, B (+2 more)",
                RewindInvoker.FormatCappedProseList(new List<string> { "A", "B", "C", "D" }, 2));
            Assert.Equal(
                "A, <unnamed>",
                RewindInvoker.FormatCappedProseList(new List<string> { "A", "" }, 8));
            Assert.Equal(
                "A (+1 more)",
                RewindInvoker.FormatCappedProseList(new List<string> { "A", "B" }, 0));
        }

        [Fact]
        public void FormatCappedNameList_NullOrEmpty_RendersEmptyBrackets()
        {
            Assert.Equal("[]", RewindInvoker.FormatCappedNameList(null, 8));
            Assert.Equal("[]", RewindInvoker.FormatCappedNameList(new List<string>(), 8));
        }

        [Fact]
        public void FormatCappedNameList_UnderCap_ListsEveryEntryWithNoOverflowNote()
        {
            string s = RewindInvoker.FormatCappedNameList(new List<string> { "A", "B" }, 8);

            Assert.Equal("[A, B]", s);
        }

        [Fact]
        public void FormatCappedNameList_OverCap_TruncatesAndCountsTheRemainder()
        {
            string s = RewindInvoker.FormatCappedNameList(
                new List<string> { "A", "B", "C", "D" }, 2);

            Assert.Equal("[A, B] (+2 more)", s);
        }

        [Fact]
        public void FormatCappedNameList_BlankEntry_RendersAsUnnamedNotAsAGap()
        {
            string s = RewindInvoker.FormatCappedNameList(new List<string> { "A", "", null }, 8);

            Assert.Equal("[A, <unnamed>, <unnamed>]", s);
        }

        [Fact]
        public void FormatCappedNameList_NonPositiveCap_StillShowsOneEntry()
        {
            // Defensive: a zero/negative cap must not produce "[]" for a
            // non-empty list, which would read as "nothing is affected".
            string s = RewindInvoker.FormatCappedNameList(new List<string> { "A", "B" }, 0);

            Assert.Equal("[A] (+1 more)", s);
        }

        // ------------------------------------------------------------------
        // Log line
        // ------------------------------------------------------------------

        [Fact]
        public void FormatAdvisoryLogLine_CarriesTheGrepStableTokens()
        {
            string line = RewindInvoker.FormatReFlyAdvisoryLogLine(
                "rp_abc", 2, 98765.4321, new List<string> { "Kerbal X Booster", "Kerbal X" });

            Assert.StartsWith("Pre-invoke advisory: ", line);
            Assert.Contains("rp=rp_abc", line);
            Assert.Contains("slot=2", line);
            Assert.Contains("rewindUT=98765.4", line);
            Assert.Contains("siblingSlotsPutAway=2", line);
            Assert.Contains("[Kerbal X Booster, Kerbal X]", line);
            Assert.Contains("preserved=unrelated-vessels+stations+asteroids+comets+flags", line);
        }

        [Fact]
        public void FormatAdvisoryLogLine_NullRpId_RendersPlaceholderNotAnEmptyToken()
        {
            string line = RewindInvoker.FormatReFlyAdvisoryLogLine(null, 0, 0.0, null);

            Assert.Contains("rp=<null>", line);
            Assert.Contains("siblingSlotsPutAway=0", line);
            Assert.Contains("names=[]", line);
        }

        [Fact]
        public void LogReFlyAdvisory_EmitsOneInfoLineUnderTheRewindSubsystem()
        {
            RewindInvoker.LogReFlyAdvisory(
                "rp_log_cell", 1, 4242.0, new List<string> { "Kerbal X Booster" });

            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("Pre-invoke advisory:")
                && l.Contains("rp=rp_log_cell")
                && l.Contains("slot=1")
                && l.Contains("siblingSlotsPutAway=1")
                && l.Contains("[Kerbal X Booster]"));
        }

        [Fact]
        public void LogReFlyAdvisory_StatesTheFacetSummaryTheDialogStates()
        {
            // The dialog and the log line must not drift: both read the same
            // PatchedCareerFacetsSummary constant.
            RewindInvoker.LogReFlyAdvisory("rp_drift", 0, 1.0, new List<string>());
            string body = RewindInvoker.ComposeReFlyAdvisoryBody(new List<string>());

            Assert.Contains(logLines, l =>
                l.Contains("Pre-invoke advisory:")
                && l.Contains(RewindInvoker.PatchedCareerFacetsSummary));
            Assert.Contains(RewindInvoker.PatchedCareerFacetsSummary, body);
        }
    }
}
