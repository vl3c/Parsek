"""Unit tests for rendercompose.py, the pure M-A7 render-composition verifier.

Runnable with the stdlib runner only (NO pytest, NO KSP, NO network)::

    python -m unittest discover -s harness/lib

Corpus: PRODUCTION-SHAPED synthetic ``RENDER_MANIFEST`` text as module-level
string literals, authored against the SPEC schema (``.scout/SPEC.md`` "Manifest
schema (ConfigNode text, version 1)") the C# ``RenderCompositionManifest`` writer
implements. One rich positive fixture (two units, two cycles each, holds, a
loiter cut, a descent, seams, a route section) plus one violating fixture per
defect class, plus the adversarial mutations a torn or hand-edited file
produces.

The CONSTANTS block of the well-formed fixtures is RENDERED FROM
``rendercompose.RATIFIED_TOLERANCES`` rather than copied into the literal, on
purpose: a second hand-maintained copy of a moving table is a second thing to
leave stale, and RC-CONST's drift path is exercised by a dedicated fixture that
DOES carry literal values. The table itself is pinned exactly by
``RatifiedTableTests``.

The binding properties throughout, mirroring test_saveparse.py's:

- a manifest that cannot be parsed must NEVER read as "zero records";
- a clause the manifest cannot answer must land in the defined-unevaluable
  ledger, never silently pass and never falsely red;
- every finding cites a contract.

One test is deliberately SKIPPED: the byte-copied C# writer fixture the SPEC
asks for cannot exist until Phase 1 lands its sample output. It names the file
it will read so the reconciliation step is a wiring change, not a rediscovery.
"""

import math
import unittest

import rendercompose as rc
import saveparse


# ---------------------------------------------------------------------------
# Fixture construction helpers.
# ---------------------------------------------------------------------------

CONSTANTS_TOKEN = "%CONSTANTS%"


def render_constants(indent="\t\t", overrides=None, drop=()):
    """The CONSTANTS body, rendered from the ratified table.

    ``overrides`` re-spells one key's value (the RC-CONST drift fixture);
    ``drop`` omits keys entirely (the missing-constant path)."""
    overrides = overrides or {}
    lines = []
    for name in sorted(rc.RATIFIED_TOLERANCES):
        if name in drop:
            continue
        value = overrides.get(name, rc.RATIFIED_TOLERANCES[name])
        lines.append("%s%s = %r" % (indent, name, value))
    return "\n".join(lines)


def build(text, **kwargs):
    """Fill the CONSTANTS placeholder and parse."""
    return rc.parse_render_manifest(fill(text, **kwargs))


def fill(text, **kwargs):
    return text.replace(CONSTANTS_TOKEN, render_constants(**kwargs))


# ---------------------------------------------------------------------------
# The rich positive fixture.
#
# Unit A (Flight, owner 0): a re-aimed two-member loop with one loiter cut, an
# arrival hold, a joint (D8) secondary and a descent member. Two observed cycles
# bounded by three cycle-rollover events, fully covered by four dwells.
# Unit B (TrackingStation, owner 1): a route-backed two-member loop, two cycles,
# no cuts and no hold.
#
# The arithmetic is hand-derived so every recomputation agrees:
#   compressed span A = (2000 - 1000) - 300 = 700
#   W_0 = 100, cadence % alignPeriod = 4000 % 500 = 0  =>  W_N = 100 for all N
#   entry_offset0 = 10000 + (compress(1800) - 1000) - 1800 = 8700
#   descent cycle 0: entry = 10000 + 0*4000 + (compress(1700) - 1000) = 10400,
#                    trigger = 10400 + ((1700 - 10400) mod 3000) = 10700
#   descent cycle 1: entry = 14400, trigger = 16700; residual 0 deg both cycles.
# ---------------------------------------------------------------------------

POSITIVE_MANIFEST = """\
RENDER_MANIFEST
{
	schemaVersion = 1
	exportUT = 24000.0
	exportReason = verb
	scene = FLIGHT
	saveName = m-a7-reading
	envArmed = True
	forceArmed = False
	mapRenderTracingOn = True
	CONSTANTS
	{
%CONSTANTS%
	}
	PLAN
	{
		UNIT
		{
			host = Flight
			planSeq = 0
			signatureHash = 8811223344
			ownerIndex = 0
			spanStartUT = 1000.0
			spanEndUT = 2000.0
			cadenceSeconds = 4000.0
			overlapCadenceSeconds = 4000.0
			phaseAnchorUT = 10000.0
			isReaim = True
			hasRelaunchSchedule = True
			MEMBER
			{
				index = 0
				recId = recA0
				startUT = 1000.0
				endUT = 1600.0
			}
			MEMBER
			{
				index = 1
				recId = recA1
				startUT = 1600.0
				endUT = 2000.0
			}
			LOITER_CUT
			{
				startUT = 1200.0
				lengthSeconds = 300.0
			}
			arrivalHoldSeconds = 100.0
			arrivalHoldAtUT = 1800.0
			arrivalAlignPeriodSeconds = 500.0
			arrivalJointSecondaryPeriodSeconds = 500.0
			arrivalJointSecondaryToleranceSeconds = 1.0
			arrivalJointMaxWholeHoldPeriods = 64
			launchBodyRotationPeriodSeconds = 21549.425183089825
			launchHoldEngaged = True
			recordedSoiExitUT = 1450.0
			descentMemberIndices = 1
			recordedDeorbitUT = 1700.0
			descentEndUT = 1950.0
			destinationBodyRotationPeriodSeconds = 3000.0
			loiterPeriodSeconds = 150.0
			captureShiftSeconds = 0.0
			parkingConicEndUT = 1700.0
			transferMemberIndex = 0
			firstDeorbitLegStartUT = 1700.0
			transferMemberRecordingId = recA0
			REAIM_SCHEDULE
			{
				firstDepartureUT = 10000.0
				synodicPeriodSeconds = 4000.0
				tofSeconds = 600.0
				phaseAnchorUT = 10000.0
				cadenceSeconds = 4000.0
				prograde = True
			}
		}
		UNIT
		{
			host = TrackingStation
			planSeq = 1
			signatureHash = 9911223344
			ownerIndex = 1
			spanStartUT = 500.0
			spanEndUT = 900.0
			cadenceSeconds = 2000.0
			overlapCadenceSeconds = 2000.0
			phaseAnchorUT = 20000.0
			isReaim = False
			hasRelaunchSchedule = False
			MEMBER
			{
				index = 2
				recId = recB0
				startUT = 500.0
				endUT = 700.0
			}
			MEMBER
			{
				index = 3
				recId = recB1
				startUT = 700.0
				endUT = 900.0
			}
			arrivalHoldSeconds = 0.0
			ROUTE
			{
				routeId = route-1
				backingMissionTreeId = tree-77
				recordedDockUT = 800.0
				recordedOriginUndockUT = 520.0
				dispatchWindowPeriod = 0.0
				scope = SameBody
				excludedIntervalKeys = k1;k2
			}
		}
	}
	CHAIN
	{
		CHAIN_BUILD
		{
			pid = 5001
			recId = recA0
			committedIndex = 0
			ut = 10000.0
			signature = sigA
			windowIndex = 0
			provenance = spine
			hasReaimedSegments = True
			seamSource = assembler
			PHASE
			{
				kind = ascent
				provenance = recorded
				body = Kerbin
				startUT = 10000.0
				endUT = 12000.0
			}
			PHASE
			{
				kind = heliocentric-transfer
				provenance = synthesized
				body = Kerbin
				startUT = 12000.0
				endUT = 16000.0
			}
			PHASE
			{
				kind = descent
				provenance = recorded
				body = Mun
				startUT = 16000.0
				endUT = 18000.0
			}
			SEAM
			{
				boundaryIndex = 1
				kind = rigid
			}
			SEAM
			{
				boundaryIndex = 2
				kind = flexible-soi
			}
		}
		CHAIN_BUILD
		{
			pid = 5003
			recId = recB0
			committedIndex = 2
			ut = 20000.0
			signature = sigB
			windowIndex = 0
			provenance = assembler-fallback
			hasReaimedSegments = False
			seamSource = assembler
			PHASE
			{
				kind = soi-arrival
				provenance = recorded
				body = Kerbin
				startUT = 20000.0
				endUT = 22000.0
			}
			SEAM
			{
				boundaryIndex = 1
				kind = rigid
			}
		}
	}
	OBSERVED
	{
		DWELL
		{
			pid = 5001
			recId = recA0
			committedIndex = 0
			chainSignature = sigA
			segmentIndex = 0
			phaseKind = ascent
			treatment = TracedPath
			visible = True
			coverage = InSegment
			frameBody = Kerbin
			ownerIndex = 0
			openUT = 10000.0
			closeUT = 12000.0
			openLoopUT = 1000.0
			closeLoopUT = 1600.0
			frames = 100
			warp1x = 80
			warpPhys = 0
			warp100 = 20
			warp1000 = 0
			warpHigh = 0
			minHeadUT = 1000.0
			maxHeadUT = 1600.0
			maxUtStep = 5.0
			openBody = Kerbin
			openX = 100.0
			openY = 200.0
			openZ = 300.0
			closeBody = Kerbin
			closeX = 110.0
			closeY = 220.0
			closeZ = 330.0
			markerDecision = True
			markerTracedPath = True
			markerPolyline = True
			markerIconSuppressed = False
		}
		DWELL
		{
			pid = 5002
			recId = recA1
			committedIndex = 1
			chainSignature = sigA
			segmentIndex = 2
			phaseKind = descent
			treatment = StockConic
			visible = True
			coverage = InSegment
			frameBody = Mun
			ownerIndex = 0
			openUT = 12000.0
			closeUT = 14000.0
			openLoopUT = 1600.0
			closeLoopUT = 2000.0
			frames = 90
			warp1x = 70
			warpPhys = 0
			warp100 = 20
			warp1000 = 0
			warpHigh = 0
			minHeadUT = 1600.0
			maxHeadUT = 2000.0
			maxUtStep = 5.0
			markerDecision = True
			markerTracedPath = False
			markerPolyline = False
			markerIconSuppressed = False
		}
		DWELL
		{
			pid = 5001
			recId = recA0
			committedIndex = 0
			chainSignature = sigA
			segmentIndex = 0
			phaseKind = ascent
			treatment = TracedPath
			visible = True
			coverage = InSegment
			frameBody = Kerbin
			ownerIndex = 0
			openUT = 14000.0
			closeUT = 16000.0
			openLoopUT = 1000.0
			closeLoopUT = 1600.0
			frames = 100
			warp1x = 80
			warpPhys = 0
			warp100 = 20
			warp1000 = 0
			warpHigh = 0
			minHeadUT = 1000.0
			maxHeadUT = 1600.0
			maxUtStep = 5.0
			markerDecision = True
			markerTracedPath = True
			markerPolyline = True
			markerIconSuppressed = False
		}
		DWELL
		{
			pid = 5002
			recId = recA1
			committedIndex = 1
			chainSignature = sigA
			segmentIndex = 2
			phaseKind = descent
			treatment = StockConic
			visible = True
			coverage = InSegment
			frameBody = Mun
			ownerIndex = 0
			openUT = 16000.0
			closeUT = 18000.0
			openLoopUT = 1600.0
			closeLoopUT = 2000.0
			frames = 90
			warp1x = 70
			warpPhys = 0
			warp100 = 20
			warp1000 = 0
			warpHigh = 0
			minHeadUT = 1600.0
			maxHeadUT = 2000.0
			maxUtStep = 5.0
			markerDecision = True
			markerTracedPath = False
			markerPolyline = False
			markerIconSuppressed = False
		}
		DWELL
		{
			pid = 5003
			recId = recB0
			committedIndex = 2
			chainSignature = sigB
			segmentIndex = 0
			phaseKind = soi-arrival
			treatment = StockConic
			visible = True
			coverage = InSegment
			frameBody = Kerbin
			ownerIndex = 1
			openUT = 20000.0
			closeUT = 21000.0
			openLoopUT = 500.0
			closeLoopUT = 700.0
			frames = 40
			warp1x = 40
			warpPhys = 0
			warp100 = 0
			warp1000 = 0
			warpHigh = 0
			minHeadUT = 500.0
			maxHeadUT = 700.0
			maxUtStep = 8.0
			markerDecision = True
		}
		DWELL
		{
			pid = 5004
			recId = recB1
			committedIndex = 3
			chainSignature = sigB
			segmentIndex = 1
			phaseKind = surface
			treatment = StockConic
			visible = True
			coverage = InSegment
			frameBody = Kerbin
			ownerIndex = 1
			openUT = 21000.0
			closeUT = 22000.0
			openLoopUT = 700.0
			closeLoopUT = 900.0
			frames = 40
			warp1x = 40
			warpPhys = 0
			warp100 = 0
			warp1000 = 0
			warpHigh = 0
			minHeadUT = 700.0
			maxHeadUT = 900.0
			maxUtStep = 8.0
			markerDecision = True
		}
		DWELL
		{
			pid = 5003
			recId = recB0
			committedIndex = 2
			chainSignature = sigB
			segmentIndex = 0
			phaseKind = soi-arrival
			treatment = StockConic
			visible = True
			coverage = InSegment
			frameBody = Kerbin
			ownerIndex = 1
			openUT = 22000.0
			closeUT = 23000.0
			openLoopUT = 500.0
			closeLoopUT = 700.0
			frames = 40
			warp1x = 40
			warpPhys = 0
			warp100 = 0
			warp1000 = 0
			warpHigh = 0
			minHeadUT = 500.0
			maxHeadUT = 700.0
			maxUtStep = 8.0
			markerDecision = True
		}
		DWELL
		{
			pid = 5004
			recId = recB1
			committedIndex = 3
			chainSignature = sigB
			segmentIndex = 1
			phaseKind = surface
			treatment = StockConic
			visible = True
			coverage = InSegment
			frameBody = Kerbin
			ownerIndex = 1
			openUT = 23000.0
			closeUT = 24000.0
			openLoopUT = 700.0
			closeLoopUT = 900.0
			frames = 40
			warp1x = 40
			warpPhys = 0
			warp100 = 0
			warp1000 = 0
			warpHigh = 0
			minHeadUT = 700.0
			maxHeadUT = 900.0
			maxUtStep = 8.0
			markerDecision = True
		}
		TRANSITION
		{
			pid = 5001
			ut = 12000.0
			fromPhaseKind = ascent
			toPhaseKind = heliocentric-transfer
			fromTreatment = TracedPath
			toTreatment = StockConic
			fromBody = Kerbin
			toBody = Kerbin
			fromSegmentIndex = 0
			toSegmentIndex = 1
			chainSignature = sigA
		}
		TRANSITION
		{
			pid = 5002
			ut = 16000.0
			fromPhaseKind = heliocentric-transfer
			toPhaseKind = descent
			fromTreatment = StockConic
			toTreatment = StockConic
			fromBody = Kerbin
			toBody = Mun
			fromSegmentIndex = 1
			toSegmentIndex = 2
			chainSignature = sigA
		}
		SEAM_TANGENT
		{
			pid = 5001
			recId = recA0
			legIndex = 3
			ut = 12000.0
			continuous = True
			angleRad = 0.05
			toleranceRadians = 0.1
		}
		SEAM_ENDPOINT
		{
			pid = 5002
			recId = recA1
			ut = 16000.0
			sampled = True
			ratio = 1.002
			endpointDistanceMeters = 2434105.0
			soiRadiusMeters = 2429559.1
			ratioTolerance = 1.005
			outsideSoi = True
			fromBody = Kerbin
			toBody = Mun
			recordedSeamUT = 1600.0
			seamUT = 16000.0
			clockConvention = rendered
			seedKind = recorded
		}
		CLOCK_EVENT
		{
			kind = cycle-rollover
			ownerIndex = 0
			cycleIndex = 0
			ut = 10000.0
		}
		CLOCK_EVENT
		{
			kind = cycle-rollover
			ownerIndex = 0
			cycleIndex = 1
			ut = 14000.0
		}
		CLOCK_EVENT
		{
			kind = cycle-rollover
			ownerIndex = 0
			cycleIndex = 2
			ut = 18000.0
		}
		CLOCK_EVENT
		{
			kind = descent-phase
			ownerIndex = 1
			cycleIndex = 0
			ut = 10700.0
			detailA = 10700.0
			detailB = 10400.0
			detailC = 0.0
			detailS = Descent
			detailD = 1700.0
		}
		CLOCK_EVENT
		{
			kind = descent-phase
			ownerIndex = 1
			cycleIndex = 1
			ut = 16700.0
			detailA = 16700.0
			detailB = 14400.0
			detailC = 0.0
			detailS = Descent
			detailD = 1700.0
		}
		CLOCK_EVENT
		{
			kind = hold-engage
			ownerIndex = 0
			cycleIndex = 0
			ut = 10500.0
			detailA = 0.0
			detailB = 1150.0
			detailC = 0.0
			detailS = 
		}
		CLOCK_EVENT
		{
			kind = hold-release
			ownerIndex = 0
			cycleIndex = 0
			ut = 10600.0
			detailA = 0.0
			detailB = 1150.0
			detailC = 100.0
			detailS = 
		}
		CLOCK_EVENT
		{
			kind = reaim-window
			ownerIndex = 0
			cycleIndex = 0
			ut = 10000.0
			detailA = 0.0
			detailB = 1600.0
			detailC = 0.0
			detailS = recA0
		}
		CLOCK_EVENT
		{
			kind = cycle-rollover
			ownerIndex = 1
			cycleIndex = 0
			ut = 20000.0
		}
		CLOCK_EVENT
		{
			kind = cycle-rollover
			ownerIndex = 1
			cycleIndex = 1
			ut = 22000.0
		}
		CLOCK_EVENT
		{
			kind = cycle-rollover
			ownerIndex = 1
			cycleIndex = 2
			ut = 24000.0
		}
		LINE_BRANCH
		{
			pid = 5001
			recId = recA0
			ut = 10000.0
			reason = visible-body-frame
			lineActive = True
			drawIcons = 0
			iconSuppressed = False
			coverage = Inside
		}
		OWNERSHIP_CHANGE
		{
			recId = recA0
			ut = 10000.0
			event = appear
		}
		OWNERSHIP_CHANGE
		{
			recId = recA0
			ut = 12000.0
			event = disappear
		}
		OWNERSHIP_CHANGE
		{
			recId = recA0
			ut = 14000.0
			event = appear
		}
		OWNERSHIP_CHANGE
		{
			recId = recA0
			ut = 16000.0
			event = disappear
		}
		ROUTE_LINE_BUILD
		{
			routeId = route-1
			signature = 771122
			dockClipUT = 800.0
			dispatchWindowPeriod = 0.0
			scope = SameBody
			resolvableMembers = 2
			groups = 2
			totalLegs = 4
			transferLegsDropped = 0
			ut = 20000.0
		}
		ROUTE_LEG_DEFER
		{
			routeId = route-1
			recId = recB1
			count = 3
		}
	}
}
"""


def variant(*pairs):
    """The positive fixture with one or more single-occurrence substitutions.

    Mutating the POSITIVE text (rather than authoring a second full manifest)
    keeps every violating fixture's delta visible in one line, which is what
    makes "this fixture reds for THIS reason" checkable by reading the test."""
    text = POSITIVE_MANIFEST
    for old, new in pairs:
        if text.count(old) != 1:
            raise AssertionError(
                "variant anchor %r occurs %d times in POSITIVE_MANIFEST - a "
                "fixture mutation must be unambiguous" % (old, text.count(old)))
        text = text.replace(old, new, 1)
    return text


# One extra dwell block, appended inside OBSERVED, used by the overlong-hold
# fixture. It sits AFTER unit A's last cycle window so it perturbs nothing else.
OVERLONG_INTERIOR_GAP_DWELL = """\
		DWELL
		{
			pid = 5001
			recId = recA0
			committedIndex = 0
			chainSignature = sigA
			segmentIndex = 1
			phaseKind = Hold
			treatment = StockConic
			visible = True
			coverage = InInteriorGap
			frameBody = Kerbin
			openUT = 18000.0
			closeUT = 23000.0
			frames = 500
			warp1x = 500
			warpPhys = 0
			warp100 = 0
			warp1000 = 0
			warpHigh = 0
			maxUtStep = 5.0
			markerDecision = True
		}
		ROUTE_LEG_DEFER
"""

CODRAW_VIOLATION_RECORD = """\
		ROUTE_CODRAW_VIOLATION
		{
			routeId = route-1
			recId = recB1
			ut = 21500.0
			frame = 4242
		}
		ROUTE_LEG_DEFER
"""

TRUNCATED_DWELL_RECORD = """\
		TRUNCATED
		{
			section = DWELL
			pid = 5001
			kind = dwell
			droppedCount = 17
		}
		ROUTE_LEG_DEFER
"""

UNKNOWN_OBSERVED_RECORD = """\
		MYSTERY_RECORD
		{
			pid = 5001
			ut = 11000.0
		}
		ROUTE_LEG_DEFER
"""


# --- review-pass fixture builders ------------------------------------------
#
# One unit-A dwell shortened so cycle 0 carries a 1000 s dark window, far wider
# than that cycle's 5 s warp resolution. Every RC-COVER cell below is this one
# mutation plus one extra record, so what each cell proves is the effect of THAT
# record and nothing else.
COVER_GAP = ("openUT = 10000.0\n\t\t\tcloseUT = 12000.0",
             "openUT = 10000.0\n\t\t\tcloseUT = 11000.0")


def ratified_skip(pid, first_ut, last_ut, reason="no-conic", count=3):
    """One OBSERVED.RATIFIED_SKIP hull, ready to insert at the ROUTE_LEG_DEFER
    anchor. Note the shape the rule has to live with: a bracket plus a COUNT,
    never the intervals themselves."""
    return ("\t\tRATIFIED_SKIP\n\t\t{\n\t\t\tpid = %s\n\t\t\treason = %s\n"
            "\t\t\tfirstUT = %s\n\t\t\tlastUT = %s\n\t\t\tcount = %s\n\t\t}\n%s"
            % (pid, reason, first_ut, last_ut, count, ANCHOR_ROUTE_LEG_DEFER))


def truncated_record(section, pid=0, kind="family-cap", dropped=5):
    return ("\t\tTRUNCATED\n\t\t{\n\t\t\tsection = %s\n\t\t\tpid = %s\n"
            "\t\t\tkind = %s\n\t\t\tdroppedCount = %s\n\t\t}\n%s"
            % (section, pid, kind, dropped, ANCHOR_ROUTE_LEG_DEFER))


def rules_for(text, block=None):
    snap = rc.parse_render_manifest(fill(text))
    return snap, rc.evaluate_rules(snap, block)


def fails(findings, rule_id=None):
    return [f for f in findings
            if f.level == rc.LEVEL_FAIL and (rule_id is None or f.rule_id == rule_id)]


# ---------------------------------------------------------------------------
# Vocabulary / table anti-vacuity.
# ---------------------------------------------------------------------------


class RatifiedTableTests(unittest.TestCase):
    """The tables are the AUTHORITY. An exact pin here is what makes an RC-CONST
    red mean "the product moved" instead of "somebody edited the checker"."""

    def test_ratified_tolerances_pinned_exactly(self):
        self.assertEqual({
            "PhaseSeamClassifier.DefaultTangentToleranceRadians": 0.1,
            "CrossMemberSeamStitcher.TangentToleranceRadians": 0.1,
            "SeamEndpointOracle.DefaultRatioTolerance": 1.005,
            "GhostTrajectoryPolylineRenderer.BridgeMergeSampleCount": 60.0,
            "GhostTrajectoryPolylineRenderer.BridgeMaxAngleRadians": 0.7853981633974483,
            "GhostTrajectoryPolylineRenderer.BridgeMinAngleRadians": 0.08726646259971647,
            "GhostTrajectoryPolylineRenderer.BridgeChordMinAngleRadians": 0.008726646259971648,
            "GhostTrajectoryPolylineRenderer.BridgeMaxSeamGapSeconds": 120.0,
            "GhostTrajectoryPolylineRenderer.BridgeSeamSharedBoundaryToleranceSeconds": 1.0,
            "GhostTrajectoryPolylineRenderer.AnchorMaxResidualKm": 50.0,
            "GhostTrajectoryPolylineRenderer.AnchorMaxRelResidual": 0.05,
            "ShadowRenderDriver.SeedFreshnessFrames": 2.0,
            "GhostOrbitLinePatch.PolylineReleaseGraceSeconds": 1.5,
            "GhostTrajectoryPolylineRenderer.TangentSeamConicSampleDtSeconds": 1.0,
            "DescentTrigger.DefaultSeamEpsSeconds": 1.0,
            "ReaimLoiterCompressor.DefaultKeepRevs": 1.0,
            "ReaimLoiterCompressor.DefaultAStepRelThreshold": 0.05,
            "ReaimLoiterCompressor.DefaultContiguityEpsilonSeconds": 1.0,
            "ReaimLoiterCompressor.DefaultSameOrbitRelThreshold": 0.001,
            "DestinationArrivalSolver.MaxJointHoldWholePeriods": 64.0,
        }, rc.RATIFIED_TOLERANCES)

    def test_the_alias_pair_agrees_with_its_canonical_row(self):
        for alias, canonical in rc.RATIFIED_ALIAS_PAIRS:
            self.assertEqual(rc.RATIFIED_TOLERANCES[alias],
                             rc.RATIFIED_TOLERANCES[canonical])

    def test_accessors_raise_on_an_unknown_key(self):
        with self.assertRaises(ValueError):
            rc.ratified_tolerance("NoSuchConstant")
        with self.assertRaises(ValueError):
            rc.body_rotation_period("Kerbol")
        with self.assertRaises(ValueError):
            rc.body_orbital_period("Kerbol")

    def test_stock_body_rows_are_the_cited_ones_only(self):
        # Every row cites a committed in-repo source in its comment; the SET is
        # pinned so an uncited addition reds here rather than in a flight.
        self.assertEqual({"Kerbin", "Mun"},
                         set(rc.STOCK_BODY_ROTATION_PERIOD_SECONDS))
        self.assertEqual({"Mun", "Minmus", "Ike", "Laythe", "Gilly"},
                         set(rc.STOCK_BODY_ORBITAL_PERIOD_SECONDS))
        # The Mun is tidally locked: V23M:194 states rotation == orbital exactly.
        self.assertEqual(rc.body_rotation_period("Mun"),
                         rc.body_orbital_period("Mun"))

    def test_every_rule_id_carries_a_non_empty_cited_contract(self):
        self.assertEqual(set(rc.RULE_IDS), set(rc.RULE_CITED_CONTRACTS))
        for rule_id in rc.RULE_IDS:
            self.assertTrue(rc.RULE_CITED_CONTRACTS[rule_id].strip(),
                            "rule %s cites nothing" % rule_id)

    def test_a_finding_without_a_cited_contract_cannot_be_constructed(self):
        with self.assertRaises(ValueError):
            rc.RenderComposeFinding(rule_id=rc.RULE_SEAM, level=rc.LEVEL_FAIL,
                                    target="t", message="m", cited_contract="")
        with self.assertRaises(ValueError):
            rc.RenderComposeFinding(rule_id=rc.RULE_SEAM, level="LOUD",
                                    target="t", message="m", cited_contract="c")
        with self.assertRaises(ValueError):
            rc.RenderComposeFinding(rule_id="", level=rc.LEVEL_FAIL,
                                    target="t", message="m", cited_contract="c")

    def test_every_finding_the_fixtures_produce_cites_something(self):
        texts = [POSITIVE_MANIFEST,
                 variant(("openUT = 12000.0", "openUT = 13000.0")),
                 variant(("lengthSeconds = 300.0", "lengthSeconds = 320.0")),
                 variant(("angleRad = 0.05", "angleRad = 0.25")),
                 variant(("recordedDockUT = 800.0", "recordedDockUT = 600.0")),
                 variant(("kind = reaim-window", "kind = mystery-event"))]
        seen = 0
        for text in texts:
            _snap, (findings, _u, _m) = rules_for(text)
            for f in findings:
                self.assertTrue(f.cited_contract.strip())
                self.assertTrue(f.message.strip())
                seen += 1
        self.assertGreater(seen, 10, "anti-vacuity: the fixtures produced almost "
                                     "no findings, so this cell proved nothing")


# ---------------------------------------------------------------------------
# Clock math. Hand-computed values, including every edge case the transcription
# in .scout/plan-surface.md section 5.2 calls out.
# ---------------------------------------------------------------------------


class ClockMathTests(unittest.TestCase):

    def test_arrival_align_hold_normalises_into_the_period(self):
        self.assertAlmostEqual(300.0, rc.arrival_align_hold(1300.0, 1000.0, 500.0))
        # A recorded arrival BEFORE the live entry still normalises forward.
        self.assertAlmostEqual(200.0, rc.arrival_align_hold(1000.0, 1300.0, 500.0))
        self.assertAlmostEqual(0.0, rc.arrival_align_hold(1500.0, 1000.0, 500.0))

    def test_arrival_align_hold_is_zero_on_a_degenerate_period(self):
        for bad in (0.0, -10.0, float("nan"), float("inf")):
            self.assertEqual(0.0, rc.arrival_align_hold(1300.0, 1000.0, bad))
        self.assertEqual(0.0, rc.arrival_align_hold(float("nan"), 1000.0, 500.0))

    def test_per_loop_arrival_hold_w0_fence(self):
        # The 13b regression fence: w0 <= 0 returns w0 UNCHANGED, not 0.0.
        self.assertEqual(0.0, rc.per_loop_arrival_hold(0.0, 5, 4000.0, 500.0))
        self.assertEqual(-7.0, rc.per_loop_arrival_hold(-7.0, 5, 4000.0, 500.0))
        # A degenerate align period also returns w0 unchanged.
        for bad in (0.0, -1.0, float("nan"), float("inf")):
            self.assertEqual(100.0, rc.per_loop_arrival_hold(100.0, 3, 4000.0, bad))

    def test_per_loop_arrival_hold_walks_the_cadence_remainder(self):
        # cadence % align == 0 => the hold never moves.
        for n in range(4):
            self.assertAlmostEqual(
                100.0, rc.per_loop_arrival_hold(100.0, n, 4000.0, 500.0))
        # cadence % align == 100 => the hold walks BACKWARDS 100 s per cycle and
        # wraps POSITIVELY; C#'s signed % would go negative without the double mod.
        self.assertAlmostEqual(150.0, rc.per_loop_arrival_hold(150.0, 0, 1100.0, 500.0))
        self.assertAlmostEqual(50.0, rc.per_loop_arrival_hold(150.0, 1, 1100.0, 500.0))
        self.assertAlmostEqual(450.0, rc.per_loop_arrival_hold(150.0, 2, 1100.0, 500.0))
        for n in range(6):
            val = rc.per_loop_arrival_hold(150.0, n, 1100.0, 500.0)
            self.assertGreaterEqual(val, 0.0)
            self.assertLess(val, 500.0)

    def test_circular_phase_error_takes_the_short_way_round(self):
        self.assertAlmostEqual(100.0, rc.circular_phase_error(100.0, 1000.0))
        self.assertAlmostEqual(100.0, rc.circular_phase_error(900.0, 1000.0))
        self.assertAlmostEqual(500.0, rc.circular_phase_error(500.0, 1000.0))
        self.assertAlmostEqual(0.0, rc.circular_phase_error(2000.0, 1000.0))
        for bad in (0.0, -1.0, float("nan"), float("inf")):
            self.assertEqual(0.0, rc.circular_phase_error(100.0, bad))

    def test_per_loop_joint_hold_first_hit_wins(self):
        # delta = 0 + 0*1000 + 100 = 100; one 500 s station period lands on 600,
        # whose phase error against a 300 s rotation is 0 <= tol.
        got = rc.per_loop_joint_hold(w0=100.0, n=0, cadence=1000.0, t_station=500.0,
                                     t_rot=300.0, tol=1.0, entry_offset0=0.0, max_k=8)
        self.assertAlmostEqual(600.0, got)

    def test_per_loop_joint_hold_falls_back_to_the_bounded_best(self):
        got = rc.per_loop_joint_hold(w0=100.0, n=0, cadence=1000.0, t_station=500.0,
                                     t_rot=3000.0, tol=1.0, entry_offset0=8700.0,
                                     max_k=64)
        # Nothing within budget brings the error under 1 s, so the DEFENSIVE
        # bounded-best is returned rather than an unbounded search.
        self.assertAlmostEqual(100.0, got)

    def test_per_loop_joint_hold_degenerates_to_the_base_hold(self):
        base = rc.per_loop_arrival_hold(100.0, 2, 1000.0, 500.0)
        for override in (dict(t_rot=0.0), dict(t_rot=float("nan")),
                         dict(tol=float("nan")), dict(tol=-1.0), dict(max_k=0),
                         dict(entry_offset0=float("nan"))):
            args = dict(w0=100.0, n=2, cadence=1000.0, t_station=500.0,
                        t_rot=300.0, tol=1.0, entry_offset0=0.0, max_k=8)
            args.update(override)
            self.assertAlmostEqual(base, rc.per_loop_joint_hold(**args))

    def test_compress_and_decompress_span_ut(self):
        cuts = [rc.LoiterCut(start_ut=1200.0, length_seconds=300.0)]
        self.assertAlmostEqual(1100.0, rc.compress_span_ut(1100.0, cuts))
        self.assertAlmostEqual(1200.0, rc.compress_span_ut(1200.0, cuts))
        # PARTIAL credit inside the cut: a mid-cut UT maps back to the cut start.
        self.assertAlmostEqual(1200.0, rc.compress_span_ut(1350.0, cuts))
        self.assertAlmostEqual(1400.0, rc.compress_span_ut(1700.0, cuts))
        self.assertAlmostEqual(1500.0, rc.compress_span_ut(1800.0, cuts))
        self.assertAlmostEqual(1700.0, rc.decompress_span_ut(1400.0, cuts))
        self.assertAlmostEqual(1100.0, rc.decompress_span_ut(1100.0, cuts))

    def test_compressed_span_ignores_a_cut_that_swallows_the_span(self):
        self.assertAlmostEqual(1000.0, rc.compressed_span_seconds(
            1000.0, 2000.0, [rc.LoiterCut(1200.0, 5000.0)]))
        self.assertAlmostEqual(1000.0, rc.compressed_span_seconds(1000.0, 2000.0, []))
        self.assertAlmostEqual(700.0, rc.compressed_span_seconds(
            1000.0, 2000.0, [rc.LoiterCut(1200.0, 300.0)]))

    def test_apply_hold_to_phase_three_regions(self):
        self.assertAlmostEqual(50.0, rc.apply_hold_to_phase(50.0, 100.0, 30.0))
        self.assertAlmostEqual(100.0, rc.apply_hold_to_phase(100.0, 100.0, 30.0))
        self.assertAlmostEqual(100.0, rc.apply_hold_to_phase(115.0, 100.0, 30.0))
        self.assertAlmostEqual(100.0, rc.apply_hold_to_phase(130.0, 100.0, 30.0))
        self.assertAlmostEqual(120.0, rc.apply_hold_to_phase(150.0, 100.0, 30.0))
        self.assertAlmostEqual(150.0, rc.apply_hold_to_phase(150.0, 100.0, 0.0))
        self.assertAlmostEqual(150.0, rc.apply_hold_to_phase(150.0, 100.0,
                                                            float("nan")))

    def test_apply_loiter_extension_wraps_inside_the_insertion(self):
        self.assertAlmostEqual(
            50.0, rc.apply_loiter_extension(50.0, 100.0, 300.0, 150.0))
        # Inside the insertion the phase WRAPS on the parking period:
        # 100 + ((200 - 100) mod 150) = 200, and 100 + (150 mod 150) = 100.
        self.assertAlmostEqual(
            200.0, rc.apply_loiter_extension(200.0, 100.0, 300.0, 150.0))
        self.assertAlmostEqual(
            100.0, rc.apply_loiter_extension(250.0, 100.0, 300.0, 150.0))
        self.assertAlmostEqual(
            200.0, rc.apply_loiter_extension(500.0, 100.0, 300.0, 150.0))
        self.assertAlmostEqual(
            500.0, rc.apply_loiter_extension(500.0, 100.0, 0.0, 150.0))

    def test_per_loop_launch_advance_is_a_sawtooth_not_a_drift(self):
        vals = [rc.per_loop_launch_advance(10000.0, 1000.0, n, 4000.0, 3000.0)
                for n in range(6)]
        # off_n = 9000 + n*4000, mod 3000 -> 0, 1000, 2000, 0, 1000, 2000
        self.assertEqual([0.0, 1000.0, 2000.0, 0.0, 1000.0, 2000.0], vals)
        for v in vals:
            self.assertGreaterEqual(v, 0.0)
            self.assertLess(v, 3000.0)

    def test_per_loop_launch_advance_takes_the_absolute_period(self):
        # abs() is PadAlignLaunch's retrograde handling, not defensive tidying.
        self.assertAlmostEqual(
            rc.per_loop_launch_advance(10000.0, 1000.0, 1, 4000.0, 3000.0),
            rc.per_loop_launch_advance(10000.0, 1000.0, 1, 4000.0, -3000.0))
        for bad in (0.0, float("nan"), float("inf")):
            self.assertEqual(0.0, rc.per_loop_launch_advance(
                10000.0, 1000.0, 1, 4000.0, bad))

    def test_capped_launch_advance_is_bounded_by_the_launching_cycle_slack(self):
        cuts = [rc.LoiterCut(1200.0, 300.0)]
        # slack = cadence - compressed - hold(win-1) = 4000 - 700 - 100 = 3200,
        # so a 1000 s raw advance passes through untouched.
        self.assertAlmostEqual(1000.0, rc.capped_launch_advance(
            10000.0, 1000.0, 2000.0, 4000.0, 1, 3000.0, cuts, 100.0, 500.0))
        # With the cadence barely above the compressed span the slack collapses.
        self.assertAlmostEqual(100.0, rc.capped_launch_advance(
            10000.0, 1000.0, 2000.0, 900.0, 1, 3000.0, cuts, 100.0, 500.0))

    def test_boundary_overlap_advance_is_gated_on_raw_exceeding_capped(self):
        cuts = [rc.LoiterCut(1200.0, 300.0)]
        raw = rc.per_loop_launch_advance(10000.0, 1000.0, 1, 4000.0, 3000.0)
        capped = rc.capped_launch_advance(10000.0, 1000.0, 2000.0, 4000.0, 1,
                                          3000.0, cuts, 100.0, 500.0)
        self.assertAlmostEqual(raw, capped)
        # Gate closed: the capped value is returned, never a blanket un-capping.
        self.assertAlmostEqual(capped, rc.boundary_overlap_advance(
            10000.0, 1000.0, 2000.0, 4000.0, 1, 3000.0, cuts, 100.0, 500.0))
        raw2 = rc.per_loop_launch_advance(10000.0, 1000.0, 1, 900.0, 3000.0)
        capped2 = rc.capped_launch_advance(10000.0, 1000.0, 2000.0, 900.0, 1,
                                           3000.0, cuts, 100.0, 500.0)
        self.assertGreater(raw2, capped2)
        self.assertAlmostEqual(raw2, rc.boundary_overlap_advance(
            10000.0, 1000.0, 2000.0, 900.0, 1, 3000.0, cuts, 100.0, 500.0))

    def test_clock_clamp_never_lets_a_hold_outrun_the_cadence(self):
        self.assertAlmostEqual(100.0, rc.clamp_hold_to_cycle(100.0, 700.0, 4000.0))
        self.assertAlmostEqual(200.0, rc.clamp_hold_to_cycle(1000.0, 700.0, 900.0))
        self.assertAlmostEqual(0.0, rc.clamp_hold_to_cycle(1000.0, 900.0, 700.0))

    def test_rotation_aligned_trigger_lands_in_the_first_period(self):
        trig = rc.rotation_aligned_trigger(10400.0, 1700.0, 3000.0)
        self.assertAlmostEqual(10700.0, trig)
        self.assertGreaterEqual(trig, 10400.0)
        self.assertLess(trig, 10400.0 + 3000.0)
        for bad in (0.0, -1.0, float("nan"), float("inf")):
            self.assertTrue(math.isnan(
                rc.rotation_aligned_trigger(10400.0, 1700.0, bad)))

    def test_compute_descent_timing_matches_the_hand_derivation(self):
        cuts = [rc.LoiterCut(1200.0, 300.0)]
        conic, entry, trigger = rc.compute_descent_timing(
            0, 10000.0, 4000.0, 1000.0, 1700.0, 3000.0, 0.0, cuts)
        self.assertAlmostEqual(1700.0, conic)
        self.assertAlmostEqual(10400.0, entry)
        self.assertAlmostEqual(10700.0, trigger)
        _c, entry1, trigger1 = rc.compute_descent_timing(
            1, 10000.0, 4000.0, 1000.0, 1700.0, 3000.0, 0.0, cuts)
        self.assertAlmostEqual(14400.0, entry1)
        self.assertAlmostEqual(16700.0, trigger1)

    def test_descent_congruence_residual_is_zero_by_construction(self):
        cuts = [rc.LoiterCut(1200.0, 300.0)]
        for n in range(4):
            _c, _e, trigger = rc.compute_descent_timing(
                n, 10000.0, 4000.0, 1000.0, 1700.0, 3000.0, 0.0, cuts)
            self.assertAlmostEqual(0.0, rc.descent_site_rotation_residual_deg(
                trigger, 1700.0, 3000.0))
        # A trigger nudged off the congruent instant reads a real residual.
        self.assertAlmostEqual(36.0, rc.descent_site_rotation_residual_deg(
            10700.0 + 300.0, 1700.0, 3000.0))

    def test_site_align_offset_seconds(self):
        self.assertAlmostEqual(750.0, rc.site_align_offset_seconds(90.0, 3000.0))
        self.assertAlmostEqual(0.0, rc.site_align_offset_seconds(360.0, 3000.0))
        self.assertAlmostEqual(2250.0, rc.site_align_offset_seconds(-90.0, 3000.0))
        for bad in (0.0, float("nan"), float("inf")):
            self.assertEqual(0.0, rc.site_align_offset_seconds(90.0, bad))

    def test_launch_residual_seam_deg_wraps_into_zero_360(self):
        self.assertAlmostEqual(
            120.0, rc.launch_residual_seam_deg(1000.0, 0.0, 3000.0))
        self.assertAlmostEqual(0.0, rc.launch_residual_seam_deg(3000.0, 0.0, 3000.0))
        self.assertTrue(math.isnan(rc.launch_residual_seam_deg(1.0, 0.0, 0.0)))

    def test_descent_window_end_ut(self):
        self.assertAlmostEqual(10950.0,
                               rc.descent_window_end_ut(10700.0, 1950.0, 1700.0))

    def test_whole_multiple_snap_tolerance(self):
        self.assertIs(True, rc.is_whole_multiple(300.0, 150.0))
        self.assertIs(True, rc.is_whole_multiple(300.0 + 1e-8, 150.0))
        self.assertIs(False, rc.is_whole_multiple(320.0, 150.0))
        self.assertIsNone(rc.is_whole_multiple(300.0, 0.0))
        self.assertIsNone(rc.is_whole_multiple(300.0, float("nan")))

    def test_leg_within_dock_clip_tests_the_start_only(self):
        # RouteTrajectoryLineRenderer.cs:178-182 - the END UT is deliberately not
        # consulted, and a "tidied" re-derivation would stop measuring the product.
        self.assertTrue(rc.leg_within_dock_clip(700.0, 800.0))
        self.assertFalse(rc.leg_within_dock_clip(800.0, 800.0))
        self.assertFalse(rc.leg_within_dock_clip(900.0, 800.0))
        self.assertTrue(rc.leg_within_dock_clip(900.0, 0.0))
        self.assertTrue(rc.leg_within_dock_clip(900.0, -1.0))


# ---------------------------------------------------------------------------
# Parsing, including the adversarial mutations.
# ---------------------------------------------------------------------------


class ParseTests(unittest.TestCase):

    def test_positive_fixture_parses_every_section(self):
        s = build(POSITIVE_MANIFEST)
        self.assertTrue(s.parsed, s.error)
        self.assertEqual("", s.error)
        self.assertEqual(1, s.schema_version)
        self.assertEqual("verb", s.export_reason)
        self.assertEqual("FLIGHT", s.scene)
        self.assertIs(True, s.env_armed)
        self.assertIs(True, s.map_render_tracing_on)
        # Anti-vacuity FLOOR: the counts below are what makes every rule cell
        # below a measurement instead of a walk over an empty snapshot.
        self.assertEqual(len(rc.RATIFIED_TOLERANCES), len(s.constants))
        self.assertEqual((), s.constants_unparsed)
        self.assertEqual(2, len(s.units))
        self.assertEqual(2, len(s.chain_builds))
        self.assertEqual(8, len(s.dwells))
        self.assertEqual(0, len(s.open_dwells))
        self.assertEqual(2, len(s.transitions))
        self.assertEqual(1, len(s.seam_tangents))
        self.assertEqual(1, len(s.seam_endpoints))
        self.assertEqual(11, len(s.clock_events))
        self.assertEqual(1, len(s.line_branches))
        self.assertEqual(4, len(s.ownership_changes))
        self.assertEqual(1, len(s.route_line_builds))
        self.assertEqual(1, len(s.route_leg_defers))
        self.assertEqual((), s.unknown_observed_sections)

    def test_plan_unit_fields_round_trip(self):
        unit = build(POSITIVE_MANIFEST).units[0]
        self.assertEqual("Flight", unit.host)
        self.assertEqual(0, unit.owner_index)
        self.assertAlmostEqual(4000.0, unit.cadence_seconds)
        self.assertAlmostEqual(1000.0, unit.span_seconds)
        self.assertIs(True, unit.is_reaim)
        self.assertEqual(2, len(unit.members))
        self.assertEqual("recA1", unit.members[1].rec_id)
        self.assertEqual((rc.LoiterCut(1200.0, 300.0),), unit.loiter_cuts)
        self.assertEqual((1,), unit.descent_member_indices)
        self.assertIsNotNone(unit.reaim_schedule)
        self.assertIsNone(unit.route)
        self.assertIsNotNone(build(POSITIVE_MANIFEST).units[1].route)
        self.assertEqual(("k1", "k2"),
                         build(POSITIVE_MANIFEST).units[1].route.excluded_interval_keys)

    def test_a_torn_file_is_a_named_fault_not_zero_records(self):
        torn = fill(POSITIVE_MANIFEST)[:len(fill(POSITIVE_MANIFEST)) // 2]
        s = rc.parse_render_manifest(torn)
        self.assertFalse(s.parsed)
        self.assertTrue(s.error)
        self.assertEqual((), s.dwells)

    def test_an_empty_file_is_a_named_fault_not_a_clean_zero_parse(self):
        # A zero-byte file is trivially brace-balanced, so without the
        # RENDER_MANIFEST root check it would read as a clean all-zero manifest.
        for text in ("", "   \n\n\t", None):
            s = rc.parse_render_manifest(text)
            self.assertFalse(s.parsed, repr(text))
            self.assertIn("RENDER_MANIFEST", s.error)

    def test_a_non_manifest_file_is_a_named_fault(self):
        s = rc.parse_render_manifest("GAME\n{\n\tversion = 1.12.5\n}\n")
        self.assertFalse(s.parsed)
        self.assertIn("no top-level RENDER_MANIFEST node", s.error)

    def test_two_manifest_roots_are_a_writer_contract_violation(self):
        s = rc.parse_render_manifest(fill(POSITIVE_MANIFEST) * 2)
        self.assertFalse(s.parsed)
        self.assertIn("2 RENDER_MANIFEST nodes", s.error)

    def test_unbalanced_braces_are_a_defined_parse_fault(self):
        s = rc.parse_render_manifest(fill(POSITIVE_MANIFEST) + "}\n")
        self.assertFalse(s.parsed)
        self.assertTrue(s.error)

    def test_open_at_export_dwell_is_kept_out_of_the_closed_population(self):
        # The shipped writer marks an open dwell with `openAtExport = True` on an
        # ordinary DWELL node. A half-observed interval must not count toward a
        # coverage union or an anti-vacuity floor.
        text = variant(("\t\t\tmarkerDecision = True\n\t\t\tmarkerTracedPath = False\n"
                        "\t\t\tmarkerPolyline = False\n\t\t\tmarkerIconSuppressed = False\n"
                        "\t\t}\n\t\tDWELL\n\t\t{\n\t\t\tpid = 5001\n"
                        "\t\t\trecId = recA0\n\t\t\tcommittedIndex = 0\n"
                        "\t\t\tchainSignature = sigA\n\t\t\tsegmentIndex = 0\n"
                        "\t\t\tphaseKind = ascent\n\t\t\ttreatment = TracedPath\n"
                        "\t\t\tvisible = True\n\t\t\tcoverage = InSegment\n"
                        "\t\t\tframeBody = Kerbin\n\t\t\townerIndex = 0\n"
                        "\t\t\topenUT = 14000.0",
                        "\t\t\tmarkerDecision = True\n\t\t\tmarkerTracedPath = False\n"
                        "\t\t\tmarkerPolyline = False\n\t\t\tmarkerIconSuppressed = False\n"
                        "\t\t}\n\t\tDWELL\n\t\t{\n\t\t\topenAtExport = True\n"
                        "\t\t\tpid = 5001\n"
                        "\t\t\trecId = recA0\n\t\t\tcommittedIndex = 0\n"
                        "\t\t\tchainSignature = sigA\n\t\t\tsegmentIndex = 0\n"
                        "\t\t\tphaseKind = ascent\n\t\t\ttreatment = TracedPath\n"
                        "\t\t\tvisible = True\n\t\t\tcoverage = InSegment\n"
                        "\t\t\tframeBody = Kerbin\n\t\t\townerIndex = 0\n"
                        "\t\t\topenUT = 14000.0"))
        s = build(text)
        self.assertTrue(s.parsed, s.error)
        self.assertEqual(7, len(s.dwells))
        self.assertEqual(1, len(s.open_dwells))
        self.assertTrue(s.open_dwells[0].is_open)

    def test_positions_absent_when_the_probe_never_sampled(self):
        s = build(POSITIVE_MANIFEST)
        with_pos = [d for d in s.dwells if d.open_position is not None]
        without = [d for d in s.dwells if d.open_position is None]
        self.assertTrue(with_pos)
        self.assertTrue(without)
        self.assertEqual("Kerbin", with_pos[0].open_position.body)
        self.assertAlmostEqual(100.0, with_pos[0].open_position.x)

    def test_phase_kind_tokens_and_names_both_normalise(self):
        self.assertEqual("HeliocentricTransfer",
                         rc._normalize_phase_kind("heliocentric-transfer"))
        self.assertEqual("HeliocentricTransfer",
                         rc._normalize_phase_kind("HeliocentricTransfer"))
        self.assertEqual("Hold", rc._normalize_phase_kind("hold"))
        self.assertEqual("None", rc._normalize_phase_kind("none"))
        # An unrecognised spelling stays VERBATIM so it reaches RC-UNKNOWN.
        self.assertEqual("wat", rc._normalize_phase_kind("wat"))
        self.assertEqual("Synthesized", rc._normalize_provenance("synthesized"))


class CSharpWriterFixtureTests(unittest.TestCase):
    """Reconciliation against the SHIPPED C# writer's own sample output.

    ``Source/Parsek.Tests/Fixtures/RenderManifest/sample-manifest.txt`` carries
    one record of every node kind and is drift-pinned on the C# side by
    ``RenderManifestSampleFixtureTests``. Reading the REAL file (rather than a
    hand-copy) is what makes this a reconciliation: a writer change that renames
    a key or flips a token's casing reds HERE.

    The sample's NUMBERS are illustrative, not a clean run (its tangent angle
    0.2345 exceeds the 0.1 tolerance on purpose), so this asserts the PARSE and
    that the rule set runs over it - never that it is finding-free."""

    def setUp(self):
        import os
        lib_dir = os.path.dirname(os.path.abspath(__file__))
        repo_root = os.path.dirname(os.path.dirname(lib_dir))
        self.path = os.path.join(repo_root, "Source", "Parsek.Tests", "Fixtures",
                                 "RenderManifest", "sample-manifest.txt")

    def _snapshot(self):
        with open(self.path, "r", encoding="utf-8") as fh:
            return rc.parse_render_manifest(fh.read())

    def test_the_shipped_sample_parses_every_node_kind(self):
        s = self._snapshot()
        self.assertTrue(s.parsed, s.error)
        self.assertEqual(1, s.schema_version)
        self.assertEqual("TRACKSTATION", s.scene)
        self.assertEqual(1, len(s.units))
        self.assertEqual(1, len(s.chain_builds))
        self.assertEqual(1, len(s.dwells))
        self.assertEqual(1, len(s.open_dwells))     # openAtExport = True
        self.assertEqual(1, len(s.transitions))
        self.assertEqual(1, len(s.seam_tangents))
        self.assertEqual(1, len(s.seam_endpoints))
        # 6 base kinds + TWO hold-engage / hold-release PAIRS: the review pass
        # replaced the frame-step stationarity test with stall accumulation, so
        # more than one run per (owner, cycle) is legal and the fixture pins that
        # shape (both pairs on cycle 1, ordinals 0 and 1).
        self.assertEqual(10, len(s.clock_events))
        self.assertEqual(2, len(s.line_branches))
        self.assertEqual(2, len(s.ownership_changes))
        self.assertEqual(1, len(s.ratified_skips))
        self.assertEqual(1, len(s.clock_defers))
        self.assertEqual(1, len(s.route_line_builds))
        self.assertEqual(1, len(s.route_leg_defers))
        self.assertEqual(1, len(s.route_codraw_violations))
        self.assertEqual(1, len(s.truncated))
        self.assertEqual((), s.unknown_observed_sections)
        # STANDALONE ANOMALY_ECHO (parent = OBSERVED), two records - and the
        # DWELL-nested aggregate of the same node NAME stays where it was. The
        # two are told apart by parent, never by name.
        self.assertEqual(2, len(s.anomaly_echoes))
        self.assertEqual(["100000", "route-0001"],
                         sorted(e.pid_key for e in s.anomaly_echoes))
        self.assertEqual((("rigid-seam-tangent-discontinuity", 1),),
                         s.dwells[0].anomaly_echoes)

    def test_the_standalone_anomaly_echo_is_a_modelled_record_not_an_unknown(self):
        # It arrived in the review pass; before it was modelled the whole record
        # reached RC-UNKNOWN as "a record type no rule claimed".
        s = self._snapshot()
        findings, _u, _m = rc.evaluate_rules(s, None)
        self.assertEqual([], [f for f in fails(findings, rc.RULE_UNKNOWN)
                              if "ANOMALY_ECHO" in f.target])
        facets = rc.observed_composition_facets(s)[rc.RENDER_COMPOSITION_BLOCK]
        self.assertEqual({"polyline-orbit-overlap": 1,
                          "rigid-seam-tangent-discontinuity": 1},
                         facets["anomalyEchoes"])

    def test_every_ratified_constant_is_exported_and_within_tolerance(self):
        # The .NET "R" format writes 17 significant digits
        # (0.78539816339744828), which is the SAME double as the table's
        # 0.7853981633974483 - hence math.isclose, never string equality.
        s = self._snapshot()
        for name, expected in rc.RATIFIED_TOLERANCES.items():
            self.assertIn(name, s.constants, name)
            self.assertTrue(math.isclose(s.constants[name], expected,
                                         rel_tol=rc.CONSTANT_REL_TOLERANCE),
                            "%s: %r vs %r" % (name, s.constants[name], expected))
        _f, _u, _m = rc.evaluate_rules(s, None)
        self.assertEqual([], fails(_f, rc.RULE_CONST))

    def test_the_shipped_token_vocabularies_are_all_recognised(self):
        # Casing is MIXED BY SOURCE: spine chain phases carry lowercase
        # PhaseKind tokens while treatment / coverage / scope / descent detailS
        # are PascalCase enum .ToString(). Nothing here may reach RC-UNKNOWN.
        s = self._snapshot()
        findings, _u, _m = rc.evaluate_rules(s, None)
        self.assertEqual([], fails(findings, rc.RULE_UNKNOWN),
                         [f.as_text() for f in fails(findings, rc.RULE_UNKNOWN)])
        self.assertEqual("Ascent", s.chain_builds[0].phases[0].kind)
        self.assertEqual("Recorded", s.chain_builds[0].phases[0].provenance)
        self.assertEqual("Ascent", s.dwells[0].phase_kind)
        self.assertEqual("TracedPath", s.dwells[0].treatment)
        self.assertEqual("InSegment", s.dwells[0].coverage)
        self.assertEqual("Inside", s.line_branches[1].coverage)
        self.assertEqual("SameBody", s.route_line_builds[0].scope)

    def test_the_shipped_sample_reaches_the_rules_and_reports_its_defects(self):
        s = self._snapshot()
        findings, unevaluable, _m = rc.evaluate_rules(s, None)
        rules = {f.rule_id for f in fails(findings)}
        # The sample's illustrative numbers exercise these paths on purpose.
        self.assertIn(rc.RULE_SEAM, rules)
        self.assertIn(rc.RULE_ROUTE, rules)      # its ROUTE_CODRAW_VIOLATION row
        self.assertIn(rc.RULE_DESCENT, rules)    # residual 0.0004 deg
        # Its TRUNCATED DWELL row makes coverage DEFINED-UNEVALUABLE, not unknown.
        self.assertIn("truncated-section-dwell", unevaluable)
        self.assertEqual([], fails(findings, rc.RULE_UNKNOWN))

    def test_the_descent_event_owner_index_is_the_member_not_the_unit(self):
        # Shipped convention: a descent-phase CLOCK_EVENT names its DESCENT
        # MEMBER index in ownerIndex (6 here) while the unit's ownerIndex is 4.
        # Resolving through the unit's owner would match nothing and silently
        # drop the whole descent leg.
        s = self._snapshot()
        unit = s.units[0]
        self.assertEqual(4, unit.owner_index)
        self.assertEqual((6, 7), unit.descent_member_indices)
        ev = [e for e in s.clock_events if e.kind == "descent-phase"][0]
        self.assertEqual(6, ev.owner_index)
        self.assertIn(ev.owner_index, unit.descent_member_indices)
        findings, unevaluable, _m = rc.evaluate_rules(s, None)
        self.assertNotIn("descent-primitives-absent", unevaluable)
        self.assertTrue([f for f in fails(findings, rc.RULE_DESCENT)])


# ---------------------------------------------------------------------------
# Rule behaviour: one positive reading and one violating fixture per defect
# class. The anchor every violating cell leans on is that the POSITIVE fixture
# produces ZERO FAIL and ZERO WARN findings, so a red below is attributable to
# the one line the variant changed.
# ---------------------------------------------------------------------------

# Insertion anchors, each unique in POSITIVE_MANIFEST.
ANCHOR_ROUTE_LEG_DEFER = "\t\tROUTE_LEG_DEFER\n"


class PositiveReadingTests(unittest.TestCase):

    def test_the_positive_fixture_produces_no_fail_and_no_warn(self):
        _s, (findings, _u, _m) = rules_for(POSITIVE_MANIFEST)
        self.assertEqual([], [f.as_text() for f in findings
                              if f.level in (rc.LEVEL_FAIL, rc.LEVEL_WARN)])

    def test_the_positive_fixture_still_reports_the_qual_trend_rows(self):
        _s, (findings, _u, _m) = rules_for(POSITIVE_MANIFEST)
        quals = {f.target for f in findings if f.rule_id == rc.RULE_QUAL}
        self.assertEqual({"seam.tangent", "seam.endpoint", "hold.arrival",
                          "hold.observed", "descent.siteRotationResidual"}, quals)

    def test_the_positive_fixture_names_its_unevaluable_clauses(self):
        # Defined-unevaluable is neither a pass nor a red: every one of these is a
        # POSITIVE statement about what the manifest cannot answer.
        #
        # Schema v1.1 retired FOUR of the five this fixture used to carry - the hold
        # pair, the recorded-clock dwell stamps and the descent head made RC-HOLD
        # leg 2, RC-CUT containment, RC-DESCENT's head clauses and RC-WARP's hold
        # half evaluable. What remains is a genuine gap in the fixture's own data,
        # not in the schema: its synthetic 3000 s destination rotation period
        # belongs to no body the ratified table carries, and the verifier declines
        # to assert that only the listed bodies exist.
        _s, (_f, unevaluable, _m) = rules_for(POSITIVE_MANIFEST)
        self.assertEqual({"plan-primitive-body-unidentified": 1}, dict(unevaluable))
        for reason in unevaluable:
            self.assertIn(reason.split("-")[0],
                          {r.split("-")[0] for r in rc.UNEVALUABLE_REASONS})


class RuleViolationTests(unittest.TestCase):

    def test_rc_cover_reds_on_an_unexplained_dark_window(self):
        _s, (findings, _u, _m) = rules_for(
            variant(("openUT = 12000.0", "openUT = 13000.0")))
        cover = fails(findings, rc.RULE_COVER)
        self.assertEqual(1, len(cover), [f.as_text() for f in findings])
        self.assertIn("unexplained dark window", cover[0].message)
        self.assertIn("1000", cover[0].message)

    def test_rc_cover_stays_silent_when_the_gap_is_below_the_warp_resolution(self):
        # Same gap shape, but the dwell's own maxUtStep says one frame stepped
        # further than the gap is wide, so the window is UNRESOLVABLE, not dark.
        _s, (findings, _u, _m) = rules_for(variant(
            ("openUT = 12000.0", "openUT = 13000.0"),
            ("maxUtStep = 5.0\n\t\t\topenBody = Kerbin",
             "maxUtStep = 4000.0\n\t\t\topenBody = Kerbin")))
        self.assertEqual([], fails(findings, rc.RULE_COVER))

    def test_rc_cut_reds_on_a_non_whole_multiple(self):
        _s, (findings, _u, _m) = rules_for(
            variant(("lengthSeconds = 300.0", "lengthSeconds = 320.0")))
        cut = fails(findings, rc.RULE_CUT)
        self.assertEqual(1, len(cut))
        self.assertIn("not a whole multiple", cut[0].message)
        # A cut length change also moves the compressed clock, so the descent
        # recomputation legitimately diverges too - the coupling is real and is
        # asserted rather than filtered, so a future decoupling reds here.
        self.assertTrue(fails(findings, rc.RULE_DESCENT))

    def test_rc_hold_reds_on_an_interior_gap_that_outlives_a_cycle(self):
        _s, (findings, _u, _m) = rules_for(variant(
            (ANCHOR_ROUTE_LEG_DEFER, OVERLONG_INTERIOR_GAP_DWELL)))
        hold = fails(findings, rc.RULE_HOLD)
        self.assertEqual(1, len(hold))
        self.assertIn("held-forever", hold[0].message)
        self.assertIn("5000", hold[0].message)

    def test_rc_hold_reds_on_an_unnormalised_w0(self):
        _s, (findings, _u, _m) = rules_for(
            variant(("arrivalHoldSeconds = 100.0", "arrivalHoldSeconds = 900.0")))
        hold = fails(findings, rc.RULE_HOLD)
        self.assertTrue(hold)
        self.assertIn("not normalised", hold[0].message)

    def test_rc_seam_reds_on_a_tangent_outside_tolerance(self):
        _s, (findings, _u, _m) = rules_for(
            variant(("angleRad = 0.05", "angleRad = 0.25")))
        seam = fails(findings, rc.RULE_SEAM)
        self.assertEqual(1, len(seam))
        self.assertIn("exceeds tolerance", seam[0].message)

    def test_rc_seam_reds_on_an_endpoint_ratio_outside_tolerance(self):
        _s, (findings, _u, _m) = rules_for(
            variant(("ratio = 1.002", "ratio = 1.09")))
        seam = fails(findings, rc.RULE_SEAM)
        self.assertEqual(1, len(seam))
        self.assertIn("endpoint/SOI ratio", seam[0].message)

    def test_rc_seam_reds_when_a_body_change_is_classified_rigid(self):
        # PhaseSeamClassifier.Classify ranks a body change ABOVE rigid.
        _s, (findings, _u, _m) = rules_for(
            variant(("kind = flexible-soi", "kind = rigid")))
        seam = fails(findings, rc.RULE_SEAM)
        self.assertEqual(1, len(seam))
        self.assertIn("classified as a rigid seam", seam[0].message)

    def test_rc_seam_is_unevaluable_not_passing_when_tracing_was_off(self):
        text = variant(("mapRenderTracingOn = True", "mapRenderTracingOn = False"))
        # Drop both measurement records, as a manifest-only lane produces.
        for node in ("SEAM_TANGENT", "SEAM_ENDPOINT"):
            start = text.index("\t\t%s\n" % node)
            end = text.index("\t\t}\n", start) + len("\t\t}\n")
            text = text[:start] + text[end:]
        _s, (findings, unevaluable, _m) = rules_for(text)
        self.assertEqual([], fails(findings, rc.RULE_SEAM))
        self.assertIn(rc.UNEVAL_SEAM_TRACING_OFF, unevaluable)
        self.assertGreaterEqual(unevaluable[rc.UNEVAL_SEAM_TRACING_OFF], 2)

    def test_rc_route_reds_on_a_dock_clip_violation(self):
        _s, (findings, _u, _m) = rules_for(
            variant(("recordedDockUT = 800.0", "recordedDockUT = 600.0")))
        route = fails(findings, rc.RULE_ROUTE)
        self.assertEqual(1, len(route))
        self.assertIn("dock clip", route[0].message)

    def test_rc_route_reds_on_a_co_draw_violation_record(self):
        _s, (findings, _u, _m) = rules_for(
            variant((ANCHOR_ROUTE_LEG_DEFER, CODRAW_VIOLATION_RECORD)))
        route = fails(findings, rc.RULE_ROUTE)
        self.assertEqual(1, len(route))
        self.assertIn("painted the same recording", route[0].message)

    def test_rc_route_reds_when_same_body_scope_dropped_legs(self):
        _s, (findings, _u, _m) = rules_for(
            variant(("transferLegsDropped = 0", "transferLegsDropped = 2")))
        route = fails(findings, rc.RULE_ROUTE)
        self.assertEqual(1, len(route))
        self.assertIn("same-body scope dropped", route[0].message)

    def test_rc_const_reds_on_a_drifted_constant(self):
        text = fill(POSITIVE_MANIFEST, overrides={
            "SeamEndpointOracle.DefaultRatioTolerance": 1.05})
        findings, _u, _m = rc.evaluate_rules(rc.parse_render_manifest(text), None)
        const = fails(findings, rc.RULE_CONST)
        self.assertEqual(1, len(const))
        self.assertIn("drifted from the ratified", const[0].message)

    def test_rc_const_reds_on_a_missing_constant(self):
        text = fill(POSITIVE_MANIFEST,
                    drop=("ShadowRenderDriver.SeedFreshnessFrames",))
        findings, _u, _m = rc.evaluate_rules(rc.parse_render_manifest(text), None)
        const = fails(findings, rc.RULE_CONST)
        self.assertEqual(1, len(const))
        self.assertIn("absent from the exported CONSTANTS node", const[0].message)

    def test_rc_const_reds_when_the_alias_diverges_from_its_canonical(self):
        text = fill(POSITIVE_MANIFEST, overrides={
            "CrossMemberSeamStitcher.TangentToleranceRadians": 0.2,
            "PhaseSeamClassifier.DefaultTangentToleranceRadians": 0.2})
        findings, _u, _m = rc.evaluate_rules(rc.parse_render_manifest(text), None)
        # Both drifted from the table AND the alias pair still agrees, so this is
        # two table drifts and no alias finding.
        self.assertEqual(2, len(fails(findings, rc.RULE_CONST)))
        text = fill(POSITIVE_MANIFEST, overrides={
            "CrossMemberSeamStitcher.TangentToleranceRadians": 0.2})
        findings, _u, _m = rc.evaluate_rules(rc.parse_render_manifest(text), None)
        msgs = [f.message for f in fails(findings, rc.RULE_CONST)]
        self.assertTrue(any("alias" in m for m in msgs), msgs)

    def test_rc_const_warns_on_an_unpinned_exported_constant(self):
        text = fill(POSITIVE_MANIFEST).replace(
            "\tPLAN\n", "", 0)
        text = fill(POSITIVE_MANIFEST).replace(
            "\t\tShadowRenderDriver.SeedFreshnessFrames = 2.0",
            "\t\tShadowRenderDriver.SeedFreshnessFrames = 2.0\n"
            "\t\tSomeNewThing.NobodyPinned = 7.5", 1)
        findings, _u, _m = rc.evaluate_rules(rc.parse_render_manifest(text), None)
        warns = [f for f in findings
                 if f.level == rc.LEVEL_WARN and f.rule_id == rc.RULE_CONST]
        self.assertEqual(1, len(warns))
        self.assertIn("not pinned by RATIFIED_TOLERANCES", warns[0].message)

    def test_rc_unknown_reds_on_an_unknown_clock_event_kind(self):
        _s, (findings, _u, _m) = rules_for(
            variant(("kind = reaim-window", "kind = mystery-event")))
        unknown = fails(findings, rc.RULE_UNKNOWN)
        self.assertEqual(1, len(unknown))
        self.assertIn("unknown clock-event kind", unknown[0].message)

    def test_rc_unknown_reds_on_an_unmodelled_observed_record(self):
        _s, (findings, _u, _m) = rules_for(
            variant((ANCHOR_ROUTE_LEG_DEFER, UNKNOWN_OBSERVED_RECORD)))
        unknown = fails(findings, rc.RULE_UNKNOWN)
        self.assertEqual(1, len(unknown))
        self.assertIn("MYSTERY_RECORD", unknown[0].target)

    def test_a_truncated_section_is_unevaluable_never_unknown(self):
        _s, (findings, unevaluable, _m) = rules_for(
            variant((ANCHOR_ROUTE_LEG_DEFER, TRUNCATED_DWELL_RECORD)))
        self.assertEqual([], fails(findings, rc.RULE_UNKNOWN))
        self.assertEqual([], fails(findings, rc.RULE_COVER))
        self.assertEqual(17, unevaluable["truncated-section-dwell"])

    def test_rc_own_reds_on_two_concurrent_treatments_for_one_pid(self):
        _s, (findings, _u, _m) = rules_for(
            variant(("openUT = 14000.0", "openUT = 11000.0")))
        own = fails(findings, rc.RULE_OWN)
        self.assertTrue(own)
        self.assertIn("exactly one treatment is active", own[0].message)

    def test_rc_own_reds_when_a_published_ownership_has_no_draw(self):
        _s, (findings, _u, _m) = rules_for(
            variant(("\t\t\trecId = recA0\n\t\t\tut = 14000.0\n\t\t\tevent = appear",
                     "\t\t\trecId = recB1\n\t\t\tut = 14000.0\n\t\t\tevent = appear")))
        own = fails(findings, rc.RULE_OWN)
        self.assertTrue(own)
        self.assertTrue(any("implies a draw" in f.message
                            or "implies a publish" in f.message for f in own))


class OracleIndependenceTests(unittest.TestCase):
    """Both legs of the design's binding independence rule."""

    def test_leg_1_plan_primitive_drifted_from_the_stock_table(self):
        # 21560 is 4.9e-4 relative from Kerbin's ratified 21549.425183089825 -
        # inside the near-match band, so it is a DRIFTED primitive rather than an
        # unlisted body, and the finding names leg 1 explicitly.
        _s, (findings, _u, _m) = rules_for(variant((
            "launchBodyRotationPeriodSeconds = 21549.425183089825",
            "launchBodyRotationPeriodSeconds = 21560.0")))
        const = fails(findings, rc.RULE_CONST)
        self.assertEqual(1, len(const))
        self.assertIn("leg 1 (solver drift)", const[0].message)
        self.assertIn("Kerbin", const[0].message)

    def test_leg_1_stays_silent_for_a_body_the_table_does_not_carry(self):
        # Far outside the near-match band => an unlisted or modded body, which is
        # DEFINED-UNEVALUABLE. A red here would be the verifier asserting that
        # only five bodies exist.
        _s, (findings, unevaluable, _m) = rules_for(variant((
            "launchBodyRotationPeriodSeconds = 21549.425183089825",
            "launchBodyRotationPeriodSeconds = 47000.0")))
        self.assertEqual([], fails(findings, rc.RULE_CONST))
        self.assertEqual(2, unevaluable[rc.UNEVAL_PRIMITIVE_BODY_UNKNOWN])

    def test_leg_1_uses_the_exact_row_when_a_body_name_is_present(self):
        # The forward-compatible launchBodyName key (absent from schema v1): with
        # a name the check is exact against THAT row, not "some known period".
        text = variant(("\t\t\tlaunchHoldEngaged = True",
                        "\t\t\tlaunchBodyName = Mun\n\t\t\tlaunchHoldEngaged = True"))
        _s, (findings, _u, _m) = rules_for(text)
        const = fails(findings, rc.RULE_CONST)
        self.assertEqual(1, len(const))
        self.assertIn("for body 'Mun'", const[0].message)

    def test_leg_2_observed_trigger_drifted_from_the_recomputation(self):
        _s, (findings, _u, _m) = rules_for(
            variant(("detailA = 10700.0", "detailA = 10750.0")))
        descent = fails(findings, rc.RULE_DESCENT)
        self.assertEqual(1, len(descent))
        self.assertIn("leg 2 (render drift)", descent[0].message)
        self.assertIn("triggerUT", descent[0].message)

    def test_leg_2_observed_residual_drifted_from_zero(self):
        _s, (findings, _u, _m) = rules_for(
            variant(("detailC = 0.0\n\t\t\tdetailS = Descent\n\t\t\tdetailD = 1700.0"
                     "\n\t\t}\n\t\tCLOCK_EVENT"
                     "\n\t\t{\n\t\t\tkind = descent-phase\n\t\t\townerIndex = 1\n"
                     "\t\t\tcycleIndex = 1",
                     "detailC = 0.5\n\t\t\tdetailS = Descent\n\t\t\tdetailD = 1700.0"
                     "\n\t\t}\n\t\tCLOCK_EVENT"
                     "\n\t\t{\n\t\t\tkind = descent-phase\n\t\t\townerIndex = 1\n"
                     "\t\t\tcycleIndex = 1")))
        descent = fails(findings, rc.RULE_DESCENT)
        self.assertEqual(1, len(descent))
        self.assertIn("site rotation residual", descent[0].message)

    def test_the_two_legs_are_distinguishable_in_the_message(self):
        # The design requires the finding to NAME which leg disagreed; a single
        # "mismatch" string would leave a solver bug and a render bug identical.
        _s, (leg1, _u, _m) = rules_for(variant((
            "launchBodyRotationPeriodSeconds = 21549.425183089825",
            "launchBodyRotationPeriodSeconds = 21560.0")))
        _s, (leg2, _u, _m) = rules_for(
            variant(("detailA = 10700.0", "detailA = 10750.0")))
        self.assertTrue(all("leg 1" in f.message for f in fails(leg1)))
        self.assertTrue(all("leg 2" in f.message for f in fails(leg2)))


# ---------------------------------------------------------------------------
# Spec surface: window grammar + the three anti-vacuity notches.
# ---------------------------------------------------------------------------


class SpecSurfaceTests(unittest.TestCase):

    def test_no_block_declared_is_valid(self):
        self.assertEqual([], rc.validate_render_composition_expectations(None))

    def test_a_non_table_block_is_rejected(self):
        errs = rc.validate_render_composition_expectations(7)
        self.assertEqual(1, len(errs))
        self.assertIn("must be a table", errs[0])

    def test_unknown_keys_are_rejected(self):
        errs = rc.validate_render_composition_expectations({"minDwells": 3})
        self.assertTrue(any("unknown key(s)" in e for e in errs))

    def test_bool_is_rejected_as_a_window_despite_being_an_int_subclass(self):
        errs = rc.validate_render_composition_expectations({"dwells": True})
        self.assertEqual(1, len(errs))
        self.assertIn("must be a non-negative int", errs[0])
        errs = rc.validate_render_composition_expectations(
            {"dwells": {"min": True}})
        self.assertTrue(any("must be a non-negative int" in e for e in errs))

    def test_window_grammar_edges(self):
        cases = {
            "negative bare int": ({"dwells": -1}, "must be >= 0"),
            "empty window": ({"dwells": {}}, "gates nothing"),
            "unknown window key": ({"dwells": {"min": 1, "avg": 2}},
                                   "unknown key(s)"),
            "min above max": ({"dwells": {"min": 5, "max": 2}}, "min 5 > max 2"),
            "non-table window": ({"dwells": "many"}, "must be a non-negative int"),
        }
        for label, (block, needle) in cases.items():
            errs = rc.validate_render_composition_expectations(block)
            self.assertTrue(any(needle in e for e in errs), "%s: %r" % (label, errs))

    def test_the_window_grammar_matches_saveparses_semantics(self):
        # The two implementations are deliberate COPIES (saveparse's are
        # privates). This cell is the drift guard between them: same inputs,
        # same verdict, so a change to one that is not made to the other reds.
        samples = [3, 0, -1, True, {"min": 1}, {"max": 0}, {"min": 1, "max": 5},
                   {"min": 5, "max": 1}, {}, {"min": True}, {"avg": 2}, "x", None]
        for val in samples:
            mine = rc._validate_window("p", val)
            theirs = saveparse._validate_window("p", val)
            self.assertEqual(bool(theirs), bool(mine), repr(val))
            self.assertEqual(theirs, mine, repr(val))

    def test_all_five_copied_helpers_match_saveparses_semantics(self):
        # FIVE helpers are copies, not one: the window grammar, the three
        # anti-vacuity notches, and the evaluator's `_check_window`. The cell
        # above covered exactly one of them, so four could drift silently.
        keys = ("dwells", "cycles", "unevaluable")
        blocks = [
            {}, {"gating": True}, {"gating": False}, {"gating": "true"},
            {"gating": 1}, {"gating": True, "dwells": 3},
            {"gating": True, "dwells": {"min": 0}},
            {"gating": True, "dwells": {"min": 0, "max": 4}},
            {"dwells": {"min": 0}},
            {"gating": True, "cycles": {"min": 0}, "unevaluable": {"max": 2}},
        ]
        for block in blocks:
            with self.subTest(block=block):
                self.assertEqual(saveparse._validate_gating("p", block),
                                 rc._validate_gating("p", block))
                self.assertEqual(
                    saveparse._validate_armed_empty("p", block, keys),
                    rc._validate_armed_empty("p", block, keys))
                self.assertEqual(
                    saveparse._validate_armed_unreddable("p", block, keys),
                    rc._validate_armed_unreddable("p", block, keys))
        for spec_val in (3, 0, True, {"min": 1}, {"max": 0},
                         {"min": 1, "max": 5}, {}, "x", None):
            for measured in (0, 1, 5, 9):
                with self.subTest(spec_val=spec_val, measured=measured):
                    mine, theirs = [], []
                    rc._check_window("p", spec_val, measured, mine)
                    saveparse._check_window("p", spec_val, measured, theirs)
                    self.assertEqual(theirs, mine)

    def test_the_gating_key_is_imported_from_saveparse_not_copied(self):
        # It is the spelling of a spec KEY: two modules disagreeing about it
        # would let an armed block read as unarmed on one side.
        self.assertIs(saveparse.GATING_KEY, rc.GATING_KEY)

    def test_gating_must_be_a_bool(self):
        errs = rc.validate_render_composition_expectations(
            {"gating": "true", "dwells": 1})
        self.assertTrue(any("must be a bool" in e for e in errs))

    def test_armed_with_no_assertion_key_is_an_error(self):
        errs = rc.validate_render_composition_expectations({"gating": True})
        self.assertEqual(1, len(errs))
        self.assertIn("gates nothing", errs[0])
        # Unarmed and empty is only a WARNING - a reading that asserts nothing is
        # uninformative, not a lie.
        self.assertEqual([], rc.validate_render_composition_expectations(
            {"gating": False}))
        warns = rc.render_composition_expectation_warnings(
            {"renderComposition": {}})
        self.assertEqual(1, len(warns))
        self.assertIn("gates nothing", warns[0])

    def test_an_armed_min_zero_window_can_never_red_and_is_refused(self):
        errs = rc.validate_render_composition_expectations(
            {"gating": True, "dwells": {"min": 0}})
        self.assertEqual(1, len(errs))
        self.assertIn("can never red", errs[0])
        # With a max beside it the window is a real gate.
        self.assertEqual([], rc.validate_render_composition_expectations(
            {"gating": True, "dwells": {"min": 0, "max": 20}}))
        # UNARMED, the same window is merely a reading.
        self.assertEqual([], rc.validate_render_composition_expectations(
            {"dwells": {"min": 0}}))

    def test_list_keys_reject_empty_unknown_duplicate_and_non_string(self):
        cases = {
            "empty": ({"warpBuckets": []}, "asserts nothing"),
            "unknown": ({"warpBuckets": ["warp10"]}, "unknown value"),
            "duplicate": ({"warpBuckets": ["warp1x", "warp1x"]},
                          "listed more than once"),
            "non string": ({"warpBuckets": [7]}, "must be a string"),
            "not a list": ({"warpBuckets": "warp1x"}, "must be a list"),
            "seam kind": ({"requireSeamKinds": ["wobbly"]}, "unknown value"),
        }
        for label, (block, needle) in cases.items():
            errs = rc.validate_render_composition_expectations(block)
            self.assertTrue(any(needle in e for e in errs), "%s: %r" % (label, errs))
        self.assertEqual([], rc.validate_render_composition_expectations(
            {"warpBuckets": ["warp1x", "warp100"],
             "requireSeamKinds": ["rigid", "flexible-soi"]}))

    def test_require_seam_kinds_only_accepts_kinds_the_writer_can_emit(self):
        # `SeamKindToken` emits exactly three tokens and `switch-continuation` is
        # not one of them - it belongs to PhaseSeamClassifier's PhaseSeamKind,
        # which never reaches a manifest SEAM record. A spec requiring it could
        # only ever fly and red, so it is refused BEFORE launch.
        self.assertEqual(("rigid", "flexible-soi", "none"), rc.SEAM_KINDS)
        self.assertEqual(("rigid", "flexible-soi"), rc.SEAM_KINDS_REQUIRABLE)
        for token in ("switch-continuation", "none"):
            with self.subTest(token=token):
                errs = rc.validate_render_composition_expectations(
                    {"requireSeamKinds": [token]})
                self.assertTrue(any("unknown value" in e for e in errs), errs)

    def test_the_parse_vocabulary_still_tolerates_none(self):
        # `none` is a REAL emitted token: a boundary with no distinguished seam.
        # It must parse without reaching RC-UNKNOWN even though no spec may
        # require it.
        self.assertIn("none", rc.SEAM_KINDS)
        _s, (findings, _u, _m) = rules_for(
            POSITIVE_MANIFEST.replace("kind = rigid", "kind = none", 1))
        self.assertEqual([], [f for f in fails(findings, rc.RULE_UNKNOWN)
                              if "seam kind" in f.message])

    def test_block_accessors(self):
        self.assertEqual((), rc.declared_composition_blocks(None))
        self.assertEqual((), rc.declared_composition_blocks({}))
        self.assertEqual(("renderComposition",), rc.declared_composition_blocks(
            {"renderComposition": {"dwells": 1}}))
        self.assertEqual((), rc.armed_composition_blocks(
            {"renderComposition": {"dwells": 1}}))
        self.assertEqual(("renderComposition",), rc.armed_composition_blocks(
            {"renderComposition": {"gating": True, "dwells": 1}}))
        self.assertFalse(rc.gating_armed({"renderComposition": {"dwells": 1}}))
        self.assertTrue(rc.gating_armed(
            {"renderComposition": {"gating": True, "dwells": 1}}))
        # gating = "true" (a string) is NOT armed; the validator rejects it
        # up front, and the accessor must not treat a truthy string as an arm.
        self.assertFalse(rc.gating_armed(
            {"renderComposition": {"gating": "true", "dwells": 1}}))

    def test_render_composition_is_not_a_reserved_expectation_block(self):
        # SPEC decision 6, and the `ledger` shape: a block enters
        # RESERVED_EXPECTATION_BLOCKS only while NO evaluator owns it. This
        # evaluator ships in the same change, so the name never enters the tuple
        # and evaluate_expectations tolerates it as an unknown block.
        import hlib
        self.assertEqual(("route", "loop"), hlib.RESERVED_EXPECTATION_BLOCKS)
        self.assertNotIn(rc.RENDER_COMPOSITION_BLOCK,
                         hlib.RESERVED_EXPECTATION_BLOCKS)

    def test_the_assertion_key_vocabulary_is_pinned(self):
        self.assertEqual(("gating", "dwells", "cycles", "unevaluable",
                          "warpBuckets", "requireSeamKinds"),
                         rc.RENDER_COMPOSITION_BLOCK_KEYS)
        self.assertEqual(("dwells", "cycles", "unevaluable"),
                         rc.RENDER_COMPOSITION_WINDOW_KEYS)
        self.assertEqual(("warpBuckets", "requireSeamKinds"),
                         rc.RENDER_COMPOSITION_LIST_KEYS)


# ---------------------------------------------------------------------------
# Facets + the evaluator.
# ---------------------------------------------------------------------------


class FacetTests(unittest.TestCase):

    def test_facets_are_empty_when_nothing_was_measured(self):
        # ABSENT means "not measured", never zero.
        self.assertEqual({}, rc.observed_composition_facets(None))
        self.assertEqual({}, rc.observed_composition_facets(
            rc.parse_render_manifest("")))

    def test_facets_mirror_the_block_layout_and_carry_the_window_keys(self):
        facets = rc.observed_composition_facets(build(POSITIVE_MANIFEST))
        block = facets["renderComposition"]
        for key in rc.RENDER_COMPOSITION_WINDOW_KEYS:
            self.assertIn(key, block)
        self.assertEqual(8, block["dwells"])
        self.assertEqual(4, block["cycles"])       # two per unit, two units
        self.assertEqual(1, block["unevaluable"])
        self.assertEqual({"warp1x": 460, "warpPhys": 0, "warp100": 80,
                          "warp1000": 0, "warpHigh": 0}, block["warpBuckets"])
        self.assertEqual({"rigid": 2, "flexible-soi": 1}, block["seamKinds"])
        self.assertEqual({"cycle-rollover": 6, "descent-phase": 2,
                          "reaim-window": 1, "hold-engage": 1,
                          "hold-release": 1}, block["clockEvents"])
        self.assertEqual({"TracedPath": 2, "StockConic": 6}, block["treatments"])
        self.assertEqual(2, block["planUnits"])
        self.assertEqual([], block["truncatedSections"])

    def test_facets_record_the_rule_census_and_the_unevaluable_ledger(self):
        block = rc.observed_composition_facets(
            build(POSITIVE_MANIFEST))["renderComposition"]
        self.assertEqual({}, block["findings"]["FAIL"])
        self.assertEqual({}, block["findings"]["WARN"])
        self.assertEqual({"RC-QUAL": 5}, block["findings"]["INFO"])
        # The ledger names WHY a clause could not be answered. The positive fixture
        # has exactly one such clause left after schema v1.1: its synthetic 3000 s
        # destination rotation belongs to no ratified body.
        self.assertEqual({"plan-primitive-body-unidentified": 1},
                         block["unevaluableReasons"])

    def test_quality_facets_are_json_safe(self):
        import json
        block = rc.observed_composition_facets(
            build(POSITIVE_MANIFEST))["renderComposition"]
        # NaN/Inf ride as None: a run JSON read with strict parsing must not
        # carry a NaN literal, and "not measured" is exactly what None means.
        json.dumps(block, allow_nan=False)
        self.assertAlmostEqual(0.05, block["quality"]["maxTangentAngleRad"])
        self.assertAlmostEqual(1.002, block["quality"]["maxEndpointRatio"])
        self.assertIsNone(block["quality"]["maxInteriorGapSeconds"])
        self.assertEqual([2.0], block["quality"]["cutWholeRatios"])
        self.assertEqual(0, block["quality"]["coverUnexplainedGaps"])


class EvaluateTests(unittest.TestCase):

    POS = {"renderComposition": {"dwells": {"min": 4},
                                 "cycles": {"min": 2},
                                 "unevaluable": {"max": 10}}}

    def test_no_block_declared_is_an_empty_report_row_with_facets(self):
        res = rc.evaluate_render_composition({}, build(POSITIVE_MANIFEST))
        self.assertEqual(rc.STATUS_REPORT, res.status)
        self.assertFalse(res.gating)
        self.assertEqual((), res.blocks)
        self.assertEqual((), res.mismatches)
        self.assertTrue(res.observed)          # facets are UNCONDITIONAL
        self.assertIs(True, res.parsed)

    def test_an_absent_manifest_is_a_defined_mismatch_on_every_declared_block(self):
        res = rc.evaluate_render_composition(self.POS, None)
        self.assertEqual(("renderComposition",), res.blocks)
        self.assertEqual(1, len(res.mismatches))
        self.assertIn("manifest absent", res.mismatches[0])
        self.assertIsNone(res.parsed)
        self.assertEqual({}, res.observed)
        # Unarmed, the fault is RECORDED and moves no verdict.
        self.assertEqual(rc.STATUS_REPORT, res.status)

    def test_an_absent_manifest_reds_an_armed_block(self):
        exp = {"renderComposition": dict(self.POS["renderComposition"],
                                         gating=True)}
        res = rc.evaluate_render_composition(exp, None)
        self.assertEqual(rc.STATUS_FAIL, res.status)
        self.assertTrue(res.gating)
        self.assertEqual(res.mismatches, res.armed_mismatches)

    def test_an_unparseable_manifest_is_a_defined_mismatch_not_zero_records(self):
        res = rc.evaluate_render_composition(
            self.POS, rc.parse_render_manifest("GAME\n{\n}\n"))
        self.assertEqual(1, len(res.mismatches))
        self.assertIn("manifest unreadable", res.mismatches[0])
        self.assertIn("RENDER_MANIFEST", res.mismatches[0])
        self.assertIs(False, res.parsed)

    def test_a_clean_armed_run_passes(self):
        exp = {"renderComposition": dict(self.POS["renderComposition"],
                                         gating=True)}
        res = rc.evaluate_render_composition(exp, build(POSITIVE_MANIFEST))
        self.assertEqual(rc.STATUS_PASS, res.status, res.mismatches)
        self.assertEqual((), res.armed_mismatches)
        self.assertEqual((), res.fail_findings)
        self.assertEqual(5, len(res.findings))     # the RC-QUAL trend rows

    def test_an_armed_window_miss_reds(self):
        exp = {"renderComposition": {"gating": True, "dwells": {"min": 99}}}
        res = rc.evaluate_render_composition(exp, build(POSITIVE_MANIFEST))
        self.assertEqual(rc.STATUS_FAIL, res.status)
        self.assertEqual(("renderComposition.dwells 8 < min 99",), res.mismatches)

    def test_an_armed_block_gates_on_fail_findings_too(self):
        # The findings ARE the module; a block that armed but let a FAIL finding
        # through would be fail-open.
        exp = {"renderComposition": dict(self.POS["renderComposition"],
                                         gating=True)}
        snap = build(variant(("angleRad = 0.05", "angleRad = 0.25")))
        res = rc.evaluate_render_composition(exp, snap)
        self.assertEqual(rc.STATUS_FAIL, res.status)
        self.assertTrue(any("RC-SEAM" in m for m in res.armed_mismatches))

    def test_the_same_fixture_only_reports_when_unarmed(self):
        snap = build(variant(("angleRad = 0.05", "angleRad = 0.25")))
        res = rc.evaluate_render_composition(self.POS, snap)
        self.assertEqual(rc.STATUS_REPORT, res.status)
        self.assertEqual((), res.armed_mismatches)
        self.assertTrue(any("RC-SEAM" in m for m in res.mismatches))

    def test_the_unevaluable_ceiling_is_the_honest_anti_vacuity_gate(self):
        # A run where nothing could be measured must not pass just because
        # nothing red. The `unevaluable` window is the key that says so.
        exp = {"renderComposition": {"gating": True, "unevaluable": {"max": 0}}}
        res = rc.evaluate_render_composition(exp, build(POSITIVE_MANIFEST))
        self.assertEqual(rc.STATUS_FAIL, res.status)
        self.assertEqual(("renderComposition.unevaluable 1 > max 0",),
                         res.mismatches)
        self.assertEqual(1, sum(res.unevaluable.values()))
        # ... and the same fixture PASSES the ceiling it actually meets, so the cell
        # is testing the gate rather than a fixture that can never satisfy it.
        exp = {"renderComposition": {"gating": True, "unevaluable": {"max": 1}}}
        self.assertEqual(
            rc.STATUS_PASS,
            rc.evaluate_render_composition(exp, build(POSITIVE_MANIFEST)).status)

    def test_warp_buckets_are_info_unarmed_and_fail_armed(self):
        declared = {"warpBuckets": ["warp1x", "warpHigh"]}
        res = rc.evaluate_render_composition(
            {"renderComposition": declared}, build(POSITIVE_MANIFEST))
        warp = [f for f in res.findings if f.rule_id == rc.RULE_WARP]
        self.assertEqual(1, len(warp))
        self.assertEqual(rc.LEVEL_INFO, warp[0].level)
        self.assertEqual(rc.STATUS_REPORT, res.status)
        armed = dict(declared, gating=True)
        res = rc.evaluate_render_composition(
            {"renderComposition": armed}, build(POSITIVE_MANIFEST))
        warp = [f for f in res.findings if f.rule_id == rc.RULE_WARP]
        self.assertEqual(rc.LEVEL_FAIL, warp[0].level)
        self.assertEqual(rc.STATUS_FAIL, res.status)
        self.assertIn("warpHigh", warp[0].target)

    def test_require_seam_kinds_is_consumed_by_rc_seam(self):
        exp = {"renderComposition": {"gating": True,
                                     "requireSeamKinds": ["switch-continuation"]}}
        res = rc.evaluate_render_composition(exp, build(POSITIVE_MANIFEST))
        seam = [f for f in res.findings if f.rule_id == rc.RULE_SEAM]
        self.assertEqual(1, len(seam))
        self.assertEqual(rc.LEVEL_FAIL, seam[0].level)
        self.assertEqual(rc.STATUS_FAIL, res.status)
        # The kinds the fixture DOES carry are satisfied silently.
        exp = {"renderComposition": {"gating": True,
                                     "requireSeamKinds": ["rigid", "flexible-soi"],
                                     "dwells": {"min": 1}}}
        res = rc.evaluate_render_composition(exp, build(POSITIVE_MANIFEST))
        self.assertEqual(rc.STATUS_PASS, res.status, res.mismatches)

    def test_result_shape_is_the_saveparse_row_shape_plus_findings(self):
        res = rc.evaluate_render_composition(self.POS, build(POSITIVE_MANIFEST))
        for attr in ("status", "gating", "mismatches", "armed_mismatches",
                     "observed", "blocks", "armed_blocks", "parsed",
                     "parse_error", "findings", "unevaluable"):
            self.assertTrue(hasattr(res, attr), attr)
        self.assertIsInstance(res.findings, tuple)
        self.assertIsInstance(res.mismatches, tuple)


# ---------------------------------------------------------------------------
# Schema v1.1: the clauses the additive keys made evaluable. Each gets BOTH a
# positive cell (the clause runs and agrees) and a violating cell (the clause
# actually reds), because a clause that can only pass is not a check.
# ---------------------------------------------------------------------------

# The cycle-0 descent event's tail, unique in POSITIVE_MANIFEST (detailB 10400 is
# the cycle-0 entry instant and appears nowhere else).
DESCENT_CYCLE0_TAIL = ("detailB = 10400.0\n\t\t\tdetailC = 0.0\n"
                       "\t\t\tdetailS = Descent\n\t\t\tdetailD = 1700.0")

# The two hold records verbatim, so a variant can delete either wholesale. Built by
# concatenation rather than as a triple-quoted block because the writer emits an
# empty `detailS` as "detailS = " WITH a trailing space, which an editor would
# silently strip out of a literal.
def _hold_block(kind, ut, held):
    return ("\t\tCLOCK_EVENT\n\t\t{\n\t\t\tkind = %s\n\t\t\townerIndex = 0\n"
            "\t\t\tcycleIndex = 0\n\t\t\tut = %s\n\t\t\tdetailA = 0.0\n"
            "\t\t\tdetailB = 1150.0\n\t\t\tdetailC = %s\n\t\t\tdetailS = \n\t\t}\n"
            % (kind, ut, held))


HOLD_ENGAGE_BLOCK = _hold_block("hold-engage", "10500.0", "0.0")
HOLD_RELEASE_BLOCK = _hold_block("hold-release", "10600.0", "100.0")

# A second cycle-0 descent event, inserted before the reaim-window record. Its
# trigger / entry / residual all agree with the recomputation, so ONLY the head
# clause can red on it.
def descent_second_sample(ut, head):
    return ("\t\tCLOCK_EVENT\n\t\t{\n\t\t\tkind = descent-phase\n"
            "\t\t\townerIndex = 1\n\t\t\tcycleIndex = 0\n"
            "\t\t\tut = %s\n\t\t\tdetailA = 10700.0\n\t\t\tdetailB = 10400.0\n"
            "\t\t\tdetailC = 0.0\n\t\t\tdetailS = Descent\n\t\t\tdetailD = %s\n"
            "\t\t}\n" % (ut, head))


ANCHOR_REAIM_WINDOW_EVENT = "\t\tCLOCK_EVENT\n\t\t{\n\t\t\tkind = reaim-window\n"


class SchemaV11ClauseTests(unittest.TestCase):
    """`.scout/schema-v1.1-decisions.md` decisions 1-6, from the Python side."""

    # --- decision 2: RC-HOLD leg 2 -----------------------------------------

    def test_hold_leg_2_runs_and_agrees_on_the_positive_fixture(self):
        # W_N recomputes to 100 s for every cycle of unit A (cadence 4000 is a whole
        # multiple of the 500 s align period, so the realignment is a no-op), and
        # the observed release says the render clock stood still for exactly that.
        _s, (findings, unevaluable, metrics) = rules_for(POSITIVE_MANIFEST)
        self.assertEqual([], fails(findings, rc.RULE_HOLD))
        self.assertNotIn(rc.UNEVAL_HOLD_EVIDENCE_ABSENT, unevaluable)
        self.assertNotIn(rc.UNEVAL_HOLD_RELEASE_ABSENT, unevaluable)
        self.assertEqual([100.0], metrics["observedHoldSeconds"])

    def test_hold_leg_2_reds_when_the_render_clock_held_too_long(self):
        _s, (findings, _u, _m) = rules_for(
            variant(("detailC = 100.0", "detailC = 400.0")))
        hold = fails(findings, rc.RULE_HOLD)
        self.assertEqual(1, len(hold))
        self.assertIn("leg 2 (render drift)", hold[0].message)
        self.assertIn("400", hold[0].message)

    def test_hold_leg_2_tolerates_one_frame_step_either_side(self):
        # Tolerance is max(2 s, 2 x the local maxUtStep = 5 s) = 10 s, which is the
        # recorder's own bracketing accuracy - inside it is NOT a finding.
        _s, (inside, _u, _m) = rules_for(
            variant(("detailC = 100.0", "detailC = 109.0")))
        self.assertEqual([], fails(inside, rc.RULE_HOLD))
        _s, (outside, _u, _m) = rules_for(
            variant(("detailC = 100.0", "detailC = 111.0")))
        self.assertEqual(1, len(fails(outside, rc.RULE_HOLD)))

    def test_a_hold_shorter_than_the_warp_step_is_unevaluable_not_a_mismatch(self):
        # Engage with no release: the run was still open at export or its release
        # frame was warped over. Below-resolution is neither a pass nor a red.
        text = POSITIVE_MANIFEST.replace(HOLD_RELEASE_BLOCK, "", 1)
        self.assertNotEqual(text, POSITIVE_MANIFEST)
        _s, (findings, unevaluable, _m) = rules_for(text)
        self.assertEqual([], fails(findings, rc.RULE_HOLD))
        self.assertEqual(1, unevaluable[rc.UNEVAL_HOLD_RELEASE_ABSENT])

    def test_a_release_with_no_engage_is_a_lost_half_of_the_pair(self):
        text = POSITIVE_MANIFEST.replace(HOLD_ENGAGE_BLOCK, "", 1)
        self.assertNotEqual(text, POSITIVE_MANIFEST)
        _s, (findings, _u, _m) = rules_for(text)
        hold = fails(findings, rc.RULE_HOLD)
        self.assertEqual(1, len(hold))
        self.assertIn("no hold-engage", hold[0].message)

    def test_the_release_detail_a_convention_is_pinned(self):
        # detailA on a hold event is the run's 0-BASED WHOLE ORDINAL within its
        # (ownerIndex, cycleIndex). A NON-ORDINAL there is the shape that breaks
        # pairing, so that is what the clause refuses: a fractional or negative
        # value cannot name a run.
        for spelling in ("-1.0", "0.5"):
            with self.subTest(detail_a=spelling):
                _s, (findings, _u, _m) = rules_for(
                    variant(("detailA = 0.0\n\t\t\tdetailB = 1150.0\n"
                             "\t\t\tdetailC = 100.0",
                             "detailA = %s\n\t\t\tdetailB = 1150.0\n"
                             "\t\t\tdetailC = 100.0" % spelling)))
                unknown = fails(findings, rc.RULE_UNKNOWN)
                self.assertEqual(1, len(unknown))
                self.assertIn("detailA", unknown[0].message)
                self.assertIn("run ordinal", unknown[0].message)

    def test_a_second_ordinal_in_the_same_cycle_is_a_legal_pair_not_a_defect(self):
        # THE BREAKING CHANGE: a cycle that stalls twice emits ordinal 0 and
        # ordinal 1. Pairing on (owner, cycle) alone would match run 1's release
        # to run 0's engage; the ordinal is what separates them.
        second = (_hold_block("hold-engage", "10800.0", "0.0")
                  .replace("detailA = 0.0", "detailA = 1.0")
                  + _hold_block("hold-release", "10900.0", "100.0")
                  .replace("detailA = 0.0", "detailA = 1.0"))
        text = POSITIVE_MANIFEST.replace(HOLD_RELEASE_BLOCK,
                                         HOLD_RELEASE_BLOCK + second, 1)
        self.assertNotEqual(text, POSITIVE_MANIFEST)
        snap, (findings, _u, _m) = rules_for(text)
        self.assertEqual(2, len([e for e in snap.clock_events
                                 if e.kind == rc.CLOCK_HOLD_RELEASE]))
        self.assertEqual([], fails(findings, rc.RULE_HOLD))
        self.assertEqual([], fails(findings, rc.RULE_UNKNOWN))

    def test_a_second_release_whose_engage_ordinal_is_missing_reds(self):
        # Only the RELEASE half of run 1 is present. Under the old cycle-only
        # pairing it would have silently matched run 0's engage and passed.
        orphan = (_hold_block("hold-release", "10900.0", "100.0")
                  .replace("detailA = 0.0", "detailA = 1.0"))
        text = POSITIVE_MANIFEST.replace(HOLD_RELEASE_BLOCK,
                                         HOLD_RELEASE_BLOCK + orphan, 1)
        _s, (findings, _u, _m) = rules_for(text)
        hold = fails(findings, rc.RULE_HOLD)
        self.assertEqual(1, len(hold))
        self.assertIn("run ordinal", hold[0].message)
        self.assertIn("run=1", hold[0].target)

    # --- decision 3: RC-DESCENT head ---------------------------------------

    def test_the_descent_head_clauses_run_on_the_positive_fixture(self):
        _s, (findings, unevaluable, _m) = rules_for(POSITIVE_MANIFEST)
        self.assertEqual([], fails(findings, rc.RULE_DESCENT))
        self.assertNotIn(rc.UNEVAL_DESCENT_HEAD_ABSENT, unevaluable)

    def test_a_descent_head_before_the_recorded_deorbit_reds(self):
        _s, (findings, _u, _m) = rules_for(variant(
            (DESCENT_CYCLE0_TAIL, DESCENT_CYCLE0_TAIL.replace("1700.0", "1600.0"))))
        descent = fails(findings, rc.RULE_DESCENT)
        self.assertEqual(1, len(descent))
        self.assertIn("BEFORE the recording's own deorbit", descent[0].message)

    def test_a_descent_head_that_goes_backwards_inside_one_cycle_reds(self):
        text = variant(
            (DESCENT_CYCLE0_TAIL, DESCENT_CYCLE0_TAIL.replace("1700.0", "1800.0")),
            (ANCHOR_REAIM_WINDOW_EVENT,
             descent_second_sample("10900.0", "1750.0") + ANCHOR_REAIM_WINDOW_EVENT))
        _s, (findings, _u, _m) = rules_for(text)
        descent = fails(findings, rc.RULE_DESCENT)
        self.assertEqual(1, len(descent))
        self.assertIn("BACKWARDS inside one cycle", descent[0].message)

    def test_a_forward_head_across_two_samples_of_one_cycle_does_not_red(self):
        # The mirror of the cell above: same shape, head moving forward. Without
        # this the backwards cell could be passing for the wrong reason.
        text = variant(
            (ANCHOR_REAIM_WINDOW_EVENT,
             descent_second_sample("10900.0", "1900.0") + ANCHOR_REAIM_WINDOW_EVENT))
        _s, (findings, _u, _m) = rules_for(text)
        self.assertEqual([], fails(findings, rc.RULE_DESCENT))

    def test_a_head_that_re_anchors_each_cycle_is_not_a_backwards_head(self):
        # The positive fixture's two events are cycles 0 and 1 and BOTH carry head
        # 1700: every cycle re-anchors the clip at the recorded deorbit instant. A
        # monotonicity check across cycles would red the loop working correctly.
        s = build(POSITIVE_MANIFEST)
        heads = [(e.cycle_index, e.detail_d) for e in s.clock_events
                 if e.kind == "descent-phase"]
        self.assertEqual([(0, 1700.0), (1, 1700.0)], heads)
        self.assertEqual([], fails(rc.evaluate_rules(s, None)[0], rc.RULE_DESCENT))

    # --- decision 4: RC-CUT containment ------------------------------------

    def test_cut_containment_runs_on_the_positive_fixture(self):
        _s, (findings, unevaluable, _m) = rules_for(POSITIVE_MANIFEST)
        self.assertEqual([], fails(findings, rc.RULE_CUT))
        self.assertNotIn(rc.UNEVAL_CUT_CONTAINMENT, unevaluable)

    def test_a_dwell_sample_inside_a_cut_reds(self):
        # Unit A's cut is [1200, 1500); moving a dwell's recorded open instant to
        # 1300 means a frame rendered recording the compressor removed.
        _s, (findings, _u, _m) = rules_for(variant((
            "openUT = 10000.0\n\t\t\tcloseUT = 12000.0\n\t\t\topenLoopUT = 1000.0",
            "openUT = 10000.0\n\t\t\tcloseUT = 12000.0\n\t\t\topenLoopUT = 1300.0")))
        cut = fails(findings, rc.RULE_CUT)
        self.assertEqual(1, len(cut))
        self.assertIn("openLoopUT", cut[0].message)
        self.assertIn("compressed away", cut[0].message)

    def test_a_dwell_straddling_a_cut_is_not_a_violation(self):
        # The positive fixture's recA0 dwells open at 1000 and close at 1600, either
        # side of the [1200, 1500) cut: the compressor is a DISCONTINUITY in the
        # recorded clock, not a break in the dwell key, so one dwell legitimately
        # spans one. Flagging that would red the compressor working as designed.
        s = build(POSITIVE_MANIFEST)
        spanning = [d for d in s.dwells
                    if d.open_loop_ut == 1000.0 and d.close_loop_ut == 1600.0]
        self.assertTrue(spanning)
        self.assertEqual([], fails(rc.evaluate_rules(s, None)[0], rc.RULE_CUT))

    def test_cut_containment_is_unevaluable_without_the_recorded_clock(self):
        # A schema-v1 manifest (or a dwell whose member mapped to no unit) carries
        # no recorded-clock stamps, and the clause must say so rather than pass.
        text = POSITIVE_MANIFEST
        for old in ("\t\t\topenLoopUT = 1000.0\n\t\t\tcloseLoopUT = 1600.0\n",
                    "\t\t\topenLoopUT = 1600.0\n\t\t\tcloseLoopUT = 2000.0\n"):
            text = text.replace(old, "")
        _s, (findings, unevaluable, _m) = rules_for(text)
        self.assertEqual([], fails(findings, rc.RULE_CUT))
        self.assertEqual(1, unevaluable[rc.UNEVAL_CUT_CONTAINMENT])

    # --- decision 5: ownerIndex attribution --------------------------------

    def test_owner_index_attributes_dwells_without_guessing(self):
        s = build(POSITIVE_MANIFEST)
        unit_a, unit_b = s.units
        self.assertEqual({0}, {d.owner_index for d in rc._dwells_for_unit(s, unit_a)})
        self.assertEqual({1}, {d.owner_index for d in rc._dwells_for_unit(s, unit_b)})
        self.assertEqual(4, len(rc._dwells_for_unit(s, unit_a)))

    def test_attribution_falls_back_to_rec_ids_without_an_owner_index(self):
        text = POSITIVE_MANIFEST
        for owner in ("\t\t\townerIndex = 0\n", "\t\t\townerIndex = 1\n"):
            text = text.replace(owner, "")
        s = build(text)
        self.assertEqual({None}, {d.owner_index for d in s.dwells})
        self.assertEqual(4, len(rc._dwells_for_unit(s, s.units[0])))

    # --- decision 1: the exact stock-table row -----------------------------

    def test_leg_1_passes_cleanly_when_the_named_body_agrees(self):
        # The mirror of test_leg_1_uses_the_exact_row_when_a_body_name_is_present:
        # naming the RIGHT body must silence the near-match heuristic entirely, not
        # merely change which row it guessed.
        text = variant(("\t\t\tlaunchHoldEngaged = True",
                        "\t\t\tlaunchBodyName = Kerbin\n\t\t\tlaunchHoldEngaged = True"))
        _s, (findings, unevaluable, _m) = rules_for(text)
        self.assertEqual([], fails(findings, rc.RULE_CONST))
        # Only the (unnamed) destination primitive stays unidentified.
        self.assertEqual(1, unevaluable[rc.UNEVAL_PRIMITIVE_BODY_UNKNOWN])

    def test_a_named_body_the_table_does_not_carry_is_unevaluable(self):
        text = variant(("\t\t\tlaunchHoldEngaged = True",
                        "\t\t\tlaunchBodyName = Laythe\n\t\t\tlaunchHoldEngaged = True"))
        _s, (findings, unevaluable, _m) = rules_for(text)
        self.assertEqual([], fails(findings, rc.RULE_CONST))
        self.assertEqual(2, unevaluable[rc.UNEVAL_PRIMITIVE_BODY_UNKNOWN])


class ShippedSampleSchemaV11Tests(unittest.TestCase):
    """The v1.1 keys as the SHIPPED C# writer actually emits them."""

    def setUp(self):
        import os
        lib_dir = os.path.dirname(os.path.abspath(__file__))
        repo_root = os.path.dirname(os.path.dirname(lib_dir))
        path = os.path.join(repo_root, "Source", "Parsek.Tests", "Fixtures",
                            "RenderManifest", "sample-manifest.txt")
        with open(path, "r", encoding="utf-8") as fh:
            self.snap = rc.parse_render_manifest(fh.read())
        self.assertTrue(self.snap.parsed, self.snap.error)

    def test_the_writer_emits_the_body_names_and_leg_1_uses_the_exact_row(self):
        unit = self.snap.units[0]
        self.assertEqual("Kerbin", unit.launch_body_name)
        self.assertEqual("Duna", unit.destination_body_name)
        findings, unevaluable, _m = rc.evaluate_rules(self.snap, None)
        # Kerbin's row matches exactly; Duna is not in the ratified rotation table,
        # so that primitive is unevaluable rather than red.
        self.assertEqual([], fails(findings, rc.RULE_CONST))
        self.assertEqual(1, unevaluable[rc.UNEVAL_PRIMITIVE_BODY_UNKNOWN])

    def test_the_writer_emits_the_hold_pair_and_leg_2_agrees(self):
        kinds = {e.kind for e in self.snap.clock_events}
        self.assertIn(rc.CLOCK_HOLD_ENGAGE, kinds)
        self.assertIn(rc.CLOCK_HOLD_RELEASE, kinds)
        releases = [e for e in self.snap.clock_events
                    if e.kind == rc.CLOCK_HOLD_RELEASE]
        release = releases[0]
        self.assertEqual(1, release.cycle_index)
        # detailA is the RUN ORDINAL, not a repeat of cycleIndex - the fixture is
        # authored so the two are visibly different numbers (cycle 1, ordinal 0).
        self.assertAlmostEqual(0.0, release.detail_a)
        self.assertEqual(0, rc._hold_run_ordinal(release))
        self.assertAlmostEqual(3600.0, release.detail_c)
        findings, _u, _m = rc.evaluate_rules(self.snap, None)
        self.assertEqual([], fails(findings, rc.RULE_HOLD))

    def test_the_writer_emits_two_runs_in_one_cycle_and_both_pair_up(self):
        # "One pair per (owner, cycle)" is NO LONGER TRUE. Pairing on the cycle
        # alone would match the second release to the first engage; the key is
        # (ownerIndex, cycleIndex, detailA ordinal).
        engages = [e for e in self.snap.clock_events
                   if e.kind == rc.CLOCK_HOLD_ENGAGE]
        releases = [e for e in self.snap.clock_events
                    if e.kind == rc.CLOCK_HOLD_RELEASE]
        self.assertEqual(2, len(engages))
        self.assertEqual(2, len(releases))
        self.assertEqual({(4, 1, 0), (4, 1, 1)},
                         {(e.owner_index, e.cycle_index, rc._hold_run_ordinal(e))
                          for e in engages})
        self.assertEqual({(4, 1, 0), (4, 1, 1)},
                         {(e.owner_index, e.cycle_index, rc._hold_run_ordinal(e))
                          for e in releases})
        # Both runs are on ONE cycle index, so the ordinal is the only thing that
        # separates them.
        self.assertEqual({1}, {e.cycle_index for e in engages + releases})
        findings, _u, _m = rc.evaluate_rules(self.snap, None)
        self.assertEqual([], fails(findings, rc.RULE_HOLD))
        self.assertEqual([], [f for f in fails(findings, rc.RULE_UNKNOWN)
                              if "detailA" in f.message])

    def test_the_writer_emits_detail_d_only_on_the_descent_event(self):
        for e in self.snap.clock_events:
            self.assertEqual(e.kind == "descent-phase", math.isfinite(e.detail_d),
                             e.kind)
        descent = [e for e in self.snap.clock_events
                   if e.kind == "descent-phase"][0]
        self.assertAlmostEqual(7250.0, descent.detail_d)

    def test_the_writer_emits_the_dwell_recorded_clock_and_owner(self):
        d = self.snap.dwells[0]
        self.assertEqual(4, d.owner_index)
        self.assertAlmostEqual(1000.0, d.open_loop_ut)
        self.assertAlmostEqual(1010.0, d.close_loop_ut)

    def test_the_boundary_overlap_secondary_carries_the_pinned_indices(self):
        # cycleIndex is the PRIMARY N, detailA the SECONDARY N+1. The gate RC-CYCLE
        # recomputes lives at N+1, so reading cycleIndex as the secondary would test
        # the wrong window - and on this sample it would red a correct event.
        ev = [e for e in self.snap.clock_events
              if e.kind == "boundary-overlap-secondary"][0]
        self.assertEqual(0, ev.cycle_index)
        self.assertAlmostEqual(1.0, ev.detail_a)
        findings, _u, _m = rc.evaluate_rules(self.snap, None)
        self.assertEqual([], fails(findings, rc.RULE_CYCLE))


# ---------------------------------------------------------------------------
# The review-pass clauses: the TRUNCATED token family, the RC-COVER resolution
# model, the RATIFIED_SKIP hull, and the closed-interval RC-OWN endpoints.
# ---------------------------------------------------------------------------


class TruncationTokenTests(unittest.TestCase):
    """One admission helper now runs every append site, so a section is
    truncated under FOUR spellings. Reading only the plain token would let a
    family-cap or whole-export drop read as a clean pass."""

    def _cover_and_uneval(self, extra_record):
        _s, (findings, unevaluable, _m) = rules_for(
            variant(COVER_GAP, (ANCHOR_ROUTE_LEG_DEFER, extra_record)))
        return findings, unevaluable

    def test_the_plain_per_pid_token_still_stands_down_rc_cover(self):
        findings, _u = self._cover_and_uneval(truncated_record("DWELL", pid=5001,
                                                               kind="dwell"))
        self.assertEqual([], fails(findings, rc.RULE_COVER))

    def test_the_family_cap_global_suffix_stands_down_rc_cover(self):
        # `DWELL` -> `DWELL:global` with pid 0 is what a FAMILY cap drop now
        # writes. Before the suffix was modelled this read as a clean pass.
        findings, unevaluable = self._cover_and_uneval(
            truncated_record("DWELL:global"))
        self.assertEqual([], fails(findings, rc.RULE_COVER))
        self.assertEqual(5, unevaluable["truncated-section-dwell:global"])

    def test_the_whole_export_ceiling_stands_down_every_section(self):
        # ALL:global truncates from some point onward, so NO section is complete.
        findings, _u = self._cover_and_uneval(truncated_record("ALL:global"))
        self.assertEqual([], fails(findings, rc.RULE_COVER))
        for section in ("DWELL", "CLOCK_EVENT", "RATIFIED_SKIP", "TRANSITION"):
            with self.subTest(section=section):
                self.assertTrue(rc._Ctx(
                    rc.parse_render_manifest(fill(variant(
                        (ANCHOR_ROUTE_LEG_DEFER,
                         truncated_record("ALL:global"))))),
                    None).section_truncated(section))

    def test_the_dedupe_exhausted_row_truncates_clock_event_and_only_it(self):
        snap = rc.parse_render_manifest(fill(variant(
            (ANCHOR_ROUTE_LEG_DEFER,
             truncated_record("CLOCK_EVENT:dedupe-exhausted",
                              kind="dedupe-exhausted")))))
        ctx = rc._Ctx(snap, None)
        self.assertTrue(ctx.section_truncated("CLOCK_EVENT"))
        # ...and nothing else. The dedupe table is per-clock-event; reading it as
        # a global stand-down would silence rules it says nothing about.
        self.assertFalse(ctx.section_truncated("DWELL"))
        self.assertFalse(ctx.section_truncated("RATIFIED_SKIP"))

    def test_an_untruncated_manifest_answers_no_for_every_section(self):
        ctx = rc._Ctx(rc.parse_render_manifest(fill(POSITIVE_MANIFEST)), None)
        for section in rc.OBSERVED_SECTIONS:
            with self.subTest(section=section):
                self.assertFalse(ctx.section_truncated(section))


class CoverResolutionModelTests(unittest.TestCase):
    """RC-COVER's dark-window arithmetic: what counts as evidence of the local
    sampling rate, and what happens when there is none."""

    def test_the_positive_fixture_has_no_gap_to_judge(self):
        _s, (findings, unevaluable, _m) = rules_for(POSITIVE_MANIFEST)
        self.assertEqual([], fails(findings, rc.RULE_COVER))
        self.assertNotIn(rc.UNEVAL_COVER_RESOLUTION_ABSENT, unevaluable)

    def test_a_gap_wider_than_the_local_step_reds(self):
        _s, (findings, _u, _m) = rules_for(variant(COVER_GAP))
        cover = fails(findings, rc.RULE_COVER)
        self.assertEqual(1, len(cover))
        self.assertIn("unexplained dark window", cover[0].message)

    def test_no_positive_step_evidence_is_unevaluable_never_a_red(self):
        # THE FLOOR BUG: with every step stamped 0 the old code compared the gap
        # against a resolution of 0.0, so EVERY dark window was "wider than the
        # resolution" and red - turning "the manifest never measured the sampling
        # rate" into a defect report.
        text = fill(variant(COVER_GAP)).replace("maxUtStep = 5.0",
                                                "maxUtStep = 0.0")
        snap = rc.parse_render_manifest(text)
        findings, unevaluable, metrics = rc.evaluate_rules(snap, None)
        self.assertEqual([], fails(findings, rc.RULE_COVER))
        self.assertEqual(1, unevaluable[rc.UNEVAL_COVER_RESOLUTION_ABSENT])
        self.assertEqual(1, metrics["coverResolutionAbsentGaps"])

    def test_the_resolution_helper_refuses_zero_and_nan_as_evidence(self):
        snap = rc.parse_render_manifest(fill(POSITIVE_MANIFEST))
        ctx = rc._Ctx(snap, None)
        unit = snap.units[0]
        covering = [d for d in ctx.dwells_for_unit(unit)]
        self.assertEqual(5.0, rc._cover_resolution(ctx, unit, covering, 11000.0))
        # An in-cycle set with no POSITIVE step falls through to the unit-wide
        # answer; a unit with no positive step anywhere answers None.
        blank = rc.Dwell(pid=9, max_ut_step=0.0, open_ut=0.0, close_ut=1.0)
        self.assertEqual(5.0, rc._cover_resolution(ctx, unit, [blank], 11000.0))
        empty = rc.PlanUnit(host="Flight", owner_index=99)
        self.assertIsNone(rc._cover_resolution(ctx, empty, [blank], 11000.0))


class RatifiedSkipHullTests(unittest.TestCase):
    """A RATIFIED_SKIP is a HULL WITH A COUNT, not a list of intervals."""

    def _run(self, *records):
        extra = "".join(records) if records else ""
        pairs = [COVER_GAP]
        if extra:
            pairs.append((ANCHOR_ROUTE_LEG_DEFER, extra + ANCHOR_ROUTE_LEG_DEFER))
        _s, (findings, unevaluable, _m) = rules_for(variant(*pairs))
        return findings, unevaluable

    def _skip(self, first_ut, last_ut):
        return ratified_skip(5001, first_ut, last_ut)[
            :-len(ANCHOR_ROUTE_LEG_DEFER)]

    def test_a_hull_over_the_gap_is_unevaluable_not_covered_and_not_red(self):
        findings, unevaluable = self._run(self._skip(11200.0, 11800.0))
        self.assertEqual([], fails(findings, rc.RULE_COVER))
        self.assertEqual(1, unevaluable[rc.UNEVAL_COVER_RATIFIED_SKIP_HULL])

    def test_a_singleton_hull_inside_the_gap_still_participates(self):
        # firstUT == lastUT. A strict overlap test says a zero-width hull
        # intersects nothing, which would red the window the skip explains.
        findings, unevaluable = self._run(self._skip(11500.0, 11500.0))
        self.assertEqual([], fails(findings, rc.RULE_COVER))
        self.assertEqual(1, unevaluable[rc.UNEVAL_COVER_RATIFIED_SKIP_HULL])

    def test_a_hull_touching_the_gap_edge_participates(self):
        for first, last in ((9000.0, 11000.0), (12000.0, 13000.0)):
            with self.subTest(hull=(first, last)):
                findings, unevaluable = self._run(self._skip(first, last))
                self.assertEqual([], fails(findings, rc.RULE_COVER))
                self.assertEqual(1,
                                 unevaluable[rc.UNEVAL_COVER_RATIFIED_SKIP_HULL])

    def test_a_hull_elsewhere_leaves_the_gap_red(self):
        findings, unevaluable = self._run(self._skip(30000.0, 31000.0))
        self.assertEqual(1, len(fails(findings, rc.RULE_COVER)))
        self.assertNotIn(rc.UNEVAL_COVER_RATIFIED_SKIP_HULL, unevaluable)

    def test_a_hull_is_never_fed_to_the_coverage_union(self):
        # The regression this guards: feeding the hull into `covered` made a
        # WIDE bracket swallow a dark window it says nothing about. Here the hull
        # spans the whole cycle, and the window is still not COVERED - it is
        # counted as unevaluable, which is a different (and honest) answer.
        findings, unevaluable = self._run(self._skip(10000.0, 14000.0))
        self.assertEqual([], fails(findings, rc.RULE_COVER))
        self.assertEqual(1, unevaluable[rc.UNEVAL_COVER_RATIFIED_SKIP_HULL])
        self.assertNotIn("coverBelowResolutionGaps", unevaluable)

    def test_a_truncated_skip_section_makes_the_gap_unevaluable(self):
        # An unknown number of hulls never reached the manifest, so a window one
        # of them would have explained is indistinguishable from a real gap.
        findings, unevaluable = self._run(
            truncated_record("RATIFIED_SKIP:global")[
                :-len(ANCHOR_ROUTE_LEG_DEFER)])
        self.assertEqual([], fails(findings, rc.RULE_COVER))
        self.assertEqual(1, unevaluable[rc.UNEVAL_COVER_SKIPS_TRUNCATED])


class OwnershipEndpointTests(unittest.TestCase):
    """RC-OWN conservation uses CLOSED-interval overlap in both directions."""

    def test_a_zero_width_ownership_span_is_satisfied_by_a_touching_dwell(self):
        # appear and disappear on ONE frame. Under a strict test the span
        # intersects nothing and the shortest real publish reds as "no draw".
        _s, (findings, _u, _m) = rules_for(variant(
            ("ut = 12000.0\n\t\t\tevent = disappear",
             "ut = 10000.0\n\t\t\tevent = disappear")))
        self.assertEqual([], fails(findings, rc.RULE_OWN))

    def test_a_zero_width_traced_dwell_is_inside_its_published_span(self):
        # The mirror direction: a one-frame TracedPath dwell at the publish
        # instant must not red as "outside every published ownership interval".
        _s, (findings, _u, _m) = rules_for(variant(
            ("openUT = 10000.0\n\t\t\tcloseUT = 12000.0",
             "openUT = 10000.0\n\t\t\tcloseUT = 10000.0")))
        own = [f for f in fails(findings, rc.RULE_OWN)
               if "ownership" in f.message]
        self.assertEqual([], own)

    def test_a_dwell_genuinely_outside_every_span_still_reds(self):
        # The endpoint relaxation must not blunt the rule itself.
        _s, (findings, _u, _m) = rules_for(variant(
            ("openUT = 14000.0\n\t\t\tcloseUT = 16000.0",
             "openUT = 16500.0\n\t\t\tcloseUT = 17000.0")))
        self.assertTrue([f for f in fails(findings, rc.RULE_OWN)
                         if "outside every" in f.message])


class WarpHelperTests(unittest.TestCase):
    """The two shared RC-WARP primitives, so both halves ask one question."""

    def test_warp_totals_sums_every_bucket_over_a_dwell_set(self):
        a = rc.Dwell(warp={"warp1x": 3, "warp100": 1})
        b = rc.Dwell(warp={"warp1x": 2, "warpHigh": 4})
        self.assertEqual({"warp1x": 5, "warpPhys": 0, "warp100": 1,
                          "warp1000": 0, "warpHigh": 4},
                         rc._warp_totals([a, b]))
        self.assertEqual({b: 0 for b in rc.WARP_BUCKETS}, rc._warp_totals([]))

    def test_uts_inside_uses_the_closed_span_and_skips_unusable_dwells(self):
        d = rc.Dwell(open_ut=10.0, close_ut=20.0)
        broken = rc.Dwell(open_ut=float("nan"), close_ut=20.0)
        self.assertEqual(3, rc._uts_inside([d, broken], [10.0, 15.0, 20.0]))
        self.assertEqual(0, rc._uts_inside([d], [9.9, 20.1]))
        self.assertEqual(0, rc._uts_inside([], [15.0]))

    def test_the_dominant_bucket_reads_the_same_totals(self):
        d = rc.Dwell(warp={"warp1x": 1, "warp100": 9})
        self.assertEqual("warp100", rc._dominant_warp_bucket([d]))
        self.assertEqual("", rc._dominant_warp_bucket([rc.Dwell()]))


class SinglePassAndCacheTests(unittest.TestCase):
    """One rule pass per row, and the memo that makes it cheap."""

    def test_the_combined_path_facets_equal_the_standalone_facets(self):
        snap = rc.parse_render_manifest(fill(POSITIVE_MANIFEST))
        standalone = rc.observed_composition_facets(snap)
        for label, expectations in (
                ("no block", {}),
                ("declared, no list key",
                 {"renderComposition": {"dwells": {"min": 1}}}),
                ("armed, no list key",
                 {"renderComposition": {"gating": True, "dwells": {"min": 1}}})):
            with self.subTest(label):
                res = rc.evaluate_render_composition(expectations, snap)
                self.assertEqual(standalone, res.observed)

    def test_a_declared_list_key_moves_only_the_findings_census(self):
        # The ONE documented difference: RC-WARP / RC-SEAM raise their list-key
        # rows only when the spec declared them, so the census counts a row the
        # row genuinely produced. Every other facet is block-independent.
        snap = rc.parse_render_manifest(fill(POSITIVE_MANIFEST))
        standalone = rc.observed_composition_facets(snap)
        res = rc.evaluate_render_composition(
            {"renderComposition": {"warpBuckets": ["warpHigh"]}}, snap)
        mine = standalone[rc.RENDER_COMPOSITION_BLOCK]
        theirs = res.observed[rc.RENDER_COMPOSITION_BLOCK]
        self.assertEqual(sorted(mine), sorted(theirs))
        differing = {k for k in mine if mine[k] != theirs[k]}
        self.assertEqual({"findings"}, differing)

    def test_the_dwell_memo_returns_the_same_answer_as_the_pure_function(self):
        snap = rc.parse_render_manifest(fill(POSITIVE_MANIFEST))
        ctx = rc._Ctx(snap, None)
        for unit in snap.units:
            with self.subTest(owner=unit.owner_index):
                self.assertEqual(rc._dwells_for_unit(snap, unit),
                                 ctx.dwells_for_unit(unit))
                self.assertIs(ctx.dwells_for_unit(unit),
                              ctx.dwells_for_unit(unit))
                self.assertEqual(rc._cycle_windows(snap, unit.owner_index),
                                 ctx.cycle_windows(unit.owner_index))

    def test_units_sharing_an_owner_index_do_not_share_a_memo_entry(self):
        # The memo is keyed on the unit OBJECT, not the owner index: the index is
        # not unique across hosts (Flight owner 0 and TrackingStation owner 0 are
        # different units), and attribution falls back to the unit's own MEMBER
        # set for any dwell with no ownerIndex - so an index key would hand one
        # unit the other's member-matched dwells.
        snap = rc.parse_render_manifest(fill(POSITIVE_MANIFEST))
        ctx = rc._Ctx(snap, None)
        # Owner-less units: attribution is by MEMBER set alone, so an index key
        # (both `None` here) would hand the second unit the first's dwells.
        a = rc.PlanUnit(host="Flight",
                        members=(rc.PlanMember(index=0, rec_id="recA0"),))
        b = rc.PlanUnit(host="TrackingStation",
                        members=(rc.PlanMember(index=2, rec_id="recB0"),))
        self.assertEqual({"recA0"}, {d.rec_id for d in ctx.dwells_for_unit(a)})
        self.assertEqual({"recB0"}, {d.rec_id for d in ctx.dwells_for_unit(b)})
        # ...and two units that are EQUAL by value still get their own entries,
        # which is what makes the key safe when an owner index repeats across
        # hosts and a dwell carrying no ownerIndex falls through to the member
        # set.
        c = rc.PlanUnit(host="Flight", owner_index=7,
                        members=(rc.PlanMember(index=0, rec_id="recA0"),))
        d = rc.PlanUnit(host="Flight", owner_index=7,
                        members=(rc.PlanMember(index=0, rec_id="recA0"),))
        ctx.dwells_for_unit(c)
        ctx.dwells_for_unit(d)
        self.assertIn(id(c), ctx._dwell_cache)
        self.assertIn(id(d), ctx._dwell_cache)


class UnknownTokenHelperTests(unittest.TestCase):
    """The one helper every unknown-token site routes through."""

    def _ctx(self):
        return rc._Ctx(rc.parse_render_manifest(fill(POSITIVE_MANIFEST)), None)

    def test_a_blank_token_is_absent_not_unknown(self):
        ctx = self._ctx()
        for blank in ("", None, "   "):
            with self.subTest(blank=blank):
                self.assertFalse(ctx.unknown_token("t", "thing", blank, ("a",)))
        self.assertEqual([], ctx.findings)

    def test_a_known_token_raises_nothing_and_reports_false(self):
        ctx = self._ctx()
        self.assertFalse(ctx.unknown_token("t", "thing", "a", ("a", "b")))
        self.assertEqual([], ctx.findings)

    def test_an_unknown_token_raises_one_citable_rc_unknown_fail(self):
        ctx = self._ctx()
        self.assertTrue(ctx.unknown_token("t", "thing", "z", ("a", "b"), "note"))
        self.assertEqual(1, len(ctx.findings))
        f = ctx.findings[0]
        self.assertEqual(rc.RULE_UNKNOWN, f.rule_id)
        self.assertEqual(rc.LEVEL_FAIL, f.level)
        self.assertIn("unknown thing 'z'", f.message)
        self.assertIn("['a', 'b']", f.message)
        self.assertIn("note", f.message)
        self.assertTrue(f.cited_contract)

    def test_every_converted_site_still_reds_on_its_own_token(self):
        # One mutation per converted vocabulary. The point of the sweep is that
        # the shared helper did not quietly drop a site.
        cases = {
            "seam kind": (("kind = rigid", "kind = wobbly"), "seam kind"),
            "chain phase kind": (("kind = ascent\n\t\t\t\tprovenance = recorded",
                                  "kind = mystery\n\t\t\t\tprovenance = recorded"),
                                 "phase kind"),
            "chain provenance": (("provenance = spine", "provenance = telepathy"),
                                 "chain provenance"),
            "seamSource": (("seamSource = assembler", "seamSource = vibes"),
                           "seamSource"),
            "treatment": (("treatment = TracedPath", "treatment = Sketch"),
                          "treatment"),
            "coverage": (("coverage = InSegment\n\t\t\tframeBody = Kerbin\n"
                          "\t\t\townerIndex = 0\n\t\t\topenUT = 10000.0",
                          "coverage = Elsewhere\n\t\t\tframeBody = Kerbin\n"
                          "\t\t\townerIndex = 0\n\t\t\topenUT = 10000.0"),
                         "coverage"),
            "dwell phase kind": (("phaseKind = soi-arrival", "phaseKind = wat"),
                                 "phase kind"),
            "line-branch coverage": (("coverage = Inside", "coverage = Sideways"),
                                     "render-window coverage"),
            "ownership event": (("event = appear\n\t\t}\n\t\tOWNERSHIP_CHANGE",
                                 "event = materialise\n\t\t}\n\t\tOWNERSHIP_CHANGE"),
                                "ownership event"),
            "route scope": (("scope = SameBody", "scope = Whatever"),
                            "route scope"),
            "descent phase token": (("detailS = Descent\n\t\t\tdetailD = 1700.0\n"
                                     "\t\t}\n\t\tCLOCK_EVENT\n\t\t{\n"
                                     "\t\t\tkind = hold-engage",
                                     "detailS = Falling\n\t\t\tdetailD = 1700.0\n"
                                     "\t\t}\n\t\tCLOCK_EVENT\n\t\t{\n"
                                     "\t\t\tkind = hold-engage"),
                                    "descent phase token"),
            "host": (("host = Flight", "host = Basement"), "host"),
            "exportReason": (("exportReason = verb", "exportReason = whim"),
                             "exportReason"),
        }
        for label, ((old, new), needle) in cases.items():
            with self.subTest(label):
                # FIRST occurrence, not `variant`'s unique-anchor rule: several
                # of these tokens legitimately repeat across the fixture's two
                # units, and one mutated record is enough to exercise the site.
                self.assertIn(old, POSITIVE_MANIFEST, label)
                text = POSITIVE_MANIFEST.replace(old, new, 1)
                _s, (findings, _u, _m) = rules_for(text)
                unknown = fails(findings, rc.RULE_UNKNOWN)
                self.assertTrue(
                    any(needle in f.message for f in unknown),
                    "%s: %r" % (label, [f.message for f in unknown]))


class HoldPrimitiveLedgerTests(unittest.TestCase):

    def test_a_non_finite_leg_1_recomputation_is_counted_not_skipped(self):
        # The uncounted-skip bug: leg 1 `continue`d on a non-finite recomputation
        # with no ledger entry, so a unit whose primitives could not answer the
        # clause read as a silent pass. Leg 2 always counted; now both do.
        _s, (findings, unevaluable, _m) = rules_for(
            variant(("cadenceSeconds = 4000.0\n\t\t\toverlapCadenceSeconds = 4000.0",
                     "cadenceSeconds = NaN\n\t\t\toverlapCadenceSeconds = 4000.0")))
        self.assertEqual([], fails(findings, rc.RULE_HOLD))
        # Two cycle windows through leg 1 + the one release through leg 2.
        self.assertEqual(3, unevaluable[rc.UNEVAL_HOLD_PRIMITIVES_ABSENT])



class PendingReconciliationTests(unittest.TestCase):

    @unittest.skip("reconciled in wave 3 against "
                   "Source/Parsek.Tests/Fixtures/RenderManifest/sample-manifest.txt")
    def test_csharp_writer_fixture_reconciliation_pending(self):
        """The SHAPE reconciliation against the shipped writer's sample already
        runs (see CSharpWriterFixtureTests, which reads the real file). What is
        still pending, and what this placeholder holds open, is the wave-2/3
        work the shipped notes name:

        - the GLOBAL record cap plus its TRUNCATED marker (per-pid caps do not
          bound totals: 20 pids x 512 dwells is ~14 MB worst case), after which
          this module's ``truncated`` handling needs a global-section row;
        - the ``ExportRenderManifest`` verb, the in-game well-formedness cell,
          and the ``run.py`` row wiring, none of which exist yet to test against;
        - a manifest produced by an actual FLIGHT rather than the hand-built
          sample, which is the only thing that can confirm the CLOCK_EVENT
          detail-field conventions end to end.
        """
        raise AssertionError("unreachable: this cell is skipped by design")
