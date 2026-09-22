#!/usr/bin/env python3
"""Phase 0 inventory scanner for the Parsek unit-test quality audit.

Static scan of Source/Parsek.Tests: one CSV row per test method plus a per-file
summary. No compilation, no execution.

The repo's test classes are flat (no inheritance, no xUnit fixtures) and every
test carries its attribute directly on the method, so a line-oriented regex scan
is accurate enough for review routing. Documented approximations:
  - nested helper classes are attributed to the enclosing class name seen last;
  - method bodies are brace-matched on string/comment-stripped text, not Roslyn;
  - sut_guess is class-minus-Tests (agents correct integration classes later).
"""

import argparse
import csv
import os
import re
import sys
from collections import defaultdict

ATTR_RE = re.compile(r"^\s*\[(?P<name>[A-Za-z]+)(?P<rest>.*)$")
CLASS_RE = re.compile(
    r"^\s*(?:public|internal|private|protected)?\s*"
    r"(?:sealed\s+|static\s+|abstract\s+|partial\s+)*class\s+(\w+)"
)
METHOD_RE = re.compile(
    r"^\s*(?:public|internal|protected)\s+"
    r"(?:static\s+|async\s+|virtual\s+|override\s+|new\s+)*"
    r"(?:void|Task|IEnumerator(?:<[^>]+>)?)\s+(\w+)\s*\("
)

ASSERT_RE = re.compile(r"\bAssert\.(\w+)\s*\(")
SOURCE_TEXT_RE = re.compile(r"SourceText|ReadAllText|ReadAllLines")
GUID_RE = re.compile(r"Guid\.NewGuid")
DATETIME_RE = re.compile(r"DateTime\.(?:Now|UtcNow)")
SLEEP_RE = re.compile(r"Thread\.Sleep")
MOQ_RE = re.compile(r"\bMock<|Mock\.Of|It\.IsAny")
SKIP_RE = re.compile(r"Skip\s*=")
INLINE_DATA_RE = re.compile(r"\[InlineData")
MEMBER_DATA_RE = re.compile(r"\[MemberData(?:\(\s*nameof\(([\w.]+)\))?")
EARLY_RETURN_RE = re.compile(r"\breturn\s*;")
SWALLOW_CATCH_RE = re.compile(r"catch\s*(?:\([^)]*\))?\s*\{\s*\}")
PROCESS_RE = re.compile(r"Process\.Start|\bpwsh\b|\bcmd\.exe\b|powershell")
TEMPFS_RE = re.compile(r"GetTempPath|CreateTemp|Path\.GetTempFileName|File\.Delete")
CULTURE_RE = re.compile(r"CultureInfo")
SHARED_SAVE_RE = re.compile(r"persistent\.sfs|SaveGame|test career|InjectAllRecordings")

# Routing clusters, most specific first. Matched against the file stem.
CLUSTER_RULES = [
    (r"^(Route|Logistics)", "logistics-route"),
    (r"^(Rewind|ReFly|Supersede|Tombstone|MergeJournal|Splitter|LoadTimeSweep)", "rewind-refly"),
    (r"^(Ghost|Playback|Watch|Audio|Gloops)", "ghost-playback"),
    (r"^(Map|Render|Polyline|Icon|OrbitLine|Tracer|Ksc|TrackingStation|Marker)", "map-render"),
    (r"^(Mission|MissionPresentation|MissionGroup|MissionVessel|MissionsWindow|Group)", "mission-groups"),
    (r"^(Ledger|GameAction|GameState|Recalc|Funds|Science|Reputation|Contracts|Strategies|Kerbals|Crew|Kerbal|Earnings|Career|KspState|Strategy)", "ledger-career"),
    (r"^(Recording|RecordingsTable|Session|Chain|Tree|Merge|Effective|Ers|Els|Committed|GroupHierarchy|Storage|Sidecar|Legacy|Switch|Discard|Overlap|Checkpoint)", "recording-tree"),
    (r"^(Quickload|SceneExit|StartDocked|RevertFlow|SaveLoad|Load|Rescue|Recovery)", "recording-tree"),
    (r"^(Flight|PartEvent|Background|Sampling|Split|Terminal|Atmosphere|Adaptive|Compound|Engine|Rcs|Fx|Variant|Explosion|Reentry|RuntimePolicy|Policy|SeedEvent|TrackSection|Loop|Zone|Deployment|PartState|Rails)", "recorder-events"),
    (r"^(Spawn|Vessel|Snapshot|Debris|Selective|Resource|Orphan|Deferred|IdentityLoss)", "spawn-vessel"),
    (r"^(Trajectory|Anchor|Relative|TwoBody|Ballistic|Kepler|Orbit|Warp|Descent|Atmospheric|Incomplete|Patched|Site|Reaim|Periodicity)", "trajectory-orbit"),
    (r"^(Analyzer|Analysis|Baseline|Invariant|Schema)", "analyzer"),
    (r"^(LogVal|LogContract|Logging|Diagnostic|Observability|ParsekKspLog|ParsekLog|TreeLog|RecorderState)", "logging"),
    (r"^(TestCommand|Seam|InGameTest|Batch|TestRunner|Autorun|Coroutine)", "harness-seam"),
    (r"^(AgentInstruction|CommittedBatchTally|MultiCategory|GrepAudit|.*Wiring|.*SourceGate|.*Contract|.*Gate)", "wiring-gates"),
    (r"^(Settings|ParsekUI|Timeline|Format|Dialog|UI|Window|Tooltip|Toolbar|SettingsWindow|SpawnControl|GroupPicker|SpawnWarning|SelectiveSpawnUI|Prefs|RecordingSection)", "ui-settings"),
    (r"^(Unfinished|Phase|Milestone|Bug|Issue|Regression|Fix|Feature|TimeJump|CrashCoalescer|LineBlink|Environment)", "legacy-bugfix"),
    (r"^(Gu|Files|File|Path|Config|Serialization|Codec|Proto|Node|Pannotations)", "io-serialization"),
]

# Directory overrides win over stem rules.
DIR_RULES = [
    ("Logistics", "logistics-route"),
    ("MapRender", "map-render"),
    ("Rendering", "map-render"),
    ("Analyzer", "analyzer"),
    ("LogValidation", "logging"),
    ("Harness", "harness-seam"),
    ("Generators", "generators"),
    ("Fixtures", "generators"),
]


def cluster_for(rel, stem):
    parts = rel.split("/")
    for p in parts[:-1]:
        for dirname, name in DIR_RULES:
            if p == dirname:
                return name
    for pat, name in CLUSTER_RULES:
        if re.match(pat, stem):
            return name
    return "catchall"


def tier_for(size_kb):
    if size_kb > 32:
        return "A"
    if size_kb >= 8:
        return "B"
    if size_kb >= 2:
        return "C"
    return "D"


def strip_code(text):
    """Blank out string/char literals and comments, preserving line structure."""
    out = []
    i, n = 0, len(text)
    state = "code"
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if state == "code":
            if c == "/" and nxt == "/":
                state = "line"
                out.append("  ")
                i += 2
                continue
            if c == "/" and nxt == "*":
                state = "block"
                out.append("  ")
                i += 2
                continue
            if c == '"':
                if nxt == '"':
                    out.append('""')
                    i += 2
                    continue
                state = "string"
                out.append(" ")
                i += 1
                continue
            if c == "'":
                state = "char"
                out.append(" ")
                i += 1
                continue
            out.append(c)
            i += 1
        elif state == "line":
            out.append("\n" if c == "\n" else " ")
            if c == "\n":
                state = "code"
            i += 1
        elif state == "block":
            if c == "*" and nxt == "/":
                state = "code"
                out.append("  ")
                i += 2
                continue
            out.append("\n" if c == "\n" else " ")
            i += 1
        elif state == "string":
            if c == "\\":
                out.append("  ")
                i += 2
                continue
            if c == '"':
                state = "code"
                out.append(" ")
                i += 1
                continue
            out.append("\n" if c == "\n" else " ")
            i += 1
        elif state == "char":
            if c == "\\":
                out.append("  ")
                i += 2
                continue
            if c == "'":
                state = "code"
                out.append(" ")
                i += 1
                continue
            out.append(" ")
            i += 1
    return "".join(out)


def extract_body(code_lines, start_idx):
    """Brace-match from start_idx on string/comment-stripped text."""
    depth = 0
    started = False
    body = []
    for i in range(start_idx, len(code_lines)):
        for ch in code_lines[i]:
            if not started:
                if ch == "{":
                    started = True
                    depth = 1
                continue
            if ch == "{":
                depth += 1
            elif ch == "}":
                depth -= 1
                if depth == 0:
                    return "\n".join(body), i
            body.append(ch)
    return "\n".join(body), len(code_lines) - 1


def side_effects(body_code, body_raw):
    tags = []
    if PROCESS_RE.search(body_raw):
        tags.append("process-spawn")
    if SHARED_SAVE_RE.search(body_raw):
        tags.append("shared-save")
    if DATETIME_RE.search(body_code):
        tags.append("wall-clock")
    if GUID_RE.search(body_code):
        tags.append("random")
    if SLEEP_RE.search(body_code):
        tags.append("sleep")
    if TEMPFS_RE.search(body_raw):
        tags.append("temp-fs")
    if CULTURE_RE.search(body_code):
        tags.append("culture")
    return ";".join(tags)


def scan_file(path, rel):
    with open(path, "r", encoding="utf-8-sig", errors="replace") as fh:
        raw = fh.read()
    raw_lines = raw.split("\n")
    code_lines = strip_code(raw).split("\n")
    tests = []
    pending = []
    class_stack = []
    depth = 0
    class_has_idisposable = "IDisposable" in raw
    uses_sink_class = "TestSinkForTesting" in raw
    i = 0
    while i < len(lines := code_lines):
        line = lines[i]
        m = CLASS_RE.match(line)
        if m:
            class_stack.append([m.group(1), depth, False])
        am = ATTR_RE.match(line)
        if am:
            pending.append((i + 1, am.group("name"), raw_lines[i].strip()))
            i += 1
            continue
        mm = METHOD_RE.match(line)
        if mm and pending:
            kinds = [p for p in pending if p[1] in ("Fact", "Theory")]
            if kinds:
                method = mm.group(1)
                body_code, end = extract_body(code_lines, i)
                body_raw = "\n".join(raw_lines[i:end + 1])
                asserts = ASSERT_RE.findall(body_code)
                distinct = sorted(set(asserts))
                log_only = bool(asserts) and all(
                    re.search(r"log|lines", self_text, re.IGNORECASE)
                    for self_text in re.findall(r"Assert\.\w+\s*\(([^\n]*)", body_code)
                )
                kinds_set = {p[1] for p in kinds}
                kind = "Theory" if "Theory" in kinds_set else "Fact"
                inline = sum(len(INLINE_DATA_RE.findall(p[2])) for p in pending)
                member = ""
                for p in pending:
                    md = MEMBER_DATA_RE.search(p[2])
                    if md:
                        member = md.group(1) or "member"
                        break
                if kind == "Theory":
                    cases = "inline:%d" % inline if inline else (
                        "member:%s" % member if member else "?")
                else:
                    cases = "1"
                skip = any(SKIP_RE.search(p[2]) for p in pending)
                tests.append({
                    "file": rel,
                    "class": class_stack[-1][0] if class_stack else "",
                    "method": method,
                    "kind": kind,
                    "theory_cases": cases,
                    "line": i + 1,
                    "asserts": ";".join(distinct),
                    "assert_count": len(asserts),
                    "log_only": int(log_only),
                    "source_text": int(bool(SOURCE_TEXT_RE.search(body_raw))),
                    "moq": int(bool(MOQ_RE.search(body_code))),
                    "side_effects": side_effects(body_code, body_raw),
                    "skip": int(skip),
                    "early_return": int(bool(EARLY_RETURN_RE.search(body_code))),
                    "swallow_catch": int(bool(SWALLOW_CATCH_RE.search(body_code))),
                })
                pending = []
                i = end + 1
                continue
        if pending and line.strip() and not line.strip().startswith("["):
            pending = []
        for ch in line:
            if ch == "{":
                depth += 1
            elif ch == "}":
                depth -= 1
        while class_stack:
            top = class_stack[-1]
            if depth > top[1]:
                top[2] = True
            if top[2] and depth <= top[1]:
                class_stack.pop()
            else:
                break
        i += 1
    return tests, class_has_idisposable, uses_sink_class


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tests-root", required=True, help="path to Source/Parsek.Tests")
    ap.add_argument("--out", required=True, help="output directory")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    inv_path = os.path.join(args.out, "test-inventory.csv")
    sum_path = os.path.join(args.out, "file-summary.csv")

    all_tests = []
    file_rows = []
    name_files = defaultdict(set)

    for dirpath, dirnames, filenames in os.walk(args.tests_root):
        dirnames[:] = [d for d in dirnames if d not in ("bin", "obj")]
        for fn in sorted(filenames):
            if not fn.endswith(".cs"):
                continue
            full = os.path.join(dirpath, fn)
            rel = os.path.relpath(full, args.tests_root).replace("\\", "/")
            size_kb = os.path.getsize(full) / 1024.0
            tests, idisp, sink = scan_file(full, rel)
            stem = os.path.splitext(fn)[0]
            cluster = cluster_for(rel, stem)
            tier = tier_for(size_kb)

            with open(full, "r", encoding="utf-8-sig", errors="replace") as fh:
                raw = fh.read()
            sequential = bool(re.search(r'\[Collection\("Sequential"\)\]', raw))

            for t in tests:
                t["cluster"] = cluster
                t["tier"] = tier
                t["sut_guess"] = t["class"][:-5] if t["class"].endswith("Tests") else (
                    stem[:-5] if stem.endswith("Tests") else stem)
                name_files[t["method"]].add(rel)
            all_tests.extend(tests)

            file_rows.append({
                "file": rel,
                "size_kb": round(size_kb, 1),
                "tier": tier,
                "cluster": cluster,
                "tests": len(tests),
                "facts": sum(1 for t in tests if t["kind"] == "Fact"),
                "theories": sum(1 for t in tests if t["kind"] == "Theory"),
                "inline_data_total": sum(
                    int(t["theory_cases"].split(":", 1)[1])
                    for t in tests if t["theory_cases"].startswith("inline:")),
                "sequential": int(sequential),
                "idisposable": int(idisp),
                "log_sink": int(sink),
                "uses_source_text": int(bool(SOURCE_TEXT_RE.search(raw))),
                "skip_attrs": sum(1 for t in tests if t["skip"]),
                "sut_guess": ";".join(sorted(set(t["sut_guess"] for t in tests))[:4]),
            })

    dup_names = {n for n, fs in name_files.items() if len(fs) > 1 and n != "Dispose"}

    inv_fields = ["file", "class", "method", "kind", "theory_cases", "line", "cluster",
                  "tier", "sut_guess", "asserts", "assert_count", "log_only", "source_text",
                  "moq", "side_effects", "skip", "early_return", "swallow_catch", "dup_name",
                  "verdict", "register_id"]
    with open(inv_path, "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=inv_fields, extrasaction="ignore")
        w.writeheader()
        for t in sorted(all_tests, key=lambda x: (x["file"], x["line"])):
            t["dup_name"] = int(t["method"] in dup_names)
            t["verdict"] = ""
            t["register_id"] = ""
            w.writerow(t)

    sum_fields = ["file", "size_kb", "tier", "cluster", "tests", "facts", "theories",
                  "inline_data_total", "sequential", "idisposable", "log_sink",
                  "uses_source_text", "skip_attrs", "sut_guess"]
    with open(sum_path, "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=sum_fields)
        w.writeheader()
        for r in sorted(file_rows, key=lambda x: x["file"]):
            w.writerow(r)

    clusters = defaultdict(lambda: [0, 0])
    tiers = defaultdict(int)
    for r in file_rows:
        clusters[r["cluster"]][0] += 1
        clusters[r["cluster"]][1] += r["tests"]
        tiers[r["tier"]] += 1
    print("files=%d methods=%d" % (len(file_rows), len(all_tests)))
    print("tiers:", dict(sorted(tiers.items())))
    for c, (f, t) in sorted(clusters.items(), key=lambda x: -x[1][1]):
        print("  %-18s files=%3d methods=%5d" % (c, f, t))


if __name__ == "__main__":
    sys.exit(main())
