#!/usr/bin/env python3
"""Race content dump diff for spike S6 step (f) (spike branch only, plan r0-spikes.md).

The armed spike (PARSEK_SPIKE=S6, PARSEK_SPIKE_S6_CONTENT=1, PARSEK_SPIKE_S6_LABEL=<run>)
writes <KSP>/parsek-spike/S6/content-<run>-mainmenu.cfg at the main menu and
content-<run>-rnd-<source>.cfg once the R&D building is read. This tool compares them.
Read-only.

    python spikes/r0/S6/content_diff.py diff <A-mainmenu.cfg> <B-mainmenu.cfg> [--expect-same]
        GameData folders, tech nodes (by id), parts (by name: every changed key of the
        partConfig, entry cost, tech), PARTUPGRADE (by name), EXPERIMENT_DEFINITION (by
        id), bodies' science values. --expect-same exits 1 on any difference.
    python spikes/r0/S6/content_diff.py rnd <X-mainmenu.cfg> <X-rnd-*.cfg>
        The main-menu tree read against the R&D building's (exit 1 when they differ).
    python spikes/r0/S6/content_diff.py variants <X-mainmenu.cfg>
        Parts whose ModulePartVariants VARIANT nodes can change gameplay values (a cost or
        mass delta, or EXTRA_INFO / MODULE / NODES / UPGRADE children), and the PARTUPGRADE count.
    python spikes/r0/S6/content_diff.py --selftest

Stdlib only; ASCII only.
"""

import argparse
import os
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.dont_write_bytecode = True  # no __pycache__ beside the tools in the repo tree
import s6cfg  # noqa: E402

COSMETIC_VARIANT_CHILDREN = ("GAMEOBJECTS", "TEXTURE", "MATERIAL", "EXTRA_INFO_COSMETIC")


def flatten(node, prefix=""):
    """{path: value} for every value under node; repeated children are keyed by their
    `name` value (or `id`) and an occurrence index, so a reorder of unnamed nodes shows."""
    out = {}
    counts = {}
    for k, v in node.values:
        key = prefix + k
        n = counts.get(key, 0)
        counts[key] = n + 1
        out[key if n == 0 else "%s#%d" % (key, n)] = s6cfg.normalize_value(v)
    seen = {}
    for c in node.nodes:
        label = c.get("name") or c.get("id") or ""
        base = "%s%s[%s]" % (prefix, c.name, label)
        idx = seen.get(base, 0)
        seen[base] = idx + 1
        out.update(flatten(c, "%s#%d/" % (base, idx)))
    return out


def section(root, name):
    found = root.children(name)
    return found[0] if found else s6cfg.Node(name)


def keyed(nodes, key):
    out = {}
    for n in nodes:
        out[n.get(key) or "<unnamed>"] = n
    return out


def diff_maps(a, b):
    """(only_a, only_b, changed[(key, va, vb)])."""
    only_a = sorted(set(a) - set(b))
    only_b = sorted(set(b) - set(a))
    changed = [(k, a[k], b[k]) for k in sorted(set(a) & set(b)) if a[k] != b[k]]
    return only_a, only_b, changed


def tech_map(lines):
    return {l.split("|", 1)[0]: l for l in lines}


def cmd_diff(pa, pb, expect_same, out=sys.stdout):
    a = s6cfg.parse_file(pa)
    b = s6cfg.parse_file(pb)
    total = 0
    out.write("A=%s label=%s\nB=%s label=%s\n" % (pa, a.get("label"), pb, b.get("label")))

    fa = set(section(a, "GAMEDATA").get_all("folder"))
    fb = set(section(b, "GAMEDATA").get_all("folder"))
    out.write("== GameData folders: onlyA=%s onlyB=%s\n" % (sorted(fa - fb), sorted(fb - fa)))
    total += len(fa ^ fb)

    for sec in ("TECHTREE_DB", "TECHTREE_FILE"):
        oa, ob, ch = diff_maps(tech_map(section(a, sec).get_all("line")), tech_map(section(b, sec).get_all("line")))
        out.write("== %s: onlyA=%d onlyB=%d changed=%d\n" % (sec, len(oa), len(ob), len(ch)))
        for k in oa:
            out.write("  onlyA %s\n" % k)
        for k in ob:
            out.write("  onlyB %s\n" % k)
        for k, va, vb in ch:
            out.write("  changed %s: %s -> %s\n" % (k, va, vb))
        if sec == "TECHTREE_DB":
            total += len(oa) + len(ob) + len(ch)

    pa_map = keyed(section(a, "PARTS").children("PART"), "name")
    pb_map = keyed(section(b, "PARTS").children("PART"), "name")
    oa, ob, _ = diff_maps({k: 1 for k in pa_map}, {k: 1 for k in pb_map})
    changed_parts = 0
    lines = []
    for name in sorted(set(pa_map) & set(pb_map)):
        fa_ = flatten(pa_map[name])
        fb_ = flatten(pb_map[name])
        for k in ("configFile", "partUrl"):
            fa_.pop(k, None)
            fb_.pop(k, None)
        x, y, ch = diff_maps(fa_, fb_)
        if x or y or ch:
            changed_parts += 1
            lines.append("  part %s: removed=%d added=%d changed=%d" % (name, len(x), len(y), len(ch)))
            for k in x[:20]:
                lines.append("    - %s = %s" % (k, fa_[k]))
            for k in y[:20]:
                lines.append("    + %s = %s" % (k, fb_[k]))
            for k, va, vb in ch[:40]:
                lines.append("    ~ %s: %s -> %s" % (k, va, vb))
    out.write("== parts: A=%d B=%d onlyA=%d onlyB=%d changed=%d\n"
              % (len(pa_map), len(pb_map), len(oa), len(ob), changed_parts))
    for k in oa:
        out.write("  onlyA %s\n" % k)
    for k in ob:
        out.write("  onlyB %s\n" % k)
    for l in lines:
        out.write(l + "\n")
    total += len(oa) + len(ob) + changed_parts

    for sec, child, key in (("PARTUPGRADES", "PARTUPGRADE", "name"), ("EXPERIMENTS", "EXPERIMENT_DEFINITION", "id"),
                            ("BODIES", "BODY", "name")):
        ma = {k: flatten(v) for k, v in keyed(section(a, sec).children(child), key).items()}
        mb = {k: flatten(v) for k, v in keyed(section(b, sec).children(child), key).items()}
        x, y, _ = diff_maps({k: 1 for k in ma}, {k: 1 for k in mb})
        ch = [k for k in sorted(set(ma) & set(mb)) if ma[k] != mb[k]]
        out.write("== %s: A=%d B=%d onlyA=%s onlyB=%s changed=%s\n" % (sec, len(ma), len(mb), x, y, ch))
        total += len(x) + len(y) + len(ch)

    out.write("diff: differences=%d result=%s\n" % (total, "SAME" if total == 0 else "DIFFERENT"))
    return 1 if expect_same and total else 0


def cmd_rnd(pm, pr, out=sys.stdout):
    m = tech_map(section(s6cfg.parse_file(pm), "TECHTREE_DB").get_all("line"))
    r = tech_map(section(s6cfg.parse_file(pr), "TECHTREE_RND").get_all("line"))
    oa, ob, ch = diff_maps(m, r)
    for k in oa:
        out.write("only-mainmenu %s\n" % m[k])
    for k in ob:
        out.write("only-rnd %s\n" % r[k])
    for k, va, vb in ch:
        out.write("differs %s | %s\n" % (va, vb))
    ok = not (oa or ob or ch) and len(m) > 0
    out.write("rnd: mainmenu=%d rnd=%d onlyMainmenu=%d onlyRnd=%d changed=%d result=%s\n"
              % (len(m), len(r), len(oa), len(ob), len(ch), "PASS" if ok else "FAIL"))
    return 0 if ok else 1


def variant_gameplay(part):
    """[(variant, reason)] for VARIANT nodes that can change gameplay values."""
    found = []
    cfg = part.children("CONFIG")
    if not cfg:
        return found
    for mod in cfg[0].children("MODULE"):
        if mod.get("name") != "ModulePartVariants":
            continue
        for v in mod.children("VARIANT"):
            reasons = []
            for k in ("cost", "mass"):
                val = v.get(k)
                try:
                    if val is not None and float(val) != 0.0:
                        reasons.append("%s=%s" % (k, val))
                except ValueError:
                    reasons.append("%s=%s" % (k, val))
            for c in v.nodes:
                if c.name not in COSMETIC_VARIANT_CHILDREN:
                    reasons.append("child " + c.name)
            if reasons:
                found.append((v.get("name") or "<unnamed>", ", ".join(reasons)))
    return found


def cmd_variants(pm, out=sys.stdout):
    root = s6cfg.parse_file(pm)
    n = 0
    for part in section(root, "PARTS").children("PART"):
        hits = variant_gameplay(part)
        if hits:
            n += 1
            out.write("%s (%s):\n" % (part.get("name"), part.get("configFile")))
            for name, why in hits:
                out.write("    %s: %s\n" % (name, why))
    ups = section(root, "PARTUPGRADES").children("PARTUPGRADE")
    out.write("variants: partsWithGameplayVariants=%d partUpgrades=%d\n" % (n, len(ups)))
    for u in ups:
        out.write("  PARTUPGRADE %s techRequired=%s\n" % (u.get("name"), u.get("techRequired")))
    return 0


def _dump(label, tech_cost, thrust, extra_folder=False, variant_mass="0"):
    folders = ["Squad", "SquadExpansion", "000_Harmony"] + (["ReStock"] if extra_folder else [])
    return (
        "label = %s\nphase = mainmenu\nGAMEDATA\n{\n%s}\n" % (label, "".join("\tfolder = %s\n" % f for f in folders))
        + "TECHTREE_DB\n{\n\ttrees = 1\n\tline = start|0|False|\n\tline = basicRocketry|%s|False|start\n}\n" % tech_cost
        + "PARTS\n{\n\tPART\n\t{\n\t\tname = liquidEngine\n\t\tentryCost = 3200\n\t\tconfigFile = x\n\t\tCONFIG\n\t\t{\n"
          "\t\t\tname = liquidEngine\n\t\t\tMODULE\n\t\t\t{\n\t\t\t\tname = ModuleEnginesFX\n\t\t\t\tmaxThrust = %s\n\t\t\t}\n"
          "\t\t\tMODULE\n\t\t\t{\n\t\t\t\tname = ModulePartVariants\n\t\t\t\tVARIANT\n\t\t\t\t{\n\t\t\t\t\tname = Dark\n"
          "\t\t\t\t\tmass = %s\n\t\t\t\t\tGAMEOBJECTS\n\t\t\t\t\t{\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n" % (thrust, variant_mass)
        + "PARTUPGRADES\n{\n}\nEXPERIMENTS\n{\n\tEXPERIMENT_DEFINITION\n\t{\n\t\tid = crewReport\n\t\tbaseValue = 5\n\t}\n}\n"
        + "BODIES\n{\n\tBODY\n\t{\n\t\tname = Kerbin\n\t\tradius = 600000\n\t}\n}\n")


def _selftest():
    import io
    failures = []
    with tempfile.TemporaryDirectory() as tmp:
        paths = {}
        for name, args in (("clean", ("clean", "5", "215")), ("same", ("same", "5", "215")),
                           ("tech", ("tech", "77", "215")), ("stat", ("stat", "5", "999")),
                           ("restock", ("restock", "5", "215", True, "0.01"))):
            paths[name] = os.path.join(tmp, name + ".cfg")
            with open(paths[name], "w") as fh:
                fh.write(_dump(*args))
        buf = io.StringIO()
        if cmd_diff(paths["clean"], paths["same"], True, buf) != 0:
            failures.append("identical dumps differ:\n" + buf.getvalue())
        buf = io.StringIO()
        if cmd_diff(paths["clean"], paths["tech"], True, buf) != 1 or "changed basicRocketry" not in buf.getvalue():
            failures.append("tech patch not shown:\n" + buf.getvalue())
        buf = io.StringIO()
        if cmd_diff(paths["clean"], paths["stat"], True, buf) != 1 or "maxThrust: 215 -> 999" not in buf.getvalue():
            failures.append("stat patch not shown:\n" + buf.getvalue())
        buf = io.StringIO()
        cmd_diff(paths["clean"], paths["restock"], False, buf)
        if "onlyB=['ReStock']" not in buf.getvalue():
            failures.append("folder diff not shown:\n" + buf.getvalue())
        buf = io.StringIO()
        cmd_variants(paths["restock"], buf)
        if "Dark: mass=0.01" not in buf.getvalue():
            failures.append("gameplay variant not listed:\n" + buf.getvalue())
        buf = io.StringIO()
        cmd_variants(paths["clean"], buf)
        if "partsWithGameplayVariants=0" not in buf.getvalue():
            failures.append("cosmetic variant listed:\n" + buf.getvalue())
        rnd = os.path.join(tmp, "rnd.cfg")
        with open(rnd, "w") as fh:
            fh.write("TECHTREE_RND\n{\n\tline = start|0|False|\n\tline = basicRocketry|5|False|start\n}\n")
        if cmd_rnd(paths["clean"], rnd, io.StringIO()) != 0 or cmd_rnd(paths["tech"], rnd, io.StringIO()) != 1:
            failures.append("rnd compare exit codes")
    if failures:
        for f in failures:
            print("SELFTEST FAIL: " + f)
        return 1
    print("content_diff selftest: PASS")
    return 0


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    p.add_argument("--selftest", action="store_true")
    p.add_argument("--expect-same", action="store_true")
    p.add_argument("mode", nargs="?", choices=("diff", "rnd", "variants"))
    p.add_argument("paths", nargs="*")
    a = p.parse_args(argv)
    if a.selftest:
        return _selftest()
    need = {"diff": 2, "rnd": 2, "variants": 1}
    if not a.mode or len(a.paths) != need[a.mode]:
        p.print_usage()
        return 2
    try:
        if a.mode == "diff":
            return cmd_diff(a.paths[0], a.paths[1], a.expect_same)
        if a.mode == "rnd":
            return cmd_rnd(a.paths[0], a.paths[1])
        return cmd_variants(a.paths[0])
    except OSError as exc:
        print("unreadable input: %s" % exc)
        return 2


if __name__ == "__main__":
    sys.exit(main())
