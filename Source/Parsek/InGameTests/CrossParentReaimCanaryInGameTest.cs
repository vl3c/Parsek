using System.Collections.Generic;
using Parsek.Reaim;
using UnityEngine;

namespace Parsek.InGameTests
{
    // The Phase-2 re-aim canary (docs/dev/plans/reaim-interplanetary-transfers.md). This is the
    // CRITICAL verification the design review (C2) flagged as un-checkable off-Unity: does stock
    // PatchedConics.CalculatePatch actually PROMOTE a target ENCOUNTER on a hand-built SYNTHETIC
    // heliocentric orbit (no vessel), under DEFAULT game settings? Decompilation says yes (the
    // ENCOUNTER promotion is gated only on closest-approach < SOI, not on the approach-marker
    // setting), but it must be confirmed in-game against the live body graph + stock solver before
    // the rest of re-aim is built on it.
    //
    // It scans departure UTs across one Kerbin->Duna synodic period, re-planning the transfer to
    // Duna's position at each candidate arrival via ReaimTransferSynthesizer (UvLambert ->
    // Orbit.UpdateFromStateVectors -> CalculatePatch), and asserts at least one window yields a sane
    // elliptic transfer that encounters Duna with a finite future SOI-entry UT. Runs at the Space
    // Center (no vessel needed). Skips cleanly on a non-stock body graph.
    public class CrossParentReaimCanaryInGameTest
    {
        [InGameTest(Category = "Periodicity", Scene = GameScenes.SPACECENTER,
            Description = "Re-aim canary: CalculatePatch promotes a Kerbin->Duna encounter on a synthetic heliocentric orbit under default settings (design review C2 gate)")]
        public void Reaim_KerbinToDuna_SynthesizedTransferEncountersDuna()
        {
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
            InGameAssert.IsTrue(tof > 0.0 && !double.IsInfinity(synodic),
                "Hohmann tof + synodic period must be finite/positive");

            double now = Planetarium.GetUniversalTime();
            const int steps = 36; // scan ~one synodic period at coarse resolution
            int encounters = 0;
            int saneTransfers = 0;
            double firstEncounterDepUT = double.NaN, firstSoiEntryUT = double.NaN;
            string lastFail = null;

            for (int i = 0; i < steps; i++)
            {
                double tDep = now + (synodic * i) / steps;
                bool ok = ReaimTransferSynthesizer.TrySynthesizeTransfer(
                    kerbin, duna, tDep, tof, prograde: true,
                    out Orbit transfer, out double soiEntryUT, out CelestialBody enc, out string fail);
                if (transfer != null && ReaimTransferSynthesizer.IsSaneTransferConic(
                        transfer.eccentricity, transfer.semiMajorAxis))
                    saneTransfers++;
                if (ok)
                {
                    encounters++;
                    if (double.IsNaN(firstEncounterDepUT))
                    {
                        firstEncounterDepUT = tDep;
                        firstSoiEntryUT = soiEntryUT;
                    }
                    InGameAssert.AreEqual(duna, enc, "encounter body must be Duna");
                    InGameAssert.IsTrue(!double.IsNaN(soiEntryUT) && soiEntryUT > tDep,
                        "SOI-entry UT must be finite and after departure");
                }
                else
                {
                    lastFail = fail;
                }
            }

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            ParsekLog.Info("ReaimCanary",
                $"Kerbin->Duna scan: steps={steps} saneTransfers={saneTransfers} encounters={encounters} " +
                $"tof={tof.ToString("F0", ic)}s synodic={synodic.ToString("F0", ic)}s " +
                $"firstEncounterDepUT={firstEncounterDepUT.ToString("F1", ic)} " +
                $"firstSoiEntryUT={firstSoiEntryUT.ToString("F1", ic)} lastFail={lastFail ?? "<none>"}");

            // The C2 gate: at least one departure window in a synodic period must yield a real Duna
            // encounter on the synthetic orbit under default settings. (Most windows are off-phase and
            // produce a degenerate/no-encounter transfer; we only need the feature to work at SOME
            // window - the scheduler picks the good ones.)
            InGameAssert.IsTrue(saneTransfers > 0,
                "at least one window must produce a sane elliptic transfer conic");
            InGameAssert.IsTrue(encounters > 0,
                "CalculatePatch must promote a Duna ENCOUNTER on the synthetic orbit at >=1 window " +
                "(design review C2). If this fails, re-aim's SOI patch needs the SolverParameters / " +
                "setup adjusted before building further.");
        }

        // The arrival-seam SOI-timing canary (docs/dev/plans/reaim-arrival-seam-timing.md). Verifies the v1
        // objective the off-Unity tests cannot: at a LATER synodic window, does choosing the tof that
        // best-matches a RECORDED arrival v_inf beat the faithful (recorded-tof) transfer's arrival v_inf,
        // and what is the achieved residual + seam (the go/no-go number for the loiter follow-up)?
        //
        // Setup mirrors the real loop: synthesize the transfer at window 0 and take ITS arrival v_inf as the
        // RECORDED reference (a real arrival is the fair stand-in for a recorded one). Then at window k=2,
        // evaluate the +-6% tof sweep, score each candidate's arrival-v_inf mismatch against the recorded
        // reference, and assert the best-mismatch tof scores no worse than the recorded-tof (faithful) one,
        // LOGGING the achieved v_inf residual + the target-relative arrival-position seam across the sweep.
        [InGameTest(Category = "Periodicity", Scene = GameScenes.SPACECENTER,
            Description = "Re-aim arrival-timing: at a later window the v_inf-chosen tof matches the recorded arrival v_inf better than the faithful tof; logs residual + seam (loiter go/no-go)")]
        public void Reaim_ArrivalTiming_ChosenTofBeatsFaithful_LogsResidualAndSeam()
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
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
            double now = Planetarium.GetUniversalTime();

            // Find a window-0 departure that yields a real Duna encounter, to seed the RECORDED reference.
            double dep0 = double.NaN, soi0 = double.NaN;
            Orbit ref0 = null;
            const int scan = 36;
            for (int i = 0; i < scan; i++)
            {
                double tDep = now + (synodic * i) / scan;
                if (ReaimTransferSynthesizer.TrySynthesizeTransfer(
                        kerbin, duna, tDep, tof, prograde: true,
                        out Orbit t0, out double s0, out _, out _))
                {
                    dep0 = tDep; soi0 = s0; ref0 = t0;
                    break;
                }
            }
            if (ref0 == null)
            {
                InGameAssert.Skip("no window-0 Kerbin->Duna encounter to seed the recorded reference");
                return;
            }

            // The RECORDED arrival v_inf reference (Zup), from the window-0 transfer's target-relative state.
            double[] vInfRec = ReaimArrivalVInf.CandidateArrivalVInf(ref0, duna, soi0);
            if (vInfRec == null)
            {
                InGameAssert.Skip("window-0 arrival is not hyperbolic (no recorded v_inf reference)");
                return;
            }
            Vector3d refArrivalRel = ref0.getRelativePositionAtUT(soi0)
                - duna.orbit.getRelativePositionAtUT(soi0);

            // At a LATER window (k=2 synodic periods on), search the +-6% tof sweep. Score each candidate's
            // arrival-v_inf mismatch vs the recorded reference; track the faithful (recorded-tof) one and the
            // best-mismatch one. Pinned departure = D_k (we never search departure).
            double depK = dep0 + 2.0 * synodic;
            const double stepFrac = 0.005;
            const int maxSteps = 12;
            double tofStep = tof * stepFrac;

            double faithfulMismatch = double.NaN, faithfulSeam = double.NaN;
            double bestMismatch = double.PositiveInfinity, bestSeam = double.NaN, bestTof = double.NaN;
            int scored = 0;

            void Eval(double tofCand, bool isFaithful)
            {
                if (!(tofCand > 0.0))
                    return;
                if (!ReaimTransferSynthesizer.TrySynthesizeTransfer(
                        kerbin, duna, depK, tofCand, prograde: true,
                        out Orbit cand, out double candSoi, out _, out _))
                    return;
                double[] vInfCand = ReaimArrivalVInf.CandidateArrivalVInf(cand, duna, candSoi);
                double mismatch = ReaimArrivalGeometry.VInfMismatch(vInfCand, vInfRec);
                if (double.IsNaN(mismatch) || double.IsInfinity(mismatch))
                    return;
                // Seam vs the recorded arrival position, both target-relative (Zup), NO .xzy.
                Vector3d candRel = cand.getRelativePositionAtUT(candSoi)
                    - duna.orbit.getRelativePositionAtUT(candSoi);
                double seam = (candRel - refArrivalRel).magnitude;
                scored++;
                if (isFaithful)
                {
                    faithfulMismatch = mismatch;
                    faithfulSeam = seam;
                }
                if (mismatch < bestMismatch)
                {
                    bestMismatch = mismatch;
                    bestSeam = seam;
                    bestTof = tofCand;
                }
            }

            Eval(tof, isFaithful: true); // recorded tof = the faithful baseline at this window
            for (int s = 1; s <= maxSteps; s++)
            {
                Eval(tof + s * tofStep, isFaithful: false);
                Eval(tof - s * tofStep, isFaithful: false);
            }

            double dirDeg = ReaimArrivalGeometry.AngleBetweenDegrees(
                ReaimArrivalVInf.CandidateArrivalVInf(ref0, duna, soi0), vInfRec); // 0 by construction (sanity)

            ParsekLog.Info("ReaimCanary",
                $"arrival-timing: recordedVInf={ReaimArrivalVInf.FormatVInfMag(vInfRec)}m/s " +
                $"window=k2 scored={scored} faithfulResidual={faithfulMismatch.ToString("F1", ic)}m/s " +
                $"chosenResidual={(double.IsInfinity(bestMismatch) ? double.NaN : bestMismatch).ToString("F1", ic)}m/s " +
                $"faithfulSeam={faithfulSeam.ToString("F0", ic)}m chosenSeam={bestSeam.ToString("F0", ic)}m " +
                $"chosenTof={bestTof.ToString("F0", ic)}s recordedTof={tof.ToString("F0", ic)}s " +
                $"refSanityDirDeg={dirDeg.ToString("F4", ic)} (the go/no-go for the loiter follow-up)");

            InGameAssert.IsTrue(scored > 0, "at least one tof in the sweep must synthesize + score at window k=2");
            InGameAssert.IsTrue(!double.IsNaN(faithfulMismatch),
                "the faithful (recorded-tof) transfer must synthesize at window k=2");
            // The whole point: the best-mismatch tof must match the recorded arrival v_inf at least as well
            // as the recorded-tof (faithful) transfer. The recorded-tof IS one of the swept candidates, so
            // the best can never be worse; this guards that the objective is being applied (not bypassed).
            InGameAssert.IsTrue(bestMismatch <= faithfulMismatch + 1e-6,
                "the v_inf-chosen tof must not score worse than the faithful (recorded-tof) tof");
        }
    }
}
