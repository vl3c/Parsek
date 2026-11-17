#!/usr/bin/env python3
"""Spike S3 (plan r0-spikes.md "S3 The four looks ..."): print the report cells.

    python summarize_s3.py summarize --log <spike-r0 KSP.log with the sets>
        [--baseline-log <spike-r0 KSP.log of a launch WITHOUT the sets>]
        [--nodlc-log <spike-r0-nodlc KSP.log>] [--nodlc-save <its persistent.sfs>]
        [--colors-csv <look,hex rows, hand-filled>]
        [--background <#RRGGBB> | --map-png <a map-view screenshot>]
        [--out <summary.md>]
    python summarize_s3.py --selftest

Reads only the [Parsek][...][Spike.S3] lines the spike writes (SpikeS3Looks.cs,
SpikeS3Ghosts.cs) and stock's "Suit combo ... validated." lines. Prints one verdict line
per cell, each PASS, FAIL or OWED (input missing), then the evidence lines:

  COMBOS     ExtraCombos grows by the set size (4: two sets x two genders) between the
             baseline launch and the launch with the sets; all four set combos listed.
  DRESS      every dress step of the run took (ok=true).
  LOOKS      each of the four stock suits is worn by a kerbal whose EVA part and IVA
             prefab both resolve (the "all four suits show" precondition).
  WRITEBACK  informational (--nodlc-save): what the no-DLC game wrote back into its own
             save for each kerbal (suit, comboId); stock's Save writes the getter, which
             resets an uninstalled suit to Default.
  NODLC      the no-DLC load: every kerbal saved in a DLC suit loads, and each saved suit
             maps to ONE (suit, EVA part) pair (deterministic); the mapping is printed.
  GHOST-SHARED  the prefab's three shared materials (instance id and texture) are the
             same before the first and after the last of the 20 builds, for every part.
  GHOST-COST the per-ghost swap adds at most 2 ms (median of the 10 swaps, per part).
  GHOST-PARTS informational: which of the eight EVA parts resolved, which fell back.
  COLORS     the four look colors: each at least 3:1 contrast against the map
             background, and every pair's hue at least 30 deg apart. The colors come
             from --colors-csv when given (the hand-picked ones), else from the in-game
             texture samples (chromatic dominant color, male prefab body texture).

Stdlib only. Never writes anywhere but --out.
"""

import argparse
import csv
import importlib.util
import io
import os
import re
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))

SPIKE_LINE_RE = re.compile(r"\[Parsek\]\[(INFO|WARN|VERBOSE)\]\[Spike\.S3\] (.*?)\s*$")
BODY_RE = re.compile(r"^([a-z][a-z ]*): ?(.*)$")
# Stock SuitCombos.ValidateCombo: "Suit combo " + displayName + "/" + name + " validated."
VALIDATED_RE = re.compile(r"Suit combo (.+)/(\S+) validated\.")

LOOKS = ["Default", "Slim", "Vintage", "Future"]
DLC_SUITS = ["Vintage", "Future"]
SET_SIZE = 4
MIN_CONTRAST = 3.0
MIN_HUE = 30.0
MAX_SWAP_MS = 2.0
ACHROMATIC_SAT = 0.15
PARSEK_PREFIX = "ParsekS3_"


# ------------------------------------------------------------------ parsing

def parse_fields(text):
    out = {}
    for tok in text.split():
        if "=" in tok:
            k, v = tok.split("=", 1)
            out[k] = v
    return out


def parse_log(lines):
    """List of (kind, fields) for every Spike.S3 line, plus the stock validated count."""
    recs, validated = [], 0
    for line in lines:
        if VALIDATED_RE.search(line):
            validated += 1
        m = SPIKE_LINE_RE.search(line)
        if not m:
            continue
        b = BODY_RE.match(m.group(2))
        if not b:
            recs.append((m.group(2), {}))
            continue
        recs.append((b.group(1), parse_fields(b.group(2))))
    return recs, validated


def of_kind(recs, kind):
    return [f for k, f in recs if k == kind]


def read_lines(path):
    with io.open(path, encoding="utf-8", errors="replace") as f:
        return f.read().splitlines()


# ------------------------------------------------------------------ color math

def parse_hex(text):
    if not text:
        return None
    s = text.strip().lstrip("#")
    if len(s) == 3:
        s = "".join(c * 2 for c in s)
    if len(s) == 8:
        s = s[:6]
    if len(s) != 6:
        return None
    try:
        v = int(s, 16)
    except ValueError:
        return None
    return ((v >> 16) & 255, (v >> 8) & 255, v & 255)


def hexstr(rgb):
    return "#%02X%02X%02X" % tuple(rgb)


def _lin(c):
    s = c / 255.0
    return s / 12.92 if s <= 0.04045 else ((s + 0.055) / 1.055) ** 2.4


def luminance(rgb):
    r, g, b = rgb
    return 0.2126 * _lin(r) + 0.7152 * _lin(g) + 0.0722 * _lin(b)


def contrast(a, b):
    la, lb = luminance(a), luminance(b)
    hi, lo = max(la, lb), min(la, lb)
    return (hi + 0.05) / (lo + 0.05)


def hsv(rgb):
    r, g, b = (c / 255.0 for c in rgb)
    mx, mn = max(r, g, b), min(r, g, b)
    d = mx - mn
    s = 0.0 if mx <= 0 else d / mx
    if d <= 0:
        return 0.0, s, mx
    if mx == r:
        h = ((g - b) / d) % 6.0
    elif mx == g:
        h = (b - r) / d + 2.0
    else:
        h = (r - g) / d + 4.0
    return (h * 60.0) % 360.0, s, mx


def hue_distance(a, b):
    d = abs(a - b) % 360.0
    return 360.0 - d if d > 180.0 else d


def judge_colors(named, background):
    """named: list of (look, '#hex'). Returns (contrast_ok, hue_ok, lines, min_c, min_h)."""
    lines = []
    bg = parse_hex(background)
    if bg is None or not named:
        return False, False, ["background or colors missing"], None, None
    c_ok = h_ok = True
    min_c = min_h = None
    parsed = []
    for look, text in named:
        rgb = parse_hex(text)
        if rgb is None:
            c_ok = h_ok = False
            lines.append("color: look=%s hex=%s result=FAIL why=unparsable" % (look, text))
            continue
        cr = contrast(rgb, bg)
        h, s, _ = hsv(rgb)
        chroma = s >= ACHROMATIC_SAT
        min_c = cr if min_c is None else min(min_c, cr)
        ok = cr >= MIN_CONTRAST
        c_ok = c_ok and ok
        lines.append("color: look=%s hex=%s contrast=%.2f hue=%s sat=%.2f result=%s" % (
            look, hexstr(rgb), cr, ("%.0f" % h) if chroma else "achromatic", s, "PASS" if ok else "FAIL"))
        parsed.append((look, rgb, h, chroma))
    for i in range(len(parsed)):
        for j in range(i + 1, len(parsed)):
            la, ra, ha, ca = parsed[i]
            lb, rb, hb, cb = parsed[j]
            mutual = contrast(ra, rb)
            if not (ca and cb):
                h_ok = False
                min_h = 0.0
                lines.append("hue pair: a=%s b=%s distance=- mutualContrast=%.2f result=FAIL why=achromatic" % (la, lb, mutual))
                continue
            d = hue_distance(ha, hb)
            min_h = d if min_h is None else min(min_h, d)
            ok = d >= MIN_HUE
            h_ok = h_ok and ok
            lines.append("hue pair: a=%s b=%s distance=%.0f mutualContrast=%.2f result=%s" % (
                la, lb, d, mutual, "PASS" if ok else "FAIL"))
    return c_ok, h_ok, lines, min_c, min_h


def png_median_color(path):
    """Per-channel median of a screenshot (the map background is most of the frame)."""
    spec = importlib.util.spec_from_file_location("make_suit_pngs", os.path.join(HERE, "make_suit_pngs.py"))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    with open(path, "rb") as f:
        width, height, ch, rows = mod.decode_png(f.read())
    chans = [[], [], []]
    step = max(1, (width * height) // 200000)  # sample at most ~200k pixels
    k = 0
    for row in rows:
        for x in range(width):
            if k % step == 0:
                for c in range(3):
                    chans[c].append(row[x * ch + c])
            k += 1
    med = []
    for c in chans:
        c.sort()
        med.append(c[len(c) // 2])
    return hexstr(med)


# ------------------------------------------------------------------ cells

def combos_cell(recs, baseline_recs, validated, out):
    summ = [f for f in of_kind(recs, "combos summary") if f.get("reason") == "mainmenu"] or of_kind(recs, "combos summary")
    if not summ:
        out.append("COMBOS: OWED (no combos summary line; was S3 armed?)")
        return
    s = summ[0]
    extra, sets = int(s.get("extra", "0")), int(s.get("parsekSets", "0"))
    names = sorted({f.get("name") for f in of_kind(recs, "combo")
                    if f.get("source") == "extra" and f.get("name", "").startswith(PARSEK_PREFIX)})
    base = None
    if baseline_recs is not None:
        bs = [f for f in of_kind(baseline_recs, "combos summary") if f.get("reason") == "mainmenu"] or of_kind(baseline_recs, "combos summary")
        if bs:
            base = int(bs[0].get("extra", "0"))
    if base is None:
        ok = sets == SET_SIZE
        out.append("COMBOS: %s extra=%d parsekSets=%d baseline=none (growth not measured; parsekSets used)" % (
            "PASS" if ok else "FAIL", extra, sets))
    else:
        growth = extra - base
        ok = growth == SET_SIZE and sets == SET_SIZE
        out.append("COMBOS: %s extra=%d baseline=%d growth=%d expected=%d parsekSets=%d" % (
            "PASS" if ok else "FAIL", extra, base, growth, SET_SIZE, sets))
    out.append("  default=%s stock=%s otherExtra=%s configs=%s stockValidatedLines=%d" % (
        s.get("default"), s.get("stock"), s.get("otherExtra"), s.get("configs"), validated))
    out.append("  set combos: " + (", ".join(names) if names else "none"))


def dress_cell(recs, out):
    rows = of_kind(recs, "dress")
    if not rows:
        out.append("DRESS: OWED (no dress line)")
        return
    bad = [f for f in rows if f.get("ok") != "true"]
    out.append("DRESS: %s steps=%d failed=%d" % ("PASS" if not bad else "FAIL", len(rows), len(bad)))
    for f in rows:
        out.append("  phase=%s %s[%s] %s look=%s suit=%s comboId=%s ok=%s reason=%s" % (
            f.get("phase"), f.get("pool"), f.get("index"), f.get("name", "-"), f.get("look"),
            f.get("suit"), f.get("comboId"), f.get("ok"), f.get("reason")))


def looks_cell(recs, out):
    rows = of_kind(recs, "roster")
    if not rows:
        out.append("LOOKS: OWED (no roster line)")
        return
    seen = {}
    for f in rows:
        if f.get("evaFound") == "true" and f.get("ivaFound") == "true":
            seen.setdefault(f.get("suit"), f.get("name"))
    missing = [s for s in LOOKS if s not in seen]
    out.append("LOOKS: %s %s" % ("PASS" if not missing else "FAIL",
               " ".join("%s=%s" % (s, seen.get(s, "missing")) for s in LOOKS)))


def nodlc_cell(nodlc_recs, out):
    if nodlc_recs is None:
        out.append("NODLC: OWED (no --nodlc-log)")
        return
    exp = of_kind(nodlc_recs, "expansions")
    if exp and (exp[0].get("MakingHistory") == "true" or exp[0].get("Serenity") == "true"):
        out.append("NODLC: FAIL (the no-DLC log shows an expansion installed: %s)" % " ".join(
            "%s=%s" % kv for kv in sorted(exp[0].items())))
        return
    loads = [f for f in of_kind(nodlc_recs, "pcm load") if f.get("nodeSuit") in DLC_SUITS]
    if not loads:
        out.append("NODLC: OWED (no pcm load line of a kerbal saved in a DLC suit)")
        return
    mapping = {}
    for f in loads:
        mapping.setdefault(f.get("nodeSuit"), set()).add((f.get("suit"), f.get("evaPart")))
    det = all(len(v) == 1 for v in mapping.values())
    out.append("NODLC: %s deterministic=%s %s" % (
        "PASS" if det else "FAIL", "true" if det else "false",
        " ".join("%s->%s" % (k, "|".join("%s/%s" % p for p in sorted(v))) for k, v in sorted(mapping.items()))))
    for f in loads:
        out.append("  pcm load %s nodeSuit=%s nodeCombo=%s -> suit=%s comboId=%s evaPart=%s" % (
            f.get("name"), f.get("nodeSuit"), f.get("nodeCombo"), f.get("suit"), f.get("comboId"), f.get("evaPart")))
    for f in of_kind(nodlc_recs, "eva live"):
        out.append("  eva live %s part=%s suit=%s comboId=%s bodyTex=%s" % (
            f.get("crew"), f.get("part"), f.get("suit"), f.get("comboId"), f.get("bodyTex")))


def parse_sfs_kerbals(lines):
    """name -> {key: value} for every KERBAL node of a save (top-level values only)."""
    out, depth, cur, pending = {}, 0, None, None
    for raw in lines:
        line = raw.split("//", 1)[0].strip()
        if not line:
            continue
        if line == "{":
            depth += 1
            if pending == "KERBAL" and cur is None:
                cur, cur_depth = {}, depth
            pending = None
            continue
        if line == "}":
            if cur is not None and depth == cur_depth:
                if "name" in cur:
                    out[cur["name"]] = cur
                cur = None
            depth -= 1
            continue
        if "=" in line:
            if cur is not None and depth == cur_depth:
                k, v = line.split("=", 1)
                cur.setdefault(k.strip(), v.strip())
            continue
        pending = line
    return out


def writeback_cell(nodlc_recs, save_lines, out):
    if save_lines is None:
        out.append("WRITEBACK: OWED (no --nodlc-save)")
        return
    kerbals = parse_sfs_kerbals(save_lines)
    loads = {f.get("name", "").replace("_", " "): f for f in of_kind(nodlc_recs or [], "pcm load")
             if f.get("nodeSuit") in DLC_SUITS}
    if not loads:
        out.append("WRITEBACK: OWED (no DLC-suit kerbal in the no-DLC log)")
        return
    rows = []
    for name in sorted(loads):
        k = kerbals.get(name)
        rows.append("%s:%s->%s/combo=%s" % (name.replace(" ", "_"), loads[name].get("nodeSuit"),
                                             k.get("suit", "-") if k else "missing",
                                             k.get("comboId", "-") if k else "-"))
    kept = all(kerbals.get(n, {}).get("suit") == loads[n].get("nodeSuit") for n in loads)
    out.append("WRITEBACK: %s %s" % ("suit-kept" if kept else "suit-rewritten", " ".join(rows)))


def ghost_cells(recs, out, label=""):
    parts = of_kind(recs, "ghost part")
    shared = of_kind(recs, "shared material")
    tag = ("[%s] " % label) if label else ""
    if not parts:
        out.append("GHOST-SHARED: %sOWED (no ghost part line)" % tag)
        out.append("GHOST-COST: %sOWED" % tag)
        return
    changed = [f.get("part") for f in shared if f.get("unchanged") != "true"]
    found = [f for f in parts if f.get("found") == "true" and f.get("fallback") == "none"]
    out.append("GHOST-SHARED: %s%s parts=%d changed=%s" % (
        tag, "PASS" if shared and not changed else "FAIL", len(shared), ",".join(changed) or "none"))
    if not found:
        out.append("GHOST-COST: %sOWED (no part built)" % tag)
    else:
        worst = max(found, key=lambda f: float(f.get("swapMedianMs", "0")))
        wmax = max(float(f.get("swapMaxMs", "0")) for f in found)
        ok = float(worst.get("swapMedianMs", "0")) <= MAX_SWAP_MS
        bad_tex = [f.get("part") for f in found if f.get("textureOk") != "true"]
        out.append("GHOST-COST: %s%s worstSwapMedianMs=%s (%s) worstSwapMaxMs=%.3f limit=%.1f textureBad=%s" % (
            tag, "PASS" if ok and not bad_tex else "FAIL", worst.get("swapMedianMs"), worst.get("part"),
            wmax, MAX_SWAP_MS, ",".join(bad_tex) or "none"))
    out.append("GHOST-PARTS: %sfound=%d/%d %s" % (tag, len(found), len(parts), " ".join(
        "%s=%s" % (f.get("part"), "built" if f.get("fallback") == "none" else "sphere") for f in parts)))
    for f in parts:
        out.append("  %s build=%s/%sms swap=%s/%sms materials=%s roles=%s sharedBeforeSwap=%s source=%s" % (
            f.get("part"), f.get("buildMedianMs"), f.get("buildMaxMs"), f.get("swapMedianMs"),
            f.get("swapMaxMs"), f.get("materials"), f.get("rolesMatched"), f.get("sharedBeforeSwap"),
            f.get("source")))
    for f in of_kind(recs, "material census"):
        out.append("  material census before=%s measured=%s final=%s deltaFinal=%s" % (
            f.get("before"), f.get("measured"), f.get("final"), f.get("deltaFinal")))


def look_colors_from_log(recs):
    out = {}
    for f in of_kind(recs, "color sample"):
        if f.get("gender") != "Male" or not f.get("source", "").startswith("prefab-body"):
            continue
        look = f.get("look")
        if look in LOOKS and look not in out:
            c = f.get("chromatic")
            out[look] = c if c and c != "-" else f.get("dominant")
    return out


def read_colors_csv(path):
    out = {}
    with io.open(path, encoding="utf-8-sig") as f:
        for row in csv.reader(f):
            if len(row) < 2 or row[0].strip().lower() in ("look", "") or row[0].strip().startswith("#"):
                continue
            out[row[0].strip()] = row[1].strip()
    return out


def colors_cell(recs, colors_csv, background, out):
    source = "csv" if colors_csv else "texture-samples"
    colors = read_colors_csv(colors_csv) if colors_csv else look_colors_from_log(recs)
    missing = [l for l in LOOKS if not colors.get(l) or colors.get(l) == "-"]
    if missing:
        out.append("COLORS: OWED (no color for %s; source=%s)" % (",".join(missing), source))
        return
    if not background:
        out.append("COLORS: OWED (no --background or --map-png; colors=%s)" % " ".join(
            "%s=%s" % (l, colors[l]) for l in LOOKS))
        return
    c_ok, h_ok, lines, min_c, min_h = judge_colors([(l, colors[l]) for l in LOOKS], background)
    out.append("COLORS: %s contrast=%s hue=%s minContrast=%s minHue=%s background=%s source=%s" % (
        "PASS" if c_ok and h_ok else "FAIL", "PASS" if c_ok else "FAIL", "PASS" if h_ok else "FAIL",
        "-" if min_c is None else "%.2f" % min_c, "-" if min_h is None else "%.0f" % min_h, background, source))
    out.extend("  " + l for l in lines)


def summarize(lines, baseline_lines=None, nodlc_lines=None, colors_csv=None, background=None,
              nodlc_save_lines=None):
    recs, validated = parse_log(lines)
    base = parse_log(baseline_lines)[0] if baseline_lines is not None else None
    nodlc = parse_log(nodlc_lines)[0] if nodlc_lines is not None else None
    out = ["# S3 summary", ""]
    combos_cell(recs, base, validated, out)
    dress_cell(recs, out)
    looks_cell(recs, out)
    nodlc_cell(nodlc, out)
    writeback_cell(nodlc, nodlc_save_lines, out)
    ghost_cells(recs, out)
    if nodlc is not None and of_kind(nodlc, "ghost part"):
        ghost_cells(nodlc, out, "nodlc")
    colors_cell(recs, colors_csv, background, out)
    return "\n".join(out)


def main(argv):
    if argv and argv[0] == "--selftest":
        return selftest()
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd")
    m = sub.add_parser("summarize")
    m.add_argument("--log", required=True)
    m.add_argument("--baseline-log")
    m.add_argument("--nodlc-log")
    m.add_argument("--nodlc-save")
    m.add_argument("--colors-csv")
    g = m.add_mutually_exclusive_group()
    g.add_argument("--background")
    g.add_argument("--map-png")
    m.add_argument("--out")
    args = ap.parse_args(argv)
    if args.cmd != "summarize":
        ap.print_help()
        return 2
    background = args.background
    if args.map_png:
        background = png_median_color(args.map_png)
    text = summarize(read_lines(args.log),
                     read_lines(args.baseline_log) if args.baseline_log else None,
                     read_lines(args.nodlc_log) if args.nodlc_log else None,
                     args.colors_csv, background,
                     read_lines(args.nodlc_save) if args.nodlc_save else None)
    print(text)
    if args.out:
        with io.open(args.out, "w", encoding="utf-8", newline="\n") as f:
            f.write(text + "\n")
    return 0


# ------------------------------------------------------------------ selftest

def _l(body, level="INFO"):
    return "[LOG 12:00:00.000] [Parsek][%s][Spike.S3] %s" % (level, body)


def _ghost(part, swap="0.120", fallback="none", found="true", unchanged="true", tex="true"):
    return [
        _l("ghost part: part=%s suit=Default gender=Male found=%s fallback=%s source=Squad/x "
           "builds=20 buildMedianMs=3.000 buildMaxMs=5.000 swapMedianMs=%s swapMaxMs=0.400 "
           "buildSwapMedianMs=3.100 materials=6 renderers=6 rolesMatched=3 sharedBeforeSwap=true "
           "textureOk=%s swapTexture=ParsekSpikeS3/Textures/vintage_like_suit" % (part, found, fallback, swap, tex)),
        _l("shared material: part=%s before=helmet=1/a#2 after=helmet=1/a#2 unchanged=%s builds=20" % (part, unchanged)),
    ]


def _main_log(extra=6, sets=4, dress_ok="true", swap="0.120", unchanged="true"):
    lines = [
        "[LOG 12:00:00.000] Suit combo Parsek S3 Vintage-like/ParsekS3_VintageLike_Male validated.",
        _l("expansions: MakingHistory=true Serenity=true suitVintage=true suitFuture=true suitSlim=true"),
        _l("combo: source=extra index=0 name=ParsekS3_VintageLike_Male suitType=Default gender=Male config=ParsekSpikeS3/VintageLike"),
        _l("combos summary: reason=mainmenu default=2 stock=16 extra=%d parsekSets=%d otherExtra=%d configs=2"
           % (extra, sets, extra - sets)),
        _l("combos summary: reason=test default=2 stock=16 extra=99 parsekSets=0 otherExtra=99 configs=2"),
        _l("dress: phase=1 pool=crew index=0 name=Jebediah_Kerman look=Vintage suit=Vintage comboId=x ok=%s reason=-" % dress_ok),
        _l("dress: phase=1 pool=applicant index=0 name=Foo_Kerman look=VintageLike suit=Default comboId=ParsekS3_VintageLike_Male ok=true reason=-"),
    ]
    for suit, name in (("Vintage", "Jebediah"), ("Future", "Bill"), ("Slim", "Bob"), ("Default", "Valentina")):
        lines.append(_l("roster: label=dressed-phase1 name=%s_Kerman suit=%s evaFound=true ivaFound=true" % (name, suit)))
    lines.append(_l("color sample: look=Default gender=Male source=prefab-body:kerbalEVA texture=a#1 dominant=#FFFFFF share=0.5 chromatic=#FF8C00 chromaticShare=0.2 primaryColor=-"))
    lines.append(_l("color sample: look=Slim gender=Male source=prefab-body:kerbalEVASlimSuit texture=a#1 dominant=#00C8FF share=0.5 chromatic=- chromaticShare=- primaryColor=-"))
    lines.append(_l("color sample: look=Vintage gender=Male source=prefab-body:kerbalEVAVintage texture=a#1 dominant=#4DFF6A share=0.5 chromatic=#4DFF6A chromaticShare=0.3 primaryColor=-"))
    lines.append(_l("color sample: look=Future gender=Male source=prefab-body:kerbalEVAFuture texture=a#1 dominant=#B07CFF share=0.5 chromatic=#B07CFF chromaticShare=0.3 primaryColor=-"))
    lines.append(_l("color sample: look=Future gender=Female source=prefab-body:kerbalEVAfemaleFuture texture=a#1 dominant=#000000 share=0.5 chromatic=- chromaticShare=- primaryColor=-"))
    for part in ("kerbalEVA", "kerbalEVAfemale", "kerbalEVAVintage"):
        lines.extend(_ghost(part, swap=swap, unchanged=unchanged))
    lines.append(_l("material census: before=100 measured=104 final=100 deltaFinal=0"))
    return lines


def _nodlc_log(second_future_part="kerbalEVAfemale"):
    return [
        _l("expansions: MakingHistory=false Serenity=false suitVintage=false suitFuture=false suitSlim=true"),
        _l("pcm load: name=Jebediah_Kerman type=Crew gender=Male nodeSuit=Vintage nodeCombo=c1 nodeSuitTexture=- "
           "nodeSprite=- suit=Default comboId=c1 suitTexture=- evaPart=kerbalEVA"),
        _l("pcm load: name=Bill_Kerman type=Crew gender=Male nodeSuit=Future nodeCombo=c2 nodeSuitTexture=- "
           "nodeSprite=- suit=Default comboId=c2 suitTexture=- evaPart=kerbalEVA"),
        _l("pcm load: name=Val_Kerman type=Crew gender=Female nodeSuit=Future nodeCombo=c3 nodeSuitTexture=- "
           "nodeSprite=- suit=Default comboId=c3 suitTexture=- evaPart=%s" % second_future_part),
        _l("pcm load: name=Bob_Kerman type=Crew gender=Male nodeSuit=Slim nodeCombo=c4 nodeSuitTexture=- "
           "nodeSprite=- suit=Slim comboId=c4 suitTexture=- evaPart=kerbalEVASlimSuit"),
        _l("eva live: label=x vessel=Jebediah_Kerman part=kerbalEVA crew=Jebediah_Kerman suit=Default comboId=c1 bodyTex=t#1"),
    ]


def _cell(text, name):
    for line in text.splitlines():
        if line.startswith(name + ":"):
            return line
    return ""


class SelfTest(unittest.TestCase):
    def test_parse_reads_only_s3_lines_and_validated(self):
        recs, validated = parse_log(_main_log() + ["[Parsek][INFO][Spike.S9] combo: name=x"])
        self.assertEqual(1, validated)
        self.assertEqual(1, len(of_kind(recs, "combo")))
        self.assertEqual("ParsekS3_VintageLike_Male", of_kind(recs, "combo")[0]["name"])

    def test_combos_growth_against_baseline(self):
        base = [_l("combos summary: reason=mainmenu default=2 stock=16 extra=2 parsekSets=0 otherExtra=2 configs=0")]
        t = summarize(_main_log(extra=6), baseline_lines=base)
        self.assertTrue(_cell(t, "COMBOS").startswith("COMBOS: PASS extra=6 baseline=2 growth=4"), t)
        t = summarize(_main_log(extra=5, sets=3), baseline_lines=base)
        self.assertTrue(_cell(t, "COMBOS").startswith("COMBOS: FAIL"), t)

    def test_combos_without_baseline_uses_set_count_and_mainmenu_line(self):
        t = summarize(_main_log())
        self.assertIn("COMBOS: PASS extra=6 parsekSets=4 baseline=none", t)
        self.assertIn("set combos: ParsekS3_VintageLike_Male", t)

    def test_combos_owed_without_summary(self):
        self.assertIn("COMBOS: OWED", summarize([]))

    def test_dress_and_looks(self):
        t = summarize(_main_log())
        self.assertIn("DRESS: PASS steps=2 failed=0", t)
        self.assertIn("LOOKS: PASS Default=Valentina_Kerman Slim=Bob_Kerman Vintage=Jebediah_Kerman Future=Bill_Kerman", t)
        t = summarize(_main_log(dress_ok="false"))
        self.assertIn("DRESS: FAIL steps=2 failed=1", t)
        t = summarize([l for l in _main_log() if "suit=Slim" not in l])
        self.assertIn("LOOKS: FAIL", t)
        self.assertIn("Slim=missing", t)

    def test_nodlc_deterministic_mapping(self):
        t = summarize(_main_log(), nodlc_lines=_nodlc_log())
        line = _cell(t, "NODLC")
        self.assertTrue(line.startswith("NODLC: FAIL deterministic=false"), line)  # female differs
        t = summarize(_main_log(), nodlc_lines=[l for l in _nodlc_log() if "Val_Kerman" not in l])
        self.assertEqual("NODLC: PASS deterministic=true Future->Default/kerbalEVA Vintage->Default/kerbalEVA",
                         _cell(t, "NODLC"))
        self.assertIn("eva live Jebediah_Kerman part=kerbalEVA", t)

    def test_writeback_reads_the_kerbal_nodes(self):
        sfs = ["GAME", "{", "ROSTER", "{", "KERBAL", "{", "name = Jebediah Kerman", "suit = Default",
               "comboId = c1", "CAREER_LOG", "{", "suit = nested", "}", "}", "KERBAL", "{",
               "name = Bill Kerman", "suit = Future", "}", "}", "}"]
        self.assertEqual("Default", parse_sfs_kerbals(sfs)["Jebediah Kerman"]["suit"])
        log = [l for l in _nodlc_log() if "Val_Kerman" not in l]
        t = summarize(_main_log(), nodlc_lines=log, nodlc_save_lines=sfs)
        self.assertEqual("WRITEBACK: suit-rewritten Bill_Kerman:Future->Future/combo=- "
                         "Jebediah_Kerman:Vintage->Default/combo=c1", _cell(t, "WRITEBACK"))
        self.assertIn("WRITEBACK: OWED (no --nodlc-save)", summarize(_main_log(), nodlc_lines=log))

    def test_nodlc_refuses_a_log_with_the_dlc(self):
        log = [_l("expansions: MakingHistory=true Serenity=false")] + _nodlc_log()[1:]
        self.assertTrue(_cell(summarize(_main_log(), nodlc_lines=log), "NODLC").startswith("NODLC: FAIL (the no-DLC log"))
        self.assertIn("NODLC: OWED (no --nodlc-log)", summarize(_main_log()))

    def test_ghost_cells(self):
        t = summarize(_main_log())
        self.assertIn("GHOST-SHARED: PASS parts=3 changed=none", t)
        self.assertTrue(_cell(t, "GHOST-COST").startswith("GHOST-COST: PASS worstSwapMedianMs=0.120"), t)
        self.assertIn("GHOST-PARTS: found=3/3", t)
        self.assertIn("material census before=100", t)
        t = summarize(_main_log(swap="2.500", unchanged="false"))
        self.assertIn("GHOST-SHARED: FAIL parts=3 changed=kerbalEVA,kerbalEVAfemale,kerbalEVAVintage", t)
        self.assertTrue(_cell(t, "GHOST-COST").startswith("GHOST-COST: FAIL"), t)

    def test_ghost_fallback_is_not_costed(self):
        lines = _main_log() + _ghost("kerbalEVAFuture", swap="0.000", fallback="sphere", found="false")
        t = summarize(lines)
        self.assertIn("GHOST-PARTS: found=3/4", t)
        self.assertIn("kerbalEVAFuture=sphere", t)

    def test_colors_from_samples_need_a_background(self):
        t = summarize(_main_log())
        self.assertTrue(_cell(t, "COLORS").startswith("COLORS: OWED (no --background"), t)
        t = summarize(_main_log(), background="#0B1A2A")
        self.assertTrue(_cell(t, "COLORS").startswith("COLORS: PASS contrast=PASS hue=PASS"), t)
        self.assertIn("color: look=Slim hex=#00C8FF", t)  # dominant used when no chromatic

    def test_colors_csv_overrides_and_fails_close_hues(self):
        path = os.path.join(_tmpdir(), "colors.csv")
        with io.open(path, "w", encoding="utf-8") as f:
            f.write("look,hex\nDefault,#FF8C00\nSlim,#FFA000\nVintage,#4DFF6A\nFuture,#B07CFF\n")
        t = summarize(_main_log(), colors_csv=path, background="#0B1A2A")
        line = _cell(t, "COLORS")
        self.assertTrue(line.startswith("COLORS: FAIL contrast=PASS hue=FAIL"), line)
        self.assertIn("source=csv", line)

    def test_color_math_reference_values(self):
        self.assertAlmostEqual(21.0, contrast((0, 0, 0), (255, 255, 255)), 6)
        self.assertAlmostEqual(4.48, contrast((0x77,) * 3, (255,) * 3), 2)
        self.assertAlmostEqual(330.0, hsv((255, 0, 128))[0], 0)
        self.assertEqual(20.0, hue_distance(350, 10))
        self.assertEqual((0xC9, 0xA2, 0x27), parse_hex("c9a227"))
        self.assertEqual((255, 255, 255), parse_hex("#fff"))
        self.assertIsNone(parse_hex("#12345"))
        c_ok, h_ok, lines, _, _ = judge_colors([("W", "#F0F0F0"), ("R", "#FF2020")], "#000000")
        self.assertTrue(c_ok)
        self.assertFalse(h_ok)
        self.assertTrue(any("why=achromatic" in l for l in lines))

    def test_png_median_reads_a_generated_png(self):
        spec = importlib.util.spec_from_file_location("make_suit_pngs", os.path.join(HERE, "make_suit_pngs.py"))
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        rows = [bytes(bytearray((11, 26, 42)) * 10) for _ in range(9)] + [bytes(bytearray((255, 0, 0)) * 10)]
        path = os.path.join(_tmpdir(), "map.png")
        with open(path, "wb") as f:
            f.write(mod.encode_png(rows, 3))
        self.assertEqual("#0B1A2A", png_median_color(path))

    def test_the_line_kinds_are_the_ones_the_csharp_writes(self):
        src = ""
        for name in ("SpikeS3Looks.cs", "SpikeS3Ghosts.cs"):
            p = os.path.join(HERE, "..", "..", "..", "Source", "Parsek", "InGameTests", "Spikes", "S3", name)
            with io.open(p, encoding="utf-8") as f:
                src += f.read()
        for kind in ("combos summary", "combo", "dress", "roster", "pcm load", "expansions", "eva live",
                     "color sample", "ghost part", "shared material", "material census"):
            self.assertIn('Line("%s"' % kind, src, kind)
        for key in ("swapMedianMs", "swapMaxMs", "unchanged", "parsekSets", "evaFound", "ivaFound",
                    "nodeSuit", "chromatic", "deltaFinal", "rolesMatched", "textureOk"):
            self.assertIn('"%s"' % key, src, key)


_TMP = []


def _tmpdir():
    import tempfile
    if not _TMP:
        _TMP.append(tempfile.mkdtemp(prefix="s3-selftest-"))
    return _TMP[0]


def selftest():
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(SelfTest)
    result = unittest.TextTestRunner(verbosity=1).run(suite)
    if _TMP:
        import shutil
        shutil.rmtree(_TMP[0], ignore_errors=True)
    print("SELFTEST PASS" if result.wasSuccessful() else "SELFTEST FAIL")
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
