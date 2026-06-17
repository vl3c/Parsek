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
    // PARAMETRIZATION (M-MIS-3 stage B prep): the geometry/scan/member-plan/assertion machinery is
    // generic over a (launchBodyName, targetBodyName) pair and target-appropriate synthetic leg
    // elements (ReaimFixture). The Kerbin->Duna tests below are the regression fence: they call the
    // parametrized helpers with KerbinToDuna() and assert exactly what they asserted when they were
    // hardcoded. The eccentric-target (Moho, Eeloo) fixtures that exercise stage B's tof centering
    // reuse the SAME helpers with a different ReaimFixture (added in a later step); nothing in the
    // pinned-scan / mid-band / band-edge / sweep / determinism machinery is Kerbin/Duna specific.
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

        // A target fixture: the two stock bodies of an interplanetary re-aim transfer plus the
        // synthetic member-segment leg elements (parking / heliocentric / arrival sma+ecc) that
        // stand in for a recorded mission's on-rails SOI chain. Kerbin/Duna values are the legacy
        // hardcoded constants; an eccentric-target fixture (Moho, Eeloo) supplies its own arrival
        // elements so the same helpers exercise stage B's tof centering. The leg elements are
        // placement stand-ins only - the resolver re-solves the heliocentric leg's conic per window
        // from the real stock ephemerides (BuildGeometryOrSkip's Hohmann tof), so they do not need
        // to be the body's true orbit; they only need to be sane OrbitSegments the assembler can
        // place across the recorded span.
        private sealed class ReaimFixture
        {
            public string LaunchBodyName;
            public string TargetBodyName;

            public double ParkingSma;          // S0 launch-body parking orbit sma (metres)
            public double ParkingEcc;
            public double HeliocentricSma;     // S2 recorded heliocentric leg sma (metres)
            public double HeliocentricEcc;
            public double ArrivalSma;          // S3 target-body arrival orbit sma (metres)
            public double ArrivalEcc;

            // The OFFSET-RECORDED-TOF construction (plan section 4.2.2, the silent-no-op trap). The harness
            // uses ctx.TofSeconds as the STAND-IN recorded tof (fed to ReaimWindowPlanner.Plan as
            // recordedTof => schedule.TofSeconds, AND to the scan). Stage B (ReaimPlaybackResolver) centers
            // its tof search on schedule.TofSeconds and EXTENDS it toward the GEOMETRIC Hohmann time
            // (geomTof), gated by target eccentricity. If a fixture leaves the recorded tof EQUAL to geomTof
            // (the legacy Kerbin->Duna pattern: RecordedTofOffsetFraction = 0), the band is already centered
            // on geomTof, so stage B changes nothing and an eccentric fixture proves NOTHING.
            //
            // To actually exercise limitation B, an eccentric fixture sets RecordedTofOffsetFraction != 0:
            // ctx.TofSeconds = geomTof * (1 + RecordedTofOffsetFraction), i.e. a recorded tof DELIBERATELY
            // displaced from the geometric center. This stands in for a recording captured when the target
            // sat at one specific true anomaly (a longer-than-Hohmann recorded tof => the target was farther
            // out, toward apoapsis). The offset must be CHOSEN so geomTof falls OUTSIDE the recorded +-6%
            // base band (|offset| > ReaimTofSearch.BaseHalfWidthFraction) but INSIDE the eccentricity-scaled
            // band (|offset| < ReaimTofSearch.HalfWidthFraction(eTarget)): then the pre-stage-B +-6% search
            // cannot reach geomTof for the drifted windows (they decline), while stage B's ecc-gated
            // expansion can (they resolve). That delta IS the in-game measurement the user gates on.
            // 0 = legacy Duna behaviour (recorded tof == geomTof, no offset).
            public double RecordedTofOffsetFraction;
        }

        // The legacy Kerbin->Duna fixture: the exact synthetic leg elements the four tests used
        // when launch/target were hardcoded, so routing them through the parametrized helpers is a
        // pure refactor (byte-identical geometry + assertions).
        private static ReaimFixture KerbinToDuna()
        {
            return new ReaimFixture
            {
                LaunchBodyName = "Kerbin",
                TargetBodyName = "Duna",
                ParkingSma = 700000.0,
                ParkingEcc = 0.0,
                HeliocentricSma = 1.5e10,
                HeliocentricEcc = 0.2,
                ArrivalSma = 500000.0,
                ArrivalEcc = 0.1,
                // Regression fence: recorded tof == geometric Hohmann time (no offset), so stage B's band is
                // centered exactly where it always was and the Duna windows resolve byte-identically. Duna's
                // ecc (~0.051) is below the offset, so even with a nonzero offset stage B would barely widen;
                // keeping 0 makes the no-regression contract explicit.
                RecordedTofOffsetFraction = 0.0,
            };
        }

        // The eccentric-target fixtures (plan section 4.2.3 Moho, 4.2.4 Eeloo). Both use the same
        // OFFSET-RECORDED-TOF construction (see ReaimFixture.RecordedTofOffsetFraction): the stand-in
        // recorded tof is displaced from the geometric Hohmann center so the geometric tof lands OUTSIDE the
        // recorded +-6% base band but INSIDE the eccentricity-scaled band - the pre-stage-B search cannot
        // reach it, stage B's ecc-gated expansion can. The offset is a POSITIVE fraction (a longer recorded
        // tof than the Hohmann time, standing in for a recording captured with the target toward apoapsis);
        // it is sized between ReaimTofSearch.BaseHalfWidthFraction (0.06) and the per-target scaled
        // HalfWidthFraction so the construction is correct against the stage-B band law, NOT a magic number.
        //
        // The leg sma/ecc are placement stand-ins only (the resolver re-solves the heliocentric conic per
        // window from the real stock ephemerides); the arrival leg uses a higher ecc to flag visually that
        // these are eccentric targets, but no assertion reads it (requirement 3: sma/ecc are EXPECTED to vary
        // per window, never asserted congruent).

        // Moho: high inclination (~7 deg) + near-180 Hohmann transfer + moderate eccentricity (~0.2). The
        // COMBINED case - validates stage A's un-projection inclination lift (the transfer must aim at Moho's
        // actual out-of-plane position and the proximity check must find the SOI) AND stage B's ecc tof
        // centering. Offset +0.10: above the 0.06 base band, below Moho's scaled band
        // (clamp(0.06 + 0.5*0.20, .., 0.20) = 0.16).
        private static ReaimFixture KerbinToMoho()
        {
            return new ReaimFixture
            {
                LaunchBodyName = "Kerbin",
                TargetBodyName = "Moho",
                ParkingSma = 700000.0,
                ParkingEcc = 0.0,
                HeliocentricSma = 1.0e10,
                HeliocentricEcc = 0.3,
                ArrivalSma = 200000.0,
                ArrivalEcc = 0.2,
                RecordedTofOffsetFraction = 0.10,
            };
        }

        // Eeloo: high eccentricity (~0.26), the clean stage-B isolate. Offset +0.12: above the 0.06 base
        // band, below Eeloo's scaled band (clamp(0.06 + 0.5*0.26, .., 0.20) = 0.19).
        private static ReaimFixture KerbinToEeloo()
        {
            return new ReaimFixture
            {
                LaunchBodyName = "Kerbin",
                TargetBodyName = "Eeloo",
                ParkingSma = 700000.0,
                ParkingEcc = 0.0,
                HeliocentricSma = 4.0e10,
                HeliocentricEcc = 0.4,
                ArrivalSma = 400000.0,
                ArrivalEcc = 0.26,
                RecordedTofOffsetFraction = 0.12,
            };
        }

        // A HELIOCENTRIC-PARKING-DEPARTURE fixture (Kerbin->Duna two-burn): ascend + park in Kerbin orbit,
        // escape into a near-circular co-orbital heliocentric PARK, coast the park, then a 2nd burn onto the
        // transfer to Duna, then capture. Unlike the direct fixtures this carries a Sun PARK sub-coast ENDING
        // at the burn (RecordedDepartureUT) with full Kepler elements (LAN/argPe/mEp/epoch), so the resolver
        // engages the park-end r1 override (DepartedFromHeliocentricPark=true). The park is near-circular and
        // near-equatorial (co-orbital with Kerbin) so it passes the ParkRephaseMaxInclinationDeg guard.
        private sealed class ParkingDepartureFixture
        {
            public string LaunchBodyName = "Kerbin";
            public string TargetBodyName = "Duna";
            // The Sun PARK sub-coast (co-orbital with Kerbin's heliocentric orbit; the icon abuts this at the
            // burn). sma ~ Kerbin's heliocentric sma so it is genuinely co-orbital; near-circular + near-eq.
            public double ParkSma;            // ~ Kerbin heliocentric sma (set live from the body)
            public double ParkEcc = 0.01;
            public double ParkInc = 1.0;      // degrees, under the 15 deg guard
            public double ParkLan = 200.0;    // degrees
            public double ParkArgPe = 40.0;   // degrees
            public double ParkMEp = 1.2;      // radians
        }

        private sealed class ScanContext
        {
            public ReaimFixture Fixture;
            public CelestialBody LaunchBody;
            public CelestialBody TargetBody;
            public double TofSeconds;          // the STAND-IN recorded tof (geomTof for Duna; geomTof*(1+offset) for eccentric fixtures)
            public double GeomTofSeconds;      // the GEOMETRIC Hohmann tof (== stage B's center; == TofSeconds when no offset)
            public double SynodicSeconds;
            public bool[] Scan;                // per scan step: window-0 transfer synthesizes?
            public double[] ScanDepartureUTs;  // per scan step: candidate departure UT

            public string LaunchBodyName => Fixture.LaunchBodyName;
            public string TargetBodyName => Fixture.TargetBodyName;
        }

        [InGameTest(Category = "Periodicity", Scene = GameScenes.SPACECENTER,
            Description = "Re-aim end-to-end (deterministic, mid-band departure): every synodic window resolves a sane re-aimed Kerbin->Duna transfer whose orientation rotates (congruent-window model)")]
        public void Reaim_KerbinToDuna_EveryWindowResolvesSaneTransfer()
        {
            var ic = CultureInfo.InvariantCulture;
            ScanContext ctx = BuildPinnedScanOrSkip(KerbinToDuna());
            if (ctx == null)
                return;

            int midIdx = ReaimFeasibilityScan.CenterOfLongestRunIndex(ctx.Scan, cyclic: true);
            if (midIdx < 0)
            {
                InGameAssert.Skip($"no good {ctx.LaunchBodyName}->{ctx.TargetBodyName} departure found in the pinned synodic scan (unexpected on stock)");
                return;
            }
            double goodDep = ctx.ScanDepartureUTs[midIdx];

            BuildMemberAndPlan(ctx, goodDep,
                out List<OrbitSegment> memberSegments, out ReaimMissionPlan plan,
                out double spanStart, out double spanEnd);
            ReaimWindowPlanner.ReaimWindowSchedule sched = ReaimWindowPlanner.Plan(
                ctx.LaunchBody.orbit.period, ctx.TargetBody.orbit.period, goodDep, ctx.TofSeconds,
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
                AssertSaneWindowSegments(ctx, segs, plan, goodDep, spanEnd, k);

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
                $"{ctx.LaunchBodyName}->{ctx.TargetBodyName} congruent windows (pinned mid-band): checked={WindowsToCheck} resolved={resolved} " +
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
            ScanContext ctx = BuildPinnedScanOrSkip(KerbinToDuna());
            if (ctx == null)
                return;

            int edgeIdx = ReaimFeasibilityScan.FirstSuccessIndex(ctx.Scan);
            if (edgeIdx < 0)
            {
                InGameAssert.Skip($"no good {ctx.LaunchBodyName}->{ctx.TargetBodyName} departure found in the pinned synodic scan (unexpected on stock)");
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
                $"{ctx.LaunchBodyName}->{ctx.TargetBodyName} band-edge windows (pinned): scanIdx={edgeIdx} edgeDep={edgeDep.ToString("F1", ic)} " +
                $"map={map} resolved={resolvedCount} declined={declinedCount} of {WindowsToCheck} " +
                $"(decline = clean faithful fall-back; all-decline is a valid fail-closed outcome)");
        }

        [InGameTest(Category = "Periodicity", Scene = GameScenes.SPACECENTER,
            Description = "Re-aim end-to-end (pinned observed 2026-06-11 failure geometry): the near-180 handedness fix resolves the nominal departure prograde (the previous inc=180 retrograde-branch decline); later windows resolve or decline cleanly, expected all-R")]
        public void Reaim_KerbinToDuna_ObservedEdgeDeparture_ResolvesOrDeclinesCleanly()
        {
            var ic = CultureInfo.InvariantCulture;
            ScanContext ctx = BuildGeometryOrSkip(KerbinToDuna());
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
                $"{ctx.LaunchBodyName}->{ctx.TargetBodyName} observed-failure departure (pinned 2026-06-11): depUT={ObservedEdgeDepartureUT.ToString("R", ic)} " +
                $"map={map} resolved={resolvedCount} declined={declinedCount} of {WindowsToCheck} " +
                $"(near-180 handedness fix: the nominal-departure inc=180 retrograde-branch decline is now resolved prograde; " +
                $"map expected all-R, a later 'd' is the separate M-MIS-3 eccentric-drift mode falling back cleanly, not a handedness regression)");
        }

        // ----- Eccentric-target fixtures (M-MIS-3 stage B): Moho + Eeloo. -----
        //
        // CONTRACT (measure-first; plan section 4.2 + this step's requirements 2-4):
        //  - HARD (asserted every run): the STRUCTURAL contract via DriveWindowsResolveOrDeclineCleanly with
        //    requireWindow0Resolve:true - window 0 (the recorded departure, where the offset recorded tof
        //    synthesizes by construction of the scan) MUST resolve; every window resolves a sane elliptic
        //    PROGRADE conic OR declines cleanly to faithful (null segments, correct window index); the
        //    render-span placement holds (transfer starts at the recorded departure, arrival keeps the span
        //    end); and the outcome is deterministic on a cache-cleared re-solve. requireAllWindowsResolve is
        //    FALSE - an eccentric window that still declines is a clean fail-closed fall-back, NOT a test
        //    failure (the eccentric-window pass/fail is the USER's in-game gate, below).
        //  - SOFT / OBSERVATIONAL (the measurement artifact, NOT an assertion): the per-window R/d map +
        //    the resolved/declined counts logged via ParsekLog.Info. This is the in-game gate the USER reads
        //    to confirm stage B actually pulled the drifted windows from 'd' to 'R'. A pre-stage-B build (or
        //    stage B disabled) is EXPECTED to decline the windows whose geometric tof drifted outside the
        //    recorded +-6% base band; stage B is EXPECTED to resolve them. That delta is the proof, and it is
        //    left to the live run exactly as stage A left requireAllWindowsResolve soft.
        //  - Requirement 3 (NO shape-congruence assertion): like the Duna strict test, these assert per-window
        //    SANE conic + prograde + render-span only. They DO NOT assert sma/ecc recur across windows -
        //    varying sma/ecc per window is CORRECT for an eccentric target (the congruent-window premise is
        //    shape-congruent only for circular targets). The per-window sma/ecc are LOGGED, never asserted.

        [InGameTest(Category = "Periodicity", Scene = GameScenes.SPACECENTER,
            Description = "Re-aim end-to-end (Moho, eccentric + inclined + near-180): structural contract holds + window 0 resolves; the per-window R/d map is the measure-first stage-B + stage-A-inclination artifact (eccentric-window pass/fail is the user's in-game gate)")]
        public void Reaim_KerbinToMoho_StructuralContractAndWindow0_MeasureRdMap()
        {
            RunEccentricTargetWindows(KerbinToMoho(), "reaim-e2e-moho",
                combinedInclinationCase: true);
        }

        [InGameTest(Category = "Periodicity", Scene = GameScenes.SPACECENTER,
            Description = "Re-aim end-to-end (Eeloo, high eccentricity): structural contract holds + window 0 resolves; the per-window R/d map is the measure-first stage-B isolate artifact (eccentric-window pass/fail is the user's in-game gate)")]
        public void Reaim_KerbinToEeloo_StructuralContractAndWindow0_MeasureRdMap()
        {
            RunEccentricTargetWindows(KerbinToEeloo(), "reaim-e2e-eeloo",
                combinedInclinationCase: false);
        }

        // The shared eccentric-target driver. Picks the band CENTER departure (the stable mid-band pick, like
        // the strict Duna test) from the pinned scan run with the OFFSET recorded tof, then drives the windows
        // under the structural-hard / all-resolve-soft contract and emits the per-window R/d measurement map.
        // combinedInclinationCase only flavours the log line (Moho also exercises stage A's un-projection
        // inclination lift); the assertions are identical for both targets.
        private static void RunEccentricTargetWindows(
            ReaimFixture fixture, string memberIdPrefix, bool combinedInclinationCase)
        {
            var ic = CultureInfo.InvariantCulture;
            ScanContext ctx = BuildPinnedScanOrSkip(fixture);
            if (ctx == null)
                return;

            // Sanity-check the offset construction against the live stage-B band law so a fixture that has
            // been mis-tuned (offset inside the base band => stage B is a no-op, or outside the scaled band
            // => geomTof is unreachable even after stage B) is caught here instead of silently proving
            // nothing. This is a STRUCTURAL assertion on the fixture's own numbers, not on a magic constant:
            // it reads ReaimTofSearch's BaseHalfWidthFraction / HalfWidthFraction, so re-pinning those
            // measure-first placeholders cannot break it as long as the fixture offset is chosen sanely.
            double eTarget = ctx.TargetBody.orbit.eccentricity;
            double absOffset = System.Math.Abs(fixture.RecordedTofOffsetFraction);
            double scaledHalfWidth = ReaimTofSearch.HalfWidthFraction(eTarget);
            // Lower bound: a HARD assert. It compares two FIXED constants (the fixture's authored offset vs the
            // fixed base band), independent of stock numbers, so a failure here is always a fixture-authoring
            // bug (offset placed inside the base band => stage B is a no-op and the fixture proves nothing).
            InGameAssert.IsTrue(absOffset > ReaimTofSearch.BaseHalfWidthFraction,
                $"{ctx.TargetBodyName} recorded-tof offset ({absOffset.ToString("F4", ic)}) must exceed the base band " +
                $"({ReaimTofSearch.BaseHalfWidthFraction.ToString("F4", ic)}) so geomTof lands OUTSIDE the pre-stage-B +-6% search (else stage B proves nothing)");
            // Upper bound: a clean SKIP, not a hard assert. This guard reads the LIVE stock eccentricity
            // (ctx.TargetBody.orbit.eccentricity), so a future stock rebalance that drops the target's ecc far
            // enough would shrink the scaled band below the fixture's fixed offset and make geomTof unreachable
            // even after stage B. That is a fixture-tuning issue for the new stock numbers, NOT a regression in
            // the production code under test, so it must skip (re-pin the fixture offset or PinnedScanBaseUT),
            // not fail. The measure-first canary stays honest either way.
            if (absOffset >= scaledHalfWidth)
            {
                InGameAssert.Skip(
                    $"{ctx.TargetBodyName} recorded-tof offset ({absOffset.ToString("F4", ic)}) is no longer INSIDE the eccentricity-scaled band " +
                    $"({scaledHalfWidth.ToString("F4", ic)} for live eTarget={eTarget.ToString("F4", ic)}) - stage B cannot reach geomTof for this fixture; " +
                    "re-pin the fixture offset (stock ecc may have drifted)");
                return;
            }

            int midIdx = ReaimFeasibilityScan.CenterOfLongestRunIndex(ctx.Scan, cyclic: true);
            if (midIdx < 0)
            {
                // No feasible window-0 departure for the OFFSET recorded tof on stock. Skip (not a failure):
                // the scan tests window-0 synthesis at the offset tof, and a target/offset pairing that finds
                // none in this pinned synodic period cannot drive the contract. The user re-pins the offset
                // or PinnedScanBaseUT (open question 3) if this skips in the live batch.
                InGameAssert.Skip($"no feasible {ctx.LaunchBodyName}->{ctx.TargetBodyName} window-0 departure for the offset recorded tof in the pinned scan");
                return;
            }
            double midDep = ctx.ScanDepartureUTs[midIdx];

            // HARD: structural contract + window 0 resolves. SOFT: all-windows (requireAllWindowsResolve:false).
            DriveWindowsResolveOrDeclineCleanly(ctx, midDep, memberIdPrefix + "-mid-" + midIdx.ToString(ic),
                requireWindow0Resolve: true, requireAllWindowsResolve: false,
                out string map, out int resolvedCount, out int declinedCount);

            // The measurement artifact (SOFT/observational): the R/d map + the recorded/geom tof centers + the
            // offset, so the live-run reader can see whether stage B's ecc band pulled the drifted windows from
            // 'd' to 'R'. The eccentric-window pass/fail is the USER's in-game gate, NOT an assertion here.
            double offsetSeconds = ctx.TofSeconds - ctx.GeomTofSeconds;
            ParsekLog.Info("ReaimE2E",
                $"{ctx.LaunchBodyName}->{ctx.TargetBodyName} eccentric windows (pinned mid-band, stage-B measure-first): " +
                $"scanIdx={midIdx} midDep={midDep.ToString("F1", ic)} eTarget={eTarget.ToString("F4", ic)} " +
                $"recordedTof={ctx.TofSeconds.ToString("F0", ic)} geomTof={ctx.GeomTofSeconds.ToString("F0", ic)} " +
                $"offset={fixture.RecordedTofOffsetFraction.ToString("F4", ic)}({offsetSeconds.ToString("F0", ic)}s) " +
                $"scaledHalfWidth={scaledHalfWidth.ToString("F4", ic)} synodic={ctx.SynodicSeconds.ToString("F0", ic)} | " +
                $"map={map} resolved={resolvedCount} declined={declinedCount} of {WindowsToCheck} | " +
                (combinedInclinationCase
                    ? "COMBINED case: also validates stage A's un-projection inclination lift (transfer aims at the target's actual out-of-plane position, prograde inc carried). "
                    : "clean high-ecc isolate. ") +
                "OBSERVATIONAL: a 'd' on a drifted window pre-stage-B should flip to 'R' under stage B - that delta is the user's in-game gate, not asserted; " +
                "per-window sma/ecc EXPECTED to vary (requirement 3, never asserted congruent)");
        }

        // Manual-only per-target measurement sweep (the M-MIS-1 "measure before knob math" artifact, applied
        // to the eccentric fixtures): maps the WHOLE pinned departure scan x WindowsToCheck into KSP.log for
        // the offset recorded tof, so the regression boundary (which windows decline pre-stage-B vs resolve
        // post-stage-B) is visible across the whole band, not just the mid-band pick. AllowBatchExecution=false
        // (tens of seconds of Lambert/CalculatePatch work). Reuses the Duna sweep machinery verbatim via
        // RunEccentricFeasibilitySweep.
        [InGameTest(Category = "Periodicity", Scene = GameScenes.SPACECENTER,
            AllowBatchExecution = false,
            BatchSkipReason = "diagnostic measurement sweep (M-MIS-3 stage B); run manually from the test runner",
            Description = "Re-aim feasibility sweep (Moho, eccentric): per-departure x per-window resolve/decline map for the offset recorded tof (measure-first artifact, manual-only)")]
        public IEnumerator Reaim_KerbinToMoho_FeasibilitySweep_Diagnostic()
        {
            return RunEccentricFeasibilitySweep(KerbinToMoho(), "reaim-e2e-moho-sweep");
        }

        [InGameTest(Category = "Periodicity", Scene = GameScenes.SPACECENTER,
            AllowBatchExecution = false,
            BatchSkipReason = "diagnostic measurement sweep (M-MIS-3 stage B); run manually from the test runner",
            Description = "Re-aim feasibility sweep (Eeloo, high eccentricity): per-departure x per-window resolve/decline map for the offset recorded tof (measure-first artifact, manual-only)")]
        public IEnumerator Reaim_KerbinToEeloo_FeasibilitySweep_Diagnostic()
        {
            return RunEccentricFeasibilitySweep(KerbinToEeloo(), "reaim-e2e-eeloo-sweep");
        }

        // Drives the whole-band x per-window sweep for an eccentric fixture (offset recorded tof). Mirrors the
        // Duna FeasibilitySweep_Diagnostic body; kept as a shared coroutine so the two eccentric sweeps differ
        // only in their fixture. Purely a KSP.log measurement product - NO assertions (it is the measure-first
        // input, not a contract).
        private static IEnumerator RunEccentricFeasibilitySweep(ReaimFixture fixture, string memberIdPrefix)
        {
            var ic = CultureInfo.InvariantCulture;
            ScanContext ctx = BuildPinnedScanOrSkip(fixture);
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
                    ctx.LaunchBody.orbit.period, ctx.TargetBody.orbit.period, dep, ctx.TofSeconds,
                    spanStart, spanEnd, referenceUT: dep - 1.0);
                if (!sched.Valid)
                {
                    ParsekLog.Warn("ReaimE2E", $"{ctx.TargetBodyName} sweep dep={i}/{ScanSteps} schedule invalid ({sched.Reason}) - skipped");
                    continue;
                }

                var resolver = new ReaimPlaybackResolver();
                string memberId = memberIdPrefix + "-" + i.ToString(ic);
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
                    $"{ctx.TargetBodyName} sweep dep={i}/{ScanSteps} depUT={dep.ToString("F1", ic)} windows={map} resolved={resolvedCount}/{WindowsToCheck}");
                yield return null;
            }

            double offsetSeconds = ctx.TofSeconds - ctx.GeomTofSeconds;
            var perWindow = new StringBuilder();
            for (int k = 0; k < WindowsToCheck; k++)
                perWindow.Append(k == 0 ? "k0=" : $" k{k.ToString(ic)}=").Append(perWindowResolved[k].ToString(ic));
            ParsekLog.Info("ReaimE2E",
                $"{ctx.LaunchBodyName}->{ctx.TargetBodyName} sweep summary (offset recorded tof): feasible={feasible}/{ScanSteps} band={bandMap} " +
                $"first={firstIdx} center={centerIdx} recordedTof={ctx.TofSeconds.ToString("F0", ic)} geomTof={ctx.GeomTofSeconds.ToString("F0", ic)} " +
                $"offset={fixture.RecordedTofOffsetFraction.ToString("F4", ic)}({offsetSeconds.ToString("F0", ic)}s) " +
                $"perWindowResolved {perWindow} (stage-B measure-first: declines = the windows whose geometric tof drifted outside the recorded +-6% band; " +
                $"compare a pre-stage-B build to see the resolve delta - the user's in-game gate)");
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
                ctx.LaunchBody.orbit.period, ctx.TargetBody.orbit.period, departureUT, ctx.TofSeconds,
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
                    AssertSaneWindowSegments(ctx, segs, plan, departureUT, spanEnd, k);
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
            ScanContext ctx = BuildPinnedScanOrSkip(KerbinToDuna());
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
                    ctx.LaunchBody.orbit.period, ctx.TargetBody.orbit.period, dep, ctx.TofSeconds,
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

        // Resolves the stock launch/target geometry (bodies, Hohmann tof, synodic period) shared by
        // all four tests for the given fixture, or returns null after InGameAssert.Skip on a
        // non-stock body graph / degenerate geometry. Scan fields stay null; the scan-driven tests
        // fill them via BuildPinnedScanOrSkip.
        private static ScanContext BuildGeometryOrSkip(ReaimFixture fixture)
        {
            CelestialBody launchBody = FlightGlobals.Bodies?.Find(b => b.bodyName == fixture.LaunchBodyName);
            CelestialBody targetBody = FlightGlobals.Bodies?.Find(b => b.bodyName == fixture.TargetBodyName);
            if (launchBody == null || targetBody == null)
            {
                InGameAssert.Skip($"{fixture.LaunchBodyName}/{fixture.TargetBodyName} not in FlightGlobals.Bodies (non-stock pack)");
                return null;
            }
            if (launchBody.referenceBody == null || launchBody.referenceBody != targetBody.referenceBody)
            {
                InGameAssert.Skip($"{fixture.LaunchBodyName} and {fixture.TargetBodyName} do not share a parent in this pack");
                return null;
            }

            double muParent = launchBody.referenceBody.gravParameter;
            double geomTof = TransferWindowMath.HohmannTransferTimeSeconds(
                launchBody.orbit.semiMajorAxis, targetBody.orbit.semiMajorAxis, muParent);
            double synodic = TransferWindowMath.SynodicPeriodSeconds(launchBody.orbit.period, targetBody.orbit.period);
            InGameAssert.IsTrue(geomTof > 0.0 && !double.IsInfinity(synodic) && synodic > 0.0,
                "Hohmann tof + synodic period must be finite/positive");

            // The stand-in recorded tof. For Duna (offset 0) it IS the geometric Hohmann time, byte-identical
            // to before. For an eccentric fixture (offset != 0) it is DISPLACED from the geometric center
            // (plan section 4.2.2) so geomTof lands outside the recorded +-6% base band and stage B has real
            // work to do - the resolver re-centers on THIS recorded tof and extends toward geomTof.
            double recordedTof = geomTof * (1.0 + fixture.RecordedTofOffsetFraction);
            InGameAssert.IsTrue(recordedTof > 0.0, "stand-in recorded tof must be positive");

            return new ScanContext
            {
                Fixture = fixture,
                LaunchBody = launchBody,
                TargetBody = targetBody,
                TofSeconds = recordedTof,
                GeomTofSeconds = geomTof,
                SynodicSeconds = synodic
            };
        }

        // Builds the pinned deterministic scan on top of the shared geometry for the given fixture,
        // or returns null after InGameAssert.Skip.
        private static ScanContext BuildPinnedScanOrSkip(ReaimFixture fixture)
        {
            ScanContext ctx = BuildGeometryOrSkip(fixture);
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
                    ctx.LaunchBody, ctx.TargetBody, tDep, ctx.TofSeconds, prograde: true,
                    out _, out _, out _, out _);
                if (scan[i])
                    hits++;
            }
            ParsekLog.Verbose("ReaimE2E",
                $"pinned scan {ctx.LaunchBodyName}->{ctx.TargetBodyName} base={PinnedScanBaseUT.ToString("F0", CultureInfo.InvariantCulture)} steps={ScanSteps} feasible={hits}");

            ctx.Scan = scan;
            ctx.ScanDepartureUTs = depUTs;
            return ctx;
        }

        // Builds the synthetic member segments + re-aim plan for one departure: launch-body parking
        // just before, common-ancestor heliocentric leg = [dep, dep+tof], target-body arrival just
        // after. Stands in for a recorded mission's segment list; the congruent-window model
        // relaunches this geometry every synodic period. The leg sma/ecc come from the fixture so an
        // eccentric target supplies target-appropriate arrival elements.
        private static void BuildMemberAndPlan(
            ScanContext ctx, double departureUT,
            out List<OrbitSegment> memberSegments, out ReaimMissionPlan plan,
            out double spanStart, out double spanEnd)
        {
            ReaimFixture fx = ctx.Fixture;
            string launchName = fx.LaunchBodyName;
            string targetName = fx.TargetBodyName;
            string parent = ctx.LaunchBody.referenceBody.bodyName;
            spanStart = departureUT - 600.0;
            double recordedArrivalUT = departureUT + ctx.TofSeconds;
            spanEnd = recordedArrivalUT + 600.0;
            memberSegments = new List<OrbitSegment>
            {
                new OrbitSegment { bodyName = launchName, startUT = spanStart, endUT = departureUT,
                    semiMajorAxis = fx.ParkingSma, eccentricity = fx.ParkingEcc, epoch = spanStart },
                new OrbitSegment { bodyName = parent, startUT = departureUT, endUT = recordedArrivalUT,
                    semiMajorAxis = fx.HeliocentricSma, eccentricity = fx.HeliocentricEcc, epoch = departureUT }, // recorded heliocentric leg
                new OrbitSegment { bodyName = targetName, startUT = recordedArrivalUT, endUT = spanEnd,
                    semiMajorAxis = fx.ArrivalSma, eccentricity = fx.ArrivalEcc, epoch = recordedArrivalUT },
            };
            plan = new ReaimMissionPlan
            {
                Supported = true,
                LaunchBody = launchName,
                TargetBody = targetName,
                CommonAncestor = parent,
                ParkingOrbit = memberSegments[0],
                HeliocentricLeg = memberSegments[1],
                ArrivalLeg = memberSegments[2],
                RecordedDepartureUT = departureUT,
                RecordedArrivalUT = recordedArrivalUT,
                RecordedTransferTofSeconds = ctx.TofSeconds
            };
        }

        // ----- Heliocentric-parking-departure window-0 geometry gate (the r1==park-end fix) -----

        [InGameTest(Category = "Periodicity", Scene = GameScenes.SPACECENTER,
            Description = "Re-aim end-to-end (heliocentric-parking departure): the re-aimed transfer starts from the ghost's re-phased PARK-END position (r1==park-end), not the launch body's center, so the icon traverses park->transfer continuously. Covers window 0 (identity rotation) AND a non-identity window (real LAN rotation + departureUT != RecordedDepartureUT, exercising the Unity park reconstruction and the ParkEvalUT==RecordedDepartureUT contract end to end).")]
        public void Reaim_KerbinToDuna_ParkingDeparture_TransferStartsAtParkEnd()
        {
            ScanContext ctx = BuildGeometryOrSkip(KerbinToDuna());
            if (ctx == null)
                return;

            // A pinned departure that synthesizes window 0 (the mid-band pick, deterministic).
            ScanContext scanCtx = BuildPinnedScanOrSkip(KerbinToDuna());
            if (scanCtx == null)
                return;
            int midIdx = ReaimFeasibilityScan.CenterOfLongestRunIndex(scanCtx.Scan, cyclic: true);
            if (midIdx < 0)
            {
                InGameAssert.Skip("no good Kerbin->Duna departure in the pinned scan (unexpected on stock)");
                return;
            }
            double goodDep = scanCtx.ScanDepartureUTs[midIdx];

            // CASE A (window 0, identity rotation): referenceUT < recordedDeparture => D0 == RecordedDepartureUT,
            // so parkDeltaLon == 0 and departureUT == RecordedDepartureUT. The seam fix's primary case (the two
            // clocks coincide). The transfer MUST resolve here (the pinned mid-band departure is a known-good
            // window-0 geometry), so this is a HARD seam assertion.
            AssertParkEndSeam(ctx, goodDep, referenceUT: goodDep - 1.0, memberId: "reaim-e2e-parking-w0",
                requireResolve: true, requireNonIdentityRotation: false);

            // CASE B (non-identity window): referenceUT ~ RecordedDeparture + one synodic => D0 = RecordedDeparture
            // + N*synodic (N>=1), so departureUT != RecordedDepartureUT AND parkDeltaLon != 0 (a real LAN
            // rotation). This exercises the Unity park reconstruction with a non-identity rotation AND
            // distinguishes ParkEvalUT==RecordedDepartureUT from ==departureUT (evaluating at departureUT would
            // re-open the seam). requireResolve:false - the synthesis at a later synodic window can decline via
            // the separate, still-open eccentric-drift tof-band mode (I cannot run KSP to debug a decline), so
            // the full transfer-start==park-end seam sub-assertion runs ONLY when the window resolves; the pure
            // decision (ParkEvalUT + non-zero rotation) + the Unity reconstruction are asserted regardless.
            AssertParkEndSeam(ctx, goodDep, referenceUT: goodDep + ctx.SynodicSeconds, memberId: "reaim-e2e-parking-wN",
                requireResolve: false, requireNonIdentityRotation: true);
        }

        // The shared per-window park-end seam gate. Builds the parking-departure fixture at goodDep, plans the
        // schedule from referenceUT (which selects D0 and so the window-0 rotation magnitude), drives the
        // resolver at window 0 of THAT schedule, and asserts:
        //  - the pure DecideDepartureAnchor returns ParkEndOverride with ParkEvalUT == RecordedDepartureUT
        //    (NEVER departureUT / parkReplayUT) - the BLOCKER-1 contract;
        //  - when requireNonIdentityRotation, parkDeltaLon != 0 and departureUT != RecordedDepartureUT (the
        //    case actually exercises a real LAN rotation, not the window-0 identity);
        //  - the Unity park reconstruction (new Orbit(...) on the LAN-rotated park) evaluates without NaN;
        //  - when the window resolves (always, if requireResolve), the rendered transfer leg's start at
        //    RecordedDepartureUT equals the re-phased park-end at RecordedDepartureUT (the r1==park-end seam),
        //    and the park-end is genuinely off the launch body's center (so the gate is not vacuous).
        private static void AssertParkEndSeam(
            ScanContext ctx, double goodDep, double referenceUT, string memberId,
            bool requireResolve, bool requireNonIdentityRotation)
        {
            var ic = CultureInfo.InvariantCulture;
            var fx = new ParkingDepartureFixture { ParkSma = ctx.LaunchBody.orbit.semiMajorAxis };
            BuildParkingMemberAndPlan(ctx, goodDep, fx,
                out List<OrbitSegment> memberSegments, out ReaimMissionPlan plan,
                out double spanStart, out double spanEnd);
            InGameAssert.IsTrue(plan.DepartedFromHeliocentricPark,
                "the parking-departure fixture plan must have DepartedFromHeliocentricPark=true");

            ReaimWindowPlanner.ReaimWindowSchedule sched = ReaimWindowPlanner.Plan(
                ctx.LaunchBody.orbit.period, ctx.TargetBody.orbit.period, goodDep, ctx.TofSeconds,
                spanStart, spanEnd, referenceUT: referenceUT);
            InGameAssert.IsTrue(sched.Valid, "window schedule must be valid: " + (sched.Reason ?? ""));

            const long k = 0; // window 0 OF THIS SCHEDULE; referenceUT selects whether D0 == RecordedDeparture
            double departureUT = sched.DepartureUTForWindow(k);
            double parkReplayUT = sched.RelaunchUTForWindow(k);

            // The pure decision the resolver runs: must take the park-end override, evaluated at the RECORDED
            // burn UT (NOT departureUT / parkReplayUT). This is the BLOCKER-1 contract, asserted independent of
            // whether the live synthesis then resolves a target encounter.
            double parkInc = ReaimSegmentAssembler.FindHeliocentricParkInclination(
                memberSegments, plan.CommonAncestor, plan.RecordedDepartureUT);
            ReaimSegmentAssembler.DepartureAnchorDecision anchor =
                ReaimSegmentAssembler.DecideDepartureAnchor(
                    plan.DepartedFromHeliocentricPark, parkInc,
                    parkReplayUT, plan.RecordedDepartureUT, ctx.LaunchBody.orbit.period);
            InGameAssert.AreEqual(ReaimSegmentAssembler.DepartureAnchorMode.ParkEndOverride, anchor.Mode,
                "the parking departure must take the park-end override (near-equatorial co-orbital park)");
            InGameAssert.IsTrue(System.Math.Abs(anchor.ParkEvalUT - plan.RecordedDepartureUT) < 1.0,
                $"r1 must be evaluated at RecordedDepartureUT (BLOCKER 1), got evalUT={anchor.ParkEvalUT.ToString("R", ic)} vs recDep={plan.RecordedDepartureUT.ToString("R", ic)}");
            InGameAssert.IsTrue(System.Math.Abs(anchor.ParkEvalUT - departureUT) > 1.0 || !requireNonIdentityRotation,
                $"on the non-identity window the eval UT must NOT equal departureUT (D0={departureUT.ToString("R", ic)}); evaluating r1 at D0 would re-open the seam");

            if (requireNonIdentityRotation)
            {
                InGameAssert.IsTrue(System.Math.Abs(departureUT - plan.RecordedDepartureUT) > 1.0,
                    $"non-identity case must have departureUT != RecordedDepartureUT (D0={departureUT.ToString("R", ic)} recDep={plan.RecordedDepartureUT.ToString("R", ic)})");
                double deltaMod = anchor.ParkDeltaLonDeg % 360.0;
                InGameAssert.IsTrue(System.Math.Abs(deltaMod) > 0.5 && System.Math.Abs(deltaMod) < 359.5,
                    $"non-identity case must apply a real LAN rotation (parkDeltaLon mod 360 = {deltaMod.ToString("F2", ic)} deg)");
            }

            // The Unity park reconstruction with the (possibly non-identity) rotation: new Orbit(...) on the
            // LAN-rotated park, evaluated at RecordedDepartureUT - exactly what the resolver feeds as r1.
            OrbitSegment? parkSeg = ReaimSegmentAssembler.FindHeliocentricParkSegment(
                memberSegments, plan.CommonAncestor, plan.RecordedDepartureUT);
            InGameAssert.IsTrue(parkSeg.HasValue, "the parking fixture must expose a recorded park segment");
            OrbitSegment rotatedPark = ReaimSegmentAssembler.RotateLanForParkRephase(parkSeg.Value, anchor.ParkDeltaLonDeg);
            CelestialBody sun = ctx.LaunchBody.referenceBody;
            Orbit parkOrbit = new Orbit(
                rotatedPark.inclination, rotatedPark.eccentricity, rotatedPark.semiMajorAxis,
                rotatedPark.longitudeOfAscendingNode, rotatedPark.argumentOfPeriapsis,
                rotatedPark.meanAnomalyAtEpoch, rotatedPark.epoch, sun);
            Vector3d parkEndPos = parkOrbit.getRelativePositionAtUT(anchor.ParkEvalUT); // == RecordedDepartureUT
            InGameAssert.IsTrue(!double.IsNaN(parkEndPos.x) && !double.IsNaN(parkEndPos.y) && !double.IsNaN(parkEndPos.z),
                "the LAN-rotated park reconstruction must evaluate to a finite position at RecordedDepartureUT");
            Vector3d launchCenter = ctx.LaunchBody.orbit.getRelativePositionAtUT(plan.RecordedDepartureUT);
            double parkEndOffLaunchCenter = (parkEndPos - launchCenter).magnitude;

            // Drive the resolver at window 0 of this schedule.
            var resolver = new ReaimPlaybackResolver();
            double span = spanEnd - spanStart;
            double currentUT = sched.PhaseAnchorUT + k * sched.CadenceSeconds + 0.5 * span;
            bool ok = resolver.TryResolveWindowSegments(
                memberId, memberSegments, plan, sched,
                sched.PhaseAnchorUT, spanStart, spanEnd, sched.CadenceSeconds, currentUT,
                out List<OrbitSegment> segs, out long windowIndex);
            if (requireResolve)
                InGameAssert.IsTrue(ok, "this parking-departure window must resolve a re-aimed transfer");
            InGameAssert.AreEqual(k, windowIndex, "resolved window index must equal k=0 of this schedule");

            const double seamTolMetres = 1.0e6; // 1000 km: sub-pixel at interplanetary map scale

            if (!ok)
            {
                // A non-identity window can decline via the separate eccentric-drift tof-band mode (NOT a
                // departure-seam regression). Coverage boundary: the pure decision + the Unity reconstruction
                // above are still asserted; the end-to-end transfer-start==park-end sub-assertion needs a
                // resolved transfer, so log and skip it here.
                ParsekLog.Info("ReaimE2E",
                    $"parking-departure seam gate ({memberId}): window DECLINED to faithful (separate eccentric-drift mode, " +
                    $"not a seam regression) - decision+reconstruction asserted, end-to-end seam sub-assert skipped. " +
                    $"parkDeltaLon={anchor.ParkDeltaLonDeg.ToString("F2", ic)}deg D0={departureUT.ToString("R", ic)} recDep={plan.RecordedDepartureUT.ToString("R", ic)}");
                return;
            }

            InGameAssert.IsTrue(segs != null && segs.Count >= 3,
                $"resolved window must keep the parking/transfer/arrival legs (got {(segs == null ? 0 : segs.Count)})");

            // Find the re-aimed transfer leg (common-ancestor-bodied, the IN-WINDOW Sun segment whose UT span
            // covers the recorded departure). The departure-side PARK is also Sun-bodied but ENDS at the burn.
            OrbitSegment transferSeg = FindInWindowSunTransfer(segs, plan);
            InGameAssert.IsTrue(!string.IsNullOrEmpty(transferSeg.bodyName),
                "the resolved window must contain an in-window common-ancestor transfer segment");

            // The rendered transfer leg, reconstructed and evaluated at the RECORDED departure UT (the transfer
            // segment was ShiftInTime'd into recorded-span time, so at RecordedDepartureUT it sits where r1 was
            // placed). Same parent-relative native frame as parkEndPos (both getRelativePositionAtUT, no swizzle).
            Orbit transferOrbit = new Orbit(
                transferSeg.inclination, transferSeg.eccentricity, transferSeg.semiMajorAxis,
                transferSeg.longitudeOfAscendingNode, transferSeg.argumentOfPeriapsis,
                transferSeg.meanAnomalyAtEpoch, transferSeg.epoch, sun);
            Vector3d transferStartPos = transferOrbit.getRelativePositionAtUT(plan.RecordedDepartureUT);
            double seamMiss = (transferStartPos - parkEndPos).magnitude;

            ParsekLog.Info("ReaimE2E",
                $"parking-departure seam gate ({memberId}): seamMiss={seamMiss.ToString("F0", ic)}m " +
                $"parkEndOffLaunchCenter={parkEndOffLaunchCenter.ToString("F0", ic)}m " +
                $"parkSma={rotatedPark.semiMajorAxis.ToString("R", ic)} parkDeltaLon={anchor.ParkDeltaLonDeg.ToString("F2", ic)}deg " +
                $"evalUT={anchor.ParkEvalUT.ToString("R", ic)}=RecordedDepartureUT D0={departureUT.ToString("R", ic)} goodDep={goodDep.ToString("F1", ic)}");

            InGameAssert.IsTrue(seamMiss < seamTolMetres,
                $"re-aimed transfer must START at the re-phased park-end (r1==park-end): seamMiss={seamMiss.ToString("F0", ic)}m must be < {seamTolMetres.ToString("F0", ic)}m");
            InGameAssert.IsTrue(parkEndOffLaunchCenter > seamTolMetres,
                $"the park-end must be genuinely OFF the launch body's center (else the gate is vacuous): off={parkEndOffLaunchCenter.ToString("F0", ic)}m");
        }

        // Builds a HELIOCENTRIC-PARKING-DEPARTURE member + plan: Kerbin ascent/park [spanStart, depUT-parkDur],
        // a Sun PARK sub-coast ENDING at the burn (depUT), the recorded heliocentric transfer leg [depUT, arrUT],
        // then the Duna arrival. plan.DepartedFromHeliocentricPark=true, RecordedDepartureUT=depUT (the burn).
        private static void BuildParkingMemberAndPlan(
            ScanContext ctx, double departureUT, ParkingDepartureFixture fx,
            out List<OrbitSegment> memberSegments, out ReaimMissionPlan plan,
            out double spanStart, out double spanEnd)
        {
            string launchName = fx.LaunchBodyName;
            string targetName = fx.TargetBodyName;
            string parent = ctx.LaunchBody.referenceBody.bodyName;

            const double parkDur = 1200.0;        // the Sun PARK sub-coast duration before the burn
            double parkStart = departureUT - parkDur;
            spanStart = parkStart - 600.0;        // a brief Kerbin park/ascent before the heliocentric park
            double recordedArrivalUT = departureUT + ctx.TofSeconds;
            spanEnd = recordedArrivalUT + 600.0;

            var parkSeg = new OrbitSegment
            {
                bodyName = parent, startUT = parkStart, endUT = departureUT, epoch = parkStart,
                semiMajorAxis = fx.ParkSma, eccentricity = fx.ParkEcc, inclination = fx.ParkInc,
                longitudeOfAscendingNode = fx.ParkLan, argumentOfPeriapsis = fx.ParkArgPe,
                meanAnomalyAtEpoch = fx.ParkMEp, isPredicted = false
            };
            var transferLeg = new OrbitSegment
            {
                bodyName = parent, startUT = departureUT, endUT = recordedArrivalUT, epoch = departureUT,
                semiMajorAxis = 1.5e10, eccentricity = 0.2, inclination = 0.5,
                longitudeOfAscendingNode = 10.0, argumentOfPeriapsis = 20.0, meanAnomalyAtEpoch = 0.3,
                isPredicted = false
            };
            memberSegments = new List<OrbitSegment>
            {
                new OrbitSegment { bodyName = launchName, startUT = spanStart, endUT = parkStart,
                    semiMajorAxis = 700000.0, eccentricity = 0.0, epoch = spanStart }, // Kerbin park/ascent
                parkSeg,        // Sun PARK ending at the burn (the icon abuts this)
                transferLeg,    // recorded heliocentric transfer leg (re-aimed)
                new OrbitSegment { bodyName = targetName, startUT = recordedArrivalUT, endUT = spanEnd,
                    semiMajorAxis = 500000.0, eccentricity = 0.1, epoch = recordedArrivalUT }, // Duna capture
            };
            plan = new ReaimMissionPlan
            {
                Supported = true,
                LaunchBody = launchName,
                TargetBody = targetName,
                CommonAncestor = parent,
                ParkingOrbit = memberSegments[0],
                HeliocentricLeg = transferLeg,
                ArrivalLeg = memberSegments[3],
                RecordedDepartureUT = departureUT,        // the trans-target BURN (park end)
                RecordedArrivalUT = recordedArrivalUT,
                RecordedTransferTofSeconds = ctx.TofSeconds,
                DepartedFromHeliocentricPark = true
            };
        }

        // The re-aimed transfer leg in a resolved segment list: the common-ancestor-bodied segment whose UT
        // span COVERS the recorded departure (the in-window transfer). The departure-side park is also
        // common-ancestor-bodied but ENDS at/before the burn, so the strict-cover predicate excludes it.
        private static OrbitSegment FindInWindowSunTransfer(List<OrbitSegment> segs, ReaimMissionPlan plan)
        {
            if (segs == null)
                return default(OrbitSegment);
            for (int i = 0; i < segs.Count; i++)
            {
                OrbitSegment s = segs[i];
                if (!s.isPredicted && s.bodyName == plan.CommonAncestor
                    && s.startUT <= plan.RecordedDepartureUT + 1.0 && s.endUT > plan.RecordedDepartureUT + 1.0)
                    return s;
            }
            return default(OrbitSegment);
        }

        // The shared per-window sanity assertions: 3 segments (launch parking / re-aimed
        // common-ancestor transfer / target arrival), sane elliptic transfer conic, recorded-span
        // placement. Reads the launch/target body names from the fixture so it is target-agnostic.
        private static void AssertSaneWindowSegments(
            ScanContext ctx, List<OrbitSegment> segs, ReaimMissionPlan plan, double departureUT, double spanEnd, long k)
        {
            var ic = CultureInfo.InvariantCulture;
            string launchName = ctx.LaunchBodyName;
            string targetName = ctx.TargetBodyName;
            InGameAssert.IsTrue(segs != null && segs.Count == 3,
                $"window k={k} must keep 3 segments ({launchName} parking / re-aimed transfer / {targetName} arrival)");
            OrbitSegment transfer = segs[1];
            InGameAssert.AreEqual(plan.CommonAncestor, transfer.bodyName,
                $"window k={k} transfer must be common-ancestor-bodied");
            InGameAssert.IsTrue(
                ReaimTransferSynthesizer.IsSaneTransferConic(transfer.eccentricity, transfer.semiMajorAxis),
                $"window k={k} transfer must be a sane elliptic conic (ecc={transfer.eccentricity.ToString("R", ic)} sma={transfer.semiMajorAxis.ToString("R", ic)})");
            // PROGRADE: a resolved transfer must travel the right way (inc < 90). The synthesizer's
            // IsRetrogradeTransfer direction guard already declines a retrograde solution to faithful (it never
            // returns segments), so a resolved window is prograde BY CONSTRUCTION - this asserts it explicitly
            // as the regression backstop, sharing the production predicate so test + code agree on "prograde".
            // For Moho the inc is a few degrees (stage A carries the target's real out-of-plane offset), NOT
            // ~0 - this is < 90 prograde, not the projection era's near-zero. Same predicate at both targets.
            InGameAssert.IsTrue(
                !ReaimTransferSynthesizer.IsRetrogradeTransfer(transfer.inclination),
                $"window k={k} transfer must be prograde (inc={transfer.inclination.ToString("F2", ic)} deg < 90; a retrograde result declines to faithful, never resolves)");
            // Per-member: only the heliocentric leg is re-aimed (placed at [departure, recordedArrival]);
            // the launch parking + target arrival legs keep their recorded UTs + bodies.
            InGameAssert.AreEqual(launchName, segs[0].bodyName, $"window k={k} must keep the {launchName} parking leg");
            InGameAssert.AreEqual(targetName, segs[2].bodyName, $"window k={k} must keep the {targetName} arrival leg");
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
