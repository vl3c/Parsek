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
import datetime
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
# The owner's feedback blob carries its own schema id, because it leaves the page
# and is read back by an agent that has nothing else to key on.
NOTES_SCHEMA = "parsek-gui-mirror-notes/1"
MODE_TOKENS = ("advanced", "basic")
DEFAULT_BUDGET_BYTES = 16 * 1024 * 1024
# The census instance's frame. Overridden per capture by the dump's own `screen`.
FRAME_W, FRAME_H = 1280, 720

# The dataset a MOCKED capture is filed under. `fixture` is already part of
# `key_of`, so giving mocked captures their own dataset makes the Compare
# isolation structural rather than cosmetic: a mocked BEFORE can only ever pair
# with a mocked AFTER of the same state.
MOCK_FIXTURE = "mock"
# The catalogue state id's own separator, per design-gui-state-gallery 7.6:
# `<window>.<family>.<variant>`.
MOCK_STATE_SEP = "."
# The pointer op's own token for "GUI.tooltip was empty when the frame was taken".
POINTER_TOOLTIP_EMPTY = "-"
# The scene a dataset's breadth is judged at, which is the census's
# general-purpose host scene. Automation vocabulary, out of the seam's own
# `describe` line.
SCENE_SPACECENTER = "SPACECENTER"
# The verdicts a note can carry. Page chrome, not window text.
NOTE_VERDICTS = ("keep", "change", "unsure")
# One row of the export blob. Emitted into the page so the JS that builds a row
# and the Python that round-trips one cannot disagree about the field set.
NOTES_FIELDS = ("key", "window", "tab", "state", "mode", "fixture", "mocked",
                "mockState", "beforeId", "afterId", "verdict", "note")

# LAYOUT EPOCHS: window token -> the UTC instant from which that window's captures
# show its CURRENT layout. A capture of the window taken before it is `outdated`
# (old layout): the rail never lists it and it counts as no coverage, but it stays
# a Compare BEFORE picture. See `mark_layout_epochs` and design-gui-mirror.md
# section 17 (e).
#
# This is the one piece of product history the corpus cannot tell: a re-layout
# that removed no tab leaves nothing a capture could be told apart by, so every
# state a lane did not re-fly would otherwise stay "current" in the old layout.
#
# UPDATE THIS TABLE IN EVERY PR THAT CHANGES A WINDOW'S LAYOUT. Set `utc` to the
# `startedUtc` (run.py's result JSON) of the FIRST census run that PR flew on the
# new layout - NOT its merge time: a window-round PR re-flies its lanes before it
# merges, and a merge-time floor would mark that proof itself as the old layout.
# The floor is a time, not a build: a lane flown after it from a branch that does
# not carry the change still counts as current, so re-fly from a branch that does.
LAYOUT_EPOCHS = {
    # PR #1755 (merged 2026-09-22T19:16:54Z): no flight status block, bold title.
    # First run on it: GUI-1-census-ksc 2026-09-22_1841.
    "main": {"utc": "2026-09-22T18:41:11Z", "pr": 1755},
    # PR #1762 (merged 2026-09-22T20:38:41Z): slot-grouped roster, no Since column.
    # First run on it: GUI-11-census-kerbals-crewed 2026-09-22_2004.
    "kerbals": {"utc": "2026-09-22T20:04:25Z", "pr": 1762},
    # PR #1818 (after #1809 and #1792): every filter button one width, rows
    # left-aligned (the view row no longer stretches).
    # First run on it: GUI-24-census-timeline-filters 2026-09-25_1725.
    "timeline": {"utc": "2026-09-25T17:25:59Z", "pr": 1818},
    # PR #1796 (merged 2026-09-24T16:18:48Z): the state view, two tabs.
    # First run on it: GUI-15-census-career-contracts 2026-09-24_1522.
    "career": {"utc": "2026-09-24T15:22:01Z", "pr": 1796},
}


# --------------------------------------------------------------------------
# pure helpers
# --------------------------------------------------------------------------

def norm(text):
    """Lowercase alphanumeric squash, the join key for every token match."""
    return re.sub(r"[^a-z0-9]+", "", (text or "").lower())


def esc(text):
    """HTML-escape for the page's own chrome - the one header line that is built
    as markup rather than set as a DOM text node.

    It is NOT what protects the control strings: those travel in the inlined JSON
    (guarded by `json_for_script`) and are written with `textContent`, or, for the
    rich-text subset, through a whitelist that never touches `innerHTML`.
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
    r"uiaction (?P<verb>open|close|tab|rect|complexity|dialog|describe|playback"
    r"|pointer)\b(?P<tail>[^\r\n]*)")
_LOG_SHOT = re.compile(r"capturescreenshot ok label=(?P<label>[A-Za-z0-9_.-]+)")
_KV = re.compile(r"(\w+)=([^\s]+)")

# The seam's op names, read out of the log grammar above rather than retyped.
# They are AUTOMATION vocabulary, not window text - which matters because one of
# them ("close") is also a button label, and the mirror's no-typed-UI-text guard
# has to be able to tell the two apart.
SEAM_VERBS = tuple(v for v in re.split(
    r"[|)]", _LOG_LINE.pattern.split("(?P<verb>")[1])
    if re.match(r"^[a-z]+$", v))
VERB_OPEN, VERB_CLOSE, VERB_TAB, VERB_RECT = SEAM_VERBS[0:4]
VERB_COMPLEXITY, VERB_DIALOG, VERB_DESCRIBE = SEAM_VERBS[4:7]
# `playback` is at 7 and is not read here; `pointer` is what a hover capture's
# own outcome is read from.
VERB_POINTER = SEAM_VERBS[8]


def _kv(tail):
    return dict(_KV.findall(tail or ""))


def parse_ksp_log(text):
    """Replay a shots-dir KSP.log into per-capture seam state.

    Returns ``{label: {...}}`` with the window that was open besides the main
    one, its rect, its selected tab, the complexity mode, the whole open set, the
    standing dialog (title plus button labels) if any, and the last pointer op's
    own outcome. The seam prints these itself, so this is the game's own account
    of each frame rather than a guess off the label.
    """
    state = {
        "scene": None,
        "mode": None,
        "open": [],
        "rects": {},
        "tabs": {},
        "dialog": None,
        "pointer": None,
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
                "pointer": dict(state["pointer"]) if state["pointer"] else None,
            }
            # A pointer op sets up ONE frame: its reported tooltip is what
            # `GUI.tooltip` held when the next screenshot was taken, and
            # carrying it forward would claim the same outcome for every later
            # capture of the run.
            state["pointer"] = None
            continue
        m = _LOG_LINE.search(raw)
        if not m:
            continue
        verb, tail = m.group("verb"), m.group("tail")
        kv = _kv(tail)
        if verb == VERB_COMPLEXITY and "mode" in kv:
            state["mode"] = kv["mode"]
        elif verb == VERB_OPEN and "window" in kv and "initiated" not in tail:
            w = kv["window"]
            windows_seen[w] = True
            if w not in state["open"]:
                state["open"].append(w)
        elif verb == VERB_CLOSE and "window" in kv:
            w = kv["window"]
            if w in state["open"]:
                state["open"].remove(w)
        elif verb == VERB_RECT and "window" in kv and "rect" in kv:
            try:
                state["rects"][kv["window"]] = [int(float(v)) for v in kv["rect"].split(",")][:4]
            except ValueError:
                pass
        elif verb == VERB_TAB and "window" in kv and "tab" in kv:
            state["tabs"][kv["window"]] = kv["tab"]
            try:
                idx = int(kv.get("index", "0"))
            except ValueError:
                idx = 0
            tabs_seen[kv["window"]].setdefault(kv["tab"], idx)
        elif verb == VERB_DESCRIBE:
            if "scene" in kv:
                state["scene"] = kv["scene"]
            if "complexity" in kv:
                state["mode"] = kv["complexity"]
            names = kv.get("openWindows", "")
            if names and names != "-":
                state["open"] = [n for n in names.split(",") if n]
        elif verb == VERB_POINTER and "at=" in tail:
            # The pointer op logs several lines per step; the one that carries
            # `at=` is its RESULT. Only that line is read, and `tooltip=` is cut
            # out by position because a populated tooltip carries spaces - the
            # same reason the dialog's title is.
            tip = None
            if "tooltip=" in tail:
                tip = _between(tail, "tooltip=", " tooltipFrame=")
                if tip is None:
                    tip = _between(tail, "tooltip=", None)
            state["pointer"] = {
                "at": kv.get("at", ""),
                # A PARK moves the cursor to 0,0 to get it out of the frame, so
                # it is the opposite of a hover: the capture after it was never
                # asked to show one.
                "park": kv.get("park") == "true",
                # None means the log does not SAY: the key postdates the older
                # census runs, and an absent statement is not an empty tooltip.
                "tooltip": tip,
            }
        elif verb == VERB_DIALOG:
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


def mock_provenance(dump):
    """The dump's own `mock` block, or None for an ordinary real-save capture.

    ADDITIVE at the unchanged `parsek-gui-tree/1` schema id
    (`docs/dev/design-gui-tree-dump.md`), so a dump written before the block
    existed - which is every committed census artifact - reads exactly as it did.
    ABSENT means real, which is the reader's rule and the common case.

    It has to be the DUMP rather than the label, because a gallery lane HAS a
    `fixture.saveTemplate` (it needs a loaded game) and the mirror derives a
    capture's dataset from that: without this block a mocked capture would file
    under a real fixture's name and pair against real captures in Compare.
    """
    block = (dump or {}).get("mock")
    if not isinstance(block, dict):
        return None
    state_id = str(block.get("stateId") or "").strip()
    if not state_id:
        return None
    try:
        states = int(block.get("states") or 0)
    except (TypeError, ValueError):
        states = 0
    covers = block.get("covers")
    return {
        "stateId": state_id,
        "window": str(block.get("window") or "").strip(),
        "catalogue": str(block.get("catalogue") or "").strip(),
        "states": states,
        "covers": [str(c) for c in (covers if isinstance(covers, list) else ()) if c],
    }


def mock_facets(prov, tab_tokens=()):
    """A mocked capture's (window, tab, state), derived from the catalogue state
    id rather than from its label.

    The id is `<window>.<family>.<variant>`, and the label a gallery lane writes
    is that id with the window prefix dropped and the dots turned into dashes
    (`design-gui-state-gallery.md` 7.6). Reading the facets back out of the id
    means the filing does not depend on a filename the label grammar has to
    re-split, and the `window` field of the block is the authority for the window
    token. The tab is taken only when the leading tail token is in that window's
    own tab vocabulary, which is the same rule `parse_label` applies.
    """
    prov = prov or {}
    parts = [p for p in str(prov.get("stateId") or "").split(MOCK_STATE_SEP) if p]
    window = prov.get("window") or (parts[0] if parts else "")
    tail = parts[1:] if (parts and parts[0] == window) else list(parts)
    tab = None
    if tail and tail[0] in (tab_tokens or ()):
        tab = tail[0]
        tail = tail[1:]
    return window, tab, "-".join(tail)


def label_log_disagreements(lab, log, window_tabs, tab_alias=None):
    """Where the LABEL's own reading of a capture and the seam log's disagree.

    The log wins - it is what was on screen, and the label is a filename someone
    typed into a spec - so this changes no filing. It reports, so a reader of the
    rail knows the row he is looking at is filed under something its name denies,
    which is exactly the case three of the corpus's captures are in.

    `window_tabs` is that window's `{token: index}` out of the logs. The third
    arm is the one that catches the quiet form: a label that names NO tab reads
    as the window's default (index 0), so a log that selected a later tab
    contradicts it just as plainly as a different token would. A label whose
    leading state token turned out to be a tab's DISPLAY name is not a
    disagreement at all, which is what `tab_alias` suppresses.
    """
    out = []
    log_window = (log or {}).get("window")
    log_tab = (log or {}).get("tab")
    tabs = window_tabs or {}
    if lab.get("window") and log_window and lab["window"] != log_window:
        out.append({"field": "window", "label": lab["window"], "log": log_window})
    if lab.get("tab") and log_tab and lab["tab"] != log_tab:
        out.append({"field": "tab", "label": lab["tab"], "log": log_tab})
    elif (not lab.get("tab") and not tab_alias and log_tab
            and tabs.get(log_tab, 0) > 0):
        out.append({"field": "tab", "label": "", "log": log_tab})
    head = (lab.get("state") or "").split("-")[0]
    if head and head in tabs and log_tab and head != log_tab:
        out.append({"field": "state", "label": head, "log": log_tab})
    return out


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


def key_of(fixture, window, tab, state, mode, scene="", mock_state=""):
    """The identity a before/after pair is formed on.

    `scene` is part of it: the same window drawn at the Space Center and in
    flight is two different pictures, not a change, and pairing them would
    report every scene difference as a regression.

    `mock_state` is the catalogue state id, and it is APPENDED only when there is
    one, so every real capture's key stays byte-identical to what it was before
    the gallery existed. Mocked captures already sit in their own `fixture`, so
    this segment is not what isolates them - it is what keeps two DIFFERENT
    states of one window from pairing with each other when their derived state
    tails happen to agree.
    """
    parts = [fixture or "", window or "", tab or "", state or "",
             mode or "", scene or ""]
    if mock_state:
        parts.append(mock_state)
    return "|".join(parts)


def default_fixture(captures, fixture_order, scene=SCENE_SPACECENTER):
    """The dataset the page OPENS on, decided here and PINNED into the model.

    It used to be derived in the page, which was fine while every dataset was a
    real save: the fixture that photographed the most DIFFERENT windows at the
    Space Center is the census's general-purpose host, where "most captures"
    would only pick whichever lane ran longest. A ~300-state mocked gallery wins
    that contest outright, and would silently become the page the owner opens -
    so the mocked dataset is excluded from the contest here and offered as an
    explicit choice instead (`design-gui-state-gallery.md` 7.6).

    Deterministic: breadth first, then the declared fixture order, then the name.
    """
    real = [c for c in captures if not c.get("mocked")]
    for pool in ([c for c in real if (c.get("scene") or "") == scene], real):
        breadth = defaultdict(set)
        for cap in pool:
            breadth[cap["fixture"]].add(cap["window"])
        if breadth:
            return sorted(breadth, key=lambda f: (
                -len(breadth[f]),
                fixture_order.index(f) if f in fixture_order else len(fixture_order),
                f))[0]
    return fixture_order[0] if fixture_order else ""


# --------------------------------------------------------------------------
# the owner's notes: the blob that leaves the page and comes back
# --------------------------------------------------------------------------

def note_key(window, tab, state, mode, fixture, run_pair=""):
    """The identity a note is stored under.

    `(run pair, window, tab, state, mode, fixture)`. The run pair is what makes a
    Compare note about THIS before/after pair rather than about the window in
    general: re-fly the lane and the pair changes, so last round's verdict does
    not silently attach itself to a picture the owner has not seen. A note taken
    on a single state view carries that capture's own run id as both halves.

    The JOINED ORDER is the contract - the page's own `noteKey` takes the run
    pair FIRST because every one of its callers has one, and this one takes it
    last with a default because the import path reads rows that carry none.
    """
    return "|".join([run_pair or "", window or "", tab or "", state or "",
                     mode or "", fixture or ""])


def notes_blob(rows, stamp="", scope="", page_schema=MIRROR_SCHEMA):
    """The export payload: every non-empty note, with enough beside it to act on
    without the page.

    The point of the extra fields is that the blob is read by an agent in a fresh
    session. A verdict with no capture ids, no window and no mocked flag is a
    sentence about nothing; with them it is a task. `generatedUtc` is the PAGE's
    own generation stamp, not the export time, because that is what says which
    corpus the owner was looking at.
    """
    out = []
    for row in rows or ():
        rec = OrderedDict()
        for field in NOTES_FIELDS:
            val = row.get(field)
            if field == "mocked":
                rec[field] = bool(val)
            else:
                rec[field] = "" if val is None else str(val)
        out.append(rec)
    return OrderedDict([
        ("schema", NOTES_SCHEMA),
        ("pageSchema", page_schema),
        ("generatedUtc", stamp or ""),
        ("scope", scope or ""),
        ("count", len(out)),
        ("notes", out),
    ])


def notes_markdown(blob):
    """The same rows as a markdown table, for pasting somewhere that renders one.

    Two forms of one payload rather than a choice: the owner pastes whichever his
    next destination reads, and the JSON is the one an agent parses.
    """
    blob = blob or {}
    head = ["window", "tab", "state", "mode", "fixture", "mocked", "verdict", "note"]
    lines = ["| " + " | ".join(head) + " |",
             "| " + " | ".join("---" for _ in head) + " |"]
    for row in blob.get("notes") or ():
        cells = []
        for field in head:
            val = row.get(field)
            if field == "mocked":
                val = "yes" if val else ""
            cells.append(str("" if val is None else val).replace("|", "/")
                         .replace("\n", " ").strip() or "-")
        lines.append("| " + " | ".join(cells) + " |")
    return "\n".join(lines)


def parse_notes_blob(text):
    """A pasted blob back into `{key: row}`, tolerantly.

    Tolerant on purpose: the owner pastes out of a browser and into a chat box,
    and the thing that comes back may be the whole blob, a bare list of rows, or
    one row. Anything with no usable key is DROPPED rather than merged under a
    made-up one, and the count of what was dropped is returned so the page can
    say so instead of quietly losing a verdict.
    """
    try:
        data = json.loads(text) if isinstance(text, str) else text
    except (TypeError, ValueError):
        return {}, 0, "not JSON"
    rows = None
    schema = ""
    if isinstance(data, dict) and isinstance(data.get("notes"), list):
        rows, schema = data["notes"], str(data.get("schema") or "")
    elif isinstance(data, list):
        rows = data
    elif isinstance(data, dict) and data.get("key"):
        rows = [data]
    if rows is None:
        return {}, 0, "no notes in it"
    out = OrderedDict()
    dropped = 0
    for row in rows:
        if not isinstance(row, dict):
            dropped += 1
            continue
        key = str(row.get("key") or "").strip()
        if not key:
            key = note_key(row.get("window"), row.get("tab"), row.get("state"),
                           row.get("mode"), row.get("fixture"),
                           row.get("runPair") or "")
            if key.strip("|") == "":
                dropped += 1
                continue
        rec = {}
        for field in NOTES_FIELDS:
            if field in row:
                rec[field] = bool(row[field]) if field == "mocked" else row[field]
        rec["key"] = key
        out[key] = rec
    return out, dropped, schema


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


def sample_colors(w, h, bpp, px, rect, step=2, exclude=()):
    """Background and foreground colour actually drawn on a control's OWN surface.

    `exclude` carries the node's children in the same screen coordinates. Only
    points inside `rect` and outside every child are probed, because a container's
    colour is the colour of its own padding and gutters - the pixels its children
    do not cover. Probing the whole rect made a window take the colour of whatever
    opaque child happened to sit under the probe point: the Kerbals window of
    `ksc-kerbals-outcomes-advanced` has a 404 px content box over its centre, so
    the whole window painted in that box's `#292929` while every other Kerbals
    capture painted `#444444`. The same mechanism waits for any container whose
    sample point falls inside an opaque child.

    Background is the MEDIAN of the uncovered points by luminance (a mode can be
    swung by one thin border run); foreground is the mean of the points furthest
    from it, which on a flat skin is exactly the glyph ink - and which for a
    container legitimately comes back empty, because its margins carry no text.

    When the children cover the rect completely there is no own surface to read,
    so it falls back to the whole rect: a wrong-but-consistent colour beats None,
    and the fallback is what the old behaviour always did.
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

    boxes = []
    for c in exclude or ():
        cx, cy, cw, ch = [int(v) for v in c[:4]]
        if cw > 0 and ch > 0:
            boxes.append((cx, cy, cx + cw, cy + ch))

    def covered(x, y):
        for bx0, by0, bx1, by1 in boxes:
            if bx0 <= x < bx1 and by0 <= y < by1:
                return True
        return False

    pixels = _probe(w, bpp, px, x0, y0, x1, y1, step, covered if boxes else None)
    if not pixels:
        # fully covered: no own surface to read
        pixels = _probe(w, bpp, px, x0, y0, x1, y1, step, None)
    if not pixels:
        return None, None

    ordered = sorted(pixels, key=_lum)
    bg = ordered[len(ordered) // 2]
    bl = _lum(bg)
    scored = sorted(pixels, key=lambda q: -abs(_lum(q) - bl))
    take = scored[:max(1, len(scored) // 20)]
    # Ink is the extreme tail; averaging the whole tail would drag it back
    # towards the anti-aliased edge pixels, so only the strongest fifth counts.
    strong = take[:max(1, len(take) // 5)]
    fg = tuple(sum(q[i] for q in strong) // len(strong) for i in range(3))
    if abs(_lum(fg) - bl) < 12:
        fg = None
    return _hex(bg), (_hex(fg) if fg else None)


def _probe(w, bpp, px, x0, y0, x1, y1, step, covered):
    out = []
    for y in range(y0, y1, step):
        rowbase = y * w * bpp
        for x in range(x0, x1, step):
            if covered is not None and covered(x, y):
                continue
            o = rowbase + x * bpp
            out.append((px[o], px[o + 1], px[o + 2]))
    return out


def _lum(rgb):
    return 0.299 * rgb[0] + 0.587 * rgb[1] + 0.114 * rgb[2]


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


def text_ink_offset(w, h, bpp, px, rect, inset=3, cut=0.45):
    """Where a control's text actually STARTS inside its own rect, measured off
    the frame, as an offset in pixels from the rect's left edge.

    For the same reason the tab bar's labels are measured rather than assumed:
    the product is not uniform about alignment and the dump records none of it.
    KSP's `box` style CENTRES the Logistics section heading ("Active Routes" sits
    at x=649 in a 1358 px box) and LEFT-ALIGNS the sortable column headers of the
    same table ("Origin" at the left edge of its 95 px cell). The page, which
    left-aligned the first and centred the second, was 643 px out on one and
    roughly a cell wide on the other.

    The middle of the rect only, inset from every edge, so a box's own border is
    never mistaken for its first glyph.

    The three numbers, each measured rather than chosen. `inset=3`: KSP's box
    style draws a one-pixel outline with a one-pixel inner shadow, and the
    Logistics header cells put their first glyph 4 to 5 px in (measured: "#" at
    4, "Actions" at 4, "Origin" at 5, "Interval" at 5) - so 3 clears the frame
    and still sits before the earliest glyph. `cut=0.45`: a fraction of the
    rect's OWN contrast range rather than an absolute level, because the same
    style is drawn on #444444, #292929 and #313131 across the corpus; at 0.45
    the anti-aliased left edge of a glyph counts and the shadow does not.
    `spread < 20` is the "nothing here to read" floor - an empty box varies by
    2 or 3 across its own surface, and the weakest real run in the corpus (the
    dimmed Missions interval field) spreads 30.

    Returns None when there is no ink to measure, and the page then keeps its CSS
    alignment.
    """
    x, y, rw, rh = [int(v) for v in rect]
    if rw <= 2 * inset or rh <= 2 * inset:
        return None
    x0, x1 = max(0, x + inset), min(w, x + rw - inset)
    y0, y1 = max(0, y + inset), min(h, y + rh - inset)
    if x1 <= x0 or y1 <= y0:
        return None
    vals = []
    for yy in range(y0, y1):
        base = yy * w * bpp
        for xx in range(x0, x1):
            o = base + xx * bpp
            vals.append(_lum((px[o], px[o + 1], px[o + 2])))
    if not vals:
        return None
    ordered = sorted(vals)
    med = ordered[len(ordered) // 2]
    spread = max(abs(ordered[0] - med), abs(ordered[-1] - med))
    if spread < 20:
        return None
    thr = spread * cut
    for xx in range(x0, x1):
        for yy in range(y0, y1):
            o = (yy * w + xx) * bpp
            if abs(_lum((px[o], px[o + 1], px[o + 2])) - med) > thr:
                return xx - x
    return None


def slider_thumb_run(w, h, bpp, px, rect, min_contrast=12):
    """Where a slider's or scrollbar's THUMB sits, measured off the frame inside
    the control's own rect.

    The dump records a slider's rect and no value, so the page had nothing to
    position a knob from and drew a bare groove - a Settings row that reads as an
    empty channel where the game shows a handle at about 70%, and a scroll bar
    with no bar in it. But the thumb IS in the pixels.

    What is bright is the thumb's BEVEL, not its face, and the difference
    matters. Measured across the `ib-structure-mission-advanced` vertical scroll
    bar: the groove is a flat luminance 45, and the thumb's own columns read
    14, 0, 101, 85, 68, 50, 17, 17, 17, 17, 34, 50, 50, 0, 14 - a one-pixel
    highlight at 101/85 over a FACE of 17 to 50, which is DARKER than the groove
    it sits in. So this profiles the MAXIMUM along each step of the control's
    long axis, where the bevel shows, and the longest run above two thirds of the
    control's own range is the thumb. Taking "lighter than its groove" literally
    and colouring the face light is exactly the mistake that made the page's
    handle read at luminance 185 against the game's 17.

    Returns `(start, length, vertical)` in the control's own axis - `start`
    relative to the rect - or None when the rect carries no contrast to read
    (a groove whose thumb fills it, or a control drawn flat). The page then
    keeps the bare groove, which is the honest answer for "not measurable here".
    """
    x, y, rw, rh = [int(v) for v in rect]
    if rw <= 0 or rh <= 0:
        return None
    vertical = rh > rw
    span = rh if vertical else rw
    if span < 6:
        return None
    prof = []
    if vertical:
        for yy in range(max(0, y), min(h, y + rh)):
            top = 0
            for xx in range(max(0, x), min(w, x + rw)):
                o = (yy * w + xx) * bpp
                top = max(top, _lum((px[o], px[o + 1], px[o + 2])))
            prof.append(top)
    else:
        for xx in range(max(0, x), min(w, x + rw)):
            top = 0
            for yy in range(max(0, y), min(h, y + rh)):
                o = (yy * w + xx) * bpp
                top = max(top, _lum((px[o], px[o + 1], px[o + 2])))
            prof.append(top)
    if len(prof) < 6:
        return None
    lo, hi = min(prof), max(prof)
    if hi - lo < min_contrast:
        return None
    # `min_contrast` of 12: the smallest real separation in the corpus is the
    # Settings horizontal slider, whose groove profiles at 36-40 against a handle
    # at 54 - a range of about 18. A flat groove (no thumb to find, or a thumb
    # that fills it) profiles within 2 or 3 of itself, so 12 sits clear of the
    # noise and below every genuine thumb measured.
    #
    # Two thirds of the way up the control's OWN range, not an absolute level:
    # the bevel tops out at 101 over a 45 groove on a scroll bar and at 54 over
    # a 36 groove on the Settings slider, and no single threshold separates both.
    cut = lo + (hi - lo) * 2.0 / 3.0
    best = None
    start = None
    for i, v in enumerate(prof):
        if v >= cut and start is None:
            start = i
        elif v < cut and start is not None:
            if best is None or i - start > best[1]:
                best = (start, i - start)
            start = None
    if start is not None and (best is None or len(prof) - start > best[1]):
        best = (start, len(prof) - start)
    if not best or best[1] <= 0:
        return None
    return (best[0], best[1], vertical)


def _hex(rgb):
    return "#%02x%02x%02x" % tuple(rgb)


# --------------------------------------------------------------------------
# tree flattening
# --------------------------------------------------------------------------

# The kinds the page draws a background for. Sampling and storing one for a kind
# whose CSS never paints it (a layout group, a bare label, a scroll view) only
# made the payload bigger - 385 dead values on a 35-capture corpus.
BG_KINDS = ("window", "box", "button", "repeatbutton", "buttongrid", "textfield")
# The kinds that take a click. ONE source: emitted into the page's JS and into
# its CSS, so the two can never disagree about what looks clickable.
CLICK_KINDS = ("button", "repeatbutton", "toggle", "buttongrid", "slider", "textfield")


# The bottom chrome of an auto-fitted window: the gap between its last child's
# bottom edge and its own border. Measured on ksc-main-advanced (2026-09-11_0548):
# the seam's applied rect was 300 tall while the Close row ended 286 px in, and the
# frame's border sits 14 px under Close in Basic mode as well.
WINDOW_BOTTOM_PAD = 14


def root_height(root, log_rects):
    """A GUILayout window reports h=0 in the dump because its host zeroes the
    height every frame so IMGUI re-fits it to the content (`ParsekKSC.cs`,
    `ParsekFlight.cs`). So the CONTENT decides: the child extent plus the bottom
    chrome. The seam's own `rect` line is the height the window had when the
    seam applied it, which goes stale the moment the content shrinks - the Basic
    mode main window drew 74 px of empty panel under Close from it - so it is
    only a fallback for a window that drew no children at all."""
    rect = list(root.get("rect") or [0, 0, 0, 0])
    if rect[3] > 0:
        return rect[3]
    applied = 0
    for _w, r in (log_rects or {}).items():
        if len(r) == 4 and r[0] == rect[0] and r[1] == rect[1] and r[2] == rect[2] and r[3] > 0:
            applied = r[3]
            break
    if not root.get("children") and applied:
        return applied
    bottom = rect[1]

    def walk(node):
        nonlocal bottom
        nr = node.get("rect") or [0, 0, 0, 0]
        bottom = max(bottom, nr[1] + nr[3])
        for ch in node.get("children") or ():
            walk(ch)

    walk(root)
    return max(1, bottom - rect[1] + WINDOW_BOTTOM_PAD)


# The dump's `fontStyle` wire names (GuiTreeAssembler.FontStyleWireName) -> the
# page's one-letter codes. `n` is kept: a style that UN-bolds a bold skin style says
# so, and the page must then draw it at normal weight.
FONT_STYLE_CODES = {"normal": "n", "bold": "b", "italic": "i", "boldItalic": "bi"}


def compact_font(node):
    """The node's font DELTA as page keys: `fs` (pixels) and `fw` (a style code).

    The recorder writes `fontSize` / `fontStyle` only when the drawing style's font
    departs from the skin's style of the same name, so an absent key means "the
    skin default the page already draws". Without this a `new GUIStyle(label)` copy
    at 10 px - the main window's version footer - was drawn at the page's 13 px
    label size and clipped inside the 35 px rect the game laid it out in. Pixels
    map 1:1: the footer's run fits its rect at 10 px Arial, and at the scaled
    13/12 it would not."""
    out = {}
    size = node.get("fontSize")
    if isinstance(size, int) and not isinstance(size, bool) and size > 0:
        out["fs"] = size
    code = FONT_STYLE_CODES.get(node.get("fontStyle"))
    if code:
        out["fw"] = code
    return out


def compact_tree(node, parent_rect, sampler=None, parent_bg=None,
                 grid_runs=None, thumb_runs=None, text_offsets=None):
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
    font = compact_font(node)
    if font:
        out.update(font)
    sel = node.get("selectedIndex")
    if (out["k"] == "buttongrid" and isinstance(sel, int)
            and not isinstance(sel, bool) and sel >= 0):
        # The grid's OWN reading of which cell is pushed in. Preferred over the
        # seam's tab token, which only covers the windows the seam names as
        # tabbed - and only while its `tab=` line and the dump agree.
        out["si"] = sel
    if out["k"] == "buttongrid" and grid_runs is not None:
        runs = grid_runs(rect)
        if runs:
            # Stored relative to the grid, so the page places each label where the
            # game drew it instead of centring them all.
            out["gi"] = [[r[0] - rect[0], r[1] - r[0] + 1] for r in runs]
            if sampler is not None and len(runs) > 1:
                # One background per cell, read off the frame. KSP's selected tab
                # is the DARK pushed-in one and the unselected ones carry the
                # light top edge - the opposite of what a "selected is brighter"
                # guess produces, which is why this is measured and not chosen.
                seg = float(rect[2]) / len(runs)
                cells = []
                for i in range(len(runs)):
                    cbg, _cfg = sampler([int(rect[0] + i * seg), rect[1],
                                         max(1, int(seg)), rect[3]], ())
                    cells.append(cbg)
                if all(cells):
                    out["gc"] = cells
    if (style == "box" and text and text_offsets is not None
            and out["k"] in ("label", "button")):
        # Only the box style: the ordinary label and button alignments agree with
        # the frame to a pixel or three, and storing an offset for all 71 000
        # labels would be payload for nothing.
        off = text_offsets(rect)
        if off is not None:
            out["tx"] = off
    thumb_rect = None
    if out["k"] == "slider" and thumb_runs is not None:
        run = thumb_runs(rect)
        if run:
            # Start and length along the control's own long axis, plus which axis
            # that is. Read off THIS frame, so the page draws the handle where
            # the game drew it rather than at a position nothing recorded.
            out["th"] = [run[0], run[1]]
            if sampler is not None:
                # And its COLOURS, off the same frame, by the same rule as every
                # other surface on this page. A typed colour was wrong by 168
                # luminance: KSP's scroll bar thumb has a dark face (17 to 50)
                # under a one-pixel bevel (85 to 101), and the page drew the
                # whole handle at #b9b9b9. `sample_colors` answers with exactly
                # the two the thumb has - the median of its own surface, and the
                # extreme tail, which IS the bevel.
                trect = ([rect[0], rect[1] + run[0], rect[2], run[1]]
                         if run[2] else
                         [rect[0] + run[0], rect[1], run[1], rect[3]])
                thumb_rect = trect
                tbg, tfg = sampler(trect, ())
                if tbg:
                    out["tc"] = tbg
                if tfg:
                    out["te"] = tfg
        out["vt"] = 1 if rect[3] > rect[2] else 0
    if node.get("horizontal") is not None:
        out["hz"] = 1 if node["horizontal"] else 0
    bg = None
    if sampler is not None and out["w"] > 0 and out["h"] > 0:
        kid_rects = [c.get("rect") or [0, 0, 0, 0]
                     for c in (node.get("children") or ())]
        if thumb_rect is not None:
            # The groove is the slider's surface MINUS its thumb, the same rule
            # a container's colour follows. Sampling the whole rect took the
            # median of a scroll bar that is more thumb than groove, so the page
            # painted the groove in the thumb's own colour - and then drawing the
            # thumb on top of it changed nothing a measurement could see.
            kid_rects = list(kid_rects) + [thumb_rect]
        bg, fg = sampler(rect, kid_rects)
        # A background identical to the parent's is what CSS already inherits,
        # so storing it again would only make the page bigger.
        # A toggle in the BUTTON style is painted like a button, so its fill is
        # worth storing; the 7000 checkbox-styled ones draw no surface of their
        # own and storing a colour for them was only payload.
        paints = (out["k"] in BG_KINDS or style == "box"
                  or (out["k"] == "toggle" and style == "button")
                  # A slider's GROOVE is a surface like any other, and the page
                  # was drawing a 3 px line at #666 (luminance 102) where KSP
                  # fills the whole 15 px width at 45. Measured on the
                  # ib-structure-mission scroll bar.
                  or out["k"] == "slider")
        if bg and bg != parent_bg and paints:
            out["bg"] = bg
        if fg and (text or out["k"] in ("box", "toggle")):
            out["fg"] = fg
    kids = [compact_tree(ch, rect, sampler, bg or parent_bg, grid_runs,
                         thumb_runs, text_offsets)
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


def _first_grid_index(roots):
    """The pushed-in cell of the first selection grid, as the grid itself
    recorded it (`si`), or None."""
    stack = list(roots)
    while stack:
        node = stack.pop(0)
        if node.get("k") == "buttongrid" and isinstance(node.get("si"), int):
            return node["si"]
        stack.extend(node.get("c") or ())
    return None


MODE_RANK = {"basic": 0, "advanced": 1}


def tab_order(tabs_by_window, captures):
    """Per window, tab token -> its position on the tab bar.

    The seam's `uiaction tab ... index=N` line is the grid index it clicked; where
    a tab was reached some other way, the window's own grid says which cell was
    pushed in (`si`) in the capture filed under that tab. The seam wins where
    both speak, since it is the one that named the token.
    """
    out = defaultdict(dict)
    for w, toks in tabs_by_window.items():
        for tok, idx in toks.items():
            out[w][tok] = idx
    for cap in captures:
        if cap["tab"] and isinstance(cap.get("gridIndex"), int):
            out[cap["window"]].setdefault(cap["tab"], cap["gridIndex"])
    return out


def rail_group(cap, order):
    """(sort key, tab token) of the rail group a capture is listed under.

    The capture's own tab where it has one. A capture of a tabbed window taken
    with no tab selected by the seam is grouped by the cell its grid shows, and
    one with neither leads the list: that is the window as it opens.
    """
    tab = cap["tab"]
    if not tab and isinstance(cap.get("gridIndex"), int):
        for tok, idx in order.items():
            if idx == cap["gridIndex"]:
                tab = tok
                break
    if not tab:
        return (0, -1, ""), ""
    if tab in order:
        return (1, order[tab], tab), tab
    return (2, 0, tab), tab


def is_stale(cap):
    """Not the current picture of its state: a hover the log says photographed
    nothing, a capture a later run of the same key replaced, or one its own lane
    no longer produces."""
    return bool(cap.get("hoverEmpty") or cap.get("supersededBy")
                or cap.get("retired") or cap.get("outdated"))


def rail_rows(captures, order_by_window):
    """Each window's rail, in reading order.

    One row per (tab, state, mode), the row being the current capture of that
    state where there is one (model order among equals, which is what the rail
    listed before it was ordered). Rows are grouped by tab, tabs in the window's
    own tab-bar order, and within a tab run from the least drawn to the most -
    the node count of the window's own tree - so reading down a tab shows the
    window filling in. Basic before Advanced on a tie, then the label, so the
    order is total and a regeneration cannot shuffle it.
    """
    firsts = OrderedDict()
    for pos, cap in sorted(enumerate(captures),
                           key=lambda pc: (is_stale(pc[1]), pc[0])):
        k = (cap["window"], cap["tab"] or "", cap["state"] or "", cap["mode"] or "")
        firsts.setdefault(k, cap)
    per_window = defaultdict(list)
    for (win, _t, _s, _m), cap in firsts.items():
        per_window[win].append(cap)
    out = {}
    for win, caps in per_window.items():
        order = order_by_window.get(win) or {}
        rows = []
        for cap in caps:
            gkey, gtok = rail_group(cap, order)
            rows.append((gkey, cap.get("complexity") or 0,
                         MODE_RANK.get(cap["mode"] or "", 2), cap["label"],
                         cap["id"], gtok))
        rows.sort(key=lambda r: r[:5])
        out[win] = [{"id": r[4], "g": r[5]} for r in rows]
    return out


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


def window_compare_summary(captures, keys, window, missing=()):
    """One window's Compare header, in counts.

    Two things it deliberately does NOT count. Coverage is DISTINCT KEYS, not
    files: the corpus has 228 PNGs behind 134 distinct labels, so a file count
    reads as about 1.7x the coverage there is. And a capture the log says
    photographed no hover is left out of the state counts entirely, because a
    frame that is text-identical to its own baseline is not a state.

    NEW and GONE are read off SPEC RE-FLIGHTS, which is the only place the corpus
    has a before and an after of the same intent: for each spec that photographed
    this window more than once, the keys its newest run has and its oldest run
    does not are new, and the reverse are gone. A spec that flew once contributes
    neither, because one flight cannot say a state disappeared.
    """
    by_id = {c["id"]: c for c in captures}
    caps = [c for c in captures if c["window"] == window]
    live = [c for c in caps if not c.get("hoverEmpty")]
    # A key whose latest capture its own lane no longer produces is not a state
    # the window has any more, so it is left out of the state counts too.
    win_keys = {k: v for k, v in keys.items()
                if (by_id.get(v["after"]) or {}).get("window") == window
                and not v.get("retired") and not v.get("outdated")}
    real_keys = {k for k, v in win_keys.items()
                 if not (by_id.get(v["after"]) or {}).get("mocked")}
    mock_keys = set(win_keys) - real_keys
    changed = sum(1 for v in win_keys.values() if v["changed"])

    # spec -> run -> earliest capturedUtc in that run
    runs_by_spec = defaultdict(dict)
    keys_by_run = defaultdict(set)
    for cap in live:
        seen = runs_by_spec[cap["specId"]].get(cap["runId"])
        if seen is None or cap["capturedUtc"] < seen:
            runs_by_spec[cap["specId"]][cap["runId"]] = cap["capturedUtc"]
        keys_by_run[(cap["specId"], cap["runId"])].add(cap.get("key") or "")
    fresh = gone = reflown = 0
    for spec, runs in runs_by_spec.items():
        if len(runs) < 2:
            continue
        reflown += 1
        order = sorted(runs, key=lambda r: (runs[r], r))
        first, last = keys_by_run[(spec, order[0])], keys_by_run[(spec, order[-1])]
        fresh += len(last - first)
        gone += len(first - last)
    return {
        "window": window,
        "captures": len(caps),
        "capturesReal": len([c for c in caps if not c.get("mocked")]),
        "capturesMocked": len([c for c in caps if c.get("mocked")]),
        "superseded": len([c for c in caps if c.get("supersededBy")]),
        "retired": len([c for c in caps if c.get("retired")]),
        "outdated": len([c for c in caps if c.get("outdated")]),
        "hoverNotCaptured": len([c for c in caps if c.get("hoverEmpty")]),
        "labelDisagreements": len([c for c in caps if c.get("disagrees")]),
        "statesReal": len(real_keys),
        "statesMocked": len(mock_keys),
        "changed": changed,
        "unchanged": len(win_keys) - changed,
        "new": fresh,
        "gone": gone,
        "reflownSpecs": reflown,
        "uncaptured": [m for m in (missing or ()) if m.get("window") == window],
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
    `Fix:` line.

    Two shapes the file really uses and the first version of this missed: the id
    sits on a `## ` heading as often as on a bullet, and the `Fix:` line is
    written `**Fix:**` and usually FOLLOWS a bullet list inside the entry. Ending
    an entry at the first bullet therefore truncated every one of them before its
    Fix line - 52 entries parsed, every `fix` empty. An entry now ends only at the
    next heading or the next `GUI-` id.
    """
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
            if ln.startswith("#"):
                out.append(cur)
                cur = None
            elif ln.strip():
                cur["text"] += " " + ln.strip()
    if cur:
        out.append(cur)
    for e in out:
        e["text"] = re.sub(r"\s+", " ", e["text"])
        # `**Fix:**`, `**Fix (shipped).**`, `**Fix (proposed, not applied).**`
        # and a bare `Fix:` all occur. The bolded forms close with their own
        # `**`, so the emphasis run is consumed rather than leaked into the text.
        m = re.search(r"\*\*Fix\b[^*]{0,80}\*\*\s*(.+)$", e["text"])
        if m is None:
            m = re.search(r"\bFix:\s*(.+)$", e["text"])
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


def title_prefix(titles):
    """The leading words every window title shares - the product's own name.

    Derived, not typed. Titles read "Parsek", "Parsek - Logistics",
    "Parsek - Real Spawn Control": the longest run of leading words common to all
    of them is the head the page must strip before it can compare a control's
    label to a window's name.

    Returns "" unless at least two distinct titles agree on a prefix AND at least
    one title is longer than it - a single title would otherwise "share" the
    whole of itself and strip every window's name to nothing.
    """
    words = []
    for title in titles:
        parts = [p for p in re.split(r"[\s-]+", (title or "").strip()) if p]
        if parts:
            words.append(parts)
    if len(words) < 2:
        return ""
    head = []
    for i in range(min(len(w) for w in words)):
        first = words[0][i].lower()
        if any(w[i].lower() != first for w in words):
            break
        head.append(words[0][i])
    if not head or not any(len(w) > len(head) for w in words):
        return ""
    return " ".join(head)


def display_title_prefix(titles):
    """The product's own name as the READER sees it: the leading words shared by
    most window titles, not by all of them.

    `title_prefix` asks every title to agree, which is right for the record
    vocabulary but returns nothing on the real corpus, where one window of
    another product family is titled by its own name. For a display label the
    majority is enough: the words carried by more than half the distinct titles
    (at least two), with at least one of them longer than the prefix.
    """
    distinct = sorted({(t or "").strip() for t in titles if (t or "").strip()})
    firsts = defaultdict(list)
    for title in distinct:
        parts = [p for p in re.split(r"[\s-]+", title) if p]
        if parts:
            firsts[parts[0].lower()].append(title)
    if not firsts:
        return ""
    group = max(firsts.values(), key=len)
    if len(group) < 2 or len(group) * 2 <= len(distinct):
        return ""
    return title_prefix(group)


def window_display_names(window_titles, prefix):
    """Each window's name as the game draws it: its captured title minus the
    product prefix, keyed by seam token.

    A window whose title VARIES with its subject (the structure window is titled
    by the route or mission it shows) is named by the one title that spells its
    own token, which is the title it draws with no subject; with no such title
    it keeps its token. A title that IS the product name keeps the whole title.
    Two windows that would read the same are told apart by their tokens.
    """
    names = {}
    for tok, titles in window_titles.items():
        shown = []
        for title in titles:
            raw = (title or "").strip()
            if raw:
                shown.append(strip_title_prefix(raw, prefix) or raw)
        shown = sorted(set(shown))
        if len(shown) == 1:
            names[tok] = shown[0]
        else:
            own = [t for t in shown if norm(t) == norm(tok)]
            names[tok] = own[0] if own else tok
    readers = defaultdict(list)
    for tok, name in names.items():
        readers[norm(name)].append(tok)
    for toks in readers.values():
        if len(toks) < 2:
            continue
        for tok in toks:
            if names[tok] != tok:
                names[tok] = "%s (%s)" % (names[tok], tok)
    return names


def _state_controls(cap, own_titles):
    """The labelled toggles and buttons the capture's OWN window drew.

    A toggle is keyed by its text and read by its lit value; a button is keyed
    by the letters and digits of its text and read by its whole text, so a
    button that swaps a glyph beside its word reads as changed. A key the
    window draws twice is ambiguous and dropped.
    """
    out, dup = {}, set()

    def walk(node):
        kind = node.get("k")
        text = node.get("t") or ""
        if kind in ("toggle", "button") and re.search(r"[A-Za-z0-9]", text):
            if kind == "toggle" and "v" in node:
                key, val = ("toggle", text.strip()), bool(node["v"])
            else:
                key, val = ("button", norm(text)), text.strip()
            if key in out:
                dup.add(key)
            out[key] = val
        for child in node.get("c") or []:
            walk(child)

    for root in cap.get("roots") or []:
        if root.get("foreign") or root.get("title") not in own_titles:
            continue
        walk(root)
    for key in dup:
        out.pop(key, None)
    return out


def _word_of(text):
    """A control's text without the glyphs around its words."""
    return re.sub(r"^[^A-Za-z0-9]+|[^A-Za-z0-9]+$", "", text or "")


def _differing_control(mine, controls, unanimous):
    """The one control `mine` draws differently from its peers, or None.

    A peer value counts when more than half the peers that draw the control
    agree on it (`unanimous`: all of them). Exactly one toggle lit against
    unlit peers names that toggle; otherwise exactly one toggle unlit against
    lit peers names it with "off"; otherwise, with no toggle differing, exactly
    one button whose text differs names its words. A button whose text carries
    a digit is a count, which moves with the data rather than the state, and
    never names one.
    """
    lit, unlit, changed = [], [], []
    for key, val in mine.items():
        if key[0] == "button" and re.search(r"[0-9]", key[1]):
            continue
        peers = [other[key] for other in controls
                 if other is not mine and key in other]
        if len(peers) < 2:
            continue
        modal, count = Counter(peers).most_common(1)[0]
        agreed = count == len(peers) if unanimous else count * 2 > len(peers)
        if not agreed or val == modal:
            continue
        if key[0] == "toggle":
            (lit if val else unlit).append(key[1])
        else:
            changed.append(_word_of(val))
    if len(lit) == 1:
        return lit[0]
    if not lit and len(unlit) == 1:
        return unlit[0] + " off"
    if not lit and not unlit and len(changed) == 1 and changed[0]:
        return changed[0]
    return None


def toggle_tab_names(captures, window_titles):
    """Tab names for a window whose tabs are a row of toggles rather than a
    selection grid, keyed `{window: {tab: name}}`.

    A grid reports its selected item's text, which is how most tabs get their
    names (`_first_grid_value`); a toggle row reports none. A tab's toggle is
    the one lit in every capture of that tab and unlit in every capture of
    another tab that draws it - a grouping toggle lit under several tabs names
    none of them. Captures that name no tab are left out of both sides. The
    CURRENT captures are asked first; where they leave a tab unnamed or
    ambiguous (every current capture of it happens to share a lit preset, or a
    re-layout left it no current capture at all), every capture of the window
    is asked, which only adds captures to both sides. A tab with no such
    toggle, or with more than one, keeps its token.
    """
    current = _toggle_tab_names([c for c in captures if not is_stale(c)],
                                window_titles)
    everything = _toggle_tab_names(captures, window_titles)
    out = {}
    for win in set(current) | set(everything):
        merged = dict(everything.get(win, {}))
        merged.update(current.get(win, {}))
        out[win] = merged
    return out


def _toggle_tab_names(captures, window_titles):
    """One pass of `toggle_tab_names` over the captures it is given."""
    by_window = defaultdict(lambda: defaultdict(list))
    for cap in captures:
        if cap.get("mocked") or not cap.get("tab"):
            continue
        by_window[cap["window"]][cap["tab"]].append(cap)
    out = {}
    for win, tabs in by_window.items():
        own = set(window_titles.get(win) or ())
        drawn = {}
        always_lit = {}
        for tab, caps in tabs.items():
            sets = []
            for cap in caps:
                ctl = _state_controls(cap, own)
                sets.append({k[1] for k, v in ctl.items()
                             if k[0] == "toggle" and v is True})
                drawn.setdefault(tab, []).append(ctl)
            always_lit[tab] = set.intersection(*sets) if sets else set()
        for tab, always in always_lit.items():
            picks = []
            for text in always:
                elsewhere = [ctl[("toggle", text)]
                             for other, ctls in drawn.items() if other != tab
                             for ctl in ctls if ("toggle", text) in ctl]
                if elsewhere and not any(elsewhere):
                    picks.append(text)
            if len(picks) == 1:
                out.setdefault(win, {})[tab] = picks[0]
    return out


def state_display_names(captures, window_titles):
    """The visible control each state's seam name stands for, keyed by
    `window|tab|state|mode` - where the capture's own tree shows it.

    A state token is the lane's name for a seam step, not something the game
    draws. Among the CURRENT captures of one window, tab and mode, a state's
    control is the one it draws differently from its peers
    (`_differing_control`), asked first of the controls every peer agrees on and
    then of those most peers agree on: a capture taken under another sort order
    differs in its sort headers by majority only, so the unanimous pass finds
    the one control its own step changed. Anything else - no difference, or
    several - keeps the token, and so does a name two states of the same group
    would share, because two rows reading the same would be worse than a token.
    Nothing is typed: every name comes out of a dump.
    """
    groups = defaultdict(list)
    for cap in captures:
        if is_stale(cap) or cap.get("mocked") or not cap.get("window"):
            continue
        groups[(cap["window"], cap.get("tab") or "",
                cap.get("mode") or "")].append(cap)
    derived = {}
    for (win, tab, mode), caps in groups.items():
        own = set(window_titles.get(win) or ())
        controls = [_state_controls(c, own) for c in caps]
        by_state = defaultdict(set)
        for cap, mine in zip(caps, controls):
            state = cap.get("state") or ""
            if not state:
                continue
            name = (_differing_control(mine, controls, True)
                    or _differing_control(mine, controls, False))
            if name:
                by_state[state].add(name)
        owners = defaultdict(set)
        for state, names in by_state.items():
            if len(names) == 1:
                owners[next(iter(names))].add(state)
        for name, states in owners.items():
            if len(states) == 1:
                derived["|".join((win, tab, next(iter(states)), mode))] = name
    return derived


def strip_title_prefix(title, prefix):
    """`title` with the product's own name taken off the front, if it is there."""
    if not prefix:
        return (title or "").strip()
    low, plow = (title or "").strip(), prefix.strip()
    if low.lower().startswith(plow.lower()):
        rest = low[len(plow):]
        return rest.lstrip(" -\t").strip()
    return low


def window_vocabulary(window_tokens, window_titles, window_tabs=None):
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
    # The product's own name, DERIVED from the titles rather than typed: it is
    # the run of leading words every window title shares.
    prefix = title_prefix([t for tok in window_tokens
                           for t in (window_titles.get(tok) or ()) if t])
    product = set()
    for tok in window_tokens:
        for title in window_titles.get(tok, ()):
            raw = (title or "").strip()
            if raw and not strip_title_prefix(raw, prefix):
                product.add(norm(raw))
    vocab = {}
    for tok in window_tokens:
        titles = [t for t in (window_titles.get(tok) or ()) if t]
        per_title = []
        for title in titles:  # e.g. "Parsek - Real Spawn Control"
            cleaned = strip_title_prefix(title, prefix).lower()
            if not cleaned:
                continue
            # A multi-word title contributes only its concatenation. Its
            # individual words are generic ("Real Spawn Control" -> real, spawn,
            # control; "Gloops Flight Recorder" -> flight, recorder) and each one
            # pulled in records about something else entirely.
            generic = {norm(prefix), "state", "window"} - {""}
            parts = [w for w in re.findall(r"[a-z]{4,}", cleaned)
                     if w not in generic]
            words = {norm(cleaned)} | (set(parts) if len(parts) == 1 else set())
            per_title.append(words)
        stable = set()
        if per_title:
            stable = set.intersection(*per_title)
        # A window's TAB tokens name it too. A record about the Recordings tab
        # names that tab and not the window that hosts it, so without this the
        # alignment entries attached to no window at all.
        tabs = {t for t in (window_tabs or {}).get(tok, ()) if len(t) >= 5}
        record = sorted(stable | {tok} | tabs)
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
    """The window sources: anything under `Source/Parsek/UI/`, plus the
    surface-shaped files that live at `Source/Parsek/` root - `*UI.cs`,
    `*Window*.cs` and `*Dialog*.cs`.

    Widening it to the whole tree makes every PR that touched a recording look
    like a PR about a window; leaving it at `UI/` alone attributed MergeDialog,
    ReFlyRevertDialog, CommittedActionDialog and ForwardRenderWindow to no window
    at all.
    """
    slashed = path.replace("\\", "/")
    if "Source/Parsek/UI/" in slashed:
        return True
    if not slashed.startswith("Source/Parsek/"):
        return False
    leaf = slashed.split("/")[-1]
    if not leaf.endswith(".cs"):
        return False
    stem = leaf[:-3]
    return (stem.endswith("UI") or "Window" in stem or "Dialog" in stem)


def scan_shots_dir(path, scenarios_dir, want_colors=True, verbose=False):
    """One `<runId>_<specId>_shots` directory -> capture records."""
    base = os.path.basename(path.rstrip("/\\"))
    # run.py's collision guard appends `_run<N>` (a second run in the same minute)
    # and `_a<N>` (a retry attempt) AFTER the scenario id (hlib.format_run_id). They
    # belong to the RUN, not the spec: read as part of the spec id they named no
    # scenario, so the capture lost its dataset and never paired in Compare.
    m = re.match(r"(?P<run>\d{4}-\d{2}-\d{2}_\d{4})_(?P<spec>.+?)"
                 r"(?P<sfx>(?:_run\d+)?(?:_a\d+)?)_shots$", base)
    run_id = (m.group("run") + m.group("sfx")) if m else base
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
            # ABSENT means a real-save capture, which is every committed one.
            "mock": mock_provenance(dump),
            "dump": dump,
            "png": png if os.path.isfile(png) else None,
            "log": replay["captures"].get(label) or {},
        })
    return {"captures": caps, "tabs": replay["tabs"], "windows": replay["windows"],
            "runId": run_id, "specId": spec_id, "fixture": fixture,
            "complete": run_complete(path)}


def run_complete(shots_dir):
    """Whether the run behind a shots directory flew to the end.

    run.py writes its result JSON beside the shots directory, under the same
    name without the `_shots` suffix. `True` is a PASS verdict; `False` is a
    result that says anything else (a failed or partial run can be missing
    captures it would otherwise have taken); `None` is no readable result, where
    the caller has to fall back to what the run did photograph.
    """
    base = shots_dir.rstrip("/\\")
    if base.endswith("_shots"):
        base = base[:-len("_shots")]
    path = base + ".json"
    if not os.path.isfile(path):
        return None
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            verdict = (json.load(fh) or {}).get("verdict")
    except Exception:
        return None
    if not verdict:
        return None
    return verdict == "PASS"


def run_sort_key(run_id):
    """A run id as an orderable value: the minute stamp, then run.py's `_run<N>`
    collision counter, then the `_a<N>` retry attempt (hlib.format_run_id). An
    absent counter is the first of its kind, so `_1841` sorts before
    `_1841_run2`."""
    m = re.match(r"^(\d{4}-\d{2}-\d{2}_\d{4})(?:_run(\d+))?(?:_a(\d+))?$",
                 run_id or "")
    if not m:
        return (run_id or "", 0, 0)
    return (m.group(1), int(m.group(2) or 1), int(m.group(3) or 1))


def prune_removed_tabs(captures):
    """Drop a tab from the tab bars of captures taken after that tab stopped existing.

    A selection grid records only the SELECTED item's text, so a capture's tab bar
    is assembled from every tab token its window has shown in any capture. When the
    product removes a tab (the Career window's Facilities and Milestones tabs,
    2026-09-24), the old captures keep it, and without this pass every NEW capture
    of that window drew the removed tabs too - over a frame that shows two.

    A tab counts as removed when none of its captures is live (each one is
    superseded or retired). It is then dropped from every capture of the same
    window taken AFTER the tab's last capture; older captures keep it, because they
    were drawn when it still existed. Returns {window: [removed tokens]}.
    """
    by_window = defaultdict(lambda: defaultdict(list))
    for cap in captures:
        if cap.get("tab"):
            by_window[cap["window"]][cap["tab"]].append(cap)
    removed = {}
    for window, tabs in by_window.items():
        for tok, caps in tabs.items():
            if any(not c.get("supersededBy") and not c.get("retired") for c in caps):
                continue
            last = max(c["capturedUtc"] for c in caps)
            removed.setdefault(window, []).append(tok)
            for cap in captures:
                if cap["window"] != window:
                    continue
                if cap["capturedUtc"] <= last:
                    # Drawn while the window still had the tab: a picture of a
                    # layout the product no longer has, so not a current state.
                    info = cap.setdefault("outdated", {"tabs": [], "window": window})
                    if tok not in info["tabs"]:
                        info["tabs"].append(tok)
                    continue
                names = cap.get("tabNames") or []
                cap["tabNames"] = [t for t in names if t.get("token") != tok]
    return removed


def parse_utc(text):
    """An ISO-8601 UTC stamp (`2026-09-23T21:35:49Z`, optionally with fractional
    seconds) as an aware datetime, or None when it is absent or unreadable."""
    if not text:
        return None
    s = str(text).strip()
    if s.endswith("Z"):
        s = s[:-1] + "+00:00"
    try:
        dt = datetime.datetime.fromisoformat(s)
    except ValueError:
        return None
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=datetime.timezone.utc)
    return dt


def mark_layout_epochs(captures, epochs=None):
    """Mark every capture drawn before its window's current layout existed.

    `epochs` maps a window token to `{"utc", "pr"}` (default `LAYOUT_EPOCHS`). A
    capture of that window whose `capturedUtc` is EARLIER than the epoch is
    `outdated`, with the epoch recorded beside any removed-tab reason
    `prune_removed_tabs` already gave it; a capture taken at the epoch or after it
    is current. A window with no epoch, and a capture with no readable time, are
    not judged. Independent of superseded / retired: those say a later capture
    exists or the lane stopped, this says the picture shows a layout the product
    no longer has. Returns the captures it marked.
    """
    epochs = LAYOUT_EPOCHS if epochs is None else epochs
    out = []
    for cap in captures:
        ep = epochs.get(cap.get("window"))
        if not ep:
            continue
        floor = parse_utc(ep.get("utc"))
        at = parse_utc(cap.get("capturedUtc"))
        if floor is None or at is None or at >= floor:
            continue
        info = cap.setdefault("outdated", {"tabs": [], "window": cap["window"]})
        info["epoch"] = {"utc": ep["utc"], "pr": ep.get("pr")}
        out.append(cap)
    return out


def mark_retired(captures, runs):
    """Mark every capture its own lane no longer produces.

    `runs` is `[{"specId", "runId", "complete"}]`, one per shots directory. For a
    capture of scenario S at run R, each NEWER run of S is a witness if it is
    complete (a PASS result, and at least one capture - a PASS whose dumps are
    gone proves nothing) or, where no result says, if it photographed the same
    window. A capture that no witness reproduced - neither its key nor its label -
    and that no later capture of its key already superseded is RETIRED, dated
    from the first witness. A failed run is never a witness: a lane that crashed
    half way lacks captures for a reason that is not the product.

    Returns the retired captures.
    """
    by_run = {}
    for r in runs:
        by_run[(r["specId"], r["runId"])] = {
            "complete": r.get("complete"), "keys": set(), "labels": set(),
            "windows": set(), "n": 0}
    for cap in captures:
        slot = by_run.setdefault((cap["specId"], cap["runId"]), {
            "complete": None, "keys": set(), "labels": set(), "windows": set(),
            "n": 0})
        slot["keys"].add(cap.get("key") or "")
        slot["labels"].add(cap["label"])
        slot["windows"].add(cap["window"])
        slot["n"] += 1
    runs_of = defaultdict(list)
    for (spec, run) in by_run:
        runs_of[spec].append(run)
    for spec in runs_of:
        runs_of[spec].sort(key=run_sort_key)
    out = []
    for cap in captures:
        if cap.get("supersededBy") or not cap["specId"]:
            continue
        mine = run_sort_key(cap["runId"])
        since = None
        reproduced = False
        for run in runs_of[cap["specId"]]:
            if run_sort_key(run) <= mine:
                continue
            slot = by_run[(cap["specId"], run)]
            if slot["complete"] is True:
                witness = slot["n"] > 0
            elif slot["complete"] is None:
                witness = cap["window"] in slot["windows"]
            else:
                witness = False
            if not witness:
                continue
            if (cap.get("key") or "") in slot["keys"] or cap["label"] in slot["labels"]:
                reproduced = True
                break
            if since is None:
                since = run
        if since is not None and not reproduced:
            cap["retired"] = {"spec": cap["specId"], "since": since}
            out.append(cap)
    return out


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
                budget=DEFAULT_BUDGET_BYTES, verbose=False, stamp="",
                pin_fixture="", layout_epochs=None):
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
        prov = cap.get("mock")
        fixture = cap["fixture"]
        disagrees = []
        if prov:
            # A MOCKED capture's facets come out of the catalogue state id, not
            # out of its label and not out of the lane's own fixture: the gallery
            # lane loads a real save, so the fixture the spec names would file a
            # synthetic picture under a real dataset.
            window, tab, state = mock_facets(
                prov, set(tabs_by_window.get(prov.get("window") or "") or ()))
            window = window or lab["window"] or lab["host"]
            fixture = MOCK_FIXTURE
        elif lab["window"] and lab["window"] != window:
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
        if not prov:
            # Filed under the log either way; this only SAYS so, and only for a
            # real capture - a mocked one's label is generated from the same
            # state id its facets came from, so there is nothing to disagree.
            disagrees = label_log_disagreements(
                lab, cap["log"], tabs_by_window.get(window) or {}, tab_alias)

        screen = dump.get("screen") or {}
        sw = int(screen.get("width") or FRAME_W)
        sh = int(screen.get("height") or FRAME_H)

        sampler = None
        grid_runs = None
        thumb_runs = None
        text_offsets = None
        pix = None
        if cap["png"]:
            try:
                pw, ph, bpp, px = read_png(cap["png"])
                if pw != sw or ph != sh:
                    # The dump's rects are in the frame the dump was taken at. A
                    # PNG of a different size (a superSize screenshot, a resized
                    # instance) would sample the wrong pixels for every control,
                    # and scaling it here would be a guess about which way. Skip
                    # the sampling and say so; the geometry still renders.
                    raise ValueError(
                        "frame %dx%d does not match the dump's %dx%d"
                        % (pw, ph, sw, sh))
                pix = (pw, ph, bpp, px)

                def sampler(rect, exclude=(), _p=pix):
                    return sample_colors(_p[0], _p[1], _p[2], _p[3], rect,
                                         exclude=exclude)

                def grid_runs(rect, _p=pix):
                    return grid_label_runs(_p[0], _p[1], _p[2], _p[3], rect)

                def thumb_runs(rect, _p=pix):
                    return slider_thumb_run(_p[0], _p[1], _p[2], _p[3], rect)

                def text_offsets(rect, _p=pix):
                    return text_ink_offset(_p[0], _p[1], _p[2], _p[3], rect)
            except Exception as exc:
                if verbose:
                    sys.stderr.write("png %s: %s\n" % (cap["png"], exc))

        roots = []
        parsek_rects = []
        own_roots = []
        parsek_roots = []
        for root in dump.get("roots") or ():
            rect = [int(v) for v in (root.get("rect") or [0, 0, 0, 0])]
            key = (root.get("text") or "", tuple(rect))
            is_foreign = key in foreign
            h = root_height(root, cap["log"].get("rects"))
            node = compact_tree(root, [rect[0], rect[1]], sampler,
                                grid_runs=grid_runs, thumb_runs=thumb_runs,
                                text_offsets=text_offsets)
            node["x"], node["y"] = rect[0], rect[1]
            node["h"] = h
            node["title"] = root.get("text") or ""
            if sampler is not None and node["title"]:
                # The title bar only: sampling the whole window rect finds the
                # ink of whatever row happens to be brightest instead.
                _tbg, tfg = sampler([rect[0], rect[1], rect[2], 24], ())
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
                if owner == window:
                    own_roots.append(node)
                parsek_roots.append(node)
            roots.append(node)
        # How much the subject window drew: the rail's simple-to-complex order
        # within a tab. Its own root(s) when the seam's rect names one, every
        # Parsek root otherwise.
        complexity = sum(count_nodes(r) for r in (own_roots or parsek_roots))
        own_grid_si = _first_grid_index(own_roots)

        photo = None
        if with_photos and pix and (parsek_rects or cap["log"].get("dialog")):
            if cap["log"].get("dialog"):
                # A PopupDialog is a CENTRED uGUI canvas with no presence in the
                # control tree, so the Parsek windows' bounding box does not
                # contain it - cropping to that box captioned another mod's window
                # as "Confirm: Wipe Recordings". The whole frame is the only honest
                # crop, and inventing a modal rect would be worse than a big one.
                photo = {"x": 0, "y": 0, "w": sw, "h": sh, "whole": 1}
            else:
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
            "fixture": fixture,
            "window": window,
            "tab": tab,
            "tabNames": [],
            "tabAlias": tab_alias,
            "scene": cap["log"].get("scene") or "",
            "mode": mode,
            "state": state,
            # The dump's own provenance block, forwarded whole: the page badges
            # off it and the index counts off it.
            "mocked": prov,
            "disagrees": disagrees,
            "capturedUtc": dump.get("capturedUtc") or "",
            "screen": [sw, sh],
            "openWindows": cap["log"].get("openWindows") or [],
            "dialog": cap["log"].get("dialog"),
            "pointer": cap["log"].get("pointer"),
            "roots": roots,
            "photo": photo,
            "counts": dump.get("counts") or {},
            "complexity": complexity,
            "gridIndex": own_grid_si,
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
    # on the tab bar is therefore still a string the game drew. The NEWEST capture
    # of a tab names it: a tab renamed in the product keeps its seam token, and
    # first-seen let a pre-rename heading ("Roster State") name the tab in the
    # rail, the header and Compare long after every new capture read "Roster".
    tab_display = defaultdict(dict)
    for cap in sorted(captures, key=lambda c: (c["capturedUtc"],
                                               run_sort_key(c["runId"])),
                      reverse=True):
        if not cap["tab"]:
            continue
        name = _first_grid_value(cap["roots"])
        if name:
            tab_display[cap["window"]].setdefault(cap["tab"], name)

    # ---- tab names, resolved PER CAPTURE ----------------------------------
    # A selection grid reports only the SELECTED item's text, so the other tabs'
    # names have to come from the captures where THEY were selected. Resolving
    # that once, globally and first-seen, meant a pre-rename heading from an older
    # epoch won for every dataset: the rebuilt Kerbals window rendered
    # "Roster State" / "Mission Outcomes" over a frame that says "Roster" /
    # "Flights", and since the selected marker compared NAMES, no cell was marked
    # selected either. So it is resolved per capture, newest capture first, and
    # narrowest scope first: this capture's own text for its own tab, then the
    # same dataset and mode, then the same dataset, then anywhere.
    newest_first = sorted(captures, key=lambda c: c["capturedUtc"], reverse=True)
    sel_tv = {}
    for cap in newest_first:
        if not cap["tab"]:
            continue
        tv = _first_grid_value(cap["roots"])
        if not tv:
            continue
        for scope in ((cap["fixture"], cap["mode"]), (cap["fixture"], None),
                      (None, None)):
            sel_tv.setdefault((cap["window"], cap["tab"]) + scope, tv)
    for cap in captures:
        toks = tabs_by_window.get(cap["window"]) or {}
        if not toks:
            continue
        own = _first_grid_value(cap["roots"])
        names = []
        for tok, idx in sorted(toks.items(), key=lambda kv: kv[1]):
            if tok == cap["tab"] and own:
                name = own
            else:
                name = (sel_tv.get((cap["window"], tok, cap["fixture"], cap["mode"]))
                        or sel_tv.get((cap["window"], tok, cap["fixture"], None))
                        or sel_tv.get((cap["window"], tok, None, None))
                        or tok)
            names.append({"token": tok, "index": idx, "name": name})
        cap["tabNames"] = names

    # ---- a hover capture that photographed no hover -------------------------
    # The pointer op's own outcome, read off the log: a step that moved the
    # cursor onto a control (`park=false`) and then reported `tooltip=-` had an
    # EMPTY `GUI.tooltip` when the frame was taken, so the capture is of the
    # window's idle state under a hover label. `tooltip` absent from the line is
    # the log declining to say - the key postdates the older runs - and the
    # fallback there is the capture's own tree: a hover frame that is
    # byte-identical to another capture of the same run, colours stripped,
    # photographed nothing its sibling did not.
    sig_by_run = defaultdict(dict)
    for cap in captures:
        sig_by_run[cap["runId"]].setdefault(_tree_sig(cap), []).append(cap)
    for cap in captures:
        ptr = cap.get("pointer")
        if not ptr or ptr.get("park"):
            continue
        tip = ptr.get("tooltip")
        if tip == POINTER_TOOLTIP_EMPTY:
            cap["hoverEmpty"] = {"why": "log", "at": ptr.get("at") or ""}
        elif tip is None:
            twins = [c for c in sig_by_run[cap["runId"]].get(_tree_sig(cap), [])
                     if c["id"] != cap["id"]]
            if twins:
                cap["hoverEmpty"] = {"why": "twin", "twin": twins[0]["label"]}

    # ---- keys, before/after -------------------------------------------------
    by_key = defaultdict(list)
    for cap in captures:
        cap["key"] = key_of(cap["fixture"], cap["window"], cap["tab"], cap["state"],
                            cap["mode"], cap["scene"],
                            (cap.get("mocked") or {}).get("stateId", ""))
        by_key[cap["key"]].append(cap)
    keys = {}
    for k, caps in by_key.items():
        caps.sort(key=lambda c: (c["capturedUtc"], c["runId"]))
        before, after = caps[0], caps[-1]
        changed = before["id"] != after["id"] and _differs(before, after)
        # SUPERSEDED: a later capture of the same key exists, so this one is not
        # the current picture of that state. It stays reachable as the pair's
        # BEFORE, and it stops being what the mirror shows or what coverage
        # counts - which is how a re-flown lane retires its own stale capture
        # without a label being named anywhere.
        for older in caps[:-1]:
            older["supersededBy"] = after["id"]
        keys[k] = {
            "all": [c["id"] for c in caps],
            "before": before["id"],
            "after": after["id"],
            "superseded": [c["id"] for c in caps[:-1]],
            "changed": bool(changed),
            "measured": measure_pair(before, after) if changed else None,
        }

    # ---- retired: a state its own lane stopped producing --------------------
    # Superseding needs a later capture of the SAME key, so a state a re-flown
    # lane no longer photographs at all (the Kerbals "owner chain" after the
    # round-2 rebuild) stayed current forever. See `mark_retired`.
    mark_retired(captures, [{"specId": s["specId"], "runId": s["runId"],
                             "complete": s.get("complete")} for s in scans])
    # A tab counts as removed from superseded / retired captures only, never from
    # old-layout ones: a tab whose only captures predate a re-layout still exists.
    prune_removed_tabs(captures)
    mark_layout_epochs(captures, layout_epochs)
    for k, info in keys.items():
        after = next(c for c in by_key[k] if c["id"] == info["after"])
        if after.get("retired"):
            info["retired"] = after["retired"]
        if after.get("outdated"):
            info["outdated"] = after["outdated"]

    fixtures = OrderedDict()
    for cap in captures:
        fixtures.setdefault(cap["fixture"], {"key": cap["fixture"], "specIds": []})
        if cap["specId"] not in fixtures[cap["fixture"]]["specIds"]:
            fixtures[cap["fixture"]]["specIds"].append(cap["specId"])

    # A tab row of toggles names its tabs through the toggle each tab lights.
    toggled = toggle_tab_names(captures, window_titles)
    windows = []
    for tok in window_tokens:
        caps = [c for c in captures if c["window"] == tok]
        windows.append({
            "token": tok,
            "titles": sorted(window_titles.get(tok, [])),
            "tabs": [{"token": t, "index": i,
                       "name": (tab_display.get(tok, {}).get(t)
                                or toggled.get(tok, {}).get(t) or t)}
                     for t, i in sorted(tabs_by_window.get(tok, {}).items(),
                                        key=lambda kv: kv[1])],
            "captureCount": len(caps),
            "mockedCount": len([c for c in caps if c.get("mocked")]),
        })
    for tok in sorted({c["window"] for c in captures} - set(window_tokens)):
        caps = [c for c in captures if c["window"] == tok]
        windows.append({"token": tok, "titles": sorted(window_titles.get(tok, [])),
                        "tabs": [], "captureCount": len(caps),
                        "mockedCount": len([c for c in caps if c.get("mocked")])})

    # What the rail and the headers call each window: its own title, not its
    # seam token (`window_display_names`). The token stays the key everywhere.
    # Only the seam's windows are named: a diagnostic surface the census
    # photographed (the GuiTree probe) keeps its token, so it never contends
    # with a product window for a name.
    shown_names = window_display_names(
        {w["token"]: w["titles"] for w in windows if w["token"] in window_tokens},
        display_title_prefix([t for w in windows for t in w["titles"]]))
    for w in windows:
        w["name"] = shown_names.get(w["token"]) or w["token"]

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
        vocab = window_vocabulary(
            [w["token"] for w in windows],
            {w["token"]: w["titles"] for w in windows},
            {w["token"]: [t["token"] for t in w["tabs"]] for w in windows})
        cl = _read(os.path.join(repo_root, "CHANGELOG.md"))
        td = _read(os.path.join(repo_root, "docs", "dev", "todo-and-known-bugs.md"))
        for extra in _done_volumes(repo_root):
            td += "\n" + _read(extra)
        notes = attach_records(vocab, parse_changelog(cl), parse_todo(td),
                               parse_merges(repo_root), ui_paths=True)

    fixture_order = [f["key"] for f in fixtures.values()]
    pinned = pin_fixture if pin_fixture in fixture_order else None
    if pin_fixture and not pinned:
        raise SystemExit("--default-fixture %r is not one of: %s"
                         % (pin_fixture, ", ".join(fixture_order)))
    summaries = {w["token"]: window_compare_summary(captures, keys, w["token"],
                                                    missing)
                 for w in windows if w["captureCount"]}

    return {
        "schema": MIRROR_SCHEMA,
        # The page's own generation stamp. It travels in the notes blob, because
        # a verdict is about the corpus that was on screen when it was typed.
        "generatedUtc": stamp or "",
        # The layout epochs this page was judged against (`mark_layout_epochs`).
        "layoutEpochs": dict(LAYOUT_EPOCHS if layout_epochs is None else layout_epochs),
        # The windows the command seam can open. A capture whose subject is not
        # one of them is a diagnostic surface (the GuiTree probe), shown in the
        # mirror because it WAS photographed but left out of Compare, which is
        # about the product's windows.
        "seamWindows": list(window_tokens.keys()),
        # The product's own name, derived from the window titles the captures
        # carry, so the page can strip it without knowing it.
        "titlePrefix": title_prefix([t for w in windows for t in w["titles"]]),
        # Each state's visible control where the dumps show one
        # (`state_display_names`), keyed `window|tab|state|mode`.
        "stateNames": state_display_names(
            captures, {w["token"]: w["titles"] for w in windows}),
        "clickKinds": list(CLICK_KINDS),
        # The op vocabulary, so the page can recognise the one op that is also a
        # button label without a window string being typed into this file.
        "seamOps": list(SEAM_VERBS),
        "closeOp": VERB_CLOSE,
        "fixtures": list(fixtures.values()),
        # PINNED here, never derived in the page: see `default_fixture`.
        "defaultFixture": pinned or default_fixture(captures, fixture_order),
        "mockFixture": MOCK_FIXTURE,
        "notesSchema": NOTES_SCHEMA,
        "notesFields": list(NOTES_FIELDS),
        "noteVerdicts": list(NOTE_VERDICTS),
        "windows": windows,
        "windowSummaries": summaries,
        # Each window's rail rows in reading order (`rail_rows`): ordered here,
        # so the page lists them and never sorts them itself.
        "railRows": rail_rows(captures, tab_order(tabs_by_window, captures)),
        "captures": captures,
        "keys": keys,
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


def _tree_sig(cap):
    """One capture's tree with the SAMPLED COLOURS stripped: what two captures
    have to differ in before a difference is a layout difference rather than a
    screenshot one."""
    return json.dumps([_strip(r) for r in cap["roots"] if not r.get("foreign")],
                      sort_keys=True)


def _differs(a, b):
    return _tree_sig(a) != _tree_sig(b)


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
/* The transient line: empty unless a click or a fallback has something to say.
   Fixed height, so a message appearing never moves the stage. */
#status{font-size:11px;color:var(--dim);height:16px;line-height:16px;margin:0 0 4px;
  overflow:hidden;white-space:nowrap;text-overflow:ellipsis}
#status.warn{color:var(--warn)}
#opts{position:relative;font-size:12px}
#opts>summary{cursor:pointer;color:var(--dim);list-style:none;padding:3px 6px;
  border:1px solid #444;border-radius:3px}
#opts>summary::-webkit-details-marker{display:none}
#opts[open]>summary{color:var(--ink);border-color:#666}
#opts .optpanel{position:absolute;right:0;top:28px;background:#232323;
  border:1px solid #444;border-radius:4px;padding:8px;display:flex;
  flex-direction:column;gap:6px;z-index:60;white-space:nowrap}
.stagewrap{position:relative;overflow:auto;border:1px solid #000;background:#0c0c0c;
  max-width:100%}
.stage{position:relative;width:1280px;height:720px;background:#1a2430;
  flex:0 0 auto}
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
/* The modal block is text over the frame too, so it obeys the same suppression. */
.stage.overlay .dlg .dt,.stage.overlay .dlg .db,.stage.overlay .dlg .dcap{display:none}
.stage.overlay .dlg{background:none;box-shadow:none;border-color:rgba(110,159,208,.7)}
.stage.overlay.noboxes .gn,.stage.overlay.noboxes .kwin{border-color:transparent !important}
.sidebyside{display:flex;gap:10px;align-items:flex-start;flex-wrap:wrap}
.sidebyside>div{flex:0 1 auto;min-width:0}
.sidebyside h5{margin:0 0 3px;font-size:11px;color:var(--dim);font-weight:600}
/* The two captions only mean something when there are two panes. */
.sidebyside:not(.two) h5{display:none}
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
/* Measured on the ib-logistics-basic frame (Rename / Log (Route) / Logistics /
   Timeline): KSP draws a button as a NEAR-BLACK outline - grey 5 to 25 - with a
   light top bevel inside it (88, 71, 61, fading) over a fill of 25 to 76. A
   uniform #5a5a5a line is brighter than the fill on every one of them, which is
   why the whole button interior read as ink where only its label should. */
.gn.k-button,.gn.k-repeatbutton{background:var(--btn);border:1px solid #141414;
  box-shadow:inset 0 1px 0 rgba(255,255,255,.17);
  border-radius:3px;justify-content:center;color:var(--ink);cursor:pointer}
/* A LABEL-styled button (a clickable table cell, e.g. the Career row name that
   opens the Timeline) is drawn by KSP as plain text: no outline, no bevel. */
.gn.k-button.s-label,.gn.k-button.s-{background:none;border:0;box-shadow:none;
  justify-content:flex-start;padding-left:0}
.gn.k-buttongrid{background:var(--btn);border:1px solid #141414;border-radius:3px}
.gn.k-buttongrid.grid{background:none;border:0}
/* A BOX-styled button is a table header cell, not a raised button: KSP draws it
   with the box's dark outline and no bevel, and the page's button rules were
   giving it a light border and centring its label. The label's own position is
   measured off the frame (see `text_ink_offset`); these rules carry the edge. */
.gn.k-button.s-box{border:1px solid #1d1d1d;box-shadow:none;
  justify-content:flex-start}
.gn.k-buttongrid .gi{position:absolute;top:0;bottom:0;background:var(--btn);
  border:1px solid #141414;border-radius:3px;color:var(--ink);cursor:pointer;
  font:var(--gfont)/1 Arial,Helvetica,sans-serif}
.gn.k-buttongrid .gi>.gl{left:0;right:0}
/* Measured on the cek-career-contracts and bdk-kerbals-roster frames: the
   SELECTED cell is the DARK pushed-in one with no top highlight (grey profile
   130,30,32,35,40,43,44..60) and the UNSELECTED cells carry the light top edge
   (14,102,88,78,68,41..59). A brighter "selected" is the intuitive guess and the
   wrong one. The fills themselves are sampled per cell where a frame was
   available; these rules only carry the edge. */
.gn.k-buttongrid .gi{box-shadow:inset 0 1px 0 rgba(255,255,255,.16)}
.gn.k-buttongrid .gi.on{box-shadow:none;background:#2c2c2c;color:#f0f0f0;
  border-color:#242424}
.gn.k-buttongrid .gl{position:absolute;top:0;bottom:0;display:flex;align-items:center;
  justify-content:center;white-space:pre;overflow:hidden}
.gn.k-buttongrid .gi:hover{outline:1px solid var(--accent);outline-offset:-1px}
.gn.k-toggle{cursor:pointer}
.gn.k-toggle .cb{width:14px;height:14px;border:1px solid #777;background:#222;
  display:inline-block;margin-right:4px;text-align:center;line-height:12px;font-size:11px;
  color:#cfe6ff;flex:0 0 auto;position:relative}
/* The mark inside the box, drawn rather than typed: KSP's tick lives in its own
   skin texture, and an ASCII `x` in its place was the wrong SHAPE at the right
   state. Two borders rotated 45 degrees is a tick, and it costs no font. */
.gn.k-toggle .cb.on::after{content:"";position:absolute;left:3px;top:0px;
  width:5px;height:9px;border:solid #cfe6ff;border-width:0 2px 2px 0;
  transform:rotate(40deg)}
/* A toggle in the BUTTON style is not a checkbox. KSP draws
   `GUILayout.Toggle(v, text, "button")` as a button that sits pushed in while
   it is on - 987 of the corpus's 8154 toggles - and the page drew every one of
   them as "x label" on bare window fill. The fill is sampled off the frame like
   any other button's; these rules carry the edge and the pushed state. */
.gn.k-toggle.s-button{background:var(--btn);border:1px solid #141414;
  border-radius:3px;justify-content:center;color:var(--ink);
  box-shadow:inset 0 1px 0 rgba(255,255,255,.17)}
/* Pushed in: the bevel goes and the outline darkens, which is the same move the
   selection grid's selected cell makes - measured there as a grey profile of
   130,30,32,35,40,43,44..60 against an unselected 14,102,88,78,68,41..59. The
   two use the same #242424 because they are the same skin state. */
.gn.k-toggle.s-button.on{box-shadow:none;border-color:#242424}
.gn.k-toggle.s-button .cb{display:none}
/* A text field is SUNKEN: KSP draws the same near-black outline as a button
   (measured 5 to 25 on the ib-logistics frame) with no top bevel, over a fill
   darker than the window's. The #666 line this replaced was brighter than the
   field it enclosed, which is what made 55 disabled fields read as having no
   text at all. */
.gn.k-textfield{background:#1e1e1e;border:1px solid #141414;border-radius:2px;
  padding-left:3px}
/* A scroll view SCROLLS. Its children are laid out at the rects the dump
   recorded, which for the Missions table and the test runner's idle tree run
   thousands of pixels past the fold, and `overflow:hidden` made everything
   below it unreachable - the page showed the same first screenful the census
   photographed and nothing else, with no sign that more existed. The children
   are absolutely positioned inside it, and an absolutely positioned descendant
   of its own containing block DOES create scrollable overflow, so the extent
   needs no content sizer: the rects are the extent. The initial offset stays
   zero, which is the offset the frame was taken at. */
.gn.k-scrollview{overflow:auto;scrollbar-width:none}
/* The BROWSER's own scroll bar, hidden. KSP's scroll bar is a control in the
   dump and the page draws it from that control's own rect, so leaving the
   native one visible put a white bar over the mirrored one on all 52
   overflowing scroll views - a difference from the game that the page itself
   introduced. Wheel and drag still scroll; the mirrored bar is the visible one,
   as it is in the game. */
.gn.k-scrollview::-webkit-scrollbar{display:none}
.gn.k-slider{display:flex;align-items:center}
/* The groove. Where the frame gave a colour for it (`sg`) the control is filled
   with it across its own rect, which is what KSP draws - the
   ib-structure-mission scroll bar is a flat luminance 45 over its whole 15 px
   width. The drawn line below is the FALLBACK for a groove that would not
   sample, oriented by the control's own rect: 130 of the corpus's 142 sliders
   are scroll bars and 130 of those are vertical, and a horizontal rule drew a
   15x546 scroll bar as a short bar across its middle. */
.gn.k-slider::before{content:"";position:absolute;left:0;right:0;top:50%;height:3px;
  margin-top:-1px;background:#666;border-radius:2px}
.gn.k-slider.vt::before{left:50%;right:auto;top:0;bottom:0;width:3px;height:auto;
  margin-top:0;margin-left:-1px}
.gn.k-slider.sg::before{display:none}
/* The handle, at the position AND in the colours MEASURED off the frame
   (`slider_thumb_run` plus the same sampler every other surface uses). Absent
   when the frame carried no contrast to read, and then the groove stays bare
   rather than showing a knob at a position nothing recorded.
   The fallback greys are for a thumb whose colours would not sample; the face is
   the one a scroll bar actually has (dark, under a light bevel), not the light
   one a "handle" suggests. */
.gn.k-slider .th{position:absolute;background:#2b2b2b;border:1px solid #5a5a5a;
  border-radius:2px}
.gn.k-slider:not(.vt) .th{top:1px;bottom:1px}
.gn.k-slider.vt .th{left:1px;right:1px}
.gn.k-layoutgroup{}
.gn.dis{opacity:.42}
%CLICK_CSS%
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
table.sum tr.here td{background:#25323f}
table.sum .wlink{color:var(--accent);cursor:pointer}
table.sum .wlink:hover{text-decoration:underline}
#compareView>details{margin-top:24px}
#compareView>details>summary{cursor:pointer;color:var(--accent);font-size:12px}
/* The window's own tooltip echo strip: `TooltipEchoBox` draws one box-styled
   label, wrapped, one or two text lines tall, and scrolls (never ellipsises)
   text that will not fit. */
.gn.strip{display:block;padding:2px 4px;white-space:normal;overflow:hidden;
  line-height:15px;color:#b9c6d4}
.gn.strip .tx{display:block;white-space:normal}
.gn.strip.over .tx{white-space:pre;animation:mq 9s linear infinite}
@keyframes mq{0%{transform:translateX(0)}8%{transform:translateX(0)}
  92%{transform:translateX(var(--mqshift,-50%))}100%{transform:translateX(var(--mqshift,-50%))}}
.echo{margin-top:6px;font-size:12px;line-height:16px;color:#b9c6d4;
  height:48px;box-sizing:content-box;overflow-y:auto;
  background:#1b1b1b;border:1px solid #2c2c2c;border-radius:3px;padding:4px 8px}
.dlg{position:absolute;z-index:40;left:50%;transform:translateX(-50%);top:150px;
  background:#2c2c2c;border:1px solid #777;border-radius:6px;padding:0 0 10px;
  min-width:360px;box-shadow:0 8px 30px #000a}
.dlg .dt{background:#3a3a3a;padding:6px 10px;font-weight:600;color:#fff;font-size:13px;
  border-radius:5px 5px 0 0}
.dlg img{display:block;max-width:560px;margin:8px auto 6px;border:1px solid #111}
.dlg .db{display:flex;gap:8px;justify-content:center;padding:4px 10px}
.dlg .dcap{font-size:10px;color:var(--dim);text-align:center;padding:0 10px 4px}
.small{font-size:11px;color:var(--dim)}
.hidden{display:none !important}
/* A badge is page chrome. Every word INSIDE one that names a state comes out of
   the dump's own `mock` block or the seam log's own tokens. */
.badge{display:inline-block;font-size:9px;letter-spacing:.06em;text-transform:uppercase;
  border:1px solid #4a4a4a;color:#9a9a9a;border-radius:3px;padding:0 3px;margin-left:4px;
  vertical-align:middle;white-space:nowrap}
.badge.mock{border-color:#8a6ab0;background:#2a2036;color:#dcc6f2}
.badge.sup{border-color:#4a4a4a;background:#232323;color:#9a9a9a}
#stagehead .stalebanner{color:var(--warn);font-weight:600;margin-left:8px}
#stagehead .stalebanner button{margin-left:6px}
.badge.nohover{border-color:#7a6a3a;background:#2a2620;color:#cbb782}
.badge.disagree{border-color:#a05a5a;background:#2e2020;color:#e0a0a0}
#rail .s.stale{opacity:.5;font-style:italic}
#rail .tg{padding:4px 10px 1px 16px;font-size:10px;color:var(--dim);
  text-transform:uppercase;letter-spacing:.06em;border-top:1px solid #2a2a2a}
#rail .s .badge{margin-left:2px;flex:0 0 auto}
#stagehead{font-size:14px;color:#e6e6e6;margin:0 0 2px;display:flex;gap:8px;
  align-items:baseline;flex-wrap:wrap}
#stagehead .lab{font-size:11px;color:var(--dim)}
#stagehead .help{font-size:11px;color:var(--dim);border:1px solid #444;
  border-radius:50%;width:16px;height:16px;line-height:14px;text-align:center;
  cursor:help;display:inline-block}
.notebox{display:flex;gap:6px;align-items:flex-start;margin:8px 0;
  font-size:11px;color:var(--dim);max-width:900px}
.notebox textarea.nt{flex:1 1 auto;min-width:200px;height:52px;resize:vertical;
  background:#1b1b1b;color:var(--ink);border:1px solid #333;border-radius:3px;
  font:12px/1.4 inherit;font-family:inherit;padding:4px 6px}
.notebox .saved{color:var(--ok);min-width:40px;padding-top:4px}
.notebox .nostore{color:var(--warn);padding-top:4px}
#rail .s .dot{flex:0 0 6px;width:6px;height:6px;border-radius:50%;
  margin-left:auto;align-self:center}
#rail .noted .dot{background:var(--ok)}
#rail .w .dot{flex:0 0 6px;width:6px;height:6px;border-radius:50%;align-self:center;
  margin-left:auto}
#rail .w .dot+.n{margin-left:6px}
#rail .s.more{color:var(--accent);font-size:11px;font-style:normal}
#notesPanel{background:#1b1b1b;border:1px solid #2c2c2c;border-radius:4px;
  padding:8px 10px;margin:0 0 10px;font-size:12px}
#notesPanel .row{display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin:5px 0}
/* `user-select:all` is the fallback's fallback: a viewer that refuses the
   clipboard API still lets the reader click once and copy. */
#notesPanel pre{background:#111;border:1px solid #2a2a2a;border-radius:3px;
  padding:6px 8px;max-height:300px;overflow:auto;font:11px Consolas,monospace;
  color:#cfe0b8;white-space:pre-wrap;user-select:all}
#notesPanel textarea{width:100%;height:78px;background:#111;color:#cfe0b8;
  border:1px solid #2a2a2a;border-radius:3px;font:11px Consolas,monospace}
#notesPanel .saved{color:var(--ok)}
#notesPanel .nostore{color:var(--warn)}
#notesPanel .nrow{display:flex;gap:8px;align-items:baseline;padding:3px 4px;
  cursor:pointer;border-radius:3px}
#notesPanel .nrow:hover{background:#262626}
#notesPanel .nrow .nt1{color:var(--dim);flex:1;min-width:0;overflow:hidden;
  white-space:nowrap;text-overflow:ellipsis}
#notesPanel .nrow .vd{font-size:10px;text-transform:uppercase;color:#cfe0b8}
#notesPanel .nrow button{padding:0 6px}
#notesPanel details{margin-top:6px}
#notesPanel summary{cursor:pointer;color:var(--accent);font-size:11px}
.cmp .sumhead{background:#1b1b1b;border:1px solid #2c2c2c;border-radius:4px;
  padding:6px 10px;font-size:12px;margin:6px 0 12px}
.cmp .sumhead .num{font-family:Consolas,monospace;color:#c8d8a8}
.cmp .sumhead>div{margin-top:4px}
"""

# The bare-mode skin, kept apart from CSS so a test can assert that every
# selector in it is scoped to the `bare` class - which is what makes "the page
# opened without the hash is unchanged" a mechanical claim and not a promise.
BARE_CSS = """
/* Bare mode: exactly one capture's stage, at 1:1 CSS pixels, at the top-left of
   the page, with no rail, header, status line, echo strip or photograph, and no
   animation running. It is the surface `gui_mirror_fidelity.py` photographs, so
   that what gets measured is THIS page's own rendering rather than a second
   renderer written to imitate it. */
body.bare{background:#000;overflow:hidden}
body.bare #top,body.bare #rail,body.bare #status,body.bare #echo,
body.bare #sidewrap,body.bare #compareView,body.bare #mirrorView>p,
body.bare #stagehead,body.bare #statenote,body.bare #notesPanel{display:none}
body.bare #wrap{display:block}
body.bare #main{padding:0}
body.bare .sidebyside{display:block;gap:0}
body.bare .sidebyside h5{display:none}
body.bare .stagewrap{border:0;overflow:visible;max-width:none}
body.bare .stage{background:#000;background-image:none}
/* The ready gate, and the reason the instrument needs no second browser call to
   read the DOM marker: in bare mode the stage paints only once `data-ready` is
   set, so a screenshot taken before the page finished comes back BLANK instead
   of half-drawn, and a blank frame where the census frame has ink is a refusal
   the instrument can see. */
body.bare .stagewrap{visibility:hidden}
html[data-ready="1"] body.bare .stagewrap{visibility:visible}
body.bare *,body.bare *::before,body.bare *::after{animation:none;transition:none}
"""

JS = r"""
'use strict';
var M = window.__MIRROR__;
/* The clickable kinds and the seam's op names, both emitted by the generator so
   the JS, the CSS and the log parser cannot drift apart. */
var CLICK_KINDS = M.clickKinds;
var byId = {};
M.captures.forEach(function(c){ byId[c.id] = c; });
var S = {
  view: 'mirror',
  window: null, tab: null, state: null, mode: null,
  fixture: null, photo: 'off', boxes: true, foreign: false, capture: null,
  /* One window at a time (owner ruling): the review runs a window at a time, so
     the rail and Compare can be scoped to one, and a `#win=` link opens the page
     already scoped. Null means the whole page, which is the default. */
  focus: null,
  collapsed: {},
  /* Per window: whether the rail also lists the rows it folds by default. */
  showHidden: {}
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
/* The owner's per-state notes. Same guard discipline as the rail's fold set -
   every access in try/catch, and the page renders correctly with none of it -
   but a note is WORK rather than a convenience, so a write the browser refused
   says so next to the field and the Export panel repeats the warning. That is
   what makes "export before you close the tab" actionable instead of a surprise.
   One entry per (run pair, window, tab, state, mode, fixture). */
var NOTES_KEY = 'parsek-gui-mirror.notes';
var STORE_OK = true;
function loadNotes(){
  try {
    var raw = window.localStorage.getItem(NOTES_KEY);
    if (!raw) return {};
    var obj = JSON.parse(raw);
    return (obj && typeof obj === 'object' && !obj.length) ? obj : {};
  } catch (e) { STORE_OK = false; return {}; }
}
function saveNotes(){
  try {
    window.localStorage.setItem(NOTES_KEY, JSON.stringify(NOTES));
    STORE_OK = true;
  } catch (e) { STORE_OK = false; }
  paintNotesCount();
  return STORE_OK;
}
var NOTES = loadNotes();
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
/* A window title with the product's own name taken off the front. The prefix is
   the generator's, derived from the titles; this page types no part of it. */
function stripPrefix(t){
  var s = (t || '').trim(), p = (M.titlePrefix || '').trim();
  if (p && s.toLowerCase().indexOf(p.toLowerCase()) === 0){
    return s.slice(p.length).replace(/^[\s-]+/, '').trim();
  }
  return s;
}
function capsFor(win){ return M.captures.filter(function(c){ return c.window === win; }); }
/* The generator's `is_stale`: not the current picture of its state. */
function isStale(c){ return !!(c.hoverEmpty || c.supersededBy || c.retired || c.outdated); }
function status(msg, warn){
  var s = document.getElementById('status');
  s.textContent = msg || '';
  s.className = warn ? 'warn' : '';
}

/* ---- what a capture declares about itself ---- */
/* Five flags, none of them a judgement this page makes: MOCKED comes out of the
   dump's own provenance block, SUPERSEDED out of a later capture existing for
   the same key, RETIRED out of a newer run of the same lane not producing it, the
   hover one out of the pointer op's own reported tooltip (or,
   where the log predates that key, out of the capture's own tree being
   byte-identical to a sibling of the same run), and the last out of the label
   and the seam log saying different things about what was on screen. */
function capFlags(cap){
  var out = [];
  if (!cap) return out;
  if (cap.mocked){
    out.push({ cls: 'mock', text: 'MOCKED DATA', short: 'mock',
      title: 'a synthetic view model drove this draw: state ' + cap.mocked.stateId
             + ' of ' + cap.mocked.states + ' in catalogue ' + cap.mocked.catalogue
             + (cap.mocked.covers && cap.mocked.covers.length
                ? '; covers ' + cap.mocked.covers.join(', ') : '') });
  }
  if (cap.supersededBy){
    out.push({ cls: 'sup', text: 'superseded', short: 'old',
      title: 'a later run photographed this same key: ' + cap.supersededBy
             + '. Kept as the BEFORE of that pair; not counted as coverage.' });
  }
  if (cap.outdated){
    var had = cap.outdated.tabs || [], ep = cap.outdated.epoch, why = [];
    if (had.length) why.push('drawn while this window still had tabs it no longer has: '
                             + had.join(', '));
    if (ep) why.push('drawn before the current layout of this window (PR #' + ep.pr
                     + ', first captured ' + ep.utc + ')');
    out.push({ cls: 'sup', text: had.length ? 'old layout (had ' + had.join(', ') + ')'
                                            : 'old layout (before #' + ep.pr + ')',
      short: 'old layout',
      title: why.join('; ') + '. Kept as a BEFORE picture; not counted as coverage.' });
  }
  if (cap.retired){
    out.push({ cls: 'sup', text: 'no longer captured by ' + cap.retired.spec
               + ' since ' + cap.retired.since, short: 'retired',
      title: 'a newer complete run of ' + cap.retired.spec + ' did not '
             + 'photograph this state. Kept for reference; not counted as '
             + 'coverage.' });
  }
  if (cap.hoverEmpty){
    out.push({ cls: 'nohover', text: 'hover not captured', short: 'no hover',
      title: cap.hoverEmpty.why === 'log'
        ? 'the pointer op moved to ' + cap.hoverEmpty.at
          + ' and reported an empty tooltip, so this is the idle state under a'
          + ' hover label'
        : 'this frame is byte-identical to ' + cap.hoverEmpty.twin
          + ' in the same run, so the hover changed nothing' });
  }
  if (cap.disagrees && cap.disagrees.length){
    out.push({ cls: 'disagree', text: 'label disagrees with the log',
      short: 'label?',
      title: 'filed under the log, which is what was on screen. '
             + cap.disagrees.map(function(d){
                 return d.field + ': label "' + (d.label || '-')
                        + '", log "' + d.log + '"'; }).join('; ') });
  }
  return out;
}
/* The newest non-stale capture of the same key, or null. */
function currentOf(cap){
  var best = null;
  M.captures.forEach(function(c){
    if (c.key !== cap.key || c.window !== cap.window || isStale(c)) return;
    if (!best || (c.capturedUtc || '') > (best.capturedUtc || '')) best = c;
  });
  return best;
}
function appendFlags(host, cap, short){
  capFlags(cap).forEach(function(f){
    var b = el('span', 'badge ' + f.cls, short ? f.short : f.text);
    b.title = (short ? f.text + ' - ' : '') + f.title;
    host.appendChild(b);
  });
}
function flagWords(cap){
  return capFlags(cap).map(function(f){ return f.text; });
}

/* ---- the notes a round of review comes back through ---- */
function noteKey(runPair, win, tab, state, mode, fixture){
  return [runPair || '', win || '', tab || '', state || '', mode || '',
          fixture || ''].join('|');
}
/* The row shape is the generator's `NOTES_FIELDS`, forwarded in the model, so
   the blob this page writes and the blob the Python side round-trips cannot
   disagree about the field set. */
function noteRow(ctx, verdict, note){
  var row = {};
  M.notesFields.forEach(function(f){ row[f] = ''; });
  row.key = ctx.key;
  row.window = ctx.win || '';
  row.tab = ctx.tab || '';
  row.state = ctx.state || '';
  row.mode = ctx.mode || '';
  row.fixture = ctx.fixture || '';
  row.mocked = !!ctx.mocked;
  row.mockState = ctx.mockState || '';
  row.beforeId = ctx.beforeId || '';
  row.afterId = ctx.afterId || '';
  row.verdict = verdict || '';
  row.note = note || '';
  return row;
}
function stateCtx(cap){
  return {
    key: noteKey(cap.runId + ' -> ' + cap.runId, cap.window, cap.tab, cap.state,
                 cap.mode, cap.fixture),
    win: cap.window, tab: cap.tab, state: cap.state, mode: cap.mode,
    fixture: cap.fixture, mocked: !!cap.mocked,
    mockState: cap.mocked ? cap.mocked.stateId : '',
    beforeId: cap.id, afterId: cap.id
  };
}
function pairCtx(info){
  var b = byId[info.before], a = byId[info.after];
  if (!a) return null;
  return {
    key: noteKey((b ? b.runId : '') + ' -> ' + a.runId, a.window, a.tab, a.state,
                 a.mode, a.fixture),
    win: a.window, tab: a.tab, state: a.state, mode: a.mode, fixture: a.fixture,
    mocked: !!a.mocked, mockState: a.mocked ? a.mocked.stateId : '',
    beforeId: b ? b.id : '', afterId: a.id
  };
}
/* ---- how a state reads to a person ---- */
/* Tokens as words: `tooltip-logistics` reads "tooltip logistics", a tab reads
   by the name its own grid drew (the token where no capture named it), and the
   mode token is capitalised rather than retyped. */
function modeWord(m){ return m ? m.charAt(0).toUpperCase() + m.slice(1) : ''; }
function tabName(win, tab){
  if (!tab) return '';
  var w = M.windows.filter(function(x){ return x.token === win; })[0] || {};
  var t = (w.tabs || []).filter(function(x){ return x.token === tab; })[0];
  return (t && t.name) ? t.name.replace(/<[^>]*>/g, '') : tab;
}
/* A window by the name its own title bar draws (the generator's
   `window_display_names`), the seam token where no capture named it. The token
   stays the key of every link, note and lookup. */
function winName(win){
  var w = M.windows.filter(function(x){ return x.token === win; })[0];
  return (w && w.name) || win || '';
}
/* A state by the control it stands for where the dumps show one (the
   generator's `state_display_names`), else its seam token as words. */
function stateWords(win, tab, state, mode){
  if (!state) return '';
  var k = [win || '', tab || '', state, mode || ''].join('|');
  var named = (M.stateNames || {})[k];
  return named || state.replace(/-/g, ' ');
}
function stateLabel(win, tab, state, mode, hideTab){
  var parts = [hideTab ? '' : tabName(win, tab), stateWords(win, tab, state, mode),
               modeWord(mode)];
  return parts.filter(Boolean).join(' - ') || winName(win);
}
function railNoteKey(win, tab, state, mode){
  return [win || '', tab || '', state || '', mode || ''].join('|');
}
function notedSet(){
  var out = {};
  Object.keys(NOTES).forEach(function(k){
    var r = NOTES[k] || {};
    out[railNoteKey(r.window, r.tab, r.state, r.mode)] = 1;
    out['w|' + (r.window || '')] = 1;
  });
  return out;
}
/* The rail's dots and the button's count, repainted in place: rebuilding the
   rail from a blur handler would detach the row a reader is in the middle of
   clicking, and the click would be lost. */
function paintNoteDots(){
  var noted = notedSet();
  Array.prototype.forEach.call(document.querySelectorAll('#rail [data-nk]'),
    function(n){ n.classList.toggle('noted', !!noted[n.dataset.nk]); });
}
function paintNotesCount(){
  var b = document.getElementById('btnNotes');
  if (b) b.textContent = 'Notes (' + Object.keys(NOTES).length + ')';
}
function notesBox(ctx){
  var d = el('div', 'notebox');
  if (!ctx) return d;
  var rec = NOTES[ctx.key] || {};
  var sel = document.createElement('select');
  sel.setAttribute('aria-label', 'verdict');
  [''].concat(M.noteVerdicts).forEach(function(v){
    var o = document.createElement('option');
    o.value = v;
    o.textContent = v || 'verdict';
    sel.appendChild(o);
  });
  sel.value = rec.verdict || '';
  var inp = document.createElement('textarea');
  inp.className = 'nt';
  inp.value = rec.note || '';
  inp.placeholder = 'notes on this state';
  inp.setAttribute('aria-label', 'notes on this state');
  var st = el('span', 'saved', '');
  var timer = null, dirty = false;
  function commit(){
    if (timer){ clearTimeout(timer); timer = null; }
    dirty = false;
    var row = noteRow(ctx, sel.value, inp.value);
    if (!row.verdict && !String(row.note).replace(/\s+/g, '')){
      delete NOTES[ctx.key];
    } else {
      NOTES[ctx.key] = row;
    }
    var ok = saveNotes();
    st.className = ok ? 'saved' : 'nostore';
    st.textContent = ok
      ? 'saved'
      : 'this browser refused storage - export before you close the tab';
    paintNoteDots();
    paintNotesPanel();
  }
  sel.onchange = commit;
  /* Saved as it is typed, half a second after the last key, and on the way out
     of the field if a save is still pending. */
  inp.oninput = function(){
    dirty = true;
    st.textContent = '';
    if (timer) clearTimeout(timer);
    timer = setTimeout(commit, 500);
  };
  inp.onblur = function(){ if (dirty) commit(); };
  d.appendChild(sel);
  d.appendChild(inp);
  d.appendChild(st);
  if (!STORE_OK){
    st.className = 'nostore';
    st.textContent = 'this browser refused storage - export before you close the tab';
  }
  return d;
}
function refreshStateNote(){
  var host = document.getElementById('statenote');
  var cap = byId[S.capture];
  if (!host || !cap) return;
  host.innerHTML = '';
  host.appendChild(notesBox(stateCtx(cap)));
}
/* After a delete, a clear or an import: every surface that shows a note. */
function afterNotesChange(){
  paintNotesCount();
  paintNoteDots();
  paintNotesPanel();
  if (S.view === 'compare') buildCompare();
  else refreshStateNote();
}

/* ---- export / import: the blob, and the two ways out of the page ---- */
function notesRowsFor(scope){
  return Object.keys(NOTES).sort().map(function(k){ return NOTES[k]; })
    .filter(function(r){ return !scope || r.window === scope; });
}
function notesBlob(scope){
  var rows = notesRowsFor(scope);
  return { schema: M.notesSchema, pageSchema: M.schema,
           generatedUtc: M.generatedUtc || '',
           scope: scope || 'all windows', count: rows.length, notes: rows };
}
function notesMarkdown(blob){
  var head = ['window','tab','state','mode','fixture','mocked','verdict','note'];
  var out = ['| ' + head.join(' | ') + ' |',
             '| ' + head.map(function(){ return '---'; }).join(' | ') + ' |'];
  (blob.notes || []).forEach(function(r){
    out.push('| ' + head.map(function(f){
      var v = (f === 'mocked') ? (r[f] ? 'yes' : '') : r[f];
      v = String(v == null ? '' : v).replace(/\|/g, '/').replace(/\n/g, ' ').trim();
      return v || '-';
    }).join(' | ') + ' |');
  });
  return out.join('\n');
}
function selectAllIn(node){
  try {
    var r = document.createRange();
    r.selectNodeContents(node);
    var s = window.getSelection();
    s.removeAllRanges();
    s.addRange(r);
  } catch (e) { /* the CSS `user-select:all` is the fallback's fallback */ }
}
function mergeNotes(text){
  var data;
  try { data = JSON.parse(text); }
  catch (e) { return { ok: false, msg: 'that is not JSON' }; }
  var rows = null;
  if (data && data.notes && data.notes.length != null) rows = data.notes;
  else if (data && data.length != null && typeof data !== 'string') rows = data;
  else if (data && data.key) rows = [data];
  if (!rows) return { ok: false, msg: 'no notes in it' };
  var merged = 0, dropped = 0;
  rows.forEach(function(r){
    if (!r || typeof r !== 'object'){ dropped++; return; }
    var key = String(r.key || '').replace(/^\s+|\s+$/g, '');
    if (!key){
      key = noteKey(r.runPair, r.window, r.tab, r.state, r.mode, r.fixture);
    }
    if (key.replace(/\|/g, '') === ''){ dropped++; return; }
    var row = {};
    M.notesFields.forEach(function(f){ row[f] = (f in r) ? r[f] : ''; });
    row.key = key;
    NOTES[key] = row;
    merged++;
  });
  var ok = saveNotes();
  return { ok: true, msg: 'merged ' + merged
    + (dropped ? ', dropped ' + dropped + ' row(s) with nothing to key on' : '')
    + (ok ? '' : ' (not stored: this browser refused localStorage)') };
}
function deleteNote(key){
  delete NOTES[key];
  saveNotes();
  afterNotesChange();
}
/* Clearing is the one irreversible thing on the page, so it asks first. A
   viewer that refuses confirm() refuses the clear with it. */
function clearAllNotes(){
  var n = Object.keys(NOTES).length;
  if (!n) return false;
  var yes = false;
  try {
    yes = window.confirm('Delete all ' + n + ' note(s)? Copy them out first if '
                         + 'you want to keep them.');
  } catch (e) { yes = false; }
  if (!yes) return false;
  Object.keys(NOTES).forEach(function(k){ delete NOTES[k]; });
  saveNotes();
  afterNotesChange();
  return true;
}
/* A note names the capture it was written on. Where that capture is not in
   this page (a later generation dropped it), the note's own facets pick the
   nearest one, and a window the page has no capture of is said out loud. */
function jumpToNote(r){
  var cap = byId[r.afterId] || byId[r.beforeId];
  if (S.focus && S.focus !== r.window) S.focus = null;
  if (cap){ select(cap, true); }
  else if (capsFor(r.window).length){
    go(r.window, r.tab || null, r.state || null, r.mode || null);
  } else {
    status('that note is about window "' + r.window
           + '", which this page has no capture of.', true);
    return;
  }
  if (S.view === 'compare') buildCompare();
}
function paintNotesPanel(){
  var host = document.getElementById('notesPanel');
  if (!host || host.classList.contains('hidden')) return;
  host.innerHTML = '';
  var rows = notesRowsFor('');
  var blob = notesBlob('');

  var bar = el('div', 'row');
  bar.appendChild(el('b', null, 'Notes (' + rows.length + ')'));
  var cp = el('button', 'ui', 'Copy all');
  bar.appendChild(cp);
  var fmt = document.createElement('select');
  fmt.setAttribute('aria-label', 'copy format');
  [['json', 'JSON blob'], ['md', 'markdown table']].forEach(function(p){
    var o = document.createElement('option');
    o.value = p[0]; o.textContent = p[1];
    fmt.appendChild(o);
  });
  fmt.value = S.notesFormat || 'json';
  fmt.onchange = function(){ S.notesFormat = fmt.value; paintNotesPanel(); };
  bar.appendChild(fmt);
  var clr = el('button', 'ui', 'Clear all');
  clr.disabled = !rows.length;
  clr.onclick = function(){ clearAllNotes(); };
  bar.appendChild(clr);
  var msg = el('span', S.notesMsg ? S.notesMsg.cls : 'small',
               S.notesMsg ? S.notesMsg.text : '');
  S.notesMsg = null;
  bar.appendChild(msg);
  host.appendChild(bar);

  var list = el('div', 'nlist');
  if (!rows.length){
    list.appendChild(el('div', 'small',
      'No notes yet. Pick a state in the rail and type in the box under it.'));
  }
  rows.forEach(function(r){
    var li = el('div', 'nrow');
    li.title = 'show this state';
    li.appendChild(el('b', null, r.window ? winName(r.window) : '-'));
    li.appendChild(el('span', null, stateLabel(r.window, r.tab, r.state, r.mode)));
    if (r.mocked) li.appendChild(el('span', 'badge mock', 'mock'));
    if (r.verdict) li.appendChild(el('span', 'vd', r.verdict));
    li.appendChild(el('span', 'nt1', String(r.note || '').split('\n')[0]));
    var x = el('button', 'ui', 'x');
    x.title = 'delete this note';
    x.setAttribute('aria-label', 'delete this note');
    x.onclick = function(ev){ ev.stopPropagation(); deleteNote(r.key); };
    li.appendChild(x);
    li.onclick = function(){ jumpToNote(r); };
    list.appendChild(li);
  });
  host.appendChild(list);

  /* The text Copy all puts on the clipboard, folded: it is the fallback when
     the clipboard is refused, not something to read. */
  var det = document.createElement('details');
  var ds = document.createElement('summary');
  ds.textContent = 'show the text';
  det.appendChild(ds);
  var pre = document.createElement('pre');
  pre.textContent = (S.notesFormat === 'md')
    ? notesMarkdown(blob)
    : JSON.stringify(blob, null, 1);
  det.appendChild(pre);
  host.appendChild(det);
  /* There is no download link on purpose: a viewer sandbox blocks one, and a
     blocked link is worse than a box you can select. */
  cp.onclick = function(){
    var done = function(ok){
      msg.className = ok ? 'saved' : 'nostore';
      msg.textContent = ok
        ? 'copied ' + blob.count + ' note(s)'
        : 'the clipboard was refused - the text below is selected, copy it by hand';
      if (!ok){ det.open = true; selectAllIn(pre); }
    };
    try {
      if (window.navigator && navigator.clipboard && navigator.clipboard.writeText){
        navigator.clipboard.writeText(pre.textContent)
          .then(function(){ done(true); }, function(){ done(false); });
      } else { done(false); }
    } catch (e) { done(false); }
  };
  if (!STORE_OK){
    host.appendChild(el('div', 'nostore',
      'This browser refused localStorage, so nothing typed here survives a '
      + 'reload. Copy the blob out before you close the tab.'));
  }

  var imp = document.createElement('details');
  var is = document.createElement('summary');
  is.textContent = 'import...';
  imp.appendChild(is);
  imp.appendChild(el('div', 'small',
    'paste a blob back to merge it; a row already here is replaced by key'));
  var ta = document.createElement('textarea');
  ta.setAttribute('aria-label', 'paste a notes blob');
  imp.appendChild(ta);
  var ib = el('button', 'ui', 'merge');
  ib.onclick = function(){
    var res = mergeNotes(ta.value);
    S.notesMsg = { text: res.msg, cls: res.ok ? 'saved' : 'nostore' };
    afterNotesChange();
  };
  imp.appendChild(ib);
  host.appendChild(imp);
}

/* ---- the address bar is the link ---- */
/* `#win=<token>&cap=<capture id>`, plus `&view=compare` and `&focus=1`. Kept in
   step with the page on every selection, so copying the address bar is how a
   view is shared; `focus=1` additionally scopes the rail to that one window.
   Written with replaceState, which some viewer sandboxes refuse - the page
   works the same without it. */
function focusLink(){
  return '#win=' + encodeURIComponent(S.window || '')
         + (S.capture ? '&cap=' + encodeURIComponent(S.capture) : '')
         + (S.view === 'compare' ? '&view=compare' : '')
         + (S.focus ? '&focus=1' : '');
}
function syncHash(){
  if (!S.capture) return;
  try { window.history.replaceState(null, '', focusLink()); }
  catch (e) { /* a sandboxed viewer; the page does not depend on it */ }
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
  /* A capture a LATER run photographed the same key of is not the current
     picture of that state, so a click never lands on it. It stays reachable as
     the BEFORE of its own Compare pair, which is how a re-flown lane retires its
     predecessor without a label being named anywhere. A capture its own lane no
     longer produces (RETIRED) and one drawn with an old layout of the window
     (OUTDATED) are left out the same way, so neither is ever a window's default.
     If every candidate is out (nothing else was ever photographed) the pool is
     left alone rather than emptied. */
  var live = pool.filter(function(c){ return !c.supersededBy && !c.retired && !c.outdated; });
  if (live.length) pool = live;
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
/* KSP draws a Unity rich-text subset in labels, and the dump carries the raw
   markup: the Kerbals outcome rows really do read `<b>Jebediah Kerman</b>`.
   Rendering it as text shows the tags; rendering it as HTML hands a control's own
   string the run of the page. So the four tags Unity actually supports are
   translated into spans on a whitelist and EVERY other character - including any
   tag that is not on it - lands as a DOM text node. `innerHTML` is never used. */
var RICH_TAG = /<(\/?)(b|i|color|size)(=([^>]*))?>/gi;
function richText(parent, text){
  var stack = [parent];
  var at = 0;
  RICH_TAG.lastIndex = 0;
  var m;
  while ((m = RICH_TAG.exec(text)) !== null){
    if (m.index > at){
      stack[stack.length-1].appendChild(
        document.createTextNode(text.slice(at, m.index)));
    }
    at = m.index + m[0].length;
    var closing = m[1] === '/', tag = m[2].toLowerCase(), arg = m[4] || '';
    if (closing){
      if (stack.length > 1) stack.pop();
      continue;
    }
    var sp = document.createElement('span');
    if (tag === 'b') sp.style.fontWeight = '700';
    else if (tag === 'i') sp.style.fontStyle = 'italic';
    else if (tag === 'color' && /^#[0-9a-f]{3,8}$|^[a-z]{3,20}$/i.test(arg)) {
      sp.style.color = arg;
    } else if (tag === 'size' && /^[0-9]{1,3}$/.test(arg)) {
      sp.style.fontSize = arg + 'px';
    }
    stack[stack.length-1].appendChild(sp);
    stack.push(sp);
  }
  if (at < text.length){
    stack[stack.length-1].appendChild(document.createTextNode(text.slice(at)));
  }
  return parent;
}
/* The recorded font DELTA (`fs` / `fw`, see compact_font): only a node whose
   style departs from the skin carries one, so everything else keeps the page's
   calibrated default. On a window root it is the TITLE's font. */
function applyFont(d, n){
  if (n.fs) d.style.fontSize = n.fs + 'px';
  if (n.fw){
    d.style.fontWeight = (n.fw === 'b' || n.fw === 'bi') ? '700' : '400';
    d.style.fontStyle = (n.fw === 'i' || n.fw === 'bi') ? 'italic' : 'normal';
  }
}
function renderNode(n, out, opts){
  opts = opts || {};
  /* `dis` dims a control that the frame gave us no colour for. Where it DID -
     every control with text - the sampled `fg` is already the greyed colour the
     game drew, and dimming it again put the Missions window's disabled interval
     field below the threshold of being visible at all. */
  var d = el('div', 'gn k-' + n.k + (n.s ? ' s-' + n.s : '') +
                   ((n.e === 0 && !n.fg) ? ' dis' : '') +
                   (n.t ? '' : ' notext'));
  d.style.left = n.x + 'px'; d.style.top = n.y + 'px';
  d.style.width = n.w + 'px'; d.style.height = n.h + 'px';
  if (n.bg && (n.k === 'box' || n.s === 'box' || n.k === 'button' ||
               n.k === 'buttongrid' || n.k === 'textfield' || n.k === 'window' ||
               (n.k === 'toggle' && n.s === 'button'))) {
    d.style.background = n.bg;
  }
  if (n.fg) d.style.color = n.fg;
  applyFont(d, n);
  if (n.k === 'toggle'){
    /* The box is empty and its MARK is drawn in CSS; a glyph typed here was the
       wrong shape. A toggle in the button style hides the box entirely and takes
       the pushed-in look instead - which is what KSP draws for that style. */
    d.appendChild(el('span','cb' + (n.v ? ' on' : '')));
    if (n.v) d.classList.add('on');
  }
  if (n.k === 'slider'){
    if (n.vt) d.classList.add('vt');
    if (n.bg){ d.style.background = n.bg; d.classList.add('sg'); }
    /* The handle at its MEASURED position. No measurement, no handle. */
    if (n.th && n.th[1] > 0){
      var th = el('div','th');
      if (n.vt){ th.style.top = n.th[0] + 'px'; th.style.height = n.th[1] + 'px'; }
      else { th.style.left = n.th[0] + 'px'; th.style.width = n.th[1] + 'px'; }
      /* Sampled off this capture's own frame - the face and its bevel. */
      if (n.tc) th.style.background = n.tc;
      if (n.te) th.style.borderColor = n.te;
      d.appendChild(th);
    }
  }
  if (n.k === 'buttongrid'){
    /* A selection grid reports only the selected item. The item NAMES come from
       the captures where each tab was in turn selected, and the widths from
       IMGUI's own equal-split rule, so the bar is still all capture. */
    /* Names from THIS capture (see the per-capture resolution in the generator),
       never from a global table: a stale heading from an older epoch would
       otherwise be drawn over a frame that says something else. */
    var tabs = opts.tabNames || [];
    if (tabs.length > 1){
      d.classList.add('grid');
      var seg = n.w / tabs.length;
      var runs = (n.gi && n.gi.length === tabs.length) ? n.gi : null;
      var fills = (n.gc && n.gc.length === tabs.length) ? n.gc : null;
      tabs.forEach(function(t, i){
        /* Selected by the grid's own RECORDED index when the dump carries one,
           else by TOKEN. Comparing names is what lost the marker when a name went
           stale; the token needs the seam's `tab=` line, which only covers the
           windows the seam names as tabbed. */
        var on = (typeof n.si === 'number') ? (i === n.si) : (t.token === opts.tab);
        var b = el('div','gi' + (on ? ' on' : ''));
        b.style.left = (i*seg) + 'px'; b.style.width = seg + 'px';
        if (fills) b.style.background = fills[i];
        var lab = richText(el('span','gl'), t.name);
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
  if (n.tx != null){
    /* The text run at its MEASURED offset: KSP's box style centres some of them
       and left-aligns others, and no rule the page could carry knows which. */
    d.style.justifyContent = 'flex-start';
    d.style.paddingLeft = n.tx + 'px';
  }
  if (n.t || (n.k === 'buttongrid' && n.tv)){
    var span = el('span', 'tx');
    richText(span, n.t || n.tv);
    d.appendChild(span);
  }
  if (n.p){ d.title = n.p; d.dataset.tip = n.p; }
  if (n.strip){ d.classList.add('strip'); d.dataset.strip = String(n.strip); }
  if (CLICK_KINDS.indexOf(n.k) >= 0){ d.classList.add('click'); d.dataset.click = '1'; }
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
    applyFont(t, r);
    w.appendChild(t);
    (r.c || []).forEach(function(ch){
      renderNode(ch, w, {win: cap.window, tab: cap.tab, tabNames: cap.tabNames});
    });
    host.appendChild(w);
  });
  /* The modal block carries a PHOTOGRAPH of the whole frame, so bare mode leaves
     it out: a measurement of the page's own rendering must not be handed a copy
     of the thing it is being measured against. */
  if (cap.dialog && opts.dialog !== false){
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
  if (cap.photo && cap.photo.src && cap.photo.whole){
    /* The WHOLE frame, captioned as such. A PopupDialog is a centred uGUI canvas
       outside every window rect, so there is no modal crop to take and no honest
       way to invent one. */
    var img = el('img');
    img.src = cap.photo.src;
    img.alt = cap.dialog.title || '';
    box.appendChild(img);
    box.appendChild(el('div','dcap',
      'the whole ' + cap.screen[0] + 'x' + cap.screen[1] + ' frame this modal '
      + 'stood on - a PopupDialog is uGUI and appears in no control tree'));
  } else {
    box.appendChild(el('div','dcap',
      'no frame inlined for this modal; its title and buttons are the seam\'s own '
      + 'report'));
  }
  /* Zero buttons reported means zero buttons. A fabricated OK would be the one
     control on this page that the census never saw. */
  if (cap.dialog.buttons.length){
    var row = el('div','db');
    cap.dialog.buttons.forEach(function(b){
      var btn = el('button','ui', b);
      btn.onclick = function(){ box.classList.add('hidden');
        status('the "' + (cap.dialog.title||'') + '" modal was dismissed with "'
               + b + '".'); };
      row.appendChild(btn);
    });
    box.appendChild(row);
  } else {
    box.appendChild(el('div','dcap',
      'the seam reported no buttons on this modal'));
  }
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
  span.textContent = '';
  if (text) richText(span, text);
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
          tabName(cap.window, node.dataset.tab) + ' / ' + cap.mode + '.');
    return;
  }
  var txt = norm(node.querySelector('.tx') ? node.querySelector('.tx').textContent : '');
  if (!txt) { flash(node, 'that control has no text to match a capture by.'); return; }

  /* The home window: the seam's first window, the launcher every other window
     opens from and the one the close affordance returns to. */
  var home = M.seamWindows[0];
  /* a launcher: the control names another window the census captured. Only the
     home window's controls launch windows. Elsewhere a control whose text spells
     a window's name (a view button of another window's filter row) is that
     window's own tab or state and is routed below, or flashed. */
  var wins = cap.window === home
    ? M.windows.filter(function(w){ return w.captureCount > 0; }) : [];
  for (var i=0;i<wins.length;i++){
    var w = wins[i];
    /* `M.titlePrefix` is the product's own name, derived by the generator from
       the run of leading words every window title shares - not a word typed
       into this page. */
    var names = [norm(w.token)].concat(w.titles.map(function(t){
      return norm(stripPrefix(t)); }));
    if (names.indexOf(txt) >= 0 && w.token !== cap.window){
      go(w.token, null, null, S.mode); return;
    }
  }
  /* The close affordance. `M.closeOp` is the seam's own op name, carried in the
     model rather than typed here: it happens to spell the same word as the
     button's label, and the no-typed-UI-text guard has to be able to tell seam
     vocabulary from window text. */
  if (txt === norm(M.closeOp)){
    if (cap.window !== home){ go(home, null, null, S.mode); }
    else { status('the window closes; nothing else was photographed behind it.'); }
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
  if (best){ select(best, true); return; }
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
  S.tab = tab; S.state = state; if (mode) S.mode = mode;
  var r = pick(win, tab, state, mode, S.fixture) ||
          pick(win, tab, state, null, S.fixture) ||
          pick(win, null, null, mode, S.fixture) ||
          pick(win, null, null, null, S.fixture);
  if (!r){ status('no capture for window ' + win + ' yet.', true); return; }
  /* Not a direct pick: select() compares what was asked with what was found
     and says which axes fell back. A caller that names one exact capture (a rail
     row, a note, a deep link) passes true and gets no such line. */
  select(r.cap, false);
}

/* Assigned in boot(); called from select() so the button can never disagree with
   the mode actually on screen. A window photographed in Advanced only used to
   leave it reading "basic" and stuck there. */
var paintMode = function(){};
function select(cap, exact){
  var want = { fixture: S.fixture, mode: S.mode, tab: S.tab, state: S.state };
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
  document.getElementById('sidebyside').classList.toggle('two', !!wantSide);
  /* The stage's one header line: what this capture IS in words, every flag it
     declares, and where it came from in small print. */
  var head = document.getElementById('stagehead');
  head.innerHTML = '';
  head.appendChild(el('b', null, winName(cap.window)));
  head.appendChild(el('span', null, stateLabel(cap.window, cap.tab, cap.state, cap.mode)));
  appendFlags(head, cap);
  /* A stale capture (superseded, retired, old layout, no hover) reached by a link
     or a Compare BEFORE is not the window as it is today: say so in words and
     offer the current capture of the same state, when there is one. */
  if (isStale(cap)){
    var cur = currentOf(cap);
    var ban = el('span', 'stalebanner', cur
      ? 'Not the current picture of this state (run ' + cur.runId + ' is).'
      : 'Not the current picture of this state; no current capture exists yet.');
    if (cur){
      var go2 = el('button', 'ui', 'show current');
      go2.onclick = function(ev){ ev.stopPropagation(); select(cur, true);
        if (S.view === 'compare') buildCompare(); };
      ban.appendChild(go2);
    }
    head.appendChild(ban);
  }
  var src = el('span', 'lab', cap.fixture + ', run ' + cap.runId);
  src.title = cap.label;
  head.appendChild(src);
  var help = el('span', 'help', '?');
  help.title = HELP_TEXT;
  head.appendChild(help);
  refreshStateNote();
  /* Every axis the request fell back on, not just the dataset: asking for a Basic
     capture and silently getting an Advanced one is the same kind of lie. The
     line is otherwise empty; the header above already says what is shown. */
  var fell = [];
  if (cap.fixture !== want.fixture) fell.push('dataset "' + want.fixture + '"');
  if (want.mode && cap.mode !== want.mode) fell.push('mode "' + want.mode + '"');
  if (want.tab && cap.tab !== want.tab) fell.push('tab "' + tabName(cap.window, want.tab) + '"');
  if (want.state && cap.state !== want.state) fell.push('state "' + want.state + '"');
  if (fell.length && !exact){
    status('nothing photographed for ' + fell.join(', ')
           + '; showing the nearest capture there is.', true);
  } else { status(''); }
  paintMode();
  buildRail();
  paintNotesPanel();
  syncHash();
}
var HELP_TEXT = 'Hovering a control puts its real tooltip in the strip under the '
  + 'picture and on the element itself. A click switches to the capture of that '
  + 'state where the census produced one; where it did not, the control flashes '
  + 'and the line above the picture says so. Nothing on this page is drawn from '
  + 'anything but a capture. Notes save in this browser as you type; the Notes '
  + 'button lists, copies and clears them. The address bar always links to the '
  + 'state on screen.';

/* ---- left rail: every window and state with a capture, and the gaps ---- */
/* Each window header is a disclosure toggle, not a shortcut: clicking it folds
   that window's captures away and selects nothing. Everything starts folded
   except the window being shown, and a capture selected from anywhere else - a
   launcher, a tab, the Compare view - unfolds its own window on the way in. */
function buildRail(){
  var rail = document.getElementById('rail');
  rail.innerHTML = '';
  var mocked = M.captures.filter(function(c){ return c.mocked; }).length;
  /* The page's statistics, in this one place. */
  var h = el('h2', null, 'Windows (' + M.captures.length + ' captures'
    + (mocked ? ', ' + mocked + ' mocked' : '')
    + ', ' + Object.keys(M.keys).length + ' distinct states)');
  h.title = M.fixtures.length + ' datasets: '
    + M.fixtures.map(function(f){ return f.key; }).join(', ');
  rail.appendChild(h);
  var noted = notedSet();
  var shown = M.windows.filter(function(w){ return w.captureCount > 0; });
  if (S.focus){
    shown = shown.filter(function(w){ return w.token === S.focus; });
    var all = el('div', 's', 'show every window');
    all.title = 'Leave the one-window focus this page was opened with.';
    all.onclick = function(){ S.focus = null; buildRail(); syncHash();
      if (S.view === 'compare') buildCompare(); };
    rail.appendChild(all);
  }
  shown.forEach(function(w){
    var open = !S.collapsed[w.token];
    var listId = 'rail-' + w.token;
    var row = el('div', 'w' + (w.token === S.window && S.view === 'mirror' ? ' sel' : ''));
    row.setAttribute('role', 'button');
    row.setAttribute('tabindex', '0');
    row.setAttribute('aria-expanded', open ? 'true' : 'false');
    row.setAttribute('aria-controls', listId);
    var here = (w.token === S.window);
    row.title = here
      ? (open ? 'Hide' : 'Show') + ' the ' + w.captureCount + ' captures of ' +
        winName(w.token)
      : 'Show the ' + winName(w.token) + ' window';
    /* Every title the window drew, where it drew more than one (a window
       titled by its subject), and the seam token the page keys it by. */
    row.title += ' (seam window ' + w.token
      + ((w.titles || []).length > 1 ? '; titled ' + w.titles.join(', ') : '') + ')';
    row.appendChild(el('span', 'caret' + (open ? ' open' : ''), '\u25b8'));
    row.appendChild(el('b', null, winName(w.token)));
    if (w.mockedCount){
      /* The mocked count BESIDE the real one, per window: "how much of this is
         real" has to be answerable without opening a capture. */
      var mb = el('span', 'badge mock', w.mockedCount + ' mocked');
      mb.title = w.mockedCount + ' of this window\'s ' + w.captureCount
                 + ' captures were driven by a synthetic view model';
      row.appendChild(mb);
    }
    var wdot = el('span', 'dot');
    row.dataset.nk = 'w|' + w.token;
    if (noted[row.dataset.nk]) row.classList.add('noted');
    row.appendChild(wdot);
    row.appendChild(el('span', 'n', String(w.captureCount)));
    /* A title click on a window that is NOT the one on screen shows it - that is
       what a reader means by clicking a window's name - and select() unfolds its
       list on the way in. On the window already shown there is nothing to switch
       to, so the click is the fold toggle, which is how the toggle stays usable
       at all. */
    function toggle(ev){
      if (ev) ev.stopPropagation();
      if (!here){
        go(w.token, null, null, S.mode);
        if (S.view === 'compare') buildCompare();
        return;
      }
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
    /* Rows that are not the current picture of a state - a hover the log says
       captured nothing, a capture a later run replaced, a state the seam knows
       and no lane photographed - fold behind one link per window. The row on
       screen is never folded away. */
    var seen = {};
    /* The generator's `rail_rows`: one row per state, the CURRENT capture of it
       where there is one, grouped by tab in the tab bar's order and running from
       the least drawn to the most within a tab. A model without it (a hand-built
       one) falls back to the current-first capture order. */
    var ordered = (M.railRows || {})[w.token];
    var rows = ordered
      ? ordered.map(function(r){ var c = byId[r.id];
                                 return c ? { c: c, g: r.g } : null; })
               .filter(Boolean)
      : capsFor(w.token).slice().sort(function(a, b){
          return (isStale(a) ? 1 : 0) - (isStale(b) ? 1 : 0);
        }).map(function(c){ return { c: c, g: null }; });
    /* A thin tab header between groups, only where the window has more than one
       group; it folds away with its rows when every one of them is hidden. */
    var groups = {};
    rows.forEach(function(r){ if (r.g !== null) groups[r.g] = 1; });
    var headed = Object.keys(groups).length > 1;
    var gcur = null, ghead = null, gshown = 0;
    function closeGroup(){
      if (ghead && !gshown) ghead.classList.add('hidden');
    }
    rows.forEach(function(r){
      var c = r.c;
      var k = [c.tab || '', c.state || '', c.mode || ''].join('|');
      if (seen[k]) return;
      seen[k] = 1;
      /* The rail lists CURRENT states only (owner, 2026-09-24): a superseded,
         retired, old-layout or no-hover capture is still a Compare BEFORE, but
         it is not a state of the window as it is today. */
      if (isStale(c)) return;
      if (headed && r.g !== gcur){
        closeGroup();
        gcur = r.g; gshown = 0;
        ghead = el('div', 'tg', r.g ? tabName(w.token, r.g) : '(no tab)');
        list.appendChild(ghead);
      }
      var sr = el('div', 's' + (c.id === S.capture ? ' sel' : '')
                       + (isStale(c) ? ' stale' : ''));
      sr.appendChild(el('span', null, stateLabel(w.token, c.tab, c.state, c.mode, headed)));
      appendFlags(sr, c, true);
      sr.title = 'dataset ' + c.fixture + ', run ' + c.runId + ' (' + c.label + ')'
        + (c.state ? '; seam state ' + c.state : '');
      sr.dataset.nk = railNoteKey(w.token, c.tab, c.state, c.mode);
      if (noted[sr.dataset.nk]) sr.classList.add('noted');
      sr.appendChild(el('span', 'dot'));
      sr.onclick = function(){
        select(c, true);
        if (S.view === 'compare') buildCompare();
      };
      gshown++;
      list.appendChild(sr);
    });
    closeGroup();
    rail.appendChild(list);
  });
}

function showView(){
  document.getElementById('mirrorView').classList.toggle('hidden', S.view !== 'mirror');
  document.getElementById('compareView').classList.toggle('hidden', S.view !== 'compare');
  document.getElementById('btnMirror').classList.toggle('on', S.view === 'mirror');
  document.getElementById('btnCompare').classList.toggle('on', S.view === 'compare');
  syncHash();
  if (S.view === 'compare') buildCompare();
}

/* ---- Compare: earliest capture of a key beside the latest ---- */
/* The keys of ONE window, which is the unit a reader compares in. Pure, and the
   only place the filter lives, so the test can assert it never returns a key
   belonging to another window. */
function compareRowsFor(win){
  var rows = [];
  Object.keys(M.keys).forEach(function(k){
    var info = M.keys[k];
    var cap = byId[info.after];
    if (!cap) return;
    if (cap.window !== win) return;
    if (M.seamWindows.indexOf(cap.window) < 0) return;
    /* A capture the log says photographed no hover is the window's idle state
       under a hover label, so a pair of it reports no change and occupies a row
       that reads as coverage. It keeps its rail row and its badge. */
    if (cap.hoverEmpty) return;
    rows.push({ k: k, info: info });
  });
  rows.sort(function(a,b){ return (b.info.changed?1:0) - (a.info.changed?1:0); });
  return rows;
}
/* One window's own header, in counts the generator measured. `fixture` is part
   of the pair key and a mocked capture sits in its own fixture, so a mocked
   BEFORE can only ever pair with a mocked AFTER - that isolation is structural,
   and this line is where a reader can SEE the two populations side by side. */
function summaryHead(win){
  var s = (M.windowSummaries || {})[win] || {};
  var d = el('div', 'sumhead');
  var line = el('div');
  line.appendChild(el('b', null, 'This window: '));
  var parts = [
    'states ' + (s.statesReal || 0) + ' real'
      + (s.statesMocked ? ' + ' + s.statesMocked + ' mocked' : ''),
    'changed ' + (s.changed || 0),
    'unchanged ' + (s.unchanged || 0),
    'new ' + (s['new'] || 0),
    'gone ' + (s.gone || 0),
    'captures ' + (s.captures || 0)
      + (s.superseded ? ' (' + s.superseded + ' superseded)' : '')
  ];
  if (s.hoverNotCaptured) parts.push('hover not captured ' + s.hoverNotCaptured);
  if (s.labelDisagreements) parts.push('label disagrees with the log '
                                       + s.labelDisagreements);
  line.appendChild(el('span', 'num', parts.join('   |   ')));
  d.appendChild(line);
  d.appendChild(el('div', 'small',
    'States are DISTINCT keys, not files - a capture a later run replaced is not '
    + 'a second state. NEW and GONE are read off spec RE-FLIGHTS ('
    + (s.reflownSpecs || 0) + ' of this window\'s specs flew more than once): the '
    + 'keys the newest run of a spec has and its oldest does not, and the '
    + 'reverse.'));
  if ((s.uncaptured || []).length){
    d.appendChild(el('div', 'small', 'known to the seam, never photographed: '
      + s.uncaptured.map(function(m){
          return (m.tab ? tabName(win, m.tab) : '-') + ' / ' + (m.mode || '-');
        }).join(', ')));
  }
  return d;
}
function buildCompare(){
  var host = document.getElementById('compareView');
  host.innerHTML = '';
  var win = S.window;
  if (M.seamWindows.indexOf(win) < 0){
    /* the GuiTree probe and anything else the seam cannot open */
    host.appendChild(el('p','small',
      'Compare covers the windows the command seam can open. "' + win +
      '" is a diagnostic surface, so it has no before/after to show. Pick a window '
      + 'in the rail.'));
    return;
  }
  host.appendChild(el('p','small',
    'Comparing the "' + win + '" window only - pick another in the rail to switch. '
    + 'BEFORE is the earliest capture of a (fixture, window, tab, state, mode, scene) '
    + 'key; AFTER is the latest. Both sides are drawn by the same generator off their '
    + 'own dump, so a layout difference on the page is a layout difference in the '
    + 'game. The notes are lifted from the repo records named beside them; the numbers '
    + 'are measured off the two dumps.'));

  var rows = {};
  rows[win] = compareRowsFor(win);

  /* The whole-program summary, kept as a fold under the window being compared:
     one row per window is context, not the thing being read. */
  var sumHost = document.createElement('details');
  var sumSum = document.createElement('summary');
  sumSum.textContent = 'every window at a glance';
  sumHost.appendChild(sumSum);
  var tb = el('table','sum');
  var hr = el('tr');
  ['Window','Changed','What (from the record)','PRs'].forEach(function(h){
    hr.appendChild(el('th',null,h)); });
  tb.appendChild(hr);
  var order = M.seamWindows.filter(function(w){
    return M.captures.some(function(c){ return c.window === w; }); }).sort();
  order.forEach(function(w2){
    var wrows = compareRowsFor(w2);
    var changed = wrows.some(function(r){ return r.info.changed; });
    var note = M.notes[w2] || {};
    var what = (note.changelog && note.changelog.length)
      ? note.changelog[0].title
      : (changed ? 'capture set differs; no CHANGELOG entry names this window'
                 : 'no change yet');
    var prs = (note.prs || []).map(function(p){ return '#' + p.pr; }).join(' ');
    var tr = el('tr');
    if (w2 === win) tr.className = 'here';
    var wc = el('td');
    var link = el('span','wlink', w2);
    link.onclick = function(){ S.window = w2;
      /* Following a summary row MOVES the focus rather than dropping it: the
         rail is filtered to the focused window, and leaving it behind would
         empty the rail. */
      if (S.focus) S.focus = w2;
      buildCompare(); buildRail(); syncHash(); };
    wc.appendChild(link);
    tr.appendChild(wc);
    var td = el('td', changed ? 'y' : 'n', changed ? 'yes' : 'no');
    tr.appendChild(td);
    tr.appendChild(el('td',null,what));
    tr.appendChild(el('td',null,prs || '-'));
    tb.appendChild(tr);
  });
  sumHost.appendChild(tb);

  [win].forEach(function(win){
    var sec = el('div','cmp');
    sec.appendChild(el('h3', null, winName(win)));
    sec.appendChild(summaryHead(win));
    sec.appendChild(noteBlock(win, rows[win]));
    if (!rows[win].length){
      sec.appendChild(el('div','small',
        'No key of this window has a capture the seam could pair - it was '
        + 'photographed in one run only, so there is no BEFORE to put beside it.'));
    }
    rows[win].forEach(function(r){
      var before = byId[r.info.before], after = byId[r.info.after];
      var pair = el('div','pair');
      var n = (r.info.all || []).length;
      pair.appendChild(sideBlock(
        r.info.changed ? 'BEFORE'
                       : (n > 1 ? 'UNCHANGED (' + n + ' captures, no difference)'
                                : 'UNCHANGED (one capture)'),
        before, r.info));
      if (r.info.changed) pair.appendChild(sideBlock('AFTER', after, r.info));
      var kline = el('div','small');
      kline.appendChild(document.createTextNode(
        r.k.split('|').filter(Boolean).join(' / ')));
      appendFlags(kline, after);
      sec.appendChild(kline);
      sec.appendChild(pair);
      if (r.info.measured) sec.appendChild(measuredBlock(r.info.measured));
      /* One line of the owner's own, per pair, keyed on THIS before/after: a
         verdict does not follow a picture he has not seen. */
      sec.appendChild(notesBox(pairCtx(r.info)));
    });
    host.appendChild(sec);
  });
  host.appendChild(sumHost);
}

function sideBlock(title, cap, info){
  var side = el('div','side');
  var h = el('h4', null, title + '  -  ' + (cap ? cap.runId + ' / ' + cap.label : '?'));
  appendFlags(h, cap);
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

/* ---- bare mode: the deep link the fidelity instrument photographs ---- */
/* `#cap=<capture id>&bare=1` renders exactly one capture's stage at 1:1 CSS
   pixels at the top-left, with the page's own chrome, photograph and animations
   off, and sets `data-ready` on <html> once the stage has painted, which is what
   the headless screenshot waits for.
   Everything below is reached only through the hash: `bootBare` returns false on
   a page opened without it and boot() then follows exactly the path it always
   did. That is the whole reason the instrument can claim it measures the real
   page - there is no second rendering path to drift. */
function parseHash(h){
  var out = {};
  String(h || '').replace(/^#/, '').split('&').forEach(function(p){
    if (!p) return;
    var i = p.indexOf('=');
    var k = (i < 0) ? p : p.slice(0, i);
    var v = (i < 0) ? '1' : p.slice(i + 1);
    if (!k) return;
    try { out[decodeURIComponent(k)] = decodeURIComponent(v); }
    catch (e) { out[k] = v; }
  });
  return out;
}
function bootBare(){
  var q = parseHash(window.location.hash);
  if (q.bare !== '1') return false;
  document.body.classList.add('bare');
  var stage = document.getElementById('stage');
  var cap = byId[q.cap];
  if (!cap){
    /* Named a capture that is not in this page: say which, and mark the document
       NOT ready, so the instrument fails loudly instead of measuring a blank. */
    document.documentElement.dataset.error = 'no capture "' + (q.cap || '') + '"';
    document.documentElement.dataset.ready = '0';
    return true;
  }
  renderCapture(cap, stage, { photo: 'off', dialog: false,
                              foreign: (q.foreign === '1') });
  /* The stage is pinned to the frame the dump was taken at, so a screenshot of
     it shares one coordinate system with the census PNG: pixel (x,y) here is
     pixel (x,y) there. renderCapture grows the stage to the widest root, which
     is right for a reader scrolling a window the instance could not fit and
     wrong for a measurement against a screen-sized frame. */
  stage.style.width = cap.screen[0] + 'px';
  stage.style.height = cap.screen[1] + 'px';
  document.documentElement.dataset.capture = cap.id;
  /* `&scroll=<px>` scrolls every scroll view on the stage before the page marks
     itself ready. It exists so that "content below the fold is REACHABLE" is a
     thing a screenshot can show rather than a claim about CSS: photograph one
     capture at 0 and at 400 and the rows on screen differ. The default is 0,
     which is the offset the census frame was taken at, so an ordinary
     measurement is unaffected. */
  var sc = parseInt(q.scroll, 10);
  if (sc > 0){
    Array.prototype.forEach.call(stage.querySelectorAll('.gn.k-scrollview'),
      function(sv){ sv.scrollTop = sc; });
    document.documentElement.dataset.scrolled = String(sc);
  }
  /* Two frames: one for layout, one for the paint. */
  requestAnimationFrame(function(){
    requestAnimationFrame(function(){
      document.documentElement.dataset.ready = '1';
    });
  });
  return true;
}

/* ---- boot ---- */
function boot(){
  if (bootBare()) return;
  var fixSel = document.getElementById('fixture');
  M.fixtures.forEach(function(f){
    var o = document.createElement('option');
    o.value = f.key;
    o.textContent = f.key + (f.key === M.mockFixture ? ' (mocked data)' : '');
    fixSel.appendChild(o);
  });
  /* The default dataset is PINNED by the generator, not derived here. It used to
     be derived - the fixture that photographed the most different windows at the
     Space Center - and that was right while every dataset was a real save; a
     ~300-state mocked gallery wins that contest outright and would silently
     become the page the owner opens. `default_fixture` excludes the mocked
     dataset from the contest and `--default-fixture` pins it by hand. */
  S.fixture = (FIX_ORDER.indexOf(M.defaultFixture) >= 0)
    ? M.defaultFixture : FIX_ORDER[0];
  fixSel.value = S.fixture;
  fixSel.onchange = function(){ S.fixture = fixSel.value;
    go(S.window, S.tab, S.state, S.mode);
    if (S.view === 'compare') buildCompare(); };
  S.mode = M.modes.indexOf('advanced') >= 0 ? 'advanced' : (M.modes[0]||null);

  var mb = document.getElementById('btnMode');
  paintMode = function(){ mb.textContent = 'mode: ' + (modeWord(S.mode) || '-'); };
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
  var nb = document.getElementById('btnNotes');
  nb.onclick = function(){
    var host = document.getElementById('notesPanel');
    var open = host.classList.contains('hidden');
    host.classList.toggle('hidden', !open);
    nb.classList.toggle('on', open);
    paintNotesPanel();
  };
  paintNotesCount();

  /* The capture the page OPENS on goes through the same ranking a click does,
     so it cannot be one a later run superseded. Taking the first in model order
     opened the page on the oldest capture of the main window, badged
     `superseded`, which is the one picture the mirror should never lead with. */
  var firstWin = capsFor('main').length
    ? 'main' : ((M.captures[0] || {}).window || null);
  var r0 = pick(firstWin, null, null, S.mode, S.fixture)
           || pick(firstWin, null, null, null, S.fixture);
  var first = (r0 && r0.cap) || capsFor('main')[0] || M.captures[0];
  var stored = loadCollapsed();
  if (stored){
    S.collapsed = stored;
  } else {
    M.windows.forEach(function(w){ S.collapsed[w.token] = true; });
  }
  /* The deep link: `#win=<token>`, optionally `&cap=<capture id>` (the exact
     state), `&view=compare` and `&focus=1` (scope the rail to that one window).
     The address bar carries it for whatever is on screen (syncHash), so it is
     what a chat message can carry. A token no capture is of is SAID rather than
     silently ignored - the whole page would otherwise look like the answer to a
     link that missed. `bare=1` never reaches here: bootBare() returned before
     this. */
  var q = parseHash(window.location.hash);
  var missed = null;
  var direct = false;
  if (q.win){
    if (M.captures.some(function(c){ return c.window === q.win; })){
      if (q.focus === '1') S.focus = q.win;
      S.window = q.win;
      var r = pick(q.win, null, null, S.mode, S.fixture)
              || pick(q.win, null, null, null, S.fixture);
      if (r) first = r.cap;
      if (byId[q.cap] && byId[q.cap].window === q.win){
        first = byId[q.cap];
        direct = true;
      }
      if (q.view === 'compare') S.view = 'compare';
    } else {
      missed = 'the link names window "' + q.win
               + '", which no capture in this page is of.';
    }
  }
  showView();
  select(first, direct || first.fixture === S.fixture);
  if (missed) status(missed, true);
}
document.addEventListener('DOMContentLoaded', boot);
"""


def click_kind_css():
    """The hover affordance, emitted from `CLICK_KINDS` so the CSS and the JS
    cannot disagree about which controls look clickable."""
    sel = ",".join(".gn.k-%s:hover" % k for k in CLICK_KINDS)
    return (".gn.click:hover{outline:1px solid var(--accent);outline-offset:-1px}\n"
            + sel + "{cursor:pointer}")


def render_html(model):
    head = [
        "<!doctype html>",
        '<html lang="en"><head><meta charset="utf-8">',
        '<meta name="viewport" content="width=device-width,initial-scale=1">',
        "<title>Parsek GUI mirror</title>",
        "<style>%s</style>" % (CSS.replace("%CLICK_CSS%", click_kind_css())
                               + BARE_CSS),
        "</head><body>",
        # The top bar carries the explore -> note -> export flow only. The page
        # statistics live in ONE place, the rail header; the dataset, mode and
        # other-mods preferences sit behind the options fold because the rail
        # already picks a concrete capture per row.
        '<div id="top">',
        "<h1>Parsek GUI mirror</h1>",
        '<button class="ui on" id="btnMirror">Mirror</button>',
        '<button class="ui" id="btnCompare">Compare</button>',
        '<button class="ui" id="btnPhoto">photo: off</button>',
        '<button class="ui hidden" id="btnBoxes">outlines on</button>',
        '<span class="sp"></span>',
        '<button class="ui" id="btnNotes">Notes (0)</button>',
        '<details id="opts"><summary>options</summary><div class="optpanel">',
        '<label title="The dataset a window name or a click prefers when it has to '
        'pick a capture. A rail row always opens its own.">dataset '
        '<select id="fixture"></select></label>',
        '<button class="ui" id="btnMode" title="The mode a window name or a click '
        'prefers when it has to pick a capture.">mode</button>',
        '<button class="ui" id="btnForeign" title="Also draw the windows of other mods '
        'that were on screen when the capture was taken.">other mods</button>',
        "</div></details>",
        "</div>",
        '<div id="wrap"><div id="rail"></div><div id="main">',
        '<div id="notesPanel" class="hidden"></div>',
        '<div id="mirrorView">',
        '<div id="stagehead"></div>',
        '<div id="status"></div>',
        '<div class="sidebyside" id="sidebyside">',
        '<div><h5>rendered from the control tree</h5>'
        '<div class="stagewrap"><div class="stage" id="stage"></div></div></div>',
        '<div id="sidewrap" class="hidden"><h5>the frame the tree was dumped on</h5>'
        '<div class="stagewrap"><div class="stage" id="sidestage"></div></div></div>',
        '</div>',
        '<div class="echo" id="echo"></div>',
        '<div id="statenote"></div>',
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
        "generatedUtc": model.get("generatedUtc", ""),
        "seamWindows": model["seamWindows"],
        "titlePrefix": model["titlePrefix"],
        "stateNames": model.get("stateNames") or {},
        "clickKinds": model["clickKinds"],
        "seamOps": model["seamOps"],
        "closeOp": model["closeOp"],
        "fixtures": model["fixtures"],
        # Defaulted rather than required, so a hand-built model (the fidelity
        # tool's own fixture) still renders a page.
        "defaultFixture": model.get("defaultFixture") or "",
        "mockFixture": model.get("mockFixture") or MOCK_FIXTURE,
        "notesSchema": model.get("notesSchema") or NOTES_SCHEMA,
        "notesFields": model.get("notesFields") or list(NOTES_FIELDS),
        "noteVerdicts": model.get("noteVerdicts") or list(NOTE_VERDICTS),
        "windows": model["windows"],
        "windowSummaries": model.get("windowSummaries") or {},
        "railRows": model.get("railRows") or {},
        "modes": model["modes"],
        "captures": model["captures"],
        "keys": model["keys"],
        "missing": model["missing"],
        "notes": model["notes"],
    }


def build_index(model):
    """The companion JSON: coverage without the geometry.

    Coverage is counted in DISTINCT KEYS, not files. The corpus carries 228 PNGs
    behind 134 distinct labels, so a file count reads as about 1.7x the coverage
    there is; and a capture a later run superseded, or one the log says
    photographed no hover, is not coverage at all.
    """
    per_window = {}
    for cap in model["captures"]:
        w = per_window.setdefault(cap["window"], {
            "captures": 0, "capturesMocked": 0, "superseded": 0, "retired": 0,
            "outdated": 0, "hoverNotCaptured": 0, "labelDisagreements": 0,
            "states": {}, "fixtures": {}})
        w["captures"] += 1
        if cap.get("mocked"):
            w["capturesMocked"] += 1
        if cap.get("supersededBy"):
            w["superseded"] += 1
        if cap.get("retired"):
            w["retired"] += 1
        if cap.get("outdated"):
            w["outdated"] += 1
        if cap.get("hoverEmpty"):
            w["hoverNotCaptured"] += 1
        if cap.get("disagrees"):
            w["labelDisagreements"] += 1
        k = "%s / %s / %s" % (cap["tab"] or "-", cap["state"] or "-", cap["mode"] or "-")
        row = w["states"].setdefault(k, {"captures": 0, "mocked": False,
                                         "superseded": 0})
        row["captures"] += 1
        if cap.get("mocked"):
            row["mocked"] = True
        if cap.get("supersededBy"):
            row["superseded"] += 1
        if cap.get("retired"):
            row["retired"] = cap["retired"]
        if cap.get("outdated"):
            row["outdated"] = row.get("outdated", 0) + 1
        if cap.get("hoverEmpty"):
            row["hoverNotCaptured"] = row.get("hoverNotCaptured", 0) + 1
        if cap.get("disagrees"):
            row["labelDisagreements"] = row.get("labelDisagreements", 0) + 1
        w["fixtures"][cap["fixture"]] = w["fixtures"].get(cap["fixture"], 0) + 1
    for tok, w in per_window.items():
        w["summary"] = (model.get("windowSummaries") or {}).get(tok) or {}
    caps = model["captures"]
    return {
        "schema": MIRROR_SCHEMA,
        "generatedUtc": model.get("generatedUtc", ""),
        "captureCount": len(caps),
        "mockedCaptureCount": len([c for c in caps if c.get("mocked")]),
        "distinctKeyCount": len(model["keys"]),
        "supersededCaptureCount": len([c for c in caps if c.get("supersededBy")]),
        "retiredCaptureCount": len([c for c in caps if c.get("retired")]),
        "retiredKeyCount": len([k for k, v in model["keys"].items()
                                if v.get("retired")]),
        "outdatedCaptureCount": len([c for c in caps if c.get("outdated")]),
        "outdatedKeyCount": len([k for k, v in model["keys"].items()
                                 if v.get("outdated")]),
        "layoutEpochs": model.get("layoutEpochs") or {},
        "hoverNotCapturedCount": len([c for c in caps if c.get("hoverEmpty")]),
        "labelDisagreementCount": len([c for c in caps if c.get("disagrees")]),
        "defaultFixture": model["defaultFixture"],
        "seamWindows": model["seamWindows"],
        "fixtures": [f["key"] for f in model["fixtures"]],
        "windows": per_window,
        "missing": model["missing"],
        "compare": {k: {"before": v["before"], "after": v["after"],
                        "changed": v["changed"],
                        "superseded": v.get("superseded") or [],
                        "retired": v.get("retired"),
                        "outdated": v.get("outdated")}
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
    ap.add_argument("--default-fixture", default="",
                    help="pin the dataset the page opens on; the default is the "
                         "widest REAL fixture and is never the mocked one")
    ap.add_argument("--stamp", default="",
                    help="the page's generation stamp; defaults to now (UTC). It "
                         "travels in the exported notes blob, so a verdict names "
                         "the corpus it was typed against")
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

    budget = int(args.budget_mb * 1024 * 1024)
    stamp = args.stamp or datetime.datetime.now(
        datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    model = build_model(dirs, scenarios, repo_root=args.repo,
                        with_photos=not args.no_photos,
                        budget=budget,
                        verbose=args.verbose,
                        stamp=stamp,
                        pin_fixture=args.default_fixture)
    html = render_html(model)
    size = len(html.encode("utf-8"))
    # Measured BEFORE the file is opened: an over-budget page that has already
    # been written is an over-budget page someone will open anyway.
    if size > budget:
        sys.stderr.write(
            "gui-mirror: REFUSED, %.2f MB over the %.0f MB budget - nothing written "
            "to %s. Re-run with a larger --budget-mb, or with --no-photos.\n"
            % ((size - budget) / 1048576.0, args.budget_mb, args.out))
        return 2
    with open(args.out, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(html)
    if args.index:
        with open(args.index, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(build_index(model), fh, indent=1, sort_keys=True)
    sys.stderr.write("gui-mirror: %d captures, %d windows -> %s (%.2f MB of %.0f MB)\n"
                     % (len(model["captures"]),
                        len([w for w in model["windows"] if w["captureCount"]]),
                        args.out, size / 1048576.0, args.budget_mb))
    return 0


if __name__ == "__main__":
    sys.exit(main())
