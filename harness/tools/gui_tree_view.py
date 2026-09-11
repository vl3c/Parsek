#!/usr/bin/env python3
"""Offline viewer for a Parsek GUI-tree dump (``<label>.gui.json``).

Given one dump - and optionally the matching ``<label>.png`` screenshot - writes a
self-contained ``<label>.gui.html``: outlined boxes at every node's screen rect drawn
over the screenshot, coloured by control kind and labelled with the node's text, plus a
collapsible tree panel beside it. Hovering a box highlights its tree row and vice
versa; clicking either scrolls the other into view.

    python harness/tools/gui_tree_view.py <label>.gui.json [-o out.html]
    python harness/tools/gui_tree_view.py --batch results/<runId>_shots   # + gui-tree-index.html

The dump's producer is ``Source/Parsek/GuiTreeRecorder.cs``; the schema is
``parsek-gui-tree/1``, documented in ``docs/dev/design-gui-tree-dump.md``.

CONTRACTS (binding):
  - Stdlib only, like every other tool in this directory.
  - Self-contained output: inline CSS and JS, no CDN, no external asset. The
    screenshot is embedded as a ``data:`` URI so a single HTML file can be opened
    or handed on anywhere. That is not cosmetic - the pages get read on machines
    with no access to the run directory.
  - Read-only and failure-isolated: it reads a dump and writes ``*.gui.html`` /
    ``gui-tree-index.html`` beside it, and nothing else. A malformed, truncated
    or empty-tree dump produces a page that SAYS so rather than raising, because
    the malformed case is exactly the one someone is trying to look at.
  - The batch index is ``gui-tree-index.html`` and NOT ``index.html``, because
    ``tools/gui_contact_sheet.py`` owns ``index.html`` inside the very same
    ``*_shots`` directory. A census runs both tools over one directory (the
    README's steps 4 and 5), so a shared filename would mean whichever ran last
    silently replaced the other's page.
  - The dump is untrusted text. EVERY string reaching the HTML goes through
    ``html_escape`` - the page title, the box captions, the tree rows and their
    detail column - so a control labelled ``"><img src=x onerror=...>`` renders as
    text. There is deliberately no JSON payload inlined in a ``<script>`` block:
    the page's JS reads what it needs off the DOM, so there is no second escaping
    path to keep correct. ``harness/lib/test_gui_tree_view.py`` fires the payloads
    at both surfaces.
"""

from __future__ import annotations

import argparse
import base64
import glob
import html
import json
import mimetypes
import os
import sys

SCHEMA_ID = "parsek-gui-tree/1"
DUMP_SUFFIX = ".gui.json"
OUT_SUFFIX = ".gui.html"

# The ``--batch`` index's filename. Deliberately NOT ``index.html``: that name belongs to
# ``tools/gui_contact_sheet.py``, which writes it into the SAME ``*_shots`` directory a
# census points this tool at, so sharing it would make the two tools clobber each other.
BATCH_INDEX_FILENAME = "gui-tree-index.html"

# Colour per kind. Containers get cool hues and leaves warm ones, so the nesting
# skeleton reads at a glance and the controls stand out against it. `control` is
# deliberately loud: it means the recorder could not name the kind.
KIND_COLORS = {
    "window": "#4f8cff",
    "group": "#4fd2ff",
    "scrollview": "#00b8a9",
    "layoutgroup": "#7a8cff",
    "label": "#ffd166",
    "box": "#c9a227",
    "button": "#ff8c42",
    "repeatbutton": "#ff6f3c",
    "toggle": "#a06cd5",
    "textfield": "#06d6a0",
    "buttongrid": "#ef476f",
    "slider": "#f78c6b",
    "control": "#ff0055",
}
FALLBACK_COLOR = "#9aa0a6"

# Kinds whose boxes are drawn but whose fill stays clear, so a deep stack of
# containers does not wash out the screenshot underneath.
CONTAINER_KINDS = ("window", "group", "scrollview", "layoutgroup")


# ---------------------------------------------------------------------------
# Pure layer: everything below is unit-tested in test_gui_tree_view.py.
# ---------------------------------------------------------------------------


def html_escape(value) -> str:
    """Any dump value as HTML-safe text. ``None`` becomes an empty string."""
    if value is None:
        return ""
    return html.escape(str(value), quote=True)


def kind_color(kind) -> str:
    """Colour for a kind, falling back for a kind this viewer does not know.

    An unknown kind must render, not vanish: a newer mod build may emit one, and a
    box the viewer silently dropped would read as a missing control.
    """
    if not isinstance(kind, str):
        return FALLBACK_COLOR
    return KIND_COLORS.get(kind.lower(), FALLBACK_COLOR)


def rect_of(node):
    """A node's screen rect as ``(x, y, w, h)`` floats, or None.

    None for anything that is not four numbers, or for a rect with no drawable
    area - a zero-size carrier label has a real place in the TREE and no place on
    the overlay.
    """
    if not isinstance(node, dict):
        return None
    raw = node.get("rect")
    if not isinstance(raw, (list, tuple)) or len(raw) != 4:
        return None
    out = []
    for v in raw:
        if isinstance(v, bool) or not isinstance(v, (int, float)):
            return None
        out.append(float(v))
    if out[2] <= 0.5 or out[3] <= 0.5:
        return None
    return tuple(out)


def node_label(node) -> str:
    """The one-line caption for a node: its text, else its value, else its kind."""
    if not isinstance(node, dict):
        return "?"
    for key in ("text", "textValue"):
        value = node.get(key)
        if isinstance(value, str) and value.strip():
            return value
    kind = node.get("kind")
    return kind if isinstance(kind, str) else "?"


def describe_rect(rect) -> str:
    """``[x, y, w, h]`` for display, integers when they are whole."""
    if rect is None:
        return "-"

    def fmt(v):
        return str(int(v)) if float(v).is_integer() else ("%.2f" % v)

    return "[%s, %s, %s, %s]" % tuple(fmt(v) for v in rect)


def flatten(dump):
    """Depth-first walk of the dump's roots into a flat list of render records.

    Each record is ``{id, depth, kind, color, label, rect, tooltip, style,
    enabled, extras, children}``, with ``id`` the DFS index so a box and its tree
    row can address each other. Draw order is preserved, which is also paint
    order, so a later box legitimately sits over an earlier one.
    """
    records = []
    if not isinstance(dump, dict):
        return records
    roots = dump.get("roots")
    if not isinstance(roots, list):
        return records

    def visit(node, depth, parent_id):
        if not isinstance(node, dict):
            return
        index = len(records)
        kind = node.get("kind")
        record = {
            "id": index,
            "parent": parent_id,
            "depth": depth,
            "kind": kind if isinstance(kind, str) else "?",
            "color": kind_color(kind),
            "label": node_label(node),
            "rect": rect_of(node),
            "localRect": node.get("localRect"),
            "tooltip": node.get("tooltip"),
            "style": node.get("style"),
            "enabled": node.get("enabled", True) is not False,
            "extras": extras_of(node),
            "children": [],
        }
        records.append(record)
        if parent_id is not None:
            records[parent_id]["children"].append(index)
        kids = node.get("children")
        if isinstance(kids, list):
            for child in kids:
                visit(child, depth + 1, index)

    for root in roots:
        visit(root, 0, None)
    return records


def extras_of(node) -> str:
    """The optional per-kind fields, as one compact display string."""
    if not isinstance(node, dict):
        return ""
    parts = []
    if "value" in node:
        parts.append("value=%s" % ("true" if node.get("value") else "false"))
    if isinstance(node.get("textValue"), str):
        parts.append('textValue="%s"' % node["textValue"])
    if "horizontal" in node:
        parts.append("horizontal" if node.get("horizontal") else "vertical")
    for key in ("windowId", "controlId", "clipDepth"):
        if isinstance(node.get(key), int) and not isinstance(node.get(key), bool):
            parts.append("%s=%d" % (key, node[key]))
    return " ".join(parts)


def screen_size(dump):
    """The captured screen size as ``(w, h)``, defaulting to 1920x1080.

    A default rather than a failure: the overlay only needs a coordinate frame to
    scale into, and a dump missing the block is still worth looking at.
    """
    if isinstance(dump, dict):
        block = dump.get("screen")
        if isinstance(block, dict):
            w, h = block.get("width"), block.get("height")
            if isinstance(w, int) and isinstance(h, int) and w > 0 and h > 0:
                return w, h
    return 1920, 1080


def health_notes(dump):
    """Human-readable warnings derived from the dump's own counters.

    This is the part a supervising agent should read FIRST: a dump whose
    interceptions were bypassed still parses, still renders and is still missing
    controls, so the page has to say so out loud rather than looking complete.
    """
    notes = []
    if not isinstance(dump, dict):
        return ["the dump is not a JSON object"]

    schema = dump.get("schema")
    if schema != SCHEMA_ID:
        notes.append("schema is %r, expected %r" % (schema, SCHEMA_ID))

    counts = dump.get("counts") if isinstance(dump.get("counts"), dict) else {}
    for key, message in (
        ("strayEnds", "%d end record(s) matched no open container"),
        ("recordFaults", "%d patch body fault(s) during the capture"),
        ("droppedOverCap", "%d event(s) dropped at the per-frame cap"),
        ("unclosedAtEnd", "%d container(s) were still open when the frame ended"),
    ):
        value = counts.get(key)
        if isinstance(value, int) and value > 0:
            notes.append(message % value)

    repaired = sum(
        counts.get(k, 0) if isinstance(counts.get(k), int) else 0
        for k in ("autoClosedByClip", "autoClosedByEnd", "autoClosedByRect")
    )
    if repaired:
        notes.append(
            "%d container(s) were closed by the assembler's recovery rules rather "
            "than by their own end record - expected if Mono inlined an End funnel"
            % repaired)

    inert = counts.get("rectRuleInert")
    if isinstance(inert, int) and inert > 0:
        # Not an error - a zero-size carrier group is ordinary GUILayout - but the
        # layout nesting at those points rests on the End pairing alone, with no
        # independent rect check behind it.
        notes.append(
            "layout nesting unverified at %d point(s): every open layout group had a "
            "degenerate rect, so the containment rule could not be applied there"
            % inert)

    matrix = dump.get("guiMatrix")
    if isinstance(matrix, dict) and matrix.get("identity") is False:
        # GUIUtility.GUIToScreenRect converts a rect's ORIGIN only, so the recorder
        # multiplied every width and height through by m00 / m11 itself. Worth saying
        # out loud: a reader comparing boxes against a screenshot needs to know.
        notes.append(
            "GUI.matrix was not identity (m00=%s m11=%s m03=%s m13=%s); widths and "
            "heights were scaled by the recorder, origins by Unity"
            % (matrix.get("m00"), matrix.get("m11"),
               matrix.get("m03"), matrix.get("m13")))

    funnels = dump.get("funnels")
    if isinstance(funnels, list):
        unpatched = [f.get("name") for f in funnels
                     if isinstance(f, dict) and f.get("patched") is False]
        silent = [f.get("name") for f in funnels
                  if isinstance(f, dict) and f.get("patched") is True
                  and f.get("hits") == 0]
        if unpatched:
            notes.append("NOT PATCHED (signature drift): " + ", ".join(
                str(n) for n in unpatched))
        if silent:
            notes.append("patched but never hit (inlined, or nothing drew it): "
                         + ", ".join(str(n) for n in silent))

    if not flatten(dump):
        notes.append("the dump contains no nodes at all")
    return notes


def find_screenshot(json_path):
    """The screenshot beside a dump, or None.

    ``<label>.gui.json`` pairs with ``<label>.png``; a run's own screenshot is
    also accepted under ``<label>.jpg`` / ``.jpeg``.
    """
    if not json_path.endswith(DUMP_SUFFIX):
        stem = os.path.splitext(json_path)[0]
    else:
        stem = json_path[:-len(DUMP_SUFFIX)]
    for ext in (".png", ".jpg", ".jpeg"):
        candidate = stem + ext
        if os.path.isfile(candidate):
            return candidate
    return None


def output_path_for(json_path, explicit=None):
    """Where the page goes: ``<label>.gui.html`` beside the dump by default."""
    if explicit:
        return explicit
    if json_path.endswith(DUMP_SUFFIX):
        return json_path[:-len(DUMP_SUFFIX)] + OUT_SUFFIX
    return os.path.splitext(json_path)[0] + OUT_SUFFIX


def embed_image(path):
    """The screenshot as a ``data:`` URI, or None when it cannot be read.

    Failure-isolated on purpose: a missing or unreadable image must still leave a
    usable page - the boxes carry their own text, so the tree is readable without
    the picture.
    """
    if not path:
        return None
    try:
        with open(path, "rb") as fh:
            blob = fh.read()
    except OSError:
        return None
    if not blob:
        return None
    mime = mimetypes.guess_type(path)[0] or "image/png"
    return "data:%s;base64,%s" % (mime, base64.b64encode(blob).decode("ascii"))


def load_dump(path):
    """Parse a dump. Returns ``(dump_or_None, error_or_None)``."""
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return json.load(fh), None
    except OSError as exc:
        return None, "cannot read %s: %s" % (path, exc)
    except ValueError as exc:
        return None, "%s is not valid JSON: %s" % (path, exc)


def render_page(dump, error=None, title=None, image_uri=None, source_name=None) -> str:
    """The whole self-contained HTML page."""
    records = flatten(dump)
    width, height = screen_size(dump)
    notes = health_notes(dump) if error is None else [error]
    label = title or (dump.get("label") if isinstance(dump, dict) else None) or "gui-tree"
    counts = dump.get("counts") if isinstance(dump, dict) and isinstance(
        dump.get("counts"), dict) else {}

    meta_bits = []
    if isinstance(dump, dict):
        for key in ("capturedUtc", "frame"):
            if dump.get(key) not in (None, ""):
                meta_bits.append("%s %s" % (key, dump[key]))
    meta_bits.append("screen %dx%d" % (width, height))
    for key in ("windows", "nodes", "events"):
        if isinstance(counts.get(key), int):
            meta_bits.append("%s %d" % (key, counts[key]))
    if source_name:
        meta_bits.append(source_name)

    out = []
    out.append("<!doctype html>")
    out.append('<html lang="en"><head><meta charset="utf-8">')
    out.append("<title>%s - Parsek GUI tree</title>" % html_escape(label))
    out.append("<style>%s</style>" % _CSS)
    out.append("</head><body>")
    out.append('<header><h1>%s</h1><div class="meta">%s</div></header>'
               % (html_escape(label),
                  " &middot; ".join(html_escape(bit) for bit in meta_bits)))

    if notes:
        out.append('<ul class="notes">')
        for note in notes:
            out.append("<li>%s</li>" % html_escape(note))
        out.append("</ul>")

    out.append('<div class="split">')

    # --- the stage: screenshot + absolutely positioned boxes ---------------
    out.append('<div class="stage-wrap"><div class="stage" id="stage" '
               'style="aspect-ratio:%d/%d">' % (width, height))
    if image_uri:
        out.append('<img id="shot" alt="capture" src="%s">' % image_uri)
    else:
        out.append('<div class="noshot">no screenshot beside this dump - '
                   'boxes are drawn on the bare capture frame</div>')
    for record in records:
        rect = record["rect"]
        if rect is None:
            continue
        x, y, w, h = rect
        klass = "box container" if record["kind"] in CONTAINER_KINDS else "box"
        if not record["enabled"]:
            klass += " disabled"
        out.append(
            '<div class="%s" data-id="%d" style="left:%.4f%%;top:%.4f%%;'
            'width:%.4f%%;height:%.4f%%;--c:%s"><span class="tag">%s</span></div>'
            % (klass, record["id"],
               100.0 * x / width, 100.0 * y / height,
               100.0 * w / width, 100.0 * h / height,
               record["color"], html_escape(record["label"])))
    out.append("</div></div>")

    # --- the tree panel ----------------------------------------------------
    out.append('<div class="tree" id="tree">')
    out.append('<div class="toolbar">'
               '<input id="filter" type="text" placeholder="filter by text, kind or style">'
               '<button id="expand" type="button">expand all</button>'
               '<button id="collapse" type="button">collapse all</button>'
               "</div>")
    out.append('<div class="rows">')
    for record in records:
        classes = ["row"]
        if not record["enabled"]:
            classes.append("disabled")
        detail = []
        if record["style"]:
            detail.append("style=%s" % record["style"])
        if record["extras"]:
            detail.append(record["extras"])
        if not record["enabled"]:
            detail.append("DISABLED")
        if record["tooltip"]:
            detail.append('tooltip="%s"' % record["tooltip"])
        out.append(
            '<div class="%s" data-id="%d" data-parent="%s" data-depth="%d" '
            'style="--indent:%dpx">'
            '<span class="twist">%s</span>'
            '<span class="chip" style="background:%s">%s</span>'
            '<span class="label">%s</span>'
            '<span class="rect">%s</span>'
            '<span class="detail">%s</span>'
            "</div>"
            % (" ".join(classes), record["id"],
               "" if record["parent"] is None else str(record["parent"]),
               record["depth"], 14 * record["depth"],
               "&#9662;" if record["children"] else "&nbsp;",
               record["color"], html_escape(record["kind"]),
               html_escape(record["label"]),
               html_escape(describe_rect(record["rect"])),
               html_escape(" ".join(detail))))
    out.append("</div></div>")
    out.append("</div>")

    out.append("<script>%s</script>" % _JS)
    out.append("</body></html>")
    return "\n".join(out) + "\n"


def render_batch_index(entries) -> str:
    """The ``--batch`` index (``gui-tree-index.html``): one row per page, newest first."""
    out = ["<!doctype html>", '<html lang="en"><head><meta charset="utf-8">',
           "<title>Parsek GUI tree dumps</title>",
           "<style>%s</style>" % _CSS, "</head><body>",
           "<header><h1>Parsek GUI tree dumps</h1>"
           '<div class="meta">%d dump(s)</div></header>' % len(entries),
           '<div class="rows index">']
    for entry in entries:
        out.append(
            '<div class="row"><a href="%s">%s</a>'
            '<span class="detail">%s</span></div>'
            % (html_escape(entry.get("href", "")),
               html_escape(entry.get("label", "")),
               html_escape(entry.get("detail", ""))))
    out.append("</div></body></html>")
    return "\n".join(out) + "\n"


def index_entry(json_path, out_path, dump, error=None):
    """One ``render_batch_index`` row for a processed dump."""
    label = "?"
    if isinstance(dump, dict) and isinstance(dump.get("label"), str):
        label = dump["label"]
    if label == "?":
        label = os.path.basename(json_path)
    if error:
        detail = error
    else:
        counts = dump.get("counts") if isinstance(dump, dict) else None
        counts = counts if isinstance(counts, dict) else {}
        problems = len(health_notes(dump))
        detail = "windows=%s nodes=%s notes=%d" % (
            counts.get("windows", "?"), counts.get("nodes", "?"), problems)
    return {"href": os.path.basename(out_path), "label": label, "detail": detail}


# ---------------------------------------------------------------------------
# Shell
# ---------------------------------------------------------------------------


def write_page(json_path, out_path=None, embed=True):
    """Render one dump to HTML. Returns ``(out_path, dump, error)``."""
    dump, error = load_dump(json_path)
    out_path = output_path_for(json_path, out_path)
    image_uri = embed_image(find_screenshot(json_path)) if embed else None
    page = render_page(dump, error=error, image_uri=image_uri,
                       source_name=os.path.basename(json_path))
    with open(out_path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(page)
    return out_path, dump, error


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="Render a Parsek GUI-tree dump as a self-contained HTML page.")
    parser.add_argument("dump", nargs="?", help="path to a <label>.gui.json")
    parser.add_argument("-o", "--output", help="output HTML path")
    parser.add_argument("--batch", metavar="DIR",
                        help="render every *.gui.json in DIR plus a "
                             + BATCH_INDEX_FILENAME)
    parser.add_argument("--no-embed", action="store_true",
                        help="do not embed the screenshot (smaller page, no picture)")
    args = parser.parse_args(argv)

    if not args.batch and not args.dump:
        parser.error("give a dump path or --batch DIR")
    if args.batch and args.output:
        parser.error("--output is meaningless with --batch")

    if args.dump:
        out_path, _, error = write_page(args.dump, args.output, embed=not args.no_embed)
        print("wrote %s%s" % (out_path, "" if error is None else " (with errors)"))
        return 0

    pattern = os.path.join(args.batch, "*" + DUMP_SUFFIX)
    dumps = sorted(glob.glob(pattern),
                   key=lambda p: (-os.path.getmtime(p), p))
    if not dumps:
        print("no %s files under %s" % (DUMP_SUFFIX, args.batch))
        return 0
    entries = []
    for path in dumps:
        out_path, dump, error = write_page(path, embed=not args.no_embed)
        entries.append(index_entry(path, out_path, dump, error))
        print("wrote %s" % out_path)
    index_path = os.path.join(args.batch, BATCH_INDEX_FILENAME)
    with open(index_path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(render_batch_index(entries))
    print("wrote %s" % index_path)
    return 0


_CSS = """
:root { color-scheme: dark; }
* { box-sizing: border-box; }
body { margin: 0; background: #14161a; color: #e6e6e6;
  font: 13px/1.45 "Segoe UI", system-ui, sans-serif; }
header { padding: 10px 14px; border-bottom: 1px solid #2a2f36; background: #1a1d22; }
h1 { margin: 0; font-size: 16px; font-weight: 600; }
.meta { color: #9aa0a6; font-size: 12px; margin-top: 2px; }
.notes { margin: 0; padding: 8px 14px 8px 32px; background: #33210f;
  border-bottom: 1px solid #5a3a12; color: #ffcf87; }
.notes li { margin: 2px 0; }
.split { display: flex; flex-wrap: wrap; align-items: flex-start; gap: 10px;
  padding: 10px; }
/* min-width:0 on both panels is load-bearing: the tree rows are nowrap, so
   without it the panel's min-content width wins the flex negotiation and
   pushes itself off the viewport. */
.stage-wrap { flex: 1 1 520px; min-width: 0; }
.stage { position: relative; width: 100%; background: #0c0e11;
  border: 1px solid #2a2f36; overflow: hidden; }
.stage img { position: absolute; inset: 0; width: 100%; height: 100%;
  display: block; }
.noshot { position: absolute; inset: 0; display: flex; align-items: flex-end;
  justify-content: center; color: #4b5563; padding: 8px; text-align: center;
  z-index: 0; }
.box { position: absolute; border: 1px solid var(--c); pointer-events: auto;
  z-index: 1; }
.box.container { border-style: dashed; }
.box.disabled { opacity: 0.55; }
.box .tag { position: absolute; left: 0; top: -1px;
  transform: translateY(-100%); background: var(--c); color: #11131a;
  font-size: 9px; line-height: 1.25; padding: 0 3px; white-space: nowrap;
  max-width: 240px; overflow: hidden; text-overflow: ellipsis;
  opacity: 0; pointer-events: none; }
.box:hover, .box.hot { border-width: 2px; box-shadow: 0 0 0 1px #000 inset; }
.box:hover .tag, .box.hot .tag { opacity: 1; }
.tree { flex: 1 1 360px; min-width: 0; max-height: calc(100vh - 120px);
  display: flex; flex-direction: column; border: 1px solid #2a2f36;
  background: #1a1d22; overflow: hidden; }
.toolbar { display: flex; gap: 6px; padding: 6px; border-bottom: 1px solid #2a2f36; }
.toolbar input { flex: 1; background: #0c0e11; border: 1px solid #2a2f36;
  color: #e6e6e6; padding: 3px 6px; }
.toolbar button { background: #262b33; border: 1px solid #3a414b; color: #e6e6e6;
  padding: 3px 8px; cursor: pointer; }
.rows { overflow: auto; flex: 1; }
@media (max-width: 900px) {
  .tree { max-height: 60vh; }
}
.row { display: flex; align-items: baseline; gap: 6px; padding: 1px 6px 1px 0;
  padding-left: calc(6px + var(--indent, 0px)); white-space: nowrap;
  border-top: 1px solid #1f232a; cursor: default; }
.row:hover, .row.hot { background: #2b3140; }
.row.disabled .label { color: #7b8189; font-style: italic; }
.row.hidden { display: none; }
.row.collapsed-child { display: none; }
.twist { width: 10px; color: #6b7280; cursor: pointer; user-select: none; }
.chip { color: #11131a; font-size: 10px; padding: 0 4px; border-radius: 2px; }
.label { overflow: hidden; text-overflow: ellipsis; max-width: 40ch; }
.rect { color: #6b7280; font-family: Consolas, monospace; font-size: 11px; }
.detail { color: #8b9198; font-size: 11px; overflow: hidden;
  text-overflow: ellipsis; }
.index .row { padding-left: 8px; }
.index a { color: #7cb0ff; }
"""

_JS = """
(function () {
  var boxes = {}, rows = {}, hot = null;
  document.querySelectorAll('.box').forEach(function (el) {
    boxes[el.dataset.id] = el;
  });
  document.querySelectorAll('.rows .row').forEach(function (el) {
    if (el.dataset.id !== undefined) { rows[el.dataset.id] = el; }
  });

  function setHot(id) {
    if (hot === id) { return; }
    [boxes[hot], rows[hot]].forEach(function (el) {
      if (el) { el.classList.remove('hot'); }
    });
    hot = id;
    [boxes[hot], rows[hot]].forEach(function (el) {
      if (el) { el.classList.add('hot'); }
    });
  }

  Object.keys(boxes).forEach(function (id) {
    boxes[id].addEventListener('mouseenter', function () { setHot(id); });
    boxes[id].addEventListener('click', function () {
      setHot(id);
      if (rows[id]) { rows[id].scrollIntoView({ block: 'nearest' }); }
    });
  });
  Object.keys(rows).forEach(function (id) {
    rows[id].addEventListener('mouseenter', function () { setHot(id); });
  });

  // Collapse: a row is hidden when any ancestor is collapsed. Recomputed from
  // scratch each time so a filter and a collapse cannot disagree.
  var collapsed = {};
  function reflow() {
    Object.keys(rows).forEach(function (id) {
      var el = rows[id], p = el.dataset.parent, hide = false;
      while (p !== '' && p !== undefined) {
        if (collapsed[p]) { hide = true; break; }
        p = rows[p] ? rows[p].dataset.parent : '';
      }
      el.classList.toggle('collapsed-child', hide);
    });
  }
  document.querySelectorAll('.rows .twist').forEach(function (tw) {
    tw.addEventListener('click', function () {
      var id = tw.parentElement.dataset.id;
      collapsed[id] = !collapsed[id];
      tw.innerHTML = collapsed[id] ? '&#9656;' : '&#9662;';
      reflow();
    });
  });
  var expand = document.getElementById('expand');
  var collapse = document.getElementById('collapse');
  function setAll(state) {
    Object.keys(rows).forEach(function (id) { collapsed[id] = state; });
    document.querySelectorAll('.rows .twist').forEach(function (tw) {
      tw.innerHTML = state ? '&#9656;' : '&#9662;';
    });
    reflow();
  }
  if (expand) { expand.addEventListener('click', function () { setAll(false); }); }
  if (collapse) { collapse.addEventListener('click', function () { setAll(true); }); }

  var filter = document.getElementById('filter');
  if (filter) {
    filter.addEventListener('input', function () {
      var q = filter.value.trim().toLowerCase();
      Object.keys(rows).forEach(function (id) {
        var el = rows[id];
        var hit = !q || el.textContent.toLowerCase().indexOf(q) >= 0;
        el.classList.toggle('hidden', !hit);
        if (boxes[id]) { boxes[id].style.display = hit ? '' : 'none'; }
      });
    });
  }
})();
"""


if __name__ == "__main__":
    sys.exit(main())
