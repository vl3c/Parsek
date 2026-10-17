using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure labprocess gates (the results dialog page, the active-vessel target, the
    /// <c>labValue</c> written before the send, stock's lab search, the cleanup of what a refused
    /// press opened) and the inspect <c>expectSnapshot=true rec= [tolerance=]</c> lab comparison
    /// of <c>PartAction</c>.
    /// </summary>
    public class TestCommandPartActionLabTests
    {
        private static ParsedCommand Cmd(string args)
            => TestCommandParser.ParseLine("id=1 cmd=PartAction " + args, 1);

        private static bool ValidRec(string id) => RecordingPaths.ValidateRecordingId(id);

        // ----- labprocess: the page -----

        [Fact]
        public void LabPage_MatchesOnlyThisPartsPageForTheModulesOwnDatum()
        {
            Assert.True(TestCommandPartAction.LabPageMatches(true, true, true, true));
            // No page up at all (after a load or F9, or inside the experiment's dialogDelay).
            Assert.False(TestCommandPartAction.LabPageMatches(false, false, false, false));
            // Another part's page, a page without the lab callback, or a page holding another
            // ScienceData object (a stale page of the same subject): never pressed.
            Assert.False(TestCommandPartAction.LabPageMatches(true, false, true, true));
            Assert.False(TestCommandPartAction.LabPageMatches(true, true, false, true));
            Assert.False(TestCommandPartAction.LabPageMatches(true, true, true, false));
        }

        [Fact]
        public void LabRoute_OpenPageWhenItMatches_ElseReview()
        {
            Assert.Equal(TestCommandPartAction.LabProcessRoute.OpenPage, TestCommandPartAction.DecideLabRoute(true));
            Assert.Equal(TestCommandPartAction.LabProcessRoute.Review, TestCommandPartAction.DecideLabRoute(false));
            Assert.Equal("page", TestCommandPartAction.LabRouteToken(TestCommandPartAction.LabProcessRoute.OpenPage));
            Assert.Equal("review", TestCommandPartAction.LabRouteToken(TestCommandPartAction.LabProcessRoute.Review));
        }

        [Fact]
        public void LabPageAfterReview_NoPageRefuses()
        {
            Assert.Null(TestCommandPartAction.DecideLabPageAfterReview(true));
            Assert.Equal("partaction-lab-page-missing", TestCommandPartAction.DecideLabPageAfterReview(false));
        }

        // ----- labprocess: target, value, lab -----

        [Fact]
        public void LabTarget_MustBeTheActiveVessel()
        {
            Assert.Null(TestCommandPartAction.DecideLabTarget(true));
            Assert.Equal("partaction-lab-not-active-vessel", TestCommandPartAction.DecideLabTarget(false));
        }

        [Theory]
        [InlineData(0f, "partaction-lab-value-zero")]
        [InlineData(-1f, "partaction-lab-value-zero")]
        [InlineData(float.NaN, "partaction-lab-value-zero")]
        [InlineData(0.5f, null)]
        [InlineData(24f, null)]
        public void LabValue_ZeroOrLessRefusesBeforeTheSend(float labValue, string expected)
        {
            Assert.Equal(expected, TestCommandPartAction.DecideLabValue(labValue));
        }

        [Fact]
        public void LabSearch_NoLabForTheDatumRefuses()
        {
            Assert.Null(TestCommandPartAction.DecideLabSearch(true));
            Assert.Equal("partaction-lab-unavailable", TestCommandPartAction.DecideLabSearch(false));
        }

        [Fact]
        public void LabRefusal_BeforeAnySendIsRejected_AfterASendIsError()
        {
            // First candidate (or the only one): nothing sent yet, a side-effect-free refusal.
            Assert.Equal("REJECTED", TestCommandPartAction.DecideLabRefusalVerdict(
                "partaction-lab-value-zero", 0, out string reason));
            Assert.Equal("partaction-lab-value-zero", reason);
            // all=true, candidate 2 of 3 refuses after one send took its lab slot.
            Assert.Equal("ERROR", TestCommandPartAction.DecideLabRefusalVerdict(
                "partaction-lab-unavailable", 1, out reason));
            Assert.Equal("partaction-lab-walk-stopped", reason);
            Assert.Contains(reason, TestCommandPartAction.ErrorReasons);
            Assert.DoesNotContain(reason, TestCommandPartAction.Reasons);
        }

        [Fact]
        public void LabCleanup_ClosesOnlyWhatTheReviewOpened()
        {
            var open = TestCommandPartAction.LabProcessRoute.OpenPage;
            var review = TestCommandPartAction.LabProcessRoute.Review;
            // The page was already up: never closed by the seam.
            Assert.Equal(TestCommandPartAction.LabDialogCleanup.None,
                TestCommandPartAction.DecideLabCleanup(open, true, 1, 1));
            // The review opened the dialog: the whole dialog goes.
            Assert.Equal(TestCommandPartAction.LabDialogCleanup.DismissDialog,
                TestCommandPartAction.DecideLabCleanup(review, false, 0, 1));
            // The review added one page to a dialog already up: only that page goes.
            Assert.Equal(TestCommandPartAction.LabDialogCleanup.KeepPage,
                TestCommandPartAction.DecideLabCleanup(review, true, 2, 3));
            // The review re-pointed the dialog at this datum's existing page: nothing added.
            Assert.Equal(TestCommandPartAction.LabDialogCleanup.None,
                TestCommandPartAction.DecideLabCleanup(review, true, 2, 2));
        }

        [Fact]
        public void LabTail_AndPayload_OneEntryPerPress_Invariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var routes = new List<TestCommandPartAction.LabProcessRoute>
                {
                    TestCommandPartAction.LabProcessRoute.Review, TestCommandPartAction.LabProcessRoute.OpenPage,
                };
                var values = new List<float> { 24f, 7.5f };
                Assert.Equal(" labRoute=review,page labValue=24.00,7.50",
                    TestCommandPartAction.FormatLabTail(routes, values));
                var payload = TestCommandPartAction.LabPayloadFields(routes, values);
                Assert.Equal(new[] { "labRoute", "labValue" }, payload.Select(kv => kv.Key).ToArray());
                Assert.Equal("review,page", payload[0].Value);
                Assert.Equal("24.00,7.50", payload[1].Value);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void LabRefusalDetail_NamesTheGateNumbers()
        {
            string detail = TestCommandPartAction.FormatLabRefusalDetail(42, "sensorThermometer", 9,
                "temperatureScan@KerbinInSpaceLow", TestCommandPartAction.LabProcessRoute.Review,
                TestCommandPartAction.LabDialogCleanup.DismissDialog, 0f, null, 0, 1);
            Assert.Equal("button=labprocess pid=42 part='sensorThermometer' partId=9 " +
                "subject='temperatureScan@KerbinInSpaceLow' labRoute=review labValue=0.00 labError=- " +
                "cleanup=DismissDialog presses=0/1", detail);
            Assert.Contains(" labError=NO_SPACE:labs=1 ", TestCommandPartAction.FormatLabRefusalDetail(42, "x", 9,
                "s", TestCommandPartAction.LabProcessRoute.OpenPage, TestCommandPartAction.LabDialogCleanup.None,
                24f, "NO_SPACE:labs=1", 0, 1));
        }

        // ----- inspect expectSnapshot: args -----

        [Fact]
        public void SnapshotArgs_AbsentIsNotAsked()
        {
            Assert.Null(TestCommandPartAction.ParseSnapshotArgs(PartActionKind.Inspect, null, null, null, ValidRec,
                out bool expect, out string rec, out double tol));
            Assert.False(expect);
            Assert.Null(rec);
            Assert.Equal(0.01, tol);
            // Absent on any other button too.
            Assert.Null(TestCommandPartAction.ParseSnapshotArgs(PartActionKind.LabProcess, null, null, null, ValidRec,
                out _, out _, out _));
        }

        [Fact]
        public void SnapshotArgs_TrueWithRec_DefaultAndExplicitTolerance()
        {
            Assert.Null(TestCommandPartAction.ParseSnapshotArgs(PartActionKind.Inspect, "true", "abc123", null,
                ValidRec, out bool expect, out string rec, out double tol));
            Assert.True(expect);
            Assert.Equal("abc123", rec);
            Assert.Equal(0.01, tol);
            Assert.Null(TestCommandPartAction.ParseSnapshotArgs(PartActionKind.Inspect, "true", "abc123", "0.5",
                ValidRec, out _, out _, out tol));
            Assert.Equal(0.5, tol);
            // false is accepted and asks nothing.
            Assert.Null(TestCommandPartAction.ParseSnapshotArgs(PartActionKind.Inspect, "false", null, null,
                ValidRec, out expect, out _, out _));
            Assert.False(expect);
        }

        [Theory]
        [InlineData("inspect", "True", "abc", null, "partaction-expect-snapshot-arg-invalid")]
        [InlineData("inspect", "yes", "abc", null, "partaction-expect-snapshot-arg-invalid")]
        [InlineData("labprocess", "true", "abc", null, "partaction-snapshot-arg-unexpected")]
        [InlineData("deploy", null, "abc", null, "partaction-snapshot-arg-unexpected")]
        [InlineData("experiment", null, null, "0.1", "partaction-snapshot-arg-unexpected")]
        [InlineData("inspect", "true", null, null, "partaction-expect-snapshot-needs-rec")]
        [InlineData("inspect", null, "abc", null, "partaction-snapshot-arg-unexpected")]
        [InlineData("inspect", "false", "abc", null, "partaction-snapshot-arg-unexpected")]
        [InlineData("inspect", null, null, "0.1", "partaction-snapshot-arg-unexpected")]
        [InlineData("inspect", "true", "../evil", null, "partaction-rec-arg-invalid")]
        [InlineData("inspect", "true", "abc", "-1", "partaction-tolerance-arg-invalid")]
        [InlineData("inspect", "true", "abc", "x", "partaction-tolerance-arg-invalid")]
        [InlineData("inspect", "true", "abc", "1e3", "partaction-tolerance-arg-invalid")]
        public void SnapshotArgs_Refusals(string button, string expect, string rec, string tolerance, string reason)
        {
            Assert.Null(TestCommandPartAction.ParseButton(button, out PartActionKind kind));
            Assert.Equal(reason, TestCommandPartAction.ParseSnapshotArgs(kind, expect, rec, tolerance, ValidRec,
                out bool e, out string r, out _));
            Assert.False(e);
            Assert.Null(r);
        }

        [Fact]
        public void SnapshotArgKeys_AreSpelledForTheWire()
        {
            ParsedCommand cmd = Cmd("button=inspect part=Large.Crewed.Lab expectSnapshot=true rec=abc tolerance=0.5");
            Assert.Equal("true", cmd.Args[TestCommandPartAction.ExpectSnapshotKey]);
            Assert.Equal("abc", cmd.Args[TestCommandPartAction.RecKey]);
            Assert.Equal("0.5", cmd.Args[TestCommandPartAction.ToleranceKey]);
        }

        // ----- inspect expectSnapshot: snapshot parse + match -----

        private static ConfigNode LabPart(string name, uint uid, uint pid, string dataStored, string storedScience,
            params string[] experiments)
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", name);
            part.AddValue("uid", uid.ToString(CultureInfo.InvariantCulture));
            part.AddValue("persistentId", pid.ToString(CultureInfo.InvariantCulture));
            var other = part.AddNode("MODULE");
            other.AddValue("name", "ModuleScienceConverter");
            var lab = part.AddNode("MODULE");
            lab.AddValue("name", "ModuleScienceLab");
            lab.AddValue("dataStored", dataStored);
            lab.AddValue("storedScience", storedScience);
            if (experiments.Length > 0)
            {
                var ed = lab.AddNode("EXPERIMENT_DATA");
                foreach (string e in experiments) ed.AddValue("experiment", e);
            }
            return part;
        }

        private static ConfigNode PlainPart(string name, uint uid, uint pid)
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", name);
            part.AddValue("uid", uid.ToString(CultureInfo.InvariantCulture));
            part.AddValue("persistentId", pid.ToString(CultureInfo.InvariantCulture));
            return part;
        }

        private static ConfigNode Vessel(params ConfigNode[] parts)
        {
            var v = new ConfigNode("VESSEL");
            foreach (ConfigNode p in parts) v.AddNode(p);
            return v;
        }

        [Fact]
        public void ParseSnapshotParts_ReadsTheLabModule_AndUnionsTheSubjects()
        {
            ConfigNode lab = LabPart("Large.Crewed.Lab", 501, 9001, "24.5", "3.25", "a@x", "b@y");
            // A second EXPERIMENT_DATA node (stock's OnSave shape is not pinned) adds to the set.
            ConfigNode extra = lab.GetNodes("MODULE")[1].AddNode("EXPERIMENT_DATA");
            extra.AddValue("experiment", "b@y");
            extra.AddValue("experiment", "c@z");
            var parts = TestCommandPartAction.ParseSnapshotParts(Vessel(PlainPart("mk1pod.v2", 500, 9000), lab));
            Assert.Equal(2, parts.Count);
            Assert.False(parts[0].HasLab);
            Assert.True(parts[1].HasLab);
            Assert.Equal(501u, parts[1].Uid);
            Assert.Equal(9001u, parts[1].PersistentId);
            Assert.Equal(24.5, parts[1].DataStored);
            Assert.Equal(3.25, parts[1].StoredScience);
            Assert.Equal(new[] { "a@x", "b@y", "c@z" }, parts[1].Experiments.ToArray());
            Assert.Null(TestCommandPartAction.ParseSnapshotParts(null));
            Assert.Null(TestCommandPartAction.ParseSnapshotParts(new ConfigNode("VESSEL")));
        }

        [Fact]
        public void MatchSnapshotPart_UidThenPersistentIdThenIndexThenUniqueName()
        {
            var parts = TestCommandPartAction.ParseSnapshotParts(Vessel(
                PlainPart("mk1pod.v2", 500, 9000),
                LabPart("Large.Crewed.Lab", 501, 9001, "0", "0")));
            Assert.Equal(1, TestCommandPartAction.MatchSnapshotPart(parts, 501, 1, "Large.Crewed.Lab", 0, 5, out string by));
            Assert.Equal("uid", by);
            Assert.Equal(1, TestCommandPartAction.MatchSnapshotPart(parts, 7, 9001, "Large.Crewed.Lab", 0, 5, out by));
            Assert.Equal("persistentId", by);
            // A Parsek spawn regenerates both ids but keeps the PART order.
            Assert.Equal(1, TestCommandPartAction.MatchSnapshotPart(parts, 7, 8, "Large.Crewed.Lab", 1, 2, out by));
            Assert.Equal("index", by);
            // Same index but another name, or another part count: fall through to the unique name.
            Assert.Equal(1, TestCommandPartAction.MatchSnapshotPart(parts, 7, 8, "Large.Crewed.Lab", 0, 2, out by));
            Assert.Equal("name", by);
            Assert.Equal(-1, TestCommandPartAction.MatchSnapshotPart(parts, 7, 8, "mk1pod.v2", 1, 3, out by));
            Assert.Equal("none", by);
            // Two same-name labs and no id match: no silent pick.
            var two = TestCommandPartAction.ParseSnapshotParts(Vessel(
                LabPart("Large.Crewed.Lab", 1, 2, "0", "0"), LabPart("Large.Crewed.Lab", 3, 4, "0", "0")));
            Assert.Equal(-1, TestCommandPartAction.MatchSnapshotPart(two, 7, 8, "Large.Crewed.Lab", 0, 5, out by));
            Assert.Equal(-1, TestCommandPartAction.MatchSnapshotPart(null, 1, 1, "x", 0, 1, out by));
        }

        // ----- inspect expectSnapshot: the comparison -----

        private static TestCommandPartAction.LiveLabReading Live(double dataStored, double storedScience,
            params string[] experiments)
            => new TestCommandPartAction.LiveLabReading
            {
                PartId = 501, PersistentId = 9001, Name = "Large.Crewed.Lab", Index = 1, PartCount = 2,
                HasLab = true, DataStored = dataStored, StoredScience = storedScience,
                Experiments = experiments.ToList(),
            };

        private static List<TestCommandPartAction.LabSnapshotPart> Snapshot(string dataStored, string storedScience,
            params string[] experiments)
            => TestCommandPartAction.ParseSnapshotParts(Vessel(PlainPart("mk1pod.v2", 500, 9000),
                LabPart("Large.Crewed.Lab", 501, 9001, dataStored, storedScience, experiments)));

        [Fact]
        public void EvaluateLabSnapshot_AllFieldsMatch_IsTrue_Invariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var o = TestCommandPartAction.EvaluateLabSnapshot("rec1", Live(24.004, 3.1, "b@y", "a@x", "a@x"),
                    Snapshot("24", "3.1", "a@x", "b@y"), 0.01);
                Assert.Equal("true", o.Token);
                Assert.Empty(o.Mismatches);
                Assert.Equal(new[]
                {
                    "partaction snapshot rec=rec1 partId=501 field=part matchedBy=uid snapshotIndex=1 match=true",
                    "partaction snapshot rec=rec1 partId=501 field=dataStored live=24.0040 snapshot=24.0000 diff=0.0040 match=true",
                    "partaction snapshot rec=rec1 partId=501 field=storedScience live=3.1000 snapshot=3.1000 diff=0.0000 match=true",
                    "partaction snapshot rec=rec1 partId=501 field=experiments live=2 snapshot=2 diff=0 match=true onlyLive='' onlySnapshot=''",
                }, o.Lines.ToArray());
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void EvaluateLabSnapshot_ZeroDataStoredInTheLab_IsTheLabProcessGapItCatches()
        {
            // The labprocess gap: the subject took the lab slot but 0 data was stored.
            var o = TestCommandPartAction.EvaluateLabSnapshot("rec1", Live(0, 0, "a@x"),
                Snapshot("24", "0", "a@x"), 0.01);
            Assert.Equal("false", o.Token);
            Assert.Equal(new[] { "501:dataStored" }, o.Mismatches.ToArray());
            Assert.Contains(o.Lines, l => l == "partaction snapshot rec=rec1 partId=501 field=dataStored " +
                "live=0.0000 snapshot=24.0000 diff=-24.0000 match=false");
        }

        [Fact]
        public void EvaluateLabSnapshot_SubjectSetsDiffer_NamesBothSides()
        {
            var o = TestCommandPartAction.EvaluateLabSnapshot("rec1", Live(24, 1, "a@x", "c@z"),
                Snapshot("24", "1", "a@x", "b@y"), 0.01);
            Assert.Equal("false", o.Token);
            Assert.Equal(new[] { "501:experiments" }, o.Mismatches.ToArray());
            Assert.Contains(o.Lines, l => l == "partaction snapshot rec=rec1 partId=501 field=experiments " +
                "live=2 snapshot=2 diff=2 match=false onlyLive='c@z' onlySnapshot='b@y'");
        }

        [Fact]
        public void EvaluateLabSnapshot_ToleranceZero_ComparesAtFloatPrecision()
        {
            // The live field is a float widened to double (3.1f -> 3.0999999046...); the snapshot
            // holds the float's round-trip text. Equal floats must match at tolerance 0.
            float liveData = 37.3f;
            float liveScience = 3.1f;
            string snapData = liveData.ToString("G9", CultureInfo.InvariantCulture);
            var o = TestCommandPartAction.EvaluateLabSnapshot("r", Live(liveData, liveScience),
                Snapshot(snapData, "3.1"), 0.0);
            Assert.Equal("true", o.Token);
            Assert.Empty(o.Mismatches);
            // One float step apart still differs at tolerance 0.
            float next = 3.1000003f;
            Assert.NotEqual(liveScience, next);
            Assert.Equal("false", TestCommandPartAction.EvaluateLabSnapshot("r", Live(liveData, next),
                Snapshot(snapData, "3.1"), 0.0).Token);
        }

        [Fact]
        public void EvaluateLabSnapshot_ToleranceIsAbsolute()
        {
            Assert.Equal("false", TestCommandPartAction.EvaluateLabSnapshot("r", Live(24.02, 0),
                Snapshot("24", "0"), 0.01).Token);
            Assert.Equal("true", TestCommandPartAction.EvaluateLabSnapshot("r", Live(24.02, 0),
                Snapshot("24", "0"), 0.05).Token);
        }

        [Fact]
        public void EvaluateLabSnapshot_NotALab_IsNone()
        {
            var live = Live(0, 0);
            live.HasLab = false;
            var o = TestCommandPartAction.EvaluateLabSnapshot("rec1", live, Snapshot("0", "0"), 0.01);
            Assert.Equal("none", o.Token);
            Assert.Empty(o.Mismatches);
            Assert.Equal(new[] { "partaction snapshot rec=rec1 partId=501 field=part match=none reason=no-lab-module" },
                o.Lines.ToArray());
        }

        [Fact]
        public void EvaluateLabSnapshot_UncomparableLabPart_IsFalseWithAReason()
        {
            var noSnapshot = TestCommandPartAction.EvaluateLabSnapshot("rec1", Live(0, 0), null, 0.01);
            Assert.Equal("false", noSnapshot.Token);
            Assert.Equal(new[] { "501:no-snapshot" }, noSnapshot.Mismatches.ToArray());
            Assert.EndsWith("field=part matchedBy=none match=false reason=no-snapshot", noSnapshot.Lines.Single());

            var live = Live(0, 0);
            live.PartId = 7;
            live.PersistentId = 8;
            live.Name = "Other.Lab";
            var notFound = TestCommandPartAction.EvaluateLabSnapshot("rec1", live, Snapshot("0", "0"), 0.01);
            Assert.Equal("false", notFound.Token);
            Assert.Equal(new[] { "7:part-not-in-snapshot" }, notFound.Mismatches.ToArray());

            // The mapped PART lost its lab module.
            var plain = TestCommandPartAction.ParseSnapshotParts(Vessel(PlainPart("mk1pod.v2", 500, 9000),
                PlainPart("Large.Crewed.Lab", 501, 9001)));
            var noLab = TestCommandPartAction.EvaluateLabSnapshot("rec1", Live(0, 0), plain, 0.01);
            Assert.Equal("false", noLab.Token);
            Assert.Equal(new[] { "501:no-lab-in-snapshot" }, noLab.Mismatches.ToArray());
        }

        [Fact]
        public void FoldSnapshotTokens_FalseWins_ThenTrue_ElseNone()
        {
            Assert.Equal("none", TestCommandPartAction.FoldSnapshotTokens(new string[0]));
            Assert.Equal("none", TestCommandPartAction.FoldSnapshotTokens(new[] { "none", "none" }));
            Assert.Equal("true", TestCommandPartAction.FoldSnapshotTokens(new[] { "none", "true" }));
            Assert.Equal("false", TestCommandPartAction.FoldSnapshotTokens(new[] { "true", "false", "none" }));
            Assert.Equal("none", TestCommandPartAction.FoldSnapshotTokens(null));
        }

        [Fact]
        public void SnapshotTail_AndPayload()
        {
            Assert.Equal(" snapshotMatch=none", TestCommandPartAction.FormatSnapshotTail(null));
            Assert.Equal(" snapshotMatch=false", TestCommandPartAction.FormatSnapshotTail("false"));
            var payload = TestCommandPartAction.SnapshotPayloadFields("false",
                new List<string> { "501:dataStored", "501:experiments" });
            Assert.Equal("snapshotMatch", payload[0].Key);
            Assert.Equal("false", payload[0].Value);
            Assert.Equal("snapshotMismatches", payload[1].Key);
            Assert.Equal("501:dataStored,501:experiments", payload[1].Value);
            Assert.Equal("none", TestCommandPartAction.SnapshotPayloadFields(null, null)[0].Value);
            Assert.Equal(string.Empty, TestCommandPartAction.SnapshotPayloadFields(null, null)[1].Value);
        }
    }
}
