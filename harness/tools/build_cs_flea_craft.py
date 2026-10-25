#!/usr/bin/env python3
"""Build `CS Flea.craft`, the scripted campaign's first craft, BY CONSTRUCTION.

The stock Jumping Flea with a THIRD Mystery Goo unit, so CS-1 can open one Goo on
the pad, one in low flight and one after landing (Goo is not rerunnable without a
scientist, and each situation pays its own science). Every part is on the stock
start node, so a brand-new Science or Career game can build it on day one.

THE BASE is the committed shared `harness/fixtures/ships/Jumping Flea.craft`, copied
verbatim except for:
  - the `ship =` name (`CS Flea`), so it is a different craft in the VAB picker;
  - the Goo pair, which becomes a radial TRIPLE: the first Goo is kept byte for byte
    and the other two are that Goo rotated about the pod's vertical (world Y) axis by
    120 and 240 degrees - its `pos` / `attPos0` rotated as vectors and its `rot` /
    `attRot0` pre-multiplied by the Y rotation. Each Goo's `sym` lines name the other
    two. The third Goo is a fresh part id and persistentId; its tail (EVENTS / ACTIONS
    / PARTDATA / MODULE blocks) is the first Goo's, verbatim, so no module block is
    invented.

THE ROTATION IS VALIDATED against stock data: rotating the first Goo by 180 degrees
reproduces the stock craft's second Goo's `pos` and `rot` (to the file's printed
precision, up to the quaternion sign), which `test_cs_flea_craft.py` asserts.

Single consumer (`cs-science-new`), so per `fixtures/shared-ships.toml` it lives in
that fixture, not in the shared library. `--check` re-derives and compares bytes.

Usage:
    python harness/tools/build_cs_flea_craft.py          # write
    python harness/tools/build_cs_flea_craft.py --check  # exit 1 on drift
"""

from __future__ import annotations

import argparse
import math
import os
import re
import sys
from typing import List, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)

BASE_CRAFT = os.path.join(_HARNESS, "fixtures", "ships", "Jumping Flea.craft")
OUT_CRAFT = os.path.join(_HARNESS, "fixtures", "saves", "cs-science-new", "Ships", "VAB",
                         "CS Flea.craft")
SHIP_NAME = "CS Flea"

GOO_A = "GooExperiment_4294442852"   # kept verbatim
GOO_B = "GooExperiment_4294442798"   # re-placed at +120 deg
GOO_C = "GooExperiment_4294442744"   # new, at +240 deg
GOO_C_PERSISTENT_ID = "2716334921"


def _fmt(x: float) -> str:
    # The file's own float style is C#'s round-trip "R"; 9 significant digits keep
    # the derived values stable and readable.
    if abs(x) < 5e-10:
        return "0"
    return ("%.9g" % x)


def _vec(text: str) -> Tuple[float, ...]:
    return tuple(float(v) for v in text.split(","))


def _qmul(a, b):
    """Hamilton product, quaternions as (x, y, z, w)."""
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def y_rotation(deg: float):
    h = math.radians(deg) / 2.0
    return (0.0, math.sin(h), 0.0, math.cos(h))


def rotate_vec(q, v):
    """v' = q v q^-1 for a unit quaternion."""
    qv = (v[0], v[1], v[2], 0.0)
    conj = (-q[0], -q[1], -q[2], q[3])
    r = _qmul(_qmul(q, qv), conj)
    return (r[0], r[1], r[2])


def rotate_about_y(pos, rot, deg: float):
    """(pos, rot) of a part rotated rigidly about world Y by ``deg``."""
    q = y_rotation(deg)
    return rotate_vec(q, pos), _qmul(q, rot)


def _split_parts(text: str) -> Tuple[str, List[str]]:
    """(header, [PART block text, ...]); each block starts at its 'PART\\n{' line."""
    idx = [m.start() for m in re.finditer(r"^PART\n\{\n", text, flags=re.M)]
    if not idx:
        raise ValueError("no PART blocks")
    header = text[:idx[0]]
    blocks = [text[s:e] for s, e in zip(idx, idx[1:] + [len(text)])]
    return header, blocks


def _scalar(block: str, key: str) -> str:
    m = re.search(r"^\t%s = (.*)$" % re.escape(key), block, flags=re.M)
    if not m:
        raise ValueError("key %s missing" % key)
    return m.group(1)


def _set_scalar(block: str, key: str, value: str) -> str:
    out, n = re.subn(r"^(\t%s = ).*$" % re.escape(key),
                     lambda m: m.group(1) + value, block, count=1, flags=re.M)
    if n != 1:
        raise ValueError("key %s missing" % key)
    return out


def _with_sym(block: str, others: List[str]) -> str:
    lines = block.split("\n")
    out = []
    placed = False
    for line in lines:
        if line.startswith("\tsym = "):
            if not placed:
                out.extend("\tsym = %s" % o for o in others)
                placed = True
            continue
        out.append(line)
    if not placed:
        raise ValueError("no sym line")
    return "\n".join(out)


def _placed(goo_a: str, part_id: str, deg: float) -> str:
    pos, rot = rotate_about_y(_vec(_scalar(goo_a, "pos")), _vec(_scalar(goo_a, "rot")), deg)
    att_pos0, att_rot0 = rotate_about_y(_vec(_scalar(goo_a, "attPos0")),
                                        _vec(_scalar(goo_a, "attRot0")), deg)
    block = _set_scalar(goo_a, "part", part_id)
    block = _set_scalar(block, "pos", ",".join(_fmt(c) for c in pos))
    block = _set_scalar(block, "attPos0", ",".join(_fmt(c) for c in att_pos0))
    block = _set_scalar(block, "rot", ",".join(_fmt(c) for c in rot))
    block = _set_scalar(block, "attRot0", ",".join(_fmt(c) for c in att_rot0))
    return block


def build(base_text: str) -> str:
    """The derived craft. KSP writes .craft files with CRLF line ends; the
    derivation works on LF and restores the base file's line ends."""
    crlf = "\r\n" in base_text
    header, blocks = _split_parts(base_text.replace("\r\n", "\n"))
    header = re.sub(r"^ship = .*$", "ship = %s" % SHIP_NAME, header, count=1, flags=re.M)
    by_id = {_scalar(b, "part"): i for i, b in enumerate(blocks)}
    for pid in (GOO_A, GOO_B):
        if pid not in by_id:
            raise ValueError("base craft lacks %s" % pid)
    goo_a = blocks[by_id[GOO_A]]
    a = _with_sym(goo_a, [GOO_B, GOO_C])
    b = _with_sym(_set_scalar(_placed(goo_a, GOO_B, 120.0), "persistentId",
                              _scalar(blocks[by_id[GOO_B]], "persistentId")),
                  [GOO_C, GOO_A])
    c = _with_sym(_set_scalar(_placed(goo_a, GOO_C, 240.0), "persistentId",
                              GOO_C_PERSISTENT_ID),
                  [GOO_A, GOO_B])
    blocks[by_id[GOO_A]] = a
    blocks[by_id[GOO_B]] = b
    blocks.insert(by_id[GOO_B] + 1, c)
    out = header + "".join(blocks)
    return out.replace("\n", "\r\n") if crlf else out


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--check", action="store_true",
                    help="compare against the committed craft, exit 1 on drift")
    args = ap.parse_args(argv)
    with open(BASE_CRAFT, "r", encoding="utf-8", newline="") as fh:
        base = fh.read()
    built = build(base)
    if args.check:
        try:
            with open(OUT_CRAFT, "r", encoding="utf-8", newline="") as fh:
                committed = fh.read()
        except FileNotFoundError:
            print("MISSING %s" % OUT_CRAFT)
            return 1
        if committed != built:
            print("DRIFT %s (re-run without --check)" % OUT_CRAFT)
            return 1
        print("OK %s" % OUT_CRAFT)
        return 0
    os.makedirs(os.path.dirname(OUT_CRAFT), exist_ok=True)
    with open(OUT_CRAFT, "w", encoding="utf-8", newline="") as fh:
        fh.write(built)
    print("wrote %s" % OUT_CRAFT)
    return 0


if __name__ == "__main__":
    sys.exit(main())
