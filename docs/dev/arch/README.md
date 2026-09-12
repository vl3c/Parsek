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
| `archview.py` | Extracts the module graph and the type ladder, writes `edges.json`, `types.json` and the four views, and prints the `--check` report. |
| `test_archview.py` | Unit tests plus a smoke test over the real `Source/Parsek` tree. |

The generated views live in `docs/dev/arch/` and are committed:

| File | View |
| --- | --- |
| `edges.json` | The module data: file counts and metrics, and every module edge with weight, `cyclic` and `upward` flags, referencing files and referenced type names. |
| `types.json` | The type data: name, module, file, kind, modifiers, bases, enclosing, role, level, fan-in, fan-out, references to and references from. |
| `modules.dot`, `modules.svg` | Graphviz layered dependency graph of the production modules. |
| `matrix.html` | Dependency structure matrix. |
| `explore.html` | Interactive neighbourhood explorer. |
| `ladder.html` | Interactive type ladder (modules as columns, levels as rows). |

## What the scan is, and is not

This is a source-text scan, not a compiler. For each file it:

1. strips block comments, line comments, preprocessor directive lines
   (`#region ...`, `#if ...`), and string / char literals (code inside
   interpolation holes is kept);
2. collects declared type names (`class` / `struct` / `interface` / `enum`);
   a name declared in more than one module is dropped from the table and
   listed by `--check`, because text alone cannot say which one a use means;
3. matches capitalized identifiers (4+ characters) against the table of
   declared type names, skipping `Name(` when no `new` precedes it (a method
   call that shares a type's name), and counts each distinct cross-module
   type as one reference.

That means it can **miss** a real dependency that is not spelled literally (an
alias, a type only reached through `nameof`, generic inference, or reflection)
and can **over-count** a name that happens to match a type but is used as
something else (a property or enum member spelled like a type). Stripping
strings, comments and directives removes the common false positives, and the
folder granularity absorbs the rest. Treat the map as a useful approximation,
never as proof that a dependency does or does not exist.

The type ladder adds a second layer on the same stripped text. Each type's
declaration body is found by brace matching, a reference inside a nested type
belongs to the nested type only, and the parts of a partial class (same name,
same module) are merged: their modifiers and bases are unioned before the role
is decided, and their body references are unioned for the graph. A type name
declared in two different modules is dropped here too, because text alone
cannot say which one a use means. Roles, levels and the entry-point attribute
window are heuristics, not compiler facts (see "Reading the ladder").

The checker is **report-only**: it always exits 0, and nothing in the build or
the test suite gates on it. A gating version must derive edges from a real
semantic model (Roslyn), because a text scan cannot see conditional
compilation, partial classes across files, or references built through
reflection.

## Regenerating

From the repo root:

```bash
python scripts/arch/archview.py            # writes the seven files in docs/dev/arch/
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
and still writes the other six files. When a source file matches no rule in
`modules.toml`, the script warns and skips it.

`--check` prints, in order: the per-module metrics table (name, files, fan-out,
fan-in, instability, sorted by fan-in + fan-out), the type names excluded as
ambiguous, every upward edge (a more stable module depending on a less stable
one), every two-way coupling between production modules with both weights, the
25 types with the highest file-based fan-in, the count per type role with three
example names, the per-module level profile (type count per level, plus the
module's and the overall max level), and every `[forbidden]` edge that exists
with its weight, referenced types and referencing files. When `[allowed]` is
non-empty it also lists every production edge not in that list; while
`[allowed]` is empty that section is skipped. The last line is always
`ARCH-CHECK report-only`.

## Reading the views

**`modules.dot` / `modules.svg`** - a top-to-bottom layered graph of the
production modules. An arrow A -> B means code in A references types declared
in B. Node label is the module name and its file count; arrow thickness scales
with the reference count. Layers follow the stability gradient: instability
`I = fanOut / (fanOut + fanIn)` is high for modules that mostly depend on
others (top) and near zero for sinks that are mostly depended on (bottom).
Only the downward arrows constrain the layout. An arrow drawn in red runs
upward, from a more stable module to a less stable one, which the
stable-dependencies rule says should not happen; those are the arrows to look
at. "On a cycle" is still recorded per edge in `edges.json` (`cyclic`) but not
drawn, because the production graph is one large strongly connected component
and that flag marks nearly everything. Edges below `--min-edge` and the
tooling modules are hidden.

**`matrix.html`** - a dependency structure matrix. Rows are "from" modules and
columns are "to" modules, both in the same order: instability ascending, so
sinks (pure consumers such as `LogIO`) come first. Cell value is the reference
count; darker means more references. Because both axes are sorted by
instability, every cell above the diagonal is an upward edge (a more stable
row module depending on a less stable column module) and carries a red
outline; a clean layering would have an empty upper triangle. The diagonal
shows the file count, the rightmost column the instability, and the bottom
row the fan-in.

**`explore.html`** - the interactive view, self-contained inline SVG with no
library and no network access, so it opens from disk. Modules sit in rows by
instability band, unstable at the top and sinks at the bottom, the same
reading as the dot view. Click one to dim everything except that module, its
direct in-edges and out-edges, and to fill the side panel with each neighbour,
the weight, an "upward" tag where the edge runs against the stability
gradient, and the referenced type names. "show tooling modules" adds the test
and tool modules; the "min edge weight" slider hides light edges. Some
in-app file previews render HTML as a static snapshot without running
scripts; open the file in a browser for the interactive view.

**`ladder.html`** - see the next section.

## Reading the ladder

`ladder.html` lays every production type out as a grid: columns are modules
ordered by instability descending (the same order as the explorer rows), rows
are levels with the highest at the top, and a chip is one type. Read it
bottom-up: a chip at level N references types at levels below N (or in its own
cycle), so the vocabulary a newcomer needs first is at the bottom.

A **level** is the longest path from a type to a leaf over the in-repo
references in `types.json`; level 0 means the type references no in-repo type.
Cycles are condensed first, so every member of a cycle shares one level. A
level is a property of the dependency chain, not a quality judgment: the
logging core and the stores sit high because everything lines up underneath
them, not because they are more abstract in the usual sense.

A **role** is one letter in the chip glyph, assigned by the first matching
rule:

| Glyph | Role | Rule |
| --- | --- | --- |
| E | entry | a base is MonoBehaviour, ScenarioModule, PartModule or VesselModule, or `[KSPAddon` / `[HarmonyPatch` appears in the 300 characters before the header |
| I | interface | the declaration is an interface |
| A | abstract | the declaration has the `abstract` modifier |
| N | enum | the declaration is an enum |
| S | static | the declaration has the `static` modifier |
| M | implements | a base name starts with a capital I followed by a capital letter |
| D | data | a struct, or a class whose body has no method declaration |
| C | service | everything else |

The chip number is **fan-in**: the number of distinct files outside the
declaring file that mention the type, so a big file counts once.

Clicking a chip dims everything else and marks, with a blue outline, the types
it references, and with an orange outline, the types that reference it; the
side panel lists both groups by module and the filter box narrows the chips to
names containing a substring.

Two heuristics have known blind spots. A **static class with mutable state
still reads as `static`**, because the modifier is all the scan sees, so a
static cache or registry looks the same as a stateless helper. A **class whose
members are all expression-bodied may read as `data`**, because the method
check looks for a declaration shaped like `Type Name(` ...; a type with only
properties and `=>` members can fall through to `data` even though it holds
logic. A third, rarer one: the entry-point attribute window is a plain 300
character lookback, so a type declared just after an attributed type (a nested
enum near the top of an attributed class, for example) can be tagged `entry`
without carrying the attribute itself.

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
