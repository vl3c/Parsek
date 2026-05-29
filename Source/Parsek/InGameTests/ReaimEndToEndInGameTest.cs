using System.Globalization;
using Parsek.Reaim;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 3c end-to-end re-aim verification (docs/dev/plans/reaim-interplanetary-transfers.md).
    // Where the C2 canary proves a SINGLE synthesized transfer can encounter Duna, this proves the
    // CONGRUENT-WINDOW model end to end: build a re-aim plan + synodic schedule anchored on a
    // known-good Kerbin->Duna departure (found by scanning), then drive the live
    // ReaimPlaybackResolver.TryResolveWindowSegments for several consecutive synodic windows and assert
    // EVERY window resolves a sane 3-segment re-aimed trajectory (parking / Sun transfer / Duna
    // arrival). This is the load-bearing claim of the model: because each window is congruent to the
    // recorded departure and uses the RECORDED tof, the transfer stays sane at every window (not just
    // ~7/36 of arbitrary departures), and the per-window orientation rotates while the shape is fixed.
    // Runs at the Space Center (no vessel). Skips cleanly on a non-stock body graph.
    public class ReaimEndToEndInGameTest
    {
        [InGameTest(Category = "Periodicity", Scene = GameScenes.SPACECENTER,
            Description = "Re-aim end-to-end: every synodic window resolves a sane re-aimed Kerbin->Duna transfer (congruent-window model)")]
        public void Reaim_KerbinToDuna_EveryWindowResolvesSaneTransfer()
        {
            var ic = CultureInfo.InvariantCulture;
            CelestialBody kerbin = FlightGlobals.Bodies?.Find(b => b.bodyName == "Kerbin");
            CelestialBody duna = FlightGlobals.Bodies?.Find(b => b.bodyName == "Duna");
            if (kerbin == null || duna == null)
            {
                InGameAssert.Skip("Kerbin/Duna not in FlightGlobals.Bodies (non-stock pack)");
                return;
            }
            if (kerbin.referenceBody == null || kerbin.referenceBody != duna.referenceBody)
            {
                InGameAssert.Skip("Kerbin and Duna do not share a parent in this pack");
                return;
            }

            double muSun = kerbin.referenceBody.gravParameter;
            double aK = kerbin.orbit.semiMajorAxis, aD = duna.orbit.semiMajorAxis;
            double pK = kerbin.orbit.period, pD = duna.orbit.period;
            double tof = TransferWindowMath.HohmannTransferTimeSeconds(aK, aD, muSun);
            double synodic = TransferWindowMath.SynodicPeriodSeconds(pK, pD);
            InGameAssert.IsTrue(tof > 0.0 && !double.IsInfinity(synodic) && synodic > 0.0,
                "Hohmann tof + synodic period must be finite/positive");

            // 1. Find a KNOWN-GOOD departure: scan one synodic period for the first departure whose
            //    (Hohmann-tof) transfer encounters Duna. This stands in for the recorded mission's
            //    actual departure; the congruent-window model relaunches at this geometry every synodic.
            double now = Planetarium.GetUniversalTime();
            double goodDep = double.NaN;
            const int steps = 48;
            for (int i = 0; i < steps; i++)
            {
                double tDep = now + (synodic * i) / steps;
                if (ReaimTransferSynthesizer.TrySynthesizeTransfer(
                        kerbin, duna, tDep, tof, prograde: true,
                        out _, out _, out _, out _))
                {
                    goodDep = tDep;
                    break;
                }
            }
            if (double.IsNaN(goodDep))
            {
                InGameAssert.Skip("no good Kerbin->Duna departure found in a synodic scan (unexpected on stock)");
                return;
            }

            // 2. Build a synthetic re-aim plan anchored on that departure: Kerbin parking just before,
            //    Sun heliocentric leg = [goodDep, goodDep+tof], Duna arrival just after.
            double spanStart = goodDep - 600.0;
            double recordedArrivalUT = goodDep + tof;
            double spanEnd = recordedArrivalUT + 600.0;
            var plan = new ReaimMissionPlan
            {
                Supported = true,
                LaunchBody = "Kerbin",
                TargetBody = "Duna",
                CommonAncestor = kerbin.referenceBody.bodyName,
                ParkingOrbit = new OrbitSegment
                {
                    bodyName = "Kerbin", startUT = spanStart, endUT = goodDep,
                    semiMajorAxis = 700000.0, eccentricity = 0.0, epoch = spanStart
                },
                ArrivalLeg = new OrbitSegment
                {
                    bodyName = "Duna", startUT = recordedArrivalUT, endUT = spanEnd,
                    semiMajorAxis = 500000.0, eccentricity = 0.1, epoch = recordedArrivalUT
                },
                RecordedDepartureUT = goodDep,
                RecordedArrivalUT = recordedArrivalUT,
                RecordedTransferTofSeconds = tof
            };

            // 3. Plan the synodic schedule (congruent windows = goodDep + k*synodic, recorded tof).
            ReaimWindowPlanner.ReaimWindowSchedule sched = ReaimWindowPlanner.Plan(
                pK, pD, goodDep, tof, spanStart, spanEnd, referenceUT: goodDep - 1.0);
            InGameAssert.IsTrue(sched.Valid, "window schedule must be valid: " + (sched.Reason ?? ""));
            InGameAssert.IsTrue(System.Math.Abs(sched.FirstDepartureUT - goodDep) < 1.0,
                "window 0 should be the recorded departure");

            // 4. Drive the resolver for several consecutive windows; assert EVERY one resolves a sane
            //    3-segment re-aimed trajectory. Use a fresh member id so the cache is clean.
            ReaimPlaybackResolver.Shared.Clear();
            string memberId = "reaim-e2e-" + goodDep.ToString("F0", ic);
            double span = spanEnd - spanStart;
            const int windowsToCheck = 5;
            int resolved = 0;
            double firstEcc = double.NaN, firstSma = double.NaN;
            double window0Lan = double.NaN, lastLan = double.NaN;
            for (long k = 0; k < windowsToCheck; k++)
            {
                // A live UT that lands inside window k's recorded span (phaseAnchor + k*cadence + mid-span).
                double currentUT = sched.PhaseAnchorUT + k * sched.CadenceSeconds + 0.5 * span;
                bool ok = ReaimPlaybackResolver.Shared.TryResolveWindowSegments(
                    memberId, plan, sched,
                    sched.PhaseAnchorUT, spanStart, spanEnd, sched.CadenceSeconds, currentUT,
                    out System.Collections.Generic.List<OrbitSegment> segs, out long windowIndex);

                InGameAssert.IsTrue(ok, $"window k={k} must resolve a re-aimed transfer (congruent-window model)");
                InGameAssert.AreEqual(k, windowIndex, $"resolved window index must equal k={k}");
                InGameAssert.IsTrue(segs != null && segs.Count == 3,
                    $"window k={k} must assemble 3 segments (parking/transfer/arrival)");

                OrbitSegment transfer = segs[1];
                InGameAssert.AreEqual(plan.CommonAncestor, transfer.bodyName,
                    $"window k={k} transfer must be Sun-bodied");
                InGameAssert.IsTrue(
                    ReaimTransferSynthesizer.IsSaneTransferConic(transfer.eccentricity, transfer.semiMajorAxis),
                    $"window k={k} transfer must be a sane elliptic conic (ecc={transfer.eccentricity.ToString("R", ic)} sma={transfer.semiMajorAxis.ToString("R", ic)})");
                // Recorded-span placement: transfer occupies [goodDep, goodDep+tof], arrival ends at spanEnd.
                InGameAssert.IsTrue(System.Math.Abs(segs[1].startUT - goodDep) < 1.0,
                    $"window k={k} transfer must start at the recorded departure (recorded-span)");
                InGameAssert.IsTrue(System.Math.Abs(segs[2].endUT - spanEnd) < 1.0,
                    $"window k={k} arrival must end at the recorded span end (fits span, no clip)");

                if (k == 0) { firstEcc = transfer.eccentricity; firstSma = transfer.semiMajorAxis; window0Lan = transfer.longitudeOfAscendingNode; }
                lastLan = transfer.longitudeOfAscendingNode;
                resolved++;
            }

            ParsekLog.Info("ReaimE2E",
                $"Kerbin->Duna congruent windows: checked={windowsToCheck} resolved={resolved} " +
                $"goodDep={goodDep.ToString("F1", ic)} tof={tof.ToString("F0", ic)} synodic={synodic.ToString("F0", ic)} " +
                $"ecc0={firstEcc.ToString("R", ic)} sma0={firstSma.ToString("R", ic)} " +
                $"lan0={window0Lan.ToString("F2", ic)} lanLast={lastLan.ToString("F2", ic)}");

            InGameAssert.AreEqual(windowsToCheck, resolved,
                "every congruent synodic window must resolve a sane re-aimed transfer (the model's core claim)");
            // Re-aimed, not transplanted: the conic SHAPE is preserved across windows (congruent) while
            // the inertial ORIENTATION rotates (it aims at where Duna actually is each window).
            InGameAssert.IsTrue(System.Math.Abs(lastLan - window0Lan) > 1.0,
                "the transfer orientation must rotate across windows (re-aimed at the live target), not be identical");
        }
    }
}
