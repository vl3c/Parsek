using System.Collections.Generic;
using System.Globalization;
using Parsek.MapRender;
using UnityEngine;

namespace Parsek.InGameTests
{
    // Phase 7 (migration plan §9 / design §4 / §9.2 / §10) - the in-game gate for the FAIL-CLOSED
    // producers: cross-SOI / nested-SOI (Jool) / moving-target station.
    //
    // It drives the REAL FailClosedClassifier against the LIVE FlightGlobals body tree (so the Jool/Laythe/
    // Tylo parent chain is the actual stock hierarchy, not a fake) and asserts:
    //
    //  - a Jool moon tour (Jool -> Laythe -> Tylo) classifies FAIL-CLOSED (NestedSoi -> FaithfulFallback);
    //  - a moving-target station approach (a live-vessel arrival anchor) classifies FAIL-CLOSED
    //    (MovingTargetStation -> FaithfulFallback);
    //  - an ordinary single-level cross-SOI transfer (Kerbin -> Sun -> Duna) stays SUPPORTED (it renders
    //    correctly through the per-crossing FlexibleSoi G0 path - the cross-SOI kink is define-only,
    //    unchanged);
    //  - the Tier-A `fail-closed-to-faithful` structural line reaches the gated MapRenderTrace sink for a
    //    fail-closed member (the logging priority);
    //  - the parity oracle in FAITHFUL mode reports ZERO drift for a fail-closed member's recorded arc
    //    (rendered == recorded - fail-closed renders the recorded trajectory verbatim).
    //
    // The pure decision/token math is locked headlessly in FailClosedClassifierTests / NestedSoiSubtreeTests
    // / MovingTargetStationApproachTests; this exercises the Unity-coupled LIVE body tree + the gated trace
    // sink + the FAITHFUL-mode oracle on live-framed geometry those cannot reach.
    //
    // NOTE: in-game test (Ctrl+Shift+T / Settings > Diagnostics); cannot run headless (reads the live
    // FlightGlobals body hierarchy + frames recorded geometry through the body's world surface positions).
    // FLIGHT only; career-independent.
    public class FailClosedFaithfulInGameTest
    {
        [InGameTest(Category = "MapRender", Scene = GameScenes.FLIGHT,
            Description = "Phase 7 fail-closed producers: a Jool moon tour and a moving-target station "
                + "classify FaithfulFallback (render recorded verbatim) while a single-level cross-SOI "
                + "transfer stays SUPPORTED; the fail-closed-to-faithful Tier-A line is present and the "
                + "parity oracle reports zero drift in FAITHFUL mode")]
        public void FailClosed_JoolStationCrossSoi_RenderFaithfully_WithTraceLine()
        {
            // Resolve the live stock body tree. Jool's moons are the canonical nested-SOI case.
            System.Func<string, string> liveReferenceBody = FlightGlobalsBodyInfo.Instance.ReferenceBodyName;

            // Skip on a non-stock pack missing Jool's moons (the nesting check needs two siblings under a
            // non-root ancestor; a pack without Jool moons cannot exercise it).
            CelestialBody jool = FlightGlobals.Bodies?.Find(b => b.bodyName == "Jool");
            CelestialBody laythe = FlightGlobals.Bodies?.Find(b => b.bodyName == "Laythe");
            CelestialBody tylo = FlightGlobals.Bodies?.Find(b => b.bodyName == "Tylo");
            if (jool == null || laythe == null || tylo == null)
            {
                InGameAssert.Skip("Jool/Laythe/Tylo not present (non-stock pack) - cannot exercise nested-SOI");
                return;
            }

            bool prevForce = MapRenderTrace.ForceEnabledForTesting;
            var capturedLines = new List<string>();
            System.Action<string> prevSink = ParsekLog.TestSinkForTesting;
            bool prevSuppress = ParsekLog.SuppressLogging;

            try
            {
                MapRenderTrace.Reset();
                MapRenderTrace.ForceEnabledForTesting = true; // the trace sink must be live for the event
                MapRenderTrace.FrameCounterOverrideForTesting = () => Time.frameCount;
                ParsekLog.SuppressLogging = false;
                ParsekLog.TestSinkForTesting = line => capturedLines.Add(line);

                // --- (1) Jool moon tour -> NestedSoi fail-closed (against the LIVE body tree) ---
                var joolTour = new List<string> { "Jool", "Laythe", "Tylo" };
                FailClosedClassifier.FailClosedDecision joolDecision = FailClosedClassifier.Classify(
                    joolTour, hasLiveVesselArrivalAnchor: false, referenceBodyName: liveReferenceBody);

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "FailClosed Jool: reason={0} failClosed={1} prov={2}",
                    FailClosedClassifier.ReasonToken(joolDecision.Reason), joolDecision.IsFailClosed,
                    SegmentProvenanceTokens.ToToken(joolDecision.Provenance)));

                InGameAssert.IsTrue(joolDecision.IsFailClosed,
                    "a Jool moon tour (Laythe + Tylo siblings under Jool) must classify fail-closed on the "
                    + "LIVE body tree");
                InGameAssert.AreEqual(FailClosedClassifier.FailClosedReason.NestedSoi, joolDecision.Reason,
                    "the Jool tour's fail-closed reason must be nested-soi");
                InGameAssert.AreEqual(SegmentProvenance.FaithfulFallback, joolDecision.Provenance,
                    "a fail-closed member renders the recorded trajectory verbatim (FaithfulFallback)");

                // --- (2) Moving-target station -> MovingTargetStation fail-closed (live-vessel arrival) ---
                FailClosedClassifier.FailClosedDecision stationDecision = FailClosedClassifier.Classify(
                    new List<string> { "Kerbin", "Sun" }, hasLiveVesselArrivalAnchor: true,
                    referenceBodyName: liveReferenceBody);
                InGameAssert.IsTrue(stationDecision.IsFailClosed,
                    "a live-vessel arrival anchor (a station) must classify fail-closed");
                InGameAssert.AreEqual(
                    FailClosedClassifier.FailClosedReason.MovingTargetStation, stationDecision.Reason,
                    "the station's fail-closed reason must be moving-target-station");

                // --- (3) Single-level cross-SOI transfer -> SUPPORTED (renders unchanged) ---
                FailClosedClassifier.FailClosedDecision crossSoiDecision = FailClosedClassifier.Classify(
                    new List<string> { "Kerbin", "Sun", "Duna" }, hasLiveVesselArrivalAnchor: false,
                    referenceBodyName: liveReferenceBody);
                InGameAssert.IsFalse(crossSoiDecision.IsFailClosed,
                    "an ordinary single-level cross-SOI transfer must stay SUPPORTED (the cross-SOI kink is "
                    + "define-only: it renders the current FlexibleSoi G0 behavior unchanged)");

                // --- (4) The Tier-A fail-closed-to-faithful line reaches the gated sink (logging priority) ---
                FailClosedClassifier.EmitFailClosedToFaithful("rec-jool-tour", 0,
                    Planetarium.GetUniversalTime(), joolDecision);

                bool sawFailClosedLine = false;
                foreach (string line in capturedLines)
                {
                    if (line.Contains(MapRenderTrace.EventFailClosedToFaithful)
                        && line.Contains("producer=nested-soi"))
                    {
                        sawFailClosedLine = true;
                        break;
                    }
                }
                InGameAssert.IsTrue(sawFailClosedLine,
                    "the Tier-A fail-closed-to-faithful structural line (producer=nested-soi) must reach the "
                    + "gated MapRenderTrace sink");

                // --- (5) FAITHFUL-mode oracle IDENTITY / PLUMBING check (review N12 honesty relabel) ---
                // This step diffs a Jool-framed recorded arc AGAINST ITSELF, so it can NEVER detect a
                // fail-closed member rendering the wrong thing - both inputs are the same array by
                // construction. What it DOES pin is the oracle PLUMBING on real Unity-framed geometry: the
                // Faithful-mode path accepts the frame, yields a measurement (not a silent skip), derives a
                // scale tolerance, and reports zero on identical inputs. The rendered-vs-recorded truth for
                // fail-closed members is the production probe's job (the faithful lens samples the LIVE
                // rendered orbit); this in-game step only proves the oracle machinery is not blind here.
                double[] recordedArc = BuildJoolFramedArc(jool);
                RenderParityOracle.ParityResult parity = RenderParityOracle.ComputeDriftScaleDerived(
                    RenderParityOracle.ParityMode.Faithful, recordedArc, recordedArc);

                ParsekLog.Info("TestRunner", string.Format(CultureInfo.InvariantCulture,
                    "FailClosed parity (faithful identity/plumbing): hasMeas={0} maxDev={1:F2}m tol={2:F2}m over={3}",
                    parity.HasMeasurement, parity.MaxDeviationMeters, parity.ToleranceMeters,
                    parity.OverTolerance));

                InGameAssert.AreEqual(RenderParityOracle.ParityMode.Faithful, parity.Mode,
                    "the oracle identity check runs in FAITHFUL mode");
                InGameAssert.IsTrue(parity.HasMeasurement,
                    "the Jool-framed arc must yield a parity measurement (oracle plumbing not blind on "
                    + "this frame)");
                InGameAssert.IsFalse(parity.OverTolerance,
                    "an arc diffed against itself must report ZERO drift (oracle identity check - this "
                    + "does NOT prove the fail-closed render matched the recording)");
            }
            finally
            {
                MapRenderTrace.ForceEnabledForTesting = prevForce;
                MapRenderTrace.FrameCounterOverrideForTesting = null;
                ParsekLog.TestSinkForTesting = prevSink;
                ParsekLog.SuppressLogging = prevSuppress;
                MapRenderTrace.Reset();
            }
        }

        // A recorded arc framed through Jool's own world surface positions (a real Unity-framed metres
        // reference set). The exact geometry is not load-bearing - it just must be a real, finite,
        // multi-point arc the FAITHFUL-mode oracle can diff against itself (rendered == recorded => zero
        // drift). Body-relative so the absolute frame cancels.
        private static double[] BuildJoolFramedArc(CelestialBody jool)
        {
            const int n = 8;
            var flat = new double[n * 3];
            for (int i = 0; i < n; i++)
            {
                double lon = i * 12.0;            // a spread of longitudes
                double alt = 5_000_000.0;          // well above Jool's surface
                Vector3d p = jool.GetWorldSurfacePosition(0.0, lon, alt) - jool.position;
                flat[i * 3] = p.x;
                flat[i * 3 + 1] = p.y;
                flat[i * 3 + 2] = p.z;
            }
            return flat;
        }
    }
}
