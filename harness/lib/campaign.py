"""Campaign chains: every decision behind ``tools/campaign_runner.py`` (pure).

A campaign ladder (``harness/campaigns/<name>.toml``) is one or more CHAINS of
stages; each stage is one committed lane (a spec id). A chained stage does not
stage its committed fixture: it stages the previous stage's produced save, taken
from that run's own snapshot (``results/<runId>_save``) and harvested at once
into the campaign's folder ``results/campaign/<run>/`` by
``tools/harvest_bdock_station.py`` (the same harvest a fixture promotion runs),
so nothing a sibling run does to an instance save can reach the chain. The
child receives it as ``run.py --slot <s> --stage-from <stage>/in``, which
``run.py`` accepts only from a runner (``stage_from_refusal``).

This module owns:

- The ladder grammar and its validation (``parse_ladder``): stage ids, the
  ``handover`` of each edge (``chain``: stage the previous stage's save;
  ``fixture``: stage the committed fixture, with the reason the edge cannot be
  replayed yet), ``carryCraft`` (builder-made craft copied from the stage's own
  committed fixture into its chained save), the harvest flags, and ``after``
  (a cross-chain dependency, for two pipelined chains).
- Which craft every chained stage launches and whether it reaches the staged
  save (``craft_reach_problems``).
- The range a run flies (``select_range``), the stage records, the stages ready
  to start (``ready_stages``), what each stage stages (``stage_source``), the
  per-chain verdict naming the stage that broke (``chain_outcomes``) and the
  run's exit code.
- The handover marker and the ``--stage-from`` refusal ``run.py`` applies.
- A schedule simulation of N slots (``simulate_schedule``), what a dry run
  prints and what the two-chain pipeline proof checks.
- Whether a stage's save may be promoted to its successor's committed fixture
  (``promotion_target``).

Stdlib only. Nothing here reads a file, a clock or an environment variable.
"""

import re
from dataclasses import dataclass, field, replace
from typing import Any, Callable, Dict, Iterable, List, Optional, Sequence, Set, Tuple

LADDER_SCHEMA = 1
LADDERS_DIRNAME = "campaigns"
CAMPAIGN_RESULTS_DIRNAME = "campaign"
PLAN_SCHEMA = "parsek-campaign-plan/1"
HANDOVER_SCHEMA = "parsek-campaign-handover/1"
STAGE_RECORD_SCHEMA = "parsek-campaign-stage/1"

PLAN_FILE = "campaign.json"
VERDICT_FILE = "campaign.txt"
HANDOVER_FILE = "handover.json"
STAGE_RECORD_FILE = "stage.json"
IN_DIRNAME = "in"
OUT_DIRNAME = "out"

HANDOVER_CHAIN = "chain"
HANDOVER_FIXTURE = "fixture"
HANDOVER_KINDS = (HANDOVER_CHAIN, HANDOVER_FIXTURE)

# A stage record's handover result.
HANDOVER_OK = "ok"
HANDOVER_FAILED = "failed"

FIXTURE_SAVES_PREFIX = "fixtures/saves/"
GAME_MODES = ("SANDBOX", "SCIENCE_SANDBOX", "CAREER")

GREEN_VERDICTS = ("PASS", "EXPECTED-FAIL")

CHAIN_GREEN = "GREEN"
CHAIN_BROKEN = "BROKEN"
CHAIN_PENDING = "PENDING"

OUTCOME_GREEN = "GREEN"
OUTCOME_RED = "RED"
OUTCOME_STOPPED = "STOPPED"
EXIT_GREEN = 0
EXIT_RED = 1
EXIT_STOPPED = 3

_NAME_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")
_CRAFT_RE = re.compile(r"^[A-Za-z0-9_][A-Za-z0-9 _.-]{0,79}$")

STAGE_KEYS = ("id", "label", "handover", "reason", "carryCraft", "harvest", "after")
CHAIN_KEYS = ("name", "stage")
LADDER_KEYS = ("schema", "name", "description", "harvest", "chain")
HARVEST_KEYS = ("keepParsek", "allowNoVessel", "expectMode", "expectSituation")


class LadderError(ValueError):
    """A ladder the runner refuses (every problem named)."""


@dataclass(frozen=True)
class HarvestFlags:
    """The ``tools/harvest_bdock_station.py`` flags a stage's produced save is
    harvested with (``--keep-parsek`` / ``--allow-no-vessel`` /
    ``--expect-mode`` / ``--expect-situation``)."""
    keep_parsek: bool = False
    allow_no_vessel: bool = False
    expect_mode: str = ""
    expect_situation: str = ""

    def argv(self) -> List[str]:
        out: List[str] = []
        if self.keep_parsek:
            out.append("--keep-parsek")
        if self.allow_no_vessel:
            out.append("--allow-no-vessel")
        if self.expect_mode:
            out += ["--expect-mode", self.expect_mode]
        if self.expect_situation:
            out += ["--expect-situation", self.expect_situation]
        return out


@dataclass(frozen=True)
class Stage:
    chain: str
    index: int                 # position in the chain as declared (0-based)
    spec_id: str
    label: str
    template_leaf: str         # the leaf of the spec's fixture.saveTemplate
    profile: str
    handover: str              # the edge INTO this stage
    reason: str                # why a fixture edge is not replayed ("" for chain)
    carry_craft: Tuple[str, ...]
    harvest: HarvestFlags
    after: Tuple[str, ...]     # spec ids of other chains that must be green first

    @property
    def ref(self) -> str:
        return self.label or self.spec_id

    @property
    def dirname(self) -> str:
        return "%02d-%s" % (self.index, self.spec_id)


@dataclass(frozen=True)
class Chain:
    name: str
    stages: Tuple[Stage, ...]


@dataclass(frozen=True)
class Ladder:
    name: str
    description: str
    chains: Tuple[Chain, ...]

    def stages(self) -> List[Stage]:
        return [s for c in self.chains for s in c.stages]

    def stage(self, spec_id: str) -> Optional[Stage]:
        return next((s for s in self.stages() if s.spec_id == spec_id), None)

    def chain(self, name: str) -> Optional[Chain]:
        return next((c for c in self.chains if c.name == name), None)


# ---------------------------------------------------------------------------
# Ladder grammar
# ---------------------------------------------------------------------------


def template_leaf(spec: Dict) -> str:
    template = str(((spec.get("fixture") or {}).get("saveTemplate")) or "")
    return template.replace("\\", "/").rstrip("/").split("/")[-1]


def _flags(obj: Any, base: HarvestFlags, where: str, problems: List[str]) -> HarvestFlags:
    if obj is None:
        return base
    if not isinstance(obj, dict):
        problems.append("%s: harvest must be a table" % where)
        return base
    for k in obj:
        if k not in HARVEST_KEYS:
            problems.append("%s: unknown harvest key %r (known: %s)"
                            % (where, k, ", ".join(HARVEST_KEYS)))
    out = base
    for key, attr in (("keepParsek", "keep_parsek"), ("allowNoVessel", "allow_no_vessel")):
        if key in obj:
            if not isinstance(obj[key], bool):
                problems.append("%s: harvest.%s must be true or false" % (where, key))
            else:
                out = replace(out, **{attr: obj[key]})
    if "expectMode" in obj:
        mode = obj["expectMode"]
        if not isinstance(mode, str) or (mode and mode.upper() not in GAME_MODES):
            problems.append("%s: harvest.expectMode must be one of %s"
                            % (where, ", ".join(GAME_MODES)))
        else:
            out = replace(out, expect_mode=mode.upper())
    if "expectSituation" in obj:
        sit = obj["expectSituation"]
        if not isinstance(sit, str):
            problems.append("%s: harvest.expectSituation must be a string" % where)
        else:
            out = replace(out, expect_situation=sit)
    return out


def parse_ladder(obj: Any, specs_by_id: Dict[str, Dict]) -> Ladder:
    """The ladder a ``harness/campaigns/<name>.toml`` table declares, checked
    against the loaded specs. Raises LadderError naming every problem."""
    problems: List[str] = []
    if not isinstance(obj, dict):
        raise LadderError("a ladder is a TOML table")
    for k in obj:
        if k not in LADDER_KEYS:
            problems.append("unknown ladder key %r (known: %s)" % (k, ", ".join(LADDER_KEYS)))
    if obj.get("schema") != LADDER_SCHEMA:
        problems.append("schema must be %d" % LADDER_SCHEMA)
    name = obj.get("name")
    if not isinstance(name, str) or not _NAME_RE.match(name):
        problems.append("name %r is not a valid name" % (name,))
        name = str(name)
    base = _flags(obj.get("harvest"), HarvestFlags(), "ladder", problems)
    raw_chains = obj.get("chain")
    if not isinstance(raw_chains, list) or not raw_chains:
        problems.append("a ladder declares at least one [[chain]]")
        raw_chains = []
    chains: List[Chain] = []
    seen_chains: Set[str] = set()
    seen_ids: Dict[str, str] = {}
    seen_labels: Dict[str, str] = {}
    for ci, rc in enumerate(raw_chains):
        if not isinstance(rc, dict):
            problems.append("chain #%d is not a table" % ci)
            continue
        for k in rc:
            if k not in CHAIN_KEYS:
                problems.append("chain #%d: unknown key %r" % (ci, k))
        cname = rc.get("name")
        if not isinstance(cname, str) or not _NAME_RE.match(cname):
            problems.append("chain #%d: name %r is not a valid name" % (ci, cname))
            cname = "chain%d" % ci
        if cname in seen_chains:
            problems.append("chain %s is declared twice" % cname)
        seen_chains.add(cname)
        raw_stages = rc.get("stage")
        if not isinstance(raw_stages, list) or not raw_stages:
            problems.append("chain %s declares no [[chain.stage]]" % cname)
            raw_stages = []
        stages: List[Stage] = []
        for si, rs in enumerate(raw_stages):
            where = "chain %s stage #%d" % (cname, si)
            if not isinstance(rs, dict):
                problems.append("%s is not a table" % where)
                continue
            for k in rs:
                if k not in STAGE_KEYS:
                    problems.append("%s: unknown key %r (known: %s)"
                                    % (where, k, ", ".join(STAGE_KEYS)))
            sid = rs.get("id")
            if not isinstance(sid, str) or not sid:
                problems.append("%s: id is required" % where)
                continue
            where = "chain %s stage %s" % (cname, sid)
            spec = specs_by_id.get(sid)
            if spec is None:
                problems.append("%s: no committed spec has this id" % where)
                spec = {}
            if sid in seen_ids:
                problems.append("%s: the id is already a stage of chain %s (a spec flies "
                                "once per campaign)" % (where, seen_ids[sid]))
            seen_ids[sid] = cname
            label = rs.get("label", "")
            if not isinstance(label, str):
                problems.append("%s: label must be a string" % where)
                label = ""
            if label:
                if label in seen_labels or label in specs_by_id and label != sid:
                    problems.append("%s: label %r is not unique" % (where, label))
                seen_labels[label] = sid
            template = str(((spec.get("fixture") or {}).get("saveTemplate")) or "")
            if spec and not template.startswith(FIXTURE_SAVES_PREFIX):
                problems.append("%s: its saveTemplate %r is not a committed fixture (%s...)"
                                % (where, template, FIXTURE_SAVES_PREFIX))
            handover = rs.get("handover")
            reason = rs.get("reason", "")
            if si == 0:
                if handover is not None or reason:
                    problems.append("%s: the first stage of a chain stages its committed "
                                    "fixture; it declares no handover" % where)
                handover, reason = HANDOVER_FIXTURE, "chain head"
            else:
                handover = HANDOVER_CHAIN if handover is None else handover
                if handover not in HANDOVER_KINDS:
                    problems.append("%s: handover %r (known: %s)"
                                    % (where, handover, ", ".join(HANDOVER_KINDS)))
                if not isinstance(reason, str):
                    problems.append("%s: reason must be a string" % where)
                    reason = ""
                if handover == HANDOVER_FIXTURE and not reason.strip():
                    problems.append("%s: a fixture handover names the reason the edge is "
                                    "not replayed" % where)
                if handover == HANDOVER_CHAIN and reason:
                    problems.append("%s: a chain handover takes no reason" % where)
            carry = rs.get("carryCraft", [])
            if not isinstance(carry, list) or not all(isinstance(c, str) for c in carry):
                problems.append("%s: carryCraft is a list of craft names" % where)
                carry = []
            for c in carry:
                if not _CRAFT_RE.match(c) or ".." in c:
                    problems.append("%s: craft name %r is not filename-safe" % (where, c))
            if carry and handover != HANDOVER_CHAIN:
                problems.append("%s: carryCraft applies only to a chain handover" % where)
            after = rs.get("after", [])
            if not isinstance(after, list) or not all(isinstance(a, str) for a in after):
                problems.append("%s: after is a list of stage ids" % where)
                after = []
            flags = _flags(rs.get("harvest"), base, where, problems)
            stages.append(Stage(cname, si, sid, label, template_leaf(spec),
                                str(spec.get("instanceProfile") or ""), handover,
                                reason if handover == HANDOVER_FIXTURE else "",
                                tuple(carry), flags, tuple(after)))
        chains.append(Chain(cname, tuple(stages)))
    ladder = Ladder(name, str(obj.get("description") or ""), tuple(chains))
    by_id = {s.spec_id: s for s in ladder.stages()}
    for s in ladder.stages():
        for dep in s.after:
            d = by_id.get(dep)
            if d is None:
                problems.append("chain %s stage %s: after names %r, not a stage of this "
                                "ladder" % (s.chain, s.spec_id, dep))
            elif d.chain == s.chain:
                problems.append("chain %s stage %s: after names %s of its own chain (the "
                                "chain order already holds it)" % (s.chain, s.spec_id, dep))
    if not problems and _has_cycle(ladder):
        problems.append("the after dependencies and the chain order form a cycle")
    if problems:
        raise LadderError("; ".join(problems))
    return ladder


def _predecessors(ladder: Ladder, stage: Stage) -> List[str]:
    out = list(stage.after)
    chain = ladder.chain(stage.chain)
    pos = [s.spec_id for s in chain.stages].index(stage.spec_id) if chain else 0
    if chain and pos > 0:
        out.append(chain.stages[pos - 1].spec_id)
    return out


def _has_cycle(ladder: Ladder) -> bool:
    state: Dict[str, int] = {}

    def visit(sid: str) -> bool:
        if state.get(sid) == 1:
            return True
        if state.get(sid) == 2:
            return False
        state[sid] = 1
        st = ladder.stage(sid)
        for dep in (_predecessors(ladder, st) if st else []):
            if visit(dep):
                return True
        state[sid] = 2
        return False

    return any(visit(s.spec_id) for s in ladder.stages())


def spec_craft_names(spec: Dict) -> Set[str]:
    """Every craft name a spec launches: each string value under a ``craft`` key
    anywhere in its driver (a ``GoToEditor`` step's arg, a mission's
    ``missionParams.craft``)."""
    out: Set[str] = set()

    def walk(node: Any) -> None:
        if isinstance(node, dict):
            for k, v in node.items():
                if k == "craft" and isinstance(v, str) and v:
                    out.add(v)
                else:
                    walk(v)
        elif isinstance(node, list):
            for v in node:
                walk(v)

    walk(spec.get("driver") or {})
    return out


def craft_reach_problems(ladder: Ladder, spec_crafts: Dict[str, Set[str]],
                         shared_ships: Dict[str, Sequence[str]],
                         committed_craft: Dict[str, Set[str]]) -> List[str]:
    """Each craft a stage launches must be in its staged save. For a stage that
    stages its committed fixture that is the fixture's own ``Ships/VAB``
    (``committed_craft[leaf]``) or its shared-ships overlay; for a chained stage
    it is what the chain carried (every craft the previous stages' saves held:
    the chain head's committed craft, and each stage's ``carryCraft``), the
    overlay keyed by its template leaf, or its own ``carryCraft``. A
    ``carryCraft`` name must exist in the stage's committed fixture. The harvest
    drops a library craft from a chained save, so the overlay supplies it."""
    problems: List[str] = []
    for chain in ladder.chains:
        held: Set[str] = set()
        for st in chain.stages:
            own = committed_craft.get(st.template_leaf, set())
            for c in st.carry_craft:
                if c not in own:
                    problems.append("%s: carryCraft %r is not in fixtures/saves/%s/Ships/VAB"
                                    % (st.spec_id, c, st.template_leaf))
            if st.handover == HANDOVER_FIXTURE:
                held = set(own)
            else:
                held = held | set(st.carry_craft)
            have = held | set(shared_ships.get(st.template_leaf, ()))
            for c in sorted(spec_crafts.get(st.spec_id, set()) - have):
                problems.append("%s: launches %r, which its %s save does not carry (add it "
                                "to carryCraft or the shared-ships row of %s)"
                                % (st.spec_id, c, "chained" if st.handover == HANDOVER_CHAIN
                                   else "committed", st.template_leaf))
    return problems


# ---------------------------------------------------------------------------
# The range a run flies
# ---------------------------------------------------------------------------


def resolve_ref(ladder: Ladder, ref: str) -> Optional[Stage]:
    """A stage by spec id or ladder label."""
    for s in ladder.stages():
        if ref in (s.spec_id, s.label):
            return s
    return None


def select_range(ladder: Ladder, from_ref: Optional[str], to_ref: Optional[str]) -> Ladder:
    """The ladder a run flies. Without ``--from`` / ``--to``: every chain whole.
    With either: one chain, the stages from..to inclusive (by spec id or label;
    both in the same chain); the first selected stage stages its committed
    fixture, and a cross-chain ``after`` outside the range is dropped."""
    if not from_ref and not to_ref:
        return ladder
    first = resolve_ref(ladder, from_ref) if from_ref else None
    last = resolve_ref(ladder, to_ref) if to_ref else None
    unknown = [r for r, s in ((from_ref, first), (to_ref, last)) if r and s is None]
    if unknown:
        raise LadderError("no stage of ladder %s is named %s (use a spec id or a label)"
                          % (ladder.name, ", ".join(unknown)))
    chain_name = (first or last).chain
    if first and last and first.chain != last.chain:
        raise LadderError("--from %s and --to %s are in different chains (%s, %s)"
                          % (from_ref, to_ref, first.chain, last.chain))
    chain = ladder.chain(chain_name)
    ids = [s.spec_id for s in chain.stages]
    lo = ids.index(first.spec_id) if first else 0
    hi = ids.index(last.spec_id) if last else len(ids) - 1
    if lo > hi:
        raise LadderError("--from %s comes after --to %s in chain %s"
                          % (from_ref, to_ref, chain_name))
    picked = list(chain.stages[lo:hi + 1])
    kept = set(s.spec_id for s in picked)
    out = []
    for i, s in enumerate(picked):
        s = replace(s, after=tuple(a for a in s.after if a in kept))
        if i == 0:
            s = replace(s, handover=HANDOVER_FIXTURE, reason="range head", carry_craft=())
        out.append(s)
    return Ladder(ladder.name, ladder.description, (Chain(chain_name, tuple(out)),))


# ---------------------------------------------------------------------------
# Stage records, readiness, verdicts
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class StageRecord:
    spec_id: str
    verdict: str
    attempts: int = 0
    run_ids: Tuple[str, ...] = ()
    handover: str = ""        # HANDOVER_OK / HANDOVER_FAILED / "" (not attempted)
    reason: str = ""          # why the handover failed
    source: str = ""          # what the stage staged: "fixture" or the predecessor id
    out_leaf: str = ""        # the harvested save under <stage>/out/

    @property
    def green(self) -> bool:
        return self.verdict in GREEN_VERDICTS and self.handover == HANDOVER_OK

    def to_json(self) -> Dict[str, Any]:
        return {"schema": STAGE_RECORD_SCHEMA, "id": self.spec_id, "verdict": self.verdict,
                "attempts": self.attempts, "runIds": list(self.run_ids),
                "handover": self.handover, "reason": self.reason, "source": self.source,
                "outLeaf": self.out_leaf}

    @staticmethod
    def from_json(obj: Any) -> Optional["StageRecord"]:
        if not isinstance(obj, dict) or obj.get("schema") != STAGE_RECORD_SCHEMA:
            return None
        sid, verdict = obj.get("id"), obj.get("verdict")
        if not isinstance(sid, str) or not isinstance(verdict, str):
            return None
        attempts = obj.get("attempts")
        if not isinstance(attempts, int) or isinstance(attempts, bool):
            attempts = 0
        run_ids = obj.get("runIds") or []
        return StageRecord(sid, verdict, attempts,
                           tuple(str(r) for r in run_ids if isinstance(r, str)),
                           str(obj.get("handover") or ""), str(obj.get("reason") or ""),
                           str(obj.get("source") or ""), str(obj.get("outLeaf") or ""))


def harvest_leaf(ladder: Ladder, stage: Stage) -> str:
    """The directory name a stage's produced save is harvested under: the next
    stage's template leaf (so the harvested title is that fixture's title), or
    ``campaign-<id>`` for the last stage of a chain."""
    chain = ladder.chain(stage.chain)
    ids = [s.spec_id for s in chain.stages]
    pos = ids.index(stage.spec_id)
    if pos + 1 < len(ids):
        return chain.stages[pos + 1].template_leaf
    return "campaign-%s" % stage.spec_id


def predecessor(ladder: Ladder, stage: Stage) -> Optional[Stage]:
    chain = ladder.chain(stage.chain)
    ids = [s.spec_id for s in chain.stages]
    pos = ids.index(stage.spec_id)
    return chain.stages[pos - 1] if pos > 0 else None


def stage_source(ladder: Ladder, stage: Stage,
                 records: Dict[str, StageRecord]) -> Tuple[str, Optional[str]]:
    """(source, predecessor spec id) a stage stages: ``("fixture", None)`` for a
    chain head or a fixture edge, ``("chain", <prev>)`` for a chained edge. A
    chained stage is never handed a predecessor save that is not green with its
    handover ok (``ready_stages`` holds it back), so a stale or half-written
    save can never be staged."""
    prev = predecessor(ladder, stage)
    if stage.handover == HANDOVER_CHAIN and prev is not None:
        return HANDOVER_CHAIN, prev.spec_id
    return HANDOVER_FIXTURE, None


def ready_stages(ladder: Ladder, records: Dict[str, StageRecord], running: Iterable[str],
                 failed_this_run: Iterable[str] = ()) -> List[Stage]:
    """Stages that may start now, in ladder order: not green, not running, not
    already red in this invocation, every earlier stage of the chain green, and
    every ``after`` dependency green. A red stage from an EARLIER invocation is
    ready again (a resume re-flies the stage that broke)."""
    running = set(running)
    failed = set(failed_this_run)
    out: List[Stage] = []
    for chain in ladder.chains:
        for st in chain.stages:
            rec = records.get(st.spec_id)
            if rec is not None and rec.green:
                continue
            if st.spec_id in running or st.spec_id in failed:
                break
            deps_green = all(records.get(d) is not None and records[d].green
                             for d in _predecessors(ladder, st))
            if deps_green:
                out.append(st)
            break
    return out


@dataclass(frozen=True)
class ChainOutcome:
    chain: str
    status: str                  # CHAIN_GREEN / CHAIN_BROKEN / CHAIN_PENDING
    stage: Optional[str]         # the stage that broke, or the first not flown
    detail: str
    not_flown: Tuple[str, ...]

    def line(self) -> str:
        text = "chain %s %s" % (self.chain, self.status)
        if self.stage:
            text += " at %s" % self.stage
        if self.detail:
            text += ": %s" % self.detail
        if self.not_flown:
            text += "; not flown: %s" % ", ".join(self.not_flown)
        return text


def describe_record(rec: StageRecord) -> str:
    runs = ",".join(rec.run_ids) or "-"
    if rec.handover == HANDOVER_FAILED:
        return "%s attempts=%d runIds=%s, handover failed: %s" % (
            rec.verdict, rec.attempts, runs, rec.reason or "unknown")
    return "%s attempts=%d runIds=%s" % (rec.verdict, rec.attempts, runs)


def chain_outcomes(ladder: Ladder, records: Dict[str, StageRecord]) -> List[ChainOutcome]:
    """Per chain: GREEN (every stage green), BROKEN at the first stage that flew
    and is not green (its verdict, or its failed handover), or PENDING at the
    first stage not flown (a stop, or a dependency in another chain)."""
    out: List[ChainOutcome] = []
    for chain in ladder.chains:
        ids = [s.spec_id for s in chain.stages]
        status, at, detail, rest = CHAIN_GREEN, None, "", ()
        for i, st in enumerate(chain.stages):
            rec = records.get(st.spec_id)
            if rec is not None and rec.green:
                continue
            at = st.spec_id
            rest = tuple(ids[i + 1:])
            if rec is None:
                status, detail, rest = CHAIN_PENDING, "not flown", tuple(ids[i:])
            else:
                status, detail = CHAIN_BROKEN, describe_record(rec)
            break
        out.append(ChainOutcome(chain.name, status, at, detail, rest))
    return out


def campaign_outcome(ladder: Ladder, records: Dict[str, StageRecord],
                     stopped: bool) -> Tuple[str, int, str]:
    """(outcome, exit code, detail): GREEN (0) when every chain is green; RED
    (1) when a chain broke, or stages were left unflown without a stop; STOPPED
    (3) when a stop left stages unflown and nothing broke."""
    outs = chain_outcomes(ladder, records)
    green = sum(1 for s in ladder.stages() if records.get(s.spec_id) and
                records[s.spec_id].green)
    detail = "%d of %d stage(s) green; %s" % (green, len(ladder.stages()),
                                              "; ".join(o.line() for o in outs))
    if any(o.status == CHAIN_BROKEN for o in outs):
        return OUTCOME_RED, EXIT_RED, detail
    if any(o.status == CHAIN_PENDING for o in outs):
        if stopped:
            return OUTCOME_STOPPED, EXIT_STOPPED, detail
        return OUTCOME_RED, EXIT_RED, detail + " (stages left unflown)"
    return OUTCOME_GREEN, EXIT_GREEN, detail


# ---------------------------------------------------------------------------
# The handover marker and run.py's --stage-from check
# ---------------------------------------------------------------------------


def handover_marker(campaign: str, stage: Stage, from_stage: str,
                    from_run_ids: Sequence[str], token: str) -> Dict[str, Any]:
    return {"schema": HANDOVER_SCHEMA, "campaign": campaign, "chain": stage.chain,
            "forStage": stage.spec_id, "fromStage": from_stage,
            "fromRunIds": list(from_run_ids), "token": token}


def stage_from_refusal(stage_from: str, campaign_root: str, selected_ids: Sequence[str],
                       slot_mode: bool, env_token: Optional[str], marker: Any,
                       has_persistent_sfs: bool) -> Optional[str]:
    """Why ``run.py --stage-from <dir>`` must refuse, or None. ``stage_from`` and
    ``campaign_root`` are resolved absolute paths (``/`` separated). Accepted
    only from a campaign runner's child: slot mode (whose runner token run.py
    checks against the machine lock), one selected lane, a directory named
    ``in`` inside ``results/campaign/``, whose stage folder holds the runner's
    handover marker naming that lane and the runner token, and a save in it."""
    if not slot_mode:
        return "--stage-from is accepted only in a campaign runner's --slot child"
    if len(selected_ids) != 1:
        return "--stage-from takes exactly one selected lane (got %d)" % len(selected_ids)
    root = campaign_root.rstrip("/")
    path = stage_from.rstrip("/")
    if not path.startswith(root + "/"):
        return "--stage-from %s is not inside %s" % (stage_from, campaign_root)
    if path.split("/")[-1] != IN_DIRNAME:
        return "--stage-from %s is not a stage's %s/ folder" % (stage_from, IN_DIRNAME)
    if not isinstance(marker, dict) or marker.get("schema") != HANDOVER_SCHEMA:
        return "no readable %s beside %s" % (HANDOVER_FILE, stage_from)
    if marker.get("forStage") != selected_ids[0]:
        return "the handover is for %r, not the selected lane %r" % (
            marker.get("forStage"), selected_ids[0])
    token = (env_token or "").strip()
    if not token or marker.get("token") != token:
        return "the handover token is not this runner's token"
    if not has_persistent_sfs:
        return "--stage-from %s holds no persistent.sfs" % stage_from
    return None


# ---------------------------------------------------------------------------
# Schedule simulation (dry run; the two-chain pipeline)
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class ScheduledStage:
    spec_id: str
    slot: int
    start: float
    end: float


def simulate_schedule(ladder: Ladder, p50: Callable[[str], float],
                      slots: int) -> List[ScheduledStage]:
    """When each stage would run on ``slots`` copies if every stage took its p50
    and went green: the runner's rule (a stage starts when its chain
    predecessor and its ``after`` stages ended and a slot is free; among ready
    stages the earlier chain in ladder order goes first). No dispatch gap, no
    memory guard, one profile."""
    slots = max(1, int(slots))
    free_at = [0.0] * slots
    done: Dict[str, float] = {}
    out: List[ScheduledStage] = []
    remaining = ladder.stages()
    while remaining:
        best = None
        for st in remaining:
            deps = _predecessors(ladder, st)
            if any(d not in done for d in deps):
                continue
            ready_at = max([done[d] for d in deps] + [0.0])
            slot = min(range(slots), key=lambda i: (max(free_at[i], ready_at), i))
            start = max(free_at[slot], ready_at)
            if best is None or start < best[0]:
                best = (start, slot, st)
        if best is None:
            break
        start, slot, st = best
        end = start + float(p50(st.spec_id))
        free_at[slot] = end
        done[st.spec_id] = end
        out.append(ScheduledStage(st.spec_id, slot, start, end))
        remaining = [s for s in remaining if s.spec_id != st.spec_id]
    return out


# ---------------------------------------------------------------------------
# Promotion to a committed fixture
# ---------------------------------------------------------------------------


def promotion_target(ladder: Ladder, stage: Stage,
                     record: Optional[StageRecord]) -> Tuple[Optional[str], str]:
    """(fixture leaf, "") when a stage's harvested save may replace its
    successor's committed fixture, else (None, why not). Only a green stage
    with a harvested save, and only into a successor whose edge is a chain
    handover: a fixture edge means that fixture carries something the stage
    does not produce (its reason says what)."""
    if record is None or not record.green:
        return None, "stage %s is not green with a harvested save" % stage.spec_id
    chain = ladder.chain(stage.chain)
    ids = [s.spec_id for s in chain.stages]
    pos = ids.index(stage.spec_id)
    if pos + 1 >= len(ids):
        return None, "stage %s is the last of chain %s; no lane stages its save" % (
            stage.spec_id, chain.name)
    nxt = chain.stages[pos + 1]
    if nxt.handover != HANDOVER_CHAIN:
        return None, ("%s stages its committed fixture %s, not %s's save: %s"
                      % (nxt.spec_id, nxt.template_leaf, stage.spec_id, nxt.reason))
    return nxt.template_leaf, ""
