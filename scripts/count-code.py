#!/usr/bin/env python3
"""Count Parsek's lines of code and automated tests.

Usage (from anywhere inside the checkout):

    python scripts/count-code.py            # two text tables
    python scripts/count-code.py --json     # the same numbers as JSON

Stdlib only. Files come from `git ls-files`, so build output, virtualenvs and
gitignored generated views never count. The blank / comment split is a line
prefix heuristic per language (//, * and /* for C#; # for Python, TOML,
PowerShell and shell), so the "code~" column is approximate; "lines" is exact.
Markdown has no comment split: a heading or a bullet is content. In-game tests
are counted with the harness's own parser (hlib.parse_ingame_test_declarations),
because a one-line regex miscounts multi-line, const-category and
namespace-qualified attributes.

The areas are not a partition of the repository. "InGameTests" is a subset of
the mod source, "Dev scripts" includes scripts/arch and its test file, and
tracked code outside every area (tools/, build.bat, .claude/hooks, research
scripts under docs/, harness TOML outside harness/scenarios, root Markdown) is
reported as one count in the footer instead of a row. "Python test methods"
counts `def test_` lines, not unittest loader cases, and includes
scripts/arch/test_archview.py, which CI does not run.
"""

import argparse
import json
import os
import re
import subprocess
import sys

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# (label, path prefix under the repo root, extensions)
AREAS = [
    ("Mod source (C#)", "Source/Parsek/", (".cs",)),
    ("  of which InGameTests", "Source/Parsek/InGameTests/", (".cs",)),
    ("xUnit test project (C#)", "Source/Parsek.Tests/", (".cs",)),
    ("Harness (Python)", "harness/", (".py",)),
    ("Dev scripts", "scripts/", (".py", ".ps1", ".sh")),
    ("Harness scenario specs (TOML)", "harness/scenarios/", (".toml",)),
    ("Docs (Markdown)", "docs/", (".md",)),
]

# Comment line prefixes per extension. C# preprocessor lines (#if, #region)
# are code, and Markdown has no comments, so neither maps "#" to a comment.
COMMENT_PREFIXES = {
    ".cs": ("//", "*", "/*"),
    ".py": ("#",),
    ".toml": ("#",),
    ".ps1": ("#",),
    ".sh": ("#",),
    ".md": (),
}

# Extensions the footer's "outside every area" count looks at.
CODE_EXTENSIONS = (".cs", ".py", ".ps1", ".sh", ".bat", ".toml", ".md")

FACT_RE = re.compile(r"^\s*\[(?:Xunit\.)?(?:Fact|SkippableFact)\b", re.M)
THEORY_RE = re.compile(r"^\s*\[(?:Xunit\.)?(?:Theory|SkippableTheory)\b", re.M)
INLINE_RE = re.compile(r"^\s*\[InlineData\b", re.M)
MEMBER_RE = re.compile(r"^\s*\[(?:MemberData|ClassData)\b", re.M)
PY_TEST_RE = re.compile(r"^\s+def test_\w+", re.M)


def tracked_files():
    try:
        out = subprocess.run(
            ["git", "ls-files", "-z"], cwd=REPO_ROOT, check=True,
            stdout=subprocess.PIPE).stdout
    except (OSError, subprocess.CalledProcessError) as error:
        sys.exit("count-code: git ls-files failed in %s (%s); the script "
                 "counts tracked files and needs git." % (REPO_ROOT, error))
    # A tracked file deleted in the working tree is still listed; skip it.
    return [p for p in out.decode("utf-8").split("\0")
            if p and os.path.isfile(os.path.join(REPO_ROOT, p))]


def read(rel_path):
    with open(os.path.join(REPO_ROOT, rel_path), encoding="utf-8",
              errors="ignore") as handle:
        return handle.read()


def count_lines(text, comment_prefixes):
    total = blank = comment = 0
    for line in text.splitlines():
        total += 1
        stripped = line.strip()
        if not stripped:
            blank += 1
        elif comment_prefixes and stripped.startswith(comment_prefixes):
            comment += 1
    return total, blank, comment


def count_areas(files):
    rows = []
    for label, prefix, exts in AREAS:
        n_files = total = blank = comment = 0
        for path in files:
            if path.startswith(prefix) and path.endswith(exts):
                n_files += 1
                ext = os.path.splitext(path)[1]
                t, b, c = count_lines(read(path), COMMENT_PREFIXES.get(ext, ()))
                total, blank, comment = total + t, blank + b, comment + c
        rows.append({"area": label.strip(), "label": label, "files": n_files,
                     "lines": total, "code": total - blank - comment,
                     "blank": blank, "comment": comment})
    return rows


def count_outside_areas(files):
    """Tracked code-like files that no AREAS row covers."""
    outside = 0
    for path in files:
        if not path.endswith(CODE_EXTENSIONS):
            continue
        if not any(path.startswith(prefix) and path.endswith(exts)
                   for _label, prefix, exts in AREAS):
            outside += 1
    return outside


def count_tests(files):
    fact = theory = inline = member = 0
    for path in files:
        if path.startswith("Source/Parsek.Tests/") and path.endswith(".cs"):
            text = read(path)
            fact += len(FACT_RE.findall(text))
            theory += len(THEORY_RE.findall(text))
            inline += len(INLINE_RE.findall(text))
            member += len(MEMBER_RE.findall(text))

    sys.path.insert(0, os.path.join(REPO_ROOT, "harness", "lib"))
    import hlib  # noqa: E402  (path set just above)
    ingame = 0
    categories = set()
    for path in files:
        if path.startswith("Source/Parsek/") and path.endswith(".cs"):
            for decl in hlib.parse_ingame_test_declarations(read(path), path):
                ingame += 1
                categories.add(decl.category)

    py_tests = 0
    for path in files:
        if path.endswith(".py") and path.startswith(("harness/", "scripts/")):
            py_tests += len(PY_TEST_RE.findall(read(path)))

    specs = sum(1 for p in files
                if p.startswith("harness/scenarios/") and p.endswith(".toml"))
    return {
        "xunitFacts": fact,
        "xunitTheories": theory,
        "xunitInlineDataRows": inline,
        "xunitMemberOrClassDataTheories": member,
        "xunitCasesApprox": fact + inline,
        "inGameTests": ingame,
        "inGameCategories": len(categories),
        "pythonTestMethods": py_tests,
        "harnessScenarioSpecs": specs,
    }


def git_head():
    try:
        return subprocess.run(
            ["git", "log", "-1", "--format=%h %ad", "--date=short"],
            cwd=REPO_ROOT, check=True,
            stdout=subprocess.PIPE).stdout.decode("utf-8").strip()
    except (OSError, subprocess.CalledProcessError):
        return "unknown"


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--json", action="store_true",
                        help="print the counts as JSON instead of tables")
    args = parser.parse_args(argv)

    files = tracked_files()
    areas = count_areas(files)
    tests = count_tests(files)
    outside = count_outside_areas(files)
    head = git_head()

    if args.json:
        for row in areas:
            del row["label"]
        json.dump({"head": head, "areas": areas,
                   "filesOutsideAreas": outside, "tests": tests},
                  sys.stdout, indent=2)
        sys.stdout.write("\n")
        return 0

    print("Parsek code count at %s (tracked files only)" % head)
    print()
    print("%-32s %6s %9s %9s %8s %8s" % (
        "Area", "files", "lines", "code~", "blank", "comment"))
    for row in areas:
        print("%-32s %6d %9d %9d %8d %8d" % (
            row["label"], row["files"], row["lines"], row["code"],
            row["blank"], row["comment"]))
    print()
    print("Tests")
    print("  xUnit facts                   %7d" % tests["xunitFacts"])
    print("  xUnit theories                %7d  (%d InlineData rows, %d fed by "
          "MemberData/ClassData)" % (
              tests["xunitTheories"], tests["xunitInlineDataRows"],
              tests["xunitMemberOrClassDataTheories"]))
    print("  xUnit cases, approx           %7d  (facts + InlineData rows)"
          % tests["xunitCasesApprox"])
    print("  In-game tests                 %7d  across %d categories" % (
        tests["inGameTests"], tests["inGameCategories"]))
    print("  Python test methods           %7d  (def test_ lines, incl. "
          "scripts/arch)" % tests["pythonTestMethods"])
    print("  Harness scenario specs        %7d" % tests["harnessScenarioSpecs"])
    print()
    print("code~ is lines minus blank and comment-prefixed lines "
          "(a per-language heuristic; none for Markdown).")
    print("Areas are not a partition: %d tracked code-like files sit outside "
          "every area (see the docstring)." % outside)
    return 0


if __name__ == "__main__":
    sys.exit(main())
