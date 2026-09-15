using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
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
    /// <para>(2) AN IL GATE over the applier partial. The applier lives on a
    /// <c>MonoBehaviour</c> that reaches into <c>ParsekFlight</c>, so xUnit cannot call it,
    /// and the property that matters most about this pair is a NEGATIVE one the operator
    /// ruled (B4, 2026-09-15): it drives the EXISTING Gloops entry points and changes
    /// nothing about Gloops. Its CALL SET, read out of the compiled method with
    /// <see cref="ILCallSet"/>, is the mechanical witness for that - see the block comment
    /// above those cells for why it is read from the IL and not from the source text, and
    /// for the four mutants that shaped it.</para>
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

        // ----- Applier gate: the B4 no-Gloops-change ruling, read out of the IL -----
        //
        // AN ALLOWLIST, NOT A BLOCKLIST, and READ FROM THE COMPILED METHOD rather than from
        // its source text. Both halves of that were learned the hard way in review.
        //
        // The blocklist half: the first draft named four forbidden members and asserted the
        // two wanted ones were present, which four one-line mutants walked straight past -
        // `recorder.ForceStop()`, `flight.GloopsRecorderForUI.Recording.Clear()`,
        // `RecordingStore.DeleteRecordingFull(0)` and, against the derivation that replaced
        // it, `var r2 = flight.GloopsRecorderForUI; r2.ForceStop();`, which rebinds the
        // recorder to a name no text scan was looking for.
        //
        // The source-text half: a regex over source is the instrument the house rule warns
        // about (comments read as code and fail GREEN), and it has to model C# - locals,
        // chains, `?.`, casts - to answer a question the compiler has already answered. The
        // IL has no comments and no log strings in it, a deleted call site is VISIBLE, and a
        // rebound local is not a thing that survives compilation: `r2.ForceStop()` and
        // `recorder.ForceStop()` are the same `callvirt`. So these cells read the applier's
        // CALL SET with `ILCallSet` (which landed on main in the same window, PR #1698) and
        // compare it as a set.
        //
        // Mutation-tested 2026-09-15 on a scratch copy, FIVE mutants, all killed and each
        // naming the member it added: the four above plus `flight.DiscardGloopsInProgress()`
        // (the Gloops member one keystroke away from being wired here, which the B4 ruling
        // says stays unwired). One of them shaped the gate rather than merely passing
        // through it: the FIRST IL draft filtered to Parsek types and `Recording.Clear()`
        // survived, because `Clear` is declared on `List<TrajectoryPoint>` and not on any
        // Parsek type. Hence the three type families read as one allowlist below.

        /// <summary>The ONLY ParsekFlight members the appliers may call - exactly what the
        /// Gloops window's primary button calls, plus the three read-only accessors the
        /// verdict is derived from.</summary>
        private static readonly string[] AllowedParsekFlightCalls =
        {
            "StartGloopsRecording",
            "StopGloopsRecording",
            "get_IsGloopsRecording",
            "get_GloopsRecorderForUI",
            "get_LastGloopsRecording",
            // The singleton accessor both appliers open with. A read, and the only way to
            // reach the two entry points at all.
            "get_Instance",
        };

        [Fact]
        public void TheAppliersCallExactlyTheAllowedParsekFlightMembers()
        {
            var called = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string impl in new[] { "GloopsStartImpl", "GloopsStopImpl" })
            {
                foreach (MethodBase m in ILCallSet.CalledMethods(
                             ILCallSet.Method(typeof(ParsekTestCommandAddon), impl)))
                {
                    if (m.DeclaringType == typeof(ParsekFlight))
                        called.Add(m.Name);
                }
            }

            // A SET comparison, so a newly wired Gloops member reds by NAME whether or not
            // anyone thought to forbid it, and a DELETED entry point reds too.
            Assert.Equal(
                new SortedSet<string>(AllowedParsekFlightCalls, StringComparer.Ordinal),
                called);
        }

        [Fact]
        public void TheAppliersOnlyReadTheRecorderTheRecordingAndTheirCounts()
        {
            // Everything the appliers reach BELOW ParsekFlight, in one set: the recorder
            // (`ForceStop` / `StopRecording` would end a take), the committed `Recording`
            // (its id and its point list), and the point LIST itself - which is where the
            // mutant that survived the first IL draft lived. `Recording.Clear()` declares
            // `Clear` on `List<TrajectoryPoint>`, not on any Parsek type, so a gate filtered
            // to Parsek types alone never saw it. Reading all three type families as ONE
            // allowlist closes that: the list may be asked for its Count and nothing else.
            var called = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string impl in new[] { "GloopsStartImpl", "GloopsStopImpl" })
            {
                foreach (MethodBase m in ILCallSet.CalledMethods(
                             ILCallSet.Method(typeof(ParsekTestCommandAddon), impl)))
                {
                    Type t = m.DeclaringType;
                    if (t == null) continue;
                    bool isList = t.IsGenericType
                        && t.GetGenericTypeDefinition() == typeof(List<>);
                    if (t == typeof(FlightRecorder) || t == typeof(Recording) || isList)
                        called.Add(t.Name + "." + m.Name);
                }
            }
            Assert.Equal(
                new SortedSet<string>(
                    new[] { "FlightRecorder.get_Recording", "List`1.get_Count" },
                    StringComparer.Ordinal),
                called);

            // `Recording` reaches this set through FIELDS rather than properties
            // (`RecordingId` and `Points` are both public fields on it), which a call-set
            // gate cannot see at all - the hole ILCallSet's own header warns about. So the
            // read-field set is asserted beside the call set, and it is an allowlist too.
            var read = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string impl in new[] { "GloopsStartImpl", "GloopsStopImpl" })
            {
                foreach (FieldInfo f in ILCallSet.ReadFields(
                             ILCallSet.Method(typeof(ParsekTestCommandAddon), impl)))
                {
                    if (f.DeclaringType == typeof(Recording))
                        read.Add(f.Name);
                }
            }
            Assert.Equal(
                new SortedSet<string>(
                    new[] { "Points", "RecordingId" }, StringComparer.Ordinal),
                read);
        }

        [Fact]
        public void TheAppliersCallNoRecordingStoreMemberAtAll()
        {
            // Reaching PAST ParsekFlight into the store, where
            // `RecordingStore.DeleteRecordingFull(0)` would undo a Gloops take with no
            // Gloops-named member and no recorder local in sight. The appliers drive two
            // ParsekFlight methods and report; they touch no other Parsek subsystem, so the
            // allowlist here is EMPTY rather than curated.
            foreach (string impl in new[] { "GloopsStartImpl", "GloopsStopImpl" })
            {
                var storeCalls = ILCallSet.CalledMethods(
                        ILCallSet.Method(typeof(ParsekTestCommandAddon), impl))
                    .Where(m => m.DeclaringType == typeof(RecordingStore))
                    .Select(m => m.Name)
                    .ToList();
                Assert.Empty(storeCalls);
            }
        }

        [Fact]
        public void TheAppliersWriteNoGloopsFieldOrProperty()
        {
            // The assignment half. A property set is a `set_X` CALL (covered by the set
            // comparisons above, which allow only getters); a raw field write is a `stfld`
            // the call scan cannot see, so read the written-field set too.
            foreach (string impl in new[] { "GloopsStartImpl", "GloopsStopImpl" })
            {
                var written = ILCallSet.WrittenFields(
                        ILCallSet.Method(typeof(ParsekTestCommandAddon), impl))
                    .Where(f => f.DeclaringType == typeof(ParsekFlight)
                                || f.DeclaringType == typeof(FlightRecorder)
                                || f.Name.IndexOf("Gloops", StringComparison.Ordinal) >= 0)
                    .Select(f => f.Name)
                    .ToList();
                Assert.Empty(written);
            }
        }

        // ----- helpers -----

        private static string Lookup(List<KeyValuePair<string, string>> payload, string key)
        {
            KeyValuePair<string, string> hit = payload.FirstOrDefault(kv => kv.Key == key);
            Assert.False(string.IsNullOrEmpty(hit.Key), "payload has no key " + key);
            return hit.Value;
        }

    }
}
