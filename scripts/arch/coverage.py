#!/usr/bin/env python3
"""Line and branch coverage per type (and lines per method), from a Cobertura XML, kept as a snapshot.

The unit tests run on Windows (`pwsh -File scripts/test-coverage.ps1` writes
`TestResults/Coverage/coverage..cobertura.xml` through coverlet), and nothing
in CI can measure coverage: the cloud and CI run the suite under mono, with no
coverlet. So a run on the owner's machine is folded into a small committed
snapshot that the Atlas and the metrics row read, always older than the page
that shows it:

    python3 scripts/arch/coverage.py TestResults/Coverage/coverage..cobertura.xml            # summary
    python3 scripts/arch/coverage.py TestResults/Coverage/coverage..cobertura.xml \\
        --write-snapshot --commit <sha> --date <YYYY-MM-DD>                                   # refresh

The file name has two dots: test-coverage.ps1 passes coverlet the output
prefix `<dir>/coverage.` and coverlet appends `.cobertura.xml`.

It runs where the file is (Windows, Python 3, stdlib only) and streams it:
coverlet's file for this assembly is about 80 MB, so it is read with
`ElementTree.iterparse` and each <class> is cleared once counted (an 85 MB
synthetic file reads in about 3 s, 160 MB peak, on the cloud container).

The reader follows the shape coverlet.msbuild 6.0.4 writes,
coverage/packages/package[@name]/classes/class[@name,@filename]/
(lines/line | methods/method/lines/line)[@number,@hits,@branch,@condition-coverage]
(a class's own <lines> list when it has one, else its methods' lines), and
counts lines itself (`hits` > 0) instead of trusting the rounded per-class
rates. It is tolerant: unknown elements and attributes are ignored, a line with no
readable number or hits is skipped, and missing branch data (a line without a
`branch` attribute, or a branch line without a readable `condition-coverage`)
makes that type's branches unknown (null), never 0.

Types are folded the way ilfacts folds generated code: a compiler-generated
class (`<>c`, `<>c__DisplayClass5_0`, the iterator and async state machines
`<Run>d__12`, local functions' classes) counts for the nearest enclosing
source type, whether the nesting is printed with `/` (Cecil), `+`
(reflection) or `.`, and a generic arity is dropped from every segment
(`Parsek.Box`1/<Get>d__1` is `Parsek.Box`, as ilfacts keys it); a name with
no source owner (`<PrivateImplementationDetails>`, `<Module>`) is dropped.

Lines are also kept per source METHOD, keyed `Type::Method` by name, from
each method's own <lines>. Generated code is folded by name, not by the
call references ilfacts follows: a state machine's MoveNext (`Type/<Run>d__12`,
an async lambda's `<<Run>b__0>d`) is Run, a lambda (`<Run>b__5_0` in `<>c` or
a display class) is Run, a local function (`<Run>g__Local|5_0`) is Run, and
anything else generated counts for its type only. Overloads share a name and
are summed, so `Type::Run` is every `Run`. Coverlet's per-method `complexity`
is summed the same way, and only methods at METHOD_COMPLEXITY_MIN (10) or more
are kept, the threshold raised until the snapshot fits MAX_SNAPSHOT_BYTES.

A line is counted once per type (keyed by file and number) even when a
partial type or a folded class lists it twice. Only the package of the
production assembly (`--assembly`, Parsek) is read. As a
cross-check the summary sums the package's class elements as written (before
folding) and prints them beside the file's root totals (`lines-valid` ...),
with any difference: a line a class and its lambda class both list counts
twice in coverlet's totals and once here, so a difference is reported, never
failed.

Snapshot schema `parsek-coverage/1` (sorted keys, one method or type per
line, so a refresh diffs cleanly):

    schema            "parsek-coverage/1"
    assembly          the package read (Parsek)
    commit, date      what the run measured (given on the command line)
    configuration     the build configuration of the run (Debug)
    totals            {linesValid, linesCovered, branchesValid, branchesCovered,
                      branchesUnknownTypes}: over every type kept; branch totals
                      count only types whose branch data is known
    types.<path>      {lines: {valid, covered}, branches: {valid, covered} | null}
                      per source type, keyed by its IL path (`Parsek.Outer/Inner`)
    methodComplexityMin   the summed complexity a method needed to be kept
    methods.<Type::Method>  [covered, valid] lines of the method and the generated
                      code folded into it; optional: a snapshot written before
                      methods were kept has neither key and still reads

Everything except `main` and the file loader is pure.
"""

from __future__ import annotations

import argparse
import datetime
import io
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCHEMA = "parsek-coverage/1"
DEFAULT_SNAPSHOT = HERE / "coverage_snapshot.json"
DEFAULT_ASSEMBLY = "Parsek"
SHA_RE = re.compile(r"^[0-9a-f]{7,40}$")
DATE_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")
CONDITION_RE = re.compile(r"\((\d+)\s*/\s*(\d+)\)")


# A generic type's arity on one path segment: `Box`1` -> `Box` (ilgraph.canonical_path).
ARITY_RE = re.compile(r"`\d+$")
# Generated code that belongs to one source method, by name. A lambda body
# (`<Run>b__5_0`, in `<>c` or a `<>c__DisplayClass5_0`) or a local function
# (`<Run>g__Local|5_0`) names its method in the method name; an iterator or
# async state machine (`<Run>d__12`, an async lambda's `<<Run>b__0>d`) names it
# in the class name, and its MoveNext holds the body.
LAMBDA_OR_LOCAL_RE = re.compile(r"^<([^<>]+)>[bg]__")
STATE_MACHINE_RE = re.compile(r"^<+([^<>]+)>(?:[bg]__[^>]*>)?d(?:__\d+)?$")
STATE_MACHINE_BODY = "MoveNext"
# Methods kept in the snapshot: summed coverlet `complexity` at least this, and
# the snapshot under MAX_SNAPSHOT_BYTES (the threshold rises until it fits).
METHOD_COMPLEXITY_MIN = 10
MAX_SNAPSHOT_BYTES = 500 * 1024


def _segments(name):
    """A Cobertura class name split into IL path segments: nesting printed as
    `/` (Cecil), `+` (reflection) or `.<` (a generated class after a dot)."""
    name = (name or "").strip().replace("+", "/")
    return name.replace(".<", "/<").split("/") if name else []


def fold_type_name(name):
    """The source type a Cobertura class name counts for, as an IL path with
    generic arities dropped (`Parsek.Box`1/<Get>d__1` -> `Parsek.Box`, the
    key ilfacts uses); None when it has none (a top-level generated name)."""
    segments = _segments(name)
    if not segments or segments[0].startswith("<"):
        return None
    kept = []
    for segment in segments:
        if segment.startswith("<"):
            break
        kept.append(ARITY_RE.sub("", segment))
    return "/".join(kept) or None


def fold_method_name(class_name, method_name):
    """The source method a Cobertura method counts for: (type path, method name),
    or None when it counts for its type only.

    A method of the source type itself is that method; a lambda or local
    function body (`<Run>b__5_0`, `<Run>g__Local|5_0`) is Run wherever it sits;
    a state machine's MoveNext (`<Run>d__12`, `<<Run>b__0>d`) is Run. Anything
    else generated (a closure class's constructor, a `<>c` static constructor,
    a state machine's other members) counts for the type only. Overloads share
    a name, so they fold together."""
    path = fold_type_name(class_name)
    name = (method_name or "").strip()
    if path is None or not name:
        return None
    match = LAMBDA_OR_LOCAL_RE.match(name)
    if match:
        return path, match.group(1)
    generated = [s for s in _segments(class_name) if s.startswith("<")]
    if not generated:
        return path, ARITY_RE.sub("", name)
    if name == STATE_MACHINE_BODY:
        match = STATE_MACHINE_RE.match(ARITY_RE.sub("", generated[-1]))
        if match:
            return path, match.group(1)
    return None


def _int(text):
    try:
        return int(str(text).strip())
    except (TypeError, ValueError):
        return None


def _line_facts(line):
    """(number, hits, branch) of a <line>; branch is (valid, covered), (0, 0)
    for a plain line, or None when its branch data is missing. None when the
    line itself is unreadable."""
    number = _int(line.get("number"))
    hits = _int(line.get("hits"))
    if number is None or hits is None:
        return None
    flag = line.get("branch")
    if flag is None:
        return number, hits, None
    if flag.strip().lower() != "true":
        return number, hits, (0, 0)
    match = CONDITION_RE.search(line.get("condition-coverage") or "")
    if not match:
        return number, hits, None
    covered, valid = int(match.group(1)), int(match.group(2))
    return number, hits, (valid, covered)


def _class_lines(element):
    """The <line> elements of a class: its own <lines> list when it has one,
    else every method's."""
    own = element.find("lines")
    if own is not None:
        return own.findall("line")
    return element.findall("methods/method/lines/line")


ROOT_TOTAL_KEYS = (("linesValid", "lines-valid"), ("linesCovered", "lines-covered"),
                   ("branchesValid", "branches-valid"), ("branchesCovered", "branches-covered"))


def _source(source):
    """A file object or path for iterparse: XML text and bytes are wrapped."""
    if isinstance(source, bytes):
        return io.BytesIO(source)
    if isinstance(source, str) and source.lstrip().startswith("<"):
        return io.BytesIO(source.encode("utf-8"))
    return source


def parse_cobertura(source, assembly=DEFAULT_ASSEMBLY):
    """{types, packages, classes, dropped, raw, root, timestamp} from a Cobertura
    file (a path, a file object, or the XML as text or bytes).

    Streamed with iterparse, each <class> cleared once read, so an 80 MB file
    never sits in memory as a tree. `types` maps a source type path to
    {lines: {valid, covered}, branches: {valid, covered} | None}, and `methods`
    maps `Type::Method` (fold_method_name) to {valid, covered, complexity}.
    `raw` sums every class element of the package as written, before folding (what
    coverlet's root totals count), and `root` holds those root totals (None
    for one the file lacks), so the two can be compared: a line a class and
    its lambda class both list counts twice in `raw` and once in `types`.

    Raises ValueError when the root is not <coverage> or no package is named `assembly`."""
    lines = {}
    method_lines = {}
    method_complexity = {}
    seen_types = set()
    names = set()
    classes = dropped = 0
    raw = {"linesValid": 0, "linesCovered": 0, "branchesValid": 0, "branchesCovered": 0}
    root_totals = None
    timestamp = None
    package = None
    first = True
    for event, element in ET.iterparse(_source(source), events=("start", "end")):
        if first:
            first = False
            if element.tag != "coverage":
                raise ValueError("not a Cobertura file: the root element is <%s>, not <coverage>" % element.tag)
            root_totals = {key: _int(element.get(attribute)) for key, attribute in ROOT_TOTAL_KEYS}
            timestamp = _int(element.get("timestamp"))
        if element.tag == "package":
            if event == "start":
                package = element.get("name") or ""
                names.add(package)
            else:
                package = None
                element.clear()
            continue
        if event != "end" or element.tag != "class":
            continue
        if package != assembly:
            element.clear()
            continue
        classes += 1
        class_name = element.get("name")
        path = fold_type_name(class_name)
        filename = (element.get("filename") or "").replace("\\", "/")
        for method in element.findall("methods/method"):
            owner = fold_method_name(class_name, method.get("name"))
            if owner is None:
                continue
            method_complexity[owner] = method_complexity.get(owner, 0) + (_int(method.get("complexity")) or 0)
            held = method_lines.setdefault(owner, {})
            for line in method.findall("lines/line"):
                facts = _line_facts(line)
                if facts is not None:
                    key = (filename, facts[0])
                    held[key] = max(held.get(key, 0), facts[1])
        for line in _class_lines(element):
            facts = _line_facts(line)
            if facts is None:
                continue
            number, hits, branch = facts
            raw["linesValid"] += 1
            raw["linesCovered"] += hits > 0
            if branch is not None:
                raw["branchesValid"] += branch[0]
                raw["branchesCovered"] += branch[1]
            if path is None:
                continue
            key = (filename, number)
            held = lines.setdefault(path, {})
            if key in held:
                # The same line listed twice (a folded class, a method list
                # and the class list): the most hits, and the branch data
                # of whichever listing has it.
                old_hits, old_branch = held[key]
                merged = branch if old_branch is None else old_branch
                if branch is not None and old_branch is not None:
                    merged = (max(old_branch[0], branch[0]), max(old_branch[1], branch[1]))
                held[key] = (max(old_hits, hits), merged)
            else:
                held[key] = (hits, branch)
        if path is None:
            dropped += 1
        else:
            seen_types.add(path)
        element.clear()
    if first:
        raise ValueError("not a Cobertura file: it is empty")
    if assembly not in names:
        raise ValueError("no package named %r in the Cobertura file (packages: %s)"
                         % (assembly, ", ".join(sorted(names))))
    types = {}
    for path in seen_types:
        held = lines.get(path, {})
        valid = len(held)
        covered = sum(1 for hits, _branch in held.values() if hits > 0)
        branches = None
        if all(branch is not None for _hits, branch in held.values()):
            branches = {"valid": sum(b[0] for _h, b in held.values()),
                        "covered": sum(b[1] for _h, b in held.values())}
        types[path] = {"lines": {"valid": valid, "covered": covered}, "branches": branches}
    methods = {}
    for (path, name), held in method_lines.items():
        methods[path + "::" + name] = {
            "valid": len(held), "covered": sum(1 for hits in held.values() if hits > 0),
            "complexity": method_complexity.get((path, name), 0)}
    return {"types": types, "methods": methods, "packages": sorted(names), "classes": classes,
            "dropped": dropped, "raw": raw, "root": root_totals, "timestamp": timestamp}


def totals(types):
    out = {"linesValid": 0, "linesCovered": 0, "branchesValid": 0, "branchesCovered": 0, "branchesUnknownTypes": 0}
    for row in types.values():
        out["linesValid"] += row["lines"]["valid"]
        out["linesCovered"] += row["lines"]["covered"]
        if row["branches"] is None:
            out["branchesUnknownTypes"] += 1
        else:
            out["branchesValid"] += row["branches"]["valid"]
            out["branchesCovered"] += row["branches"]["covered"]
    return out


def kept_methods(methods, minimum):
    """{"Type::Method": [covered, valid]} for the methods whose summed complexity
    is at least `minimum` and that have a line."""
    return {key: [row["covered"], row["valid"]] for key, row in sorted(methods.items())
            if row["complexity"] >= minimum and row["valid"] > 0}


def make_snapshot(parsed, commit, date, configuration="Debug", assembly=DEFAULT_ASSEMBLY,
                  method_min=METHOD_COMPLEXITY_MIN):
    """The snapshot dict of a parse_cobertura result. Raises ValueError on a bad commit or date."""
    commit = (commit or "").strip().lower()
    if not SHA_RE.match(commit):
        raise ValueError("not a commit sha: %r" % commit)
    if not DATE_RE.match(date or ""):
        raise ValueError("not a YYYY-MM-DD date: %r" % date)
    return {
        "schema": SCHEMA,
        "assembly": assembly,
        "commit": commit,
        "date": date,
        "configuration": configuration,
        "totals": totals(parsed["types"]),
        "methodComplexityMin": method_min,
        "methods": kept_methods(parsed.get("methods", {}), method_min),
        "types": {path: parsed["types"][path] for path in sorted(parsed["types"])},
    }


def fit_snapshot(parsed, commit, date, configuration="Debug", assembly=DEFAULT_ASSEMBLY,
                 method_min=METHOD_COMPLEXITY_MIN, max_bytes=MAX_SNAPSHOT_BYTES):
    """(snapshot, text) with the method threshold raised from `method_min`, one
    complexity value at a time, until the text is at most `max_bytes` (or no
    method is left)."""
    # Each step past method_min drops the methods at the lowest complexity left.
    above = sorted({row["complexity"] + 1 for row in parsed.get("methods", {}).values()
                    if row["complexity"] >= method_min})
    for minimum in [method_min] + above:
        snapshot = make_snapshot(parsed, commit, date, configuration, assembly, minimum)
        text = dumps_snapshot(snapshot)
        if len(text.encode("utf-8")) <= max_bytes or not snapshot["methods"]:
            break
    return snapshot, text


def dumps_snapshot(snapshot):
    """Deterministic text: sorted keys, one method and one type per line, newline-terminated."""

    def one(value):
        return json.dumps(value, sort_keys=True, ensure_ascii=True)

    blocks = [key for key in ("methods", "types") if key in snapshot]
    head = [key for key in sorted(snapshot) if key not in blocks]
    lines = ["{"]
    for key in head:
        lines.append("  %s: %s," % (one(key), one(snapshot[key])))
    for b, block in enumerate(blocks):
        rows = snapshot[block]
        lines.append("  %s: {" % one(block))
        keys = sorted(rows)
        for i, key in enumerate(keys):
            lines.append("    %s: %s%s" % (one(key), one(rows[key]), "," if i < len(keys) - 1 else ""))
        lines.append("  }" + ("," if b < len(blocks) - 1 else ""))
    lines.append("}")
    return "\n".join(lines) + "\n"


def read_snapshot(text):
    """The snapshot in `text`. A snapshot written before methods were kept has
    no `methods` block and still reads (its methods count as not measured)."""
    snapshot = json.loads(text)
    if snapshot.get("schema") != SCHEMA:
        raise ValueError("coverage snapshot is %r, not %s" % (snapshot.get("schema"), SCHEMA))
    for key in ("commit", "date", "types"):
        if key not in snapshot:
            raise ValueError("coverage snapshot has no %r" % key)
    methods = snapshot.get("methods")
    if methods is not None and not (isinstance(methods, dict) and all(
            isinstance(row, list) and len(row) == 2 for row in methods.values())):
        raise ValueError("coverage snapshot's methods are not {\"Type::Method\": [covered, valid]}")
    return snapshot


def load_snapshot(path=DEFAULT_SNAPSHOT):
    """The snapshot at `path`, or None when there is none (coverage not measured).

    A file that exists but is not a snapshot raises: a broken snapshot should
    fail loudly, not read as "not measured"."""
    path = Path(path)
    if not path.is_file():
        return None
    return read_snapshot(path.read_text(encoding="utf-8"))


def _pct(covered, valid):
    return "%.1f%%" % (100.0 * covered / valid) if valid else "-"


def root_check(parsed):
    """Lines and branches summed over the class elements as written, against the
    file's root totals: [(name, summed, root, difference)], root None when absent."""
    rows = []
    for key, _attribute in ROOT_TOTAL_KEYS:
        root = (parsed.get("root") or {}).get(key)
        summed = parsed["raw"][key]
        rows.append((key, summed, root, None if root is None else summed - root))
    return rows


def format_summary(parsed):
    t = totals(parsed["types"])
    stamp = parsed.get("timestamp")
    when = ("" if stamp is None else ", run %s UTC" % datetime.datetime.fromtimestamp(
        stamp, datetime.timezone.utc).strftime("%Y-%m-%d %H:%M"))
    lines = [
        "COVERAGE (packages: %s%s)" % (", ".join(parsed["packages"]), when),
        "  classes read: %d, folded into %d source types (%d with no source owner dropped)"
        % (parsed["classes"], len(parsed["types"]), parsed["dropped"]),
        "  lines: %d / %d (%s)" % (t["linesCovered"], t["linesValid"], _pct(t["linesCovered"], t["linesValid"])),
        "  branches: %d / %d (%s), %d types with no branch data"
        % (t["branchesCovered"], t["branchesValid"], _pct(t["branchesCovered"], t["branchesValid"]),
           t["branchesUnknownTypes"]),
        "  methods: %d source methods with lines, %d with summed complexity >= %d"
        % (len(parsed.get("methods", {})), len(kept_methods(parsed.get("methods", {}), METHOD_COMPLEXITY_MIN)),
           METHOD_COMPLEXITY_MIN),
        "  cross-check, class elements as written against the root totals (a line two classes list counts"
        " twice there, once above):",
    ]
    for key, summed, root, difference in root_check(parsed):
        lines.append("    %-16s %9d  root %9s  %s" % (
            key, summed, "-" if root is None else root,
            "no root total" if root is None else ("equal" if difference == 0 else "differs by %+d" % difference)))
    return "\n".join(lines)


def main(argv=None):
    parser = argparse.ArgumentParser(description="Per-type coverage from a Cobertura XML (schema %s)." % SCHEMA)
    parser.add_argument("cobertura", help="the Cobertura XML (TestResults/Coverage/coverage..cobertura.xml)")
    parser.add_argument("--write-snapshot", action="store_true", help="write the snapshot (needs --commit, --date)")
    parser.add_argument("--commit", help="the commit the coverage run measured")
    parser.add_argument("--date", help="the date of the run, YYYY-MM-DD")
    parser.add_argument("--configuration", default="Debug", help="the build configuration of the run (Debug)")
    parser.add_argument("--assembly", default=DEFAULT_ASSEMBLY, help="the package to read (Parsek)")
    parser.add_argument("--out", default=str(DEFAULT_SNAPSHOT), help="where the snapshot goes")
    parser.add_argument("--method-complexity-min", type=int, default=METHOD_COMPLEXITY_MIN,
                        help="keep methods whose summed complexity is at least this (%d); raised"
                             " until the snapshot fits --max-kb" % METHOD_COMPLEXITY_MIN)
    parser.add_argument("--max-kb", type=int, default=MAX_SNAPSHOT_BYTES // 1024,
                        help="the snapshot size the method threshold is raised to fit (%d KB)"
                             % (MAX_SNAPSHOT_BYTES // 1024))
    args = parser.parse_args(argv)
    if args.write_snapshot and (not args.commit or not args.date):
        parser.error("--write-snapshot needs --commit and --date")
    parsed = parse_cobertura(str(Path(args.cobertura)), args.assembly)
    print(format_summary(parsed))
    if args.write_snapshot:
        try:
            snapshot, text = fit_snapshot(parsed, args.commit, args.date, args.configuration, args.assembly,
                                          args.method_complexity_min, args.max_kb * 1024)
        except ValueError as exc:
            parser.error(str(exc))
        Path(args.out).write_text(text, encoding="utf-8", newline="\n")
        minimum = snapshot["methodComplexityMin"]
        print("Wrote %s (measured at %s, %s): %d types, %d methods with complexity >= %d%s, %.0f KB"
              % (args.out, snapshot["commit"], snapshot["date"], len(snapshot["types"]), len(snapshot["methods"]),
                 minimum, "" if minimum == args.method_complexity_min else
                 " (raised from %d to fit %d KB)" % (args.method_complexity_min, args.max_kb),
                 len(text.encode("utf-8")) / 1024.0))
    return 0


if __name__ == "__main__":
    sys.exit(main())
