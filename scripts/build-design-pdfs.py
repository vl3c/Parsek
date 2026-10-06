"""
build-design-pdfs.py - Regenerate the design-doc PDFs in docs/pdf/ from their
markdown sources.

The PDFs are linked from outside the repository, so each one is rebuilt IN
PLACE under its fixed file name (DOCS below). A doc that is added to DOCS gets a
new PDF; never rename an existing entry.

Pipeline: pandoc (GitHub-flavoured markdown -> standalone HTML, styled by
scripts/design-pdf.css) then a headless Chromium-family browser (Chrome, Edge or
Chromium) prints the HTML to an A4 PDF (the page size and margins come from the
stylesheet's @page rule). Long table cells and code lines wrap instead of
running off the page.

Usage:  python scripts/build-design-pdfs.py [name ...] [--browser PATH] [--keep-html DIR]
  name        limit the run to these PDF names (e.g. parsek-roadmap); default all
  --browser   browser executable; default: $PARSEK_PDF_BROWSER, then the first
              Chrome / Edge / Chromium found on PATH or in the usual install dirs
  --keep-html write the intermediate HTML files to DIR (for inspection)

Requires pandoc on PATH.
"""

import argparse
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
DOCS_DIR = REPO_ROOT / "docs"
PDF_DIR = DOCS_DIR / "pdf"
CSS = Path(__file__).resolve().parent / "design-pdf.css"

# PDF name -> markdown source under docs/. Names are fixed (external links).
DOCS = {
    "parsek-flight-recorder-design": "parsek-flight-recorder-design.md",
    "parsek-game-actions-and-resources-recorder-design": "parsek-game-actions-and-resources-recorder-design.md",
    "parsek-ghost-trajectory-rendering-design": "parsek-ghost-trajectory-rendering-design.md",
    "parsek-logistics-supply-routes-design": "parsek-logistics-supply-routes-design.md",
    "parsek-missions-design": "parsek-missions-design.md",
    "parsek-recording-finalization-design": "parsek-recording-finalization-design.md",
    "parsek-rewind-to-separation-design": "parsek-rewind-to-separation-design.md",
    "parsek-timeline-design": "parsek-timeline-design.md",
    "parsek-roadmap": "roadmap.md",
}

BROWSER_NAMES = ["chrome", "google-chrome", "google-chrome-stable", "chromium",
                 "chromium-browser", "msedge", "microsoft-edge"]
BROWSER_PATHS = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
    "/opt/pw-browsers/chromium",
]


def find_browser(explicit):
    for cand in [explicit, os.environ.get("PARSEK_PDF_BROWSER")]:
        if cand:
            return cand
    for name in BROWSER_NAMES:
        hit = shutil.which(name)
        if hit:
            return hit
    for path in BROWSER_PATHS:
        if os.path.isfile(path):
            return path
    return None


def title_of(md_path):
    with open(md_path, encoding="utf-8") as fh:
        for line in fh:
            if line.startswith("# "):
                return line[2:].strip()
    return md_path.stem


def build_one(name, md_rel, browser, html_dir):
    md = DOCS_DIR / md_rel
    html = html_dir / (name + ".html")
    pdf = PDF_DIR / (name + ".pdf")
    subprocess.run(["pandoc", "-f", "gfm", "-t", "html5", "--standalone",
                    "--metadata", "title=" + title_of(md),
                    "--css", CSS.as_uri(), str(md), "-o", str(html)], check=True)
    if pdf.exists():
        pdf.unlink()  # so a browser that writes nothing cannot leave the old PDF looking fresh
    profile = tempfile.mkdtemp(prefix="parsek-pdf-profile-")
    try:
        subprocess.run([browser, "--headless", "--disable-gpu", "--no-sandbox",
                        "--user-data-dir=" + profile, "--no-pdf-header-footer",
                        "--print-to-pdf=" + str(pdf), html.as_uri()],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    finally:
        shutil.rmtree(profile, ignore_errors=True)
    if not pdf.is_file():
        raise RuntimeError("browser wrote no PDF for " + name)
    print("  %-52s %8d bytes" % (pdf.name, pdf.stat().st_size))


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("names", nargs="*")
    ap.add_argument("--browser")
    ap.add_argument("--keep-html")
    args = ap.parse_args(argv)

    unknown = [n for n in args.names if n not in DOCS]
    if unknown:
        ap.error("unknown PDF name(s): %s (known: %s)" % (", ".join(unknown), ", ".join(DOCS)))
    if shutil.which("pandoc") is None:
        sys.exit("pandoc not found on PATH")
    browser = find_browser(args.browser)
    if browser is None:
        sys.exit("no Chrome / Edge / Chromium found; pass --browser or set PARSEK_PDF_BROWSER")

    PDF_DIR.mkdir(parents=True, exist_ok=True)
    html_dir = Path(args.keep_html) if args.keep_html else Path(tempfile.mkdtemp(prefix="parsek-pdf-"))
    html_dir.mkdir(parents=True, exist_ok=True)
    print("browser: " + browser)
    try:
        for name in args.names or list(DOCS):
            build_one(name, DOCS[name], browser, html_dir)
    finally:
        if not args.keep_html:
            shutil.rmtree(html_dir, ignore_errors=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
