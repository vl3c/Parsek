#!/usr/bin/env python3
"""Move every root-level .cs file of Source/Parsek into its module's folder.

The module of a file is the folder it sits in (`scripts/arch/modules.toml` lists one
folder rule per module). This script places a file that sits in the root of
Source/Parsek: it asks the architecture tool's own assignment code
(`archview.assign_module`) which module the file belongs to and `git mv`s it to
`Source/Parsek/<Module>/<File>.cs`. No file's text changes, so git's rename detection is
100 percent and `git log --follow` keeps each file's history.

Where the module comes from. The rules are read, in order, from:
  1. the working tree's `scripts/arch/modules.toml`;
  2. each `--rules-ref REF` (default: `origin/main`), read with
     `git show REF:scripts/arch/modules.toml`.
The first rule set that places the file wins. Before the folder move landed, main's
modules.toml placed root files with name-prefix rules; after it landed the working-tree
file has folder rules only, so a root file a later merge of an older main brings in is
placed by the prefix rules main still carries. A file no rule set places fails the run
with its name, and nothing moves.

Deterministic and re-runnable: the files are processed in sorted order, and a run over a
tree with no root-level .cs file is a no-op (exit 0, "nothing to move").

Safety checks before anything moves:
  - every target path is free (no basename collision inside a module folder);
  - no target folder differs only by case from an existing directory (Windows and macOS
    file systems are case-insensitive);
  - two root files never map to the same target.

Usage:

    python3 scripts/refactor/move_to_module_folders.py            # move (git mv)
    python3 scripts/refactor/move_to_module_folders.py --dry-run  # print the plan only
    python3 scripts/refactor/move_to_module_folders.py --check    # exit 1 if a root .cs file exists

Exit code 0 on success (or a clean --check), 1 when --check finds a root file or a file
cannot be placed or a check fails, 2 on a git or usage error. Plain stdlib.
"""

import argparse
import subprocess
import sys
import tomllib
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SOURCE_REL = "Source/Parsek"
MODULES_REL = "scripts/arch/modules.toml"

sys.path.insert(0, str(REPO_ROOT / "scripts" / "arch"))
import archview  # noqa: E402


def root_files(source_root):
    """Sorted names of the .cs files directly in `source_root`."""
    return sorted(p.name for p in Path(source_root).iterdir() if p.is_file() and p.suffix == ".cs")


def rules_from_ref(repo_root, ref):
    """The module rules of modules.toml at git `ref`, or None when git cannot show it."""
    try:
        text = subprocess.run(
            ["git", "show", "%s:%s" % (ref, MODULES_REL)],
            cwd=str(repo_root),
            check=True,
            capture_output=True,
        ).stdout
    except (OSError, subprocess.CalledProcessError):
        return None
    return tomllib.loads(text.decode("utf-8")).get("module", [])


def plan_moves(names, rule_sets):
    """Return ([(name, module)], [unplaced name]) for root file names.

    `rule_sets` is a list of (label, rules); the first set whose rules place a file wins.
    """
    moves = []
    unplaced = []
    for name in names:
        module = None
        for _label, rules in rule_sets:
            module = archview.assign_module(name, rules)
            if module:
                break
        if module:
            moves.append((name, module))
        else:
            unplaced.append(name)
    return moves, unplaced


def check_targets(source_root, moves):
    """Return a list of problems with the planned targets (empty when all is clear)."""
    source_root = Path(source_root)
    problems = []
    existing_dirs = {p.name for p in source_root.iterdir() if p.is_dir()}
    lower_dirs = {}
    for name in existing_dirs:
        lower_dirs.setdefault(name.lower(), set()).add(name)
    seen = {}
    for name, module in moves:
        clash = lower_dirs.get(module.lower(), set()) - {module}
        if clash:
            problems.append(
                "folder %s differs only by case from existing %s" % (module, ", ".join(sorted(clash)))
            )
        target = source_root / module / name
        if target.exists():
            problems.append("target exists: %s/%s/%s" % (SOURCE_REL, module, name))
        key = (module.lower(), name.lower())
        if key in seen:
            problems.append("two root files map to %s/%s: %s and %s" % (module, name, seen[key], name))
        seen[key] = name
        lower_target_dir = source_root / module
        if lower_target_dir.is_dir():
            for sibling in lower_target_dir.iterdir():
                if sibling.name.lower() == name.lower() and sibling.name != name:
                    problems.append(
                        "target %s/%s differs only by case from %s" % (module, name, sibling.name)
                    )
    return sorted(set(problems))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--check", action="store_true", help="exit 1 if a root-level .cs file exists")
    parser.add_argument("--dry-run", action="store_true", help="print the plan, move nothing")
    parser.add_argument(
        "--rules-ref",
        action="append",
        default=None,
        help="git ref whose modules.toml places a root file the working tree's rules do not "
        "(repeatable; default origin/main)",
    )
    parser.add_argument("--repo", default=str(REPO_ROOT), help=argparse.SUPPRESS)
    args = parser.parse_args(argv)

    repo_root = Path(args.repo)
    source_root = repo_root / SOURCE_REL
    names = root_files(source_root)

    if args.check:
        if names:
            for name in names:
                print("root-level source file: %s/%s (run scripts/refactor/move_to_module_folders.py)"
                      % (SOURCE_REL, name))
            return 1
        print("no root-level .cs file in %s" % SOURCE_REL)
        return 0

    if not names:
        print("nothing to move: no root-level .cs file in %s" % SOURCE_REL)
        return 0

    rule_sets = [("working tree", archview.load_rules(repo_root / MODULES_REL)[0])]
    for ref in args.rules_ref or ["origin/main"]:
        rules = rules_from_ref(repo_root, ref)
        if rules is None:
            print("note: cannot read %s at %s; skipped" % (MODULES_REL, ref), file=sys.stderr)
            continue
        rule_sets.append((ref, rules))

    moves, unplaced = plan_moves(names, rule_sets)
    if unplaced:
        for name in unplaced:
            print("UNPLACED: %s/%s (no rule set places it; add a rule or move it by hand)"
                  % (SOURCE_REL, name))
        return 1
    problems = check_targets(source_root, moves)
    if problems:
        for problem in problems:
            print("REFUSED: %s" % problem)
        return 1

    counts = {}
    for name, module in moves:
        counts[module] = counts.get(module, 0) + 1
        print("%s/%s -> %s/%s/%s" % (SOURCE_REL, name, SOURCE_REL, module, name))
    for module in sorted(counts):
        print("  %-14s %d" % (module, counts[module]))
    print("%d files" % len(moves))
    if args.dry_run:
        return 0

    for module in sorted(counts):
        (source_root / module).mkdir(exist_ok=True)
    for name, module in moves:
        try:
            subprocess.run(
                ["git", "mv", "%s/%s" % (SOURCE_REL, name), "%s/%s/%s" % (SOURCE_REL, module, name)],
                cwd=str(repo_root),
                check=True,
            )
        except (OSError, subprocess.CalledProcessError) as exc:
            print("git mv failed for %s: %s" % (name, exc), file=sys.stderr)
            return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
