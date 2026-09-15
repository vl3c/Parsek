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
        //
        // AN ALLOWLIST, NOT A BLOCKLIST, and the distinction is the whole value of these
        // cells. The first draft named four forbidden members and asserted the two wanted
        // ones were present, which three one-line mutants walked straight past:
        // `recorder.ForceStop()`, `flight.GloopsRecorderForUI.Recording.Clear()` and
        // `RecordingStore.DeleteRecordingFull(0)` all changed Gloops state while naming
        // nothing on the list. A FOURTH mutant, found in review, walked past the FIRST
        // derivation too: `var r2 = flight.GloopsRecorderForUI; r2.ForceStop();` binds the
        // recorder to a name the hop scan was not looking for. That is why the local's name
        // is now DERIVED from its binding site, why a second binding site is itself a
        // failure, and why a terminator blocklist sits behind both as a backstop. Each was pasted into a SCRATCH COPY of the applier
        // and run through the cells below (2026-09-15): under the blocklist all three
        // PASSED; under the derivations below `ForceStop` and `Clear` red on the two-hop
        // member scan, `DeleteRecordingFull` reds on the empty store-call set, and the
        // rebound `r2.ForceStop()` reds on the single-binding cell AND the terminator
        // backstop (it passed every cell in the round before this one). The
        // mutants were removed from the scratch copy afterwards; the committed applier
        // never carried them. The two-hop shape is why `.Recording.Clear()` is caught at
        // all: the name scan wants a "(" right after the Gloops name and a chained call
        // does not have one.

        /// <summary>Members the applier is allowed to call on `ParsekFlight` - exactly the
        /// two the Gloops window's primary button calls.</summary>
        private static readonly string[] AllowedGloopsCalls =
        {
            "StartGloopsRecording",
            "StopGloopsRecording",
        };

        [Fact]
        public void TheApplierCallsExactlyTheTwoExistingGloopsEntryPointsAndNoOther()
        {
            string src = ReadApplierSource();

            // Every `.SomethingGloopsSomething(` call site in the file, as a SET compared
            // against the allowlist. A new Gloops member wired in reds here by NAME,
            // whether or not anyone thought to forbid it.
            var called = new SortedSet<string>(
                Regex.Matches(src, @"\.(\w*Gloops\w*)\s*\(")
                    .Cast<Match>().Select(m => m.Groups[1].Value),
                StringComparer.Ordinal);

            Assert.Equal(
                new SortedSet<string>(AllowedGloopsCalls, StringComparer.Ordinal),
                called);
        }

        [Fact]
        public void TheRecorderAccessorIsBoundToExactlyOneLocal()
        {
            // The premise the scan below rests on, asserted rather than assumed. That scan
            // has to name the local that holds the recorder, and a hand-written name is a
            // hole: `var r2 = flight.GloopsRecorderForUI; r2.ForceStop();` binds the same
            // object to a DIFFERENT name and walks past a scan keyed on `recorder`. So the
            // local's name is DERIVED from the one assignment that creates it, and a
            // second binding site reds here instead of silently widening the surface.
            string src = ReadApplierSource();
            var bindings = Regex.Matches(
                    src, @"(?:\bvar\b|\bFlightRecorder\b)\s+(\w+)\s*=\s*[\w.]*\bGloopsRecorderForUI\b")
                .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            Assert.Single(bindings);
            Assert.Equal("recorder", bindings[0]);

            // And the accessor is reached ONLY through that binding - never inline, where
            // a chained call would dodge the local scan entirely.
            Assert.Single(Regex.Matches(src, @"\bGloopsRecorderForUI\b").Cast<Match>().ToList());
        }

        [Fact]
        public void TheApplierOnlyReadsCountOffTheRecorderAndTheRecording()
        {
            // The mutant class the name scan above cannot see, in both of its shapes: a
            // call on the recorder LOCAL, whose own members carry no "Gloops" in their
            // spelling (`recorder.ForceStop()`), and a call reached THROUGH a Gloops
            // accessor rather than on it (`flight.GloopsRecorderForUI.Recording.Clear()` -
            // the name scan misses it because no "(" follows the Gloops name).
            //
            // The local's NAME is derived from its binding site rather than written here,
            // so renaming it in the applier moves this scan with it and a second binding
            // reds in the cell above.
            //
            // Derived in two hops. Hop one: every member reached off that local or off a
            // Gloops-named accessor. Hop two: every member reached off THOSE.
            string src = ReadApplierSource();
            string local = RecorderLocalName(src);

            var firstHop = new SortedSet<string>(
                Regex.Matches(src,
                        @"(?:\b" + local + @"\b|\.\w*Gloops\w*)\s*\??\.\s*(\w+)")
                    .Cast<Match>().Select(m => m.Groups[1].Value),
                StringComparer.Ordinal);
            Assert.Equal(
                new SortedSet<string>(
                    new[] { "Points", "Recording", "RecordingId" }, StringComparer.Ordinal),
                firstHop);

            var secondHop = new SortedSet<string>(
                Regex.Matches(src, @"\.(?:Recording|Points)\s*\??\.\s*(\w+)")
                    .Cast<Match>().Select(m => m.Groups[1].Value),
                StringComparer.Ordinal);
            Assert.Equal(
                new SortedSet<string>(new[] { "Count" }, StringComparer.Ordinal),
                secondHop);
        }

        [Fact]
        public void TheApplierNamesNoRecorderTerminatorAtAll()
        {
            // BELT AND BRACES over the derivations above, and cheap: the three members that
            // would end or empty a take are forbidden by NAME anywhere in the file, however
            // they are reached - through a local, a chain, a cast or a second accessor this
            // gate has not thought of. A blocklist is the wrong PRIMARY instrument (that is
            // the whole point of the header above) and a perfectly good backstop.
            string src = ReadApplierSource();
            foreach (string terminator in new[] { "ForceStop", "StopRecording", ".Clear(" })
                Assert.DoesNotContain(terminator, src);
        }

        [Fact]
        public void TheApplierCallsNoRecordingStoreMutator()
        {
            // The third mutant class: reaching PAST ParsekFlight into the store, where
            // `RecordingStore.DeleteGhostOnlyRecording(...)` would undo a Gloops take with
            // no Gloops-named member and no `recorder` local in sight. The applier's job is
            // to drive two ParsekFlight methods and report; it calls into no other Parsek
            // subsystem at all, so the allowlist here is EMPTY rather than curated.
            string src = ReadApplierSource();
            var storeCalls = Regex.Matches(src, @"\bRecordingStore\s*\.\s*(\w+)")
                .Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            Assert.Empty(storeCalls);
        }

        [Fact]
        public void TheApplierWritesNoGloopsField()
        {
            // The assignment half, kept from the first draft because it covers what a call
            // scan cannot: a field or property SET rather than an invocation.
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

        /// <summary>The name of the local the applier binds `GloopsRecorderForUI` to, read
        /// off the binding itself so a rename cannot leave a scan pointing at nothing.</summary>
        private static string RecorderLocalName(string src)
        {
            Match m = Regex.Match(
                src, @"(?:\bvar\b|\bFlightRecorder\b)\s+(\w+)\s*=\s*[\w.]*\bGloopsRecorderForUI\b");
            Assert.True(m.Success, "the applier no longer binds GloopsRecorderForUI to a local");
            return m.Groups[1].Value;
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
