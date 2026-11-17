#!/usr/bin/env python3
"""Spike S4a: stage the spike saves, and turn the spike's CSVs and log into report cells.

Spike branch only (spike/mp-r0, never merged). Plan: docs/dev/plans/multiplayer/
r0-spikes.md, "S4a Probe: where the KSC is and what reads it". Run sheet: PC-RUN.md.

Subcommands:
  stage --from <save dir> --to <instance>/saves/<NAME> [--allow-other-sites true|false]
      Copy a save folder into the spike instance and set its AllowOtherLaunchSites.
      Writes ONLY into --to (refused when it exists, is not directly inside a folder
      named "saves", or lies inside a "Kerbal Space Program" folder).
  summarize <raw dir> [--out FILE]
      Read <raw dir>/event_order.csv, ksc_state.csv, footprints.csv, circles.csv (the
      spike's parsek-spike/S4a/ folder, copied) and print the report tables as Markdown:
      ways in, the three yes / no answers per way, the KSC numbers, footprints, the
      circle cache, and the field completeness check (the spike's pass rule).
  turnback --log <KSP.log> --before-hash H --after-hash H [--before-sfs F --after-sfs F]
      Check the turn-back session (R2-02) and print PASS / PARTIAL / FAIL with reasons.
      A hash change is classified by parsing both persistent.sfs files: same content
      re-serialized = PARTIAL; anything else lists the changed key PATHS (never values).
  --selftest
      Run the built-in tests.

Output carries no save content: spike save names, counts, coordinates of stock objects,
event names and key paths only. Stdlib only.
"""

import argparse
import collections
import csv
import hashlib
import io
import os
import re
import shutil
import sys
import tempfile
import unittest

SPIKE_TAG = "[Spike.S4a]"
KSC_PAD = (-0.0972, -74.5575)      # SpawnCollisionDetector.KscPadLatitude / Longitude
KSC_RUNWAY = (-0.0502, -74.7300)   # SpawnCollisionDetector.KscRunwayLatitude / Longitude
KSC_NEAR_DEG = 0.05                # a circle within this of the pad or runway is the KSC's own

ENTRY_EVENTS = collections.OrderedDict([
    ("MainMenu.OnLoadDialogPipelineFinished", "resume"),
    ("MainMenu.CreateAndStartGame", "new"),
    ("QuickSaveLoad.quickLoad", "quickload"),
    ("FlightDriver.RevertToLaunch", "revert"),
    ("FlightDriver.StartWithNewLaunch", "launch"),
])

# Fields the plan asks for, per kind (the pass rule: "every field above is in the CSV").
REQUIRED_FIELDS = collections.OrderedDict([
    ("game", ["AllowOtherLaunchSites", "save_folder"]),
    ("spacecenter", ["Latitude", "Longitude", "AreaRadius", "transform_lat", "transform_lon"]),
    ("pqscity:KSC", ["lat", "lon", "alt", "repositionRadial", "repositionRadiusOffset",
                     "reorientFinalAngle", "reorientToSphere", "launchSite", "spaceCenterFacility",
                     "lod", "transform_lat", "transform_lon", "transform_alt"]),
    ("decal_tangent", ["radius", "position", "angle", "heightMap", "absolute", "absoluteOffset",
                       "smoothHeight", "removeScatter"]),
    ("camera", ["pqsName", "initialPositionTransformName", "altitudeInitial"]),
    ("facility", ["name", "facilityName", "facilityTransformName", "IsLaunchFacility"]),
    ("facility_spawn", ["latitude", "longitude", "altitude", "latlonaltSet"]),
    ("launchsite", ["name", "requiresPOIVisit", "poiName", "IsStockLaunchSite"]),
    ("launchsite_spawn", ["latitude", "longitude", "altitude", "latlonaltSet"]),
])
CAMERA_SCENES = ("SPACECENTER",)


# ------------------------------------------------------------------ CSV reading

def read_csv(path):
    if not os.path.isfile(path):
        return []
    with io.open(path, encoding="utf-8", newline="") as f:
        return list(csv.DictReader(f))


def fnum(text):
    try:
        return float(text)
    except (TypeError, ValueError):
        return float("nan")


# ------------------------------------------------------------------ ways in

class Way(object):
    def __init__(self, kind, rows):
        self.kind = kind
        self.rows = rows

    @property
    def first_seq(self):
        return int(self.rows[0]["seq"])

    def events(self):
        return [r["event"] for r in self.rows]


def _target_scene(rows):
    for r in rows:
        if r["event"] == "onGameSceneLoadRequested":
            return r["detail"].replace("to=", "").strip()
    return None


def segment_ways(rows):
    """Split event rows (one session, seq order) into ways in.

    A way opens at an entry hook (ENTRY_EVENTS) or, when no entry is open, at a Game.Start
    (the seam's LoadGame has no stock entry hook: kind "seam"). It closes at the arrival
    in its target scene (onFlightReady for FLIGHT, onLevelWasLoadedGUIReady otherwise), at
    a turn-back row (kind becomes "<kind>+turn-back"), or where the next way opens.
    """
    ways = []
    cur = None
    for r in rows:
        ev = r["event"]
        opens = ENTRY_EVENTS.get(ev)
        if opens is None and ev == "Game.Start" and cur is None:
            opens = "seam"
        if opens is not None and not (cur is not None and ev == "Game.Start"):
            if cur is not None:
                ways.append(cur)
            cur = Way(opens, [r])
            continue
        if cur is None:
            continue
        cur.rows.append(r)
        if ev == "turn-back":
            cur.kind += "+turn-back"
            ways.append(cur)
            cur = None
            continue
        target = _target_scene(cur.rows)
        if target is None:
            continue
        if (target == "FLIGHT" and ev == "onFlightReady") or (
                target != "FLIGHT" and ev == "onLevelWasLoadedGUIReady"):
            ways.append(cur)
            cur = None
    if cur is not None:
        ways.append(cur)
    return ways


def _first(rows, pred):
    for i, r in enumerate(rows):
        if pred(r):
            return i
    return None


def answer_pqs_before_state_load(way):
    rows = way.rows
    i_setup = _first(rows, lambda r: r["event"] == "PQSCity.OnSetup" and r["detail"].startswith("name=KSC "))
    i_load = _first(rows, lambda r: r["event"] == "onGameStateLoad")
    if i_setup is None:
        return "no KSC OnSetup in this way"
    if i_load is None:
        return "no onGameStateLoad in this way"
    return "yes" if i_setup < i_load else "no"


def new_game_id(way):
    for r in way.rows:
        if r["event"] == "Game.Start":
            m = re.search(r"starting:(\S+)", r["detail"])
            if m:
                return m.group(1)
    return way.rows[-1]["game_id"]


def answer_game_at_request(way):
    """(answer, first event at which the new Game is current)."""
    rows = way.rows
    target = new_game_id(way)
    i_req = _first(rows, lambda r: r["event"] == "onGameSceneLoadRequested")
    if i_req is None:
        return ("no scene request", "")
    same_before = rows[0]["game_id"] == target
    if rows[i_req]["game_id"] == target:
        return ("yes (same Game object as before the way)" if same_before else "yes", rows[i_req]["event"])
    i_set = _first(rows[i_req:], lambda r: r["game_id"] == target)
    first = rows[i_req + i_set]["event"] if i_set is not None else "never in this way"
    return ("no", first)


def answer_persistent_write_before_start(way):
    rows = way.rows
    i_start = _first(rows, lambda r: r["event"] == "Game.Start")
    limit = len(rows) if i_start is None else i_start
    hits = [r for r in rows[:limit]
            if r["event"] == "GamePersistence.SaveGame" and '"persistent"' in r["detail"]]
    return "yes (%d)" % len(hits) if hits else "no"


def compact_events(events):
    out = []
    for e in events:
        if out and out[-1][0] == e:
            out[-1][1] += 1
        else:
            out.append([e, 1])
    return " > ".join(e if n == 1 else "%s x%d" % (e, n) for e, n in out)


def ways_table(rows):
    lines = ["| # | way | first seq | target | KSC OnSetup before onGameStateLoad | new Game current at onGameSceneLoadRequested | first event with it | persistent written before Game.Start |",
             "|---|---|---:|---|---|---|---|---|"]
    order = []
    for i, w in enumerate(segment_ways(rows), 1):
        ans, first = answer_game_at_request(w)
        lines.append("| %d | %s | %d | %s | %s | %s | %s | %s |" % (
            i, w.kind, w.first_seq, _target_scene(w.rows) or "-", answer_pqs_before_state_load(w),
            ans, first, answer_persistent_write_before_start(w)))
        order.append("%d. %s: %s" % (i, w.kind, compact_events(w.events())))
    return lines, order


# ------------------------------------------------------------------ KSC state

def index_state(rows):
    """{dump: {(kind, object): {field: value}}} plus {dump: scene}."""
    by_dump = collections.OrderedDict()
    scenes = {}
    for r in rows:
        d = r["session"] + "/" + r["dump"]
        scenes[d] = r["scene"]
        by_dump.setdefault(d, collections.OrderedDict()).setdefault(
            (r["kind"], r["object"]), {})[r["field"]] = r["value"]
    return by_dump, scenes


def missing_fields(objs, scene):
    missing = []
    for spec, fields in REQUIRED_FIELDS.items():
        kind, _, name = spec.partition(":")
        if kind == "camera" and scene not in CAMERA_SCENES:
            continue
        if kind == "spacecenter" and objs.get(("spacecenter", "Instance"), {}).get("present") == "false":
            continue
        matches = [fs for (k, o), fs in objs.items() if k == kind and (not name or o == name)]
        if not matches:
            missing.append("%s (no object)" % spec)
            continue
        for f in fields:
            if not any(f in fs for fs in matches):
                missing.append("%s.%s" % (spec, f))
    return missing


def ksc_numbers(objs):
    out = collections.OrderedDict()
    g = objs.get(("game", "current"), {})
    out["AllowOtherLaunchSites"] = g.get("AllowOtherLaunchSites", "-")
    for (k, o), fs in objs.items():
        if k == "spacecenter" and "Latitude" in fs:
            out["SpaceCenter lat / lon"] = "%s / %s" % (fs["Latitude"], fs["Longitude"])
            out["SpaceCenter AreaRadius"] = fs.get("AreaRadius", "-")
    ksc = objs.get(("pqscity", "KSC"), {})
    if ksc:
        out["PQSCity KSC lat / lon / alt"] = "%s / %s / %s" % (ksc.get("lat"), ksc.get("lon"), ksc.get("alt"))
        out["PQSCity KSC transform lat / lon / alt"] = "%s / %s / %s" % (
            ksc.get("transform_lat"), ksc.get("transform_lon"), ksc.get("transform_alt"))
        out["PQSCity KSC repositionRadial"] = ksc.get("repositionRadial", "-")
        out["PQSCity KSC repositionRadiusOffset"] = ksc.get("repositionRadiusOffset", "-")
        out["PQSCity KSC reorientFinalAngle"] = ksc.get("reorientFinalAngle", "-")
    decals = []
    for (k, o), fs in objs.items():
        if k not in ("decal_tangent", "decal"):
            continue
        lat, lon = fnum(fs.get("centre_lat")), fnum(fs.get("centre_lon"))
        near = abs(lat - KSC_PAD[0]) < 1.0 and abs(lon - KSC_PAD[1]) < 1.0
        if near:
            decals.append("%s %s radius=%s centre=%s,%s absolute=%s absoluteOffset=%s" % (
                k, o, fs.get("radius"), fs.get("centre_lat"), fs.get("centre_lon"),
                fs.get("absolute"), fs.get("absoluteOffset")))
    out["decals within 1 deg of the pad"] = "; ".join(decals) if decals else "none"
    cam = [fs for (k, o), fs in objs.items() if k == "camera" and "pqsName" in fs]
    if cam:
        out["camera pqsName / initialPositionTransformName / altitudeInitial"] = "%s / %s / %s" % (
            cam[0].get("pqsName"), cam[0].get("initialPositionTransformName"), cam[0].get("altitudeInitial"))
    sites = sorted(o for (k, o) in objs if k == "launchsite")
    poi = sorted(o for (k, o), fs in objs.items() if k == "launchsite" and fs.get("requiresPOIVisit") == "true")
    out["launch sites"] = "%d: %s" % (len(sites), ", ".join(sites))
    out["sites requiring a POI visit"] = ", ".join(poi) if poi else "none"
    facs = sorted(o for (k, o) in objs if k == "facility")
    out["facilities"] = "%d" % len(facs)
    return out


# ------------------------------------------------------------------ circles

def is_ksc_circle(row):
    name = row.get("site", "")
    if name in ("LaunchPad", "Runway", "KSC"):
        return True
    lat, lon = fnum(row.get("lat")), fnum(row.get("lon"))
    for p in (KSC_PAD, KSC_RUNWAY):
        if abs(lat - p[0]) <= KSC_NEAR_DEG and abs(lon - p[1]) <= KSC_NEAR_DEG:
            return True
    return False


def circles_table(rows):
    lines = ["| dump | scene | list | circles | sites | holds the KSC pad or runway |",
             "|---|---|---|---:|---|---|"]
    groups = collections.OrderedDict()
    for r in rows:
        groups.setdefault((r["session"] + "/" + r["dump"], r["scene"], r["list"]), []).append(r)
    for (dump, scene, lst), rs in groups.items():
        real = [r for r in rs if r.get("index", "") not in ("-1", "")]
        if not real:
            lines.append("| %s | %s | %s | 0 | %s | no |" % (dump, scene, lst, rs[0].get("site", "")))
            continue
        ksc = [r for r in real if is_ksc_circle(r)]
        lines.append("| %s | %s | %s | %d | %s | %s |" % (
            dump, scene, lst, len(real), ", ".join("%s:%s" % (r["site"], r["kind"]) for r in real),
            "yes (%s)" % ", ".join(r["site"] for r in ksc) if ksc else "no"))
    return lines


# ------------------------------------------------------------------ footprints

def footprints_table(rows):
    lines = ["| source | site | colliders (aabb) | lat min..max | lon min..max | len x / y / z m | dumps | max centre drift deg | tags |",
             "|---|---|---|---|---|---|---:|---:|---|"]
    groups = collections.OrderedDict()
    for r in rows:
        groups.setdefault((r["source"], r["site"]), []).append(r)
    for (src, site), rs in groups.items():
        ok = [r for r in rs if r.get("root") != "no-transform"]
        if not ok:
            lines.append("| %s | %s | no transform | - | - | - | %d | - | - |" % (src, site, len(rs)))
            continue
        r0 = ok[0]
        lats = [fnum(r["centre_lat"]) for r in ok]
        lons = [fnum(r["centre_lon"]) for r in ok]
        drift = max(max(lats) - min(lats), max(lons) - min(lons))
        lines.append("| %s | %s | %s (%s) | %s..%s | %s..%s | %s / %s / %s | %d | %.6f | %s |" % (
            src, site, r0["colliders"], r0["colliders_aabb"], _r(r0["lat_min"]), _r(r0["lat_max"]),
            _r(r0["lon_min"]), _r(r0["lon_max"]), _r(r0["len_x_m"], 1), _r(r0["len_y_m"], 1),
            _r(r0["len_z_m"], 1), len(ok), drift, r0.get("tags", "")))
    return lines


def _r(text, digits=5):
    v = fnum(text)
    return "nan" if v != v else ("%.*f" % (digits, v))


# ------------------------------------------------------------------ summarize

def summarize(raw):
    events = read_csv(os.path.join(raw, "event_order.csv"))
    state = read_csv(os.path.join(raw, "ksc_state.csv"))
    foot = read_csv(os.path.join(raw, "footprints.csv"))
    circles = read_csv(os.path.join(raw, "circles.csv"))
    out = ["## S4a summary (`%s`)" % os.path.basename(os.path.normpath(raw)), ""]
    out.append("Files: event_order %d rows, ksc_state %d, footprints %d, circles %d." % (
        len(events), len(state), len(foot), len(circles)))
    out.append("")

    sessions = collections.OrderedDict()
    for r in events:
        sessions.setdefault(r["session"], []).append(r)
    out.append("### Ways in")
    out.append("")
    all_kinds = []
    for s, rows in sessions.items():
        rows.sort(key=lambda r: int(r["seq"]))
        table, order = ways_table(rows)
        all_kinds += [w.kind for w in segment_ways(rows)]
        ksc_setups = [r["seq"] for r in rows if r["event"] == "PQSCity.OnSetup" and r["detail"].startswith("name=KSC ")]
        out.append("Session %s: %d events; PQSCity.OnSetup for KSC at seq %s." % (
            s, len(rows), ", ".join(ksc_setups) if ksc_setups else "none"))
        out.append("")
        out += table
        out.append("")
        out.append("Event order per way:")
        out.append("")
        out += order
        out.append("")
    wanted = ["resume", "new", "seam", "quickload", "revert"]
    seen = [k for k in wanted if any(x.split("+")[0] == k for x in all_kinds)]
    out.append("Ways covered: %d of 5 (%s); missing: %s." % (
        len(seen), ", ".join(seen) or "none", ", ".join(k for k in wanted if k not in seen) or "none"))
    out.append("")

    by_dump, scenes = index_state(state)
    out.append("### KSC state per dump")
    out.append("")
    all_missing = 0
    for d, objs in by_dump.items():
        out.append("#### %s (%s)" % (d, scenes[d]))
        out.append("")
        out.append("| measure | value |")
        out.append("|---|---|")
        for k, v in ksc_numbers(objs).items():
            out.append("| %s | %s |" % (k, v))
        miss = missing_fields(objs, scenes[d])
        all_missing += len(miss)
        out.append("| missing required fields | %s |" % (", ".join(miss) if miss else "none"))
        out.append("")
    out.append("Field completeness: %s." % ("PASS (every required field present in every dump)"
                                            if by_dump and all_missing == 0 else
                                            "FAIL (%d missing field cells, %d dumps)" % (all_missing, len(by_dump))))
    out.append("")
    out.append("### Footprints")
    out.append("")
    out += footprints_table(foot)
    out.append("")
    out.append("### Launch-site circle list and cache")
    out.append("")
    out += circles_table(circles)
    out.append("")
    return "\n".join(out)


# ------------------------------------------------------------------ turn-back

def parse_sfs(text):
    """ConfigNode text -> Counter of (path, key, value). Path is node names joined by '/',
    with a per-parent index so two VESSEL nodes stay apart."""
    entries = collections.Counter()
    stack = []          # list of (name, child-counter)
    counters = [collections.Counter()]
    pending = None
    for raw in text.splitlines():
        line = raw.split("//", 1)[0].strip()
        if not line:
            continue
        if line == "{":
            name = pending if pending is not None else "?"
            idx = counters[-1][name]
            counters[-1][name] += 1
            stack.append("%s[%d]" % (name, idx))
            counters.append(collections.Counter())
            pending = None
            continue
        if line == "}":
            if stack:
                stack.pop()
                counters.pop()
            continue
        if "=" in line:
            k, v = line.split("=", 1)
            entries[("/".join(stack), k.strip(), _norm(v.strip()))] += 1
            pending = None
        else:
            pending = line
    return entries


def _norm(v):
    try:
        return repr(float(v))
    except ValueError:
        return v


def diff_sfs(a_text, b_text):
    a, b = parse_sfs(a_text), parse_sfs(b_text)
    only_a = a - b
    only_b = b - a
    paths = collections.Counter()
    for (p, k, _), n in list(only_a.items()) + list(only_b.items()):
        paths[re.sub(r"\[\d+\]", "", p) + "." + k] += n
    return (not only_a and not only_b), paths


def check_turnback(log_lines, before_hash, after_hash, before_text=None, after_text=None):
    reasons = []
    fail = False
    partial = False
    i_tb = next((i for i, l in enumerate(log_lines) if SPIKE_TAG + " turn-back scene=" in l), None)
    if i_tb is None:
        return "FAIL", ["no '[Spike.S4a] turn-back scene=' line: the prefix never fired"]
    m = re.search(r"turn-back scene=(\S+)", log_lines[i_tb])
    scene = m.group(1) if m else "?"
    reasons.append("turn-back fired in scene %s" % scene)
    if scene != "MAINMENU":
        fail = True
        reasons.append("FAIL: turn-back was not at the main menu")
    done = next((l for l in log_lines[i_tb:] if SPIKE_TAG + " turn-back done" in l), "")
    if "message=shown" in done:
        reasons.append("screen message accepted by stock")
    else:
        reasons.append("screen message %s (check the screenshot)" % ("dropped" if done else "line missing"))
    if "currentGame=null" not in done:
        fail = True
        reasons.append("FAIL: HighLogic.CurrentGame was not null after the turn-back")
    i_next_start = next((i for i in range(i_tb + 1, len(log_lines))
                         if "ev=Game.Start" in log_lines[i] and SPIKE_TAG in log_lines[i]), None)
    window = log_lines[i_tb + 1:i_next_start if i_next_start is not None else len(log_lines)]
    leaked = [l for l in window if "Scene Change : From" in l]
    if leaked:
        fail = True
        reasons.append("FAIL: a scene loaded after the turn-back: %s" % leaked[0].strip()[-80:])
    else:
        reasons.append("no scene loaded after the turn-back")
    if i_next_start is None:
        fail = True
        reasons.append("FAIL: no second save was started afterwards (the main menu check is missing)")
    else:
        after = log_lines[i_next_start:]
        ok = any("Scene Change : From MAINMENU to" in l for l in after)
        if ok:
            reasons.append("a second save then loaded from the same main menu")
        else:
            fail = True
            reasons.append("FAIL: the second save never left the main menu")
    if (before_hash or "").strip().lower() == (after_hash or "").strip().lower() and before_hash:
        reasons.append("persistent.sfs hash unchanged")
    else:
        if before_text is None or after_text is None:
            fail = True
            reasons.append("FAIL: persistent.sfs hash changed and no files were given to classify it")
        else:
            same, paths = diff_sfs(before_text, after_text)
            if same:
                partial = True
                reasons.append("PARTIAL: hash changed but the parsed content is equal (stock re-serialized the save)")
            else:
                fail = True
                top = ", ".join("%s (%d)" % (p, n) for p, n in paths.most_common(30))
                reasons.append("FAIL: content changed at %d key paths: %s" % (len(paths), top))
    verdict = "FAIL" if fail else ("PARTIAL" if partial else "PASS")
    return verdict, reasons


# ------------------------------------------------------------------ stage

def stage(src, dst, allow=None):
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
    sfs = os.path.join(dst, "persistent.sfs")
    if allow is not None:
        with io.open(sfs, "rb") as f:
            data = f.read()
        text = data.decode("utf-8")
        new, n = set_allow_other_sites(text, allow)
        if n != 1:
            raise SystemExit("persistent.sfs holds %d AllowOtherLaunchSites lines (expected 1); copy left at %s" % (n, dst))
        with io.open(sfs, "wb") as f:
            f.write(new.encode("utf-8"))
    with io.open(sfs, "rb") as f:
        digest = hashlib.sha256(f.read()).hexdigest()
    return "staged %s from %s AllowOtherLaunchSites=%s sha256=%s" % (
        dst, src, "unchanged" if allow is None else ("True" if allow else "False"), digest)


def set_allow_other_sites(text, allow):
    value = "True" if allow else "False"
    pattern = re.compile(r"^(\s*AllowOtherLaunchSites\s*=\s*)(\S+)(\s*)$", re.MULTILINE)
    return pattern.subn(lambda m: m.group(1) + value + m.group(3), text)


# ------------------------------------------------------------------ main

def parse_bool(text):
    t = text.strip().lower()
    if t in ("true", "1", "yes"):
        return True
    if t in ("false", "0", "no"):
        return False
    raise argparse.ArgumentTypeError("expected true or false, got %r" % text)


def main(argv):
    if argv and argv[0] == "--selftest":
        return selftest()
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd")
    s = sub.add_parser("stage")
    s.add_argument("--from", dest="src", required=True)
    s.add_argument("--to", dest="dst", required=True)
    s.add_argument("--allow-other-sites", type=parse_bool, default=None)
    m = sub.add_parser("summarize")
    m.add_argument("raw")
    m.add_argument("--out")
    t = sub.add_parser("turnback")
    t.add_argument("--log", required=True)
    t.add_argument("--before-hash", required=True)
    t.add_argument("--after-hash", required=True)
    t.add_argument("--before-sfs")
    t.add_argument("--after-sfs")
    args = ap.parse_args(argv)
    if args.cmd == "stage":
        print(stage(args.src, args.dst, args.allow_other_sites))
        return 0
    if args.cmd == "summarize":
        text = summarize(args.raw)
        print(text)
        if args.out:
            with io.open(args.out, "w", encoding="utf-8", newline="\n") as f:
                f.write(text + "\n")
        return 0
    if args.cmd == "turnback":
        with io.open(args.log, encoding="utf-8", errors="replace") as f:
            lines = f.read().splitlines()
        bt = io.open(args.before_sfs, encoding="utf-8", errors="replace").read() if args.before_sfs else None
        at = io.open(args.after_sfs, encoding="utf-8", errors="replace").read() if args.after_sfs else None
        verdict, reasons = check_turnback(lines, args.before_hash, args.after_hash, bt, at)
        for r in reasons:
            print("- " + r)
        print("TURN-BACK: " + verdict)
        return 0 if verdict != "FAIL" else 1
    ap.print_help()
    return 2


# ------------------------------------------------------------------ selftest

_EVT_HDR = "session,seq,frame,realtime_s,event,detail,scene,game_id,game_title,save_folder"


def _ev(seq, event, detail="", scene="MAINMENU", game="none", folder=""):
    return {"session": "S1", "seq": str(seq), "frame": str(seq * 10), "realtime_s": "1.0",
            "event": event, "detail": detail, "scene": scene, "game_id": game,
            "game_title": "", "save_folder": folder}


def _resume_rows(start):
    s = start
    return [
        _ev(s, "MainMenu.OnLoadDialogPipelineFinished", '<ConfigNode> "SPIKE-S4A-ON"'),
        _ev(s + 1, "GamePersistence.SaveGame", 'game:g2 "persistent" "SPIKE-S4A-ON" OVERWRITE', game="g2"),
        _ev(s + 2, "Game.Start", "starting:g2 startScene=SPACECENTER", game="g2", folder="SPIKE-S4A-ON"),
        _ev(s + 3, "HighLogic.LoadScene", "SPACECENTER", game="g2"),
        _ev(s + 4, "onGameSceneLoadRequested", "to=SPACECENTER", game="g2"),
        _ev(s + 5, "onLevelWasLoaded", "scene=SPACECENTER", scene="SPACECENTER", game="g3"),
        _ev(s + 6, "onGameStateLoad", "node", scene="SPACECENTER", game="g3"),
        _ev(s + 7, "PQSCity.OnSetup", "name=KSC lat=0 lon=0 alt=0", scene="SPACECENTER", game="g3"),
        _ev(s + 8, "onLevelWasLoadedGUIReady", "scene=SPACECENTER", scene="SPACECENTER", game="g3"),
    ]


class SelfTest(unittest.TestCase):
    def test_segment_resume_and_seam_and_quickload(self):
        rows = _resume_rows(1)
        rows += [
            _ev(20, "GamePersistence.LoadGame", '"persistent" "SPIKE-S4A-OFF" true false', scene="SPACECENTER", game="g3"),
            _ev(21, "Game.Start", "starting:g4 startScene=SPACECENTER", scene="SPACECENTER", game="g3"),
            _ev(22, "onGameSceneLoadRequested", "to=SPACECENTER", scene="SPACECENTER", game="g4"),
            _ev(23, "onLevelWasLoadedGUIReady", "scene=SPACECENTER", scene="SPACECENTER", game="g4"),
            _ev(30, "QuickSaveLoad.quickLoad", '"quicksave" "SPIKE-S4A-OFF"', scene="FLIGHT", game="g5"),
            _ev(31, "Game.Start", "starting:g6 startScene=FLIGHT", scene="FLIGHT", game="g6"),
            _ev(32, "onGameSceneLoadRequested", "to=FLIGHT", scene="FLIGHT", game="g6"),
            _ev(33, "onLevelWasLoadedGUIReady", "scene=FLIGHT", scene="FLIGHT", game="g6"),
            _ev(34, "onFlightReady", "", scene="FLIGHT", game="g6"),
        ]
        ways = segment_ways(rows)
        self.assertEqual(["resume", "seam", "quickload"], [w.kind for w in ways])
        self.assertEqual(9, len(ways[0].rows))
        self.assertEqual("onFlightReady", ways[2].rows[-1]["event"])

    def test_answers_for_resume(self):
        w = segment_ways(_resume_rows(1))[0]
        self.assertEqual("no", answer_pqs_before_state_load(w))
        self.assertEqual(("yes", "onGameSceneLoadRequested"), answer_game_at_request(w))
        self.assertEqual("yes (1)", answer_persistent_write_before_start(w))

    def test_answers_when_game_set_late_and_setup_first(self):
        rows = [
            _ev(1, "FlightDriver.RevertToLaunch", "", scene="FLIGHT", game="g7"),
            _ev(2, "PQSCity.OnSetup", "name=KSC lat=1 lon=2 alt=3", scene="FLIGHT", game="g7"),
            _ev(3, "onGameSceneLoadRequested", "to=FLIGHT", scene="FLIGHT", game="g7"),
            _ev(4, "onGameStateLoad", "node", scene="FLIGHT", game="g8"),
            _ev(5, "onFlightReady", "", scene="FLIGHT", game="g8"),
        ]
        w = segment_ways(rows)[0]
        self.assertEqual("revert", w.kind)
        self.assertEqual("yes", answer_pqs_before_state_load(w))
        self.assertEqual(("no", "onGameStateLoad"), answer_game_at_request(w))
        self.assertEqual("no", answer_persistent_write_before_start(w))

    def test_turnback_segment(self):
        rows = [
            _ev(1, "MainMenu.OnLoadDialogPipelineFinished", "", game="g1"),
            _ev(2, "Game.Start", "starting:g1 startScene=SPACECENTER", game="g1", folder="SPIKE-REFUSE-1"),
            _ev(3, "turn-back", "folder=SPIKE-REFUSE-1 message=shown", game="none", folder="SPIKE-REFUSE-1"),
        ] + [dict(r, seq=str(int(r["seq"]) + 10)) for r in _resume_rows(1)]
        ways = segment_ways(rows)
        self.assertEqual(["resume+turn-back", "resume"], [w.kind for w in ways])

    def test_compact_events(self):
        self.assertEqual("a > b x3 > a", compact_events(["a", "b", "b", "b", "a"]))

    def test_state_missing_fields_and_numbers(self):
        objs = collections.OrderedDict()
        objs[("game", "current")] = {"AllowOtherLaunchSites": "true", "save_folder": "X"}
        objs[("spacecenter", "SpaceCenter")] = {"Latitude": "-0.09", "Longitude": "-74.5", "AreaRadius": "3000",
                                                "transform_lat": "1", "transform_lon": "2"}
        ksc = dict((f, "1") for f in REQUIRED_FIELDS["pqscity:KSC"])
        objs[("pqscity", "KSC")] = ksc
        objs[("decal_tangent", "KSC_decal")] = dict((f, "1") for f in REQUIRED_FIELDS["decal_tangent"])
        objs[("decal_tangent", "KSC_decal")].update({"centre_lat": "-0.1", "centre_lon": "-74.6"})
        for kind in ("facility", "facility_spawn", "launchsite", "launchsite_spawn"):
            objs[(kind, "x")] = dict((f, "1") for f in REQUIRED_FIELDS[kind])
        self.assertEqual([], missing_fields(objs, "FLIGHT"))          # camera not required in flight
        self.assertEqual(["camera (no object)"], missing_fields(objs, "SPACECENTER"))
        del objs[("pqscity", "KSC")]["lod"]
        self.assertIn("pqscity:KSC.lod", missing_fields(objs, "FLIGHT"))
        nums = ksc_numbers(objs)
        self.assertEqual("-0.09 / -74.5", nums["SpaceCenter lat / lon"])
        self.assertIn("KSC_decal", nums["decals within 1 deg of the pad"])

    def test_circles(self):
        self.assertTrue(is_ksc_circle({"site": "LaunchPad", "lat": "0", "lon": "0"}))
        self.assertTrue(is_ksc_circle({"site": "x", "lat": "-0.0970", "lon": "-74.5570"}))
        self.assertFalse(is_ksc_circle({"site": "Desert_Launch_Site", "lat": "-6.5", "lon": "-144.0"}))
        rows = [{"session": "S", "dump": "d1", "scene": "SPACECENTER", "list": "cache", "index": "0",
                 "site": "Desert_Launch_Site", "kind": "Pad", "lat": "-6.5", "lon": "-144"},
                {"session": "S", "dump": "d1", "scene": "SPACECENTER", "list": "returned", "index": "-1",
                 "site": "null", "kind": "", "lat": "", "lon": ""}]
        text = "\n".join(circles_table(rows))
        self.assertIn("| S/d1 | SPACECENTER | cache | 1 | Desert_Launch_Site:Pad | no |", text)
        self.assertIn("| S/d1 | SPACECENTER | returned | 0 | null | no |", text)

    def test_parse_and_diff_sfs(self):
        a = "GAME\n{\n\tTitle = X\n\tPARAMETERS\n\t{\n\t\tv = 1.0\n\t}\n\tVESSEL\n\t{\n\t\tname = A\n\t}\n\tVESSEL\n\t{\n\t\tname = B\n\t}\n}\n"
        b = a.replace("v = 1.0", "v = 1")
        same, paths = diff_sfs(a, b)
        self.assertTrue(same)
        c = a.replace("name = B", "name = C")
        same, paths = diff_sfs(a, c)
        self.assertFalse(same)
        self.assertEqual({"GAME/VESSEL.name": 2}, dict(paths))

    def _log(self, scene="MAINMENU", leak=False, second=True, done="message=shown currentGame=null"):
        lines = ["[LOG 00:00:01.000]: [Parsek][INFO][Spike.S4a] event seq=5 frame=1 ev=Game.Start scene=MAINMENU",
                 "[LOG 00:00:01.001]: [Parsek][INFO][Spike.S4a] turn-back scene=%s folder=SPIKE-REFUSE-1" % scene,
                 "[LOG 00:00:01.002]: [Parsek][INFO][Spike.S4a] turn-back done %s saveFolderAfter=x" % done]
        if leak:
            lines.append("[LOG 00:00:02.000]: Scene Change : From MAINMENU to SPACECENTER (Async)")
        if second:
            lines.append("[LOG 00:00:09.000]: [Parsek][INFO][Spike.S4a] event seq=9 frame=2 ev=Game.Start scene=MAINMENU")
            lines.append("[LOG 00:00:09.500]: Scene Change : From MAINMENU to SPACECENTER (Async)")
        return lines

    def test_turnback_pass_partial_fail(self):
        self.assertEqual("PASS", check_turnback(self._log(), "AB", "ab")[0])
        a = "GAME\n{\n\tv = 1.0\n}\n"
        self.assertEqual("PARTIAL", check_turnback(self._log(), "aa", "bb", a, a.replace("1.0", "1"))[0])
        verdict, reasons = check_turnback(self._log(), "aa", "bb", a, a.replace("1.0", "2"))
        self.assertEqual("FAIL", verdict)
        self.assertTrue(any("GAME.v" in r for r in reasons))
        self.assertEqual("FAIL", check_turnback(self._log(leak=True), "a", "a")[0])
        self.assertEqual("FAIL", check_turnback(self._log(second=False), "a", "a")[0])
        self.assertEqual("FAIL", check_turnback(self._log(scene="SPACECENTER"), "a", "a")[0])
        self.assertEqual("FAIL", check_turnback(self._log(done="message=shown currentGame=set"), "a", "a")[0])
        self.assertEqual("FAIL", check_turnback(["nothing"], "a", "a")[0])

    def test_stage_copies_sets_flag_and_refuses_unsafe_targets(self):
        root = tempfile.mkdtemp(prefix="s4a-stage-")
        try:
            src = os.path.join(root, "src")
            os.makedirs(src)
            with io.open(os.path.join(src, "persistent.sfs"), "w", encoding="utf-8") as f:
                f.write("GAME\n{\n\tPARAMETERS\n\t{\n\t\tDifficulty\n\t\t{\n\t\t\tAllowOtherLaunchSites = True\n\t\t}\n\t}\n}\n")
            saves = os.path.join(root, "automation", "spike-r0", "saves")
            os.makedirs(saves)
            out = stage(src, os.path.join(saves, "SPIKE-S4A-OFF"), False)
            self.assertIn("AllowOtherLaunchSites=False", out)
            text = io.open(os.path.join(saves, "SPIKE-S4A-OFF", "persistent.sfs"), encoding="utf-8").read()
            self.assertIn("AllowOtherLaunchSites = False", text)
            with self.assertRaises(SystemExit):
                stage(src, os.path.join(saves, "SPIKE-S4A-OFF"), True)          # exists
            with self.assertRaises(SystemExit):
                stage(src, os.path.join(root, "elsewhere", "X"), True)          # not in saves
            dev = os.path.join(root, "Kerbal Space Program", "saves")
            os.makedirs(dev)
            with self.assertRaises(SystemExit):
                stage(src, os.path.join(dev, "X"), True)                        # dev install
            self.assertFalse(os.path.exists(os.path.join(dev, "X")))
        finally:
            shutil.rmtree(root)

    def test_set_allow_counts_lines(self):
        self.assertEqual(0, set_allow_other_sites("x = 1\n", True)[1])
        new, n = set_allow_other_sites("\t\tAllowOtherLaunchSites = False\r\n", True)
        self.assertEqual(1, n)
        self.assertEqual("\t\tAllowOtherLaunchSites = True\r\n", new)

    def test_summarize_end_to_end(self):
        root = tempfile.mkdtemp(prefix="s4a-sum-")
        try:
            with io.open(os.path.join(root, "event_order.csv"), "w", encoding="utf-8", newline="") as f:
                w = csv.DictWriter(f, fieldnames=_EVT_HDR.split(","))
                w.writeheader()
                for r in _resume_rows(1):
                    w.writerow(r)
            text = summarize(root)
            self.assertIn("| 1 | resume | 1 | SPACECENTER | no | yes | onGameSceneLoadRequested | yes (1) |", text)
            self.assertIn("Ways covered: 1 of 5 (resume)", text)
            self.assertIn("Field completeness: FAIL (0 missing field cells, 0 dumps)", text)
        finally:
            shutil.rmtree(root)

    def test_headers_match_the_csharp_writer(self):
        here = os.path.dirname(os.path.abspath(__file__))
        src = os.path.join(here, "..", "..", "..", "Source", "Parsek", "InGameTests", "Spikes", "S4",
                           "SpikeS4Format.cs")
        if not os.path.isfile(src):
            self.skipTest("C# source not beside this script")
        text = io.open(src, encoding="utf-8").read()

        def header(name):
            start = text.index("internal static readonly string[] %s =" % name)
            block = text[start:text.index("};", start)]
            return re.findall(r'"([^"]+)"', block)
        self.assertEqual(_EVT_HDR.split(","), header("EventHeader"))
        for name, needed in (("KscStateHeader", ["session", "dump", "scene", "kind", "object", "field", "value"]),
                             ("FootprintHeader", ["source", "site", "root", "colliders", "colliders_aabb",
                                                  "centre_lat", "centre_lon", "len_x_m", "tags"]),
                             ("CircleHeader", ["session", "dump", "scene", "list", "index", "site", "kind",
                                               "lat", "lon"])):
            cols = header(name)
            for c in needed:
                self.assertIn(c, cols, "%s lacks %s" % (name, c))

    def test_entry_events_match_the_csharp_hooks(self):
        here = os.path.dirname(os.path.abspath(__file__))
        src = os.path.join(here, "..", "..", "..", "Source", "Parsek", "InGameTests", "Spikes", "S4",
                           "SpikeS4EventOrder.cs")
        if not os.path.isfile(src):
            self.skipTest("C# source not beside this script")
        text = io.open(src, encoding="utf-8").read()
        hooks = set("%s.%s" % m for m in re.findall(r'T\(typeof\((\w+)\), "(\w+)"', text))
        for ev in list(ENTRY_EVENTS) + ["Game.Start", "GamePersistence.SaveGame"]:
            self.assertIn(ev, hooks)


def selftest():
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(SelfTest)
    result = unittest.TextTestRunner(verbosity=1).run(suite)
    print("SELFTEST PASS" if result.wasSuccessful() else "SELFTEST FAIL")
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
