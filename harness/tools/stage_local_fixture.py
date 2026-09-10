#!/usr/bin/env python3
"""Stage an OPERATOR-LOCAL fixture save into harness/fixtures/local-saves/.

Some lanes need a host with real DENSITY rather than a shape: the GUI census walks
every Parsek window and photographs it, and a window with no rows photographs as an
empty box. The operator's own career carries that - tens of megabytes, a hundred-plus
recordings, hundreds of ledger actions - and it can neither be committed (size) nor
synthesised (the point IS the content). So a spec may name a template under

    fixtures/local-saves/<leaf>

which this script fills from a save the operator already has. The directory is
gitignored, so a clone gets the spec and not the bytes, and every committed suite stays
green with the fixture absent - a lane that needs it is admission-refused with a
`INVALID(staging)` naming this script (hlib.local_fixture_hint).

WHY A SEPARATE PREFIX from the committed `fixtures/saves/`, and this is the deciding
reason rather than tidiness: `test_saveparse.test_fixture_set_is_exactly_the_committed_
set` lists the DIRECTORIES under `fixtures/saves/` and compares them to a pinned set. A
staged local fixture there would red that cell on the one machine that can actually fly
the lane - green for everyone who cannot, red for the operator. Under its own prefix
that sweep is untouched in both states.

Usage::

    python harness/tools/stage_local_fixture.py \\
        --from "C:/.../Kerbal Space Program/saves/c1" --as c1-gui
    python harness/tools/stage_local_fixture.py --list
    python harness/tools/stage_local_fixture.py --as c1-gui --remove

The copy is VERBATIM by default (`persistent.sfs`, `persistent.loadmeta`, every
`Parsek/<dir>` sidecar tree, `Ships/`, `AddOns/`) because a fixture the harness stages
must be what KSP would have loaded - with ONE unconditional exception, the top-level
`analysis/` directory, which is the offline analyzer's own OUTPUT and whose
`baseline.cfg` would make the lane INVALID under the harness's `-FreshSaveGate` (see
``classify_entry``). `--no-quicksaves` additionally drops the `*.sfs` files that are
neither `persistent.sfs` nor under `Parsek/`, together with their `.loadmeta` siblings,
which on a long-lived career is usually most of the bytes and none of the content a
census reads.

Stdlib only; ASCII only.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import sys
from typing import List, Optional, Sequence, Tuple

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOCAL_SAVES_DIR = os.path.join(HARNESS_ROOT, "fixtures", "local-saves")

# The leaf name rule. Deliberately the harness's own runSaveName rule
# (hlib._SAVE_NAME_RE) rather than something looser: the leaf becomes the staged
# directory here AND the run save name inside the instance, so a name this script
# accepted and validate_spec rejected would be staged and then unusable.
_LEAF_RE = re.compile(r"^[A-Za-z0-9 _-]+$")

# Files a KSP save carries that a fixture never needs and that are pure noise in a diff.
_ALWAYS_SKIP_NAMES = ("thumbs.db", ".ds_store")

# The offline analyzer's per-save output directory. Dropped unconditionally - see
# classify_entry for why a staged `analysis/baseline.cfg` would make the lane INVALID
# under the harness's hard-coded `-FreshSaveGate`.
_ANALYSIS_DIR_NAME = "analysis"

# KSP's per-save-file metadata sidecar. Classified by its `.sfs` twin, never on its own.
_LOADMETA_EXT = ".loadmeta"


def validate_leaf(leaf: str) -> Optional[str]:
    """None when ``leaf`` is a usable fixture name, else the reason it is not."""
    if not leaf:
        return "empty"
    if leaf in (".", ".."):
        return "a relative path component"
    if not _LEAF_RE.match(leaf):
        return ("not filename-safe (alphanumerics, dash, underscore and space only) - "
                "the leaf is also the run save name inside the KSP instance")
    return None


def classify_entry(rel_path: str, keep_quicksaves: bool) -> Tuple[bool, str]:
    """Whether one save-relative path is copied, and why not when it is skipped.

    Pure, so the selection rule has cells on it without a real save on disk.
    ``rel_path`` uses forward slashes and is relative to the save directory.

    The rule is COPY-BY-DEFAULT: a fixture the harness stages must be what KSP would
    have loaded, so anything not positively identified as noise is kept. Three classes
    are dropped - the OS junk files, the analyzer's OUTPUT directory (unconditionally,
    see below), and (with ``keep_quicksaves`` false) the save's own quicksave / backup
    `.sfs` files plus THEIR `.loadmeta` siblings, which are neither `persistent.sfs`
    nor part of a `Parsek/` sidecar tree."""
    parts = [p for p in rel_path.replace("\\", "/").split("/") if p]
    if not parts:
        return False, "empty path"
    name = parts[-1]
    if name.lower() in _ALWAYS_SKIP_NAMES:
        return False, "os junk"
    if parts[0].lower() == _ANALYSIS_DIR_NAME:
        # THE TOP-LEVEL `analysis/` DIRECTORY, dropped UNCONDITIONALLY and not as
        # tidiness. It is the offline analyzer's OUTPUT (`<leaf>.analysis.txt` /
        # `.json`) plus, on a save that has ever had one written, `baseline.cfg` - and
        # `run.py` invokes the analyzer with `-FreshSaveGate`, whose `Forbid` mode
        # treats the PRESENCE of a baseline in a produced save as a failure
        # (`BASELINE-FORBIDDEN` -> `INVALID(fixture-authoring)`). Staging one would
        # therefore make the lane INVALID before it read a single window, with a cause
        # that names fixture authoring rather than the copy that carried it in. The
        # census reads no analyzer output either way.
        return False, "analyzer output dir"
    if keep_quicksaves:
        return True, ""
    lowered = name.lower()
    if lowered.endswith(_LOADMETA_EXT):
        # A `.loadmeta` is KSP's sidecar for the `.sfs` of the same stem, so it is
        # classified BY THAT FILE rather than on its own: dropping `quicksave.sfs` and
        # keeping `quicksave.loadmeta` would leave the save folder advertising a
        # quicksave whose bytes are gone. `persistent.loadmeta` rides along with
        # `persistent.sfs` and is kept for exactly the same reason.
        sibling = parts[:-1] + [name[:-len(_LOADMETA_EXT)] + ".sfs"]
        return classify_entry("/".join(sibling), keep_quicksaves)
    if not lowered.endswith(".sfs"):
        return True, ""
    if lowered == "persistent.sfs" and len(parts) == 1:
        return True, ""
    if parts[0] == "Parsek":
        # RewindPoints/<id>.sfs and anything else Parsek keeps: load-bearing.
        return True, ""
    return False, "quicksave / backup sfs"


def plan_copy(source: str, keep_quicksaves: bool) -> Tuple[List[str], List[str]]:
    """Walk ``source`` and return (relative paths to copy, skipped relative paths)."""
    copy: List[str] = []
    skipped: List[str] = []
    for root, _dirs, files in os.walk(source):
        for name in files:
            abs_path = os.path.join(root, name)
            rel = os.path.relpath(abs_path, source).replace(os.sep, "/")
            keep, _why = classify_entry(rel, keep_quicksaves)
            (copy if keep else skipped).append(rel)
    copy.sort()
    skipped.sort()
    return copy, skipped


def _human(size: int) -> str:
    for unit in ("B", "KB", "MB", "GB"):
        if size < 1024 or unit == "GB":
            return "%.1f %s" % (size, unit)
        size /= 1024.0
    return "%d B" % size


def stage(source: str, leaf: str, keep_quicksaves: bool, force: bool) -> int:
    reason = validate_leaf(leaf)
    if reason:
        sys.stderr.write("--as %r is %s\n" % (leaf, reason))
        return 2
    if not os.path.isdir(source):
        sys.stderr.write("--from %r is not a directory\n" % source)
        return 2
    sfs = os.path.join(source, "persistent.sfs")
    if not os.path.isfile(sfs):
        # Fail closed, pre-copy: a directory with no persistent.sfs is not a KSP save,
        # and staging it would produce a fixture whose run dies at LoadGame minutes later
        # with a misleading cause.
        sys.stderr.write("--from %r carries no persistent.sfs, so it is not a KSP save "
                         "directory\n" % source)
        return 2

    target = os.path.join(LOCAL_SAVES_DIR, leaf)
    if os.path.isdir(target) and not force:
        sys.stderr.write("%s already exists; pass --force to replace it\n" % target)
        return 1

    copy, skipped = plan_copy(source, keep_quicksaves)
    os.makedirs(LOCAL_SAVES_DIR, exist_ok=True)
    if os.path.isdir(target):
        shutil.rmtree(target)
    total = 0
    for rel in copy:
        src = os.path.join(source, rel.replace("/", os.sep))
        dst = os.path.join(target, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(src, dst)
        total += os.path.getsize(dst)

    # Provenance, written as a SIBLING file rather than inside the fixture: the staged
    # directory is copytree'd into the KSP instance verbatim, and a stray file in a save
    # folder is the kind of thing that shows up in a diff of a produced save later.
    note = os.path.join(LOCAL_SAVES_DIR, leaf + ".staged.txt")
    with open(note, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("source: %s\n" % os.path.abspath(source))
        fh.write("files: %d copied, %d skipped\n" % (len(copy), len(skipped)))
        fh.write("bytes: %d\n" % total)
        fh.write("quicksaves: %s\n" % ("kept" if keep_quicksaves else "dropped"))

    sys.stdout.write("staged %s -> %s\n" % (os.path.abspath(source), target))
    sys.stdout.write("  %d file(s), %s (%d skipped)\n"
                     % (len(copy), _human(total), len(skipped)))
    sys.stdout.write("  spec key: saveTemplate = \"fixtures/local-saves/%s\"\n" % leaf)
    return 0


def list_staged() -> int:
    if not os.path.isdir(LOCAL_SAVES_DIR):
        sys.stdout.write("no local fixtures staged (%s does not exist)\n"
                         % LOCAL_SAVES_DIR)
        return 0
    names = sorted(n for n in os.listdir(LOCAL_SAVES_DIR)
                   if os.path.isdir(os.path.join(LOCAL_SAVES_DIR, n)))
    if not names:
        sys.stdout.write("no local fixtures staged\n")
        return 0
    for name in names:
        path = os.path.join(LOCAL_SAVES_DIR, name)
        size = 0
        count = 0
        for root, _dirs, files in os.walk(path):
            for f in files:
                try:
                    size += os.path.getsize(os.path.join(root, f))
                    count += 1
                except OSError:
                    pass
        sys.stdout.write("%-24s %6d file(s)  %10s  fixtures/local-saves/%s\n"
                         % (name, count, _human(size), name))
    return 0


def remove(leaf: str) -> int:
    reason = validate_leaf(leaf)
    if reason:
        sys.stderr.write("--as %r is %s\n" % (leaf, reason))
        return 2
    target = os.path.join(LOCAL_SAVES_DIR, leaf)
    if not os.path.isdir(target):
        sys.stdout.write("nothing to remove: %s\n" % target)
        return 0
    shutil.rmtree(target)
    note = os.path.join(LOCAL_SAVES_DIR, leaf + ".staged.txt")
    if os.path.isfile(note):
        os.remove(note)
    sys.stdout.write("removed %s\n" % target)
    return 0


def main(argv: Optional[Sequence[str]] = None) -> int:
    ap = argparse.ArgumentParser(
        description="Stage an operator-local fixture save under "
                    "harness/fixtures/local-saves/ (gitignored).")
    ap.add_argument("--from", dest="source", default=None,
                    help="a real KSP save DIRECTORY (must contain persistent.sfs)")
    ap.add_argument("--as", dest="leaf", default=None,
                    help="the fixture leaf name, which is also the run save name")
    ap.add_argument("--list", action="store_true", help="list what is already staged")
    ap.add_argument("--remove", action="store_true",
                    help="delete the staged fixture named by --as")
    ap.add_argument("--no-quicksaves", action="store_true",
                    help="drop quicksave / backup .sfs files and their .loadmeta "
                         "siblings (keeps persistent.sfs, persistent.loadmeta and "
                         "every Parsek/ sidecar)")
    ap.add_argument("--force", action="store_true",
                    help="replace an existing staged fixture of the same name")
    args = ap.parse_args(list(argv) if argv is not None else None)

    if args.list:
        return list_staged()
    if args.remove:
        if not args.leaf:
            sys.stderr.write("--remove needs --as <leaf>\n")
            return 2
        return remove(args.leaf)
    if not args.source or not args.leaf:
        sys.stderr.write("both --from <save dir> and --as <leaf> are required "
                         "(or use --list / --remove)\n")
        return 2
    return stage(args.source, args.leaf, not args.no_quicksaves, args.force)


if __name__ == "__main__":
    raise SystemExit(main())
