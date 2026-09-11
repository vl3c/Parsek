# Parsek architecture viewer

A small, dependency-free tool that shows the Parsek codebase as a handful of
modules and the references between them, and reports (but never fails on)
project-declared dependency boundaries. It is modelled on Robert C. Martin's
`arch-view` and `dependency-checker`, adapted from Clojure namespaces to C#
folders and file-name groups.

Everything lives in `scripts/arch/`:

| File | Role |
| --- | --- |
| `modules.toml` | The only hand-authored input: maps source paths to module names. |
| `archview.py` | Extracts the graph, writes `edges.json` and the three views, and prints the `--check` report. |
| `test_archview.py` | Unit tests plus a smoke test over the real `Source/Parsek` tree. |

The generated views live in `docs/dev/arch/` and are committed:

| File | View |
| --- | --- |
| `edges.json` | The raw data: modules with file count and metrics, and every edge with weight, cycle flag, referencing files and referenced type names. |
| `modules.dot`, `modules.svg` | Graphviz layered dependency graph of the production modules. |
| `matrix.html` | Dependency structure matrix. |
| `explore.html` | Interactive neighbourhood explorer. |

## What the scan is, and is not

This is a source-text scan, not a compiler. For each file it:

1. strips block comments, line comments, and string / char literals;
2. collects declared type names (`class` / `struct` / `interface` / `enum`);
3. matches capitalized identifiers (4+ characters) against the table of
   declared type names, and counts each distinct cross-module type as one
   reference.

That means it can **miss** a real dependency that is not spelled literally (an
alias, a type only reached through `nameof`, generic inference, or reflection)
and can **over-count** a name that happens to match a type but is used as
something else. Stripping strings and comments removes the common false
positives, and the folder granularity absorbs the rest. Treat the map as a
useful approximation, never as proof that a dependency does or does not exist.

The checker is **report-only**: it always exits 0, and nothing in the build or
the test suite gates on it. A gating version must derive edges from a real
semantic model (Roslyn), because a text scan cannot see conditional
compilation, partial classes across files, or references built through
reflection.

## Regenerating

From the repo root:

```bash
python scripts/arch/archview.py            # writes the five files in docs/dev/arch/
python scripts/arch/archview.py --check    # same, plus the report on stdout
python -m unittest scripts/arch/test_archview.py
```

Options:

- `--source DIR` (default `Source/Parsek`)
- `--out DIR` (default `docs/dev/arch`)
- `--min-edge N` (default 8): edges lighter than N references are hidden from
  the dot view; the explorer slider starts at N but can go down to 1.
- `--modules FILE` (default `scripts/arch/modules.toml`)

Requirements: Python 3.11+ (the TOML reader is `tomllib`) and, for the SVG
only, Graphviz `dot` on PATH. If `dot` is missing the script prints a warning
and still writes the other four files. When a source file matches no rule in
`modules.toml`, the script warns and skips it.

`--check` prints, in order: the per-module metrics table (name, files, fan-out,
fan-in, instability, sorted by fan-in + fan-out), every two-way coupling
between production modules with both weights, and every `[forbidden]` edge that
exists with its weight and referencing files. When `[allowed]` is non-empty it
also lists every production edge not in that list; while `[allowed]` is empty
that section is skipped. The last line is always `ARCH-CHECK report-only`.

## Reading the views

**`modules.dot` / `modules.svg`** - a top-to-bottom layered graph of the
production modules. An arrow A -> B means code in A references types declared
in B. Node label is the module name and its file count; arrow thickness scales
with the reference count; an arrow drawn in red is part of a two-way cycle
(A and B reference each other). Edges below `--min-edge` and the tooling
modules are hidden.

**`matrix.html`** - a dependency structure matrix. Rows are "from" modules and
columns are "to" modules, both in the same order: instability ascending, so
sinks (pure consumers such as `LogIO`) come first. Cell value is the reference
count; darker means more references. A red outline marks a pair of modules
that reference each other directly (a cycle). The diagonal shows the file
count, the rightmost column the fan-out and instability, and the bottom row
the fan-in.

**`explore.html`** - the interactive view. All production modules are shown;
click one to dim everything except that module, its direct in-edges and
out-edges, and to fill the side panel with each neighbour, the weight, and the
referenced type names. "show tooling modules" adds the test and tool modules;
the "min edge weight" slider hides light edges. This is the only view with an
external resource: Cytoscape.js is loaded from cdnjs, so the layout needs a
network connection (the data is still embedded in the file, and `edges.json`
plus `matrix.html` work offline).

## Editing `modules.toml`

Rules are read top to bottom and the first match wins.

- `folder = "X"` matches the first path segment under `Source/Parsek/`, so it
  applies to files in that folder and its subfolders.
- `prefix = "regex"` is matched against the file name of root-level files only.
- Keep a catch-all `prefix = ".*"` rule (`Core`) LAST; anything above it wins.
- `bin/`, `obj/` and `Properties/` are always skipped.

Add the most specific rules first, confirm with
`python scripts/arch/archview.py --check` that nothing is unclassified, and
mention module additions in the same commit as the code they cover.

The other sections:

- `[tooling] modules = [...]`: modules hidden from the production views and
  the metrics table. `explore.html` can still reveal them.
- `[forbidden] edges = ["A -> B", ...]`: declared boundaries that must not
  exist. The checker reports each one it finds.
- `[allowed] edges = [...]`: while empty, the allowlist section is skipped.
  Once any edge is listed, every production edge not listed is reported,
  which turns the checker into a strict allowlist without changing its
  report-only exit code.
