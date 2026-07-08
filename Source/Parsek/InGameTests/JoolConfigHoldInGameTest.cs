using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Reaim;

namespace Parsek.InGameTests
{
    /// <summary>
    /// (M-MIS-6 multi-moon configuration-hold gate) The one automated proof that a looped Jool
    /// tour touching the resonant inner three moons (Laythe:Vall:Tylo, the 1:2:4 lock) engages the
    /// per-loop T_config configuration hold - and that an incommensurate moon set (adding Bop)
    /// degrades to faithful (fail-closed, amber) - against the LIVE stock body graph.
    ///
    /// <para><b>The gap this closes (from the M-MIS-6 merge gate, verbatim intent).</b> The PR's
    /// stated gate was "an in-game looped Jool tour playtest... headless fixtures cannot
    /// substitute". The reason headless genuinely cannot substitute is the LIVE body graph: the
    /// engage decision turns on the REAL Jool/Laythe/Vall/Tylo/Bop orbital periods, SOI radii and
    /// orbital velocities actually satisfying (or not) the resonance within each moon's SOI
    /// tolerance. The M-MIS-6 xUnit fixtures (<c>MultiMoonAlignmentTests</c>) PIN those values as
    /// stock-derived constants (<c>LaytheOrbit = 52980.9</c>, ...); they prove the math, never that
    /// the shipped game's ephemerides match. This test reads the periods/SOI/velocities from
    /// <c>FlightGlobalsBodyInfo.Instance</c> (which reads live <c>FlightGlobals</c> bodies) and
    /// drives the SAME production entry point the fixtures do -
    /// <c>ArrivalHoldPlanner.ComputeArrivalHold</c> - through the REAL
    /// <c>DestinationConstraintExtractor</c> + <c>DestinationArrivalSolver</c> +
    /// <c>MissionPeriodicity</c> chain, this time against the live geometry the fixtures stand in
    /// for. This is the precedent that lifted the M5 merge hold
    /// (<c>RouteInterBodyBuilderShapeInGameTest</c>): an in-game synthetic-input test gets exactly
    /// the live body graph a headless run cannot.
    ///
    /// <para><b>Why in-game (not xUnit).</b> The config-hold engage decision reads the live moon
    /// periods (<c>bodyInfo.OrbitPeriod</c>), the live SOI tolerances
    /// (<c>SoiRadius / OrbitalVelocity</c>) and the live parent chain
    /// (<c>ReferenceBodyName</c> = Jool for the moons) through
    /// <c>FlightGlobalsBodyInfo.Instance</c>. In xUnit <c>FlightGlobals</c> is empty, so the
    /// fixtures MUST inject a hand-built <c>IBodyInfo</c> with pinned constants - the only way
    /// headless reaches the branch, and by construction never a proof about the shipped game.
    ///
    /// <para><b>Live-vs-synthetic boundary.</b> The recorded-mission TIME BASE (recorded arrival
    /// UT, phase anchor, span start, loop slack) is a body-independent stand-in mirroring the
    /// fixtures' <c>Compute()</c> - the geometry re-solves against the live ephemerides, so those
    /// numbers do not need to be "real". What ONLY the in-game run proves: that the shipped
    /// Laythe/Vall/Tylo periods actually lock 1:2:4 within their SOI tolerances so
    /// <c>T_config = k * P_anchor</c> lands within one Tylo period and engages, and that Bop is
    /// genuinely incommensurate so the four-moon set fails closed. The MissionLoopUnitBuilder
    /// segment-&gt;constraint extraction is exercised by the M5 gate for the re-aim routing
    /// generally; the config-hold contract lives entirely in <c>ComputeArrivalHold</c> and below,
    /// which this drives against live periods (the same entry the fixtures use).
    ///
    /// <para><b>Isolation.</b> No store / ledger / route mutation whatsoever - the test calls a
    /// pure planner with a live-read <c>IBodyInfo</c>. The only seams touched are the log sink,
    /// <c>ParsekLog.SuppressLogging</c> / <c>VerboseOverrideForTesting</c> and
    /// <c>MissionPeriodicity.SuppressLogging</c> (so the config-hold engage line reaches the
    /// capturing sink); every one is snapshotted and restored in <c>finally</c> regardless of
    /// pass / fail / skip. Batch-safe at the Space Center, ordinary Run-All executable.
    /// </summary>
    public sealed class JoolConfigHoldInGameTest
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private const string Tag = "TestRunner";

        // The recorded-mission time base: body-independent stand-ins mirroring the M-MIS-6 xUnit
        // Compute() (MultiMoonAlignmentTests). The config hold re-solves the geometry against the
        // live ephemerides, so these are pure clock drivers - the recorded arrival is the
        // heliocentric->capture boundary the hold is inserted at, the phase anchor / span start map
        // the recorded span to the live clock, and the loop slack is a wide idle gap (well above one
        // anchor period so the hold budget is not slack-clamped).
        private const double RecordedArrivalUT = 1.0e6;
        private const double PhaseAnchorUT = 350.0;
        private const double SpanStartUT = 0.0;
        private const double LoopSlackSeconds = 5.0e6;
        private const string LaunchBody = "Kerbin";

        // === Test A: the resonant inner-three tour engages the T_config configuration hold ========

        [InGameTest(Category = "Missions", Scene = GameScenes.SPACECENTER,
            Description = "M-MIS-6 multi-moon config-hold gate (LIVE Jool): a Kerbin->Jool tour "
                + "touching the resonant inner three (Laythe:Vall:Tylo 1:2:4) drives the REAL "
                + "ArrivalHoldPlanner.ComputeArrivalHold against FlightGlobalsBodyInfo and engages "
                + "the per-loop configuration hold on T_config = k*P_anchor (~one live Tylo period), "
                + "with the single-period per-loop hold re-aligning every moon encounter within its "
                + "live SOI tolerance across the aligned horizon")]
        public void ResonantInnerThree_EngagesConfigHold_AgainstLiveJool()
        {
            JoolSystem sys = ResolveResonantJoolSystemOrSkip();
            var bi = FlightGlobalsBodyInfo.Instance;
            double windowSpacing = sys.KerbinJoolSynodicSeconds;

            List<PhaseConstraint> tour = InnerThreeTourLive(bi);

            // The anchor the planner will pick (smallest-duty participant, MissionPeriodicity's own
            // rule): compute it via the REAL selector on the same moon set so the whole-multiple
            // assertion below is self-documenting (Vall for the stock inner three).
            var moonsOnly = new List<PhaseConstraint>
            {
                Orbital(bi, "Laythe"), Orbital(bi, "Vall"), Orbital(bi, "Tylo"),
            };
            int anchorIdx = MissionPeriodicity.SelectAnchorConstraintIndex(
                moonsOnly, bi, LaunchBody, TransitedBodyRotationMode.Loose);
            double pAnchor = moonsOnly[anchorIdx].PeriodSeconds;
            string anchorBody = moonsOnly[anchorIdx].BodyName;

            var captured = new List<string>();
            System.Action<string> prevSink = ParsekLog.TestSinkForTesting;
            bool prevLogSuppress = ParsekLog.SuppressLogging;
            bool? prevVerbose = ParsekLog.VerboseOverrideForTesting;
            bool prevMpSuppress = MissionPeriodicity.SuppressLogging;
            try
            {
                ParsekLog.SuppressLogging = false;
                ParsekLog.VerboseOverrideForTesting = true;      // the engage line is Verbose
                MissionPeriodicity.SuppressLogging = false;      // the engage line is gated on this
                ParsekLog.TestSinkForTesting = line => captured.Add(line);

                ArrivalHoldPlanner.ArrivalHoldResult r = ArrivalHoldPlanner.ComputeArrivalHold(
                    tour, "Jool", RecordedArrivalUT, TransitedBodyRotationMode.Loose,
                    PhaseAnchorUT, SpanStartUT, null, bi,
                    windowSpacingSeconds: windowSpacing,
                    launchBodyName: LaunchBody,
                    loopSlackSeconds: LoopSlackSeconds);

                // (1) The multi-moon configuration hold engaged (not station, not joint, no amber).
                InGameAssert.IsTrue(r.Applied,
                    "the resonant inner-three Jool tour must ENGAGE the configuration hold against "
                    + "the live body graph (Applied=false => the tour silently loops un-aligned)");
                InGameAssert.IsTrue(r.IsConfigHold,
                    "the engaged hold must be the multi-moon CONFIGURATION hold (IsConfigHold)");
                InGameAssert.IsFalse(r.IsStationHold, "a no-station moon tour is not a station hold");
                InGameAssert.IsFalse(r.IsJointHold, "a no-station moon tour is not a joint landing+station hold");
                InGameAssert.IsNull(r.AmberReason, "an engaged configuration hold carries no amber reason");
                InGameAssert.AreEqual(3, r.ConfigMoonCount,
                    "the constrained-moon count must be the three inner moons (Laythe/Vall/Tylo)");

                // (2) T_config lies where the design says: a WHOLE multiple of the live anchor period
                //     (k*P_anchor, the joint-configuration recurrence) and ~one LIVE Tylo period (the
                //     1:2:4 lock). Both derived from FlightGlobals, not pinned constants.
                double tConfig = r.AlignPeriodSeconds;
                double kRaw = tConfig / pAnchor;
                long k = (long)Math.Round(kRaw);
                InGameAssert.IsTrue(k >= 1 && Math.Abs(kRaw - k) <= 1e-6,
                    $"T_config ({tConfig.ToString("R", IC)}s) must be a whole multiple of the live anchor "
                    + $"'{anchorBody}' period ({pAnchor.ToString("R", IC)}s); got k={kRaw.ToString("R", IC)}");
                double pTylo = bi.OrbitPeriod("Tylo");
                double tyloSoiTol = MoonSoiToleranceSeconds(bi, "Tylo");
                InGameAssert.ApproxEqual(pTylo, tConfig, tyloSoiTol,
                    $"T_config ({tConfig.ToString("R", IC)}s) must land within one LIVE Tylo period "
                    + $"({pTylo.ToString("R", IC)}s, SOI tol {tyloSoiTol.ToString("F1", IC)}s) - the 1:2:4 lock");

                // (3) The hold value is a real forward defer in [0, T_config], inserted at the recorded
                //     heliocentric->capture boundary.
                InGameAssert.IsTrue(r.HoldSeconds > 0.0 && r.HoldSeconds <= tConfig + 1e-6,
                    $"the hold must lie in (0, T_config], got {r.HoldSeconds.ToString("R", IC)}s");
                InGameAssert.ApproxEqual(RecordedArrivalUT, r.HoldAtUT, 1e-3,
                    "the hold is inserted at the recorded arrival UT (the heliocentric->capture boundary)");

                // (4) The encounter-alignment property against LIVE periods: with the shipped
                //     single-period per-loop hold on T_config, every replayed loop's total live shift
                //     sits within each moon's SOI tolerance of a whole number of that moon's periods -
                //     i.e. every moon is where the recording had it, within SOI-crossing time, across
                //     the aligned horizon. This re-derives MultiMoonAlignmentTests'
                //     PerLoopHold_AlignsEveryEncounterWithinSoiTolerance with the LIVE ephemerides.
                double cadence = windowSpacing;
                double entryOffset0 = PhaseAnchorUT - SpanStartUT; // no loiter cuts
                var moons = new (string body, double period, double tol)[]
                {
                    ("Laythe", bi.OrbitPeriod("Laythe"), MoonSoiToleranceSeconds(bi, "Laythe")),
                    ("Vall", bi.OrbitPeriod("Vall"), MoonSoiToleranceSeconds(bi, "Vall")),
                    ("Tylo", bi.OrbitPeriod("Tylo"), MoonSoiToleranceSeconds(bi, "Tylo")),
                };
                double worstErr = 0.0;
                string worstBody = "?";
                for (long n = 0; n <= 12; n++)
                {
                    double wN = GhostPlaybackLogic.ComputePerLoopArrivalHoldSeconds(
                        r.HoldSeconds, n, cadence, tConfig);
                    InGameAssert.IsTrue(wN >= 0.0 && wN <= tConfig + 1e-6,
                        $"loop {n.ToString(IC)}: per-loop hold {wN.ToString("R", IC)}s must stay in [0, T_config]");
                    double shift = entryOffset0 + n * cadence + wN;
                    foreach (var (body, period, tol) in moons)
                    {
                        double err = MissionPeriodicity.CircularPhaseError(shift, period);
                        if (err > worstErr) { worstErr = err; worstBody = body; }
                        InGameAssert.IsTrue(err <= tol,
                            $"loop {n.ToString(IC)}: {body} phase error {err.ToString("F1", IC)}s exceeds its "
                            + $"live SOI tolerance {tol.ToString("F1", IC)}s (the per-loop config hold must "
                            + "align every moon encounter)");
                    }
                }

                // (5) The config-hold engage log line reached the capturing sink (the logging
                //     priority: the engage decision is never silent).
                bool sawEngage = false;
                foreach (string line in captured)
                {
                    if (line.Contains("config-hold engage") && line.Contains("dest=Jool"))
                    {
                        sawEngage = true;
                        break;
                    }
                }
                InGameAssert.IsTrue(sawEngage,
                    "the 'config-hold engage dest=Jool' Verbose line must reach the log sink");

                ParsekLog.Info(Tag, string.Format(IC,
                    "JoolConfigHold A PASS (LIVE): engaged config hold dest=Jool moons={0} anchor={1} "
                    + "P_anchor={2}s k={3} T_config={4}s (~Tylo {5}s) hold={6}s alignedHorizon={7} "
                    + "worstMoonErr={8:F1}s({9})",
                    r.ConfigMoonCount.ToString(IC), anchorBody, pAnchor.ToString("R", IC), k.ToString(IC),
                    tConfig.ToString("R", IC), bi.OrbitPeriod("Tylo").ToString("R", IC),
                    r.HoldSeconds.ToString("R", IC), r.ConfigAlignedWindowHorizon.ToString(IC),
                    worstErr, worstBody));
            }
            finally
            {
                ParsekLog.TestSinkForTesting = prevSink;
                ParsekLog.SuppressLogging = prevLogSuppress;
                ParsekLog.VerboseOverrideForTesting = prevVerbose;
                MissionPeriodicity.SuppressLogging = prevMpSuppress;
            }
        }

        // === Test B: an incommensurate moon (Bop) fails the whole set closed to faithful =========

        [InGameTest(Category = "Missions", Scene = GameScenes.SPACECENTER,
            Description = "M-MIS-6 multi-moon config-hold gate (LIVE Jool): adding an INCOMMENSURATE "
                + "moon (Bop) to the resonant inner three makes the joint configuration non-recurring, "
                + "so the REAL ArrivalHoldPlanner.ComputeArrivalHold against FlightGlobalsBodyInfo "
                + "fails the whole set closed to faithful (Applied=false, hold=0) WITH an amber reason "
                + "naming the shape ('does not recur', 'Bop') - never a silent decline")]
        public void IncommensurateBop_FailsClosedFaithful_AgainstLiveJool()
        {
            JoolSystem sys = ResolveResonantJoolSystemOrSkip();
            var bi = FlightGlobalsBodyInfo.Instance;

            // Bop must be present and a child of Jool for the four-moon incommensurate shape.
            CelestialBody bop = FindBody("Bop");
            if (bop == null)
                InGameAssert.Skip("Bop not in FlightGlobals.Bodies (non-stock pack) - cannot exercise the "
                    + "incommensurate four-moon shape");
            if (bi.ReferenceBodyName("Bop") != "Jool")
                InGameAssert.Skip("Bop is not a child of Jool in this pack - not the incommensurate Jool moon");
            double pBop = bi.OrbitPeriod("Bop");
            if (double.IsNaN(pBop) || pBop <= 0.0)
                InGameAssert.Skip("Bop has degenerate live orbit data - cannot exercise the incommensurate shape");

            List<PhaseConstraint> tour = InnerThreeTourLive(bi);
            tour.Add(Orbital(bi, "Bop"));

            bool prevLogSuppress = ParsekLog.SuppressLogging;
            bool prevMpSuppress = MissionPeriodicity.SuppressLogging;
            try
            {
                ParsekLog.SuppressLogging = false;
                MissionPeriodicity.SuppressLogging = false;

                ArrivalHoldPlanner.ArrivalHoldResult r = ArrivalHoldPlanner.ComputeArrivalHold(
                    tour, "Jool", RecordedArrivalUT, TransitedBodyRotationMode.Loose,
                    PhaseAnchorUT, SpanStartUT, null, bi,
                    windowSpacingSeconds: sys.KerbinJoolSynodicSeconds,
                    launchBodyName: LaunchBody,
                    loopSlackSeconds: LoopSlackSeconds);

                // Fail-closed to faithful: no hold engaged, no config hold, zero hold seconds.
                InGameAssert.IsFalse(r.Applied,
                    "adding incommensurate Bop must FAIL the whole configuration hold closed to faithful");
                InGameAssert.IsFalse(r.IsConfigHold, "a declined set must not carry a config hold");
                InGameAssert.IsFalse(r.IsStationHold, "no station is involved");
                InGameAssert.IsFalse(r.IsJointHold, "no joint landing+station is involved");
                InGameAssert.ApproxEqual(0.0, r.HoldSeconds, 1e-9,
                    "a fail-closed configuration hold defers nothing (hold=0)");

                // The decline is NOT silent (the pre-M-MIS-6 bug): an amber reason names the shape.
                InGameAssert.IsNotNull(r.AmberReason,
                    "the incommensurate decline must carry an amber reason (never a silent None)");
                InGameAssert.Contains(r.AmberReason, "does not recur",
                    "the amber must state the joint configuration does not recur within tolerance");
                InGameAssert.Contains(r.AmberReason, "Bop",
                    "the amber must name Bop (the incommensurate participant in the described shape)");

                ParsekLog.Info(Tag, string.Format(IC,
                    "JoolConfigHold B PASS (LIVE): incommensurate Bop (P={0}s) failed the 4-moon set "
                    + "closed to faithful; amber='{1}'",
                    pBop.ToString("R", IC), r.AmberReason));
            }
            finally
            {
                ParsekLog.SuppressLogging = prevLogSuppress;
                MissionPeriodicity.SuppressLogging = prevMpSuppress;
            }
        }

        // -----------------------------------------------------------------
        // Live-system resolution + skip gate
        // -----------------------------------------------------------------

        private struct JoolSystem
        {
            public double KerbinJoolSynodicSeconds;
        }

        // Resolves the live stock Jool system and verifies the 1:2:4 resonance holds within the live
        // SOI tolerances (the design's own engage criterion), skipping cleanly on a non-stock pack /
        // rescaled resonance. Returns the live Kerbin<->Jool synodic spacing (the arrival window grid).
        private static JoolSystem ResolveResonantJoolSystemOrSkip()
        {
            var bi = FlightGlobalsBodyInfo.Instance;

            CelestialBody jool = FindBody("Jool");
            CelestialBody laythe = FindBody("Laythe");
            CelestialBody vall = FindBody("Vall");
            CelestialBody tylo = FindBody("Tylo");
            CelestialBody kerbin = FindBody("Kerbin");
            if (jool == null || laythe == null || vall == null || tylo == null || kerbin == null)
                InGameAssert.Skip("Jool/Laythe/Vall/Tylo/Kerbin not all present (non-stock pack) - cannot "
                    + "drive the live multi-moon configuration hold");

            // The moons must be children of Jool (the extractor's MoonConfig rule keys on the parent).
            if (bi.ReferenceBodyName("Laythe") != "Jool" || bi.ReferenceBodyName("Vall") != "Jool"
                || bi.ReferenceBodyName("Tylo") != "Jool")
                InGameAssert.Skip("Laythe/Vall/Tylo are not all children of Jool in this pack - not the "
                    + "stock Jool moon system");

            double pLaythe = bi.OrbitPeriod("Laythe");
            double pVall = bi.OrbitPeriod("Vall");
            double pTylo = bi.OrbitPeriod("Tylo");
            double pKerbin = bi.OrbitPeriod("Kerbin");
            double pJool = bi.OrbitPeriod("Jool");
            if (double.IsNaN(pLaythe) || double.IsNaN(pVall) || double.IsNaN(pTylo)
                || double.IsNaN(pKerbin) || double.IsNaN(pJool)
                || pLaythe <= 0.0 || pVall <= 0.0 || pTylo <= 0.0 || pKerbin <= 0.0 || pJool <= 0.0)
                InGameAssert.Skip("one of Kerbin/Jool/Laythe/Vall/Tylo has degenerate live orbit data - "
                    + "cannot drive the configuration hold");

            // The 1:2:4 lock, checked by the design's engage criterion: at the joint configuration
            // period T_config = 2*P_Vall the OTHER two moons must be within their live SOI tolerances.
            // (If this fails, the pack rescaled the resonance and the hold would honestly decline -
            // skip rather than assert a false negative.)
            double tConfigCandidate = 2.0 * pVall;
            double laytheErr = MissionPeriodicity.CircularPhaseError(tConfigCandidate, pLaythe);
            double tyloErr = MissionPeriodicity.CircularPhaseError(tConfigCandidate, pTylo);
            double laytheTol = MoonSoiToleranceSeconds(bi, "Laythe");
            double tyloTol = MoonSoiToleranceSeconds(bi, "Tylo");
            if (laytheErr > laytheTol || tyloErr > tyloTol)
                InGameAssert.Skip(string.Format(IC,
                    "the live Jool inner three do not lock 1:2:4 within SOI tolerance (Laythe err "
                    + "{0:F1}s vs tol {1:F1}s, Tylo err {2:F1}s vs tol {3:F1}s at 2*P_Vall={4}s) - "
                    + "rescaled pack, the configuration hold would honestly decline",
                    laytheErr, laytheTol, tyloErr, tyloTol, tConfigCandidate.ToString("R", IC)));

            // The live Kerbin<->Jool synodic period (the arrival window grid the hold-aware window
            // solve samples). Distinct periods are guaranteed above.
            double synodic = 1.0 / Math.Abs(1.0 / pKerbin - 1.0 / pJool);
            if (double.IsNaN(synodic) || double.IsInfinity(synodic) || synodic <= 0.0)
                InGameAssert.Skip("degenerate live Kerbin/Jool synodic period - cannot form the arrival window grid");

            return new JoolSystem { KerbinJoolSynodicSeconds = synodic };
        }

        // -----------------------------------------------------------------
        // Fixture builders (LIVE periods)
        // -----------------------------------------------------------------

        // The resonant inner-three tour as the extractor sees it: the target's own heliocentric SOI
        // entry (Orbital(Jool), EXCLUDED by the extractor) + the Laythe/Vall/Tylo SOI entries (the
        // MoonConfigs). Mirrors MultiMoonAlignmentTests.InnerThreeTour, reading every period LIVE.
        private static List<PhaseConstraint> InnerThreeTourLive(FlightGlobalsBodyInfo bi)
        {
            return new List<PhaseConstraint>
            {
                Orbital(bi, "Jool"),   // the target's own SOI entry (excluded by the extractor)
                Orbital(bi, "Laythe"),
                Orbital(bi, "Vall"),
                Orbital(bi, "Tylo"),
            };
        }

        // An Orbital SOI-entry constraint carrying the body's LIVE orbit period. RelativeToParent
        // is true (cross-parent relative to the Kerbin launch), matching the fixtures; the config
        // hold consumes only Kind/BodyName/PeriodSeconds.
        private static PhaseConstraint Orbital(FlightGlobalsBodyInfo bi, string body)
        {
            return new PhaseConstraint
            {
                Kind = ConstraintKind.Orbital,
                BodyName = body,
                PeriodSeconds = bi.OrbitPeriod(body),
                PhaseOffsetSeconds = 0.0,
                RelativeToParent = true,
            };
        }

        // The live SOI-crossing tolerance for a moon's Orbital constraint: SoiRadius / OrbitalVelocity
        // (the same band MissionPeriodicity.ToleranceSecondsFor derives for an Orbital constraint).
        private static double MoonSoiToleranceSeconds(FlightGlobalsBodyInfo bi, string body)
        {
            double soi = bi.SoiRadius(body);
            double vel = bi.OrbitalVelocity(body);
            if (double.IsNaN(soi) || double.IsNaN(vel) || vel <= 0.0)
                return double.PositiveInfinity;
            return soi / vel;
        }

        private static CelestialBody FindBody(string name)
        {
            if (FlightGlobals.Bodies == null)
                return null;
            for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
            {
                CelestialBody b = FlightGlobals.Bodies[i];
                if (b != null && b.bodyName == name)
                    return b;
            }
            return null;
        }
    }
}
