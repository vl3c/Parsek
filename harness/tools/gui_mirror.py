#!/usr/bin/env python3
"""Build ONE self-contained interactive HTML mirror of Parsek's GUI out of the
census captures, so the page cannot drift from the game.

Every window layout, every control, every string and every colour on the page is
GENERATED from a census artifact:

  * `<label>.gui.json`  - the IMGUI control tree (`parsek-gui-tree/1`), which
    carries each control's screen rect, kind, style, text, tooltip and enabled
    state. That is the geometry and the words.
  * `<label>.png`       - the frame the tree was dumped on. Sampled per control
    rect for the FOREGROUND and BACKGROUND colour actually drawn, because the
    dump records a style NAME and not a colour: the blue clickable rows, the
    dimmed unavailable rows and the status tints exist only in the pixels.
  * `KSP.log`           - the command seam's own record of what was open when
    each screenshot was taken (`uiaction open/close/tab/rect/complexity/dialog`
    plus `capturescreenshot ok label=`). That, not the label string, is the
    authority for "this capture is window W, tab T, mode M", and it is where the
    stock uGUI dialogs' titles and button labels come from - a PopupDialog is
    invisible to the control-tree dump.
  * `harness/scenarios/GUI-*.toml` - `fixture.saveTemplate`, which names the
    dataset a run flew.

NOTHING about a window is typed into this file. There is no table of window
names, no tab list, no column width, no button label and no tooltip text here;
the vocabularies are all derived from the logs and the dumps. The CSS below is a
skin (borders, fonts, row metrics) and the colours it does not derive are the
ones KSP's own skin uses for chrome.

The Compare view's prose is likewise not written here: it is lifted out of the
repo's own records - the CHANGELOG entries under the current version, the todo
entries whose ids start with `GUI-`, and the merge commits - and the numbers
beside it are MEASURED from the before/after dumps (node counts, row counts, and
the header-to-cell offset per column).

Usage::

    python harness/tools/gui_mirror.py \
        --shots <dir-of-*.gui.json> [--shots ...] \
        --repo <repo-root> \
        --out gui-mirror.html [--index gui-mirror-index.json]

Stdlib only. Unit tests: `harness/lib/test_gui_mirror.py`.
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import re
import struct
import subprocess
import sys
import zlib
from collections import Counter, OrderedDict, defaultdict

TREE_SCHEMA = "parsek-gui-tree/1"
MIRROR_SCHEMA = "parsek-gui-mirror/1"
MODE_TOKENS = ("advanced", "basic")
DEFAULT_BUDGET_BYTES = 16 * 1024 * 1024
# The census instance's frame. Overridden per capture by the dump's own `screen`.
FRAME_W, FRAME_H = 1280, 720


# --------------------------------------------------------------------------
# pure helpers
# --------------------------------------------------------------------------

def norm(text):
    """Lowercase alphanumeric squash, the join key for every token match."""
    return re.sub(r"[^a-z0-9]+", "", (text or "").lower())


def esc(text):
    """HTML-escape for text nodes AND attribute values (quotes included).

    Every string the page shows comes from a game control, so this is the only
    thing between a control whose text is markup and a broken page.
    """
    if text is None:
        return ""
    return (str(text)
            .replace("&", "&amp;")
            .replace("<", "&lt;")
            .replace(">", "&gt;")
            .replace('"', "&quot;")
            .replace("'", "&#39;"))


def parse_label(label, window_tokens=(), tab_tokens_by_window=None):
    """Split a census shot label into (host, window, tab, state, mode).

    The grammar is positional: ``<host>-<window>[-<tab>][-<state>]-<mode>``.
    `mode` is recognised only as a trailing `advanced`/`basic`; `window` is the
    token after the host, but only when it is in the window vocabulary the logs
    produced; `tab` only when it is in that window's own tab vocabulary.
    Everything left over is the state, joined with '-'. Unknown vocabularies
    degrade to window=None and a state of the whole remainder, which is what the
    dialog (`dlg-*`) and map-scope (`scope-*`) lanes want.
    """
    tab_tokens_by_window = tab_tokens_by_window or {}
    toks = [t for t in (label or "").split("-") if t]
    if not toks:
        return {"host": "", "window": None, "tab": None, "state": "", "mode": None}
    mode = None
    if toks[-1] in MODE_TOKENS and len(toks) > 1:
        mode = toks[-1]
        toks = toks[:-1]
    host = toks[0]
    rest = toks[1:]
    window = None
    if rest and (not window_tokens or rest[0] in window_tokens):
        window = rest[0]
        rest = rest[1:]
    tab = None
    if window and rest:
        known = tab_tokens_by_window.get(window) or ()
        if rest[0] in known:
            tab = rest[0]
            rest = rest[1:]
    return {"host": host, "window": window, "tab": tab,
            "state": "-".join(rest), "mode": mode}


_LOG_LINE = re.compile(
    r"uiaction (?P<verb>open|close|tab|rect|complexity|dialog|describe|playback)\b(?P<tail>[^\r\n]*)")
_LOG_SHOT = re.compile(r"capturescreenshot ok label=(?P<label>[A-Za-z0-9_.-]+)")
_KV = re.compile(r"(\w+)=([^\s]+)")


def _kv(tail):
    return dict(_KV.findall(tail or ""))


def parse_ksp_log(text):
    """Replay a shots-dir KSP.log into per-capture seam state.

    Returns ``{label: {...}}`` with the window that was open besides the main
    one, its rect, its selected tab, the complexity mode, the whole open set and
    the standing dialog (title plus button labels) if any. The seam prints these
    itself, so this is the game's own account of each frame rather than a guess
    off the label.
    """
    state = {
        "scene": None,
        "mode": None,
        "open": [],
        "rects": {},
        "tabs": {},
        "dialog": None,
    }
    out = OrderedDict()
    tabs_seen = defaultdict(OrderedDict)
    windows_seen = OrderedDict()
    for raw in (text or "").splitlines():
        m = _LOG_SHOT.search(raw)
        if m:
            label = m.group("label")
            openset = [w for w in state["open"]]
            subject = None
            for w in reversed(openset):
                if w != "main":
                    subject = w
                    break
            if subject is None and openset:
                subject = openset[0]
            out[label] = {
                "window": subject,
                "tab": state["tabs"].get(subject),
                "mode": state["mode"],
                "scene": state["scene"],
                "openWindows": list(openset),
                "rects": dict(state["rects"]),
                "dialog": dict(state["dialog"]) if state["dialog"] else None,
            }
            continue
        m = _LOG_LINE.search(raw)
        if not m:
            continue
        verb, tail = m.group("verb"), m.group("tail")
        kv = _kv(tail)
        if verb == "complexity" and "mode" in kv:
            state["mode"] = kv["mode"]
        elif verb == "open" and "window" in kv and "initiated" not in tail:
            w = kv["window"]
            windows_seen[w] = True
            if w not in state["open"]:
                state["open"].append(w)
        elif verb == "close" and "window" in kv:
            w = kv["window"]
            if w in state["open"]:
                state["open"].remove(w)
        elif verb == "rect" and "window" in kv and "rect" in kv:
            try:
                state["rects"][kv["window"]] = [int(float(v)) for v in kv["rect"].split(",")][:4]
            except ValueError:
                pass
        elif verb == "tab" and "window" in kv and "tab" in kv:
            state["tabs"][kv["window"]] = kv["tab"]
            try:
                idx = int(kv.get("index", "0"))
            except ValueError:
                idx = 0
            tabs_seen[kv["window"]].setdefault(kv["tab"], idx)
        elif verb == "describe":
            if "scene" in kv:
                state["scene"] = kv["scene"]
            if "complexity" in kv:
                state["mode"] = kv["complexity"]
            names = kv.get("openWindows", "")
            if names and names != "-":
                state["open"] = [n for n in names.split(",") if n]
        elif verb == "dialog":
            if kv.get("open") == "true":
                # `title=` and `buttons=` carry spaces, so they are cut out by
                # position rather than by the generic key=value scan.
                title = _between(tail, "title=", " nbuttons=")
                buttons = _between(tail, " buttons=", None)
                state["dialog"] = {
                    "name": kv.get("name", ""),
                    "title": title or kv.get("title", ""),
                    "buttons": [b for b in (buttons or "").split("|") if b],
                }
            else:
                state["dialog"] = None
    return {
        "captures": out,
        "tabs": {w: list(t.items()) for w, t in tabs_seen.items()},
        "windows": list(windows_seen.keys()),
    }


def _between(text, start, end):
    i = text.find(start)
    if i < 0:
        return None
    i += len(start)
    if end is None:
        j = len(text)
    else:
        j = text.find(end, i)
        if j < 0:
            j = len(text)
    return text[i:j].strip()


def parse_dialog_reports(text):
    """Every standing-dialog report in a log, in order, keyed by the capture that
    followed it. Used for the modal captures, whose popups are stock uGUI and so
    appear in no control tree at all."""
    replay = parse_ksp_log(text)
    return {k: v["dialog"] for k, v in replay["captures"].items() if v.get("dialog")}


def fixture_of_template(save_template):
    """`fixtures/saves/bdock-recorded` -> `bdock-recorded`."""
    return (save_template or "").rstrip("/").split("/")[-1]


def choose_capture(by_fixture, want_fixture, fixture_order):
    """The dataset rule: the selected fixture's capture when it exists, else the
    nearest one in the declared fixture order, and a flag saying which happened
    so the page can say so in the status line rather than pretend."""
    if not by_fixture:
        return None, False
    if want_fixture in by_fixture:
        return by_fixture[want_fixture], True
    for fx in fixture_order:
        if fx in by_fixture:
            return by_fixture[fx], False
    first = sorted(by_fixture)[0]
    return by_fixture[first], False


def key_of(fixture, window, tab, state, mode, scene=""):
    """The identity a before/after pair is formed on.

    `scene` is part of it: the same window drawn at the Space Center and in
    flight is two different pictures, not a change, and pairing them would
    report every scene difference as a regression.
    """
    return "|".join([fixture or "", window or "", tab or "", state or "",
                     mode or "", scene or ""])


def state_graph_edges(captures):
    """Edges between captures of the same window: a control whose text matches a
    tab token, a state token or another window's token is a real click.

    Both halves come from data - the tokens from the seam log, the control text
    from the dump - so an edge exists only where the census actually produced the
    destination frame. Everything else is deliberately left without an edge, and
    the page flashes the control instead of inventing a screen.
    """
    by_window = defaultdict(list)
    for cap in captures:
        by_window[cap.get("window")].append(cap)
    edges = []
    for cap in captures:
        siblings = by_window.get(cap.get("window"), [])
        for other in siblings:
            if other["id"] == cap["id"]:
                continue
            if other.get("fixture") != cap.get("fixture"):
                continue
            if other.get("mode") != cap.get("mode"):
                continue
            if other.get("tab") != cap.get("tab"):
                edges.append({"from": cap["id"], "to": other["id"],
                              "token": other.get("tab") or "", "kind": "tab"})
            elif other.get("state") != cap.get("state"):
                edges.append({"from": cap["id"], "to": other["id"],
                              "token": other.get("state") or "", "kind": "state"})
    return edges


# --------------------------------------------------------------------------
# PNG read / crop / re-encode (stdlib: zlib + struct)
# --------------------------------------------------------------------------

def read_png(path):
    """Minimal non-interlaced 8-bit PNG reader -> (w, h, bytes_per_pixel, pixels)."""
    with open(path, "rb") as fh:
        data = fh.read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a PNG: %s" % path)
    i, idat = 8, []
    w = h = bitdepth = colortype = None
    while i + 8 <= len(data):
        length = struct.unpack(">I", data[i:i + 4])[0]
        typ = data[i + 4:i + 8]
        body = data[i + 8:i + 8 + length]
        i += 12 + length
        if typ == b"IHDR":
            w, h, bitdepth, colortype, _c, _f, interlace = struct.unpack(">IIBBBBB", body)
            if bitdepth != 8 or interlace:
                raise ValueError("unsupported PNG form in %s" % path)
        elif typ == b"IDAT":
            idat.append(body)
        elif typ == b"IEND":
            break
    nch = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[colortype]
    bpp = nch
    raw = zlib.decompress(b"".join(idat))
    stride = w * bpp
    out = bytearray(h * stride)
    prev = bytearray(stride)
    pos = 0
    for y in range(h):
        ftype = raw[pos]
        pos += 1
        line = bytearray(raw[pos:pos + stride])
        pos += stride
        if ftype == 1:
            for x in range(bpp, stride):
                line[x] = (line[x] + line[x - bpp]) & 255
        elif ftype == 2:
            for x in range(stride):
                line[x] = (line[x] + prev[x]) & 255
        elif ftype == 3:
            for x in range(stride):
                a = line[x - bpp] if x >= bpp else 0
                line[x] = (line[x] + ((a + prev[x]) >> 1)) & 255
        elif ftype == 4:
            for x in range(stride):
                a = line[x - bpp] if x >= bpp else 0
                b = prev[x]
                c = prev[x - bpp] if x >= bpp else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[x] = (line[x] + pr) & 255
        out[y * stride:(y + 1) * stride] = line
        prev = line
    return w, h, bpp, bytes(out)


def write_png(w, h, bpp, px, quant=0):
    """Re-encode RGB/RGBA bytes. `quant` floors each channel to a multiple of
    `quant`, which costs nothing visible on a flat IMGUI skin and roughly halves
    the deflate size - the whole reason the page fits in its budget."""
    stride = w * bpp
    table = bytes(((b // quant) * quant) if quant else b for b in range(256))
    rows = []
    for y in range(h):
        row = px[y * stride:(y + 1) * stride]
        if quant:
            row = row.translate(table)
        rows.append(b"\x00" + row)
    idat = zlib.compress(b"".join(rows), 9)
    ctype = 6 if bpp == 4 else 2

    def chunk(typ, body):
        c = typ + body
        return struct.pack(">I", len(body)) + c + struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, ctype, 0, 0, 0))
            + chunk(b"IDAT", idat)
            + chunk(b"IEND", b""))


def crop(w, h, bpp, px, x0, y0, cw, ch):
    x0 = max(0, min(x0, w - 1))
    y0 = max(0, min(y0, h - 1))
    cw = max(1, min(cw, w - x0))
    ch = max(1, min(ch, h - y0))
    out = bytearray(cw * ch * bpp)
    for y in range(ch):
        s = ((y0 + y) * w + x0) * bpp
        d = y * cw * bpp
        out[d:d + cw * bpp] = px[s:s + cw * bpp]
    return cw, ch, bytes(out)


def subsample(w, h, bpp, px, factor):
    if factor <= 1:
        return w, h, px
    nw, nh = max(1, w // factor), max(1, h // factor)
    out = bytearray(nw * nh * bpp)
    for y in range(nh):
        base = (y * factor) * w * bpp
        for x in range(nw):
            s = base + x * factor * bpp
            d = (y * nw + x) * bpp
            out[d:d + bpp] = px[s:s + bpp]
    return nw, nh, bytes(out)


def sample_colors(w, h, bpp, px, rect, step=2):
    """Background and foreground colour actually drawn inside a control's rect.

    Background is the modal colour; foreground is the mean of the pixels furthest
    from it in luminance, which on a flat skin is exactly the glyph ink. This is
    how the mirror gets the blue clickable rows, the dimmed rows and the status
    tints: the dump records the style NAME only, and a name has no colour.
    """
    x0, y0, rw, rh = rect
    x0, y0 = int(x0), int(y0)
    rw, rh = int(rw), int(rh)
    if rw <= 0 or rh <= 0:
        return None, None
    x1, y1 = min(w, x0 + rw), min(h, y0 + rh)
    x0, y0 = max(0, x0), max(0, y0)
    if x1 <= x0 or y1 <= y0:
        return None, None
    hist = Counter()
    pixels = []
    for y in range(y0, y1, step):
        rowbase = y * w * bpp
        for x in range(x0, x1, step):
            o = rowbase + x * bpp
            rgb = (px[o], px[o + 1], px[o + 2])
            hist[rgb] += 1
            pixels.append(rgb)
    if not pixels:
        return None, None
    bg = hist.most_common(1)[0][0]
    bl = 0.299 * bg[0] + 0.587 * bg[1] + 0.114 * bg[2]
    scored = sorted(pixels, key=lambda p: -abs(0.299 * p[0] + 0.587 * p[1] + 0.114 * p[2] - bl))
    take = scored[:max(1, len(scored) // 20)]
    # Ink is the extreme tail; averaging the whole tail would drag it back
    # towards the anti-aliased edge pixels, so only the strongest fifth counts.
    strong = take[:max(1, len(take) // 5)]
    fg = tuple(sum(p[i] for p in strong) // len(strong) for i in range(3))
    if abs(0.299 * fg[0] + 0.587 * fg[1] + 0.114 * fg[2] - bl) < 12:
        fg = None
    return _hex(bg), (_hex(fg) if fg else None)


def grid_label_runs(w, h, bpp, px, rect, min_width=18, gap=16, bright=150):
    """The x-extent of each label drawn on a selection grid, measured off the
    frame.

    A grid reports one rect and the SELECTED item's text; it reports nothing about
    where the items sit or how their text is aligned, and the alignment is not
    uniform across the product - the Kerbals tab bar centres its two labels while
    the Career bar left-aligns its four in cells of the same stride. Centring
    everything put the Career labels 87 px right of where the game draws them.

    So the positions are read rather than assumed: inside the middle band of the
    grid's own rect, runs of bright pixels are the glyphs, and runs separated by
    less than a word gap are one label.
    """
    x0, y0, rw, rh = [int(v) for v in rect]
    if rw <= 0 or rh <= 4:
        return []
    x1 = min(w, x0 + rw)
    band0 = max(0, y0 + rh // 4)
    band1 = min(h, y0 + rh - rh // 4)
    if band1 <= band0 or x1 <= x0:
        return []
    lit = []
    for x in range(max(0, x0), x1):
        top = 0
        for y in range(band0, band1):
            o = (y * w + x) * bpp
            val = 0.299 * px[o] + 0.587 * px[o + 1] + 0.114 * px[o + 2]
            if val > top:
                top = val
        lit.append(1 if top > bright else 0)
    runs = []
    start = None
    for i, on in enumerate(lit):
        if on and start is None:
            start = i
        elif not on and start is not None:
            runs.append([x0 + start, x0 + i - 1])
            start = None
    if start is not None:
        runs.append([x0 + start, x1 - 1])
    merged = []
    for a, b in runs:
        if merged and a - merged[-1][1] < gap:
            merged[-1][1] = b
        else:
            merged.append([a, b])
    return [r for r in merged if r[1] - r[0] >= min_width]


def _hex(rgb):
    return "#%02x%02x%02x" % tuple(rgb)


# --------------------------------------------------------------------------
# tree flattening
# --------------------------------------------------------------------------

TEXTY = ("label", "button", "repeatbutton", "toggle", "textfield", "box", "buttongrid")
CLICKY = ("button", "repeatbutton", "toggle", "buttongrid", "slider", "textfield")


def root_height(root, log_rects):
    """A GUILayout window reports h=0 in the dump; the seam's own `rect` line
    reports what it was actually laid out at, and the child extent is the last
    resort."""
    rect = list(root.get("rect") or [0, 0, 0, 0])
    if rect[3] > 0:
        return rect[3]
    for _w, r in (log_rects or {}).items():
        if len(r) == 4 and r[0] == rect[0] and r[1] == rect[1] and r[2] == rect[2] and r[3] > 0:
            return r[3]
    bottom = rect[1]

    def walk(node):
        nonlocal bottom
        nr = node.get("rect") or [0, 0, 0, 0]
        bottom = max(bottom, nr[1] + nr[3])
        for ch in node.get("children") or ():
            walk(ch)

    walk(root)
    return max(1, bottom - rect[1] + 4)


def compact_tree(node, parent_rect, sampler=None, parent_bg=None,
                 grid_runs=None):
    """One dump node -> the page's compact node: rect made parent-relative (so a
    scroll view clips its own children), plus the colours sampled off the frame."""
    rect = [int(v) for v in (node.get("rect") or [0, 0, 0, 0])]
    px, py = parent_rect[0], parent_rect[1]
    out = {
        "k": node.get("kind") or "label",
        "x": rect[0] - px,
        "y": rect[1] - py,
        "w": rect[2],
        "h": rect[3],
    }
    text = node.get("text")
    if text:
        out["t"] = text
    tip = node.get("tooltip")
    if tip:
        out["p"] = tip
    style = node.get("style")
    if style:
        out["s"] = style
    if node.get("enabled") is False:
        out["e"] = 0
    if "value" in node and node.get("value") is not None:
        out["v"] = node["value"]
    if node.get("textValue"):
        out["tv"] = node["textValue"]
    if out["k"] == "buttongrid" and grid_runs is not None:
        runs = grid_runs(rect)
        if runs:
            # Stored relative to the grid, so the page places each label where the
            # game drew it instead of centring them all.
            out["gi"] = [[r[0] - rect[0], r[1] - r[0] + 1] for r in runs]
    if node.get("horizontal") is not None:
        out["hz"] = 1 if node["horizontal"] else 0
    bg = None
    if sampler is not None and out["w"] > 0 and out["h"] > 0:
        bg, fg = sampler(rect)
        # A background identical to the parent's is what CSS already inherits,
        # so storing it again would only make the page bigger.
        if bg and bg != parent_bg:
            out["bg"] = bg
        if fg and (text or out["k"] in ("box", "toggle")):
            out["fg"] = fg
    kids = [compact_tree(ch, rect, sampler, bg or parent_bg, grid_runs)
            for ch in (node.get("children") or ())]
    if kids:
        out["c"] = kids
    return out


def _root_texts(dump):
    """Every control string in a dump, for matching a label token against what the
    window actually drew (a renamed tab heading, for instance)."""
    out = []

    def walk(node):
        if node.get("text"):
            out.append(node["text"])
        if node.get("textValue"):
            out.append(node["textValue"])
        for ch in node.get("children") or ():
            walk(ch)

    for root in dump.get("roots") or ():
        walk(root)
    return out


def mark_tooltip_strip(root):
    """Mark the window's tooltip echo strip, the box the game writes hovered-control
    help into.

    `TooltipEchoBox` draws it as exactly one `GUILayout.Label` in the box style,
    permanently visible, empty when nothing is hovered, and one or two text lines
    tall - so in a dump it is the LAST empty-text `label` with style `box` in the
    window. Its own rect gives the line count (a two-line strip is 38 px against a
    one-line 23), which is why the height is read off the capture instead of being
    tabulated per window here.

    Returns the marked node, or None for a window that draws no strip.
    """
    found = []

    def walk(node):
        if (node.get("k") == "label" and node.get("s") == "box"
                and not node.get("t")):
            found.append(node)
        for ch in node.get("c") or ():
            walk(ch)

    walk(root)
    if not found:
        return None
    strip = found[-1]
    strip["strip"] = 2 if strip.get("h", 0) >= 30 else 1
    return strip


def _first_grid_value(roots):
    """The selected item's text on the first selection grid of a capture."""
    stack = [r for r in roots if not r.get("foreign")]
    while stack:
        node = stack.pop(0)
        if node.get("k") == "buttongrid" and node.get("tv"):
            return node["tv"]
        stack.extend(node.get("c") or ())
    return None


def flatten_texts(node, acc=None):
    acc = [] if acc is None else acc
    if node.get("t"):
        acc.append(node["t"])
    for ch in node.get("c") or ():
        flatten_texts(ch, acc)
    return acc


def count_nodes(node):
    return 1 + sum(count_nodes(ch) for ch in (node.get("c") or ()))


# --------------------------------------------------------------------------
# measured before/after deltas
# --------------------------------------------------------------------------

def header_cell_offsets(root):
    """Per-column header-to-cell dx/dw inside one window.

    A header row is the topmost horizontal run of `box`-styled labels; the body
    rows are the runs below it with the same cardinality. The offsets this
    returns are the same numbers the alignment todo entry tabulates, but measured
    here off the dump rather than copied out of the record.
    """
    rows = []

    def walk(node):
        kids = [c for c in (node.get("c") or ())]
        cells = [c for c in kids if c.get("k") in ("label", "button", "toggle", "textfield")
                 and c.get("w", 0) > 4 and c.get("h", 0) > 4]
        if len(cells) >= 3:
            rows.append(sorted(cells, key=lambda c: c["x"]))
        for c in kids:
            walk(c)

    walk(root)
    header = None
    for row in rows:
        if all((c.get("s") == "box") for c in row):
            header = row
            break
    if header is None:
        return []
    body = None
    for row in rows:
        if row is header:
            continue
        if len(row) == len(header) and all(c.get("s") != "box" for c in row):
            body = row
            break
    if body is None:
        return []
    return [{"dx": b["x"] - h["x"], "dw": b["w"] - h["w"]}
            for h, b in zip(header, body)]


def measure_pair(before, after):
    """The numbers the Compare note quotes: node and row counts each side, the
    worst header-to-cell offset each side, and the text lines that appeared or
    vanished."""
    def stats(cap):
        roots = [r for r in cap["roots"] if not r.get("foreign")]
        nodes = sum(count_nodes(r) for r in roots)
        texts = []
        for r in roots:
            texts.extend(flatten_texts(r))
        offs = []
        for r in roots:
            offs.extend(header_cell_offsets(r))
        worst = max((abs(o["dx"]) for o in offs), default=None)
        worstw = max((abs(o["dw"]) for o in offs), default=None)
        return {"nodes": nodes, "texts": texts, "rows": len(texts),
                "maxDx": worst, "maxDw": worstw, "cols": len(offs)}

    b, a = stats(before), stats(after)
    bt, at = set(b["texts"]), set(a["texts"])
    return {
        "nodesBefore": b["nodes"], "nodesAfter": a["nodes"],
        "textsBefore": b["rows"], "textsAfter": a["rows"],
        "maxDxBefore": b["maxDx"], "maxDxAfter": a["maxDx"],
        "maxDwBefore": b["maxDw"], "maxDwAfter": a["maxDw"],
        "colsBefore": b["cols"], "colsAfter": a["cols"],
        "gone": sorted(list(bt - at))[:12],
        "added": sorted(list(at - bt))[:12],
    }


# --------------------------------------------------------------------------
# repo records -> Compare notes
# --------------------------------------------------------------------------

def parse_changelog(text):
    """Bullets of the FIRST version section, split on `- **` starts."""
    lines = (text or "").splitlines()
    start = None
    for i, ln in enumerate(lines):
        if ln.startswith("## "):
            start = i + 1
            break
    if start is None:
        return []
    body = []
    for ln in lines[start:]:
        if ln.startswith("## "):
            break
        body.append(ln)
    entries = []
    section = ""
    cur = None
    for ln in body:
        if ln.startswith("### "):
            section = ln[4:].strip()
            continue
        if ln.startswith("- "):
            if cur:
                entries.append(cur)
            cur = {"section": section, "text": ln[2:].strip()}
        elif cur is not None and ln.strip():
            cur["text"] += " " + ln.strip()
        elif cur is not None and not ln.strip():
            entries.append(cur)
            cur = None
    if cur:
        entries.append(cur)
    for e in entries:
        m = re.match(r"\*\*(.+?)\*\*", e["text"])
        e["title"] = (m.group(1) if m else e["text"][:120]).strip()
        e["body"] = re.sub(r"\s+", " ", e["text"])
    return entries


def parse_todo(text):
    """Entries whose heading carries a `GUI-...` id, with their struck state and
    `Fix:` line."""
    out = []
    cur = None
    for ln in (text or "").splitlines():
        m = re.match(r"^\s*[-*]\s+(?:~~)?\*{0,2}(GUI-[A-Z0-9-]+)", ln)
        if m is None:
            m = re.match(r"^#+\s+(?:~~)?\*{0,2}(GUI-[A-Z0-9-]+)", ln)
        if m:
            if cur:
                out.append(cur)
            cur = {"id": m.group(1), "done": "~~" in ln, "text": ln.strip()}
        elif cur is not None:
            if re.match(r"^\s*[-*]\s+", ln) or ln.startswith("#"):
                out.append(cur)
                cur = None
            elif ln.strip():
                cur["text"] += " " + ln.strip()
    if cur:
        out.append(cur)
    for e in out:
        e["text"] = re.sub(r"\s+", " ", e["text"])
        m = re.search(r"Fix:\s*(.+)$", e["text"])
        e["fix"] = m.group(1).strip() if m else ""
    return out


def parse_merges(repo_root, limit=400):
    """`Merge pull request #N from <branch>` plus the files each touched, so a PR
    can be attributed to a window by the UI file it changed."""
    try:
        out = subprocess.run(
            ["git", "log", "--merges", "-n", str(limit),
             "--format=%H%x1f%s%x1f%b%x1e"],
            cwd=repo_root, capture_output=True, text=True, timeout=120).stdout
    except Exception:
        return []
    merges = []
    for rec in out.split("\x1e"):
        parts = rec.strip().split("\x1f")
        if len(parts) < 2:
            continue
        sha, subject = parts[0].strip(), parts[1]
        m = re.search(r"pull request #(\d+)", subject)
        if not m:
            continue
        try:
            files = subprocess.run(
                ["git", "diff", "--name-only", sha + "^1", sha],
                cwd=repo_root, capture_output=True, text=True, timeout=120).stdout.split()
        except Exception:
            files = []
        merges.append({"pr": int(m.group(1)), "subject": subject.strip(),
                       "body": re.sub(r"\s+", " ", (parts[2] if len(parts) > 2 else ""))[:400],
                       "files": files})
    return merges


def _rect_owner(rect, log_rects, subject, open_windows):
    """The window token this root belongs to.

    Four windows are placed at the SAME rect by the census (270,8,1000,700), so a
    plain rect lookup answers with an arbitrary one of them. The capture's own
    subject window is tried first, then the main window, and anything that matches
    neither is left unattributed rather than filed under a guess - a title filed
    under the wrong window poisons that window's record vocabulary.
    """
    log_rects = log_rects or {}

    def matches(tok):
        r = log_rects.get(tok)
        return (r and len(r) == 4
                and r[0] == rect[0] and r[1] == rect[1] and r[2] == rect[2])

    if subject and matches(subject):
        return subject
    if "main" in (open_windows or ()) and matches("main"):
        return "main"
    if subject and subject not in log_rects:
        # A surface the seam never placed (the GuiTree probe window): the capture
        # is of nothing else, so the subject is the only candidate there is.
        return subject
    return None


def window_vocabulary(window_tokens, window_titles):
    """Per window, the words a repo record may name it by, and the words a SOURCE
    FILE may name it by.

    Two lists, because they answer different questions:

    * `record` - what the CHANGELOG and the todo call this window. A window whose
      captured title is stable contributes its words; a window whose title VARIES
      with its subject (the Structure window is titled by the route or mission it
      is showing) has no stable name at all, so only the seam token is used - the
      alternative attached every record mentioning a kerbal to the Structure
      window. The product's own name is never a record word: it appears in every
      entry in the file.
    * `file` - the same words plus the product name, so the main window, whose
      title IS the product name, can still be matched to `ParsekUI.cs`.
    """
    product = set()
    for tok in window_tokens:
        for title in window_titles.get(tok, ()):
            raw = (title or "").lower().strip()
            if raw and not re.sub(r"^parsek\b[\s-]*", "", raw).strip():
                product.add(norm(raw))
    vocab = {}
    for tok in window_tokens:
        titles = [t for t in (window_titles.get(tok) or ()) if t]
        per_title = []
        for title in titles:  # e.g. "Parsek - Real Spawn Control"
            cleaned = re.sub(r"^parsek\b[\s-]*", "", title.lower()).strip()
            if not cleaned:
                continue
            # A multi-word title contributes only its concatenation. Its
            # individual words are generic ("Real Spawn Control" -> real, spawn,
            # control; "Gloops Flight Recorder" -> flight, recorder) and each one
            # pulled in records about something else entirely.
            parts = [w for w in re.findall(r"[a-z]{4,}", cleaned)
                     if w not in ("parsek", "state", "window")]
            words = {norm(cleaned)} | (set(parts) if len(parts) == 1 else set())
            per_title.append(words)
        stable = set()
        if per_title:
            stable = set.intersection(*per_title)
        record = sorted(stable | {tok})
        vocab[tok] = {"record": [w for w in record if w not in product],
                      "file": sorted(set(record) | (product if titles and
                                                    not stable else set()))}
        if not vocab[tok]["record"]:
            vocab[tok]["record"] = [tok]
    return vocab


def attach_records(vocab, changelog, todos, merges, ui_paths=None):
    """Attach each repo record to every window whose vocabulary it names.

    A record is never rewritten here: the page quotes it and cites where it came
    from, which is the only way a note beside a picture can be trusted. What IS
    decided here is how loosely to attach, and the answer is "not very": a record
    counts for a window when it names it in its TITLE (or, for a todo entry, in
    its id) or names it at least twice in its body. Attaching on one passing
    mention put an entry about the dependency graph at the top of four unrelated
    windows.

    Ordering follows the same idea: a title match first, then `Changed` before
    `Fixed` before `Added`, so the entry beside the picture is the one that
    describes the window rather than the one that merely mentions it.

    PR attribution is by CHANGED FILE only, over the window sources: the merges
    that touched this window's own file. Matching merge subjects and bodies by
    name listed thirty PRs per window, most of which never went near it.
    """
    section_rank = {"Changed": 0, "Fixed": 1, "Removed": 2, "Added": 3}
    notes = {}
    for tok, words in vocab.items():
        record_words = words["record"] if isinstance(words, dict) else list(words)
        file_words = words["file"] if isinstance(words, dict) else list(words)
        useful = [w for w in record_words if len(w) > 3] or [tok]
        pats = [re.compile(r"\b" + re.escape(w) + r"s?\b", re.I) for w in useful]

        def score(text, head="", _pats=pats):
            body_hits = sum(len(q.findall(text or "")) for q in _pats)
            in_head = any(q.search(head or "") for q in _pats)
            return body_hits, in_head

        ranked = []
        for e in changelog:
            hits, in_title = score(e["body"], e["title"])
            if in_title or hits >= 2:
                ranked.append((0 if in_title else 1,
                               section_rank.get(e["section"], 9),
                               {"section": e["section"], "title": e["title"],
                                "body": e["body"][:1600]}))
        ranked.sort(key=lambda t: (t[0], t[1]))
        cl = [r[2] for r in ranked][:6]

        td = []
        for e in todos:
            # The head of a todo entry is its id plus its opening sentence: an
            # entry that names a window where it says what is wrong is about that
            # window even if it never repeats the name.
            hits, in_head = score(e["text"], e["id"] + " " + e["text"][:160])
            if in_head or hits >= 2:
                td.append({"id": e["id"], "done": e["done"], "fix": e["fix"][:700],
                           "text": e["text"][:900]})

        prs = []
        for mg in merges:
            for f in mg["files"]:
                if ui_paths and not is_window_source(f):
                    continue
                stem = norm(os.path.splitext(os.path.basename(f))[0])
                if any(w in stem for w in file_words if len(w) > 3):
                    prs.append({"pr": mg["pr"], "subject": mg["subject"][:160],
                                "file": os.path.basename(f)})
                    break
        notes[tok] = {
            "changelog": cl,
            "todo": [t for t in td if t["done"]][:8],
            "open": [t for t in td if not t["done"]][:10],
            "prs": prs[:8],
        }
    return notes


def is_window_source(path):
    """The window sources: everything under `Source/Parsek/UI/` plus the main
    window's own `ParsekUI.cs`. Widening this to the whole tree makes every PR
    that touched a recording look like a PR about a window."""
    slashed = path.replace("\\", "/")
    return ("Source/Parsek/UI/" in slashed
            or slashed.endswith("Source/Parsek/ParsekUI.cs"))


# --------------------------------------------------------------------------
# ingest
# --------------------------------------------------------------------------

def scan_shots_dir(path, scenarios_dir, want_colors=True, verbose=False):
    """One `<runId>_<specId>_shots` directory -> capture records."""
    base = os.path.basename(path.rstrip("/\\"))
    m = re.match(r"(?P<run>\d{4}-\d{2}-\d{2}_\d{4})_(?P<spec>.+?)_shots$", base)
    run_id = m.group("run") if m else base
    spec_id = m.group("spec") if m else ""
    fixture = spec_fixture(scenarios_dir, spec_id) if spec_id else ""
    logpath = os.path.join(path, "KSP.log")
    replay = {"captures": {}, "tabs": {}, "windows": []}
    if os.path.isfile(logpath):
        with open(logpath, "r", encoding="utf-8", errors="replace") as fh:
            replay = parse_ksp_log(fh.read())
    caps = []
    for name in sorted(os.listdir(path)):
        if not name.endswith(".gui.json"):
            continue
        label = name[:-len(".gui.json")]
        try:
            with open(os.path.join(path, name), "r", encoding="utf-8", errors="replace") as fh:
                dump = json.load(fh)
        except Exception as exc:
            if verbose:
                sys.stderr.write("skip %s: %s\n" % (name, exc))
            continue
        if dump.get("schema") != TREE_SCHEMA:
            if verbose:
                sys.stderr.write("skip %s: schema %r\n" % (name, dump.get("schema")))
            continue
        png = os.path.join(path, label + ".png")
        caps.append({
            "label": label,
            "dir": path,
            "runId": run_id,
            "specId": spec_id,
            "fixture": fixture,
            "dump": dump,
            "png": png if os.path.isfile(png) else None,
            "log": replay["captures"].get(label) or {},
        })
    return {"captures": caps, "tabs": replay["tabs"], "windows": replay["windows"],
            "runId": run_id, "specId": spec_id, "fixture": fixture}


def spec_fixture(scenarios_dir, spec_id):
    if not scenarios_dir:
        return ""
    path = os.path.join(scenarios_dir, spec_id + ".toml")
    if not os.path.isfile(path):
        return ""
    try:
        import tomllib
        with open(path, "rb") as fh:
            data = tomllib.load(fh)
        return fixture_of_template((data.get("fixture") or {}).get("saveTemplate", ""))
    except Exception:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            for ln in fh:
                m = re.match(r'\s*saveTemplate\s*=\s*"([^"]+)"', ln)
                if m:
                    return fixture_of_template(m.group(1))
    return ""


def classify_foreign(all_caps):
    """Roots that are not Parsek's.

    A root is matched to an open Parsek window by its (x, y, w) against the seam's
    own `uiaction rect` line. Whatever is left over and repeats identically under
    four or more different windows is another mod's chrome (the kRPC server
    window, the MechJeb menu button) rather than part of any Parsek surface. A
    root that repeats under one window only - a group picker, the Gloops
    recorder, the watch-mode overlay - is Parsek's and stays.
    """
    seen = defaultdict(set)
    for cap in all_caps:
        win = cap["log"].get("window") or "?"
        for root in cap["dump"].get("roots") or ():
            rect = tuple(int(v) for v in (root.get("rect") or [0, 0, 0, 0]))
            seen[(root.get("text") or "", rect)].add(win)
    # The rect the seam APPLIED to a Parsek window is proof of ownership wherever
    # it was applied, so it is collected across every run before anything is
    # judged: one lane that opened a window without re-placing it must not be
    # able to demote that window everywhere.
    owned = set()
    for cap in all_caps:
        for _tok, r in (cap["log"].get("rects") or {}).items():
            if len(r) == 4:
                owned.add((r[0], r[1], r[2]))
    foreign = set()
    for key, windows in seen.items():
        rect = key[1]
        if (rect[0], rect[1], rect[2]) in owned:
            continue
        if len(windows) >= 4:
            foreign.add(key)
    return foreign


# --------------------------------------------------------------------------
# build
# --------------------------------------------------------------------------

def build_model(shots_dirs, scenarios_dir, repo_root=None, with_photos=True,
                budget=DEFAULT_BUDGET_BYTES, verbose=False):
    scans = [scan_shots_dir(d, scenarios_dir, verbose=verbose) for d in shots_dirs]
    all_caps = [c for s in scans for c in s["captures"]]
    if not all_caps:
        raise SystemExit("no captures found under: %s" % ", ".join(shots_dirs))

    tabs_by_window = defaultdict(OrderedDict)
    window_tokens = OrderedDict()
    for s in scans:
        for w, pairs in s["tabs"].items():
            for tab, idx in pairs:
                tabs_by_window[w].setdefault(tab, idx)
        for w in s["windows"]:
            window_tokens[w] = True
    for cap in all_caps:
        w = cap["log"].get("window")
        if w:
            window_tokens[w] = True

    foreign = classify_foreign(all_caps)
    window_titles = defaultdict(set)

    captures = []
    photo_jobs = []
    for cap in all_caps:
        dump = cap["dump"]
        lab = parse_label(cap["label"], set(window_tokens),
                          {w: set(t) for w, t in tabs_by_window.items()})
        window = cap["log"].get("window") or lab["window"] or lab["host"]
        tab = cap["log"].get("tab") or lab["tab"]
        mode = cap["log"].get("mode") or lab["mode"]
        state = lab["state"]
        tab_alias = None
        if lab["window"] and lab["window"] != window:
            state = "-".join([p for p in [lab["window"], lab["tab"], state] if p])
        elif lab["tab"] and lab["tab"] != tab:
            state = "-".join([p for p in [lab["tab"], state] if p])
        elif tab and tabs_by_window.get(window) and state:
            # The window's tabs were RENAMED in the product but the seam token is
            # unchanged (the Kerbals rebuild: token `outcomes`, heading `Flights`).
            # The label's leading state token is then the tab's display name, not a
            # state, and pretending otherwise would file two tabs as one.
            head = state.split("-")[0]
            if head and head not in (tabs_by_window.get(window) or {}):
                if any(norm(txt) == norm(head) for txt in _root_texts(dump)):
                    tab_alias = head
                    state = "-".join(state.split("-")[1:])

        screen = dump.get("screen") or {}
        sw = int(screen.get("width") or FRAME_W)
        sh = int(screen.get("height") or FRAME_H)

        sampler = None
        grid_runs = None
        pix = None
        if cap["png"]:
            try:
                pw, ph, bpp, px = read_png(cap["png"])
                pix = (pw, ph, bpp, px)

                def sampler(rect, _p=pix):
                    return sample_colors(_p[0], _p[1], _p[2], _p[3], rect)

                def grid_runs(rect, _p=pix):
                    return grid_label_runs(_p[0], _p[1], _p[2], _p[3], rect)
            except Exception as exc:
                if verbose:
                    sys.stderr.write("png %s: %s\n" % (cap["png"], exc))

        roots = []
        parsek_rects = []
        for root in dump.get("roots") or ():
            rect = [int(v) for v in (root.get("rect") or [0, 0, 0, 0])]
            key = (root.get("text") or "", tuple(rect))
            is_foreign = key in foreign
            h = root_height(root, cap["log"].get("rects"))
            node = compact_tree(root, [rect[0], rect[1]], sampler,
                                grid_runs=grid_runs)
            node["x"], node["y"] = rect[0], rect[1]
            node["h"] = h
            node["title"] = root.get("text") or ""
            if sampler is not None and node["title"]:
                # The title bar only: sampling the whole window rect finds the
                # ink of whatever row happens to be brightest instead.
                _tbg, tfg = sampler([rect[0], rect[1], rect[2], 24])
                node["fg"] = tfg or None
                if not node["fg"]:
                    node.pop("fg", None)
            if is_foreign:
                node["foreign"] = 1
            else:
                mark_tooltip_strip(node)
                parsek_rects.append([rect[0], rect[1], rect[2], h])
                # A title is filed under the window it BELONGS to, resolved by
                # the rect the seam applied for that token - not under the
                # capture's subject window. The main window stands open in almost
                # every capture, so filing every visible title under the subject
                # gave every window the product's own name in its vocabulary and
                # made every record match every window.
                owner = _rect_owner(rect, cap["log"].get("rects"), window,
                                    cap["log"].get("openWindows"))
                if owner:
                    window_titles[owner].add(root.get("text") or "")
            roots.append(node)

        photo = None
        if with_photos and pix and parsek_rects:
            x0 = max(0, min(r[0] for r in parsek_rects) - 2)
            y0 = max(0, min(r[1] for r in parsek_rects) - 2)
            x1 = min(sw, max(r[0] + r[2] for r in parsek_rects) + 2)
            y1 = min(sh, max(r[1] + r[3] for r in parsek_rects) + 2)
            photo = {"x": x0, "y": y0, "w": x1 - x0, "h": y1 - y0}
            photo_jobs.append((len(captures), pix, photo))

        cid = "%s/%s" % (cap["runId"], cap["label"])
        captures.append({
            "id": cid,
            "label": cap["label"],
            "runId": cap["runId"],
            "specId": cap["specId"],
            "fixture": cap["fixture"],
            "window": window,
            "tab": tab,
            "tabAlias": tab_alias,
            "scene": cap["log"].get("scene") or "",
            "mode": mode,
            "state": state,
            "capturedUtc": dump.get("capturedUtc") or "",
            "screen": [sw, sh],
            "openWindows": cap["log"].get("openWindows") or [],
            "dialog": cap["log"].get("dialog"),
            "roots": roots,
            "photo": photo,
            "counts": dump.get("counts") or {},
        })

    # ---- photo payloads under the size budget -------------------------------
    used = 0
    if photo_jobs:
        payload_budget = int(budget * 0.62)
        for quant, factor in ((8, 1), (16, 1), (24, 1), (24, 2)):
            used = 0
            blobs = {}
            for idx, pix, ph in photo_jobs:
                pw, phh, bpp, px = pix
                cw, ch, cpx = crop(pw, phh, bpp, px, ph["x"], ph["y"], ph["w"], ph["h"])
                cw, ch, cpx = subsample(cw, ch, bpp, cpx, factor)
                blob = write_png(cw, ch, bpp, cpx, quant)
                blobs[idx] = (blob, cw, ch)
                used += len(blob) * 4 // 3
            if used <= payload_budget:
                break
        for idx, (blob, cw, ch) in blobs.items():
            captures[idx]["photo"]["src"] = "data:image/png;base64," + \
                base64.b64encode(blob).decode("ascii")
            captures[idx]["photo"]["pw"] = cw
            captures[idx]["photo"]["ph"] = ch

    # ---- tab display names -------------------------------------------------
    # A selection grid reports only the SELECTED item's text (`textValue`), so no
    # single capture knows what the other tabs are called. Across the census each
    # tab was selected in turn, so the set of captures does know - and every name
    # on the tab bar is therefore still a string the game drew.
    tab_display = defaultdict(dict)
    for cap in captures:
        if not cap["tab"]:
            continue
        name = _first_grid_value(cap["roots"])
        if name:
            tab_display[cap["window"]].setdefault(cap["tab"], name)

    # ---- keys, before/after -------------------------------------------------
    by_key = defaultdict(list)
    for cap in captures:
        by_key[key_of(cap["fixture"], cap["window"], cap["tab"], cap["state"],
                      cap["mode"], cap["scene"])].append(cap)
    keys = {}
    for k, caps in by_key.items():
        caps.sort(key=lambda c: (c["capturedUtc"], c["runId"]))
        before, after = caps[0], caps[-1]
        changed = before["id"] != after["id"] and _differs(before, after)
        keys[k] = {
            "all": [c["id"] for c in caps],
            "before": before["id"],
            "after": after["id"],
            "changed": bool(changed),
            "measured": measure_pair(before, after) if changed else None,
        }

    fixtures = OrderedDict()
    for cap in captures:
        fixtures.setdefault(cap["fixture"], {"key": cap["fixture"], "specIds": []})
        if cap["specId"] not in fixtures[cap["fixture"]]["specIds"]:
            fixtures[cap["fixture"]]["specIds"].append(cap["specId"])

    windows = []
    for tok in window_tokens:
        caps = [c for c in captures if c["window"] == tok]
        windows.append({
            "token": tok,
            "titles": sorted(window_titles.get(tok, [])),
            "tabs": [{"token": t, "index": i,
                       "name": tab_display.get(tok, {}).get(t) or t}
                     for t, i in sorted(tabs_by_window.get(tok, {}).items(),
                                        key=lambda kv: kv[1])],
            "captureCount": len(caps),
        })
    for tok in sorted({c["window"] for c in captures} - set(window_tokens)):
        windows.append({"token": tok, "titles": sorted(window_titles.get(tok, [])),
                        "tabs": [], "captureCount": len([c for c in captures if c["window"] == tok])})

    edges = state_graph_edges(captures)

    # states with no capture: a tab the seam knows but no capture selected, per
    # window and mode.
    missing = []
    have = {(c["window"], c["tab"], c["mode"]) for c in captures}
    modes = sorted({c["mode"] for c in captures if c["mode"]})
    for w in windows:
        for tab in w["tabs"]:
            for mode in modes:
                if (w["token"], tab["token"], mode) not in have:
                    missing.append({"window": w["token"], "tab": tab["token"],
                                    "mode": mode, "why": "tab known to the seam, no capture"})

    notes = {}
    if repo_root:
        vocab = window_vocabulary([w["token"] for w in windows],
                                  {w["token"]: w["titles"] for w in windows})
        cl = _read(os.path.join(repo_root, "CHANGELOG.md"))
        td = _read(os.path.join(repo_root, "docs", "dev", "todo-and-known-bugs.md"))
        for extra in _done_volumes(repo_root):
            td += "\n" + _read(extra)
        notes = attach_records(vocab, parse_changelog(cl), parse_todo(td),
                               parse_merges(repo_root), ui_paths=True)

    return {
        "schema": MIRROR_SCHEMA,
        # The windows the command seam can open. A capture whose subject is not
        # one of them is a diagnostic surface (the GuiTree probe), shown in the
        # mirror because it WAS photographed but left out of Compare, which is
        # about the product's windows.
        "seamWindows": list(window_tokens.keys()),
        "fixtures": list(fixtures.values()),
        "windows": windows,
        "captures": captures,
        "keys": keys,
        "edges": edges,
        "missing": missing,
        "notes": notes,
        "modes": modes,
        "photoBytes": used,
    }


def _read(path):
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            return fh.read()
    except Exception:
        return ""


def _done_volumes(repo_root):
    base = os.path.join(repo_root, "docs", "dev", "done")
    out = []
    for dirpath, _dirs, files in os.walk(base):
        for f in files:
            if f.endswith(".md") and "todo" in f.lower():
                out.append(os.path.join(dirpath, f))
    return sorted(out)


def _differs(a, b):
    def sig(cap):
        return json.dumps([_strip(r) for r in cap["roots"] if not r.get("foreign")],
                          sort_keys=True)
    return sig(a) != sig(b)


def _strip(node):
    out = {k: v for k, v in node.items() if k not in ("bg", "fg")}
    if "c" in out:
        out["c"] = [_strip(ch) for ch in out["c"]]
    return out


# --------------------------------------------------------------------------
# HTML
# --------------------------------------------------------------------------

CSS = """
:root{
  --ink:#d6d6d6; --dim:#8b8b8b; --win:#444444; --winedge:#6a6a6a;
  --panel:#292929; --btn:#313131; --btnedge:#5a5a5a; --title:#e6e6e6;
  --page:#141414; --rail:#1c1c1c; --accent:#6e9fd0; --warn:#d08a4a;
  --ok:#7fb06a; --bad:#c05a5a;
  /* Calibrated, not guessed: the ink extent of three runs in
     bdk-kerbals-roster-expanded-advanced.png measured 136 / 144 / 87 px, and
     Arial at 13px renders them at 136.6 / 144.5 / 86.8. The dump's rects were
     laid out by KSP's own font, so getting this wrong shows up as text that
     overflows a cell the game fits. */
  --gfont:13px; --gtrack:0px;
}
*{box-sizing:border-box}
body{margin:0;background:var(--page);color:var(--ink);
  font:13px/1.45 "Segoe UI",system-ui,-apple-system,Arial,sans-serif}
a{color:var(--accent)}
#top{display:flex;align-items:center;gap:14px;padding:8px 12px;background:var(--rail);
  border-bottom:1px solid #000;position:sticky;top:0;z-index:50;flex-wrap:wrap}
#top h1{font-size:14px;margin:0;font-weight:600;letter-spacing:.3px}
#top .sp{flex:1}
select,button.ui{background:#2b2b2b;color:var(--ink);border:1px solid #555;
  border-radius:3px;padding:3px 7px;font:12px inherit;font-family:inherit;cursor:pointer}
button.ui.on{background:#3a5a7a;border-color:#6e9fd0;color:#fff}
#wrap{display:flex;align-items:flex-start}
#rail{width:262px;flex:0 0 262px;background:var(--rail);border-right:1px solid #000;
  padding:8px 0 40px;max-height:calc(100vh - 42px);overflow:auto;position:sticky;top:42px}
#rail h2{font-size:11px;text-transform:uppercase;letter-spacing:.08em;color:var(--dim);
  margin:12px 10px 4px}
#rail .w{padding:3px 10px;cursor:pointer;display:flex;gap:6px;align-items:baseline}
#rail .w:hover{background:#262626}
#rail .w.sel{background:#2f4257}
#rail .w b{font-weight:600}
#rail .w{user-select:none}
#rail .w .caret{display:inline-block;width:9px;color:var(--dim);
  transition:transform .12s ease;flex:0 0 auto}
#rail .w .caret.open{transform:rotate(90deg)}
#rail .w:focus-visible{outline:1px solid var(--accent);outline-offset:-1px}
#rail .w .n{color:var(--dim);font-size:11px;margin-left:auto}
#rail .s{padding:2px 10px 2px 22px;cursor:pointer;font-size:12px;color:#bfbfbf;
  display:flex;gap:6px}
#rail .s:hover{background:#262626}
#rail .s.sel{background:#33506b;color:#fff}
#rail .s.gap{color:#8a6a4a;cursor:not-allowed;font-style:italic}
#main{flex:1;min-width:0;padding:10px 14px 60px}
#status{font-size:12px;color:var(--dim);min-height:20px;margin:0 0 8px;
  border-left:3px solid #333;padding-left:8px}
#status.warn{color:var(--warn);border-color:var(--warn)}
.stagewrap{position:relative;overflow:auto;border:1px solid #000;background:#0c0c0c;
  max-width:100%}
.stage{position:relative;width:1280px;height:720px;
  background:#1a2430 url() no-repeat;flex:0 0 auto}
.stage.scene{background-image:linear-gradient(#20303c,#2c3a2c)}
.photo{position:absolute;image-rendering:pixelated;opacity:1;z-index:1}
/* Photo OVERLAY mode. The rendered layer collapses to outlines: text drawn over
   the photograph's own text is two copies of the same string a pixel or two
   apart, which reads as a rendering fault and hides the thing the overlay is for
   - whether each control's BOX lands where the game put it. */
.stage.overlay .kwin{background:none !important;border-color:rgba(110,159,208,.7)}
.stage.overlay .kwin>.kt{display:none}
.stage.overlay .gn{background:none !important;color:transparent !important;
  border:1px solid rgba(110,159,208,.35) !important}
.stage.overlay .gn.k-layoutgroup,.stage.overlay .gn.notext{border-color:transparent !important}
.stage.overlay .gn .tx,.stage.overlay .gn .cb,.stage.overlay .gn .gl{display:none}
.stage.overlay.noboxes .gn,.stage.overlay.noboxes .kwin{border-color:transparent !important}
.sidebyside{display:flex;gap:10px;align-items:flex-start;flex-wrap:wrap}
.sidebyside>div{flex:0 1 auto;min-width:0}
.sidebyside h5{margin:0 0 3px;font-size:11px;color:var(--dim);font-weight:600}
.kwin{position:absolute;background:var(--win);border:1px solid var(--winedge);
  border-radius:5px;z-index:5;font:var(--gfont)/1 Arial,Helvetica,sans-serif;overflow:hidden}
.kwin.foreign{opacity:.45}
/* 40px, not a guess: the title ink in the captures centres 20 px below the
   window's own rect top (KSP draws it inside the window style's top padding). */
.kwin>.kt{position:absolute;left:0;right:0;top:0;height:40px;text-align:center;
  color:var(--title);font-size:13px;line-height:40px;overflow:hidden;
  white-space:nowrap;pointer-events:none}
.gn{position:absolute;font:var(--gfont)/1 Arial,Helvetica,sans-serif;
  letter-spacing:var(--gtrack);white-space:pre;
  overflow:hidden;display:flex;align-items:center}
.gn.k-label{color:var(--ink)}
.gn.k-box,.gn.s-box{background:var(--panel);border:1px solid #1d1d1d;border-radius:3px;
  padding-left:4px}
/* A row container is a `box` with no text: KSP draws it in the panel colour, so
   a border here would invent a grid the game does not draw. */
.gn.k-box.notext{border-color:transparent;background:none;padding-left:0}
.gn.k-button,.gn.k-repeatbutton{background:var(--btn);border:1px solid var(--btnedge);
  border-radius:3px;justify-content:center;color:var(--ink);cursor:pointer}
.gn.k-button.s-label,.gn.k-button.s-{background:none;border:0;justify-content:flex-start;
  padding-left:0}
.gn.k-buttongrid{background:var(--btn);border:1px solid var(--btnedge);border-radius:3px}
.gn.k-buttongrid.grid{background:none;border:0}
.gn.k-buttongrid .gi{position:absolute;top:0;bottom:0;background:var(--btn);
  border:1px solid var(--btnedge);border-radius:3px;color:var(--ink);cursor:pointer;
  font:var(--gfont)/1 Arial,Helvetica,sans-serif}
.gn.k-buttongrid .gi>.gl{left:0;right:0}
.gn.k-buttongrid .gi.on{background:#5c5c5c;color:#fff;border-color:#7d7d7d}
.gn.k-buttongrid .gl{position:absolute;top:0;bottom:0;display:flex;align-items:center;
  justify-content:center;white-space:pre;overflow:hidden}
.gn.k-buttongrid .gi:hover{outline:1px solid var(--accent);outline-offset:-1px}
.gn.k-toggle{cursor:pointer}
.gn.k-toggle .cb{width:14px;height:14px;border:1px solid #777;background:#222;
  display:inline-block;margin-right:4px;text-align:center;line-height:12px;font-size:11px;
  color:#cfe6ff;flex:0 0 auto}
.gn.k-textfield{background:#1e1e1e;border:1px solid #666;border-radius:2px;padding-left:3px}
.gn.k-scrollview{overflow:hidden}
.gn.k-slider{display:flex;align-items:center}
.gn.k-slider::before{content:"";position:absolute;left:0;right:0;top:50%;height:3px;
  background:#666;border-radius:2px}
.gn.k-layoutgroup{}
.gn.dis{opacity:.42}
.gn.click:hover{outline:1px solid var(--accent);outline-offset:-1px}
.gn.flash{animation:fl .55s ease-out 2}
@keyframes fl{0%{box-shadow:inset 0 0 0 2px var(--warn)}100%{box-shadow:none}}
.cmp{margin:18px 0 30px;border-top:1px solid #2a2a2a;padding-top:12px}
.cmp h3{margin:0 0 4px;font-size:15px}
.cmp .pair{display:flex;gap:14px;align-items:flex-start;flex-wrap:wrap;margin:10px 0}
.cmp .side{flex:1 1 520px;min-width:0}
.cmp .side h4{margin:0 0 4px;font-size:12px;color:var(--dim);font-weight:600}
.cmp .note{background:#1b1b1b;border:1px solid #2c2c2c;border-radius:4px;padding:8px 10px;
  font-size:12px;margin:8px 0}
.cmp .note b{color:#e8e8e8}
.cmp .note ul{margin:4px 0 4px 18px;padding:0}
.cmp .note li{margin:2px 0}
.cmp .cite{color:var(--dim);font-size:11px;margin-top:4px}
.cmp .note>div{margin:3px 0}
.cmp .note code{color:#c8d8a8;font-family:Consolas,monospace}
.cmp .note details{margin-top:6px}
.cmp .note summary{cursor:pointer;color:var(--accent);font-size:11px}
.cmp .note .rec{margin:6px 0 0;padding-left:8px;border-left:2px solid #333;
  font-size:11px;color:#b5b5b5}
.cmp .num{font-family:Consolas,monospace;color:#c8d8a8}
table.sum{border-collapse:collapse;width:100%;font-size:12px;margin:8px 0 20px}
table.sum th,table.sum td{border:1px solid #2c2c2c;padding:4px 7px;text-align:left;
  vertical-align:top}
table.sum th{background:#1f1f1f;color:var(--dim);font-weight:600}
table.sum td.y{color:var(--ok)}
table.sum td.n{color:var(--dim)}
/* The window's own tooltip echo strip: `TooltipEchoBox` draws one box-styled
   label, wrapped, one or two text lines tall, and scrolls (never ellipsises)
   text that will not fit. */
.gn.strip{display:block;padding:2px 4px;white-space:normal;overflow:hidden;
  line-height:15px;color:#b9c6d4}
.gn.strip .tx{display:block;white-space:normal}
.gn.strip.over .tx{white-space:pre;animation:mq 9s linear infinite}
@keyframes mq{0%{transform:translateX(0)}8%{transform:translateX(0)}
  92%{transform:translateX(var(--mqshift,-50%))}100%{transform:translateX(var(--mqshift,-50%))}}
.echo{margin-top:6px;font-size:12px;color:#b9c6d4;min-height:18px;
  background:#1b1b1b;border:1px solid #2c2c2c;border-radius:3px;padding:4px 8px}
.dlg{position:absolute;z-index:40;left:50%;transform:translateX(-50%);top:150px;
  background:#2c2c2c;border:1px solid #777;border-radius:6px;padding:0 0 10px;
  min-width:360px;box-shadow:0 8px 30px #000a}
.dlg .dt{background:#3a3a3a;padding:6px 10px;font-weight:600;color:#fff;font-size:13px;
  border-radius:5px 5px 0 0}
.dlg img{display:block;max-width:560px;margin:8px auto 6px;border:1px solid #111}
.dlg .db{display:flex;gap:8px;justify-content:center;padding:4px 10px}
.small{font-size:11px;color:var(--dim)}
.hidden{display:none !important}
"""

JS = r"""
'use strict';
var M = window.__MIRROR__;
var byId = {};
M.captures.forEach(function(c){ byId[c.id] = c; });
var S = {
  view: 'mirror',
  window: null, tab: null, state: null, mode: null,
  fixture: null, photo: 'off', boxes: true, foreign: false, capture: null,
  collapsed: {}
};
/* Which windows are folded shut in the rail. A per-viewer convenience, so it
   lives in localStorage and every access is guarded: a private window, cleared
   site data or a preview can make the accessor throw or answer empty, and the
   rail has to come up either way. */
var RAIL_KEY = 'parsek-gui-mirror.rail-collapsed';
function loadCollapsed(){
  try {
    var raw = window.localStorage.getItem(RAIL_KEY);
    if (!raw) return null;
    var arr = JSON.parse(raw);
    if (!arr || !arr.length && arr.length !== 0) return null;
    var out = {};
    arr.forEach(function(t){ out[t] = 1; });
    return out;
  } catch (e) { return null; }
}
function saveCollapsed(){
  try {
    window.localStorage.setItem(RAIL_KEY,
      JSON.stringify(Object.keys(S.collapsed).filter(function(t){ return S.collapsed[t]; })));
  } catch (e) { /* per-viewer convenience only; nothing depends on it */ }
}
/* off -> the rendering alone (the default: it is the thing being checked)
   overlay -> the photograph alone, with the rendering as outlines over it
   side -> the rendering and the photograph next to each other, same scale */
var PHOTO_MODES = ['off', 'overlay', 'side'];
var FIX_ORDER = M.fixtures.map(function(f){ return f.key; });

function el(tag, cls, txt){
  var e = document.createElement(tag);
  if (cls) e.className = cls;
  if (txt != null) e.textContent = txt;
  return e;
}
function norm(s){ return (s||'').toLowerCase().replace(/[^a-z0-9]+/g,''); }
function capsFor(win){ return M.captures.filter(function(c){ return c.window === win; }); }
function status(msg, warn){
  var s = document.getElementById('status');
  s.textContent = msg || '';
  s.className = warn ? 'warn' : '';
}

/* ---- dataset choice: exact fixture, else nearest in declared order ---- */
/* Rank within a pool: the plain state before a named one, the first tab before a
   later one, then the newest capture. Opening a window should land where the
   game lands - its default tab with nothing unfolded - not on whichever capture
   the last census run happened to take last. */
function rank(win){
  var tabs = ((M.windows.filter(function(w){ return w.token===win; })[0])||{}).tabs || [];
  var order = {};
  tabs.forEach(function(t){ order[t.token] = t.index; });
  return function(a, b){
    var sa = (a.state ? 1 : 0), sb = (b.state ? 1 : 0);
    if (sa !== sb) return sa - sb;
    var ta = (a.tab in order) ? order[a.tab] : 99;
    var tb = (b.tab in order) ? order[b.tab] : 99;
    if (ta !== tb) return ta - tb;
    /* Fewer state tokens is closer to how the window opens: `collapsed` before
       `grouppicker-setparent`. Alphabetical then decides between equals, which
       puts the closed fold before the open one. */
    var na = (a.state||'').split('-').length, nb = (b.state||'').split('-').length;
    if (na !== nb) return na - nb;
    if ((a.state||'') !== (b.state||'')) return (a.state||'') < (b.state||'') ? -1 : 1;
    return (a.capturedUtc < b.capturedUtc) ? 1 : -1;
  };
}
function pick(win, tab, state, mode, fixture){
  var pool = capsFor(win).filter(function(c){
    return (tab == null || c.tab === tab) && (state == null || c.state === state) &&
           (mode == null || c.mode === mode);
  });
  if (!pool.length) return null;
  var cmp = rank(win);
  var exact = pool.filter(function(c){ return c.fixture === fixture; });
  if (exact.length) return { cap: exact.sort(cmp)[0], exact: true };
  for (var i=0;i<FIX_ORDER.length;i++){
    var f = FIX_ORDER[i];
    var alt = pool.filter(function(c){ return c.fixture === f; });
    if (alt.length) return { cap: alt.sort(cmp)[0], exact: false };
  }
  return { cap: pool.sort(cmp)[0], exact: false };
}

/* ---- rendering: every rect comes from the dump, nothing is laid out here ---- */
function renderNode(n, out, opts){
  opts = opts || {};
  var d = el('div', 'gn k-' + n.k + (n.s ? ' s-' + n.s : '') + (n.e === 0 ? ' dis' : '') +
                   (n.t ? '' : ' notext'));
  d.style.left = n.x + 'px'; d.style.top = n.y + 'px';
  d.style.width = n.w + 'px'; d.style.height = n.h + 'px';
  if (n.bg && (n.k === 'box' || n.s === 'box' || n.k === 'button' ||
               n.k === 'buttongrid' || n.k === 'textfield' || n.k === 'window')) {
    d.style.background = n.bg;
  }
  if (n.fg) d.style.color = n.fg;
  if (n.k === 'toggle'){
    var cb = el('span','cb', n.v ? 'x' : '');
    d.appendChild(cb);
  }
  if (n.k === 'buttongrid'){
    /* A selection grid reports only the selected item. The item NAMES come from
       the captures where each tab was in turn selected, and the widths from
       IMGUI's own equal-split rule, so the bar is still all capture. */
    var tabs = ((M.windows.filter(function(w){ return w.token === (opts.win||''); })[0])||{}).tabs || [];
    if (tabs.length > 1){
      d.classList.add('grid');
      var seg = n.w / tabs.length;
      var runs = (n.gi && n.gi.length === tabs.length) ? n.gi : null;
      tabs.forEach(function(t, i){
        var b = el('div','gi' + (norm(t.name) === norm(n.tv||'') ? ' on' : ''));
        b.style.left = (i*seg) + 'px'; b.style.width = seg + 'px';
        var lab = el('span','gl', t.name);
        if (runs){
          /* the label's own measured position in this very frame */
          lab.style.left = (runs[i][0] - i*seg) + 'px';
          lab.style.width = runs[i][1] + 'px';
        }
        b.appendChild(lab);
        b.dataset.click = '1'; b.dataset.tab = t.token;
        d.appendChild(b);
      });
      out.appendChild(d);
      return d;
    }
  }
  if (n.t || (n.k === 'buttongrid' && n.tv)){
    var span = el('span', 'tx', n.t || n.tv);
    d.appendChild(span);
  }
  if (n.p){ d.title = n.p; d.dataset.tip = n.p; }
  if (n.strip){ d.classList.add('strip'); d.dataset.strip = String(n.strip); }
  var clicky = (n.k === 'button' || n.k === 'repeatbutton' || n.k === 'toggle' ||
                n.k === 'buttongrid' || n.k === 'textfield' || n.k === 'slider');
  if (clicky){ d.classList.add('click'); d.dataset.click = '1'; }
  out.appendChild(d);
  (n.c || []).forEach(function(ch){ renderNode(ch, d, opts); });
  return d;
}

/* The crop, at its own pixel size, at the crop's own origin: the photograph is
   never stretched or re-aspected, so a box that lands on its own outline in
   overlay mode really does land there in the game. A photo the size budget had to
   subsample is upscaled by the same factor on both axes. */
function photoImg(cap){
  var img = el('img','photo');
  img.src = cap.photo.src;
  img.style.left = cap.photo.x + 'px'; img.style.top = cap.photo.y + 'px';
  img.style.width = cap.photo.w + 'px'; img.style.height = cap.photo.h + 'px';
  img.alt = cap.label;
  return img;
}
function renderCapture(cap, host, opts){
  opts = opts || {};
  host.innerHTML = '';
  /* Two windows declare a minimum wider than the 1280-wide census instance, so
     their captures run off the frame. The stage grows to the widest root instead
     of cropping them - the scroll is the honest rendering of a window the
     instance cannot fit. */
  var ext = cap.roots.reduce(function(m, r){
    return Math.max(m, (r.x||0) + (r.w||0)); }, cap.screen[0]);
  var exty = cap.roots.reduce(function(m, r){
    return Math.max(m, (r.y||0) + (r.h||0)); }, cap.screen[1]);
  host.style.width = ext + 'px';
  host.style.height = exty + 'px';
  host.classList.add('scene');
  host.classList.remove('overlay');
  host.classList.remove('noboxes');
  if (opts.photo === 'overlay' && cap.photo && cap.photo.src){
    host.appendChild(photoImg(cap));
    host.classList.add('overlay');
    if (opts.boxes === false) host.classList.add('noboxes');
  }
  if (opts.photoOnly && cap.photo && cap.photo.src){
    host.appendChild(photoImg(cap));
    return host;
  }
  cap.roots.forEach(function(r){
    if (r.foreign && !opts.foreign) return;
    var w = el('div', 'kwin' + (r.foreign ? ' foreign' : ''));
    w.style.left = r.x + 'px'; w.style.top = r.y + 'px';
    w.style.width = r.w + 'px'; w.style.height = r.h + 'px';
    if (r.bg) w.style.background = r.bg;
    var t = el('div','kt', r.title || '');
    if (r.fg) t.style.color = r.fg;
    w.appendChild(t);
    (r.c || []).forEach(function(ch){ renderNode(ch, w, {win: cap.window}); });
    host.appendChild(w);
  });
  if (cap.dialog && cap.photo && cap.photo.src){
    host.appendChild(buildDialog(cap));
  }
  wireEcho(host);
  return host;
}

/* The modals are stock uGUI: no control tree exists for them, so the picture is
   the crop the census photographed and the buttons are the labels the seam
   reported. Nothing about them is reconstructed. */
function buildDialog(cap){
  var box = el('div','dlg');
  box.appendChild(el('div','dt', cap.dialog.title || cap.dialog.name));
  var img = el('img');
  img.src = cap.photo.src;
  img.alt = cap.dialog.title || '';
  box.appendChild(img);
  var row = el('div','db');
  (cap.dialog.buttons.length ? cap.dialog.buttons : ['OK']).forEach(function(b){
    var btn = el('button','ui', b);
    btn.onclick = function(){ box.classList.add('hidden');
      status('dialog "' + (cap.dialog.title||'') + '" dismissed with ' + b + '.'); };
    row.appendChild(btn);
  });
  box.appendChild(row);
  return box;
}

/* Hovering a control writes its real tooltip into the strip of the window the
   control is in - the same box the game writes it into - and clears it on the way
   out. Wrapped; if the text does not fit the strip's own line count the strip
   scrolls it, which is what TooltipEchoBox does rather than clipping. */
function setStrip(win, text){
  if (!win) return;
  var strip = win.querySelector('[data-strip]');
  if (!strip) return;
  var span = strip.querySelector('.tx');
  if (!span){ span = el('span','tx'); strip.appendChild(span); }
  span.textContent = text || '';
  strip.classList.remove('over');
  span.style.removeProperty('--mqshift');
  if (text && strip.scrollHeight > strip.clientHeight + 1){
    strip.classList.add('over');
    var over = span.scrollWidth - strip.clientWidth;
    span.style.setProperty('--mqshift', (-Math.max(over, 0) - 8) + 'px');
  }
}
function wireEcho(host){
  host.addEventListener('mouseover', function(ev){
    var t = ev.target.closest ? ev.target.closest('[data-tip]') : null;
    var e = document.getElementById('echo');
    if (e) e.textContent = t ? t.dataset.tip : '';
    Array.prototype.forEach.call(host.querySelectorAll('.kwin'), function(w){
      setStrip(w, (t && w.contains(t)) ? t.dataset.tip : ''); });
  });
  host.addEventListener('mouseleave', function(){
    var e = document.getElementById('echo');
    if (e) e.textContent = '';
    Array.prototype.forEach.call(host.querySelectorAll('.kwin'), function(w){
      setStrip(w, ''); });
  });
}

/* ---- click routing: a control switches state only where a capture exists ---- */
function routeClick(ev, cap){
  var node = ev.target.closest('[data-click]');
  if (!node) return;
  ev.stopPropagation();
  if (node.dataset.tab){
    if (pick(cap.window, node.dataset.tab, null, cap.mode, S.fixture)){
      go(cap.window, node.dataset.tab, null, cap.mode); return;
    }
    flash(node, 'no capture for this state yet: ' + cap.window + ' / tab ' +
          node.dataset.tab + ' / ' + cap.mode + '.');
    return;
  }
  var txt = norm(node.querySelector('.tx') ? node.querySelector('.tx').textContent : '');
  if (!txt) { flash(node, 'that control has no text to match a capture by.'); return; }

  /* a launcher: the control names another window the census captured */
  var wins = M.windows.filter(function(w){ return w.captureCount > 0; });
  for (var i=0;i<wins.length;i++){
    var w = wins[i];
    var names = [norm(w.token)].concat(w.titles.map(function(t){
      return norm((t||'').replace(/^Parsek\s*-?\s*/i,'')); }));
    if (names.indexOf(txt) >= 0 && w.token !== cap.window){
      go(w.token, null, null, S.mode); return;
    }
  }
  /* close */
  if (txt === 'close' || txt === 'closewindow'){
    if (cap.window !== 'main'){ go('main', null, null, S.mode); }
    else { status('Close pressed: the main window closes.'); }
    return;
  }
  /* a tab of this window */
  var tabs = (M.windows.filter(function(w){ return w.token===cap.window; })[0]||{}).tabs || [];
  for (var j=0;j<tabs.length;j++){
    if (tabs[j].token === cap.tab &&
        (norm(tabs[j].name) === txt || norm(tabs[j].token) === txt)){
      status('already on the ' + tabs[j].name + ' tab.');
      return;
    }
    if (norm(tabs[j].token) === txt || txt.indexOf(norm(tabs[j].token)) === 0){
      if (pick(cap.window, tabs[j].token, null, S.mode, S.fixture)){
        go(cap.window, tabs[j].token, null, S.mode); return;
      }
      flash(node, 'no capture for this state yet: ' + cap.window + ' / tab ' +
            tabs[j].token + ' / ' + S.mode + '.');
      return;
    }
  }
  /* a state of this window+tab, matched on the state tokens the labels carry */
  var pool = capsFor(cap.window).filter(function(c){
    return c.tab === cap.tab && c.mode === cap.mode && c.state !== cap.state; });
  /* A fold control carries a glyph, not a word, so no token can match it. Where
     the census photographed EXACTLY TWO states of this tab on this dataset, the
     fold is unambiguous and toggles between them; with three or more it would be
     a guess, and a guess is what this page must not do. */
  var glyphy = /^[^0-9A-Za-z\s]/.test(
    (node.querySelector('.tx') ? node.querySelector('.tx').textContent : '').trim());
  if (glyphy){
    var pair = pool.filter(function(c){
      return c.fixture === cap.fixture && (c.state||'').split('-').length === 1; });
    /* Count DISTINCT states, not captures: a lane re-flown three times offers the
       same counterpart three times over. */
    var names = {};
    pair.forEach(function(c){ names[c.state||''] = 1; });
    if (Object.keys(names).length === 1){
      select(pair.sort(rank(cap.window))[0], true); return;
    }
  }
  var best = null, bestScore = 0;
  pool.forEach(function(c){
    (c.state||'').split('-').forEach(function(tok){
      if (!tok) return;
      var t = norm(tok);
      var sc = (txt === t) ? 3 : (txt.indexOf(t) >= 0 || t.indexOf(txt) >= 0 ? 1 : 0);
      if (t.length < 4) sc = (txt === t) ? 3 : 0;
      if (sc > bestScore){ bestScore = sc; best = c; }
    });
  });
  if (best){ select(best); return; }
  flash(node, 'no capture for this state yet - "' +
        (node.querySelector('.tx').textContent || '').slice(0,48) +
        '" was never photographed in another state.');
}

function flash(node, msg){
  node.classList.remove('flash');
  void node.offsetWidth;
  node.classList.add('flash');
  status(msg, true);
}

function go(win, tab, state, mode){
  var r = pick(win, tab, state, mode, S.fixture) ||
          pick(win, tab, state, null, S.fixture) ||
          pick(win, null, null, mode, S.fixture) ||
          pick(win, null, null, null, S.fixture);
  if (!r){ status('no capture for window ' + win + ' yet.', true); return; }
  select(r.cap, r.exact);
}

function select(cap, exact){
  S.collapsed[cap.window] = false;   /* show what was just selected */
  S.capture = cap.id; S.window = cap.window; S.tab = cap.tab;
  S.state = cap.state; if (cap.mode) S.mode = cap.mode;
  var stage = document.getElementById('stage');
  var side = document.getElementById('sidestage');
  var sidewrap = document.getElementById('sidewrap');
  renderCapture(cap, stage, { photo: S.photo, boxes: S.boxes, foreign: S.foreign });
  stage.onclick = function(ev){ routeClick(ev, cap); };
  var wantSide = (S.photo === 'side' && cap.photo && cap.photo.src);
  sidewrap.classList.toggle('hidden', !wantSide);
  if (wantSide){
    renderCapture(cap, side, { photoOnly: true, foreign: S.foreign });
  } else {
    side.innerHTML = '';
  }
  var bits = [cap.window, cap.tab, cap.state, cap.mode].filter(Boolean).join(' / ');
  var msg = bits + '   [' + cap.fixture + ' | ' + cap.label + ' | run ' + cap.runId + ']';
  if (exact === false && cap.fixture !== S.fixture){
    msg += '   -- no capture on "' + S.fixture + '", showing the nearest dataset "' +
           cap.fixture + '".';
    status(msg, true);
  } else { status(msg); }
  buildRail();
}

/* ---- left rail: every window and state with a capture, and the gaps ---- */
/* Each window header is a disclosure toggle, not a shortcut: clicking it folds
   that window's captures away and selects nothing. Everything starts folded
   except the window being shown, and a capture selected from anywhere else - a
   launcher, a tab, the Compare view - unfolds its own window on the way in. */
function buildRail(){
  var rail = document.getElementById('rail');
  rail.innerHTML = '';
  rail.appendChild(el('h2', null, 'Windows (' + M.captures.length + ' captures)'));
  M.windows.filter(function(w){ return w.captureCount > 0; }).forEach(function(w){
    var open = !S.collapsed[w.token];
    var listId = 'rail-' + w.token;
    var row = el('div', 'w' + (w.token === S.window && S.view === 'mirror' ? ' sel' : ''));
    row.setAttribute('role', 'button');
    row.setAttribute('tabindex', '0');
    row.setAttribute('aria-expanded', open ? 'true' : 'false');
    row.setAttribute('aria-controls', listId);
    row.title = (open ? 'Hide' : 'Show') + ' the ' + w.captureCount +
                ' captures of ' + w.token;
    row.appendChild(el('span', 'caret' + (open ? ' open' : ''), '\u25b8'));
    row.appendChild(el('b', null, w.token));
    row.appendChild(el('span', 'n', String(w.captureCount)));
    function toggle(ev){
      if (ev) ev.stopPropagation();
      S.collapsed[w.token] = open;   /* was open -> now collapsed */
      saveCollapsed();
      buildRail();
    }
    row.onclick = toggle;
    row.onkeydown = function(ev){
      if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); toggle(ev); }
    };
    rail.appendChild(row);

    var list = el('div', 'wlist');
    list.id = listId;
    list.dataset.collapsible = '1';
    list.hidden = !open;
    var seen = {};
    capsFor(w.token).forEach(function(c){
      var k = [c.tab || '-', c.state || '-', c.mode || '-'].join(' / ');
      if (seen[k]) return;
      seen[k] = 1;
      var sr = el('div', 's' + (c.id === S.capture ? ' sel' : ''));
      sr.appendChild(el('span', null, k));
      sr.appendChild(el('span', 'n', c.fixture));
      sr.onclick = function(){ select(c, c.fixture === S.fixture); };
      list.appendChild(sr);
    });
    M.missing.filter(function(m){ return m.window === w.token; }).forEach(function(m){
      var sr = el('div', 's gap');
      sr.textContent = (m.tab || '-') + ' / ' + (m.mode || '-') + '  (no capture)';
      sr.title = m.why;
      sr.onclick = function(){ status('no capture for this state yet: ' + m.window +
        ' / tab ' + m.tab + ' / ' + m.mode + ' -- ' + m.why, true); };
      list.appendChild(sr);
    });
    rail.appendChild(list);
  });
  var zero = M.windows.filter(function(w){ return !w.captureCount; });
  if (zero.length){
    rail.appendChild(el('h2', null, 'Known, never captured'));
    zero.forEach(function(w){
      var row = el('div','s gap', w.token + '  (no capture)');
      row.onclick = function(){ status('the seam knows window "' + w.token +
        '" but no census lane photographed it.', true); };
      rail.appendChild(row);
    });
  }
}

function showView(){
  document.getElementById('mirrorView').classList.toggle('hidden', S.view !== 'mirror');
  document.getElementById('compareView').classList.toggle('hidden', S.view !== 'compare');
  document.getElementById('btnMirror').classList.toggle('on', S.view === 'mirror');
  document.getElementById('btnCompare').classList.toggle('on', S.view === 'compare');
  if (S.view === 'compare') buildCompare();
}

/* ---- Compare: earliest capture of a key beside the latest ---- */
var compareBuilt = false;
function buildCompare(){
  if (compareBuilt) return;
  compareBuilt = true;
  var host = document.getElementById('compareView');
  host.innerHTML = '';
  host.appendChild(el('p','small',
    'BEFORE is the earliest capture of a (fixture, window, tab, state, mode) key; ' +
    'AFTER is the latest. Both sides are drawn by the same generator off their own ' +
    'dump, so a layout difference on the page is a layout difference in the game. ' +
    'The notes are lifted from the repo records named beside them; the numbers are ' +
    'measured off the two dumps.'));

  var rows = {};
  Object.keys(M.keys).forEach(function(k){
    var info = M.keys[k];
    var cap = byId[info.after];
    if (!cap) return;
    if (M.seamWindows.indexOf(cap.window) < 0) return;
    (rows[cap.window] = rows[cap.window] || []).push({ k: k, info: info });
  });

  /* summary table */
  var tb = el('table','sum');
  var hr = el('tr');
  ['Window','Changed','What (from the record)','PRs'].forEach(function(h){
    hr.appendChild(el('th',null,h)); });
  tb.appendChild(hr);
  var order = Object.keys(rows).sort();
  order.forEach(function(win){
    var changed = rows[win].some(function(r){ return r.info.changed; });
    var note = M.notes[win] || {};
    var what = (note.changelog && note.changelog.length)
      ? note.changelog[0].title
      : (changed ? 'capture set differs; no CHANGELOG entry names this window'
                 : 'no change yet');
    var prs = (note.prs || []).map(function(p){ return '#' + p.pr; }).join(' ');
    var tr = el('tr');
    tr.appendChild(el('td',null,win));
    var td = el('td', changed ? 'y' : 'n', changed ? 'yes' : 'no');
    tr.appendChild(td);
    tr.appendChild(el('td',null,what));
    tr.appendChild(el('td',null,prs || '-'));
    tb.appendChild(tr);
  });
  host.appendChild(tb);

  order.forEach(function(win){
    var sec = el('div','cmp');
    sec.appendChild(el('h3', null, win));
    sec.appendChild(noteBlock(win, rows[win]));
    rows[win].sort(function(a,b){ return (b.info.changed?1:0)-(a.info.changed?1:0); });
    rows[win].forEach(function(r){
      var before = byId[r.info.before], after = byId[r.info.after];
      var pair = el('div','pair');
      pair.appendChild(sideBlock(r.info.changed ? 'BEFORE' : 'UNCHANGED (one capture)',
                                 before, r.info));
      if (r.info.changed) pair.appendChild(sideBlock('AFTER', after, r.info));
      sec.appendChild(el('div','small', r.k.split('|').filter(Boolean).join(' / ')));
      sec.appendChild(pair);
      if (r.info.measured) sec.appendChild(measuredBlock(r.info.measured));
    });
    host.appendChild(sec);
  });
}

function sideBlock(title, cap, info){
  var side = el('div','side');
  var h = el('h4', null, title + '  -  ' + (cap ? cap.runId + ' / ' + cap.label : '?'));
  side.appendChild(h);
  if (!cap){ side.appendChild(el('div','small','no capture')); return side; }
  var bar = el('div');
  var pb = el('button','ui','show the photo');
  var wrap = el('div','stagewrap');
  var stage = el('div','stage');
  wrap.appendChild(stage);
  /* Two states, not an overlay: the two sides of a Compare pair are already side
     by side, so what a reader wants here is to swap one side for its photograph,
     not to stack text on text. */
  var showing = { photo:false };
  pb.onclick = function(){
    showing.photo = !showing.photo;
    pb.classList.toggle('on', showing.photo);
    pb.textContent = showing.photo ? 'show the rendering' : 'show the photo';
    renderCapture(cap, stage, { photoOnly: showing.photo, foreign: S.foreign });
  };
  bar.appendChild(pb);
  side.appendChild(bar);
  side.appendChild(wrap);
  renderCapture(cap, stage, { photo:'off', foreign:S.foreign });
  wrap.style.maxHeight = '420px';
  return side;
}

function measuredBlock(m){
  var d = el('div','note');
  d.appendChild(el('b',null,'Measured off the two dumps: '));
  var parts = [];
  parts.push('nodes ' + m.nodesBefore + ' -> ' + m.nodesAfter);
  parts.push('text lines ' + m.textsBefore + ' -> ' + m.textsAfter);
  if (m.maxDxBefore != null || m.maxDxAfter != null){
    parts.push('worst header-to-cell dx ' + (m.maxDxBefore==null?'-':m.maxDxBefore) +
               ' px -> ' + (m.maxDxAfter==null?'-':m.maxDxAfter) + ' px');
  }
  if (m.maxDwBefore != null || m.maxDwAfter != null){
    parts.push('worst dw ' + (m.maxDwBefore==null?'-':m.maxDwBefore) + ' px -> ' +
               (m.maxDwAfter==null?'-':m.maxDwAfter) + ' px');
  }
  var s = el('span','num', parts.join('   |   '));
  d.appendChild(s);
  if (m.gone.length || m.added.length){
    var ul = el('ul');
    if (m.gone.length){
      var li = el('li'); li.appendChild(el('b',null,'gone: '));
      li.appendChild(document.createTextNode(m.gone.join(' / ').slice(0,400)));
      ul.appendChild(li);
    }
    if (m.added.length){
      var li2 = el('li'); li2.appendChild(el('b',null,'new: '));
      li2.appendChild(document.createTextNode(m.added.join(' / ').slice(0,400)));
      ul.appendChild(li2);
    }
    d.appendChild(ul);
  }
  return d;
}

/* The record beside the picture, short by default. The CHANGELOG entries are
   paragraphs and the todo entries are longer still; printed in full they buried
   the windows they were describing, so the note shows one what / how / why and
   keeps the rest one click away. Nothing is summarised - only truncated, with the
   full text in the fold. */
function plain(t, n){
  var out = (t || '').replace(/[`*~]/g, '').replace(/\s+/g, ' ').trim();
  return (n && out.length > n) ? out.slice(0, n).replace(/\s\S*$/, '') + '...' : out;
}
function noteBlock(win, keyRows){
  var note = M.notes[win] || {};
  var d = el('div','note');
  var changed = keyRows.some(function(r){ return r.info.changed; });
  var cl = note.changelog || [], done = note.todo || [], open = note.open || [];

  if (!changed && !cl.length){
    var p0 = el('div');
    p0.appendChild(el('b',null,'no change yet. '));
    p0.appendChild(document.createTextNode(
      'No capture of this window differs between its earliest and its latest run, '
      + 'and no entry of the current version names it.'));
    d.appendChild(p0);
  }
  if (cl.length){
    var w = el('div');
    w.appendChild(el('b',null,'WHAT: '));
    w.appendChild(document.createTextNode(plain(cl[0].title, 220)));
    d.appendChild(w);
    var h = el('div');
    h.appendChild(el('b',null,'HOW: '));
    h.appendChild(document.createTextNode(plain(cl[0].body, 420)));
    d.appendChild(h);
  }
  if (done.length){
    var y = el('div');
    y.appendChild(el('b',null,'WHY: '));
    y.appendChild(el('code',null,done[0].id));
    y.appendChild(document.createTextNode(
      ' - ' + plain(done[0].fix || done[0].text, 300)));
    d.appendChild(y);
  }
  if (open.length){
    var o = el('div','cite');
    o.textContent = 'Still open here: ' + open.map(function(t){ return t.id; }).join(', ');
    d.appendChild(o);
  }
  if ((note.prs||[]).length){
    var c = el('div','cite');
    c.textContent = 'PRs touching this window\'s source: ' +
      note.prs.map(function(p){ return '#' + p.pr + ' (' + p.file + ')'; }).join(', ');
    d.appendChild(c);
  }

  var more = document.createElement('details');
  var sum = document.createElement('summary');
  sum.textContent = 'the full record (' + cl.length + ' changelog, ' + done.length +
                    ' closed, ' + open.length + ' open)';
  more.appendChild(sum);
  cl.forEach(function(c){
    var p = el('div','rec');
    p.appendChild(el('b',null,'[' + c.section + '] ' + plain(c.title) + ' '));
    p.appendChild(document.createTextNode(plain(c.body)));
    more.appendChild(p);
  });
  done.concat(open).forEach(function(t){
    var p = el('div','rec');
    p.appendChild(el('b',null,(t.done ? '[done] ' : '[open] ') + t.id + ' '));
    p.appendChild(document.createTextNode(plain(t.fix || t.text)));
    more.appendChild(p);
  });
  d.appendChild(more);

  var cite = el('div','cite');
  cite.textContent = 'Sources: CHANGELOG.md (current version, entries naming this '
    + 'window), docs/dev/todo-and-known-bugs.md (GUI-* ids), git merge commits '
    + 'touching this window\'s UI source. Quoted, not rewritten.';
  d.appendChild(cite);
  return d;
}

/* ---- boot ---- */
function boot(){
  var fixSel = document.getElementById('fixture');
  M.fixtures.forEach(function(f){
    var o = document.createElement('option');
    o.value = f.key; o.textContent = f.key;
    fixSel.appendChild(o);
  });
  /* Default dataset: the one that photographed the most DIFFERENT windows at
     the Space Center - the census's general-purpose host - rather than the one
     with the most captures, which is whichever lane happened to be longest. */
  var breadth = {};
  M.captures.forEach(function(c){
    if ((c.scene||'') !== 'SPACECENTER') return;
    (breadth[c.fixture] = breadth[c.fixture] || {})[c.window] = 1; });
  S.fixture = FIX_ORDER.slice().sort(function(a,b){
    return Object.keys(breadth[b]||{}).length - Object.keys(breadth[a]||{}).length;
  })[0];
  fixSel.value = S.fixture;
  fixSel.onchange = function(){ S.fixture = fixSel.value; compareBuilt=false;
    go(S.window, S.tab, S.state, S.mode); };
  S.mode = M.modes.indexOf('advanced') >= 0 ? 'advanced' : (M.modes[0]||null);

  var mb = document.getElementById('btnMode');
  function paintMode(){ mb.textContent = 'mode: ' + S.mode; }
  mb.onclick = function(){
    var i = M.modes.indexOf(S.mode);
    S.mode = M.modes[(i+1) % M.modes.length];
    paintMode();
    go(S.window, S.tab, S.state, S.mode);
  };
  paintMode();

  var pb = document.getElementById('btnPhoto');
  var bb = document.getElementById('btnBoxes');
  function paintPhoto(){
    pb.textContent = 'photo: ' + S.photo;
    pb.classList.toggle('on', S.photo !== 'off');
    bb.classList.toggle('hidden', S.photo !== 'overlay');
    bb.classList.toggle('on', S.boxes);
    bb.textContent = S.boxes ? 'outlines on' : 'outlines off';
  }
  pb.onclick = function(){
    S.photo = PHOTO_MODES[(PHOTO_MODES.indexOf(S.photo) + 1) % PHOTO_MODES.length];
    paintPhoto();
    if (byId[S.capture]) select(byId[S.capture], true);
  };
  bb.onclick = function(){ S.boxes = !S.boxes; paintPhoto();
    if (byId[S.capture]) select(byId[S.capture], true); };
  paintPhoto();
  var fb = document.getElementById('btnForeign');
  fb.onclick = function(){ S.foreign = !S.foreign; fb.classList.toggle('on', S.foreign);
    if (byId[S.capture]) select(byId[S.capture], true); };
  document.getElementById('btnMirror').onclick = function(){ S.view='mirror'; showView(); };
  document.getElementById('btnCompare').onclick = function(){ S.view='compare'; showView(); };

  var first = capsFor('main')[0] || M.captures[0];
  var stored = loadCollapsed();
  if (stored){
    S.collapsed = stored;
  } else {
    M.windows.forEach(function(w){ S.collapsed[w.token] = true; });
  }
  showView();
  select(first, first.fixture === S.fixture);
}
document.addEventListener('DOMContentLoaded', boot);
"""


def render_html(model):
    fixtures = ", ".join(f["key"] for f in model["fixtures"])
    head = [
        "<!doctype html>",
        '<html lang="en"><head><meta charset="utf-8">',
        '<meta name="viewport" content="width=device-width,initial-scale=1">',
        "<title>Parsek GUI mirror</title>",
        "<style>%s</style>" % CSS,
        "</head><body>",
        '<div id="top">',
        "<h1>Parsek GUI mirror</h1>",
        '<button class="ui on" id="btnMirror">mirror</button>',
        '<button class="ui" id="btnCompare">compare</button>',
        '<span class="small">dataset</span><select id="fixture"></select>',
        '<button class="ui" id="btnMode">mode</button>',
        '<button class="ui" id="btnPhoto">photo: off</button>',
        '<button class="ui hidden" id="btnBoxes">outlines on</button>',
        '<button class="ui" id="btnForeign">other mods</button>',
        '<span class="sp"></span>',
        '<span class="small">%d captures, %d windows, %d fixtures (%s)</span>'
        % (len(model["captures"]), len([w for w in model["windows"] if w["captureCount"]]),
           len(model["fixtures"]), esc(fixtures)),
        "</div>",
        '<div id="wrap"><div id="rail"></div><div id="main">',
        '<div id="status"></div>',
        '<div id="mirrorView">',
        '<div class="sidebyside">',
        '<div><h5>rendered from the control tree</h5>'
        '<div class="stagewrap"><div class="stage" id="stage"></div></div></div>',
        '<div id="sidewrap" class="hidden"><h5>the frame the tree was dumped on</h5>'
        '<div class="stagewrap"><div class="stage" id="sidestage"></div></div></div>',
        '</div>',
        '<div class="echo" id="echo"></div>',
        '<p class="small">Hovering a control puts its real tooltip in the strip above '
        "and on the element itself. A click switches to the capture of that state where "
        "the census produced one; where it did not, the control flashes and the status "
        "line says so. Nothing on this page is drawn from anything but a capture.</p>",
        "</div>",
        '<div id="compareView" class="hidden"></div>',
        "</div></div>",
        "<script>window.__MIRROR__=",
        json_for_script(_page_model(model)),
        ";</script>",
        "<script>%s</script>" % JS,
        "</body></html>",
    ]
    return "\n".join(head)


def json_for_script(obj):
    """JSON safe to inline in a `<script>` block.

    Every string on the page is a control's own text, and a control whose text
    contained `</script>` would otherwise close the block and break the page out
    of its own data - the same breakout the viewer's escaping cell guards. The
    angle brackets and the line separators JS treats as terminators are escaped
    as unicode, which is still valid JSON.
    """
    return (json.dumps(obj, separators=(",", ":"))
            .replace("<", "\\u003c")
            .replace(">", "\\u003e")
            .replace("&", "\\u0026")
            .replace("\u2028", "\\u2028")
            .replace("\u2029", "\\u2029"))


def _page_model(model):
    """The page gets everything except the raw dumps' unused bookkeeping."""
    return {
        "schema": model["schema"],
        "seamWindows": model["seamWindows"],
        "fixtures": model["fixtures"],
        "windows": model["windows"],
        "modes": model["modes"],
        "captures": model["captures"],
        "keys": model["keys"],
        "missing": model["missing"],
        "notes": model["notes"],
    }


def build_index(model):
    """The companion JSON: coverage without the geometry."""
    per_window = {}
    for cap in model["captures"]:
        w = per_window.setdefault(cap["window"], {"captures": 0, "states": {}, "fixtures": {}})
        w["captures"] += 1
        k = "%s / %s / %s" % (cap["tab"] or "-", cap["state"] or "-", cap["mode"] or "-")
        w["states"][k] = w["states"].get(k, 0) + 1
        w["fixtures"][cap["fixture"]] = w["fixtures"].get(cap["fixture"], 0) + 1
    return {
        "schema": MIRROR_SCHEMA,
        "captureCount": len(model["captures"]),
        "seamWindows": model["seamWindows"],
        "fixtures": [f["key"] for f in model["fixtures"]],
        "windows": per_window,
        "missing": model["missing"],
        "compare": {k: {"before": v["before"], "after": v["after"], "changed": v["changed"]}
                    for k, v in model["keys"].items()},
        "photoBytes": model.get("photoBytes", 0),
    }


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--shots", action="append", required=True,
                    help="a `<runId>_<specId>_shots` directory; repeatable")
    ap.add_argument("--scenarios", default=None,
                    help="harness/scenarios, for each run's fixture.saveTemplate")
    ap.add_argument("--repo", default=None,
                    help="repo root, for the Compare notes (CHANGELOG, todo, merges)")
    ap.add_argument("--out", required=True)
    ap.add_argument("--index", default=None)
    ap.add_argument("--no-photos", action="store_true")
    ap.add_argument("--budget-mb", type=float, default=16.0)
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args(argv)

    dirs = []
    for d in args.shots:
        for part in ([d] if os.path.isdir(d) else sorted(__import__("glob").glob(d))):
            if os.path.isdir(part):
                dirs.append(part)
    if not dirs:
        raise SystemExit("no shots directories matched")

    scenarios = args.scenarios
    if scenarios is None and args.repo:
        scenarios = os.path.join(args.repo, "harness", "scenarios")

    model = build_model(dirs, scenarios, repo_root=args.repo,
                        with_photos=not args.no_photos,
                        budget=int(args.budget_mb * 1024 * 1024),
                        verbose=args.verbose)
    html = render_html(model)
    with open(args.out, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(html)
    size = os.path.getsize(args.out)
    if args.index:
        with open(args.index, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(build_index(model), fh, indent=1, sort_keys=True)
    budget = int(args.budget_mb * 1024 * 1024)
    sys.stderr.write("gui-mirror: %d captures, %d windows -> %s (%.2f MB of %.0f MB)\n"
                     % (len(model["captures"]),
                        len([w for w in model["windows"] if w["captureCount"]]),
                        args.out, size / 1048576.0, args.budget_mb))
    if size > budget:
        sys.stderr.write("gui-mirror: OVER BUDGET by %.2f MB\n" % ((size - budget) / 1048576.0))
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
