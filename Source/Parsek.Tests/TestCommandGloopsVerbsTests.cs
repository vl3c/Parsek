using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The GloopsStart / GloopsStop seam pair (M-A2). Two groups of cells:
    ///
    /// <para>(1) The PURE half (<see cref="TestCommandGloopsVerbs"/>): the before/after
    /// classification and the payload builders. These are the whole decision surface -
    /// the applier only samples state and reports what these say.</para>
    ///
    /// <para>(2) A SOURCE GATE over the applier partial. The applier lives on a
    /// <c>MonoBehaviour</c> that reaches into <c>ParsekFlight</c>, so xUnit cannot call it,
    /// and the property that matters most about this pair is a NEGATIVE one the operator
    /// ruled (B4, 2026-09-15): it drives the EXISTING Gloops entry points and changes
    /// nothing about Gloops. A cell that reads the applier's source is the only mechanical
    /// witness for "it calls exactly StartGloopsRecording / StopGloopsRecording and no
    /// other Gloops mutator". Comments are stripped first (the applier's own header
    /// DISCUSSES DiscardGloopsInProgress and PreviewGloopsRecording as things it
    /// deliberately does not drive, so a scan that read comments would report calls that
    /// are not wired and fail against a source saying the opposite).</para>
    /// </summary>
    public class TestCommandGloopsVerbsTests
    {
        // ----- ClassifyStart -----

        [Fact]
        public void ClassifyStart_ReportsStarted_WhenTheRecorderWentLive()
        {
            Assert.Equal(
                TestCommandGloopsVerbs.StartOutcome.Started,
                TestCommandGloopsVerbs.ClassifyStart(
                    wasRecording: false, hadActiveVessel: true, isRecordingAfter: true));
        }

        [Fact]
        public void ClassifyStart_ReportsAlreadyRecording_BeforeAnythingElse()
        {
            // Production's FIRST branch returns without touching the recorder, so a
            // pre-existing recorder must be named as such even though the post-call
            // read-back ALSO says "recording" - which on its own is indistinguishable from
            // a fresh successful start.
            Assert.Equal(
                TestCommandGloopsVerbs.StartOutcome.AlreadyRecording,
                TestCommandGloopsVerbs.ClassifyStart(
                    wasRecording: true, hadActiveVessel: true, isRecordingAfter: true));
        }

        [Fact]
        public void ClassifyStart_SeparatesNoVesselFromARefusedStart()
        {
            // Two different production Warns ("no active vessel" vs "blocked (paused or no
            // vessel)"), and a spec author needs to know which: the first means the scene
            // is not what the spec thought, the second that the game was paused or the
            // recorder declined. Collapsing them would hide a paused instance behind a
            // vessel-resolution complaint.
            Assert.Equal(
                TestCommandGloopsVerbs.StartOutcome.NoActiveVessel,
                TestCommandGloopsVerbs.ClassifyStart(
                    wasRecording: false, hadActiveVessel: false, isRecordingAfter: false));
            Assert.Equal(
                TestCommandGloopsVerbs.StartOutcome.StartBlocked,
                TestCommandGloopsVerbs.ClassifyStart(
                    wasRecording: false, hadActiveVessel: true, isRecordingAfter: false));
        }

        // ----- ClassifyStop -----

        [Fact]
        public void ClassifyStop_ReportsNoRecorder_WhenThereWasNothingToStop()
        {
            Assert.Equal(
                TestCommandGloopsVerbs.StopOutcome.NoRecorder,
                TestCommandGloopsVerbs.ClassifyStop(
                    hadRecorder: false, committedIdChanged: false));
            // Even a stale LastGloopsRecording id changing could not resurrect a stop that
            // had no recorder: the recorder presence is checked first, as production does.
            Assert.Equal(
                TestCommandGloopsVerbs.StopOutcome.NoRecorder,
                TestCommandGloopsVerbs.ClassifyStop(
                    hadRecorder: false, committedIdChanged: true));
        }

        [Fact]
        public void ClassifyStop_SplitsCommitFromTheSubTwoPointDrop()
        {
            // The ONLY observable that separates them: both paths null the recorder, and
            // only a commit publishes a NEW LastGloopsRecording id.
            Assert.Equal(
                TestCommandGloopsVerbs.StopOutcome.Committed,
                TestCommandGloopsVerbs.ClassifyStop(
                    hadRecorder: true, committedIdChanged: true));
            Assert.Equal(
                TestCommandGloopsVerbs.StopOutcome.Dropped,
                TestCommandGloopsVerbs.ClassifyStop(
                    hadRecorder: true, committedIdChanged: false));
        }

        // ----- Reason mapping -----

        [Fact]
        public void TheDropIsNotARefusal()
        {
            // The load-bearing cell of this file. RecordingStore.CreateRecordingFromFlightData
            // refusing a < 2-point take is what the window's own Stop button does, so the
            // verb must terminate OK with committed=false rather than REJECTED - otherwise
            // the D1 sub-2-point-drop lane would have to declare its subject a driver fault.
            Assert.Null(TestCommandGloopsVerbs.StopRejectReason(
                TestCommandGloopsVerbs.StopOutcome.Dropped));
            Assert.Null(TestCommandGloopsVerbs.StopRejectReason(
                TestCommandGloopsVerbs.StopOutcome.Committed));
            Assert.Equal(
                TestCommandGloopsVerbs.NoRecorderReason,
                TestCommandGloopsVerbs.StopRejectReason(
                    TestCommandGloopsVerbs.StopOutcome.NoRecorder));
        }

        [Fact]
        public void EveryNonStartedStartOutcomeNamesADistinctReason()
        {
            Assert.Null(TestCommandGloopsVerbs.StartRejectReason(
                TestCommandGloopsVerbs.StartOutcome.Started));
            var reasons = new[]
            {
                TestCommandGloopsVerbs.StartRejectReason(
                    TestCommandGloopsVerbs.StartOutcome.AlreadyRecording),
                TestCommandGloopsVerbs.StartRejectReason(
                    TestCommandGloopsVerbs.StartOutcome.NoActiveVessel),
                TestCommandGloopsVerbs.StartRejectReason(
                    TestCommandGloopsVerbs.StartOutcome.StartBlocked),
            };
            Assert.All(reasons, r => Assert.False(string.IsNullOrEmpty(r)));
            // Distinct, because hlib maps each token to a driver-* subkind by exact string;
            // two outcomes sharing a token would report as one cause.
            Assert.Equal(reasons.Length, reasons.Distinct(StringComparer.Ordinal).Count());
        }

        // ----- Payloads -----

        [Fact]
        public void StartPayload_CarriesStartedAndTheVessel()
        {
            var p = TestCommandGloopsVerbs.BuildStartPayload("Airshow Pod");
            Assert.Equal("true", Lookup(p, "started"));
            Assert.Equal("Airshow Pod", Lookup(p, "vessel"));
            // A null vessel name must not produce a null wire value (the response formatter
            // percent-encodes a string, not a null).
            Assert.Equal(string.Empty, Lookup(TestCommandGloopsVerbs.BuildStartPayload(null), "vessel"));
        }

        [Fact]
        public void StopPayload_CommittedCarriesTheIdAndNoDroppedToken()
        {
            var p = TestCommandGloopsVerbs.BuildStopPayload(
                TestCommandGloopsVerbs.StopOutcome.Committed, 417, "rec-abc");
            Assert.Equal("true", Lookup(p, "committed"));
            Assert.Equal("417", Lookup(p, "points"));
            Assert.Equal("rec-abc", Lookup(p, "recordingId"));
            Assert.DoesNotContain(p, kv => kv.Key == "dropped");
        }

        [Fact]
        public void StopPayload_DroppedCarriesTheTooShortTokenAndNoId()
        {
            var p = TestCommandGloopsVerbs.BuildStopPayload(
                TestCommandGloopsVerbs.StopOutcome.Dropped, 1, null);
            Assert.Equal("false", Lookup(p, "committed"));
            Assert.Equal("1", Lookup(p, "points"));
            Assert.Equal(TestCommandGloopsVerbs.DroppedTooShort, Lookup(p, "dropped"));
            Assert.DoesNotContain(p, kv => kv.Key == "recordingId");
        }

        [Fact]
        public void StopPayload_PointsAreInvariant()
        {
            // The harness parses this number. A ro-RO / de-DE host must not print a group
            // separator into it.
            CultureInfo prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var p = TestCommandGloopsVerbs.BuildStopPayload(
                    TestCommandGloopsVerbs.StopOutcome.Committed, 1234567, "rec-x");
                Assert.Equal("1234567", Lookup(p, "points"));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        // ----- Verb-table membership -----

        [Fact]
        public void BothVerbsAreImplementedAndNeitherIsReserved()
        {
            foreach (string verb in new[] { "GloopsStart", "GloopsStop" })
            {
                Assert.Equal(TestCommandVerbClass.Implemented, TestCommandVerbs.Classify(verb));
                Assert.DoesNotContain(verb, TestCommandVerbs.ReservedVerbNames);
                // State-mutating: a Gloops take is a committed recording, so FlushAndQuit
                // must still save after one. (The NonMutatingVerbs allow-list is the
                // fail-safe direction - a verb forgotten there is treated as mutating -
                // but this pair earns the assertion because "ghost-only" reads like
                // "harmless" and it is not: the row lands in the committed store.)
                Assert.True(TestCommandVerbs.IsStateMutatingVerb(verb));
            }
        }

        // ----- Applier source gate (the B4 no-Gloops-change ruling) -----

        [Fact]
        public void TheApplierDrivesOnlyTheTwoExistingGloopsEntryPoints()
        {
            string src = ReadApplierSource();

            // The two production calls the Gloops window's primary button makes.
            Assert.Contains("StartGloopsRecording()", src);
            Assert.Contains("StopGloopsRecording()", src);

            // And NOTHING else that mutates Gloops state. Discard and Preview are real
            // ParsekFlight members one keystroke away from being wired here; the ruling is
            // that this pair stays the lifecycle + drop producer and does not grow into a
            // verb per button.
            foreach (string forbidden in new[]
                     {
                         "DiscardGloopsInProgress",
                         "DiscardLastGloopsRecording",
                         "PreviewGloopsRecording",
                         "CommitGloopsRecording",
                     })
            {
                Assert.DoesNotContain(forbidden, src);
            }
        }

        [Fact]
        public void TheApplierReadsGloopsStateAndNeverWritesIt()
        {
            // The observation surface: every Gloops member the applier touches beyond the
            // two calls above must be a READ. Derived from the source rather than asserted
            // in prose, because "it only reads" is exactly the claim a later edit breaks
            // silently.
            string src = ReadApplierSource();
            var assignments = Regex.Matches(src, @"\.(\w*Gloops\w*)\s*=[^=]")
                .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            Assert.Empty(assignments);
        }

        // ----- helpers -----

        private static string Lookup(List<KeyValuePair<string, string>> payload, string key)
        {
            KeyValuePair<string, string> hit = payload.FirstOrDefault(kv => kv.Key == key);
            Assert.False(string.IsNullOrEmpty(hit.Key), "payload has no key " + key);
            return hit.Value;
        }

        private static string ReadApplierSource()
        {
            string path = Path.Combine(
                ResolveRepoRoot(), "Source", "Parsek", "TestCommands",
                "ParsekTestCommandAddon.Gloops.cs");
            Assert.True(File.Exists(path),
                "the Gloops applier moved, this gate is vacuous: " + path);
            return ParsekDialogNamePrefixSourceGateTests.StripComments(File.ReadAllText(path));
        }

        private static string ResolveRepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "scripts"))
                    && Directory.Exists(Path.Combine(dir, "Source")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
        }
    }
}
