"""Turn "mission M in save S" into a watch-lane spec (harness/README.md ->
"Watch lanes").

    # what can I watch in this save?
    python harness/tools/make_watch_lane.py --save fixtures/local-saves/mycareer --list

    # a lane for one mission, held for the owner:
    python harness/tools/make_watch_lane.py --save fixtures/local-saves/mycareer \\
        --tree "name:Duna Express" --scene transfer --id WL-mine-duna \\
        --map-focus Sun --map-distance 5e10
    python harness/run.py --spec harness/scenarios/local/WL-mine-duna.toml --hold

``--save`` is a fixture directory relative to harness/ (a committed
``fixtures/saves/<leaf>`` or an operator-local ``fixtures/local-saves/<leaf>``
staged with ``tools/stage_local_fixture.py``). ``--tree`` takes a tree id, or a
handle resolved offline against the save's persistent.sfs: ``first``, ``latest``
or ``name:<mission name>``; the spec pins the resolved id. A lane on a committed
fixture is written to ``scenarios/<id>.toml``, one on a local fixture to the
gitignored ``scenarios/local/<id>.toml`` (fly it with ``run.py --spec``).

The pure work (handle resolution, steps, TOML) is ``harness/lib/watchlane.py``;
this file only reads the save and writes the spec.
"""

from __future__ import annotations

import argparse
import os
import shlex
import sys
from typing import List, Optional, Sequence

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(HERE)
sys.path.insert(0, os.path.join(HARNESS, "lib"))

import watchlane as wl  # noqa: E402

LOCAL_PREFIX = "fixtures/local-saves/"


def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0],
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--save", required=True,
                    help="fixture dir relative to harness/ (fixtures/saves/<leaf> or "
                         "fixtures/local-saves/<leaf>)")
    ap.add_argument("--list", action="store_true",
                    help="list the save's committed trees (id, mission name, span) and exit")
    ap.add_argument("--tree", help="tree id, or first / latest / name:<mission name>")
    ap.add_argument("--scene", choices=wl.SCENES)
    ap.add_argument("--id", dest="lane_id", help="the lane id (also the file name)")
    ap.add_argument("--at", type=float,
                    help="UT to jump to before the view (from a reading run's log); "
                         "absent = hold 15 s before the next launch (liftoff: at the launch)")
    ap.add_argument("--focus-pid", type=int,
                    help="LoadGame focusPid: the vessel the flight camera starts on")
    ap.add_argument("--map-focus", default="Kerbin", help="transfer: the body MapCamera frames")
    ap.add_argument("--map-distance", type=float, default=0.0,
                    help="transfer: camera distance in metres from the focus body's centre")
    ap.add_argument("--map-pitch", type=float, default=wl.DEFAULT_MAP_PITCH)
    ap.add_argument("--replay-rp",
                    help="replay mode: rewind to this rewind point (a ListHandles handle, "
                         "rp1, rp2, ...) instead of looping the tree")
    ap.add_argument("--replay-slot", type=int,
                    help="replay mode: the slot the rewind re-flies")
    ap.add_argument("--coast-to", type=float,
                    help="replay mode: coast on rails to this UT before the 1x ride to --at")
    ap.add_argument("--watch-vessel",
                    help="watch this vessel launch guid (EnterWatchMode vessel=; the "
                         "recordedVesselGuid in the save) instead of the tree's first "
                         "watchable member")
    ap.add_argument("--budget", type=int, default=900, help="unattended run budget, seconds")
    ap.add_argument("--count-min", type=int)
    ap.add_argument("--count-max", type=int)
    ap.add_argument("--tag", action="append", default=[], help="extra tag (repeatable)")
    ap.add_argument("--note", action="append", default=[],
                    help="a header comment line (repeatable)")
    ap.add_argument("--description", default="")
    ap.add_argument("--out", help="write here instead of the default scenarios path")
    ap.add_argument("--print", action="store_true", help="print the spec, write nothing")
    ap.add_argument("--force", action="store_true", help="overwrite an existing file")
    return ap


def canonical_argv(ns) -> List[str]:
    """The argv the spec header records: every input that shapes the spec, in a
    fixed order, nothing about where it was written."""
    out = ["--save", ns.save, "--tree", ns.tree, "--scene", ns.scene, "--id", ns.lane_id]
    if ns.at is not None:
        out += ["--at", wl._fmt_num(ns.at)]
    if ns.focus_pid is not None:
        out += ["--focus-pid", str(ns.focus_pid)]
    if ns.replay_rp:
        out += ["--replay-rp", ns.replay_rp]
    if ns.replay_slot is not None:
        out += ["--replay-slot", str(ns.replay_slot)]
    if ns.coast_to is not None:
        out += ["--coast-to", wl._fmt_num(ns.coast_to)]
    if ns.watch_vessel:
        out += ["--watch-vessel", ns.watch_vessel]
    if wl.SCENE_VIEW.get(ns.scene) == wl.VIEW_MAP:
        out += ["--map-focus", ns.map_focus, "--map-distance", wl._fmt_num(ns.map_distance)]
        if ns.map_pitch != wl.DEFAULT_MAP_PITCH:
            out += ["--map-pitch", wl._fmt_num(ns.map_pitch)]
    if ns.budget != 900:
        out += ["--budget", str(ns.budget)]
    if ns.count_min is not None:
        out += ["--count-min", str(ns.count_min), "--count-max", str(ns.count_max)]
    for t in ns.tag:
        out += ["--tag", t]
    for n in ns.note:
        out += ["--note", n]
    if ns.description:
        out += ["--description", ns.description]
    return out


def _quote(argv: Sequence[str]) -> str:
    # Plain shlex quoting keeps the header copy-pasteable in Git Bash and PowerShell
    # for the values a lane uses (no single quotes inside notes).
    return " ".join(shlex.quote(a) for a in argv)


def read_trees(save: str, harness_dir: str):
    path = os.path.join(harness_dir, save.replace("/", os.sep), "persistent.sfs")
    if not os.path.isfile(path):
        raise SystemExit("no persistent.sfs at %s (stage a local save with "
                         "tools/stage_local_fixture.py)" % path)
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        trees, err = wl.read_save_trees(fh.read())
    if err:
        raise SystemExit("cannot parse %s: %s" % (path, err))
    return trees


def params_from(ns, harness_dir: str) -> wl.WatchLaneParams:
    trees = read_trees(ns.save, harness_dir)
    tree_id, reason = wl.resolve_tree_handle(ns.tree, trees)
    if tree_id is None:
        listing = "\n".join(wl.format_tree_table(trees)) or "(no committed trees)"
        raise SystemExit("tree %r does not resolve (%s); the save's trees:\n%s"
                         % (ns.tree, reason, listing))
    return wl.WatchLaneParams(
        lane_id=ns.lane_id, scene=ns.scene, save_template=ns.save, tree_id=tree_id,
        description=ns.description, tree_handle=ns.tree, focus_pid=ns.focus_pid,
        at_ut=ns.at, map_focus=ns.map_focus, map_distance=ns.map_distance,
        map_pitch=ns.map_pitch, budget_seconds=ns.budget, count_min=ns.count_min,
        count_max=ns.count_max, extra_tags=tuple(ns.tag), notes=tuple(ns.note),
        replay_rp=ns.replay_rp or "", replay_slot=ns.replay_slot, coast_ut=ns.coast_to,
        watch_vessel=ns.watch_vessel or "",
        command=_quote(canonical_argv(ns)))


def generate_text(argv: Sequence[str], harness_dir: str = HARNESS) -> str:
    ns = build_parser().parse_args(list(argv))
    return wl.build_watch_spec(params_from(ns, harness_dir))


def recorded_argv(spec_text: str) -> Optional[List[str]]:
    """The generator argv a committed watch lane's header records, or None."""
    for line in spec_text.splitlines():
        if line.startswith(wl.GENERATED_WITH_PREFIX):
            return shlex.split(line[len(wl.GENERATED_WITH_PREFIX):])
    return None


def default_out(ns, harness_dir: str) -> str:
    sub = ("scenarios/local" if ns.save.replace("\\", "/").startswith(LOCAL_PREFIX)
           else "scenarios")
    return os.path.join(harness_dir, sub.replace("/", os.sep), "%s.toml" % ns.lane_id)


def main(argv: Optional[Sequence[str]] = None) -> int:
    ap = build_parser()
    ns = ap.parse_args(argv)
    ns.save = ns.save.replace("\\", "/").rstrip("/")
    if ns.list:
        for line in wl.format_tree_table(read_trees(ns.save, HARNESS)):
            print(line)
        return 0
    missing = [n for n, v in (("--tree", ns.tree), ("--scene", ns.scene), ("--id", ns.lane_id))
               if not v]
    if missing:
        ap.error("missing %s" % ", ".join(missing))
    try:
        text = wl.build_watch_spec(params_from(ns, HARNESS))
    except ValueError as exc:
        ap.error(str(exc))
    if ns.print:
        sys.stdout.write(text)
        return 0
    out = ns.out or default_out(ns, HARNESS)
    if os.path.exists(out) and not ns.force:
        raise SystemExit("%s exists (pass --force to overwrite)" % out)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    rel = os.path.relpath(out, os.path.dirname(HARNESS)).replace(os.sep, "/")
    print("wrote %s" % rel)
    flag = ("--spec %s" % rel) if "/local/" in rel else ("--id %s" % ns.lane_id)
    print("unattended:  python harness/run.py %s" % flag)
    print("watch it:    python harness/run.py %s --hold" % flag)
    return 0


if __name__ == "__main__":
    sys.exit(main())
