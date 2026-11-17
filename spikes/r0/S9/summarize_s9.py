#!/usr/bin/env python3
"""Spike S9: stage the spike saves, and turn the spike's KSP.log lines into report cells.

Spike branch only (spike/mp-r0, never merged). Plan: docs/dev/plans/multiplayer/
r0-spikes.md, "S9 Which debug actions a mod can observe". Run sheet: PC-RUN.md.

Subcommands:
  stage --from <save dir> --to <instance>/saves/<NAME>
      Copy a save folder into the spike instance, unchanged. Writes ONLY into --to
      (refused when it exists, is not directly inside a folder named "saves", or lies
      inside a "Kerbal Space Program" folder).
  summarize <KSP.log> [--out FILE]
      Read the [Spike.S9] lines and the MISSIONMARK segment marks (label s9-seg-<name>)
      and print the report tables as Markdown: hooks, presses per segment (lines per
      press against the run sheet's press counts), the control session, the milestone
      ways, and the tool classification. The last lines are the verdict lines
      HOOKS / PRESSES / CONTROL / MILESTONES.
  --selftest
      Run the built-in tests (synthetic logs, plus source sync against the C# files
      when they sit beside this script).

A segment runs from its mark to the next mark. Output carries no save content: tool
names, counts, progress node ids, UT values and key names only. Stdlib only.
"""

import argparse
import collections
import hashlib
import io
import os
import re
import shutil
import sys
import tempfile
import unittest

SPIKE_LINE_RE = re.compile(r"\[Parsek\]\[(INFO|WARN)\]\[Spike\.S9\] (.*?)\s*$")
BODY_RE = re.compile(r"^(observed|event|poll|baseline|rebaseline|inventory): seq=(\d+) (.*)$")
MARK_RE = re.compile(r"\[Parsek\]\[INFO\]\[TestCommands\] MISSIONMARK label=(\S*) ut=(\S+)")
HOOKS_RE = re.compile(r"hooks applied=(\d+) missing=(\d+) failed=(\d+)")
ECHO_RE = re.compile(r"\[DebugConsole\]: (Funds set to|Science set to|Reputation set to|rendezvousing with)")
SEG_PREFIX = "s9-seg-"
HOOK_COUNT = 85

N = 3       # presses per debug button (plan step 1)
CLICKS = 6  # clicks per switch: three on, three off


def entry(tool, expected):
    return ("entry", tool, expected)


def poll(key, expected):
    return ("poll", key, expected)


def echo(text, expected):
    return ("echo", text, expected)


# The toggle widgets of the Cheats screen and the CheatOptions field each one writes.
TOGGLES = [
    ("allow-part-clipping", "CheatOptions.AllowPartClipping"),
    ("biomes-visible", "CheatOptions.BiomesVisible"),
    ("ignore-eva-construction-mass", "CheatOptions.IgnoreEVAConstructionMassLimit"),
    ("ignore-kerbal-inventory", "CheatOptions.IgnoreKerbalInventoryLimits"),
    ("ignore-max-temperature", "CheatOptions.IgnoreMaxTemperature"),
    ("infinite-electricity", "CheatOptions.InfiniteElectricity"),
    ("infinite-propellant", "CheatOptions.InfinitePropellant"),
    ("middle-click-set-position", "CheatOptions.MiddleMouseClickSetPosition"),
    ("no-crash-damage", "CheatOptions.NoCrashDamage"),
    ("non-strict-attachment", "CheatOptions.NonStrictAttachmentOrientation"),
    ("pause-on-vessel-unpack", "CheatOptions.PauseOnVesselUnpack"),
    ("unbreakable-joints", "CheatOptions.UnbreakableJoints"),
]

# The Alt+F12 Difficulty screen: no apply button, every switch writes its field at once.
DIFFICULTY_KEYS = [
    "Difficulty.AllowStockVessels",
    "Difficulty.MissingCrewsRespawn",
    "Flight.CanRestart",
    "Flight.CanLeaveToEditor",
    "Flight.CanQuickSave",
    "Flight.CanQuickLoad",
    "CheatOptions.IgnoreAgencyMindsetOnContracts",
]

CURRENCY_BUTTONS = [
    "funds-plus-small", "funds-plus-big", "funds-minus-small", "funds-minus-big",
    "science-plus-small", "science-plus-big", "science-minus-small", "science-minus-big",
    "rep-plus-small", "rep-plus-big", "rep-minus-small", "rep-minus-big",
]

COMET_SPAWNS = [
    "comet-random", "comet-short", "comet-mid", "comet-long", "comet-interstellar",
    "comet-sentinel", "comet-sentinel-short", "comet-sentinel-mid", "comet-sentinel-long",
    "comet-sentinel-interstellar",
]

# Segment -> the rows the run sheet's presses must produce. An expected count of None is
# reported, never judged (a slider drag, or one effect line per part module).
GROUPS = collections.OrderedDict([
    ("career", [entry(t, N) for t in CURRENCY_BUTTONS] + [
        entry("CheatAddScience", 4 * N), entry("funds-add", 4 * N),
        entry("science-add", 4 * N), entry("rep-add", 4 * N)]),
    ("max", [entry("max-tech", N), entry("CheatTechnology", N),
             entry("max-facilities", N), entry("CheatFacilities", N),
             entry("max-xp", N), entry("CheatExperience", N)]),
    ("contracts", [entry("contract-generate", N), entry("contract-complete-or-accept", N),
                   entry("contract-cancel-or-decline", N), entry("contract-regenerate", N),
                   entry("contract-reset-weights", N), entry("contract-clear-current", N),
                   entry("contract-clear-finished", N)]),
    ("kerbal", [entry("kerbal-create", N)]),
    ("physics", [entry("physics-load", N)]),
    ("difficulty", [poll(k, CLICKS) for k in DIFFICULTY_KEYS]),
    ("console-ksc", [entry("console-funds", N), entry("funds-set", N), echo("Funds set to", N),
                     entry("console-science", N), entry("science-set", N), echo("Science set to", N),
                     entry("console-reputation", N), entry("rep-set", N),
                     echo("Reputation set to", N)]),
    ("toggles", [row for t, k in TOGGLES for row in (entry(t, CLICKS), poll(k, CLICKS))]
     + [entry("hack-gravity-toggle", CLICKS), poll("HackGravity.hacked", None)]),
    ("gravity", [entry("hack-gravity-slider", None), entry("hack-gravity-set", None),
                 entry("hack-gravity-reset", N), poll("HackGravity.gravityFactor", None)]),
    ("heat", [entry("heat-generation", CLICKS)]),
    ("comets", [entry(t, N) for t in COMET_SPAWNS]
     + [entry("comet-colliders", CLICKS), entry("comet-kill", N)]),
    ("thrower", [entry("object-thrower-arm", CLICKS), poll("CheatsObjectThrower.isArmed", CLICKS)]),
    ("teleport", [entry("set-orbit", N), entry("set-position", N), entry("rendezvous", N),
                  entry("post-orbit-set", None), entry("console-rendezvous", N),
                  echo("rendezvousing with", None)]),
    ("repair", [entry("repair", N), entry("CheatRepair", None)]),
    ("legacy", [entry("legacy-science", None), entry("legacy-funds", None),
                entry("legacy-reputation", None), entry("legacy-experience", None),
                poll("DebugToolbar.toolbarShown", None)]),
    ("ms-early", [entry("max-progress", N), entry("CheatEarlyProgression", N),
                  entry("CheatProgression", 0)]),
    ("ms-full", [entry("max-progress", N), entry("CheatProgression", N),
                 entry("CheatEarlyProgression", 0)]),
    ("ms-b4d455", [entry("console-b4d455", N), entry("CheatTechnology", N),
                   entry("CheatFacilities", N), entry("CheatExperience", N),
                   entry("CheatEarlyProgression", N), entry("funds-set", N),
                   entry("science-set", N), entry("rep-set", N)]),
])

OPTIONAL_SEGMENTS = {"legacy"}
MILESTONE_WAYS = ["ms-early", "ms-full", "ms-b4d455", "legacy"]
CONTROL = "control"
PROGRESS_EVENTS = ["OnProgressReached", "OnProgressComplete", "OnProgressAchieved"]

# What the control session must contain, so a zero-line control means something:
# (label, event name, detail substring).
CONTROL_COVERAGE = [
    ("launch (a milestone completes)", "OnProgressComplete", ""),
    ("contract accepted", "Contract.onAccepted", ""),
    ("contract completed", "Contract.onCompleted", ""),
    ("science transmitted", "OnScienceChanged", "reason=ScienceTransmission"),
    ("facility upgraded", "OnKSCFacilityUpgraded", ""),
    ("tech bought", "OnTechnologyResearched", "result=Successful"),
    ("vessel recovered", "OnFundsChanged", "reason=VesselRecovery"),
]

# Debug actions the hooks cannot see, from the decompile; listed in the classification.
STATIC_ROWS = [
    ("object thrower: a throw", "not observable by an entry point",
     "CheatsObjectThrower.Update throws on mouse input; only the arm switch has a handler"),
    ("physics: Save", "entry point (not pressed)",
     "ScreenPhysics.OnSaveClicked writes the instance's physics file; hooked, not pressed"),
    ("console: targetv", "entry point (not pressed)",
     "ConsoleCommands.OnTargetvCommand only targets a vessel; hooked, not pressed"),
    ("fill resources", "does not exist",
     "no stock 1.12.5 debug screen fills resources (ScreenResources is heat and debug only)"),
    ("mod teleports (HyperEdit and similar)", "out of reach", "plan fallback: a disclosure line"),
]


# ------------------------------------------------------------------ parse

def parse_fields(text):
    out = {}
    for tok in text.split(" "):
        if "=" in tok:
            k, v = tok.split("=", 1)
            out[k] = v
    return out


def parse_log(lines):
    """Ordered records: ('mark', seg, ut) / ('spike', rec) / ('warn', text) / ('echo', text)
    / ('hooks', (applied, missing, failed))."""
    recs = []
    for line in lines:
        m = MARK_RE.search(line)
        if m:
            label = m.group(1)
            if label.startswith(SEG_PREFIX):
                recs.append(("mark", label[len(SEG_PREFIX):], m.group(2)))
            continue
        m = ECHO_RE.search(line)
        if m:
            recs.append(("echo", m.group(1)))
            continue
        m = SPIKE_LINE_RE.search(line)
        if not m:
            continue
        level, body = m.group(1), m.group(2)
        if level == "WARN":
            recs.append(("warn", body))
            continue
        h = HOOKS_RE.search(body)
        if h and body.startswith("hooks applied="):
            recs.append(("hooks", tuple(int(x) for x in h.groups())))
            continue
        b = BODY_RE.match(body)
        if not b:
            continue
        rest = b.group(3)
        cut = rest.find(" detail=")
        if cut >= 0:
            fields_text, detail = rest[:cut], rest[cut + len(" detail="):]
        elif rest.startswith("detail="):
            fields_text, detail = "", rest[len("detail="):]
        else:
            fields_text, detail = rest, ""
        rec = parse_fields(fields_text)
        rec["kind"] = b.group(1)
        rec["seq"] = int(b.group(2))
        rec["detail"] = detail
        recs.append(("spike", rec))
    return recs


class Segment(object):
    def __init__(self, name, ut):
        self.name = name
        self.ut = ut
        self.spike = []
        self.echoes = collections.Counter()

    def of_kind(self, kind):
        return [r for r in self.spike if r["kind"] == kind]

    def observed(self, tool=None):
        return [r for r in self.of_kind("observed") if tool is None or r.get("tool") == tool]

    def polls(self, key=None):
        return [r for r in self.of_kind("poll") if key is None or r.get("tool") == key]


def segments(recs):
    """Split records at marks. Lines before the first mark go to segment 'pre'. A name
    marked twice gets a '#2' suffix, so a repeated group is never merged silently."""
    segs = [Segment("pre", "none")]
    seen = collections.Counter()
    for r in recs:
        if r[0] == "mark":
            seen[r[1]] += 1
            name = r[1] if seen[r[1]] == 1 else "%s#%d" % (r[1], seen[r[1]])
            segs.append(Segment(name, r[2]))
        elif r[0] == "spike":
            segs[-1].spike.append(r[1])
        elif r[0] == "echo":
            segs[-1].echoes[r[1]] += 1
    return segs


# ------------------------------------------------------------------ judge

def count_row(seg, row):
    kind, name, _ = row
    if kind == "entry":
        return len(seg.observed(name))
    if kind == "poll":
        return len(seg.polls(name))
    return seg.echoes.get(name, 0)


def judge(expected, actual):
    if expected is None:
        return "report"
    return "PASS" if actual == expected else "FAIL"


def toggle_values(seg, tool):
    vals = []
    for r in seg.observed(tool):
        m = re.match(r"^\((true|false)\)$", r["detail"])
        vals.append(m.group(1)[0] if m else "?")
    return "".join(vals)


def presses_table(segs, out):
    by_name = dict((s.name, s) for s in segs)
    rows_pass = rows_fail = 0
    missing = []
    out.append("## Presses per segment")
    out.append("")
    out.append("| segment | layer | tool or key | expected | lines | result | note |")
    out.append("|---|---|---|---:|---:|---|---|")
    for name, rows in GROUPS.items():
        if name in ("ms-early", "ms-full", "ms-b4d455"):
            continue  # judged in the milestone section
        seg = by_name.get(name)
        if seg is None:
            if name not in OPTIONAL_SEGMENTS:
                missing.append(name)
            out.append("| %s | - | (segment not marked) | - | - | %s | |" % (
                name, "skipped" if name in OPTIONAL_SEGMENTS else "FAIL"))
            continue
        for row in rows:
            actual = count_row(seg, row)
            res = judge(row[2], actual)
            if res == "PASS":
                rows_pass += 1
            elif res == "FAIL":
                rows_fail += 1
            note = ""
            if row[0] == "entry" and row[2] == CLICKS:
                note = "values " + (toggle_values(seg, row[1]) or "-")
            out.append("| %s | %s | %s | %s | %d | %s | %s |" % (
                name, row[0], row[1], "-" if row[2] is None else row[2], actual, res, note))
        expected_tools = set(r[1] for r in rows if r[0] == "entry")
        other = collections.Counter(r.get("tool") for r in seg.observed() if r.get("tool") not in expected_tools)
        if other:
            out.append("| %s | other | %s | 0 | %d | note | lines from tools this segment does not press |" % (
                name, ", ".join("%s=%d" % kv for kv in sorted(other.items())), sum(other.values())))
    out.append("")
    return rows_pass, rows_fail, missing


def scoped_events(seg):
    return [r for r in seg.of_kind("event") if r.get("scope", "none") != "none"]


def control_section(segs, out):
    seg = next((s for s in segs if s.name == CONTROL), None)
    out.append("## Control session (normal play)")
    out.append("")
    if seg is None:
        out.append("No `s9-seg-control` mark found.")
        out.append("")
        return "FAIL", "no control segment"
    observed = seg.observed()
    polls = seg.polls()
    in_scope = scoped_events(seg)
    cheating = [r for r in seg.of_kind("event") if "reason=Cheating" in r["detail"]]
    out.append("| Measure | Value | Pass number | Result |")
    out.append("|---------|-------|-------------|--------|")
    for label, value in (("observed lines", len(observed)), ("poll changes", len(polls)),
                         ("events inside a debug scope", len(in_scope)),
                         ("currency events with reason Cheating", len(cheating))):
        out.append("| %s | %d | 0 | %s |" % (label, value, "PASS" if value == 0 else "FAIL"))
    covered = 0
    for label, ev, sub in CONTROL_COVERAGE:
        hit = [r for r in seg.of_kind("event") if r.get("entry") == ev and sub in r["detail"]]
        covered += 1 if hit else 0
        out.append("| control step: %s | %d events `%s%s` | at least 1 | %s |" % (
            label, len(hit), ev, (" " + sub) if sub else "", "yes" if hit else "MISSING"))
    if observed:
        out.append("")
        out.append("Observed lines in the control session (each one is a false positive):")
        for r in observed[:20]:
            out.append("- seq %d %s %s" % (r["seq"], r.get("tool"), r["detail"][:120]))
    out.append("")
    zero = not observed and not polls and not in_scope and not cheating
    verdict = "PASS" if zero and covered == len(CONTROL_COVERAGE) else ("PARTIAL" if zero else "FAIL")
    summary = "observed=%d poll=%d inScope=%d cheatingReason=%d steps=%d/%d" % (
        len(observed), len(polls), len(in_scope), len(cheating), covered, len(CONTROL_COVERAGE))
    return verdict, summary


def real_completion_kinds(segs):
    """Progress event names fired per node outside every debug scope, in the control session."""
    kinds = collections.defaultdict(set)
    for seg in segs:
        if seg.name != CONTROL:
            continue
        for r in seg.of_kind("event"):
            if r.get("entry") in PROGRESS_EVENTS and r.get("scope", "none") == "none":
                m = re.search(r"node=(\S+)", r["detail"])
                if m:
                    kinds[m.group(1)].add(r["entry"])
    return kinds


def milestone_section(segs, out):
    by_name = dict((s.name, s) for s in segs)
    real = real_completion_kinds(segs)
    real_union = set()
    for v in real.values():
        real_union |= v
    out.append("## Milestones completed outside a flight")
    out.append("")
    out.append("Real completions in the control session fired: %s (nodes: %s)." % (
        ", ".join(sorted(real_union)) or "none",
        ", ".join("%s[%s]" % (k, "+".join(sorted(v))) for k, v in sorted(real.items())) or "none"))
    out.append("")
    out.append("| way | entry lines | expected | result | nodes newly completed | already complete | "
               "first UT | events in scope (Reached / Complete / Achieved) | nodes with no OnProgressComplete |")
    out.append("|---|---|---|---|---:|---:|---|---|---:|")
    results = {}
    details = []
    for way in MILESTONE_WAYS:
        seg = by_name.get(way)
        if seg is None:
            out.append("| %s | (segment not marked) | - | %s | - | - | - | - | - |" % (
                way, "skipped" if way in OPTIONAL_SEGMENTS else "FAIL"))
            if way not in OPTIONAL_SEGMENTS:
                results[way] = "FAIL"
            continue
        rows = GROUPS[way]
        parts, ok = [], True
        for row in rows:
            actual = count_row(seg, row)
            res = judge(row[2], actual)
            ok = ok and res != "FAIL"
            parts.append("%s=%d" % (row[1], actual))
        exp = ", ".join("%s=%s" % (r[1], "-" if r[2] is None else r[2]) for r in rows)
        new_nodes, already = [], 0
        first_ut = None
        for r in seg.observed("CheatComplete"):
            m = re.search(r"node=(\S+) alreadyComplete=(\w+)", r["detail"])
            if not m:
                continue
            if m.group(2) == "false":
                if m.group(1) not in new_nodes:
                    new_nodes.append(m.group(1))
                if first_ut is None:
                    first_ut = r.get("ut")
            else:
                already += 1
        ev_nodes = collections.defaultdict(set)
        ev_count = collections.Counter()
        for r in scoped_events(seg):
            if r.get("entry") in PROGRESS_EVENTS:
                ev_count[r["entry"]] += 1
                m = re.search(r"node=(\S+)", r["detail"])
                if m:
                    ev_nodes[m.group(1)].add(r["entry"])
        no_complete = [n for n in new_nodes if "OnProgressComplete" not in ev_nodes.get(n, set())]
        res = "PASS" if ok else "FAIL"
        results[way] = res
        out.append("| %s | %s | %s | %s | %d | %d | %s | %d / %d / %d | %d |" % (
            way, ", ".join(parts), exp, res, len(new_nodes), already, first_ut or "-",
            ev_count["OnProgressReached"], ev_count["OnProgressComplete"],
            ev_count["OnProgressAchieved"], len(no_complete)))
        details.append((way, new_nodes, ev_nodes, no_complete))
    out.append("")
    for way, new_nodes, ev_nodes, no_complete in details:
        out.append("Nodes newly completed by %s (%d): %s" % (
            way, len(new_nodes), " ".join("%s[%s]" % (n, "+".join(sorted(ev_nodes.get(n, set()))) or "no event")
                                          for n in new_nodes) or "none"))
        if no_complete:
            out.append("Of these, without an in-scope OnProgressComplete: %s" % " ".join(no_complete))
        out.append("")
    return results


def classification(segs, out):
    """Every hooked tool and polled switch: by entry point, by polling only, or not seen."""
    obs_tools = collections.defaultdict(lambda: [0, set()])
    poll_keys = collections.Counter()
    for seg in segs:
        for r in seg.observed():
            t = obs_tools[r.get("tool")]
            t[0] += 1
            t[1].add(r.get("layer"))
        for r in seg.polls():
            poll_keys[r.get("tool")] += 1
    toggle_key = dict(TOGGLES)
    out.append("## Classification of each tool")
    out.append("")
    out.append("| tool | class | entry lines | poll changes | note |")
    out.append("|---|---|---:|---:|---|")
    listed = set()
    for rows in GROUPS.values():
        for kind, name, _ in rows:
            if kind != "entry" or name in listed:
                continue
            listed.add(name)
            n, layers = obs_tools.get(name, [0, set()])
            key = toggle_key.get(name)
            pc = poll_keys.get(key, 0) if key else 0
            if n:
                cls = "entry point (%s)" % ",".join(sorted(layers))
            elif pc:
                cls = "polling only (within 1 s)"
            else:
                cls = "not seen in this run"
            out.append("| %s | %s | %d | %d | %s |" % (name, cls, n, pc, ("switch " + key) if key else ""))
    for key in DIFFICULTY_KEYS + ["HackGravity.gravityFactor", "HackGravity.hacked",
                                  "CheatsObjectThrower.isArmed", "DebugToolbar.toolbarShown"]:
        if key in listed:
            continue
        listed.add(key)
        pc = poll_keys.get(key, 0)
        cls = "polling only (within 1 s)" if pc else "not seen in this run"
        out.append("| %s | %s | 0 | %d | polled switch, no entry point hooked |" % (key, cls, pc))
    for name, cls, note in STATIC_ROWS:
        out.append("| %s | %s | - | - | decompile: %s |" % (name, cls, note))
    out.append("")


def inventory_section(segs, out):
    out.append("## Debug objects per scene (inventory lines)")
    out.append("")
    seen = []
    for seg in segs:
        for r in seg.of_kind("inventory"):
            if r["detail"] not in seen:
                seen.append(r["detail"])
    for d in seen:
        out.append("- " + d)
    if not seen:
        out.append("- none")
    out.append("")


def summarize_lines(lines):
    recs = parse_log(lines)
    segs = segments(recs)
    hooks = [r[1] for r in recs if r[0] == "hooks"]
    warns = [r[1] for r in recs if r[0] == "warn"]
    out = ["# S9 summary", ""]
    out.append("Segments: " + ", ".join("%s(%d)" % (s.name, len(s.spike)) for s in segs))
    out.append("")
    stray = [(s.name, r) for s in segs if s.name == "pre" or s.name.startswith("idle")
             for r in s.observed()]
    if stray:
        out.append("Observed lines outside every press segment (between saves or before the first mark):")
        for name, r in stray[:20]:
            out.append("- %s seq %d %s %s" % (name, r["seq"], r.get("tool"), r["detail"][:100]))
        out.append("")
    if warns:
        out.append("WARN lines:")
        for w in warns[:20]:
            out.append("- " + w)
        out.append("")
    inventory_section(segs, out)
    rows_pass, rows_fail, missing = presses_table(segs, out)
    ctl, ctl_summary = control_section(segs, out)
    ms = milestone_section(segs, out)
    classification(segs, out)
    if hooks:
        a, m, f = hooks[-1]
        hv = "PASS" if (a, m, f) == (HOOK_COUNT, 0, 0) else "FAIL"
        out.append("HOOKS: %s applied=%d missing=%d failed=%d (expected %d)" % (hv, a, m, f, HOOK_COUNT))
    else:
        out.append("HOOKS: FAIL no 'hooks applied=' line (spike not armed?)")
    pv = "PASS" if rows_fail == 0 and not missing else "FAIL"
    out.append("PRESSES: %s rows=%d fail=%d missingSegments=%s" % (
        pv, rows_pass + rows_fail, rows_fail, ",".join(missing) or "none"))
    out.append("CONTROL: %s %s" % (ctl, ctl_summary))
    mv = "PASS" if ms and all(v == "PASS" for v in ms.values()) else "FAIL"
    out.append("MILESTONES: %s %s" % (mv, " ".join("%s=%s" % kv for kv in sorted(ms.items()))))
    return "\n".join(out)


# ------------------------------------------------------------------ stage

def stage(src, dst):
    src = os.path.abspath(src)
    dst = os.path.abspath(dst)
    parts = [p.lower() for p in re.split(r"[\\/]+", dst)]
    if "kerbal space program" in parts:
        raise SystemExit("refused: %s is inside a 'Kerbal Space Program' folder (the dev install)" % dst)
    if os.path.basename(os.path.dirname(dst)).lower() != "saves":
        raise SystemExit("refused: %s is not directly inside a 'saves' folder" % dst)
    if os.path.exists(dst):
        raise SystemExit("refused: %s already exists" % dst)
    if not os.path.isfile(os.path.join(src, "persistent.sfs")):
        raise SystemExit("refused: %s has no persistent.sfs" % src)
    shutil.copytree(src, dst, symlinks=True)
    with io.open(os.path.join(dst, "persistent.sfs"), "rb") as f:
        digest = hashlib.sha256(f.read()).hexdigest()
    return "staged %s from %s sha256=%s" % (dst, src, digest)


# ------------------------------------------------------------------ main

def main(argv):
    if argv and argv[0] == "--selftest":
        return selftest()
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd")
    s = sub.add_parser("stage")
    s.add_argument("--from", dest="src", required=True)
    s.add_argument("--to", dest="dst", required=True)
    m = sub.add_parser("summarize")
    m.add_argument("log")
    m.add_argument("--out")
    args = ap.parse_args(argv)
    if args.cmd == "stage":
        print(stage(args.src, args.dst))
        return 0
    if args.cmd == "summarize":
        with io.open(args.log, encoding="utf-8", errors="replace") as f:
            text = summarize_lines(f.read().splitlines())
        print(text)
        if args.out:
            with io.open(args.out, "w", encoding="utf-8", newline="\n") as f:
                f.write(text + "\n")
        return 0
    ap.print_help()
    return 2


# ------------------------------------------------------------------ selftest

_SEQ = [0]


def _line(kind, tool="-", layer="ui", entry_="X.Y", scope="none", depth=0, detail="", ut="100", scene="SPACECENTER"):
    _SEQ[0] += 1
    return ("[LOG 12:00:00.000] [Parsek][INFO][Spike.S9] %s: seq=%d tool=%s layer=%s entry=%s scope=%s "
            "depth=%d frame=%d ut=%s scene=%s detail=%s" % (
                kind, _SEQ[0], tool, layer, entry_, scope, depth, _SEQ[0] * 10, ut, scene, detail))


def _mark(seg):
    return "[LOG 12:00:00.000] [Parsek][INFO][TestCommands] MISSIONMARK label=%s%s ut=100" % (SEG_PREFIX, seg)


def _press(tool, n, layer="ui", detail="()", nested=()):
    out = []
    for _ in range(n):
        out.append(_line("observed", tool, layer, "A." + tool, tool, 1, detail))
        for t, lay in nested:
            out.append(_line("observed", t, lay, "B." + t, tool, 2, "(1)"))
    return out


def _full_log(drop=None, extra_control=None, toggle_clicks=CLICKS):
    L = ["[LOG 12:00:00.000] [Parsek][INFO][Spike.S9] hooks applied=85 missing=0 failed=0",
         _line("inventory", "-", "poll", "inventory", detail="scene=SPACECENTER DebugToolbar=0 ConsoleCommands=1")]
    for name, rows in GROUPS.items():
        if name == drop or name in OPTIONAL_SEGMENTS:
            continue
        L.append(_mark(name))
        for kind, tool, exp in rows:
            n = exp if exp is not None else 1
            if kind == "entry":
                if tool in dict(TOGGLES) or tool == "hack-gravity-toggle":
                    n = toggle_clicks
                    for i in range(n):
                        L.append(_line("observed", tool, "toggle", "A." + tool, tool, 1,
                                       "(true)" if i % 2 == 0 else "(false)"))
                    continue
                if tool == "CheatEarlyProgression" and n:
                    for _ in range(n):
                        L.append(_line("observed", tool, "effect", "P.CEP", "max-progress", 2, "()"))
                        L.append(_line("observed", "CheatComplete", "effect", "PN.CC", "max-progress", 3,
                                       "node=FirstLaunch alreadyComplete=%s" % ("false" if _ == 0 else "true")))
                    if name == "ms-early":
                        L.append(_line("event", "-", "event", "OnProgressReached", "max-progress", 3,
                                       "node=FirstLaunch"))
                        L.append(_line("event", "-", "event", "OnProgressComplete", "max-progress", 3,
                                       "node=FirstLaunch"))
                    continue
                L.extend(_press(tool, n, "ui"))
            elif kind == "poll":
                for i in range(n):
                    L.append(_line("poll", tool, "poll", tool, detail="old=false new=true"))
            else:
                for i in range(n):
                    L.append("[LOG 12:00:00.000] [DebugConsole]: %s 1." % tool)
    L.append(_mark("idle"))
    L.append(_line("rebaseline", "-", "poll", "rebaseline", detail="reason=game changes=none"))
    L.append(_mark(CONTROL))
    L.append(_line("event", "-", "event", "OnProgressReached", detail="node=FirstLaunch"))
    L.append(_line("event", "-", "event", "OnProgressComplete", detail="node=FirstLaunch"))
    L.append(_line("event", "-", "event", "Contract.onAccepted", detail="contract=X id=1"))
    L.append(_line("event", "-", "event", "Contract.onCompleted", detail="contract=X id=1"))
    L.append(_line("event", "-", "event", "OnScienceChanged", detail="value=3 reason=ScienceTransmission"))
    L.append(_line("event", "-", "event", "OnKSCFacilityUpgraded", detail="facility=SpaceCenter/LaunchPad level=1"))
    L.append(_line("event", "-", "event", "OnTechnologyResearched", detail="tech=basicRocketry result=Successful"))
    L.append(_line("event", "-", "event", "OnFundsChanged", detail="value=250000 reason=VesselRecovery"))
    L.extend(extra_control or [])
    return L


class SelfTest(unittest.TestCase):
    def test_parse_line_and_fields(self):
        recs = parse_log([_line("observed", "max-tech", "ui", "CheatsCareer.OnMaxTechClick", "max-tech", 1,
                                "args with spaces=1 (x)")])
        self.assertEqual(1, len(recs))
        r = recs[0][1]
        self.assertEqual("observed", r["kind"])
        self.assertEqual("max-tech", r["tool"])
        self.assertEqual("1", r["depth"])
        self.assertEqual("args with spaces=1 (x)", r["detail"])

    def test_segments_and_repeat_suffix(self):
        segs = segments(parse_log([_mark("career"), _line("observed", "x"), _mark("career"),
                                   _line("observed", "y")]))
        self.assertEqual(["pre", "career", "career#2"], [s.name for s in segs])
        self.assertEqual("y", segs[2].spike[0]["tool"])

    def test_full_run_passes(self):
        text = summarize_lines(_full_log())
        self.assertIn("HOOKS: PASS applied=85 missing=0 failed=0", text)
        self.assertIn("PRESSES: PASS", text)
        self.assertIn("CONTROL: PASS observed=0 poll=0 inScope=0 cheatingReason=0 steps=7/7", text)
        self.assertIn("MILESTONES: PASS", text)
        self.assertIn("| toggles | entry | no-crash-damage | 6 | 6 | PASS | values tftftf |", text)
        self.assertIn("Nodes newly completed by ms-early (1): FirstLaunch[OnProgressComplete+OnProgressReached]", text)
        self.assertIn("| Difficulty.AllowStockVessels | polling only (within 1 s) | 0 | 6 |", text)

    def test_extra_press_fails_and_missing_segment_fails(self):
        log = _full_log(drop="kerbal", toggle_clicks=CLICKS + 1)
        text = summarize_lines(log)
        self.assertIn("| toggles | entry | no-crash-damage | 6 | 7 | FAIL |", text)
        self.assertIn("missingSegments=kerbal", text)
        self.assertIn("PRESSES: FAIL", text)

    def test_control_false_positive_fails(self):
        bad = [_line("observed", "funds-add", "currency", "Funding.AddFunds", "funds-add", 1,
                     "(5, Cheating)")]
        text = summarize_lines(_full_log(extra_control=bad))
        self.assertIn("CONTROL: FAIL observed=1", text)
        self.assertIn("| observed lines | 1 | 0 | FAIL |", text)

    def test_control_missing_step_is_partial(self):
        log = [l for l in _full_log() if "OnKSCFacilityUpgraded" not in l]
        self.assertIn("CONTROL: PARTIAL", summarize_lines(log))

    def test_other_tools_in_segment_are_reported(self):
        log = _full_log()
        i = log.index(_mark("kerbal"))
        log.insert(i + 1, _line("observed", "CheatRepair", "effect", "M.CR", "kerbal-create", 2, "()"))
        text = summarize_lines(log)
        self.assertIn("| kerbal | other | CheatRepair=1 | 0 | 1 | note |", text)

    def test_warn_and_echo_parsing(self):
        recs = parse_log(["[LOG 1] [Parsek][WARN][Spike.S9] hook missing: X.Y tool=t layer=ui",
                          "[LOG 1] [DebugConsole]: Funds set to 1000000."])
        self.assertEqual(("warn", "hook missing: X.Y tool=t layer=ui"), recs[0])
        self.assertEqual(("echo", "Funds set to"), recs[1])

    def test_stage_refuses_unsafe_targets(self):
        root = tempfile.mkdtemp(prefix="s9-stage-")
        try:
            src = os.path.join(root, "src")
            os.makedirs(src)
            with io.open(os.path.join(src, "persistent.sfs"), "w", encoding="utf-8") as f:
                f.write("GAME\n{\n}\n")
            saves = os.path.join(root, "automation", "spike-r0", "saves")
            os.makedirs(saves)
            self.assertIn("staged", stage(src, os.path.join(saves, "SPIKE-S9-PRESS")))
            with self.assertRaises(SystemExit):
                stage(src, os.path.join(saves, "SPIKE-S9-PRESS"))
            with self.assertRaises(SystemExit):
                stage(src, os.path.join(root, "elsewhere", "X"))
            dev = os.path.join(root, "Kerbal Space Program", "saves")
            os.makedirs(dev)
            with self.assertRaises(SystemExit):
                stage(src, os.path.join(dev, "X"))
            self.assertFalse(os.path.exists(os.path.join(dev, "X")))
        finally:
            shutil.rmtree(root)

    # Source sync: the tools, line kinds and the mark literal this script keys on.
    def _src(self, *rel):
        here = os.path.dirname(os.path.abspath(__file__))
        path = os.path.join(here, "..", "..", "..", *rel)
        if not os.path.isfile(path):
            self.skipTest("C# source not beside this script")
        return io.open(path, encoding="utf-8").read()

    def test_every_tool_is_a_hook(self):
        text = self._src("Source", "Parsek", "InGameTests", "Spikes", "S9", "SpikeS9Hooks.cs")
        tools = set(re.findall(r'new SpikeS9Hook\("([^"]+)"', text))
        self.assertEqual(HOOK_COUNT, len(tools))
        for rows in GROUPS.values():
            for kind, name, _ in rows:
                if kind == "entry":
                    self.assertIn(name, tools)
        for t, _ in TOGGLES:
            self.assertIn(t, tools)

    def test_line_kinds_and_keys_match_the_csharp(self):
        fmt = self._src("Source", "Parsek", "InGameTests", "Spikes", "S9", "SpikeS9Format.cs")
        for kind in ("observed", "event", "poll", "baseline", "rebaseline", "inventory"):
            self.assertIn('= "%s";' % kind, fmt)
        self.assertIn('Append(": seq=")', fmt)
        self.assertIn('Append(" detail=")', fmt)
        cheats = self._src("Source", "Parsek", "InGameTests", "Spikes", "S9", "SpikeS9Cheats.cs")
        self.assertIn('"hooks applied={0} missing={1} failed={2}"', cheats)
        for key in ("CheatOptions.", "HackGravity.gravityFactor", "HackGravity.hacked",
                    "CheatsObjectThrower.isArmed", "DebugToolbar.toolbarShown"):
            self.assertIn('"%s' % key, cheats)
        for key in DIFFICULTY_KEYS[:-1]:
            self.assertIn('snap["%s"]' % key, cheats)
        for ev in PROGRESS_EVENTS + ["Contract.onAccepted", "Contract.onCompleted", "OnKSCFacilityUpgraded",
                                     "OnTechnologyResearched", "OnFundsChanged", "OnScienceChanged"]:
            self.assertIn('EventLine("%s"' % ev, cheats)
        self.assertIn('"node=" + node + " alreadyComplete="', cheats)

    def test_mark_literal_matches_the_seam(self):
        text = self._src("Source", "Parsek", "TestCommands", "TestCommandMissionMark.cs")
        self.assertIn('"MISSIONMARK label="', text)
        self.assertIn('" ut="', text)
        self.assertIn('Tag = "TestCommands"', text)


def selftest():
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(SelfTest)
    result = unittest.TextTestRunner(verbosity=1).run(suite)
    print("SELFTEST PASS" if result.wasSuccessful() else "SELFTEST FAIL")
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
