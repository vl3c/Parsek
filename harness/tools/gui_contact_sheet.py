#!/usr/bin/env python3
"""GUI-census contact sheet: one browsable page over a run's captured PNGs.

Given a ``results/<runId>_shots/`` directory (the harness's always-collect
artifact folder, fed by the ``CaptureScreenshot`` seam verb), writes
``index.html`` INSIDE that directory: every image as a grid cell with its
capture label as the caption, grouped by the label's scene prefix.

WHY A SECOND SHEET, next to ``tools/contact_sheet.py``. The V3 sheet is a
per-run VERDICT page - it lives at ``results/<runId>_contact.html`` and puts
small thumbnails next to the run's key log lines and verifier rows, so a reader
asks "did this run pass and does it look right". A GUI census asks a different
question: "show me every Parsek window at full size so I can redesign it". That
wants big images, label-grouped ordering, no log panel, and a page that sits in
the SAME directory as the PNGs so it can be opened directly (a file:// page one
directory up cannot be dropped into a viewer alongside its images without the
parent folder). THREE tools write pages over a run's artifacts and no two of them
write the same path: this one owns ``index.html`` inside a ``*_shots`` dir, the V3
sheet owns ``<runId>_contact.html`` and ``index.html`` at the results ROOT, and
``tools/gui_tree_view.py`` - which a census runs over the SAME ``*_shots`` dir,
right after this one - owns ``<label>.gui.html`` plus ``gui-tree-index.html``
there. The tree viewer's index carries that name for exactly this reason: it and
this sheet are the two tools pointed at one directory, so ``index.html`` had to
belong to one of them alone.

CONTRACTS (binding, inherited from the V3 sheet for the same reasons):
  - READ-ONLY over run artifacts. Writes exactly one file, ``index.html``, in
    the directory it was pointed at. Never touches a result JSON, a verifier or
    a classification, and is not part of the run flow at all (it is an operator
    tool run after the fact).
  - Self-contained static HTML: no external assets, no scripts, inline CSS.
  - Every dynamic string is HTML-escaped and every href/src additionally
    percent-quoted, so a capture label with a space or a '#' still resolves.
  - Safe on partial input: an absent directory, a directory with no images, and
    a non-image file mixed in are all normal readings, not errors.

Usage::

    python harness/tools/gui_contact_sheet.py <shots-dir>
    python harness/tools/gui_contact_sheet.py --run-id 2026-09-10_1234
    python harness/tools/gui_contact_sheet.py --run-id <id> --title "KSC census"

Stdlib only; ASCII only.
"""

from __future__ import annotations

import argparse
import html
import os
import sys
import urllib.parse
from typing import Dict, List, Optional, Sequence, Tuple

# Same extension set the harness's own screenshot selection uses
# (hlib.ARTIFACT_SCREENSHOT_EXTENSIONS). Duplicated as a literal rather than
# imported so this tool stays runnable from the tools dir with no sys.path
# surgery; the pairing is asserted by a unit cell.
IMAGE_EXTENSIONS: Tuple[str, ...] = (".png", ".jpg", ".jpeg")

# The generated page's filename. Fixed, not an option: the operator opens
# "<shots dir>/index.html" and a per-invocation name would make that a lookup.
# This tool is the SOLE owner of that name inside a shots dir; the tree viewer's
# batch index next door is "gui-tree-index.html" so the two never overwrite each
# other when a census runs both over one directory.
INDEX_FILENAME = "index.html"

# Scene prefixes the census labels use, in the order the page shows their
# sections. A label whose first dash-segment is not one of these lands in the
# trailing "other" group rather than being dropped - this is a viewer, and a
# label it does not model must still be visible.
SCENE_ORDER: Tuple[str, ...] = ("ksc", "flight", "map", "trackstation", "editor")
OTHER_GROUP = "other"


def label_of(name: str) -> str:
    """The capture label for an image filename: the basename without extension.

    The ``CaptureScreenshot`` verb names the file ``<label>.png``, so the label
    IS the stem. Kept as a named function because the grouping and the caption
    both depend on it and a future ``<label>_2.png`` collision suffix would be
    handled here."""
    base = os.path.basename(str(name or ""))
    dot = base.rfind(".")
    return base[:dot] if dot > 0 else base


def group_of(name: str) -> str:
    """Which section an image belongs to: its label's first dash-segment when
    that is a known scene, else ``other``."""
    head = label_of(name).split("-", 1)[0].lower()
    return head if head in SCENE_ORDER else OTHER_GROUP


def is_image_name(name: str) -> bool:
    """True for a filename this sheet renders. Case-insensitive on the
    extension (KSP writes ``.png``; a hand-dropped ``.PNG`` must still show)."""
    return str(name or "").lower().endswith(IMAGE_EXTENSIONS)


def list_images(shots_dir: str) -> List[str]:
    """Image filenames in ``shots_dir``, sorted by name. [] when the directory
    is absent or unreadable - both are normal readings for a run that captured
    nothing, never an error."""
    if not os.path.isdir(shots_dir):
        return []
    out: List[str] = []
    try:
        for name in sorted(os.listdir(shots_dir)):
            if is_image_name(name) and os.path.isfile(os.path.join(shots_dir, name)):
                out.append(name)
    except OSError:
        return out
    return out


def group_images(names: Sequence[str]) -> List[Tuple[str, List[str]]]:
    """``names`` partitioned into ``(group, names)`` sections.

    Section order is ``SCENE_ORDER`` then ``other``; within a section the
    incoming order is preserved (the caller sorts by name, which is what makes
    two runs of the same census produce the same page). EMPTY groups are
    dropped, so the page never shows a heading over nothing."""
    buckets: Dict[str, List[str]] = {}
    for name in names:
        buckets.setdefault(group_of(name), []).append(name)
    out: List[Tuple[str, List[str]]] = []
    for group in SCENE_ORDER + (OTHER_GROUP,):
        if buckets.get(group):
            out.append((group, buckets[group]))
    return out


_STYLE = """
body { font-family: Verdana, Arial, sans-serif; font-size: 13px; margin: 16px;
       background: #f7f7f7; color: #222; }
h1 { font-size: 18px; margin: 0 0 4px 0; }
h2 { font-size: 15px; margin: 24px 0 8px 0; padding-bottom: 4px;
     border-bottom: 1px solid #bbb; }
p.meta { color: #555; margin: 0 0 12px 0; }
p.empty { color: #777; font-style: italic; }
div.grid { display: flex; flex-wrap: wrap; gap: 12px; }
figure { margin: 0; background: #fff; border: 1px solid #bbb; padding: 6px; }
figure img { display: block; max-width: 620px; max-height: 420px;
             background: #333; }
figcaption { font-family: Consolas, monospace; font-size: 12px;
             margin-top: 4px; word-break: break-all; max-width: 620px; }
ul.toc { margin: 0 0 12px 0; padding-left: 18px; }
"""


def render_html(shots_dir_name: str, names: Sequence[str],
                title: Optional[str] = None) -> str:
    """The whole page, as a string. Pure: no filesystem access.

    ``shots_dir_name`` is used only in the header text; the image ``src`` values
    are BARE filenames because the page is written into the same directory as
    the images."""
    heading = title or ("GUI census - %s" % shots_dir_name)
    sections = group_images(names)
    parts: List[str] = []
    parts.append("<!DOCTYPE html>")
    parts.append('<html lang="en"><head><meta charset="utf-8">')
    parts.append("<title>%s</title>" % html.escape(heading))
    parts.append("<style>%s</style></head><body>" % _STYLE)
    parts.append("<h1>%s</h1>" % html.escape(heading))
    parts.append('<p class="meta">%d image(s) in %s</p>'
                 % (len(names), html.escape(shots_dir_name)))

    if not names:
        # Named cause rather than a blank page: the two ways this happens are a
        # run that captured nothing and a wrong directory, and a reader must be
        # able to tell them apart.
        parts.append('<p class="empty">no images in this directory - either the '
                     "run captured none (no CaptureScreenshot steps, or they all "
                     "failed) or this is not a _shots directory</p>")
        parts.append("</body></html>")
        return "\n".join(parts) + "\n"

    if len(sections) > 1:
        parts.append('<ul class="toc">')
        for group, group_names in sections:
            parts.append('<li><a href="#%s">%s</a> (%d)</li>'
                         % (urllib.parse.quote(group), html.escape(group),
                            len(group_names)))
        parts.append("</ul>")

    for group, group_names in sections:
        parts.append('<h2 id="%s">%s (%d)</h2>'
                     % (urllib.parse.quote(group), html.escape(group),
                        len(group_names)))
        parts.append('<div class="grid">')
        for name in group_names:
            href = urllib.parse.quote(name)
            parts.append('<figure><a href="%s"><img src="%s" alt="%s" '
                         'loading="lazy"></a><figcaption>%s</figcaption></figure>'
                         % (href, href, html.escape(label_of(name)),
                            html.escape(label_of(name))))
        parts.append("</div>")

    parts.append("</body></html>")
    return "\n".join(parts) + "\n"


def _write_text_atomic(path: str, text: str) -> None:
    # PID-suffixed tmp, the V3 sheet's rationale verbatim: two invocations
    # against the same directory must not interleave into a torn page.
    tmp = "%s.tmp.%d" % (path, os.getpid())
    with open(tmp, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    os.replace(tmp, path)


def generate(shots_dir: str, title: Optional[str] = None) -> Tuple[str, int]:
    """Write ``index.html`` into ``shots_dir``. Returns (path, image count)."""
    names = list_images(shots_dir)
    text = render_html(os.path.basename(os.path.abspath(shots_dir)), names, title)
    path = os.path.join(shots_dir, INDEX_FILENAME)
    _write_text_atomic(path, text)
    return path, len(names)


def _default_results_dir() -> str:
    return os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                        "results")


def main(argv: Optional[Sequence[str]] = None) -> int:
    ap = argparse.ArgumentParser(
        description="Write index.html over the PNGs in a run's _shots directory.")
    ap.add_argument("shots_dir", nargs="?", default=None,
                    help="the results/<runId>_shots directory to index")
    ap.add_argument("--run-id", default=None,
                    help="resolve results/<runId>_shots under harness/results")
    ap.add_argument("--results-dir", default=None,
                    help="override the results dir used with --run-id")
    ap.add_argument("--title", default=None, help="page heading")
    args = ap.parse_args(list(argv) if argv is not None else None)

    if args.shots_dir and args.run_id:
        sys.stderr.write("pass either a directory or --run-id, not both\n")
        return 2
    if args.run_id:
        results = args.results_dir or _default_results_dir()
        shots_dir = os.path.join(results, "%s_shots" % args.run_id)
    elif args.shots_dir:
        shots_dir = args.shots_dir
    else:
        sys.stderr.write("a shots directory or --run-id is required\n")
        return 2

    if not os.path.isdir(shots_dir):
        sys.stderr.write("no such directory: %s\n" % shots_dir)
        return 1
    path, count = generate(shots_dir, args.title)
    sys.stdout.write("wrote %s (%d image(s))\n" % (path, count))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
