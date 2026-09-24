"""Pure core of the report-only mutation checker (trust risk 8, phase 1).

The question this answers: does a committed spec's gate actually BITE? Every
"this cell reds when X breaks" claim in the docs was produced by hand; nothing
re-checks it, so a refactor that makes a gate vacuous reds nothing. This module
replays the harness's own PURE gating evaluators over an archived run, first
unmutated (the BASELINE, which must be green or the lane is skipped), then over
deliberately broken copies of the archived artifacts. A mutation the evaluator
reds is KILLED; one it still passes SURVIVED and is listed for triage. Survivors
never fail anything: this is an operator tool over local archives (operator
ruling 2026-09-24), and its output is a report.

Evaluators replayed, each verified pure over an archived artifact:

- ``hlib.evaluate_expectations`` (the logContracts + recordings.count row): pure
  over (expectations, recording count, KSP.log text). Mutated by deleting the
  lines a required pattern matched, perturbing the numbers inside its matched
  spans, dropping one link of an ordered chain, and isolating its matches to a
  single run phase.
- ``hlib.scan_unity_exception_stacks`` + ``hlib.evaluate_unity_exceptions``:
  pure over the KSP.log text. Mutated by injecting a Parsek throw-site exception
  and a stock throw with a Parsek caller.
- ``hlib.grep_anomaly_tokens`` / ``count_anomaly_tokens`` +
  ``evaluate_anomaly_sweep``: pure over the KSP.log text. Mutated by injecting
  one raise of each gated token.
- ``saveparse`` window checks (``_check_window``, the evaluator's own window
  rule) over the facets of the archived save: each ARMED window's measured value
  is perturbed by one either side and to zero.

NOT replayed (not pure over an archive, or phase 2): the ledger oracle (needs the
seed capture), the mission verdict, the response-stream driver validity, the
log validator (a C# subprocess) and the render-composition row.

Pure: no file I/O, no clock. ``harness/tools/mutation_check.py`` is the shell.
"""

from __future__ import annotations

import bisect
import re
from dataclasses import dataclass, field
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

import hlib
import saveparse

KILLED = "KILLED"
SURVIVED = "SURVIVED"

# Survivor classes. TRIAGE needs an operator reading; INTENDED is a survivor the
# spec (or a recorded ruling) deliberately tolerates; INFO is a free field the
# gate was never meant to pin (a UT, a pid, a window's width).
TRIAGE = "triage"
INTENDED = "intended"
INFO = "info"

BASELINE_GREEN = "PASS"
BASELINE_NOT_GREEN = "NOT-GREEN"

PHASE_BOOT = "boot"
PHASE_RUN = "run"
PHASE_TEARDOWN = "teardown"

# The seam's per-command start line (TestCommandDiagnostics.cs:
# `exec id={id} cmd={verb} start`, tag TestCommands). The first one opens the RUN
# phase: everything before it is the KSP boot and main menu, which no scenario
# step caused.
EXEC_START_RE = re.compile(r"\[Parsek\]\[[A-Z]+\]\[TestCommands\] exec id=(\S+) cmd=(\S+) start\b")

# Numbers a mutation may perturb: a decimal integer or fixed-point literal that is
# not part of an identifier, a hex run or a dotted version.
NUMBER_TOKEN_RE = re.compile(r"(?<![\w.])\d+(?:\.\d+)?(?!\w|\.\d)")
_LABEL_KV_RE = re.compile(r"([A-Za-z_][\w.]*)\s*[=:]\s*[(\['\"]?$")
_LABEL_WORD_RE = re.compile(r"([A-Za-z_]\w*)\W*$")

# Label shapes used only to CLASSIFY a numeric survivor, never to decide a kill.
_FAILURE_LABEL_RE = re.compile(
    r"(fail|error|skip|mismatch|reject|drift|drop|lost|orphan|unparsed|missing|"
    r"invalid|violation|exception|warn|stale|leak|refus)", re.IGNORECASE)
_COUNT_LABEL_RE = re.compile(
    r"(count|total|passed|points?$|recordings?$|rows$|segments?$|sections?$|trees$|"
    r"cycles$|hits$|entries$|committed|spawned|kept|reservations|remain)",
    re.IGNORECASE)

MAX_MATCHES_PER_PATTERN = 20000
MAX_INSTANCES_PER_PATTERN = 30
MAX_NUMERIC_KEYS_PER_PATTERN = 40
MAX_SPAN_LINES_FOR_NUMERIC = 3
MUTATION_CHECK_TAG = "[MutationCheck]"
CAPPED_NOTE = ("the pattern re-formed more than MAX_INSTANCES_PER_PATTERN times; the "
               "deletion stopped, so the survival proves nothing")
LOCAL_FIXTURE_NOTE = ("an operator-local fixture lane asserts that a window drew, never "
                      "what it drew (harness/fixtures/local-saves/README.md)")


# ---------------------------------------------------------------------------
# Pattern structure: ordered-chain decomposition.
# ---------------------------------------------------------------------------

_GLOBAL_FLAG_GROUP_RE = re.compile(r"\(\?([aiLmsux]+)\)")
_WILDCARD_ATOMS = frozenset({
    r"[\s\S]", r"[\S\s]", r"[\w\W]", r"[\W\w]", r"[\d\D]", r"[\D\d]",
    r"(?:.|\n)", r"(?:.|\r?\n)", r"(?:\r?\n)", r"(?:\n)", r"(?:\r\n)",
})
_UNBOUNDED_QUANTS = frozenset({"*", "*?", "+", "+?", "*+", "++"})


class PatternShapeError(ValueError):
    """The pattern uses a construct the chain splitter does not model."""


@dataclass(frozen=True)
class ChainPlan:
    """How a required pattern splits into ordered LINKS joined by GAPS.

    A gap is a top-level newline-capable wildcard run (``[\\s\\S]*?``, ``.*`` under
    DOTALL) or a newline (``\\r?\\n``). ``instrumented`` is the pattern with each
    link wrapped in its own capture group (backreferences renumbered), so one match
    of it yields every link's span; ``link_groups[k]`` is link k's group number.
    A pattern that is not a chain (one link, a top-level alternation, or a
    construct the splitter does not model) has ``link_groups == (0,)``: its only
    link is the whole match.
    """
    links: Tuple[str, ...]
    instrumented: str
    link_groups: Tuple[int, ...]
    reason: str = ""

    @property
    def is_chain(self) -> bool:
        return len(self.link_groups) >= 2


def _read_quant(body: str, i: int) -> int:
    n = len(body)
    if i < n and body[i] in "*+?":
        i += 1
    elif i < n and body[i] == "{":
        m = re.match(r"\{\d*(?:,\d*)?\}", body[i:])
        if m is None or m.group(0) in ("{}", "{,}"):
            return i
        i += len(m.group(0))
    else:
        return i
    if i < n and body[i] in "?+":
        i += 1
    return i


def _read_atom(body: str, i: int, groups: List[int], backrefs: List[Tuple[int, int, int]]) -> int:
    """Return the index just past the atom starting at ``i``; record capturing
    group openers and numeric backreferences (absolute positions)."""
    n = len(body)
    c = body[i]
    if c == "\\":
        if i + 1 >= n:
            raise PatternShapeError("trailing backslash")
        d = body[i + 1]
        if d in "123456789":
            j = i + 2
            if j < n and body[j].isdigit():
                if (j + 1 < n and body[j + 1].isdigit()
                        and all(ch in "01234567" for ch in body[i + 1:j + 2])):
                    return j + 2      # three octal digits: an escape, not a group
                j += 1
            backrefs.append((i, j, int(body[i + 1:j])))
            return j
        if d == "N" and i + 2 < n and body[i + 2] == "{":
            k = body.find("}", i)
            if k < 0:
                raise PatternShapeError("unterminated \\N{")
            return k + 1
        return i + 2
    if c == "[":
        j = i + 1
        if j < n and body[j] == "^":
            j += 1
        if j < n and body[j] == "]":
            j += 1
        while j < n and body[j] != "]":
            j += 2 if body[j] == "\\" else 1
        if j >= n:
            raise PatternShapeError("unterminated character class")
        return j + 1
    if c == "(":
        if body.startswith("(?#", i):
            k = body.find(")", i)
            if k < 0:
                raise PatternShapeError("unterminated comment group")
            return k + 1
        if body.startswith("(?(", i):
            raise PatternShapeError("conditional group")
        if not body.startswith("(?", i) or body.startswith("(?P<", i):
            groups.append(i)
        j = i + 1
        while True:
            if j >= n:
                raise PatternShapeError("unbalanced parenthesis")
            if body[j] == ")":
                return j + 1
            j = _read_atom(body, j, groups, backrefs)
    if c == ")":
        raise PatternShapeError("unbalanced parenthesis")
    return i + 1


def split_chain(pattern: str) -> ChainPlan:
    """Decompose ``pattern`` into ordered links (see ``ChainPlan``). Never raises:
    an unmodelled construct degrades to the whole-match single link."""
    whole = ChainPlan((pattern,), pattern, (0,), "")
    prefix_end = 0
    flags = ""
    while True:
        m = _GLOBAL_FLAG_GROUP_RE.match(pattern, prefix_end)
        if m is None:
            break
        flags += m.group(1)
        prefix_end = m.end()
    if "x" in flags:
        return ChainPlan((pattern,), pattern, (0,), "verbose flag")
    dotall = "s" in flags
    prefix, body = pattern[:prefix_end], pattern[prefix_end:]
    groups: List[int] = []
    backrefs: List[Tuple[int, int, int]] = []
    items: List[Tuple[int, int, int]] = []    # (start, atom_end, end_with_quant)
    top_alt = False
    try:
        i = 0
        while i < len(body):
            if body[i] == "|":
                top_alt = True
                i += 1
                continue
            a_end = _read_atom(body, i, groups, backrefs)
            q_end = _read_quant(body, a_end)
            items.append((i, a_end, q_end))
            i = q_end
    except PatternShapeError as exc:
        return ChainPlan((pattern,), pattern, (0,), str(exc))
    if top_alt:
        return ChainPlan((pattern,), pattern, (0,), "top-level alternation")

    def is_gap(k: int) -> bool:
        s, a, e = items[k]
        atom, quant = body[s:a], body[a:e]
        if atom == r"\n" or atom in (r"(?:\r?\n)", r"(?:\n)", r"(?:\r\n)"):
            return quant in ("", "+", "?")
        if atom == r"\r":
            return quant in ("", "?")
        if quant not in _UNBOUNDED_QUANTS and not quant.startswith("{"):
            return False
        return atom in _WILDCARD_ATOMS or (atom == "." and dotall)

    link_ranges: List[Tuple[int, int]] = []
    cur: Optional[int] = None
    for k, (s, _a, e) in enumerate(items):
        if is_gap(k):
            if cur is not None:
                link_ranges.append((cur, items[k - 1][2]))
                cur = None
        elif cur is None:
            cur = s
    if cur is not None:
        link_ranges.append((cur, items[-1][2]))
    if len(link_ranges) < 2:
        return whole

    starts = [s for s, _e in link_ranges]

    def renumber(g: int) -> int:
        if g < 1 or g > len(groups):
            raise PatternShapeError("backreference to an unknown group")
        pos = groups[g - 1]
        return g + sum(1 for s in starts if s <= pos)

    try:
        out: List[str] = [prefix]
        br = sorted(backrefs)
        opens = {s for s, _e in link_ranges}
        closes = {e for _s, e in link_ranges}
        pos = 0
        br_i = 0
        while pos <= len(body):
            if pos in closes:
                out.append(")")
            if pos in opens:
                out.append("(")
            if pos == len(body):
                break
            if br_i < len(br) and br[br_i][0] == pos:
                s, e, g = br[br_i]
                out.append("(?:\\%d)" % renumber(g))
                br_i += 1
                pos = e
                continue
            out.append(body[pos])
            pos += 1
        instrumented = "".join(out)
        re.compile(instrumented)
    except (PatternShapeError, re.error) as exc:
        return ChainPlan((pattern,), pattern, (0,), "instrumentation failed: %s" % exc)
    # Wrapper k is numbered after every original group opened before its link
    # (gap groups included) and after the k wrappers before it.
    link_groups: List[int] = []
    for k, (s, _e) in enumerate(link_ranges):
        link_groups.append(sum(1 for g in groups if g < s) + k + 1)
    links = tuple(body[s:e] for s, e in link_ranges)
    return ChainPlan(links, instrumented, tuple(link_groups), "")


# ---------------------------------------------------------------------------
# The log model: lines, offsets, phases.
# ---------------------------------------------------------------------------


@dataclass
class LogModel:
    """The archived KSP.log split into lines (line endings kept, so a join is
    byte-identical) with the run-phase boundaries.

    Phases: ``boot`` is everything before the first seam ``exec ... start`` line,
    ``run`` runs from there to the first quit marker (``hlib.UNITY_QUIT_MARKERS``
    on a ``[Parsek]`` line), ``teardown`` is the rest. A log with no seam command
    is all ``run``. ``steps[i]`` is the seam command id whose segment line i is in
    (``boot`` / ``teardown`` outside the run phase).
    """
    lines: List[str]
    starts: List[int]
    run_start: int
    teardown_start: int
    steps: List[str]

    @property
    def text(self) -> str:
        return "".join(self.lines)

    def line_of(self, offset: int) -> int:
        return max(0, bisect.bisect_right(self.starts, offset) - 1)

    def phase_of(self, idx: int) -> str:
        if idx < self.run_start:
            return PHASE_BOOT
        if idx >= self.teardown_start:
            return PHASE_TEARDOWN
        return PHASE_RUN

    def lines_of_span(self, start: int, end: int) -> List[int]:
        first = self.line_of(start)
        last = self.line_of(max(start, end - 1))
        return list(range(first, last + 1))


def build_log_model(text: str) -> LogModel:
    # Split on "\n" ONLY: str.splitlines also breaks on \r, \f, \x1c... which a
    # regex `.` matches, and the line-local shortcut needs regex-true lines.
    lines = (text or "").split("\n")
    lines = [ln + "\n" for ln in lines[:-1]] + ([lines[-1]] if lines[-1] else [])
    starts: List[int] = []
    off = 0
    for ln in lines:
        starts.append(off)
        off += len(ln)
    run_start: Optional[int] = None
    teardown_start: Optional[int] = None
    steps: List[str] = []
    cur = PHASE_BOOT
    for i, ln in enumerate(lines):
        if teardown_start is None and "[Parsek]" in ln and any(
                mk in ln for mk in hlib.UNITY_QUIT_MARKERS):
            teardown_start = i
        if teardown_start is None:
            m = EXEC_START_RE.search(ln)
            if m is not None:
                if run_start is None:
                    run_start = i
                cur = m.group(1)
        steps.append(PHASE_TEARDOWN if teardown_start is not None else cur)
    if run_start is None:
        run_start = 0
        steps = [PHASE_RUN if s == PHASE_BOOT else s for s in steps]
    if teardown_start is None:
        teardown_start = len(lines)
    return LogModel(lines, starts, run_start, teardown_start, steps)


def insertion_index(model: LogModel) -> int:
    """Where an injected line lands: just before the quit marker (the last run-phase
    moment), else at the end of the log. Inserting before a ``[Parsek]`` line or at
    the end cannot graft a following stack continuation onto the injected block."""
    return model.teardown_start


def text_with_inserted(model: LogModel, at: int, new_lines: Sequence[str]) -> str:
    before = model.lines[:at]
    if before and not before[-1].endswith(("\n", "\r")):
        before = before[:-1] + [before[-1] + "\n"]
    block = [ln if ln.endswith("\n") else ln + "\n" for ln in new_lines]
    return "".join(before + block + model.lines[at:])


# ---------------------------------------------------------------------------
# Where a pattern matched.
# ---------------------------------------------------------------------------


@dataclass
class PatternHits:
    """Every INSTANCE of one required pattern: per link, the (start, end) spans in
    the ORIGINAL log's offsets, aligned by instance (``link_spans[k][i]`` is link
    k of instance i)."""
    pattern: str
    plan: ChainPlan
    link_spans: List[List[Tuple[int, int]]] = field(default_factory=list)
    matches: int = 0
    truncated: bool = False
    line_local: bool = False

    def link_lines(self, model: LogModel, link: Optional[int] = None) -> List[int]:
        out = set()
        for k, spans in enumerate(self.link_spans):
            if link is not None and k != link:
                continue
            for s, e in spans:
                out.update(model.lines_of_span(s, e))
        return sorted(out)


class _Projection:
    """The log with some lines deleted, and the map from its offsets back to the
    original log's offsets."""

    def __init__(self, model: LogModel, deleted: Iterable[int]):
        dropset = set(deleted)
        self.model = model
        self.kept = [i for i in range(len(model.lines)) if i not in dropset]
        self.mstarts: List[int] = []
        off = 0
        for i in self.kept:
            self.mstarts.append(off)
            off += len(model.lines[i])
        self.text = "".join(model.lines[i] for i in self.kept)

    def to_orig(self, off: int) -> int:
        if not self.kept:
            return 0
        j = max(0, bisect.bisect_right(self.mstarts, off) - 1)
        return self.model.starts[self.kept[j]] + off - self.mstarts[j]

    def spans(self, m: "re.Match", groups: Sequence[int]) -> List[Tuple[int, int]]:
        out: List[Tuple[int, int]] = []
        for g in groups:
            s, e = m.span(g)
            if s < 0:
                s = e = m.start()
            os_ = self.to_orig(s)
            out.append((os_, self.to_orig(e - 1) + 1 if e > s else os_))
        return out


def _instance_lines(model: LogModel, spans: Sequence[Tuple[int, int]],
                    select=None) -> set:
    out = set()
    for k, (s, e) in enumerate(spans):
        for li in model.lines_of_span(s, e):
            if select is None or select(k, li):
                out.add(li)
    return out


def find_pattern_hits(pattern: str, model: LogModel) -> PatternHits:
    """Enumerate the pattern's instances.

    A line-local single-link pattern: every ``finditer`` match (a match cannot
    cross a line, so this finds every matching line). Anything else (a chain, or
    a pattern that may span lines): ``finditer`` alone would miss instances its
    first match overlaps - a lazy chain over a whole flight swallows the second
    booster's chain - so instances are peeled one at a time: find a match, record
    it, delete its link lines, search again, up to ``MAX_INSTANCES_PER_PATTERN``.
    """
    plan = split_chain(pattern)
    try:
        rx = re.compile(plan.instrumented)
    except re.error:
        return PatternHits(pattern, plan, [[] for _ in plan.link_groups])
    local = (not plan.is_chain) and is_line_local(pattern)
    if plan.is_chain:
        # The instrumented pattern must match where the original does; anything
        # else means the splitter misread the pattern, so fall back.
        m0, m1 = re.search(pattern, model.text), rx.search(model.text)
        if (m0 and m0.span()) != (m1 and m1.span()):
            plan = ChainPlan((pattern,), pattern, (0,), "instrumented spans diverged")
            rx = re.compile(pattern)
    hits = PatternHits(pattern, plan, [[] for _ in plan.link_groups], line_local=local)

    def add(spans: Sequence[Tuple[int, int]]) -> None:
        hits.matches += 1
        for k, sp in enumerate(spans):
            hits.link_spans[k].append(sp)

    if local:
        proj = _Projection(model, ())
        for m in rx.finditer(model.text):
            add(proj.spans(m, plan.link_groups))
            if hits.matches >= MAX_MATCHES_PER_PATTERN:
                hits.truncated = True
                break
        return hits
    deleted: set = set()
    while True:
        proj = _Projection(model, deleted)
        m = rx.search(proj.text)
        if m is None:
            break
        spans = proj.spans(m, plan.link_groups)
        new = _instance_lines(model, spans) - deleted
        add(spans)
        if not new or hits.matches >= MAX_INSTANCES_PER_PATTERN:
            hits.truncated = bool(new)
            break
        deleted |= new
    return hits


def fixpoint_deletion(hits: PatternHits, model: LogModel, select) -> Tuple[set, bool]:
    """The lines a mutation deletes: every line ``select(link, line)`` picks out
    of every instance, then - for a pattern that may re-form elsewhere - keep
    searching the mutated log and deleting the selected lines of whatever match
    appears, until none does or a match survives on protected lines alone.
    Returns (deleted lines, capped)."""
    deleted: set = set()
    for i in range(hits.matches):
        deleted |= _instance_lines(model, [spans[i] for spans in hits.link_spans], select)
    if hits.line_local and not hits.truncated:
        return deleted, False
    rx = re.compile(hits.plan.instrumented)
    for _ in range(MAX_INSTANCES_PER_PATTERN):
        proj = _Projection(model, deleted)
        m = rx.search(proj.text)
        if m is None:
            return deleted, False
        new = _instance_lines(model, proj.spans(m, hits.plan.link_groups), select) - deleted
        if not new:
            return deleted, False
        deleted |= new
    return deleted, True


# ---------------------------------------------------------------------------
# Mutation records.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class Mutation:
    verifier: str        # logContracts | recordings.count | unityExceptions | anomalySweep | saveParse
    target: str          # the pattern / window label / token under test
    kind: str            # delete-all | drop-link | numeric | phase-isolate | count | inject-* | window
    detail: str
    outcome: str         # KILLED | SURVIVED
    triage: str = ""     # "" when killed, else TRIAGE | INTENDED | INFO
    note: str = ""


@dataclass
class PatternFacts:
    pattern: str
    matches: int
    lines: int
    phases: Tuple[str, ...]
    steps: Tuple[str, ...]
    chain_links: int
    chain_reason: str

    @property
    def multi_phase(self) -> bool:
        return len(self.phases) > 1


@dataclass
class LaneReport:
    spec_id: str
    archive: str
    baseline: str
    baseline_reasons: List[str] = field(default_factory=list)
    mutations: List[Mutation] = field(default_factory=list)
    patterns: List[PatternFacts] = field(default_factory=list)
    notes: List[str] = field(default_factory=list)

    def count(self, outcome: Optional[str] = None, triage: Optional[str] = None) -> int:
        return sum(1 for m in self.mutations
                   if (outcome is None or m.outcome == outcome)
                   and (triage is None or m.triage == triage))


@dataclass(frozen=True)
class ArchiveInputs:
    """What the shell read off disk for one archived run."""
    label: str
    log_text: str
    recording_count: Optional[int]
    snapshot: Optional["saveparse.ParsekSaveSnapshot"]


# ---------------------------------------------------------------------------
# Evaluator adapters (the harness's own pure functions, nothing re-derived).
# ---------------------------------------------------------------------------


def _expectations_pass(expectations: Dict, count: Optional[int], text: str) -> bool:
    return hlib.evaluate_expectations(expectations, count, text).status == "PASS"


def _unity_result(expectations: Dict, text: str):
    stacks = hlib.scan_unity_exception_stacks(text)
    return hlib.evaluate_unity_exceptions(
        stacks.counts, expectations.get(hlib.UNITY_EXCEPTIONS_BLOCK), stacks)


def _anomaly_unallowed(expectations: Dict, text: str) -> List[str]:
    allowed = expectations.get("allowedAnomalies", []) or []
    return hlib.evaluate_anomaly_sweep(hlib.grep_anomaly_tokens(text), allowed,
                                       hlib.count_anomaly_tokens(text))


# ---------------------------------------------------------------------------
# Numeric perturbation.
# ---------------------------------------------------------------------------


def perturb_number(tok: str) -> List[Tuple[str, str]]:
    """The variants of one number literal: ``plus1`` (width kept) and ``zero``
    (or ``one`` when the value already is zero)."""
    if "." in tok:
        ip, frac = tok.split(".", 1)
        is_zero = int(ip) == 0 and int(frac or "0") == 0
        plus = "%d.%s" % (int(ip) + 1, frac)
        other = ("1." + frac) if is_zero else ("0." + "0" * len(frac))
    else:
        is_zero = int(tok) == 0
        plus = str(int(tok) + 1).zfill(len(tok))
        other = "1".zfill(len(tok)) if is_zero else "0" * len(tok)
    out = [("plus1", plus)]
    if other != plus:
        out.append(("one" if is_zero else "zero", other))
    return out


def _token_label(line: str, span_start_col: int, tok_start: int) -> str:
    # The label sits just before the number; a bounded window keeps the
    # end-anchored searches linear on the very long lines KSP.log can carry.
    before = line[max(0, span_start_col, tok_start - 48):tok_start]
    m = _LABEL_KV_RE.search(before)
    if m is not None:
        return m.group(1)
    m = _LABEL_WORD_RE.search(before)
    if m is not None:
        return m.group(1)
    return "#"


def numeric_token_keys(hits: PatternHits, model: LogModel
                       ) -> Dict[Tuple[int, str, int], List[Tuple[int, int, int, str]]]:
    """Group the numbers inside every link span by (link, label, occurrence).

    Perturbing one KEY changes that field in EVERY matched line at once, so a
    pattern that matched a repeated line cannot survive merely because another
    copy of the line was left intact."""
    keys: Dict[Tuple[int, str, int], List[Tuple[int, int, int, str]]] = {}
    for k, spans in enumerate(hits.link_spans):
        for s, e in spans:
            lines = model.lines_of_span(s, e)
            if len(lines) > MAX_SPAN_LINES_FOR_NUMERIC:
                # A link spanning many lines (a tempered multi-line pattern) holds
                # mostly filler it does not constrain; its numbers are not fields.
                continue
            for li in lines:
                ln = model.lines[li]
                base = model.starts[li]
                c0, c1 = max(s - base, 0), min(e - base, len(ln))
                seen: Dict[str, int] = {}
                for m in NUMBER_TOKEN_RE.finditer(ln, c0, c1):
                    label = _token_label(ln, c0, m.start())
                    occ = seen.get(label, 0)
                    seen[label] = occ + 1
                    keys.setdefault((k, label, occ), []).append(
                        (li, m.start(), m.end(), m.group(0)))
    return keys


def classify_numeric_survivor(label: str, variant: str, baseline_values: Sequence[str]) -> Tuple[str, str]:
    all_zero = all(float(v) == 0 for v in baseline_values)
    if _FAILURE_LABEL_RE.search(label) and all_zero and variant in ("plus1", "one"):
        return TRIAGE, "a failure-shaped field moved off zero and the gate still passed"
    if _COUNT_LABEL_RE.search(label) and not all_zero and variant == "zero":
        return TRIAGE, "a count-shaped field dropped to zero and the gate still passed"
    return INFO, "free numeric field"


# ---------------------------------------------------------------------------
# Per-verifier mutation drivers.
# ---------------------------------------------------------------------------


def is_line_local(pattern: str) -> bool:
    """True when no match of ``pattern`` can include a line break or read past its
    own line (conservative: any construct that MIGHT is treated as non-local).

    Newline-capable constructs: ``\\n`` / ``\\s`` / ``\\D`` / ``\\W`` and numeric
    escapes anywhere; a negated class that does not itself exclude ``\\n``
    (``[^\\r\\n]`` is fine, ``[^x]`` is not); anchors; lookbehinds; the s / m / x
    flags. For a line-local pattern a match exists in a text iff it exists in some
    single line, which lets the lane evaluator re-check only the lines a mutation
    touched."""
    if re.search(r"\(\?[aiLmsux-]*[smx][aiLmsux-]*[):]", pattern):
        return False
    risky_escapes = ("s", "D", "W", "n", "A", "Z", "x", "u", "U", "0", "N", "")
    i, n = 0, len(pattern)
    while i < n:
        c = pattern[i]
        if c == "\\":
            if pattern[i + 1:i + 2] in risky_escapes:
                return False
            i += 2
            continue
        if c == "[":
            j = i + 1
            negated = pattern[j:j + 1] == "^"
            if negated:
                j += 1
            if pattern[j:j + 1] == "]":
                j += 1
            body_start = j
            while j < n and pattern[j] != "]":
                j += 2 if pattern[j] == "\\" else 1
            body = pattern[body_start:j]
            k = 0
            has_newline = False
            while k < len(body):
                if body[k] == "\\":
                    esc = body[k + 1:k + 2]
                    if esc == "n":
                        has_newline = True
                    elif not negated and esc in risky_escapes:
                        return False
                    k += 2
                    continue
                k += 1
            if negated and not has_newline:
                return False
            i = j + 1
            continue
        if c in "^$":
            return False
        if pattern.startswith("(?<=", i) or pattern.startswith("(?<!", i):
            return False
        i += 1
    return True


_CONTEXT_MARGIN_LINES = 2
Edits = Dict[int, List[Tuple[int, int, str]]]


class LaneEvaluator:
    """``hlib.evaluate_expectations`` over MUTATED copies of one baseline-green log,
    re-checking only what a mutation can have changed.

    A mutation is (``deleted`` lines, ``edits`` to lines). Since the baseline is
    green, the verdict can only move through a pattern whose match the mutation
    touched:

    - a REQUIRED pattern still matches when one of its original matches lies clear
      of every changed line (a line-local pattern: that very line; any other: the
      match's lines plus a two-line context margin). Otherwise it is re-searched -
      over the edited lines only when line-local, over the whole mutated text when
      not.
    - a FORBIDDEN pattern, clean in the baseline, can only newly match through
      changed text: a line-local one is searched over the edited lines (a deletion
      alters no surviving line), any other over the whole mutated text.
    - ``recordings.count`` is not touched by a log mutation.

    ``confirm`` re-runs the real ``hlib.evaluate_expectations`` over the full
    mutated text; the lane uses it for every survivor it lists for triage, and the
    unit tests hold the shortcut equal to it.
    """

    def __init__(self, expectations: Dict, count: Optional[int], model: LogModel,
                 hits: Dict[str, PatternHits]):
        self.expectations = expectations
        self.count = count
        self.model = model
        lc = expectations.get("logContracts", {}) or {}
        self.required: List[Tuple[str, "re.Pattern", bool, List[frozenset]]] = []
        for pat in lc.get("required", []) or []:
            pat = str(pat)
            h = hits.get(pat) or find_pattern_hits(pat, model)
            hits[pat] = h
            per_match: List[frozenset] = []
            n = min((len(s) for s in h.link_spans), default=0)
            for mi in range(n):
                lines = set()
                for spans in h.link_spans:
                    lines.update(model.lines_of_span(*spans[mi]))
                per_match.append(frozenset(lines))
            local = h.line_local and not h.truncated
            self.required.append((pat, re.compile(pat), local, per_match))
        self.forbidden: List[Tuple[str, "re.Pattern", bool]] = [
            (str(p), re.compile(str(p)), is_line_local(str(p)))
            for p in (lc.get("forbidden", []) or [])]

    def mutated_text(self, deleted: Iterable[int] = (), edits: Optional[Edits] = None) -> str:
        dropset = set(deleted)
        edits = edits or {}
        return "".join(_apply_line_edits(ln, edits.get(i))
                       for i, ln in enumerate(self.model.lines) if i not in dropset)

    def evaluate(self, deleted: Iterable[int] = (),
                 edits: Optional[Edits] = None) -> Tuple[bool, str]:
        deleted = frozenset(deleted)
        edits = edits or {}
        changed = deleted | frozenset(edits)
        # A line-local match cannot contain a line break, so one search over the
        # concatenated edited lines (each keeps its own ending) is one per line.
        edited_text = "".join(_apply_line_edits(self.model.lines[i], eds)
                              for i, eds in sorted(edits.items()) if i not in deleted)
        if edited_text and not edited_text.endswith(("\n", "\r")):
            edited_text += "\n"
        full: List[Optional[str]] = [None]

        def whole() -> str:
            if full[0] is None:
                full[0] = self.mutated_text(deleted, edits)
            return full[0]

        for pat, rx, local, per_match in self.required:
            if local:
                if any(not (m & changed) for m in per_match):
                    continue
                if edited_text and rx.search(edited_text):
                    continue
                return False, "required not matched: %s" % pat
            margin = range(-_CONTEXT_MARGIN_LINES, _CONTEXT_MARGIN_LINES + 1)
            if any(not any((i + d) in changed for i in m for d in margin) for m in per_match):
                continue
            if rx.search(whole()) is None:
                return False, "required not matched: %s" % pat
        if changed:
            for pat, rx, local in self.forbidden:
                if local:
                    if edited_text and rx.search(edited_text):
                        return False, "forbidden matched: %s" % pat
                elif rx.search(whole()) is not None:
                    return False, "forbidden matched: %s" % pat
        return True, ""

    def outcome(self, deleted: Iterable[int] = (), edits: Optional[Edits] = None) -> str:
        return SURVIVED if self.evaluate(deleted, edits)[0] else KILLED

    def confirm(self, deleted: Iterable[int] = (), edits: Optional[Edits] = None) -> str:
        text = self.mutated_text(deleted, edits)
        return SURVIVED if _expectations_pass(self.expectations, self.count, text) else KILLED


def _apply_line_edits(line: str, eds: Optional[List[Tuple[int, int, str]]]) -> str:
    if not eds:
        return line
    pieces: List[str] = []
    pos = 0
    for s, e, new in sorted(eds):
        pieces.append(line[pos:s])
        pieces.append(new)
        pos = e
    pieces.append(line[pos:])
    return "".join(pieces)


def mutate_required_pattern(ev: LaneEvaluator, pattern: str, hits: PatternHits,
                            local_fixture: bool = False) -> Tuple[List[Mutation], PatternFacts]:
    model = ev.model
    all_lines = hits.link_lines(model)
    phases = tuple(p for p in (PHASE_BOOT, PHASE_RUN, PHASE_TEARDOWN)
                   if any(model.phase_of(i) == p for i in all_lines))
    steps = tuple(dict.fromkeys(model.steps[i] for i in all_lines))
    facts = PatternFacts(pattern, hits.matches, len(all_lines), phases, steps,
                         len(hits.plan.link_groups) if hits.plan.is_chain else 1,
                         hits.plan.reason)
    out: List[Mutation] = []
    V = "logContracts"
    if not all_lines:
        return out, facts

    def record(kind: str, detail: str, deleted, edits, triage: str, note: str) -> None:
        res = ev.outcome(deleted, edits)
        if res == SURVIVED and triage == TRIAGE and ev.confirm(deleted, edits) == KILLED:
            # A survivor listed for triage is re-decided by the real evaluator over
            # the full mutated text; it has the last word.
            res, note = KILLED, "incremental evaluator disagreed; the real evaluator killed it"
        if res == KILLED:
            triage = ""
            note = note if note.startswith("incremental") else ""
        out.append(Mutation(V, pattern, kind, detail, res, triage, note))

    def deletion(select) -> Tuple[set, str]:
        deleted, capped = fixpoint_deletion(hits, model, select)
        return deleted, (" (capped at %d re-forms)" % MAX_INSTANCES_PER_PATTERN if capped else "")

    # 1. Delete every line the pattern matched (every instance, and whatever
    #    re-forms after that).
    drop, cap = deletion(None)
    record("delete-all", "%d line(s), %d instance(s)%s" % (len(drop), hits.matches, cap),
           drop, None, INFO if cap else TRIAGE,
           CAPPED_NOTE if cap else "still satisfiable after every matched line was deleted")

    # 2. Drop one link of an ordered chain (every link, the middle ones included).
    if hits.plan.is_chain:
        n = len(hits.plan.link_groups)
        for k in range(n):
            lines_k, cap = deletion(lambda link, _li, k=k: link == k)
            if not lines_k:
                continue
            where = "middle" if 0 < k < n - 1 else ("first" if k == 0 else "last")
            record("drop-link", "link %d/%d (%s)%s %s" % (k + 1, n, where, cap,
                                                         _clip(hits.plan.links[k], 60)),
                   lines_k, None, INFO if cap else TRIAGE,
                   CAPPED_NOTE if cap else "the chain still formed with every line of this link deleted")

    # 3. Phase isolation: can a non-run phase ALONE satisfy the pattern?
    if len(phases) > 1:
        for ph in phases:
            if ph == PHASE_RUN:
                continue
            drop, cap = deletion(lambda _link, li, ph=ph: model.phase_of(li) != ph)
            record("phase-isolate", "only %s-phase matches kept%s" % (ph, cap), drop, None,
                   INFO if cap else TRIAGE,
                   CAPPED_NOTE if cap else "satisfied by %s-phase lines alone: the gate passes "
                   "without the run doing the thing" % ph)

    # 4. Numeric perturbation inside the matched spans.
    keys = numeric_token_keys(hits, model)
    for key in sorted(keys)[:MAX_NUMERIC_KEYS_PER_PATTERN]:
        toks = keys[key]
        link, label, occ = key
        values = [t[3] for t in toks]
        for variant, _ in perturb_number(values[0]):
            edits: Edits = {}
            for li, s, e, tok in toks:
                new = dict(perturb_number(tok)).get(variant)
                if new is None:
                    new = perturb_number(tok)[0][1]
                edits.setdefault(li, []).append((s, e, new))
            triage, note = classify_numeric_survivor(label, variant, values)
            if local_fixture and triage == TRIAGE:
                triage, note = INTENDED, LOCAL_FIXTURE_NOTE
            shown = values[0] if len(set(values)) == 1 else "%s..(%d values)" % (
                values[0], len(set(values)))
            record("numeric", "%s%s=%s %s (%d site(s)%s)" % (
                label, "" if occ == 0 else "#%d" % occ, shown, variant, len(toks),
                "" if not hits.plan.is_chain else ", link %d" % (link + 1)),
                (), edits, triage, note)
    return out, facts


def mutate_recording_count(expectations: Dict, count: Optional[int], text: str) -> List[Mutation]:
    spec = ((expectations.get("recordings") or {}).get("count"))
    if not isinstance(spec, dict) or count is None:
        return []
    out: List[Mutation] = []
    variants = [("plus1", count + 1)]
    if count > 0:
        variants += [("minus1", count - 1), ("zero", 0)]
    for name, val in variants:
        res = KILLED if not _expectations_pass(expectations, val, text) else SURVIVED
        triage, note = "", ""
        if res == SURVIVED:
            if name == "zero":
                triage, note = TRIAGE, "the window admits a run that recorded nothing"
            else:
                triage, note = INFO, "inside the declared window"
        out.append(Mutation("recordings.count", "recordings.count %s" % _window_text(spec),
                            "count", "%d -> %d (%s)" % (count, val, name), res, triage, note))
    return out


def _window_text(spec) -> str:
    if isinstance(spec, dict):
        return "[%s,%s]" % (spec.get("min", ""), spec.get("max", ""))
    return "=%s" % (spec,)


PARSEK_THROW_SITE_BLOCK = (
    "[EXC 00:00:00.000] NullReferenceException: Object reference not set to an instance of an object",
    "\tParsek.MutationCheckProbe:InjectedThrowSite () (at <00000000000000000000000000000000>:0)",
    "\tUnityEngine.DebugLogHandler:LogException(Exception, Object)",
    "[LOG 00:00:00.000] mutation-check injected block end",
)
PARSEK_CALLER_BLOCK = (
    "[EXC 00:00:00.000] NullReferenceException: Object reference not set to an instance of an object",
    "\tVessel.MutationCheckStockThrow () (at <00000000000000000000000000000000>:0)",
    "\tParsek.MutationCheckProbe:InjectedCaller () (at <00000000000000000000000000000000>:0)",
    "\tUnityEngine.DebugLogHandler:LogException(Exception, Object)",
    "[LOG 00:00:00.000] mutation-check injected block end",
)


def mutate_unity_exceptions(expectations: Dict, model: LogModel) -> List[Mutation]:
    block = expectations.get(hlib.UNITY_EXCEPTIONS_BLOCK)
    if not isinstance(block, dict):
        return []
    armed = {k for k in hlib.UNITY_EXCEPTIONS_KEYS if isinstance(block.get(k), int)
             and not isinstance(block.get(k), bool)}
    target = "unityExceptions %s" % ", ".join("%s=%s" % (k, block[k]) for k in sorted(armed))
    at = insertion_index(model)
    out: List[Mutation] = []
    for kind, lines in (("inject-parsek-throw-site", PARSEK_THROW_SITE_BLOCK),
                        ("inject-parsek-caller", PARSEK_CALLER_BLOCK)):
        ue = _unity_result(expectations, text_with_inserted(model, at, lines))
        res = KILLED if ue.status == "FAIL" else SURVIVED
        triage, note = "", ""
        if res == SURVIVED:
            if (kind == "inject-parsek-caller"
                    and hlib.UNITY_EXCEPTIONS_MAX_PARSEK_THROW_SITE_KEY in armed
                    and hlib.UNITY_EXCEPTIONS_MAX_PARSEK_FRAMES_KEY not in armed):
                triage, note = INTENDED, ("maxParsekThrowSite arms the throw-site subset only "
                                          "(operator ruling 2026-09-22)")
            else:
                triage, note = TRIAGE, "a Parsek frame on an exception stack did not red the lane"
        detail = "; ".join(ue.mismatches) if ue.mismatches else "status=%s" % ue.status
        out.append(Mutation("unityExceptions", target, kind, _clip(detail, 140), res, triage, note))
    return out


def anomaly_injection_line(tok: str) -> str:
    return "[LOG 00:00:00.000] [Parsek][INFO]%s phase=Anomaly reason=%s" % (MUTATION_CHECK_TAG, tok)


def mutate_anomaly_sweep(expectations: Dict, model: LogModel) -> List[Mutation]:
    """Inject one well-formed raise (``anomaly_injection_line``) of each gated token.
    The sweep is a function of the per-token hit set and raise counts, so the
    injected run's inputs are the baseline's plus that one raise (``test_mutlib``
    holds this equal to a full text replay): one log walk per lane, not per token."""
    text = model.text
    base_hits = set(hlib.grep_anomaly_tokens(text))
    base_counts = hlib.count_anomaly_tokens(text)
    allowed = expectations.get("allowedAnomalies", []) or []
    parse = hlib.parse_allowed_anomalies(allowed)
    out: List[Mutation] = []
    for tok in hlib.ANOMALY_TOKENS:
        hits = [t for t in hlib.ANOMALY_TOKENS if t in base_hits or t == tok]
        counts = dict(base_counts)
        counts[tok] = counts.get(tok, 0) + 1
        unallowed = hlib.evaluate_anomaly_sweep(hits, allowed, counts)
        res = KILLED if tok in unallowed else SURVIVED
        triage, note = "", ""
        if res == SURVIVED:
            budget = parse.budgets.get(tok, "absent")
            triage = INTENDED
            note = ("allowedAnomalies tolerates it at any count" if budget is None
                    else "allowedAnomalies budget %s has headroom" % (budget,))
        out.append(Mutation("anomalySweep", tok, "inject-anomaly", "one raise", res, triage, note))
    return out


def save_windows(expectations: Dict, snapshot) -> List[Tuple[str, object, int, bool]]:
    """Every count window the save-parse evaluator checks: (label, window,
    measured, armed). Mirrors ``saveparse.evaluate_save_structure``'s walk; the
    labels are the evaluator's own mismatch labels."""
    if snapshot is None or not snapshot.parsed or not snapshot.scenario_found:
        return []
    facets = saveparse.observed_structure_facets(snapshot)
    armed = set(saveparse.armed_structure_blocks(expectations))
    out: List[Tuple[str, object, int, bool]] = []
    rewind = expectations.get(saveparse.REWIND_BLOCK)
    if isinstance(rewind, dict):
        for key in ("supersedeRows", "tombstones", "rewindPoints"):
            if key in rewind:
                out.append(("rewind.%s" % key, rewind[key], facets["rewind"][key],
                            saveparse.REWIND_BLOCK in armed))
    structure = saveparse._structure_block(expectations)
    if structure is not None:
        meas = facets["recordings"]["structure"]
        a = "recordings.structure" in armed
        for key in saveparse.STRUCTURE_SCALAR_KEYS:
            if key in structure:
                out.append(("recordings.structure.%s" % key, structure[key], meas[key], a))
        for group in ("terminalStates", "branchPoints", "vesselNames"):
            sub = structure.get(group)
            if isinstance(sub, dict):
                for name, window in sub.items():
                    out.append(("recordings.structure.%s.%s" % (group, name), window,
                                meas[group].get(name, 0), a))
    points = saveparse._points_block(expectations)
    if points is not None:
        meas = facets["recordings"]["points"]
        a = "recordings.points" in armed
        for key in saveparse.POINTS_ASSERTION_KEYS:
            if key in points:
                out.append(("recordings.points.%s" % key, points[key], meas[key], a))
    routes = expectations.get(saveparse.ROUTES_BLOCK)
    if isinstance(routes, dict):
        meas = facets["routes"]
        a = saveparse.ROUTES_BLOCK in armed
        for key in saveparse.ROUTES_COUNT_KEYS:
            if key in routes:
                out.append(("routes.%s" % key, routes[key], meas[key], a))
        for group in [g for g, _ in saveparse.ROUTES_ENUM_GROUPS] + list(saveparse.ROUTES_BODY_GROUPS):
            sub = routes.get(group)
            if isinstance(sub, dict):
                for name, window in sub.items():
                    out.append(("routes.%s.%s" % (group, name), window,
                                meas[group].get(name, 0), a))
    return out


def mutate_save_windows(expectations: Dict, snapshot) -> List[Mutation]:
    out: List[Mutation] = []
    for label, window, measured, armed in save_windows(expectations, snapshot):
        if not armed:
            continue
        variants = [("plus1", measured + 1)]
        if measured > 0:
            variants += [("minus1", measured - 1), ("zero", 0)]
        for name, val in variants:
            mm: List[str] = []
            saveparse._check_window(label, window, val, mm)
            res = KILLED if mm else SURVIVED
            triage, note = "", ""
            if res == SURVIVED:
                if name == "zero":
                    triage, note = TRIAGE, "the armed window admits zero"
                else:
                    triage, note = INFO, "inside the armed window"
            out.append(Mutation("saveParse", "%s %s" % (label, _window_text(window)), "window",
                                "%d -> %d (%s)" % (measured, val, name), res, triage, note))
    return out


# ---------------------------------------------------------------------------
# Lane orchestration.
# ---------------------------------------------------------------------------


def spec_gating_surfaces(spec: Dict) -> List[str]:
    """The replayable gating surfaces a spec declares (empty -> nothing to check)."""
    exp = spec.get("expectations", {}) or {}
    out: List[str] = []
    if (exp.get("logContracts", {}) or {}).get("required"):
        out.append("logContracts.required")
    if isinstance((exp.get("recordings") or {}).get("count"), dict):
        out.append("recordings.count")
    if isinstance(exp.get(hlib.UNITY_EXCEPTIONS_BLOCK), dict):
        out.append("unityExceptions")
    if saveparse.armed_structure_blocks(exp):
        out.append("saveParse")
    return out


def is_expected_fail_lane(spec: Dict) -> bool:
    """An expected-fail lane (non-empty ``expectedFail.bugId``) is red BY DESIGN,
    so its gates are not replayed here."""
    return bool(((spec.get("expectedFail") or {}).get("bugId") or ""))


def baseline_reasons(expectations: Dict, inputs: ArchiveInputs) -> List[str]:
    """Why the UNMUTATED replay is not green (empty list -> green)."""
    reasons: List[str] = []
    exp = hlib.evaluate_expectations(expectations, inputs.recording_count, inputs.log_text)
    if exp.status != "PASS":
        reasons.extend("expectations: %s" % m for m in exp.mismatches)
    ue = _unity_result(expectations, inputs.log_text)
    if ue.status == "FAIL":
        reasons.extend("unityExceptions: %s" % m for m in ue.mismatches)
    unallowed = _anomaly_unallowed(expectations, inputs.log_text)
    if unallowed:
        reasons.append("anomalySweep: unallowed %s" % unallowed)
    if inputs.snapshot is not None and saveparse.armed_structure_blocks(expectations):
        sp = saveparse.evaluate_save_structure(expectations, inputs.snapshot)
        if sp.status == saveparse.STATUS_FAIL:
            reasons.extend("saveParse: %s" % m for m in sp.armed_mismatches)
    return reasons


def check_lane(spec: Dict, inputs: ArchiveInputs) -> LaneReport:
    """Baseline, then every mutation, for one spec over one archived run."""
    spec_id = str(spec.get("id", "?"))
    expectations = spec.get("expectations", {}) or {}
    reasons = baseline_reasons(expectations, inputs)
    if reasons:
        return LaneReport(spec_id, inputs.label, BASELINE_NOT_GREEN, reasons)
    lane = LaneReport(spec_id, inputs.label, BASELINE_GREEN)
    model = build_log_model(inputs.log_text)
    if not any(EXEC_START_RE.search(ln) for ln in model.lines):
        lane.notes.append("no seam exec line: phases collapse to run")
    local_fixture = hlib.is_local_fixture_template(
        (spec.get("fixture") or {}).get("saveTemplate"))
    hits: Dict[str, PatternHits] = {}
    ev = LaneEvaluator(expectations, inputs.recording_count, model, hits)
    required = (expectations.get("logContracts", {}) or {}).get("required", []) or []
    for pat in required:
        muts, facts = mutate_required_pattern(ev, str(pat), hits[str(pat)], local_fixture)
        lane.mutations.extend(muts)
        lane.patterns.append(facts)
    lane.mutations.extend(mutate_recording_count(expectations, inputs.recording_count,
                                                 inputs.log_text))
    lane.mutations.extend(mutate_unity_exceptions(expectations, model))
    lane.mutations.extend(mutate_anomaly_sweep(expectations, model))
    if saveparse.armed_structure_blocks(expectations):
        if inputs.snapshot is None:
            lane.notes.append("saveParse armed but no archived save: window mutations skipped")
        else:
            lane.mutations.extend(mutate_save_windows(expectations, inputs.snapshot))
    return lane


# ---------------------------------------------------------------------------
# Archive naming + selection (pure over names the shell listed).
# ---------------------------------------------------------------------------

_STAMPED_NAME_RE = re.compile(r"^(\d{4}-\d{2}-\d{2}_\d{4,6})_(.+)$")


def parse_stamped_name(name: str) -> Optional[Tuple[str, str]]:
    """``2026-09-24_0041_SD-1-same-tree-redock`` -> (stamp, label)."""
    m = _STAMPED_NAME_RE.match(name)
    return (m.group(1), m.group(2)) if m else None


@dataclass(frozen=True)
class ArchiveRef:
    stamp: str
    spec_id: str
    source: str          # "results" | "collect"
    run_id: str
    log_path: str
    save_dir: Optional[str]
    recordings_dir: Optional[str]
    verdict: Optional[str]
    truncated: bool = False


def order_candidates(refs: Sequence[ArchiveRef], spec_id: str) -> List[ArchiveRef]:
    """Newest first; a results archive (it carries a verdict) before a collect
    folder of the same stamp; a recorded PASS before anything else at that stamp;
    duplicates of one run id collapse to the first."""
    mine = [r for r in refs if r.spec_id == spec_id]
    mine.sort(key=lambda r: (r.stamp, r.verdict == "PASS", r.source == "results",
                             not r.truncated), reverse=True)
    seen = set()
    out: List[ArchiveRef] = []
    for r in mine:
        key = (r.run_id, r.source)
        if key in seen:
            continue
        seen.add(key)
        out.append(r)
    return out


# ---------------------------------------------------------------------------
# Reporting.
# ---------------------------------------------------------------------------


def _clip(s: str, n: int) -> str:
    s = s.replace("\n", "\\n")
    return s if len(s) <= n else s[:n - 3] + "..."


def summary_line(lane: LaneReport) -> str:
    if lane.baseline != BASELINE_GREEN:
        return "%s: archive=%s baseline not green (%s)" % (
            lane.spec_id, lane.archive, _clip(lane.baseline_reasons[0], 120)
            if lane.baseline_reasons else "?")
    multi = sum(1 for p in lane.patterns if p.multi_phase)
    return ("%s: archive=%s baseline=PASS mutations=%d killed=%d survived=%d "
            "(triage=%d intended=%d info=%d) multiPhasePatterns=%d" % (
                lane.spec_id, lane.archive, len(lane.mutations), lane.count(KILLED),
                lane.count(SURVIVED), lane.count(SURVIVED, TRIAGE),
                lane.count(SURVIVED, INTENDED), lane.count(SURVIVED, INFO), multi))


@dataclass
class SweepTotals:
    lanes_green: int = 0
    lanes_not_green: int = 0
    lanes_no_archive: int = 0
    mutations: int = 0
    killed: int = 0
    survived: int = 0
    triage: int = 0
    intended: int = 0
    info: int = 0


def sweep_totals(lanes: Sequence[LaneReport], no_archive: int) -> SweepTotals:
    t = SweepTotals(lanes_no_archive=no_archive)
    for lane in lanes:
        if lane.baseline != BASELINE_GREEN:
            t.lanes_not_green += 1
            continue
        t.lanes_green += 1
        t.mutations += len(lane.mutations)
        t.killed += lane.count(KILLED)
        t.survived += lane.count(SURVIVED)
        t.triage += lane.count(SURVIVED, TRIAGE)
        t.intended += lane.count(SURVIVED, INTENDED)
        t.info += lane.count(SURVIVED, INFO)
    return t


def render_report(lanes: Sequence[LaneReport], no_archive: Sequence[str],
                  header: str = "", include_info: bool = False) -> str:
    t = sweep_totals(lanes, len(no_archive))
    out: List[str] = ["# Mutation check report", ""]
    if header:
        out += [header, ""]
    out += ["Report-only (trust risk 8, phase 1): survivors are listed for triage and "
            "never fail anything.", "",
            "- lanes with a green baseline: %d" % t.lanes_green,
            "- lanes whose newest archives were not green: %d" % t.lanes_not_green,
            "- lanes with no archive on this machine: %d" % t.lanes_no_archive,
            "- mutations: %d, killed %d, survived %d (triage %d, intended %d, info %d)"
            % (t.mutations, t.killed, t.survived, t.triage, t.intended, t.info), "",
            "## One line per lane", ""]
    out += ["- " + summary_line(l) for l in lanes]
    if no_archive:
        out += ["", "No archive: " + ", ".join(no_archive)]
    out += ["", "## Survivors needing triage", ""]
    any_triage = False
    for lane in lanes:
        tri = [m for m in lane.mutations if m.outcome == SURVIVED and m.triage == TRIAGE]
        if not tri:
            continue
        any_triage = True
        out.append("### %s (%s)" % (lane.spec_id, lane.archive))
        for m in tri:
            out.append("- [%s/%s] %s -- `%s` -- %s" % (m.verifier, m.kind, m.detail,
                                                      _clip(m.target, 200), m.note))
        out.append("")
    if not any_triage:
        out.append("(none)")
    out += ["", "## Multi-phase required patterns", ""]
    any_multi = False
    for lane in lanes:
        for p in lane.patterns:
            if p.multi_phase:
                any_multi = True
                out.append("- %s: `%s` phases=%s steps=%s" % (
                    lane.spec_id, _clip(p.pattern, 160), ",".join(p.phases),
                    ",".join(p.steps[:12]) + ("..." if len(p.steps) > 12 else "")))
    if not any_multi:
        out.append("(none)")
    out += ["", "## Intended survivors", ""]
    for lane in lanes:
        intended = [m for m in lane.mutations if m.outcome == SURVIVED and m.triage == INTENDED]
        if intended:
            by_kind: Dict[str, List[str]] = {}
            for m in intended:
                by_kind.setdefault("%s/%s" % (m.verifier, m.kind), []).append(m.target)
            out.append("- %s: %s" % (lane.spec_id, "; ".join(
                "%s: %s" % (k, ", ".join(v)) for k, v in sorted(by_kind.items()))))
    if include_info:
        out += ["", "## Info survivors (free fields)", ""]
        for lane in lanes:
            for m in lane.mutations:
                if m.outcome == SURVIVED and m.triage == INFO:
                    out.append("- %s [%s/%s] %s -- `%s`" % (
                        lane.spec_id, m.verifier, m.kind, m.detail, _clip(m.target, 120)))
    out += ["", "## Not green", ""]
    for lane in lanes:
        if lane.baseline != BASELINE_GREEN:
            out.append("- %s (%s): %s" % (lane.spec_id, lane.archive,
                                          "; ".join(_clip(r, 160) for r in lane.baseline_reasons[:3])))
    for lane in lanes:
        if lane.notes:
            out.append("- note %s: %s" % (lane.spec_id, "; ".join(lane.notes)))
    return "\n".join(out) + "\n"
