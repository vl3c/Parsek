# Parsek architecture viewer

A small, dependency-free tool that shows the Parsek codebase as a handful of
modules and the references between them, and reports (but never fails on)
project-declared dependency boundaries. It is modelled on Robert C. Martin's
`arch-view` and `dependency-checker`, adapted from Clojure namespaces to C#
folders and file-name groups.

Everything lives in `scripts/arch/`:

| File | Role |
| --- | --- |
| `modules.toml` | The hand-authored input that maps source paths to module names. |
| `atlas.toml` | The atlas prose: the lede, the section notes, the module summaries, the glossary, the upward-edge readings and the reading order (see "Editing the atlas prose"). |
| `archview.py` | Extracts the module graph and the type ladder, writes `edges.json`, `types.json` and the views, and prints the `--check` report. |
| `test_archview.py` | Unit tests plus a smoke test over the real `Source/Parsek` tree. |

The generated views are written to `docs/dev/arch/` and are gitignored (they
go stale on every commit that touches `Source/`; regenerate before reading):

| File | View |
| --- | --- |
| `edges.json` | The module data: file counts and metrics, and every module edge with weight, `cyclic` and `upward` flags, referencing files and referenced type names. |
| `types.json` | The type data: name, module, file, kind, modifiers, bases, enclosing, role, level, knot, sublevel, fan-in, fan-out, references to and references from; plus a top-level `levels` summary with the max level and the count per level. |
| `history.json` | The co-change history (see "Change history"): the window, per-file churn ranks, type hotspots, module commit counts, module-pair Jaccard, and the cross-module file pairs that keep changing together. |
| `modules.dot`, `modules.svg` | Graphviz layered dependency graph of the production modules. |
| `matrix.html` | Dependency structure matrix. |
| `explore.html` | Interactive neighbourhood explorer. |
| `ladder.html` | Interactive type ladder (modules as columns, levels as rows). |
| `atlas.html` | The Parsek Atlas: a prose reading over the live model (see "The atlas"). |
| `core-placement.md` | The catch-all placement evidence (see "Placing catch-all files"); written by `--place`. |

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

Nothing under `docs/dev/arch/` except this README is tracked. Run the script
first; the nine files it writes are the current truth for the checkout you
ran it in, and nothing else.

From the repo root:

```bash
python scripts/arch/archview.py            # writes the nine files in docs/dev/arch/
python scripts/arch/archview.py --check    # same, plus the report on stdout
python scripts/arch/archview.py --place    # same, plus core-placement.md (leaves it stale otherwise)
python -m unittest scripts/arch/test_archview.py
```

Options:

- `--source DIR` (default `Source/Parsek`)
- `--out DIR` (default `docs/dev/arch`)
- `--min-edge N` (default 8): edges lighter than N references are hidden from
  the dot view; the explorer slider starts at N but can go down to 1.
- `--modules FILE` (default `scripts/arch/modules.toml`)
- `--place` (default off): print the placement evidence for the current
  catch-all files and write `core-placement.md`, the before-phase placement
  table (see "Placing catch-all files"). Plain regeneration leaves that report
  alone, so run `--place` when its input changed.

Requirements: Python 3.11+ (the TOML reader is `tomllib`) and, for the SVG
only, Graphviz `dot` on PATH. If `dot` is missing the script prints a warning
and still writes the other eight files (`atlas.html` shows a one-line notice
where the map would be). When a source file matches no rule in
`modules.toml`, the script warns and skips it.

`--check` prints, in order: the per-module metrics table (name, files, fan-out,
fan-in, instability, sorted by fan-in + fan-out) followed by a line naming the
tooling modules hidden from it, the type names excluded as ambiguous, every
upward edge (a more stable module depending on a less stable one), every
two-way coupling between production modules with both weights, the 25 types
with the highest file-based fan-in, the count per type role with three example
names, the per-module level profile (type count per level, plus the module's
and the overall max level), the first 10 knots with their size and module
breakdown followed by the largest knot's hubs and greedy cut sequence, the
ATLAS prose-vs-model section (production modules without a summary, glossary
entries naming a missing type, stale glossary entries, readings for vanished
edges, missing reading-order types), the HISTORY co-change section (the window
and commit counts, the top 15 hotspots, the top 10 module pairs by Jaccard,
the top 15 cross-module file pairs, and one line per forbidden edge with how
many commits touched both of its sides), and every `[forbidden]` edge that
exists
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
them, not because they are more abstract in the usual sense. When a whole knot
sits at one level, that level's row is split into sub-rows (see "Knots"
below).

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
static cache or registry looks the same as a stateless helper. A class with
only fields, properties or expression-bodied properties **reads as `data`**,
because the method check wants a declaration shaped like `Type Name(` ...
(expression-bodied methods and properties with parentheses do match, but a
paren-less member set falls through even though it holds logic). A third,
rarer one: the entry-point attribute window is a plain 300 character lookback,
so a type declared just after an attributed type (a nested enum near the top of
an attributed class, for example) can be tagged `entry` without carrying the
attribute itself.

## Knots

A **knot** is a strongly connected component of the type graph with more than
one member: a group of types that reach one another in a cycle, directly or
through other members. Longest-path levelling condenses a cycle to one level,
so the kernel's 391-type knot collapses into a single level and only the types
above it (levels 8 to 10 here, mostly patches and entry points) rise above. A
knot is why that level is flat: the levelling cannot order types that depend on
each other.

`types.json` records this per type: `knot` is the 1-based index of the type's
component in largest-first order, or null outside a knot, and `sublevel` is
the type's level inside its knot after the cut edges are removed (again null
outside). On the current tree the knots are one of 391, then of 4 and 2.

`--check` first lists the first 10 knots (all three on the current tree) with
their size and module breakdown, then for the largest knot it shows the hubs
(the members other members reference most, with their in-knot references) and a
**greedy cut sequence**: each step picks, among the highest-fan-in members, the
sink whose outgoing references, when removed, break the knot the most, and
reports the references that were dropped. `391 -> cut ParsekLog -> 335` means
removing `ParsekLog`'s two references inside the knot (`ParsekSettings` and
`RecorderStateSnapshot`) splits off 56 members. The early cuts can be cheap
inversions - one or two references between hubs may hold whole regions
together, and those are the references a refactor should look at first - while
the later cuts usually touch many small references, and the named sink there
often needs a new abstraction (an interface, a data boundary) rather than moved
edges. The table is a research list, not a verdict - the tool never says a
reference is wrong, only that it holds a cycle together. Nothing in this phase
changes any dependency; it reports them.

`ladder.html` shows the same information in place. The row of a knot level is
split into sub-rows, top to bottom by descending sublevel (level 0 of the knot
at the bottom, so the bottom-up reading still holds), grouped by a brown
bracket labeled `knot N (size)`. A chip marked `X` is a sink from the cut
sequence; hover it to see the references it drops.

Sub-levels are a display order only: the cuts remove references from the
levelling used to draw the knot, nothing else. Levels of types outside the
knot do not change (the cut is a display device for ordering inside the knot,
not a claim about the code).

## Placing catch-all files

`modules.toml` used to end with a catch-all rule (`Core`) that collected every
root-level file no other rule claimed. That was a map maintenance queue, not a
module, and the placement policy below emptied it. The kernel guard has since
replaced the catch-all with an explicit list: `Core` now names the eight kernel
files, and an unplaced root file is reported and skipped rather than silently
joining the kernel. `--place` still scores the files in the last rule's module
and writes the phase's evidence to `core-placement.md`; for the historical
report it rebuilds the before-state with the old catch-all semantics. That
report is generated evidence - produced by `--place`, never edited by hand.

For every catch-all file the evidence line gives its declared type count (a
name the type graph drops because two modules declare it still counts here),
how many of those are in knot 1, the number of (referencing type, referenced
type) pairs arriving from other modules, that count split by module, the top
module's share, and the file's highest fan-in. The policy applies five rules
in order, and the first that fires wins:

| Rule | Condition | Outcome |
| --- | --- | --- |
| R1 | the file name starts with a family prefix (`GuiTree`, `GameState`, `ParsekConfig`/`ParsekSettings`/`SettingWhitelist`, `ParsekUI`, `MapRender`, the `Route*` family, the event/anchor family) | move to the family's module regardless of references; the family is a unit |
| R2 | `externalRefs >= 5` and (the top module owns at least half of them, or has at least twice the second module) | move to the top module |
| R3 | `externalRefs >= 5` and the top two modules reach 0.8 of them with neither alone at 0.6 | leave in the catch-all, tagged `SPLIT-CANDIDATE` with both names |
| R4 | no references from other modules | leave in the catch-all, tagged `ORPHAN` (dead, reflective, or reached only by Unity lifecycle) |
| R5 | anything else | leave in the catch-all, tagged `UNDECIDED`, with its evidence line |

R1 runs first and its moves are rebuilt into the map before R2-R5 are
evaluated, because moving a family out of the catch-all turns references from
those files into external references for everything left behind. R2 moves are
written as one exact-name rule per file (`^Name[.]cs$`, the bracket keeping the
TOML literal string readable) under a comment with its evidence, and R1 moves
as one family prefix rule per family; every phase-2 placement rule carries
`placement = "R1"` or `"R2"` (the hand placement later added `"R0"`) so the
report can rebuild the state before the phase. Files that stay behind are a
legitimate outcome: `UNDECIDED` means no rule fired - most such files sit below
the five-reference floor, and the rest have no owner with a majority or a
two-times lead. That is not a verdict on the file, and the report lists each
one with its evidence so a human can place it.

The phase that introduced this moved 48 of the 123 catch-all files (R1 31, R2
17); 75 remained (7 orphan, 68 undecided, no split candidates). `GuiTree` (the
GUI census recorder, added to `[tooling]`) and `Config` (settings types, a
production sink) are the two modules the move created.

A follow-up placed 67 of those 75 by hand as `placement = "R0"` rules (operator
decisions, each group commented with the reason in `modules.toml`): session
and revert machinery to Rewind, sidecar I/O and recorder policy to Recording,
playback and spawn support to Ghost, scene-level control including the
`WarpToTime` family to Controllers, dialogs and Unity-instantiated overlays to
UI, and in-game test support to a new `Harness` module in `[tooling]`. R0
rules sit above R1 and win on first match. The 8 files that stay in `Core`
are the kernel vocabulary (`BranchPoint`, `IPlaybackTrajectory`,
`VesselLaunchIdentity`, `VesselSpawner`, `MilestoneStore`,
`GroupHierarchyStore`, `InventoryManifest`, `PlaybackTrajectoryBoundsResolver`):
used across many modules, so no owner has a majority, and that is the intended
meaning of Core from here on.

## The atlas

`atlas.html` is the narrative page over the other views: the map inline, the
module directory grouped by layer, the top hub vocabulary, the largest knot
with its cut table, the weighted upward edges, and a reading order. Everything
numeric, tabular and visual is rendered from the live model by
`render_atlas_html`; the narrative prose comes from `scripts/arch/atlas.toml`,
while the structural strings (headings, tile captions and the generated-views
list) live in the renderer template.

### Editing the atlas prose

`atlas.toml` holds the lede, the note above each section, the module
summaries, the glossary meanings, the upward-edge readings, the reading order
and the instability bands for the module directory. The renderer looks up
prose by name:

- a production module with no `[modules.<Name>]` entry renders
  `(no summary yet)`;
- a top-18 hub with no `[glossary.<Type>]` entry renders `(no meaning yet)`;
- an `[[upward_readings]]` entry for an edge that no longer exists is simply
  not rendered;
- bands are matched top-down by the first `min` an instability reaches, so
  keep the `[bands]` entries ordered from the highest threshold down. The four
  in the file reproduce the current grouping: entry 0.70, feature 0.25, model
  0.18, floor 0.00.

Numbers written inside a prose sentence (a count in a module summary, for
example) are not checked; the generated numbers around them are. `--check`
does not silently show stale prose: its ATLAS section (after KNOTS) lists
production modules without a summary, glossary entries naming a type that is
not in the model, glossary entries outside the live top 18 (informational),
upward readings whose edge no longer exists, and reading-order types not in
the model.

## Change history

The module map says which files reference each other; `git log` says which
files actually change together. `history.json` is built from a single
`git log --no-merges --since=<18 months ago> --name-only -- Source/Parsek`
call: only `.cs` files count, and a commit touching more than 40 source files
is treated as a sweep (a rename or a bulk edit) and skipped, because it would
dominate every co-change number. The payload records the window, the kept
commit count, how many sweeps were skipped and the largest one, then four
tables:

- **files**: commits and last-touched date per production file, with a churn
  rank;
- **hotspots**: `file commits * fan-in` for every production type, top 30,
  which finds types that are both widely referenced and frequently edited;
- **modules** and **modulePairs**: commits touching each module, and for every
  production pair the number of commits touching both, the union, and their
  Jaccard ratio;
- **filePairs**: the top 40 cross-module file pairs with at least 5 shared
  commits - files the map calls separate that keep changing together.

Tooling modules are excluded everywhere. If git is missing or the window is
empty, the payload is empty and both the checker and the atlas say so instead
of failing. A co-change number is evidence, not a verdict: two files can share
commits because one change touches both sides of a real boundary, and that is
what the list is for.

`--check` prints the HISTORY section after ATLAS. The atlas has a "What
changes together" section between "Where the layering breaks" and "Reading
order" with the top 10 module pairs, top 10 cross-module file pairs and top 10
hotspots.

## Editing `modules.toml`

Rules are read top to bottom and the first match wins.

- `folder = "X"` matches the first path segment under `Source/Parsek/`, so it
  applies to files in that folder and its subfolders.
- `prefix = "regex"` is matched against the file name of root-level files only.
- `Core` is the kernel list (eight explicit file names) and MUST stay last; a
  new root file must get its own rule above it. An unplaced root file warns as
  `WARN unplaced root file: <name> (add a rule to modules.toml; Core is the
  kernel, not a catch-all)`; an unplaced folder file keeps the
  `WARN unclassified file` wording.
- `bin/`, `obj/` and `Properties/` are always skipped.

Add the most specific rules first, confirm with
`python scripts/arch/archview.py --check` that nothing is unplaced, and
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
