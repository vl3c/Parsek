#!/usr/bin/env python3
"""The far-watch site gate's pure half: read the allowlist, count sites in the IL facts, decide.

design-far-watch.md section 11.4. A site is a production method (every module
but InGameTests) whose compiled body names one of the counted patterns:

- `ActiveVessel`: `FlightGlobals::get_ActiveVessel` or the `fetch.activeVessel`
  field (`FlightGlobals::activeVessel`), each instruction counted;
- `IsGhostMapVessel`: a call to (or `ldftn` of) `GhostMapPresence::IsGhostMapVessel`;
- `ghostMapVesselPids`: any `ldsfld` / `stsfld` / `ldsflda` of that static;
- `wrapper`: a call to a declared reader wrapper (`[wrappers] members`), the
  methods that hand `ActiveVessel` back so a caller reads it without naming it.

Counts come from ilfacts' occurrence counts, compiler-generated code (lambdas,
local functions, iterators) folded into the method that wrote it. The
allowlist `far_watch_sites.toml` pins one entry per (member, pattern) with its
count and decision codes; the gate reds on a site with no entry, an entry
naming no site, a count that differs, an unknown or empty code set, a
`RULING` code, and a production method returning `Vessel` whose own body
names `get_ActiveVessel` that is neither a declared wrapper nor in
`[notWrappers]` with a reason (so a new wrapper cannot hide reads).

The handler gate: a vessel GameEvent subscription is an `ldftn` of a Parsek
method right after the event field's load (ilfacts' `delegates`, exact per
`GameEvents.onX.Add(handler)`), for the events in VESSEL_EVENT_RE. Every such
handler has an entry: `guard` (it must call one of `[handlers] guards`, any
overload, folded code included) or `exempt` with a reason. Until a guard
member exists in the assembly the call check cannot apply and is reported as
not armed; the coverage check (every handler listed, no stale entry) always
applies.

Everything here is pure: facts dicts and TOML text in, findings out.
"""

from __future__ import annotations

import re
import tomllib

SCHEMA = 1

# Decision codes, design section 11.1 (audits A / B for flown-ship reads and
# wrapper calls, audit C for map-ghost checks).
SHIP_CODES = (
    "SKIP-WHILE-DRONE", "PLAYER-SHIP", "CAMERA-OK", "WATCH-INTERNAL", "GREY", "UNREACHABLE", "TEST-SEAM",
    "RULING",
)
MAP_CODES = ("DRONE-SAME", "DRONE-DIFFERENT", "DRONE-NA")
CODES = SHIP_CODES + MAP_CODES
CODES_BY_PATTERN = {
    "ActiveVessel": SHIP_CODES,
    "wrapper": SHIP_CODES,
    "IsGhostMapVessel": MAP_CODES,
    "ghostMapVesselPids": MAP_CODES,
}
PATTERNS = ("ActiveVessel", "IsGhostMapVessel", "ghostMapVesselPids", "wrapper")

ACTIVE_VESSEL_MEMBERS = (
    "Assembly-CSharp|FlightGlobals::get_ActiveVessel",
    "Assembly-CSharp|FlightGlobals::activeVessel",
)
GET_ACTIVE_VESSEL = ACTIVE_VESSEL_MEMBERS[0]
IS_GHOST_MAP_VESSEL = "Parsek.GhostMapPresence::IsGhostMapVessel"
GHOST_MAP_VESSEL_PIDS = "Parsek.GhostMapPresence::ghostMapVesselPids"
DEFAULT_WRAPPERS = (
    "Parsek.WatchModeController::GetActiveVesselSafe()",
    "Parsek.WatchModeController::ReadActiveVesselCore()",
    "Parsek.PredictedCrashReconcilerHost::ReadActiveVesselCore()",
)
DEFAULT_PATTERNS = {"wrappers": DEFAULT_WRAPPERS}
DEFAULT_GUARDS = ("Parsek.GhostMapPresence::IsGhostOrDroneVessel", "Parsek.CameraDrone::Is")

# The vessel GameEvents of section 11.4: onVessel*, onNewVesselCreated,
# onStageActivate, onPartDie, onCrewOnEva, onCrewBoardVessel, VesselSituation.*.
VESSEL_EVENT_RE = re.compile(
    r"^Assembly-CSharp\|GameEvents(?:::(?:onVessel\w*|onNewVesselCreated|onStageActivate|onPartDie"
    r"|onCrewOnEva|onCrewBoardVessel)|/VesselSituation::\w+)$"
)
TOOLING_EXCLUDED = ("InGameTests",)


def split_params(key):
    """The parameter texts of an ilfacts method key (`T::M(a,b<c,d>)` -> ['a', 'b<c,d>'])."""
    inner = key[key.index("(") + 1: key.rindex(")")]
    parts, depth, start = [], 0, 0
    for i, c in enumerate(inner):
        if c in "<[(":
            depth += 1
        elif c in ">])":
            depth -= 1
        elif c == "," and depth == 0:
            parts.append(inner[start:i])
            start = i + 1
    if inner[start:]:
        parts.append(inner[start:])
    return parts


def module_of(facts, entry):
    return facts["types"].get(entry["type"].split("/")[0], {}).get("module")


def is_production(facts, entry):
    """Outside InGameTests/. A type the module map cannot place counts (fail closed)."""
    return module_of(facts, entry) not in TOOLING_EXCLUDED


def method_counts(entry, patterns=DEFAULT_PATTERNS):
    """{pattern: occurrences} for one ilfacts method entry."""
    external = entry.get("externalMemberCounts", {})
    calls = entry.get("callCounts", {})
    statics = entry.get("staticCounts", {})
    wrappers = set(patterns.get("wrappers", ()))
    return {
        "ActiveVessel": sum(external.get(m, 0) for m in ACTIVE_VESSEL_MEMBERS),
        "IsGhostMapVessel": sum(n for k, n in calls.items() if k.startswith(IS_GHOST_MAP_VESSEL + "(")),
        "ghostMapVesselPids": statics.get(GHOST_MAP_VESSEL_PIDS, 0),
        "wrapper": sum(n for k, n in calls.items() if k in wrappers),
    }


def site_counts(facts, patterns=DEFAULT_PATTERNS):
    """{(method key, pattern): count} over every production method with a site."""
    out = {}
    for key, entry in facts["methods"].items():
        if not is_production(facts, entry):
            continue
        for pattern, count in method_counts(entry, patterns).items():
            if count:
                out[(key, pattern)] = count
    return out


# --- the allowlist ---------------------------------------------------------


def parse_config(text):
    """The allowlist as plain data: wrappers, notWrappers, sites {(key, pattern): entry}, handlers.

    Raises ValueError on a shape the gate cannot read (that is a red, not a skip)."""
    data = tomllib.loads(text)
    if data.get("schema") != SCHEMA:
        raise ValueError("schema must be %d, got %r" % (SCHEMA, data.get("schema")))
    wrappers = data.get("wrappers", {}).get("members")
    if not isinstance(wrappers, list) or not wrappers:
        raise ValueError("[wrappers] members must be a non-empty list")
    sites = {}
    for pattern, table in data.get("sites", {}).items():
        if pattern not in PATTERNS:
            raise ValueError("unknown pattern table [sites.%s]" % pattern)
        for key, entry in table.items():
            if not isinstance(entry, dict) or not isinstance(entry.get("count"), int):
                raise ValueError("[sites.%s] %s needs an integer count" % (pattern, key))
            sites[(key, pattern)] = entry
    handlers = data.get("handlers", {})
    return {
        "wrappers": list(wrappers),
        "notWrappers": dict(data.get("notWrappers", {})),
        "sites": sites,
        "guards": list(handlers.get("guards", [])),
        "handlers": dict(handlers.get("entries", {})),
    }


def check_entry_codes(key, pattern, entry):
    """Findings for one entry's codes and audit note."""
    findings = []
    codes = entry.get("codes")
    if not isinstance(codes, list) or not codes:
        findings.append("no-codes: [sites.%s] %s has no decision codes" % (pattern, key))
        return findings
    allowed = CODES_BY_PATTERN[pattern]
    for code in codes:
        if code not in allowed:
            findings.append("unknown-code: [sites.%s] %s code %r (allowed: %s)" % (
                pattern, key, code, ", ".join(allowed)))
        elif code == "RULING":
            findings.append("ruling: [sites.%s] %s is RULING; the owner decides it before it ships" % (pattern, key))
    if not str(entry.get("audit", "")).strip():
        findings.append("no-audit: [sites.%s] %s names no audit row or reason" % (pattern, key))
    return findings


def decide_sites(facts, config):
    """Every site finding (strings starting with a kind), empty when the gate is green."""
    findings = []
    patterns = {"wrappers": config["wrappers"]}
    methods = facts["methods"]
    for wrapper in config["wrappers"]:
        if wrapper not in methods:
            findings.append("stale-wrapper: [wrappers] %s is not a method of the assembly" % wrapper)
    found = site_counts(facts, patterns)
    pinned = config["sites"]
    for site in sorted(set(found) | set(pinned)):
        key, pattern = site
        if site not in pinned:
            findings.append("unlisted: %s has %d %s site(s) and no [sites.%s] entry" % (
                key, found[site], pattern, pattern))
            continue
        findings.extend(check_entry_codes(key, pattern, pinned[site]))
        if site not in found:
            why = "is not a method of the assembly" if key not in methods else "has no %s site" % pattern
            findings.append("stale: [sites.%s] %s %s (renamed, or the read is gone: drop the entry)" % (
                pattern, key, why))
        elif found[site] != pinned[site]["count"]:
            findings.append("count: %s has %d %s site(s), the entry pins %d" % (
                key, found[site], pattern, pinned[site]["count"]))
    wrappers = set(config["wrappers"])
    for key, reason in sorted(config["notWrappers"].items()):
        if key not in methods:
            findings.append("stale-not-wrapper: [notWrappers] %s is not a method of the assembly" % key)
        elif not str(reason).strip():
            findings.append("not-wrapper-reason: [notWrappers] %s needs a reason" % key)
    for key, entry in sorted(methods.items()):
        if not is_production(facts, entry) or key in wrappers or key in config["notWrappers"]:
            continue
        if entry.get("returns") == "Vessel" and entry.get("externalMemberCounts", {}).get(GET_ACTIVE_VESSEL):
            findings.append(
                "wrapper: %s returns Vessel and reads ActiveVessel: add it to [wrappers] (its callers become"
                " sites) or to [notWrappers] with a reason" % key)
    return findings


# --- the handler gate --------------------------------------------------------


def vessel_event_handlers(facts):
    """{handler key: sorted events} for every production vessel-GameEvent subscription."""
    handlers = {}
    for _key, entry in facts["methods"].items():
        if not is_production(facts, entry):
            continue
        for delegate in entry.get("delegates", ()):
            event, _sep, target = delegate.partition(">")
            if not VESSEL_EVENT_RE.match(event):
                continue
            target_entry = facts["methods"].get(target)
            if target_entry is None or not is_production(facts, target_entry):
                continue
            handlers.setdefault(target, set()).add(event.split("::", 1)[1] if "::" in event else event)
    return {key: sorted(events) for key, events in handlers.items()}


def calls_guard(entry, guards):
    return any(callee.startswith(guard + "(") for callee in entry.get("calls", ()) for guard in guards)


def guards_built(facts, guards):
    return sorted(k for k in facts["methods"] if any(k.startswith(g + "(") for g in guards))


def decide_handlers(facts, config):
    """(findings, armed): the handler gate's findings, and whether the guard call check applied."""
    findings = []
    guards = config["guards"]
    if not guards:
        findings.append("no-guards: [handlers] guards is empty")
    armed = bool(guards_built(facts, guards))
    found = vessel_event_handlers(facts)
    listed = config["handlers"]
    for key in sorted(set(found) | set(listed)):
        if key not in listed:
            findings.append("unlisted-handler: %s handles %s and has no [handlers.entries] entry" % (
                key, ", ".join(found[key])))
            continue
        entry = listed[key]
        status = entry.get("status")
        if key not in found:
            findings.append("stale-handler: [handlers.entries] %s subscribes to no vessel event" % key)
            continue
        if sorted(entry.get("events", ())) != found[key]:
            findings.append("handler-events: [handlers.entries] %s lists %s, the IL subscribes it to %s" % (
                key, sorted(entry.get("events", ())), found[key]))
        if status == "exempt":
            if not str(entry.get("reason", "")).strip():
                findings.append("exempt-reason: [handlers.entries] %s is exempt with no reason" % key)
        elif status == "guard":
            if armed and not calls_guard(facts["methods"][key], guards):
                findings.append("unguarded: %s handles %s and calls none of %s" % (
                    key, ", ".join(found[key]), ", ".join(guards)))
        else:
            findings.append("status: [handlers.entries] %s status must be guard or exempt, got %r" % (key, status))
    return findings, armed


# --- writing (the seed script) -----------------------------------------------


def _toml_string(text):
    return '"%s"' % str(text).replace("\\", "\\\\").replace('"', '\\"')


def format_toml(entries, commit, retarget_label=None, wrappers=DEFAULT_WRAPPERS, not_wrappers=None,
                guards=DEFAULT_GUARDS, handlers=None):
    """The allowlist text: header, wrappers, notWrappers, one table per pattern, handlers."""
    lines = [
        "# Far-watch site allowlist (design-far-watch.md section 11.4), read by",
        "# scripts/arch/test_far_watch_sites.py over the compiled Debug Parsek.dll.",
        "#",
        "# One entry per (member, pattern): `count` is the number of IL instructions",
        "# naming the pattern in that method (lambdas and local functions folded in),",
        "# `codes` the audit decision codes (section 11.1), `audit` the audit rows.",
        "# Seeded by scripts/refactor/seed_far_watch_sites.py from audits A, B, C at",
        "# %s%s; from then on" % (commit, ", counts re-read on " + retarget_label if retarget_label else ""),
        "# entries are keyed by member, never by line. Change an entry in the same",
        "# commit as the method it counts: a swap to IsGhostOrDroneVessel drops that",
        "# method's IsGhostMapVessel count, a PLAYER-SHIP read moved to",
        "# FarWatchPlayerVessel.Resolve() drops its ActiveVessel count.",
        "",
        "schema = %d" % SCHEMA,
        "",
        "[wrappers]",
        "# Reader wrappers: a call to one is a `wrapper` site of its caller.",
        "members = [",
    ]
    lines.extend("    %s," % _toml_string(w) for w in wrappers)
    lines.append("]")
    lines.append("")
    lines.append("[notWrappers]")
    lines.append("# Production methods returning Vessel that read ActiveVessel but are not")
    lines.append("# reader wrappers (their callers are not sites), each with the reason.")
    for key, reason in sorted((not_wrappers or {}).items()):
        lines.append("%s = %s" % (_toml_string(key), _toml_string(reason)))
    for pattern in PATTERNS:
        lines.append("")
        lines.append("[sites.%s]" % pattern)
        for (key, p), entry in sorted(entries.items()):
            if p != pattern:
                continue
            lines.append("%s = { count = %d, codes = [%s], audit = %s }" % (
                _toml_string(key), entry["count"], ", ".join(_toml_string(c) for c in entry["codes"]),
                _toml_string(entry["audit"])))
    lines.append("")
    lines.append("[handlers]")
    lines.append("# A vessel GameEvent handler calls one of these (any overload) or is exempt.")
    lines.append("guards = [")
    lines.extend("    %s," % _toml_string(g) for g in guards)
    lines.append("]")
    lines.append("")
    lines.append("[handlers.entries]")
    for key, entry in sorted((handlers or {}).items()):
        parts = ["events = [%s]" % ", ".join(_toml_string(e) for e in entry["events"]),
                 "status = %s" % _toml_string(entry["status"])]
        if entry.get("reason"):
            parts.append("reason = %s" % _toml_string(entry["reason"]))
        lines.append("%s = { %s }" % (_toml_string(key), ", ".join(parts)))
    return "\n".join(lines) + "\n"
