"""Pure core of the forbidden-token mutation checker (MUTATION-CHECK-PHASE-2, PR 3).

``[expectations.logContracts] forbidden`` is a list of regexes that must NOT match
the run's KSP.log (``hlib.evaluate_expectations``, ``re.search`` over the whole
log). Phase 1 never injected a forbidden line: its ``LaneEvaluator`` only notices a
forbidden pattern that one of its required-pattern edits happens to create, so a
forbidden token that can never fire was invisible. This module closes that:

- for each forbidden pattern it GENERATES a string the regex itself matches
  (``sample_match``, a walk over the parsed regex: literals, classes, branches,
  repeats at their minimum, group back-references; lookarounds and anchors are
  left to the final ``re.search`` check, and up to ``SAMPLE_PASSES`` passes rotate
  the branch / class / repeat choices until one self-matches);
- injects it into the archived KSP.log as a real line would land (framed with a
  KSP ``[LOG hh:mm:ss.mmm] `` prefix, then bare at line start), just before the
  quit marker;
- re-runs the REAL ``hlib.evaluate_expectations`` over the injected log with the
  spec's forbidden list (the required patterns and the recording count cannot
  move a forbidden verdict, so they are left out for speed) and counts a kill only
  when THAT pattern's own ``logContracts.forbidden matched:`` mismatch appears.

Per pattern the verdict is ``PROVEN`` (an injected self-matching line red that
token), ``VACUOUS`` (a self-matching line injected both ways left the token green:
the regex can only match where no log line can sit, e.g. ``^`` without ``(?m)``),
or ``UNCHECKED`` (the archived log already matches it, or no self-matching string
could be generated).

A second, heuristic pass lists forbidden patterns that name a word (four or more
letters, from the pattern's literal text) appearing nowhere in the mod's source
(``unemitted_words``): a renamed log message leaves its forbidden token unable to
fire with nothing reddening. It is a triage list, never a gate verdict (a stock
KSP line, or a word assembled at runtime, reads the same).

Pure: no file I/O, no clock. ``harness/tools/mutation_check.py`` is the shell.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

import hlib
from mutsave import PROVEN, UNCHECKED, VACUOUS, GateVerdict

try:  # Python 3.11+
    import re._parser as _sre_parse  # type: ignore[import-not-found]
    import re._constants as _sre_c  # type: ignore[import-not-found]
except ImportError:  # pragma: no cover - older interpreters
    import sre_constants as _sre_c  # type: ignore[no-redef]
    import sre_parse as _sre_parse  # type: ignore[no-redef]

VERIFIER = "logContracts.forbidden"
KIND_INJECT = "forbidden-inject"
FRAME_PREFIX = "[LOG 12:00:00.000] "
SAMPLE_PASSES = 12
MIN_EMITTER_WORD = 4

# Characters tried, in order, for a class / wildcard position. Newline last, so a
# generated line stays one line unless the regex demands a break.
_CANDIDATES = ("a", "0", " ", "_", "-", "x", "A", "Z", "9", ".", ":", "=", ",", "/",
               "'", '"', "(", ")", "[", "]", "#", "@", "\t", "\n")
_PRINTABLE = tuple(chr(c) for c in range(32, 127))


# ---------------------------------------------------------------------------
# Regex sampling.
# ---------------------------------------------------------------------------


def _in_category(cat, ch: str) -> bool:
    if cat == _sre_c.CATEGORY_DIGIT:
        return ch.isdigit()
    if cat == _sre_c.CATEGORY_NOT_DIGIT:
        return not ch.isdigit()
    if cat == _sre_c.CATEGORY_SPACE:
        return ch in " \t\n\r\f\v"
    if cat == _sre_c.CATEGORY_NOT_SPACE:
        return ch not in " \t\n\r\f\v"
    if cat == _sre_c.CATEGORY_WORD:
        return ch.isalnum() or ch == "_"
    if cat == _sre_c.CATEGORY_NOT_WORD:
        return not (ch.isalnum() or ch == "_")
    return False


def _in_set(items, ch: str, ignorecase: bool) -> bool:
    negate = False
    hit = False
    for op, av in items:
        if op == _sre_c.NEGATE:
            negate = True
        elif op == _sre_c.LITERAL:
            hit = hit or ch == chr(av) or (ignorecase and ch.lower() == chr(av).lower())
        elif op == _sre_c.RANGE:
            lo, hi = av
            hit = hit or lo <= ord(ch) <= hi or (
                ignorecase and (lo <= ord(ch.lower()) <= hi or lo <= ord(ch.upper()) <= hi))
        elif op == _sre_c.CATEGORY:
            hit = hit or _in_category(av, ch)
    return hit != negate


def _pick(pred, k: int) -> Optional[str]:
    picks = [c for c in _CANDIDATES if pred(c)]
    picks += [c for c in _PRINTABLE if c not in _CANDIDATES and pred(c)]
    if not picks:
        return None
    return picks[k % len(picks)]


class _Gen:
    def __init__(self, k: int, ignorecase: bool) -> None:
        self.k = k
        self.ignorecase = ignorecase
        self.groups: Dict[int, str] = {}

    def items(self, items) -> Optional[str]:
        out: List[str] = []
        for op, av in items:
            s = self.node(op, av)
            if s is None:
                return None
            out.append(s)
        return "".join(out)

    def node(self, op, av) -> Optional[str]:
        c = _sre_c
        if op == c.LITERAL:
            return chr(av)
        if op == c.NOT_LITERAL:
            return _pick(lambda ch: ch != chr(av), self.k)
        if op == c.ANY:
            return _pick(lambda ch: ch != "\n", self.k)
        if op == c.IN:
            return _pick(lambda ch: _in_set(av, ch, self.ignorecase), self.k)
        if op == c.BRANCH:
            alts = av[1]
            first = self.k % len(alts)
            for i in range(len(alts)):
                s = self.items(alts[(first + i) % len(alts)])
                if s is not None:
                    return s
            return None
        if op == c.SUBPATTERN:
            group, _add, _del, sub = av
            s = self.items(sub)
            if s is not None and group is not None:
                self.groups[group] = s
            return s
        if op in (c.MAX_REPEAT, c.MIN_REPEAT) or op == getattr(c, "POSSESSIVE_REPEAT", None):
            lo, hi, sub = av
            n = lo
            if (self.k % 2) == 1 and (hi == c.MAXREPEAT or hi > lo):
                n = lo + 1
            out = []
            for _ in range(n):
                s = self.items(sub)
                if s is None:
                    return None
                out.append(s)
            return "".join(out)
        if op == getattr(c, "ATOMIC_GROUP", None):
            return self.items(av)
        if op == c.GROUPREF:
            return self.groups.get(av, "")
        if op == c.GROUPREF_EXISTS:
            group, yes, no = av
            return self.items(yes if group in self.groups else (no or []))
        if op in (c.AT, c.ASSERT, c.ASSERT_NOT):
            # Zero-width: the final re.search decides whether the sample holds.
            return ""
        return None


def sample_match(pattern: str) -> Optional[str]:
    """A string ``re.search(pattern, s)`` matches, or None when none of
    ``SAMPLE_PASSES`` generation passes produced one (or the pattern does not
    compile)."""
    try:
        rx = re.compile(pattern)
        tree = _sre_parse.parse(pattern)
    except (re.error, ValueError, TypeError):
        return None
    ignorecase = bool(rx.flags & re.IGNORECASE)
    for k in range(SAMPLE_PASSES):
        s = _Gen(k, ignorecase).items(list(tree))
        if s is not None and s != "" and rx.search(s) is not None:
            return s
    return None


# ---------------------------------------------------------------------------
# Emitter heuristic.
# ---------------------------------------------------------------------------


def literal_runs(pattern: str) -> List[str]:
    """The pattern's top-level literal text runs (outside repeats, branches and
    lookarounds), in order."""
    try:
        tree = _sre_parse.parse(pattern)
    except (re.error, ValueError, TypeError):
        return []
    runs: List[str] = []
    cur: List[str] = []

    def flush() -> None:
        if cur:
            runs.append("".join(cur))
            cur.clear()

    def walk(items) -> None:
        for op, av in items:
            if op == _sre_c.LITERAL:
                cur.append(chr(av))
            elif op == _sre_c.SUBPATTERN:
                walk(av[3])
            elif op in (_sre_c.MAX_REPEAT, _sre_c.MIN_REPEAT) and av[0] >= 1 and av[0] == av[1]:
                walk(av[2])
            else:
                flush()

    walk(list(tree))
    flush()
    return runs


_WORD_RE = re.compile(r"[A-Za-z0-9]+")


def emitter_words(pattern: str) -> List[str]:
    """Words (``MIN_EMITTER_WORD`` letters or more, no digits: a digit-bearing token
    is an id or a hash, data rather than message text) in the pattern's literal
    runs."""
    out: List[str] = []
    for run in literal_runs(pattern):
        for w in _WORD_RE.findall(run):
            if any(ch.isdigit() for ch in w):
                continue
            if len(w) >= MIN_EMITTER_WORD and w.lower() not in out:
                out.append(w.lower())
    return out


def unemitted_words(pattern: str, corpus_lower: str) -> List[str]:
    """The pattern's literal words absent (case-insensitively) from ``corpus_lower``
    (the lower-cased mod source the shell read)."""
    return [w for w in emitter_words(pattern) if w not in corpus_lower]


# ---------------------------------------------------------------------------
# Injection.
# ---------------------------------------------------------------------------


@dataclass
class ForbiddenCheck:
    mutations: list = field(default_factory=list)      # mutlib.Mutation
    gates: List[GateVerdict] = field(default_factory=list)
    notes: List[str] = field(default_factory=list)
    # (pattern, missing words) for the emitter triage list.
    unemitted: List[Tuple[str, List[str]]] = field(default_factory=list)


def _forbidden_only(expectations: Dict) -> Dict:
    lc = expectations.get("logContracts", {}) or {}
    return {"logContracts": {"forbidden": list(lc.get("forbidden", []) or [])}}


def _hits(expectations: Dict, text: str) -> set:
    res = hlib.evaluate_expectations(expectations, None, text)
    return {m[len(hlib.EXPECTATION_FORBIDDEN_HIT_PREFIX):] for m in res.mismatches
            if m.startswith(hlib.EXPECTATION_FORBIDDEN_HIT_PREFIX)}


def forbidden_hits(expectations: Dict, text: str) -> set:
    """The spec's forbidden patterns the real evaluator reports matched in ``text``."""
    return _hits(_forbidden_only(expectations), text)


def injection_forms(sample: str) -> List[Tuple[str, List[str]]]:
    """(form name, lines) for one generated sample: framed as a KSP log line, then
    bare at line start."""
    lines = sample.split("\n")
    return [("framed", [FRAME_PREFIX + lines[0]] + lines[1:]), ("bare", lines)]


def check_forbidden(expectations: Dict, log_text: str,
                    corpus_lower: Optional[str] = None,
                    inserter=None) -> ForbiddenCheck:
    """Every forbidden-pattern injection for one spec over one archived log.

    ``inserter(text, lines) -> text`` places the injected lines (the lane passes
    phase 1's quit-marker insertion); the default appends them at the end."""
    import mutlib  # local: mutlib imports this module inside its lane checks
    check = ForbiddenCheck()
    patterns: List[str] = []
    for p in ((expectations.get("logContracts", {}) or {}).get("forbidden", []) or []):
        if str(p) not in patterns:
            patterns.append(str(p))
    if not patterns:
        return check
    exps = _forbidden_only(expectations)
    base = _hits(exps, log_text)
    insert = inserter or _append_lines
    for pat in patterns:
        label = "forbidden[%s]" % pat
        if corpus_lower is not None:
            missing = unemitted_words(pat, corpus_lower)
            if missing:
                check.unemitted.append((pat, missing))
        if pat in base:
            check.gates.append(GateVerdict(label, "logContracts", "forbidden", "", UNCHECKED,
                                           "the archived log already matches it"))
            continue
        sample = sample_match(pat)
        if sample is None:
            check.gates.append(GateVerdict(label, "logContracts", "forbidden", "", UNCHECKED,
                                           "no string the regex itself matches could be "
                                           "generated"))
            continue
        killed_form = ""
        tried: List[str] = []
        for form, lines in injection_forms(sample):
            hits = _hits(exps, insert(log_text, lines))
            killed = pat in hits
            others = sorted(h for h in hits - base if h != pat)
            detail = "injected %s line %r%s" % (
                form, mutlib._clip("\n".join(lines), 160),
                ("; also matched: %s" % ", ".join(others)) if others else "")
            check.mutations.append(mutlib.Mutation(
                VERIFIER, pat, KIND_INJECT, detail,
                mutlib.KILLED if killed else mutlib.SURVIVED,
                "" if killed else mutlib.TRIAGE,
                "" if killed else "a line the pattern matches was injected (%s) and the "
                                  "token stayed green" % form))
            tried.append(form)
            if killed:
                killed_form = form
                break
        measured = "sample=%r" % mutlib._clip(sample, 80)
        if killed_form:
            check.gates.append(GateVerdict(label, "logContracts", "forbidden", measured,
                                           PROVEN, "red on the %s line" % killed_form))
        else:
            check.gates.append(GateVerdict(
                label, "logContracts", "forbidden", measured, VACUOUS,
                "a line the regex itself matches, injected %s, never red the token "
                "(it can only match where no log line sits: an anchor, or a break the "
                "log never has)" % " and ".join(tried)))
    return check


def _append_lines(text: str, lines: Sequence[str]) -> str:
    if text and not text.endswith(("\n", "\r")):
        text += "\n"
    return text + "".join(ln + "\n" for ln in lines)


def lane_inserter(model) -> "callable":
    """Phase 1's insertion point (just before the quit marker) over a log model."""
    import mutlib
    at = mutlib.insertion_index(model)

    def insert(_text: str, lines: Sequence[str]) -> str:
        return mutlib.text_with_inserted(model, at, lines)

    return insert
