"""Warp audit over a mission stdout log (operator PR gate, no-1x-coast).

Builds a table of contiguous warp segments per phase (mode + rate bucket,
estimated wall seconds + game seconds) from the rate-limited telemetry lines,
plus a 1X-COAST VIOLATIONS section listing every 1x segment inside the
coast-class phases (COAST-TO-TARGET, TARGET-FLYBY, PLAN-TRANSFER,
PLAN-CORRECTION) longer than the threshold, with its surrounding events.

This is the PR evidence for the operator gate "we cannot have tests that run
at 1x during coast": the pre-fix 2026-07-22_1210 flight shows its known
violations (the finding-14 PLAN-CORRECTION disqualification loop, ~280 wall
seconds at 1x); a post-fix flight must show none.

Usage:
    python warp_audit.py <mission_stdout_log> [--min-wall 30]
                         [--fail-on-violation]

Estimation contract (documented, deliberately simple):
  - The telemetry line is rate-limited to ~1 Hz of WALL time, so a segment's
    wall estimate is line-count x 1.0 s (TELEMETRY_INTERVAL_SECONDS). Wall
    time spent inside blocking RPCs emits no lines and is therefore NOT
    attributed -- the audit measures the poll-driven profile.
  - Game seconds prefer the ut= span when the log carries it (post-Phase-2
    telemetry lines); older logs fall back to wall x bucket rate.

Pure parsers (parse_log_lines / build_segments / find_violations) are
unit-tested in harness/lib/test_warp_audit.py against the committed real
1210 flight log. Stdlib only; ASCII only.
"""

from __future__ import annotations

import argparse
import math
import re
import sys
from dataclasses import dataclass, field
from typing import List, Optional, Tuple

# ~1 Hz: the fly loop polls at 0.5 s and the telemetry line is rate-limited
# to one per second of wall time (mission_runner MissionLogger).
TELEMETRY_INTERVAL_SECONDS = 1.0

# Coast-class phases the no-1x invariant covers. Burn phases (executor
# autowarp, DIY burner flips at 1x/2x physics) and the ascent are exempt by
# design: 1x there is the burn/flip contract, not wasted coast.
VIOLATION_PHASES = (
    "COAST-TO-TARGET",
    "TARGET-FLYBY",
    "PLAN-TRANSFER",
    "PLAN-CORRECTION",
)

# A "1x" sample: NONE mode, or any mode whose reported rate is ~1.
ONE_X_RATE_MAX = 1.05

# Canonical rate buckets: stock rails rates + physics warp rates. A sampled
# rate maps to the nearest canonical value by log-ratio so ramp frames join
# their target bucket instead of fragmenting the table.
CANONICAL_RATES = (1.0, 2.0, 3.0, 4.0, 5.0, 10.0, 50.0, 100.0,
                   1000.0, 10000.0, 100000.0)

_LINE_RE = re.compile(r"^\[Mission\]\[(?P<level>[^\]]+)\]\[(?P<tag>[^\]]+)\]\s*(?P<msg>.*)$")
_WARP_RE = re.compile(r"warp=(?P<mode>[A-Z]+)x(?P<rate>[-+0-9.naife]+)")
_UT_RE = re.compile(r"\but=(?P<ut>[-+0-9.naife]+)")

# Event lines worth surfacing as violation context.
_EVENT_PREFIXES = ("phase ", "action ", "gate ")


@dataclass
class Sample:
    """One parsed telemetry sample (a rate-limited 'telemetry ...' line)."""
    line_no: int
    phase: str
    mode: str
    rate: float
    ut: float  # NaN when the log predates the ut= field


@dataclass
class Event:
    """One context-worthy non-telemetry line (phase/action/gate/Warn)."""
    line_no: int
    phase: str
    text: str


@dataclass
class Segment:
    """A contiguous run of telemetry samples with one (phase, bucket)."""
    phase: str
    mode: str
    bucket: float
    start_line: int
    end_line: int
    samples: int
    start_ut: float
    end_ut: float

    @property
    def wall_est(self) -> float:
        return self.samples * TELEMETRY_INTERVAL_SECONDS

    @property
    def game_est(self) -> float:
        if math.isfinite(self.start_ut) and math.isfinite(self.end_ut) \
                and self.end_ut > self.start_ut:
            return self.end_ut - self.start_ut
        return self.wall_est * self.bucket

    @property
    def label(self) -> str:
        if self.mode == "NONE" or self.bucket <= ONE_X_RATE_MAX:
            return "1x"
        return "%sx%d" % (self.mode, int(self.bucket))


@dataclass
class Violation:
    segment: Segment
    before: Optional[Event]
    after: Optional[Event]


def _to_float(text: str) -> float:
    try:
        return float(text)
    except (TypeError, ValueError):
        return float("nan")


def bucket_rate(rate: float) -> float:
    """Nearest canonical warp rate by log-ratio (ramp frames join their
    target bucket). Non-finite / non-positive rates bucket to 1."""
    if not math.isfinite(rate) or rate <= 0:
        return 1.0
    best, best_err = 1.0, float("inf")
    for canon in CANONICAL_RATES:
        err = abs(math.log(rate / canon))
        if err < best_err:
            best, best_err = canon, err
    return best


def parse_log_lines(lines) -> Tuple[List[Sample], List[Event]]:
    """Parse a mission stdout log into telemetry samples + context events.
    Unknown / malformed lines are skipped (the log may interleave harness
    noise); the parsers never raise on content."""
    samples: List[Sample] = []
    events: List[Event] = []
    for i, raw in enumerate(lines, start=1):
        m = _LINE_RE.match(raw.strip())
        if not m:
            continue
        level, tag, msg = m.group("level"), m.group("tag"), m.group("msg")
        if msg.startswith("telemetry "):
            w = _WARP_RE.search(msg)
            if not w:
                continue
            u = _UT_RE.search(msg)
            samples.append(Sample(
                line_no=i, phase=tag,
                mode=w.group("mode"),
                rate=_to_float(w.group("rate")),
                ut=_to_float(u.group("ut")) if u else float("nan")))
        elif level == "Warn" or msg.startswith(_EVENT_PREFIXES):
            events.append(Event(line_no=i, phase=tag, text=msg[:160]))
    return samples, events


def build_segments(samples: List[Sample]) -> List[Segment]:
    """Fold consecutive samples with the same (phase, mode, rate bucket)
    into contiguous segments."""
    segments: List[Segment] = []
    cur: Optional[Segment] = None
    for s in samples:
        bucket = bucket_rate(s.rate)
        mode = s.mode if bucket > ONE_X_RATE_MAX else "NONE"
        if (cur is not None and cur.phase == s.phase and cur.mode == mode
                and cur.bucket == bucket):
            cur.end_line = s.line_no
            cur.samples += 1
            if math.isfinite(s.ut):
                cur.end_ut = s.ut
                if not math.isfinite(cur.start_ut):
                    cur.start_ut = s.ut
        else:
            cur = Segment(phase=s.phase, mode=mode, bucket=bucket,
                          start_line=s.line_no, end_line=s.line_no,
                          samples=1, start_ut=s.ut, end_ut=s.ut)
            segments.append(cur)
    return segments


def _is_one_x(seg: Segment) -> bool:
    return seg.mode == "NONE" or seg.bucket <= ONE_X_RATE_MAX


def _one_x_duration(seg: Segment) -> float:
    """Best duration estimate for a 1x segment (delta-review D2): the wall
    estimate (samples x 1 s) systematically UNDERCOUNTS when polls run
    slower than 1 Hz (RPC latency, blocking perform calls), while at 1x the
    game-time span equals real wall time -- so take the larger of the two.
    A non-finite game estimate falls back to the wall estimate."""
    game = seg.game_est
    if not math.isfinite(game):
        return seg.wall_est
    return max(seg.wall_est, game)


def cumulative_one_x(segments: List[Segment],
                     min_wall_seconds: float = 30.0
                     ) -> List[Tuple[str, float, int]]:
    """Delta-review D1: per-phase TOTAL 1x time across ALL segments in the
    coast-class phases. A fragmented 1x profile (1x/warp sawtooth, a ramp
    sample splitting a block, a phase-boundary split) defeats the contiguous
    per-segment threshold while the coast still spends most of its wall time
    at 1x -- the CUMULATIVE total is gated on the same threshold,
    independent of contiguity. Returns (phase, total_seconds,
    segment_count) for each phase at/over the threshold."""
    totals: dict = {}
    counts: dict = {}
    for seg in segments:
        if seg.phase not in VIOLATION_PHASES or not _is_one_x(seg):
            continue
        totals[seg.phase] = totals.get(seg.phase, 0.0) + _one_x_duration(seg)
        counts[seg.phase] = counts.get(seg.phase, 0) + 1
    return [(phase, totals[phase], counts[phase])
            for phase in VIOLATION_PHASES
            if totals.get(phase, 0.0) >= min_wall_seconds]


def find_violations(segments: List[Segment], events: List[Event],
                    min_wall_seconds: float = 30.0) -> List[Violation]:
    """Every 1x segment inside a coast-class phase whose estimated duration
    (max of wall and game estimates, delta-review D2) is at least
    ``min_wall_seconds``, with the nearest surrounding context events."""
    out: List[Violation] = []
    for seg in segments:
        if seg.phase not in VIOLATION_PHASES:
            continue
        if not _is_one_x(seg):
            continue
        if _one_x_duration(seg) < min_wall_seconds:
            continue
        before = None
        after = None
        for ev in events:
            if ev.line_no < seg.start_line:
                before = ev
            elif ev.line_no > seg.end_line and after is None:
                after = ev
                break
        out.append(Violation(segment=seg, before=before, after=after))
    return out


def _fmt_secs(v: float) -> str:
    if not math.isfinite(v):
        return "?"
    if v >= 1000:
        return "%.0f" % v
    return "%.1f" % v


def render_report(segments: List[Segment], violations: List[Violation],
                  min_wall_seconds: float, source: str,
                  cumulative: Optional[List[Tuple[str, float, int]]] = None
                  ) -> str:
    lines: List[str] = []
    lines.append("WARP AUDIT  %s" % source)
    lines.append("")
    lines.append("%-18s %-12s %8s %10s %12s  %s"
                 % ("PHASE", "WARP", "SAMPLES", "WALL(s)", "GAME(s)", "LINES"))
    for seg in segments:
        lines.append("%-18s %-12s %8d %10s %12s  %d-%d"
                     % (seg.phase, seg.label, seg.samples,
                        _fmt_secs(seg.wall_est), _fmt_secs(seg.game_est),
                        seg.start_line, seg.end_line))
    lines.append("")
    lines.append("1X-COAST VIOLATIONS (coast-class phases, 1x >= %ds wall):"
                 % int(min_wall_seconds))
    if not violations:
        lines.append("  NONE - coast profile is warped end to end.")
    for v in violations:
        seg = v.segment
        lines.append("  VIOLATION %-18s lines %d-%d  wall ~%ss  game ~%ss"
                     % (seg.phase, seg.start_line, seg.end_line,
                        _fmt_secs(seg.wall_est), _fmt_secs(seg.game_est)))
        if v.before is not None:
            lines.append("    before: L%d [%s] %s"
                         % (v.before.line_no, v.before.phase, v.before.text))
        if v.after is not None:
            lines.append("    after:  L%d [%s] %s"
                         % (v.after.line_no, v.after.phase, v.after.text))
    lines.append("")
    lines.append("CUMULATIVE 1X (per coast phase, ALL 1x segments summed -- "
                 "fragmentation cannot dodge the gate):")
    if not cumulative:
        lines.append("  NONE - no coast phase totals %ds of 1x."
                     % int(min_wall_seconds))
    for phase, total, count in (cumulative or []):
        lines.append("  CUMULATIVE-VIOLATION %-18s total ~%ss across %d "
                     "1x segment(s)" % (phase, _fmt_secs(total), count))
    return "\n".join(lines)


def audit_log_text(text: str, min_wall_seconds: float = 30.0,
                   source: str = "<log>"
                   ) -> Tuple[str, List[Violation], List[Tuple[str, float, int]]]:
    samples, events = parse_log_lines(text.splitlines())
    segments = build_segments(samples)
    violations = find_violations(segments, events, min_wall_seconds)
    cumulative = cumulative_one_x(segments, min_wall_seconds)
    report = render_report(segments, violations, min_wall_seconds, source,
                           cumulative)
    return report, violations, cumulative


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        description="Warp audit over a mission stdout log (no-1x-coast PR gate)")
    parser.add_argument("log", help="mission stdout log path")
    parser.add_argument("--min-wall", type=float, default=30.0,
                        help="1x violation threshold in wall seconds (default 30)")
    parser.add_argument("--fail-on-violation", action="store_true",
                        help="exit 1 when any 1x coast violation is found")
    args = parser.parse_args(argv)
    with open(args.log, "r", encoding="utf-8", errors="replace") as f:
        text = f.read()
    report, violations, cumulative = audit_log_text(text, args.min_wall,
                                                    args.log)
    print(report)
    if args.fail_on_violation and (violations or cumulative):
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
