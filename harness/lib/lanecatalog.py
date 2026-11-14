"""Flight profile catalog: what every harness lane flies, derived from its spec.

Pure library behind ``harness/tools/lane_catalog.py``, which writes the GENERATED
``docs/dev/flight-profiles.md``. Every function here takes already-read data
(parsed spec dicts, the status doc's text, the registry dict, the declared in-game
categories) and returns values or markdown text; the shell does all file I/O. The
output is deterministic (sorted, no timestamps) because ``test_lanecatalog`` pins
the committed doc byte-for-byte against a fresh generation.

What is derived, per lane (``derive_lane``):
  - family: the id's first dash token up to its first digit (``B11-mun-orbit`` ->
    ``B``, ``V33M-...`` -> ``V``, ``RF-L1-...`` -> ``RF``, ``FORGE-b17-...`` ->
    ``FORGE``);
  - host save: the leaf of ``fixture.saveTemplate``; injected recordings beside it;
  - driver kind, autopilot mission machine, the seam verbs in first-use order and
    the in-game categories every ``RunTests`` step names (``(all)`` for a step
    with no ``category``);
  - bodies: stock body names found as whole letter-runs in the tags, the fixture
    names, the mission name, the lane id, the D14 values and the ``MapCamera``
    focus args (plus the aliases in ``BODY_ALIASES``). It is a NAME scan, so a
    lane that flies at Kerbin without saying so derives no body;
  - timeline operations, from the seam verbs only (``VERB_TIMELINE_OPS``). The
    first ``LoadGame`` is the boot and is not a reload. An autopilot mission's
    own kRPC warps and burns are invisible here: the catalog reads specs, not
    mission code;
  - expectation blocks present and the dotted paths that declare
    ``gating = true``, ``dimensionsCovered`` as ``D<n>:<value>`` tokens, the
    ``expectedFail`` bug id;
  - status: from the lane's row(s) in ``docs/dev/autotest-status.md`` (the
    status authority; this catalog only quotes it), see ``derive_status``.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

import hlib

DOC_RELPATH = "docs/dev/flight-profiles.md"
REGENERATE_COMMAND = "python harness/tools/lane_catalog.py"

STOCK_BODIES: Tuple[str, ...] = (
    "Kerbin", "Mun", "Minmus", "Moho", "Eve", "Gilly", "Duna", "Ike", "Dres",
    "Jool", "Laythe", "Vall", "Tylo", "Bop", "Pol", "Eeloo", "Sun",
)

# Letter-run tokens that name a body without spelling it. Kept short on purpose:
# "pad" or "runway" would claim Kerbin for lanes that never leave the KSC scene.
BODY_ALIASES: Dict[str, str] = {"lko": "Kerbin", "ksc": "Kerbin", "kerbol": "Sun"}

NO_BODY = "(none derived)"

TIMELINE_OPS: Tuple[str, ...] = (
    "rewind-to-launch", "re-fly", "revert", "reload", "save", "warp",
    "physics-warp", "time-jump", "scene-change", "vessel-switch", "new-game",
)

# Seam verb -> the timeline operations one step of it performs. LoadGame and
# WarpToUT are argument-dependent and handled in step_timeline_ops.
VERB_TIMELINE_OPS: Dict[str, Tuple[str, ...]] = {
    "InvokeRewindToLaunch": ("rewind-to-launch",),
    "InvokeRewind": ("re-fly",),
    "ReFlyRevert": ("re-fly", "revert"),
    "RevertToLaunch": ("revert",),
    "SaveGame": ("save",),
    "TimeJump": ("time-jump",),
    "ExitToSpaceCenter": ("scene-change",),
    "GoToEditor": ("scene-change",),
    "LaunchFromEditor": ("scene-change",),
    "SimulateStockSwitchClick": ("vessel-switch",),
    "CycleVessel": ("vessel-switch",),
    "AlarmJump": ("vessel-switch",),
    "NewGame": ("new-game",),
}

TIERS: Tuple[str, ...] = ("daily", "nightly", "operator", "longhaul")

NO_STATUS_ROW = "no status row"

_LIVE_PROVEN_RE = re.compile(r"LIVE-PROVEN[^0-9|;\"]{0,40}?(\d{4}-\d{2}-\d{2})")
# "NOT LIVE-PROVEN" / "not yet ... LIVE-PROVEN" read as a denial, never a date.
_NEGATED_RE = re.compile(r"\bnot\b[^.|]{0,12}$", re.IGNORECASE)
_BOLD_RE = re.compile(r"\*\*(.+?)\*\*")
_STATUS_WORD_RE = re.compile(
    r"FLOWN|PASS|GREEN|PROVEN|CLAIMED|ARMED|READING|PENDING|CHANGED|DISCIPLINE|"
    r"RESHAPED|MEASURED|PINNED|XPASS|FAIL|BLOCKED|DEFERRED|PARKED")
_FLOWN_RE = re.compile(r"\b((?:RE-)?(?:PROVEN|FLOWN))\W{1,4}(\d{4}-\d{2}-\d{2})")
_DATE_RE = re.compile(r"\d{4}-\d{2}-\d{2}")
_FAMILY_RE = re.compile(r"^([A-Za-z]+)")
_LETTER_RUN_RE = re.compile(r"[a-z]+")


@dataclass(frozen=True)
class Lane:
    id: str
    family: str
    tier: str
    instance_profile: str
    host_save: str
    injected: str
    driver_kind: str
    mission: str
    verbs: Tuple[str, ...]
    categories: Tuple[str, ...]
    bodies: Tuple[str, ...]
    timeline_ops: Tuple[str, ...]
    expectations: Tuple[str, ...]
    gating: Tuple[str, ...]
    dimensions: Tuple[str, ...]
    expected_fail: str
    status: str
    live_proven: str = field(default="")


# ---------------------------------------------------------------------------
# Per-lane derivations.
# ---------------------------------------------------------------------------


def family_of(lane_id: str) -> str:
    """The id prefix before the first number, read off the first dash token."""
    head = (lane_id or "").split("-", 1)[0]
    m = _FAMILY_RE.match(head)
    return m.group(1) if m else head


def _ordered_unique(items: Iterable[str]) -> Tuple[str, ...]:
    seen = []
    for item in items:
        if item not in seen:
            seen.append(item)
    return tuple(seen)


def _steps(spec: Dict) -> List[Dict]:
    driver = (spec or {}).get("driver") or {}
    return [s for s in (driver.get("steps") or []) if isinstance(s, dict)]


def _args(step: Dict) -> Dict:
    args = step.get("args")
    return args if isinstance(args, dict) else {}


def seam_verbs(spec: Dict) -> Tuple[str, ...]:
    """Every seam verb the spec drives, in first-use order (the autopilot
    ``phase = "mission"`` row is not a verb)."""
    return _ordered_unique(s["cmd"] for s in _steps(spec)
                           if isinstance(s.get("cmd"), str))


def ingame_categories(spec: Dict) -> Tuple[str, ...]:
    """The in-game categories the spec's RunTests steps name, first-use order."""
    out: List[str] = []
    for step in _steps(spec):
        if step.get("cmd") != "RunTests":
            continue
        selector = _args(step).get("category")
        cats = hlib.parse_batch_selector_categories(selector)
        if not cats or cats == [hlib.INGAME_RUNALL_CATEGORY]:
            out.append("(all)")
        else:
            out.extend(cats)
    return _ordered_unique(out)


def step_timeline_ops(step: Dict, is_boot: bool) -> Tuple[str, ...]:
    """The timeline operations one seam step performs."""
    cmd = step.get("cmd")
    args = _args(step)
    if cmd == "LoadGame":
        if is_boot:
            return ()
        return ("reload", "scene-change") if args.get("scene") else ("reload",)
    if cmd == "WarpToUT":
        return ("warp", "physics-warp") if args.get(hlib.WARPTOUT_LADDER_KEY) else ("warp",)
    return VERB_TIMELINE_OPS.get(cmd, ())


def timeline_ops(spec: Dict) -> Tuple[str, ...]:
    """The lane's timeline operations, in TIMELINE_OPS order. The first step is
    the boot when it is a LoadGame (every committed spec starts with one)."""
    found = set()
    for index, step in enumerate(_steps(spec)):
        found.update(step_timeline_ops(step, index == 0 and step.get("cmd") == "LoadGame"))
    return tuple(op for op in TIMELINE_OPS if op in found)


def _leaf(path: str) -> str:
    return (path or "").replace("\\", "/").rstrip("/").rsplit("/", 1)[-1]


def body_tokens(text: str) -> List[str]:
    """Stock bodies named in one string, as whole lower-case letter runs (so
    ``cs-career-mun4`` names Mun and ``event`` does not name Eve)."""
    by_lower = {b.lower(): b for b in STOCK_BODIES}
    out = []
    for run in _LETTER_RUN_RE.findall((text or "").lower()):
        if run in by_lower:
            out.append(by_lower[run])
        elif run in BODY_ALIASES:
            out.append(BODY_ALIASES[run])
    return out


def bodies(spec: Dict) -> Tuple[str, ...]:
    """Bodies the spec names (see the module docstring), in STOCK_BODIES order."""
    spec = spec or {}
    fixture = spec.get("fixture") or {}
    texts: List[str] = [str(spec.get("id") or "")]
    texts.extend(str(t) for t in (spec.get("tags") or []))
    texts.append(_leaf(str(fixture.get("saveTemplate") or "")))
    texts.append(str(fixture.get("injectedRecordings") or ""))
    texts.extend(str(c) for c in (fixture.get("craft") or []))
    texts.append(str((spec.get("driver") or {}).get("mission") or ""))
    dims = spec.get("dimensionsCovered") or {}
    if isinstance(dims, dict):
        texts.extend(str(v) for v in (dims.get("D14") or []))
    for step in _steps(spec):
        if step.get("cmd") == "MapCamera":
            texts.append(str(_args(step).get("focus") or ""))
    found = set()
    for text in texts:
        found.update(body_tokens(text))
    return tuple(b for b in STOCK_BODIES if b in found)


def gating_paths(expectations: Dict, prefix: str = "") -> List[str]:
    """Dotted paths of every expectation table that declares ``gating = true``."""
    out = []
    for key in sorted(expectations or {}):
        value = expectations[key]
        if isinstance(value, dict):
            path = prefix + key
            if value.get("gating") is True:
                out.append(path)
            out.extend(gating_paths(value, path + "."))
    return out


def dimension_tokens(spec: Dict) -> Tuple[str, ...]:
    """``dimensionsCovered`` flattened to ``D<n>:<value>``, by dimension number."""
    dims = (spec or {}).get("dimensionsCovered") or {}
    if not isinstance(dims, dict):
        return ()
    out = []
    for dim in sorted(dims, key=_dimension_sort_key):
        values = dims[dim] if isinstance(dims[dim], list) else [dims[dim]]
        out.extend("%s:%s" % (dim, v) for v in sorted(str(v) for v in values))
    return tuple(out)


def _dimension_sort_key(dim: str) -> Tuple[int, str]:
    m = re.match(r"D(\d+)$", dim or "")
    return (int(m.group(1)) if m else 10 ** 6, dim or "")


def expected_fail_bug(spec: Dict) -> str:
    value = (spec or {}).get("expectedFail")
    if isinstance(value, dict):
        return str(value.get("bugId") or "")
    return ""


# ---------------------------------------------------------------------------
# Status (quoted from docs/dev/autotest-status.md, never decided here).
# ---------------------------------------------------------------------------


def _clean_cell(cell: str) -> str:
    return cell.strip().strip("`*").strip()


def index_status_rows(status_text: str) -> Dict[str, List[List[str]]]:
    """Map each table row's first cell (backticks / bold stripped) to the row's
    cells. A row is any line starting with ``|``; header and rule rows index
    under their own first cell and are never looked up."""
    rows: Dict[str, List[List[str]]] = {}
    for line in (status_text or "").splitlines():
        line = line.strip()
        if not line.startswith("|"):
            continue
        cells = [c.strip() for c in line.strip("|").split("|")]
        if cells:
            rows.setdefault(_clean_cell(cells[0]), []).append(cells)
    return rows


def _words(text: str, limit: int) -> str:
    words = text.replace("`", "").split()
    return " ".join(words[:limit]) + (" ..." if len(words) > limit else "")


def live_proven_dates(text: str) -> List[str]:
    """Every ``LIVE-PROVEN <date>`` claim in ``text`` (a short qualifier such as
    ``AND CLAIMED`` may sit between), skipping negated mentions."""
    out = []
    for m in _LIVE_PROVEN_RE.finditer(text or ""):
        if not _NEGATED_RE.search(text[max(0, m.start() - 24):m.start()]):
            out.append(m.group(1))
    return out


def derive_status(rows: Optional[List[List[str]]]) -> Tuple[str, str]:
    """``(status, live_proven_date)`` for one lane's status rows.

    The latest ``LIVE-PROVEN <date>`` anywhere in the rows wins. Otherwise the
    first bold phrase carrying a status word (FLOWN, PASS, CLAIMED, ...) and a
    date, then the latest plain ``RE-PROVEN`` / ``FLOWN <date>``, then an undated
    bold status phrase, then the tier cell's parenthetical, then the details
    cell's first words."""
    if not rows:
        return NO_STATUS_ROW, ""
    joined = " | ".join(" | ".join(cells[1:]) for cells in rows)
    dates = live_proven_dates(joined)
    if dates:
        latest = max(dates)
        return "LIVE-PROVEN " + latest, latest
    phrases = [p for cells in rows for c in cells[1:] for p in _BOLD_RE.findall(c)
               if _STATUS_WORD_RE.search(p)]
    dated = [p for p in phrases if _DATE_RE.search(p)]
    if dated:
        return _words(dated[0], 8), ""
    flown = _FLOWN_RE.findall(joined)
    if flown:
        word, date = max(flown, key=lambda wd: wd[1])
        return "%s %s" % (word, date), ""
    if phrases:
        return _words(phrases[0], 8), ""
    first = rows[0]
    tier_cell = first[1] if len(first) > 1 else ""
    m = re.search(r"\((.+)\)", tier_cell)
    if m:
        return _words(m.group(1), 8), ""
    details = first[2] if len(first) > 2 else ""
    return (_words(details, 6) or "row present, no status words"), ""


# ---------------------------------------------------------------------------
# The whole catalog.
# ---------------------------------------------------------------------------


def derive_lane(spec: Dict, status_rows: Dict[str, List[List[str]]]) -> Lane:
    spec = spec or {}
    lane_id = str(spec.get("id") or "")
    fixture = spec.get("fixture") or {}
    driver = spec.get("driver") or {}
    expectations = spec.get("expectations") or {}
    injected = str(fixture.get("injectedRecordings") or "none")
    status, live = derive_status(status_rows.get(lane_id))
    return Lane(
        id=lane_id,
        family=family_of(lane_id),
        tier=str(spec.get("tier") or ""),
        instance_profile=str(spec.get("instanceProfile") or ""),
        host_save=_leaf(str(fixture.get("saveTemplate") or "")) or "-",
        injected="" if injected == "none" else injected,
        driver_kind=str(driver.get("kind") or ""),
        mission=str(driver.get("mission") or "") if driver.get("kind") == "autopilot" else "",
        verbs=seam_verbs(spec),
        categories=ingame_categories(spec),
        bodies=bodies(spec),
        timeline_ops=timeline_ops(spec),
        expectations=tuple(sorted(k for k, v in expectations.items() if isinstance(v, dict))),
        gating=tuple(gating_paths(expectations)),
        dimensions=dimension_tokens(spec),
        expected_fail=expected_fail_bug(spec),
        status=status,
        live_proven=live,
    )


def build_catalog(specs: Sequence[Dict], status_text: str) -> List[Lane]:
    rows = index_status_rows(status_text)
    return sorted((derive_lane(s, rows) for s in specs), key=lambda l: l.id)


def registry_values(registry: Dict) -> List[Tuple[str, str]]:
    """Every (dimension, value) the registry declares, by dimension number."""
    out = []
    for dim in sorted((k for k, v in (registry or {}).items() if isinstance(v, dict)),
                      key=_dimension_sort_key):
        for value in registry[dim].get("values") or []:
            out.append((dim, str(value)))
    return out


# ---------------------------------------------------------------------------
# Markdown rendering.
# ---------------------------------------------------------------------------


def ascii_only(text: str) -> str:
    """The doc is plain ASCII (house style); quoted status text may not be."""
    text = (text.replace("\u2014", "-").replace("\u2013", "-")
            .replace("\u2192", "->").replace("\u2190", "<-")
            .replace("\u2018", "'").replace("\u2019", "'")
            .replace("\u201c", '"').replace("\u201d", '"').replace("\u00d7", "x"))
    return text.encode("ascii", "replace").decode("ascii")


def cell(text: str, width: int = 0) -> str:
    """One table cell: ASCII, pipes escaped, cut to ``width`` characters."""
    text = ascii_only(text).replace("|", "/").replace("\n", " ").strip()
    if width and len(text) > width:
        text = text[:max(1, width - 3)].rstrip() + "..."
    return text or "-"


def list_cell(items: Sequence[str], width: int) -> str:
    """Items joined with ", ", dropping whole items past ``width`` and naming
    how many were dropped (``a, b +3``)."""
    items = list(items)
    if not items:
        return "-"
    shown: List[str] = []
    for i, item in enumerate(items):
        rest = len(items) - i - 1
        candidate = ", ".join(shown + [item])
        suffix = " +%d" % rest if rest else ""
        if shown and len(candidate + suffix) > width:
            break
        shown.append(item)
    dropped = len(items) - len(shown)
    return cell(", ".join(shown) + (" +%d" % dropped if dropped else ""))


def _table(header: Sequence[str], rows: Iterable[Sequence[str]]) -> List[str]:
    out = ["| " + " | ".join(header) + " |",
           "|" + "|".join("---" for _ in header) + "|"]
    for row in rows:
        out.append("| " + " | ".join(row) + " |")
    out.append("")
    return out


def _count(n: int) -> str:
    return str(n) if n else "-"


def _by_count(counts: Dict[str, int]) -> List[str]:
    return sorted(counts, key=lambda k: (-counts[k], k.lower(), k))


def _families(lanes: Sequence[Lane]) -> List[str]:
    return sorted({l.family for l in lanes}, key=lambda f: (f.lower(), f))


def _families_of(lanes: Sequence[Lane]) -> List[str]:
    counts: Dict[str, int] = {}
    for lane in lanes:
        counts[lane.family] = counts.get(lane.family, 0) + 1
    return ["%s %d" % (f, counts[f]) for f in _by_count(counts)]


def render(lanes: Sequence[Lane], registry: Dict, declared_categories: Iterable[str],
           implemented_verbs: Sequence[str]) -> str:
    """The whole generated document."""
    lanes = sorted(lanes, key=lambda l: l.id)
    declared = sorted(set(declared_categories), key=lambda c: (c.lower(), c))
    out: List[str] = []
    out.extend(_render_header(lanes))
    out.extend(_render_never_used(lanes, registry, declared, implemented_verbs))
    out.extend(_render_families(lanes))
    out.extend(_render_hosts(lanes))
    out.extend(_render_bodies(lanes))
    out.extend(_render_timeline(lanes))
    out.extend(_render_matrix(lanes, "Body x timeline operation",
                              "Lanes per body (rows) per timeline operation (columns). "
                              "`-` is zero: a body never combined with that operation.",
                              "body", list(STOCK_BODIES) + [NO_BODY],
                              lambda l: l.bodies or (NO_BODY,)))
    out.extend(_render_matrix(lanes, "Family x timeline operation",
                              "Lanes per family (rows) per timeline operation (columns).",
                              "family", _families(lanes), lambda l: (l.family,)))
    out.extend(_render_missions(lanes))
    out.extend(_render_verbs(lanes, implemented_verbs))
    out.extend(_render_categories(lanes))
    out.extend(_render_expectations(lanes))
    out.extend(_render_dimensions(lanes, registry))
    out.extend(_render_lanes(lanes))
    while out and out[-1] == "":
        out.pop()
    return "\n".join(ascii_only(line) for line in out) + "\n"


def _render_header(lanes: Sequence[Lane]) -> List[str]:
    tiers = {t: sum(1 for l in lanes if l.tier == t) for t in TIERS}
    other_tiers = sum(1 for l in lanes if l.tier not in TIERS)
    profiles: Dict[str, int] = {}
    for lane in lanes:
        profiles[lane.instance_profile] = profiles.get(lane.instance_profile, 0) + 1
    autopilot = sum(1 for l in lanes if l.driver_kind == "autopilot")
    live = sum(1 for l in lanes if l.live_proven)
    no_row = sum(1 for l in lanes if l.status == NO_STATUS_ROW)
    xfail = sum(1 for l in lanes if l.expected_fail)
    out = [
        "# Flight profiles (harness lane catalog)",
        "",
        "GENERATED FILE - do not hand-edit. Regenerate with `%s` after changing a "
        "lane spec (`harness/scenarios/*.toml`), `harness/coverage/registry.toml`, an "
        "`[InGameTest]` category, or a lane's row in `docs/dev/autotest-status.md`; "
        "`harness/lib/test_lanecatalog.py` reds while this file is stale."
        % REGENERATE_COMMAND,
        "",
        "One row per harness lane, derived from its spec, for finding gaps in what the "
        "lanes fly. Status is QUOTED from `docs/dev/autotest-status.md`, which stays "
        "the status authority; the hand-written design catalog is "
        "`docs/dev/automated-testing-scenario-catalog.md`. Derivation rules: "
        "`harness/lib/lanecatalog.py` (module docstring). Two limits to keep in mind: "
        "bodies are a NAME scan (a lane that flies at Kerbin without naming it derives "
        "no body), and timeline operations come from seam verbs only (an autopilot "
        "mission's own kRPC warps are not counted; the first `LoadGame` is the boot, "
        "not a reload). `python harness/tools/lane_catalog.py --json -` prints the "
        "same data as JSON.",
        "",
        "## Overview",
        "",
    ]
    rows = [("lanes", str(len(lanes)))]
    rows.extend(("tier " + t, str(tiers[t])) for t in TIERS)
    if other_tiers:
        rows.append(("tier (other)", str(other_tiers)))
    rows.extend(("profile " + p, str(profiles[p])) for p in _by_count(profiles))
    rows.extend([("autopilot (mission) lanes", str(autopilot)),
                 ("seam-only lanes", str(len(lanes) - autopilot)),
                 ("LIVE-PROVEN date in status row", str(live)),
                 ("no LIVE-PROVEN date", str(len(lanes) - live)),
                 ("no status row", str(no_row)),
                 ("expectedFail armed", str(xfail))])
    out.extend(_table(("what", "lanes"), rows))
    return out


def _render_never_used(lanes, registry, declared, implemented_verbs) -> List[str]:
    used_verbs = {v for l in lanes for v in l.verbs}
    used_dims = {d for l in lanes for d in l.dimensions}
    used_cats = {c for l in lanes for c in l.categories}
    out = ["## Never used by any lane", ""]
    unused_verbs = [v for v in implemented_verbs if v not in used_verbs]
    out.append("- Seam verbs (of %d implemented): %s" % (
        len(implemented_verbs), ", ".join("`%s`" % v for v in unused_verbs) or "none"))
    unused_dims = [(d, v) for d, v in registry_values(registry)
                   if "%s:%s" % (d, v) not in used_dims]
    out.append("- Dimension values (%d of %d registry values): %s" % (
        len(unused_dims), len(registry_values(registry)),
        ", ".join("%s `%s`" % dv for dv in unused_dims) or "none"))
    unused_cats = [c for c in declared if c not in used_cats]
    out.append("- In-game test categories (%d of %d declared; a lane running "
               "`RunTests` with no category runs them all, see `(all)` below): %s" % (
                   len(unused_cats), len(declared),
                   ", ".join("`%s`" % c for c in unused_cats) or "none"))
    stale = sorted(c for c in used_cats if c != "(all)" and c not in set(declared))
    if stale:
        out.append("- Categories a lane names that no `[InGameTest]` declares: %s"
                   % ", ".join("`%s`" % c for c in stale))
    out.append("")
    return out


def _render_families(lanes) -> List[str]:
    out = ["## Lanes per family", ""]
    rows = []
    for fam in _families(lanes):
        members = [l for l in lanes if l.family == fam]
        rows.append([fam, str(len(members))]
                    + [_count(sum(1 for l in members if l.tier == t)) for t in TIERS]
                    + [_count(sum(1 for l in members if l.driver_kind == "autopilot")),
                       _count(sum(1 for l in members if l.live_proven))])
    out.extend(_table(["family", "lanes"] + list(TIERS) + ["autopilot", "live-proven"], rows))
    return out


def _render_hosts(lanes) -> List[str]:
    counts: Dict[str, int] = {}
    for lane in lanes:
        counts[lane.host_save] = counts.get(lane.host_save, 0) + 1
    rows = []
    for host in _by_count(counts):
        members = [l for l in lanes if l.host_save == host]
        rows.append([cell(host), str(counts[host]), list_cell(_families_of(members), 70)])
    injected: Dict[str, int] = {}
    for lane in lanes:
        if lane.injected:
            injected[lane.injected] = injected.get(lane.injected, 0) + 1
    out = ["## Lanes per host save", "",
           "The host is the leaf of `fixture.saveTemplate`. %d hosts serve %d lanes."
           % (len(counts), len(lanes)), ""]
    out.extend(_table(("host save", "lanes", "families"), rows))
    out.extend(["Injected recordings (`fixture.injectedRecordings`, lanes not on `none`):", ""])
    out.extend(_table(("injected set", "lanes"),
                      ([cell(k), str(injected[k])] for k in _by_count(injected))))
    return out


def _render_bodies(lanes) -> List[str]:
    rows = []
    for body in list(STOCK_BODIES) + [NO_BODY]:
        members = [l for l in lanes if body in (l.bodies or (NO_BODY,))]
        rows.append([body, _count(len(members)),
                     _count(sum(1 for l in members if l.driver_kind == "autopilot")),
                     _count(sum(1 for l in members if l.live_proven)),
                     list_cell(_families_of(members), 60)])
    out = ["## Lanes per body", ""]
    out.extend(_table(("body", "lanes", "autopilot", "live-proven", "families"), rows))
    return out


def _render_timeline(lanes) -> List[str]:
    rows = []
    for op in TIMELINE_OPS:
        members = [l for l in lanes if op in l.timeline_ops]
        rows.append([op, _count(len(members)), list_cell(_families_of(members), 70)])
    none = [l for l in lanes if not l.timeline_ops]
    rows.append(["(none)", _count(len(none)), list_cell(_families_of(none), 70)])
    out = ["## Lanes per timeline operation", "",
           "Derived from seam verbs: rewind-to-launch `InvokeRewindToLaunch`; re-fly "
           "`InvokeRewind` / `ReFlyRevert`; revert `RevertToLaunch` / `ReFlyRevert`; reload "
           "a non-boot `LoadGame`; save `SaveGame`; warp `WarpToUT` (physics-warp with "
           "`ladder`); time-jump `TimeJump`; scene-change `ExitToSpaceCenter` / "
           "`GoToEditor` / `LaunchFromEditor` / a reload with `scene`; vessel-switch "
           "`SimulateStockSwitchClick` / `CycleVessel` / `AlarmJump`; new-game `NewGame`.",
           ""]
    out.extend(_table(("operation", "lanes", "families"), rows))
    return out


def _render_matrix(lanes, title, blurb, label, keys, keys_of) -> List[str]:
    rows = []
    for key in keys:
        members = [l for l in lanes if key in keys_of(l)]
        if not members and label == "family":
            continue
        rows.append([key, _count(len(members))]
                    + [_count(sum(1 for l in members if op in l.timeline_ops))
                       for op in TIMELINE_OPS])
    out = ["## " + title, "", blurb, ""]
    out.extend(_table([label, "lanes"] + list(TIMELINE_OPS), rows))
    return out


def _render_missions(lanes) -> List[str]:
    missions: Dict[str, List[str]] = {}
    for lane in lanes:
        if lane.mission:
            missions.setdefault(lane.mission, []).append(lane.id)
    counts = {m: len(ids) for m, ids in missions.items()}
    out = ["## Lanes per mission machine", "",
           "%d autopilot lanes fly %d mission machines." % (sum(counts.values()), len(counts)),
           ""]
    out.extend(_table(("mission", "lanes", "lane ids"),
                      ([cell(m), str(counts[m]), list_cell(missions[m], 80)]
                       for m in _by_count(counts))))
    return out


def _render_verbs(lanes, implemented_verbs) -> List[str]:
    counts: Dict[str, int] = {v: 0 for v in implemented_verbs}
    for lane in lanes:
        for verb in lane.verbs:
            counts[verb] = counts.get(verb, 0) + 1
    rows = []
    for verb in _by_count(counts):
        members = [l for l in lanes if verb in l.verbs]
        rows.append([verb, _count(counts[verb]), list_cell(_families_of(members), 70)])
    out = ["## Lanes per seam verb", ""]
    out.extend(_table(("verb", "lanes", "families"), rows))
    return out


def _render_categories(lanes) -> List[str]:
    users: Dict[str, List[str]] = {}
    for lane in lanes:
        for cat in lane.categories:
            users.setdefault(cat, []).append(lane.id)
    counts = {c: len(ids) for c, ids in users.items()}
    out = ["## Lanes per in-game test category", "",
           "Categories named by `RunTests` steps (`(all)`: a step with no category). "
           "%d lanes run at least one batch." % sum(1 for l in lanes if l.categories), ""]
    out.extend(_table(("category", "lanes", "lane ids"),
                      ([cell(c), str(counts[c]), list_cell(users[c], 80)]
                       for c in _by_count(counts))))
    return out


def _render_expectations(lanes) -> List[str]:
    present: Dict[str, int] = {}
    gating: Dict[str, int] = {}
    for lane in lanes:
        for block in lane.expectations:
            present[block] = present.get(block, 0) + 1
        for path in lane.gating:
            gating[path] = gating.get(path, 0) + 1
    out = ["## Expectation blocks", "",
           "`[expectations.*]` tables present, and the tables that declare "
           "`gating = true` (dotted path).", ""]
    out.extend(_table(("block", "lanes present"),
                      ([cell(b), str(present[b])] for b in _by_count(present))))
    out.extend(_table(("gating table", "lanes"),
                      ([cell(g), str(gating[g])] for g in _by_count(gating))))
    return out


def _render_dimensions(lanes, registry) -> List[str]:
    counts: Dict[str, int] = {}
    for lane in lanes:
        for token in lane.dimensions:
            counts[token] = counts.get(token, 0) + 1
    rows = []
    dims: Dict[str, List[Tuple[str, int]]] = {}
    for dim, value in registry_values(registry):
        dims.setdefault(dim, []).append((value, counts.get("%s:%s" % (dim, value), 0)))
    for dim in sorted(dims, key=_dimension_sort_key):
        values = dims[dim]
        covered = sum(1 for _v, n in values if n)
        thin = ["%s %d" % (v, n) for v, n in sorted(values, key=lambda vn: (vn[1], vn[0])) if n]
        rows.append([dim, "%d/%d" % (covered, len(values)), list_cell(thin, 90)])
    out = ["## Dimension coverage (`dimensionsCovered`)", "",
           "Per registry dimension: values claimed by at least one lane, then the "
           "claimed values with their lane counts, thinnest first. Unclaimed values "
           "are listed under \"Never used by any lane\". %d lanes declare no "
           "`dimensionsCovered`." % sum(1 for l in lanes if not l.dimensions), ""]
    out.extend(_table(("dimension", "claimed", "values by lane count (thinnest first)"), rows))
    return out


def _render_lanes(lanes) -> List[str]:
    out = ["## Lanes by family", "",
           "Columns: tier (`xfail` when `expectedFail` is armed), host save (`+inj` with "
           "injected recordings), driver (`seam` or the autopilot mission), bodies, "
           "timeline operations, in-game categories, status (quoted).", ""]
    for fam in _families(lanes):
        members = [l for l in lanes if l.family == fam]
        out.extend(["### %s (%d)" % (fam, len(members)), ""])
        rows = []
        for lane in members:
            tier = lane.tier + (" xfail" if lane.expected_fail else "")
            host = lane.host_save + (" +inj %s" % lane.injected if lane.injected else "")
            driver = lane.mission if lane.mission else lane.driver_kind
            rows.append([cell(lane.id, 60), cell(tier, 16), cell(host, 34), cell(driver, 28),
                         list_cell(lane.bodies, 30), list_cell(lane.timeline_ops, 44),
                         list_cell(lane.categories, 40), cell(lane.status, 44)])
        out.extend(_table(("lane", "tier", "host save", "driver", "bodies", "timeline ops",
                           "in-game categories", "status"), rows))
    return out


def lane_to_json(lane: Lane) -> Dict:
    """A JSON-ready dict for one lane (tuples as lists, camelCase keys)."""
    return {
        "id": lane.id, "family": lane.family, "tier": lane.tier,
        "instanceProfile": lane.instance_profile, "hostSave": lane.host_save,
        "injectedRecordings": lane.injected, "driverKind": lane.driver_kind,
        "mission": lane.mission, "seamVerbs": list(lane.verbs),
        "ingameCategories": list(lane.categories), "bodies": list(lane.bodies),
        "timelineOps": list(lane.timeline_ops), "expectations": list(lane.expectations),
        "gating": list(lane.gating), "dimensionsCovered": list(lane.dimensions),
        "expectedFailBugId": lane.expected_fail, "status": lane.status,
        "liveProven": lane.live_proven,
    }
