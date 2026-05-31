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

        // S4 arrival-seam restitch (docs/dev/plans/reaim-arrival-seam-restitch.md). Synthesizes a Kerbin->
        // Duna transfer for the first encountering window, derives the re-aimed incoming approach frame
        // (s_re, h_re) from the transfer's Duna-relative state at SOI entry, builds a SYNTHETIC recorded
        // Duna arrival hyperbola with a DIFFERENT orientation, computes the restitch rotation R from the
        // recorded frame onto the re-aimed frame, rotates the synthetic arrival via the live element-
        // rotation pipeline (ReaimElementRotation.RotateSegmentOrientation, the same code the resolver
        // runs), and asserts the rotated arrival's incoming v_inf direction now matches the re-aimed
        // approach within tolerance (dot > 0.999). This is the load-bearing seam gate: before the restitch
        // the recorded arrival's v_inf pointed a different way (the ~1.37 Gm jump); after R it points the
        // re-aimed way. Runs at the Space Center; skips on a non-stock body graph.
        [InGameTest(Category = "Periodicity", Scene = GameScenes.SPACECENTER,
            Description = "Re-aim arrival-seam restitch (S4): rotating the recorded Duna arrival onto the re-aimed approach aligns the incoming v_inf direction (dot > 0.999)")]
        public void Reaim_ArrivalSeamRotation_AlignsIncomingVInf()
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
            double tof = TransferWindowMath.HohmannTransferTimeSeconds(
                kerbin.orbit.semiMajorAxis, duna.orbit.semiMajorAxis, muSun);
            double synodic = TransferWindowMath.SynodicPeriodSeconds(kerbin.orbit.period, duna.orbit.period);
            double now = Planetarium.GetUniversalTime();

            // Find the first window in a synodic period that actually encounters Duna.
            Orbit transfer = null;
            double soiEntryUT = double.NaN;
            const int steps = 36;
            for (int i = 0; i < steps && transfer == null; i++)
            {
                double tDep = now + (synodic * i) / steps;
                if (ReaimTransferSynthesizer.TrySynthesizeTransfer(
                        kerbin, duna, tDep, tof, prograde: true,
                        out Orbit t, out double soiUT, out CelestialBody enc, out string _)
                    && enc == duna && !double.IsNaN(soiUT))
                {
                    transfer = t;
                    soiEntryUT = soiUT;
                }
            }
            if (transfer == null)
            {
                InGameAssert.Skip("no Kerbin->Duna encounter window found this synodic period (geometry-dependent)");
                return;
            }

            // Re-aimed incoming approach frame from the transfer's Duna-relative state at SOI entry.
            bool reOk = ReaimElementRotation.TryReaimedArrivalFrame(
                transfer, duna, soiEntryUT, out double[] sRe, out double[] hRe, out double reEcc);
            InGameAssert.IsTrue(reOk && sRe != null && hRe != null,
                "re-aimed arrival frame must derive (transfer enters Duna SOI hyperbolically)");
            InGameAssert.IsTrue(reEcc > 1.0, "re-aimed Duna approach must be hyperbolic (ecc > 1)");

            // Build a SYNTHETIC recorded Duna arrival hyperbola with a deliberately DIFFERENT orientation
            // (a recorded approach from another bearing - the seam the restitch must close). Hyperbolic
            // (sma < 0, ecc > 1), Duna-relative, epoch near the SOI-entry instant.
            var recordedArrival = new OrbitSegment
            {
                inclination = 35.0,                 // off the re-aimed plane
                eccentricity = 1.8,
                semiMajorAxis = -4.0e6,             // hyperbolic
                longitudeOfAscendingNode = 110.0,
                argumentOfPeriapsis = 60.0,
                meanAnomalyAtEpoch = -0.6,          // inbound branch (pre-periapsis)
                epoch = soiEntryUT,
                bodyName = "Duna",
                isPredicted = false,
                orbitalFrameRotation = Quaternion.identity
            };

            // Recorded incoming approach frame, then the restitch rotation R (recorded -> re-aimed).
            bool recOk = ReaimElementRotation.TryRecordedArrivalFrame(
                recordedArrival, duna, out double[] sRec, out double[] hRec, out double recEcc);
            InGameAssert.IsTrue(recOk && sRec != null && hRec != null,
                "recorded synthetic arrival frame must derive (hyperbolic approach)");

            double handednessDot = ReaimRotation.Dot(hRec, hRe);
            // The synthetic recorded normal may be opposed to the re-aimed normal; if so, flip the synthetic
            // arrival's plane so the test exercises the rotate path rather than the (correct) faithful skip.
            if (handednessDot <= 0.0)
            {
                recordedArrival.inclination = 180.0 - recordedArrival.inclination; // flip the normal sense
                recOk = ReaimElementRotation.TryRecordedArrivalFrame(
                    recordedArrival, duna, out sRec, out hRec, out recEcc);
                InGameAssert.IsTrue(recOk, "recorded frame must re-derive after normal flip");
                handednessDot = ReaimRotation.Dot(hRec, hRe);
            }
            InGameAssert.IsTrue(handednessDot > 0.0,
                "handedness guard: recorded and re-aimed plane normals must share sense for the rotate path");

            double[,] r = ReaimRotation.RotationFrameToFrame(sRec, hRec, sRe, hRe);
            InGameAssert.IsTrue(r != null, "restitch rotation R must build from non-degenerate frames");

            // Rotate the synthetic recorded arrival via the SAME live element-rotation pipeline the resolver
            // runs, then re-derive its incoming approach frame and compare its v_inf direction to s_re.
            OrbitSegment rotated = ReaimElementRotation.RotateSegmentOrientation(recordedArrival, duna, r);
            bool rotOk = ReaimElementRotation.TryRecordedArrivalFrame(
                rotated, duna, out double[] sRot, out double[] hRot, out double rotEcc);
            InGameAssert.IsTrue(rotOk && sRot != null,
                "rotated arrival frame must derive (rigid rotation preserves the hyperbola)");

            double vinfDot = ReaimRotation.Dot(ReaimRotation.Normalize(sRot), ReaimRotation.Normalize(sRe));
            double normalDot = ReaimRotation.Dot(ReaimRotation.Normalize(hRot), ReaimRotation.Normalize(hRe));
            // Shape must be preserved by the rigid rotation (ecc unchanged to round-off).
            double eccDelta = System.Math.Abs(rotEcc - recEcc);

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            ParsekLog.Info("ReaimCanary",
                $"S4 seam rotation: R-angle={(ReaimRotation.RotationAngleRadians(r) * 180.0 / System.Math.PI).ToString("F2", ic)}deg " +
                $"handednessDot={handednessDot.ToString("F4", ic)} vinfDot={vinfDot.ToString("F6", ic)} " +
                $"normalDot={normalDot.ToString("F6", ic)} recEcc={recEcc.ToString("F4", ic)} " +
                $"rotEcc={rotEcc.ToString("F4", ic)} reEcc={reEcc.ToString("F4", ic)} soiEntryUT={soiEntryUT.ToString("F1", ic)}");

            InGameAssert.IsTrue(vinfDot > 0.999,
                $"rotated arrival's incoming v_inf must align with the re-aimed approach (dot={vinfDot.ToString("F6", ic)} > 0.999). " +
                "If this fails the arrival seam is NOT closed: the recorded approach still points a different way.");
            InGameAssert.IsTrue(normalDot > 0.999,
                $"rotated arrival's plane normal must align with the re-aimed plane (dot={normalDot.ToString("F6", ic)} > 0.999, full-frame match).");
            InGameAssert.IsTrue(eccDelta < 1.0e-3,
                $"rigid rotation must preserve eccentricity (delta={eccDelta.ToString("R", ic)}).");
        }
    }
}
