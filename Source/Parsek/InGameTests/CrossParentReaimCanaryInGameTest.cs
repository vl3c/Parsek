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
    }
}
