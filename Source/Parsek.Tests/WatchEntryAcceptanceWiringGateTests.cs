using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Source gate for the WATCH-ENTRY-REFUSED-INSIDE-QUOTED-RANGE acceptance change.
    ///
    /// <para>Every behavioural cell in <see cref="WatchEntryAcceptanceTests"/> drives the two pure
    /// predicates directly. Both PRODUCTION consumers are unreachable headlessly -
    /// <c>WatchModeController.IsGhostOnSameBody</c> reads <c>FlightGlobals.ActiveVessel</c> and
    /// <c>Planetarium</c> on every call, and <c>TryStartWatchSession</c> is a live camera /
    /// visual-load path. So unwiring either one (dropping the trajectory resolution and going back
    /// to the raw cache read, or restoring the three-term <c>resetLoopPhaseForWatch</c>
    /// expression) restores the defect with the whole xUnit suite still green. This gate closes
    /// that silent-revert hole: it pins the WIRING, not behaviour the other file already covers.</para>
    ///
    /// <para>Mirrors <c>WatchModeTargetLossWiringGateTests</c> in mechanism (read the source, strip
    /// line comments, assert over one brace-matched method body). Comment stripping matters here:
    /// the change's own comments name the pre-change shapes on purpose, and a raw scan would read
    /// those pointers as code.</para>
    /// </summary>
    public class WatchEntryAcceptanceWiringGateTests
    {
        private const string ControllerPath = "WatchModeController.cs";

        // ---- (a) the body term resolves from the trajectory, not the stale cache ----

        [Fact]
        public void IsGhostOnSameBody_ResolvesTheBodyFromTheTrajectoryWhenTheCacheIsNotCurrent()
        {
            string body = MethodBody(ControllerPath, "internal bool IsGhostOnSameBody(");

            // The staleness dispatch, the positioning-free resolver, and the loop-mapped UT the
            // resolver has to be asked at. Any one of the three missing is the pre-change shape.
            Assert.Contains("IsWatchBodyReadingCurrent(", body);
            Assert.Contains("GhostPlaybackEngine.TryResolvePendingPlaybackInterpolation(", body);
            Assert.Contains("ResolveWatchPlaybackUT(", body);

            // The answer must come out of the pure core, so the evidence rule lives in exactly one
            // testable place and the decision line is emitted with it.
            Assert.Contains("ResolveAndLogWatchSameBodyDecision(", body);

            // THE REVERT THIS CELL EXISTS FOR: the whole method used to BE the raw cache read
            // `host.Engine.IsGhostOnBody(index, FlightGlobals.ActiveVessel?.mainBody?.name)`.
            // That engine method is still correct and still unit-tested - it just must not be
            // what this term answers with any more.
            Assert.False(body.Contains("host.Engine.IsGhostOnBody("),
                "watch-entry-acceptance gate: the same-body term must not go back to reading "
                + "GhostPlaybackEngine.IsGhostOnBody, whose lastInterpolatedBodyName is a spawn "
                + "seed for any ghost the render zone hides "
                + "(WATCH-ENTRY-REFUSED-INSIDE-QUOTED-RANGE).");

            // The resolution must be reached THROUGH the staleness dispatch, not run
            // unconditionally: a positioned ghost's cache is the freshest truth there is.
            Assert.True(
                body.IndexOf("IsWatchBodyReadingCurrent(", StringComparison.Ordinal)
                    < body.IndexOf(
                        "GhostPlaybackEngine.TryResolvePendingPlaybackInterpolation(",
                        StringComparison.Ordinal),
                "watch-entry-acceptance gate: the trajectory resolution is the fallback for a "
                + "ghost that is NOT being positioned - it must sit behind IsWatchBodyReadingCurrent.");
        }

        [Fact]
        public void EveryConsumerReachesTheTermThroughTheOneFixedForwarder()
        {
            // The term has one production definition and one forwarder. If a consumer ever grew
            // its own `Engine.IsGhostOnBody` read, it would answer from the stale cache while the
            // rest of the product answered from the trajectory - the exact split this change
            // exists to remove.
            // WALKED, NOT LISTED. The gate's whole purpose is catching GROWTH, and a hardcoded
            // roster of today's consumers is satisfied by definition when a new one is added
            // tomorrow. Everything under Source/Parsek is swept except the two files that are
            // ALLOWED to name the engine method - it is declared in one of them and the other
            // is the test-only in-game suite - and a new exemption has to be argued here.
            string root = ParsekSourceRoot();
            var exempt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // Declares GhostPlaybackEngine.IsGhostOnBody. Kept: still correct, still
                // unit-tested, just no longer what the watch-entry term answers with.
                "GhostPlaybackEngine.cs",
                // In-game (live-KSP) tests, not production consumers of the term.
                "RuntimeTests.cs",
            };

            var offenders = new List<string>();
            foreach (string path in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                // Skip build output - bin/obj can hold generated or stale copies.
                string rel = path.Substring(root.Length).TrimStart(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (exempt.Contains(Path.GetFileName(path)))
                    continue;

                if (StripComments(File.ReadAllText(path)).Contains("IsGhostOnBody("))
                    offenders.Add(rel);
            }

            Assert.True(offenders.Count == 0,
                "watch-entry-acceptance gate: these files reach the same-body answer through "
                + "the engine's raw cache read (GhostPlaybackEngine.IsGhostOnBody) instead of "
                + "WatchModeController.IsGhostOnSameBody, so they would answer from a ghost's "
                + "stale spawn seed while the rest of the product answers from its trajectory "
                + "(WATCH-ENTRY-REFUSED-INSIDE-QUOTED-RANGE): "
                + string.Join(", ", offenders.ToArray()));

            // ...and ParsekFlight's forwarder must still be a forwarder.
            string forwarder = StripComments(ReadParsekSource("ParsekFlight.cs"));
            Assert.Contains(
                "internal bool IsGhostOnSameBody(int index) => watchMode.IsGhostOnSameBody(index);",
                forwarder);
        }

        [Fact]
        public void TryResolveWatchEntryState_UsesTheForwarderRatherThanItsOwnInlineCacheRead()
        {
            // THE SECOND READER, and the one a `IsGhostOnSameBody` grep does not find. The
            // auto-select conjunction only picks an INDEX; this method is what
            // `EnterWatchMode(index)` itself consults, and it used to compare
            // `gs.lastInterpolatedBodyName` to the active body inline. Fixing only the selector
            // would make the selector accept a ghost this method then refused SILENTLY - the
            // seam verb would time out with an ERROR instead of returning a clean REJECTED,
            // which is strictly worse than the refusal being fixed (and reds V7M's
            // `\[Parsek\]\[ERROR\]` forbid).
            string body = MethodBody(ControllerPath, "private bool TryResolveWatchEntryState(");

            Assert.Contains("IsGhostOnSameBody(index)", body);
            Assert.False(body.Contains("gs.lastInterpolatedBodyName"),
                "watch-entry-acceptance gate: TryResolveWatchEntryState must not read the "
                + "ghost's cached body name directly - that is the stale spawn seed "
                + "(WATCH-ENTRY-REFUSED-INSIDE-QUOTED-RANGE). Route through IsGhostOnSameBody.");

            // The body check must still run ABOVE the distance guard, whose log line
            // W1-watch-distance-cutoff pins as a required token.
            int bodyCheck = body.IndexOf("IsGhostOnSameBody(index)", StringComparison.Ordinal);
            int distanceGuard = body.IndexOf("IsWithinWatchEntryRange(distMeters)", StringComparison.Ordinal);
            Assert.True(distanceGuard >= 0,
                "watch-entry-acceptance gate: the distance guard must still be evaluated here.");
            Assert.True(bodyCheck < distanceGuard,
                "watch-entry-acceptance gate: the body check must stay ABOVE the distance guard, "
                + "the order W1-watch-distance-cutoff's spec is derived against.");
        }

        [Fact]
        public void ResolveWatchPlaybackUT_KeysBothLoopHelpersOnItsParameterNotTheWatchedField()
        {
            // THE CROSS-INDEX BUG. `recIdx` is a DATA KEY in both helpers: TryGetLoopSchedule
            // indexes the committed list with it to resolve THIS recording's auto-loop launch
            // slot, and TryComputeLoopPlaybackUT indexes engine.loopPhaseOffsets with it.
            // Reading the `watchedRecordingIndex` FIELD resolved row Y's playback UT from row
            // X's schedule and phase offset - which seam 1 turned from a latent oddity into a
            // per-row, per-frame wrong answer, because the body term now calls in here for
            // every committed row. WatchEntryAcceptanceTests
            // .LoopScheduleIsKeyedByTheRowsOwnIndex_NotTheWatchedOne sizes the divergence; this
            // pins the wiring, which no headless cell can reach.
            string body = MethodBody(ControllerPath, "private double ResolveWatchPlaybackUT(");

            Assert.False(body.Contains("watchedRecordingIndex"),
                "watch-entry-acceptance gate: ResolveWatchPlaybackUT must key its loop helpers "
                + "on the recordingIndex PARAMETER, never on the watchedRecordingIndex FIELD - "
                + "the field names a different recording (or is -1) whenever the term is "
                + "evaluated for a row other than the watched one.");

            // Both helpers must actually receive it; a call that dropped the arg would fall back
            // to the -1 default and silently lose the auto-schedule.
            Assert.Contains("TryGetLoopScheduleForWatch(", body);
            Assert.Contains("recordingIndex,", body);
            Assert.Contains("out double loopUT, out _, out _, recordingIndex)", body);
        }

        // ---- (b) the loop-phase reset routes through the predicate ----

        [Fact]
        public void TryStartWatchSession_RoutesTheLoopPhaseResetThroughThePredicate()
        {
            string body = MethodBody(ControllerPath, "internal bool TryStartWatchSession(");

            Assert.Contains("ShouldResetLoopPhaseForWatch(", body);

            // Both new inputs must actually be MEASURED here. Passing constants would satisfy the
            // call above while restoring the pre-change decision.
            Assert.Contains("IsGhostOnSameBody(index)", body);

            // The distance must come through the PACKAGED fallback the entry gate uses, not a
            // raw `currentState.lastDistance` read - otherwise the reset-skip can diverge from
            // the gate that just admitted the entry after a future edit to either one.
            Assert.Contains("ResolveWatchDistanceMeters(currentState)", body);
            Assert.Contains("IsWithinWatchEntryRange(currentPhaseDistanceMeters)", body);
            Assert.False(body.Contains("IsWithinWatchEntryRange(currentState.lastDistance)"),
                "watch-entry-acceptance gate: the reset predicate's range term must read the "
                + "same distance the entry gate does (ResolveWatchDistanceMeters), not the raw "
                + "cached field.");

            // THE REVERT THIS CELL EXISTS FOR: the three-term inline expression.
            Assert.False(
                body.Contains("resetLoopPhaseForWatch = currentState.currentZone == RenderingZone.Beyond"),
                "watch-entry-acceptance gate: the loop-phase reset must not go back to the inline "
                + "zone/loops/overlap conjunction - it teleports the camera cross-body for an "
                + "observer at an arrival park, and the 305 km debounce then auto-exits within frames.");

            // The reset consumer must still read the predicate's answer, not recompute one.
            Assert.Contains("if (resetLoopPhaseForWatch)", body);
            Assert.Contains("ResetLoopPhaseForWatch(index, currentState, rec)", body);
        }

        [Fact]
        public void TryStartWatchSession_LogsWhichWayTheLoopPhaseDecisionWent()
        {
            // The decision is invisible from the outside otherwise: a skipped reset and a reset
            // that happened to land nearby look identical in a collected log.
            string body = MethodBody(ControllerPath, "internal bool TryStartWatchSession(");

            Assert.Contains("Watch loop-phase decision:", body);
            Assert.Contains("phaseSameBody=", body);
        }

        // ---- helpers (mirror WatchModeTargetLossWiringGateTests) ----

        private static string MethodBody(string relPath, string signatureFragment)
        {
            string src = StripComments(ReadParsekSource(relPath));

            int sigIdx = src.IndexOf(signatureFragment, StringComparison.Ordinal);
            Assert.True(sigIdx >= 0,
                "watch-entry-acceptance gate: signature not found in " + relPath + ": " + signatureFragment);
            Assert.True(
                src.IndexOf(signatureFragment, sigIdx + 1, StringComparison.Ordinal) < 0,
                "watch-entry-acceptance gate: signature is ambiguous in " + relPath + ": " + signatureFragment);

            int open = src.IndexOf('{', sigIdx);
            Assert.True(open >= 0,
                "watch-entry-acceptance gate: no method body found for " + signatureFragment);

            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{')
                    depth++;
                else if (src[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return src.Substring(open, i - open + 1);
                }
            }

            Assert.True(false, "watch-entry-acceptance gate: unbalanced braces after " + signatureFragment);
            return null;
        }

        private static string ParsekSourceRoot()
        {
            string repo = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string root = Path.Combine(repo, "Source", "Parsek");
            if (!Directory.Exists(root))
                root = Path.Combine(repo, "Parsek");
            Assert.True(Directory.Exists(root), "Parsek source root not found at " + root);
            return root;
        }

        private static string ReadParsekSource(string relPath)
        {
            string path = Path.Combine(
                ParsekSourceRoot(), relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path);
        }

        // Strip line comments (XML doc comments included - they start with //) so the change's own
        // notes, which name the pre-change shapes deliberately, do not satisfy or trip a scan.
        private static string StripComments(string source)
        {
            var sb = new StringBuilder(source.Length);
            foreach (string line in source.Split('\n'))
            {
                int idx = line.IndexOf("//", StringComparison.Ordinal);
                sb.Append(idx >= 0 ? line.Substring(0, idx) : line);
                sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
