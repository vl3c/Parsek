using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Parsek.Reaim;

namespace Parsek.InGameTests
{
    // Phase 3c end-to-end re-aim verification, made DETERMINISTIC for M-MIS-1
    // (docs/dev/plans/reaim-resolver-reliability.md). Where the C2 canary proves a SINGLE
    // synthesized transfer can encounter Duna, these tests prove the CONGRUENT-WINDOW model end
    // to end: build a re-aim plan + synodic schedule anchored on a known-good Kerbin->Duna
    // departure, then drive the live ReaimPlaybackResolver.TryResolveWindowSegments for several
    // consecutive synodic windows.
    //
    // DETERMINISM (the M-MIS-1 fix): the old test seeded its departure scan off the live
    // Planetarium.GetUniversalTime() and took the FIRST departure that synthesized. That made
    // every run a different geometry (irreproducible failures) and parked the chosen departure
    // on the leading EDGE of the feasibility band by construction (first success in scan order),
    // where eccentric-target drift across synodic windows (Duna ecc 0.051 breaks exact synodic
    // recurrence) pushes later windows outside the resolver's +-6% recorded-tof search. Now the
    // scan base is a PINNED constant: stock ephemerides are functions of UT and nothing in the
    // driven path reads the live clock (the resolver's currentUT is synthesized from the
    // schedule fields).
    //
    // Determinism is PER-FRAME, not absolute (measured, 2026-06-10 in-game run): KSP re-bases
    // each body orbit's epoch/meanAnomalyAtEpoch every frame, so positions at a FIXED UT carry
    // ~1e-15 relative frame-dependent rounding noise. Within one frame results are exactly
    // reproducible (the band-edge test's cache-cleared re-solve check); across frames only
    // KNIFE-EDGE scan entries flip (observed feasible=17..20 of 48, edge index flickering 1<->2,
    // while the contiguous band and its center index stayed fixed in every invocation). The
    // tests are insensitive by design: the strict test picks the band CENTER (stable), and the
    // band-edge test asserts the resolve-or-decline-cleanly CONTRACT, not which departure is the
    // edge.
    //
    // Four tests:
    //  1. Strict (mid-band): every window must resolve a sane re-aimed transfer, and the
    //     orientation must rotate across windows - asserted on the LONGITUDE OF PERIAPSIS
    //     (LAN + AoP), because the plane-constrained transfer is near-equatorial and LAN alone
    //     is degenerate there (the old "lan0=lanLast=0.00" intermittent failure).
    //  2. Band-edge (weak contract): the deterministic pin of the old test's accidental
    //     band-edge pick. Each window must either resolve sane segments or DECLINE CLEANLY to
    //     faithful (null segments, correct window index), deterministically. Makes NO claim
    //     that any window resolves; all-decline is a valid fail-closed outcome.
    //  3. Observed-failure pin (2026-06-11): the exact departure UT reconstructed from the one
    //     observed in-game "window k must resolve" failure with a recoverable geometry, driven
    //     under the same weak contract (M-MIS-1 item 1: pin at least one UT from each observed
    //     failure mode). See ObservedEdgeDepartureUT.
    //  4. Feasibility sweep (manual-only diagnostic): the M-MIS-1 "measure before knob math"
    //     artifact - maps the whole departure band x window grid into KSP.log.
    //
    // All tests run at the Space Center (no vessel), use PRIVATE resolver instances (never
    // ReaimPlaybackResolver.Shared, so the real playback cache is untouched), and skip cleanly
    // on a non-stock body graph.
    public class ReaimEndToEndInGameTest
    {
        // Arbitrary fixed scan base (roughly Kerbin year 24). NOT derived from any observed
        // failure; any fixed value gives full determinism (see header). Changing it changes the
        // pinned geometry, so treat it as part of the test contract.
        private const double PinnedScanBaseUT = 5000000.0;
        private const int ScanSteps = 48;
        private const int WindowsToCheck = 5;

        // The one OBSERVED in-game window-resolution failure with a fully reconstructable
        // geometry (2026-06-11 18:06 SPACECENTER batch, logs `2026-06-11_1811_m1-ingame-tests`,
        // KSP.log ~:9952). That session ran a PRE-#1116 DLL (logistics branch base, proven by
        // the in-log behavioral signatures - see plan section 10),
        // i.e. the OLD live-UT-seeded strict test: it scanned from live UT ~5.5s, took the
        // FIRST success at one scan step (synodic/48) above it - the leading band EDGE by
        // construction - and asserted every window resolves. Windows 0/1 resolved (w1 stretched
        // tof +5.0%, inside the +-6% search); window 2 (departUT=39700685.32 = dep + 2*synodic)
        // declined across the WHOLE +-6% recorded-tof search with the retrograde-branch
        // direction mismatch (inc=180.00: Duna's eccentric drift pushed the window's transfer
        // angle past 180 deg, so every plane-PROJECTED Lambert candidate travelled the wrong
        // way; ReaimTransferSynthesizer rejected it and the resolver fell back to faithful).
        //
        // ROOT CAUSE (near-180 handedness fix): that decline was NOT infeasibility - it was a
        // HANDEDNESS FLIP. The old code projected r2 onto the launch plane and chose the
        // prograde/retrograde branch by sign(cross.z) of the flattened r1 x r2, which rides
        // rounding noise near 180 deg and selected the inc=180 retrograde branch. The plan
        // section 10 "unresolvable-by-design" classification of THIS handedness mode is now
        // OBSOLETE. With the launch-plane normal threaded as the Lambert handedness axis and the
        // UN-projected r2 endpoint, the near-180 window CONVERGES prograde at the nominal departure
        // (step 0) - the regression the offline UvLambert.Solve_AntipodalNear180_* test proves and
        // the requireWindow0Resolve hard assert below pins. The later synodic windows are EXPECTED
        // to resolve too (all-R map), but a later decline can still come from the SEPARATE, open
        // M-MIS-3 eccentric-drift tof-band mode (independent of handedness), so the all-windows
        // claim stays a SOFT/observational one (requireAllWindowsResolve:false) until a live
        // in-game Periodicity batch confirms all-R.
        private const double ObservedEdgeDepartureUT = 409290.81937705079;

        private sealed class ScanContext
        {
            public CelestialBody Kerbin;
            public CelestialBody Duna;
            public double TofSeconds;          // Hohmann tof (stands in for the recorded tof)
            public double SynodicSeconds;
            public bool[] Scan;                // per scan step: window-0 transfer synthesizes?
            public double[] ScanDepartureUTs;  // per scan step: candidate departure UT
        }

        [InGameTest(Category = "Periodicity", Scene = GameScenes.SPACECENTER,
            Description = "Re-aim end-to-end (deterministic, mid-band departure): every synodic window resolves a sane re-aimed Kerbin->Duna transfer whose orientation rotates (congruent-window model)")]
        public void Reaim_KerbinToDuna_EveryWindowResolvesSaneTransfer()
        {
            var ic = CultureInfo.InvariantCulture;
            ScanContext ctx = BuildPinnedScanOrSkip();
            if (ctx == null)
                return;

            int midIdx = ReaimFeasibilityScan.CenterOfLongestRunIndex(ctx.Scan, cyclic: true);
            if (midIdx < 0)
            {
                InGameAssert.Skip("no good Kerbin->Duna departure found in the pinned synodic scan (unexpected on stock)");
                return;
            }
            double goodDep = ctx.ScanDepartureUTs[midIdx];

            BuildMemberAndPlan(ctx, goodDep,
                out List<OrbitSegment> memberSegments, out ReaimMissionPlan plan,
                out double spanStart, out double spanEnd);
            ReaimWindowPlanner.ReaimWindowSchedule sched = ReaimWindowPlanner.Plan(
                ctx.Kerbin.orbit.period, ctx.Duna.orbit.period, goodDep, ctx.TofSeconds,
                spanStart, spanEnd, referenceUT: goodDep - 1.0);
            InGameAssert.IsTrue(sched.Valid, "window schedule must be valid: " + (sched.Reason ?? ""));
            InGameAssert.IsTrue(System.Math.Abs(sched.FirstDepartureUT - goodDep) < 1.0,
                "window 0 should be the pinned departure");

            // Drive the resolver for several consecutive windows; assert EVERY one resolves a
            // sane 3-segment re-aimed trajectory. Private resolver instance: clean cache, and
            // the Shared playback cache stays untouched.
            var resolver = new ReaimPlaybackResolver();
            string memberId = "reaim-e2e-mid-" + midIdx.ToString(ic);
            double span = spanEnd - spanStart;
            int resolved = 0;
            double firstEcc = double.NaN, firstSma = double.NaN;
            double firstInc = double.NaN, lastInc = double.NaN;
            double firstLan = double.NaN, lastLan = double.NaN;
            double firstAop = double.NaN, lastAop = double.NaN;
            for (long k = 0; k < WindowsToCheck; k++)
            {
                // A synthetic live UT inside window k's recorded span (phaseAnchor + k*cadence + mid-span).
                double currentUT = sched.PhaseAnchorUT + k * sched.CadenceSeconds + 0.5 * span;
                bool ok = resolver.TryResolveWindowSegments(
                    memberId, memberSegments, plan, sched,
                    sched.PhaseAnchorUT, spanStart, spanEnd, sched.CadenceSeconds, currentUT,
                    out List<OrbitSegment> segs, out long windowIndex);

                InGameAssert.IsTrue(ok, $"window k={k} must resolve a re-aimed transfer (congruent-window model, mid-band departure)");
                InGameAssert.AreEqual(k, windowIndex, $"resolved window index must equal k={k}");
                AssertSaneWindowSegments(segs, plan, goodDep, spanEnd, k);

                OrbitSegment transfer = segs[1];
                if (k == 0)
                {
                    firstEcc = transfer.eccentricity;
                    firstSma = transfer.semiMajorAxis;
                    firstInc = transfer.inclination;
                    firstLan = transfer.longitudeOfAscendingNode;
                    firstAop = transfer.argumentOfPeriapsis;
                }
                lastInc = transfer.inclination;
                lastLan = transfer.longitudeOfAscendingNode;
                lastAop = transfer.argumentOfPeriapsis;
                resolved++;
            }

            double firstLpe = TransferWindowMath.LongitudeOfPeriapsisDegrees(firstLan, firstAop);
            double lastLpe = TransferWindowMath.LongitudeOfPeriapsisDegrees(lastLan, lastAop);
            ParsekLog.Info("ReaimE2E",
                $"Kerbin->Duna congruent windows (pinned mid-band): checked={WindowsToCheck} resolved={resolved} " +
                $"scanIdx={midIdx} goodDep={goodDep.ToString("F1", ic)} tof={ctx.TofSeconds.ToString("F0", ic)} synodic={ctx.SynodicSeconds.ToString("F0", ic)} " +
                $"ecc0={firstEcc.ToString("R", ic)} sma0={firstSma.ToString("R", ic)} " +
                $"w0 inc={firstInc.ToString("F4", ic)} lan={firstLan.ToString("F2", ic)} aop={firstAop.ToString("F2", ic)} lpe={firstLpe.ToString("F2", ic)} | " +
                $"wLast inc={lastInc.ToString("F4", ic)} lan={lastLan.ToString("F2", ic)} aop={lastAop.ToString("F2", ic)} lpe={lastLpe.ToString("F2", ic)}");

            InGameAssert.AreEqual(WindowsToCheck, resolved,
                "every congruent synodic window must resolve a sane re-aimed transfer (the model's core claim)");
            // Re-aimed, not transplanted: the conic SHAPE is preserved across windows (congruent)
            // while the inertial ORIENTATION rotates (it aims at where Duna actually is each
            // window). Asserted on the longitude of periapsis (LAN + AoP): the plane-constrained
            // transfer is near-equatorial, where LAN alone is degenerate (KSP pins it to 0 with a
            // +X default node - the old intermittent lan0=lanLast=0.00 failure), while LAN + AoP
            // stays the well-defined periapsis longitude. Wrapped delta so a 360 wrap cannot mask
            // or fake the rotation.
            double lpeDelta = System.Math.Abs(TransferWindowMath.ClampDegrees180(lastLpe - firstLpe));
            InGameAssert.IsTrue(lpeDelta > 1.0,
                $"the transfer orientation must rotate across windows (longitude of periapsis delta={lpeDelta.ToString("F2", ic)} deg; lpe0={firstLpe.ToString("F2", ic)} lpeLast={lastLpe.ToString("F2", ic)})");
        }

        [InGameTest(Category = "Periodicity", Scene = GameScenes.SPACECENTER,
            Description = "Re-aim end-to-end (deterministic, band-edge departure): every synodic window either resolves a sane transfer or declines cleanly to faithful, with identical outcome on re-solve")]
        public void Reaim_KerbinToDuna_BandEdgeWindows_ResolveOrDeclineCleanly()
        {
            var ic = CultureInfo.InvariantCulture;
            ScanContext ctx = BuildPinnedScanOrSkip();
            if (ctx == null)
                return;

            int edgeIdx = ReaimFeasibilityScan.FirstSuccessIndex(ctx.Scan);
            if (edgeIdx < 0)
            {
                InGameAssert.Skip("no good Kerbin->Duna departure found in the pinned synodic scan (unexpected on stock)");
                return;
            }
            double edgeDep = ctx.ScanDepartureUTs[edgeIdx];

            // The WEAK (designed) contract at the band edge: per window, resolve sane segments OR
            // decline cleanly to faithful (fail closed, never garbage), and do so DETERMINISTICALLY
            // (cache-cleared re-solve gives the identical outcome). This is the pinned regression
            // case for the old intermittent "window k must resolve" failures: those runs had
            // accidentally picked this band-edge departure off the live clock. Deliberately makes
            // NO claim that any window resolves here; the strong claim is test 1's, on the
            // mid-band departure.
            DriveWindowsResolveOrDeclineCleanly(ctx, edgeDep, "reaim-e2e-edge-" + edgeIdx.ToString(ic),
                requireWindow0Resolve: false, requireAllWindowsResolve: false,
                out string map, out int resolvedCount, out int declinedCount);

            ParsekLog.Info("ReaimE2E",
                $"Kerbin->Duna band-edge windows (pinned): scanIdx={edgeIdx} edgeDep={edgeDep.ToString("F1", ic)} " +
                $"map={map} resolved={resolvedCount} declined={declinedCount} of {WindowsToCheck} " +
                $"(decline = clean faithful fall-back; all-decline is a valid fail-closed outcome)");
        }

        [InGameTest(Category = "Periodicity", Scene = GameScenes.SPACECENTER,
            Description = "Re-aim end-to-end (pinned observed 2026-06-11 failure geometry): the near-180 handedness fix resolves the nominal departure prograde (the previous inc=180 retrograde-branch decline); later windows resolve or decline cleanly, expected all-R")]
        public void Reaim_KerbinToDuna_ObservedEdgeDeparture_ResolvesOrDeclinesCleanly()
        {
            var ic = CultureInfo.InvariantCulture;
            ScanContext ctx = BuildGeometryOrSkip();
            if (ctx == null)
                return;

            // M-MIS-1 item 1 + the near-180 handedness fix: a pinned departure UT from the OBSERVED
            // failure mode (see the ObservedEdgeDepartureUT constant for the full reconstruction).
            // BEFORE the fix window k=2 declined with the inc=180 retrograde-branch mismatch; the root
            // cause was a HANDEDNESS FLIP, not infeasibility.
            //
            // CONTRACT (deliberately split - the strong all-R claim is NOT yet live-verified):
            //  - HARD (requireWindow0Resolve): window 0 is the nominal departure at the exact near-180
            //    geometry that used to flip retrograde. The handedness fix makes it converge prograde -
            //    this is the regression the offline UvLambert test (Solve_AntipodalNear180_*) proves, so
            //    it is asserted hard here too.
            //  - SOFT (requireAllWindowsResolve = false): the later synodic windows are EXPECTED to all
            //    resolve too (the R/d map should read all-R), but a decline on a later window can come
            //    from the SEPARATE, still-open M-MIS-3 eccentric-drift tof-band mode (Duna's true anomaly
            //    drifts the required tof outside the resolver's +-6% search), which the handedness fix does
            //    NOT address. That is a clean fail-closed fall-back to faithful, not a handedness
            //    regression, so it must NOT hard-fail this test. The logged R/d map is the live-run
            //    confirmation surface: when an in-game Periodicity batch reads all-R, the strong claim is
            //    confirmed and this can be promoted to requireAllWindowsResolve:true.
            DriveWindowsResolveOrDeclineCleanly(ctx, ObservedEdgeDepartureUT, "reaim-e2e-observed-20260611",
                requireWindow0Resolve: true, requireAllWindowsResolve: false,
                out string map, out int resolvedCount, out int declinedCount);

            ParsekLog.Info("ReaimE2E",
                $"Kerbin->Duna observed-failure departure (pinned 2026-06-11): depUT={ObservedEdgeDepartureUT.ToString("R", ic)} " +
                $"map={map} resolved={resolvedCount} declined={declinedCount} of {WindowsToCheck} " +
                $"(near-180 handedness fix: the nominal-departure inc=180 retrograde-branch decline is now resolved prograde; " +
                $"map expected all-R, a later 'd' is the separate M-MIS-3 eccentric-drift mode falling back cleanly, not a handedness regression)");
        }

        // Drives WindowsToCheck consecutive windows for one departure. The base contract (shared by the
        // band-edge test) is the WEAK (designed) one: per window, resolve sane segments OR decline
        // cleanly to faithful (null segments, correct window index), DETERMINISTICALLY (a cache-cleared
        // re-solve reproduces the outcome and the transfer conic). With requireWindow0Resolve, window 0
        // (the recorded departure itself) must additionally resolve - the premise check for a departure
        // known to synthesize. requireAllWindowsResolve is the STRONG contract switch (EVERY window must
        // resolve a sane prograde re-aimed transfer; any decline fails): it is wired but currently passed
        // false at both call sites, pending a live in-game Periodicity batch confirming the observed-pin
        // R/d map reads all-R (a later 'd' can be the separate, still-open M-MIS-3 eccentric-drift mode,
        // not a handedness regression). Promote to true only after that live confirmation.
        private static void DriveWindowsResolveOrDeclineCleanly(
            ScanContext ctx, double departureUT, string memberId, bool requireWindow0Resolve,
            bool requireAllWindowsResolve,
            out string outcomeMap, out int resolvedCount, out int declinedCount)
        {
            BuildMemberAndPlan(ctx, departureUT,
                out List<OrbitSegment> memberSegments, out ReaimMissionPlan plan,
                out double spanStart, out double spanEnd);
            ReaimWindowPlanner.ReaimWindowSchedule sched = ReaimWindowPlanner.Plan(
                ctx.Kerbin.orbit.period, ctx.Duna.orbit.period, departureUT, ctx.TofSeconds,
                spanStart, spanEnd, referenceUT: departureUT - 1.0);
            InGameAssert.IsTrue(sched.Valid, "window schedule must be valid: " + (sched.Reason ?? ""));

            var resolver = new ReaimPlaybackResolver();
            double span = spanEnd - spanStart;
            resolvedCount = 0;
            declinedCount = 0;
            var map = new StringBuilder(WindowsToCheck);
            for (long k = 0; k < WindowsToCheck; k++)
            {
                double currentUT = sched.PhaseAnchorUT + k * sched.CadenceSeconds + 0.5 * span;
                bool ok = resolver.TryResolveWindowSegments(
                    memberId, memberSegments, plan, sched,
                    sched.PhaseAnchorUT, spanStart, spanEnd, sched.CadenceSeconds, currentUT,
                    out List<OrbitSegment> segs, out long windowIndex);
                InGameAssert.AreEqual(k, windowIndex, $"window index must equal k={k} on resolve AND on decline");
                if (ok)
                {
                    AssertSaneWindowSegments(segs, plan, departureUT, spanEnd, k);
                    resolvedCount++;
                    map.Append('R');
                }
                else
                {
                    InGameAssert.IsTrue(!(requireWindow0Resolve && k == 0),
                        "window k=0 (the recorded departure itself) must resolve a re-aimed transfer");
                    InGameAssert.IsTrue(!requireAllWindowsResolve,
                        $"window k={k} must RESOLVE a sane prograde re-aimed transfer after the near-180 handedness fix " +
                        "(the observed inc=180 retrograde-branch decline is no longer the designed outcome - it was a handedness flip)");
                    InGameAssert.IsTrue(segs == null,
                        $"window k={k} decline must return null segments (clean fall-back to faithful)");
                    declinedCount++;
                    map.Append('d');
                }

                // Determinism: a cache-cleared re-solve of the SAME window must reproduce the
                // outcome (and the same transfer conic when resolved). Also locks cache-vs-fresh
                // equivalence.
                resolver.Clear();
                bool ok2 = resolver.TryResolveWindowSegments(
                    memberId, memberSegments, plan, sched,
                    sched.PhaseAnchorUT, spanStart, spanEnd, sched.CadenceSeconds, currentUT,
                    out List<OrbitSegment> segs2, out long windowIndex2);
                InGameAssert.IsTrue(ok == ok2,
                    $"window k={k} outcome must be deterministic (first={ok} re-solve={ok2})");
                InGameAssert.AreEqual(windowIndex, windowIndex2, $"window k={k} re-solve index must match");
                if (ok && ok2)
                {
                    OrbitSegment a = segs[1], b = segs2[1];
                    InGameAssert.IsTrue(
                        NearlyEqual(a.semiMajorAxis, b.semiMajorAxis) && NearlyEqual(a.eccentricity, b.eccentricity)
                        && NearlyEqual(a.inclination, b.inclination)
                        && NearlyEqual(a.longitudeOfAscendingNode, b.longitudeOfAscendingNode)
                        && NearlyEqual(a.argumentOfPeriapsis, b.argumentOfPeriapsis),
                        $"window k={k} re-solved transfer conic must match the first solve");
                }
            }
            outcomeMap = map.ToString();
        }

        // The M-MIS-1 "measure before knob math" artifact: maps the WHOLE pinned departure scan
        // (one synodic period, 48 steps) x 5 windows into KSP.log. Manual-only: tens of seconds
        // of Lambert/CalculatePatch work, not for the shared batch. Per-departure lines are the
        // measurement product (bounded by the feasible-band size, typically a fraction of 48);
        // infeasible departures are skipped (their window-0 synth already failed in the scan) and
        // appear only in the final band map.
        [InGameTest(Category = "Periodicity", Scene = GameScenes.SPACECENTER,
            AllowBatchExecution = false,
            BatchSkipReason = "diagnostic measurement sweep (M-MIS-1); run manually from the test runner",
            Description = "Re-aim feasibility sweep: per-departure x per-window resolve/decline map for the pinned Kerbin->Duna scan (measurement artifact, manual-only)")]
        public IEnumerator Reaim_KerbinToDuna_FeasibilitySweep_Diagnostic()
        {
            var ic = CultureInfo.InvariantCulture;
            ScanContext ctx = BuildPinnedScanOrSkip();
            if (ctx == null)
                yield break;

            int feasible = 0;
            var bandMap = new StringBuilder(ScanSteps);
            for (int i = 0; i < ScanSteps; i++)
            {
                bandMap.Append(ctx.Scan[i] ? 'X' : '.');
                if (ctx.Scan[i])
                    feasible++;
            }
            int firstIdx = ReaimFeasibilityScan.FirstSuccessIndex(ctx.Scan);
            int centerIdx = ReaimFeasibilityScan.CenterOfLongestRunIndex(ctx.Scan, cyclic: true);
            var perWindowResolved = new int[WindowsToCheck];

            for (int i = 0; i < ScanSteps; i++)
            {
                if (!ctx.Scan[i])
                    continue;
                double dep = ctx.ScanDepartureUTs[i];
                BuildMemberAndPlan(ctx, dep,
                    out List<OrbitSegment> memberSegments, out ReaimMissionPlan plan,
                    out double spanStart, out double spanEnd);
                ReaimWindowPlanner.ReaimWindowSchedule sched = ReaimWindowPlanner.Plan(
                    ctx.Kerbin.orbit.period, ctx.Duna.orbit.period, dep, ctx.TofSeconds,
                    spanStart, spanEnd, referenceUT: dep - 1.0);
                if (!sched.Valid)
                {
                    ParsekLog.Warn("ReaimE2E", $"sweep dep={i}/{ScanSteps} schedule invalid ({sched.Reason}) - skipped");
                    continue;
                }

                var resolver = new ReaimPlaybackResolver();
                string memberId = "reaim-e2e-sweep-" + i.ToString(ic);
                double span = spanEnd - spanStart;
                var map = new StringBuilder(WindowsToCheck);
                int resolvedCount = 0;
                for (long k = 0; k < WindowsToCheck; k++)
                {
                    double currentUT = sched.PhaseAnchorUT + k * sched.CadenceSeconds + 0.5 * span;
                    bool ok = resolver.TryResolveWindowSegments(
                        memberId, memberSegments, plan, sched,
                        sched.PhaseAnchorUT, spanStart, spanEnd, sched.CadenceSeconds, currentUT,
                        out List<OrbitSegment> _, out long _);
                    if (ok)
                    {
                        resolvedCount++;
                        perWindowResolved[k]++;
                    }
                    map.Append(ok ? 'R' : 'd');
                }
                ParsekLog.Info("ReaimE2E",
                    $"sweep dep={i}/{ScanSteps} depUT={dep.ToString("F1", ic)} windows={map} resolved={resolvedCount}/{WindowsToCheck}");
                yield return null; // keep the scene responsive; progress is observable per departure
            }

            var perWindow = new StringBuilder();
            for (int k = 0; k < WindowsToCheck; k++)
                perWindow.Append(k == 0 ? "k0=" : $" k{k.ToString(ic)}=").Append(perWindowResolved[k].ToString(ic));
            ParsekLog.Info("ReaimE2E",
                $"sweep summary: feasible={feasible}/{ScanSteps} band={bandMap} first={firstIdx} center={centerIdx} " +
                $"perWindowResolved {perWindow} (resolver declines are the fail-closed faithful path; " +
                $"this map is the M-MIS-1 measurement input for the widen-vs-classify decision)");
        }

        // Resolves the stock Kerbin/Duna geometry (bodies, Hohmann tof, synodic period) shared by
        // all four tests, or returns null after InGameAssert.Skip on a non-stock body graph /
        // degenerate geometry. Scan fields stay null; the scan-driven tests fill them via
        // BuildPinnedScanOrSkip.
        private static ScanContext BuildGeometryOrSkip()
        {
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == "Kerbin");
            CelestialBody duna = FlightGlobals.Bodies?.Find(b => b.bodyName == "Duna");
            if (kerbin == null || duna == null)
            {
                InGameAssert.Skip("Kerbin/Duna not in FlightGlobals.Bodies (non-stock pack)");
                return null;
            }
            if (kerbin.referenceBody == null || kerbin.referenceBody != duna.referenceBody)
            {
                InGameAssert.Skip("Kerbin and Duna do not share a parent in this pack");
                return null;
            }

            double muSun = kerbin.referenceBody.gravParameter;
            double tof = TransferWindowMath.HohmannTransferTimeSeconds(
                kerbin.orbit.semiMajorAxis, duna.orbit.semiMajorAxis, muSun);
            double synodic = TransferWindowMath.SynodicPeriodSeconds(kerbin.orbit.period, duna.orbit.period);
            InGameAssert.IsTrue(tof > 0.0 && !double.IsInfinity(synodic) && synodic > 0.0,
                "Hohmann tof + synodic period must be finite/positive");

            return new ScanContext
            {
                Kerbin = kerbin,
                Duna = duna,
                TofSeconds = tof,
                SynodicSeconds = synodic
            };
        }

        // Builds the pinned deterministic scan on top of the shared geometry, or returns null
        // after InGameAssert.Skip.
        private static ScanContext BuildPinnedScanOrSkip()
        {
            ScanContext ctx = BuildGeometryOrSkip();
            if (ctx == null)
                return null;

            // One pinned synodic period of candidate departures: which synthesize a window-0
            // transfer (Hohmann tof, prograde)? Batch-counted; per-candidate detail is the sweep
            // test's job.
            var scan = new bool[ScanSteps];
            var depUTs = new double[ScanSteps];
            int hits = 0;
            for (int i = 0; i < ScanSteps; i++)
            {
                double tDep = PinnedScanBaseUT + (ctx.SynodicSeconds * i) / ScanSteps;
                depUTs[i] = tDep;
                scan[i] = ReaimTransferSynthesizer.TrySynthesizeTransfer(
                    ctx.Kerbin, ctx.Duna, tDep, ctx.TofSeconds, prograde: true,
                    out _, out _, out _, out _);
                if (scan[i])
                    hits++;
            }
            ParsekLog.Verbose("ReaimE2E",
                $"pinned scan base={PinnedScanBaseUT.ToString("F0", CultureInfo.InvariantCulture)} steps={ScanSteps} feasible={hits}");

            ctx.Scan = scan;
            ctx.ScanDepartureUTs = depUTs;
            return ctx;
        }

        // Builds the synthetic member segments + re-aim plan for one departure: Kerbin parking
        // just before, Sun heliocentric leg = [dep, dep+tof], Duna arrival just after. Stands in
        // for a recorded mission's segment list; the congruent-window model relaunches this
        // geometry every synodic period.
        private static void BuildMemberAndPlan(
            ScanContext ctx, double departureUT,
            out List<OrbitSegment> memberSegments, out ReaimMissionPlan plan,
            out double spanStart, out double spanEnd)
        {
            string parent = ctx.Kerbin.referenceBody.bodyName;
            spanStart = departureUT - 600.0;
            double recordedArrivalUT = departureUT + ctx.TofSeconds;
            spanEnd = recordedArrivalUT + 600.0;
            memberSegments = new List<OrbitSegment>
            {
                new OrbitSegment { bodyName = "Kerbin", startUT = spanStart, endUT = departureUT,
                    semiMajorAxis = 700000.0, eccentricity = 0.0, epoch = spanStart },
                new OrbitSegment { bodyName = parent, startUT = departureUT, endUT = recordedArrivalUT,
                    semiMajorAxis = 1.5e10, eccentricity = 0.2, epoch = departureUT }, // recorded heliocentric leg
                new OrbitSegment { bodyName = "Duna", startUT = recordedArrivalUT, endUT = spanEnd,
                    semiMajorAxis = 500000.0, eccentricity = 0.1, epoch = recordedArrivalUT },
            };
            plan = new ReaimMissionPlan
            {
                Supported = true,
                LaunchBody = "Kerbin",
                TargetBody = "Duna",
                CommonAncestor = parent,
                ParkingOrbit = memberSegments[0],
                HeliocentricLeg = memberSegments[1],
                ArrivalLeg = memberSegments[2],
                RecordedDepartureUT = departureUT,
                RecordedArrivalUT = recordedArrivalUT,
                RecordedTransferTofSeconds = ctx.TofSeconds
            };
        }

        // The shared per-window sanity assertions: 3 segments (Kerbin parking / re-aimed Sun
        // transfer / Duna arrival), sane elliptic transfer conic, recorded-span placement.
        private static void AssertSaneWindowSegments(
            List<OrbitSegment> segs, ReaimMissionPlan plan, double departureUT, double spanEnd, long k)
        {
            var ic = CultureInfo.InvariantCulture;
            InGameAssert.IsTrue(segs != null && segs.Count == 3,
                $"window k={k} must keep 3 segments (Kerbin parking / re-aimed Sun transfer / Duna arrival)");
            OrbitSegment transfer = segs[1];
            InGameAssert.AreEqual(plan.CommonAncestor, transfer.bodyName,
                $"window k={k} transfer must be Sun-bodied");
            InGameAssert.IsTrue(
                ReaimTransferSynthesizer.IsSaneTransferConic(transfer.eccentricity, transfer.semiMajorAxis),
                $"window k={k} transfer must be a sane elliptic conic (ecc={transfer.eccentricity.ToString("R", ic)} sma={transfer.semiMajorAxis.ToString("R", ic)})");
            // Per-member: only the Sun leg is re-aimed (placed at [departure, recordedArrival]);
            // the Kerbin parking + Duna arrival legs keep their recorded UTs + bodies.
            InGameAssert.AreEqual("Kerbin", segs[0].bodyName, $"window k={k} must keep the Kerbin parking leg");
            InGameAssert.AreEqual("Duna", segs[2].bodyName, $"window k={k} must keep the Duna arrival leg");
            InGameAssert.IsTrue(System.Math.Abs(segs[1].startUT - departureUT) < 1.0,
                $"window k={k} transfer must start at the recorded departure (recorded-span)");
            InGameAssert.IsTrue(System.Math.Abs(segs[2].endUT - spanEnd) < 1.0,
                $"window k={k} arrival must keep its recorded span end (fits span)");
        }

        // Identical-solve comparison: the re-solve runs the same FP path on the same inputs, so
        // bitwise equality is expected; the tiny relative margin only guards exotic FP modes.
        private static bool NearlyEqual(double a, double b)
        {
            if (double.IsNaN(a) && double.IsNaN(b))
                return true;
            double scale = System.Math.Max(1.0, System.Math.Max(System.Math.Abs(a), System.Math.Abs(b)));
            return System.Math.Abs(a - b) <= 1e-9 * scale;
        }
    }
}
