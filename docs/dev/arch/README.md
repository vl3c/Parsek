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
| `atlas.toml` | The atlas prose: the lede, the section notes, the module summaries, the glossary, the upward-edge readings, the reading order, the main findings and the ranked opportunities (see "Editing the atlas prose"). |
| `archview.py` | Extracts the module graph and the type ladder, builds the co-change history, writes `edges.json`, `types.json`, `history.json` and the views, and prints the `--check` report. |
| `test_archview.py` | Unit tests plus a smoke test over the real `Source/Parsek` tree. |

The generated views are written to `docs/dev/arch/` and are gitignored (they
go stale on every commit that touches `Source/`; regenerate before reading):

| File | View |
| --- | --- |
| `edges.json` | The module data: file counts and metrics, and every module edge with weight, `cyclic` and `upward` flags, referencing files and referenced type names. |
| `types.json` | The type data: name, module, primary file, every file the type is declared in, kind, modifiers, bases, enclosing, role, level, knot, sublevel, fan-in, fan-out, references to and references from; plus a top-level `levels` summary with the max level and the count per level. |
| `history.json` | The co-change history (see "Change history"): the window, per-file churn ranks, type hotspots, module commit counts, module-pair Jaccard, and the cross-module file pairs that keep changing together. |
| `sizes.json` | The size view (see "Size view"): the largest files and types with their long methods, coroutines, static state (reassignable and readonly collections) and pure static pools, the growth column, and the rules that fired per type with a tier. |
| `modules.dot`, `modules.svg` | Graphviz layered dependency graph of the production modules. |
| `matrix.html` | Dependency structure matrix. |
| `explore.html` | Interactive neighbourhood explorer. |
| `ladder.html` | Interactive type ladder (modules as columns, levels as rows). |
| `atlas.html` | The Parsek Atlas: a prose reading over the live model (see "The atlas"). |
| `core-placement.md` | The historical catch-all placement evidence (see "Placing catch-all files"); written by `--place`. |

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
first; the ten files it writes (eleven with `--place`) are the current truth for
the checkout you ran it in, and nothing else.

From the repo root:

```bash
python scripts/arch/archview.py            # writes the ten files in docs/dev/arch/
python scripts/arch/archview.py --check    # same, plus the report on stdout
python scripts/arch/archview.py --place    # same, plus core-placement.md (leaves it stale otherwise)
python -m unittest scripts/arch/test_archview.py
```

The unit tests above are not run by CI (`.github/workflows/tests.yml`
discovers only the harness suites); run them by hand before pushing a change
to the tool.

Options:

- `--source DIR` (default `Source/Parsek`)
- `--out DIR` (default `docs/dev/arch`)
- `--min-edge N` (default 8): edges lighter than N references are hidden from
  the dot view; the explorer slider starts at N but can go down to 1.
- `--modules FILE` (default `scripts/arch/modules.toml`)
- `--place` (default off): print the placement evidence for the files in the
  module the last rule names (the Core kernel list now; the old catch-all in
  the historical report) and write `core-placement.md`, the before-phase
  placement table (see "Placing catch-all files"). Plain regeneration leaves
  that report alone, so run `--place` when its input changed.

Requirements: Python 3.11+ (the TOML reader is `tomllib`) and, for the SVG
only, Graphviz `dot` on PATH. If `dot` is missing the script prints a warning
and still writes the other nine files (`atlas.html` shows a one-line notice
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
many commits touched both of its sides), every `[forbidden]` edge that exists
with its weight, referenced types and referencing files, and last the SIZE
section (see "Size view"). When `[allowed]` is non-empty it also lists every
production edge not in that list, between the forbidden edges and SIZE; while
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
so the kernel's 301-type knot collapses into level 10 and only the types above
it (levels 11 to 13 here, mostly patches and entry points) rise above. A knot
is why that level is flat: the levelling cannot order types that depend on each
other.

`types.json` records this per type: `knot` is the 1-based index of the type's
component in largest-first order, or null outside a knot, and `sublevel` is
the type's level inside its knot after the cut edges are removed (again null
outside). On the current tree the knots are one of 301, then of 7, 4, 2 and 2
(re-derived 2026-09-25, when the stock-UI reservation layer was classified: it
added eight types to the kernel and the 7-type stock-screen decoration knot; it
was 286, 4, 2 and 2 on 2026-09-22; it was 391, 4 and 2 before the 2026-09-14 pass made
`ParsekLog` and `Recording` leaves).

`--check` first lists the first 10 knots (all five on the current tree) with
their size and module breakdown, then for the largest knot it shows the hubs
(the members other members reference most, with their in-knot references) and a
**greedy cut sequence**: each step picks, among the highest-fan-in members, the
sink whose outgoing references, when removed, break the knot the most, and
reports the references that were dropped. `301 -> cut GameAction -> 273` means
removing `GameAction`'s single reference inside the knot (`Ledger`) splits off
28 members. Some cuts are cheap inversions like that one - one or two
references between hubs holding a whole region together, and those are the
references a refactor should look at first - while the rest touch many small
references at once (the second cut, `RecordingStore`, drops 25), and the named
sink there often needs a new abstraction (an interface, a data boundary) rather
than moved edges. The table is a research list, not a verdict - the tool never says a
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
replaced the catch-all with an explicit list: `Core` now names the nine kernel
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
rules sit above R1 and win on first match. The 9 files that stay in `Core`
are the kernel vocabulary (`BranchPoint`, `IPlaybackTrajectory`,
`VesselLaunchIdentity`, `VesselSpawner`, `VesselSnapshotOps`, `MilestoneStore`,
`GroupHierarchyStore`, `InventoryManifest`, `PlaybackTrajectoryBoundsResolver`):
used across many modules, so no owner has a majority, and that is the intended
meaning of Core from here on.

`VesselSnapshotOps.cs` is the ninth and the newest: the 2026-09-14
`VesselSpawner` split created it as the pure-over-ConfigNode subset the ledger,
Logistics, the route proof and the UI all call, and named it in the `Core` rule
in the same PR. Its R2 evidence reads `externalRefs=8 share=0.50
top=Logistics(4) second=Recording(2)`, exactly on the R2 bar, so the placement
report lists it under "Placement rules whose destination does not match the
current map": the mechanical rule would send it to Logistics, the operator
decision keeps it in the kernel. That line is the standing note about the
disagreement, not a defect.

## The atlas

`atlas.html` is the narrative page over the other views and the one findings
report: headline tiles (source files, production types, types with no
dependencies, the largest knot's size and share, and, when git history is
available, the highest module co-change ratio and the share of commits that
touch the most-churned type), the "Main findings" list, the map inline, the
module directory grouped by layer, the top hub vocabulary, the largest knot
with its cut table, the weighted upward edges, co-change, the largest files
and types, "Opportunities, ranked", and a reading order. Everything numeric,
tabular and visual is rendered from the live model by `render_atlas_html`; the
narrative prose, the findings and the opportunities come from
`scripts/arch/atlas.toml`, while the structural strings (headings, tile
captions and the generated-views list) live in the renderer template. The page
loads nothing but Google Fonts (the map SVG is inlined), so it can be
published on its own.

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
  0.18, floor 0.00;
- `[findings]` holds `reviewed = "YYYY-MM-DD"` and an ordered
  `[[findings.items]]` list of `title` plus one-sentence `body`; the renderer
  draws "Main findings" after the tiles with `page.findings_note` and the
  review date. `[[opportunities]]` rows carry `rank`, `item`, `evidence`,
  `size`, `status` and an optional `types = [...]` (drawn as chips under the
  item); the renderer sorts them by rank and draws "Opportunities, ranked"
  before the reading order with `page.opportunities_note`. With either table
  absent or empty its section is not drawn at all. These are the only
  conclusions on the page, so they must not restate a number the page already
  generates: point at the section that shows it ("see Largest files and
  types"), or date the figure ("391 on 2026-09-14"). Re-read both against a
  fresh page and bump `reviewed` whenever they are touched.

Numbers written inside a prose sentence (a count in a module summary, for
example) are not checked; the generated numbers around them are. `--check`
does not silently show stale prose: its ATLAS section (after KNOTS) lists
production modules without a summary, glossary entries naming a type that is
not in the model, glossary entries outside the live top 18 (informational),
upward readings whose edge no longer exists, reading-order types not in
the model, and opportunity `types` entries not in the model; it then prints
the findings review date with its age in days, plus a line when that is older
than 30 days (`FINDINGS_STALE_DAYS`). All of it is informational: none of it
fails the check.

## Change history

The module map says which files reference each other; `git log` says which
files actually change together. `history.json` is built from a single
`git log --no-merges --since=<18 months ago> --name-only -- Source/Parsek`
call: only `.cs` files count, and a commit touching more than 40 source files
is treated as a sweep (a rename or a bulk edit) and skipped, because it would
dominate every co-change number. The payload records the window, the kept
commit count, how many sweeps were skipped and the largest one, then five
arrays:

- **files**: commits and last-touched date per production file, with a churn
  rank;
- **hotspots**: `file commits * fan-in` for every production type, top 30,
  which finds types that are both widely referenced and frequently edited.
  For a partial class, `file commits` is the **union** of the commits that
  touched any file the type is declared in: a commit that edited two parts of
  the same type counts once, where summing the per-file counts would count it
  twice. Attributing the type to one file instead (the first the walk met)
  read `GhostMapPresence` as a 2-commit file and dropped both it and
  `ParsekFlight` out of this table entirely, which is how the 2026-09-14
  hotspot reading came to say almost nothing about them. A file that declares
  several types lends its commits to each of them, so two types in one file
  carry the same commit count;
- **modules**: commits touching each module;
- **modulePairs**: for every production pair, the commits touching both, the
  union, and their Jaccard ratio;
- **filePairs**: the top 40 cross-module file pairs with at least 5 shared
  commits - files the map calls separate that keep changing together.

Tooling modules are excluded everywhere. If git is missing or the window is
empty, the commit count is zero and the tables carry no counts; the checker
and the atlas say so instead of failing. A co-change number is evidence, not a
verdict: two files can share commits because one change touches both sides of
a real boundary, and that is what the list is for.

`--check` prints the HISTORY section after ATLAS. The atlas has a "What
changes together" section between "Where the layering breaks" and "Largest
files and types" with the top 10 module pairs, top 10 cross-module file pairs
and top 10 hotspots.

## Size view

The module graph says what references what and the history says what changes
together; neither looks at how big anything is. `sizes.json` and the SIZE
section do, because a 13,500-line partial class whose members are all static is
a fact about the codebase that no edge count shows. It is the mechanical backbone for the next
refactor inventory, in the shape the past passes used (`refactor-5-inventory.md`
counted production files, files at or above 400 lines, and methods at or above
90 lines); the vocabulary it speaks is `docs/dev/refactor-guidelines.md` and
the candidate list it feeds is `docs/dev/plans/refactor-remaining-opportunities.md`.

Like everything else here it is a **text-scan approximation, never proof**.

### What it measures

Production files only: tooling modules (`[tooling]` in `modules.toml`) are
excluded, the same as the module metrics table and the type ladder, so the
in-game tests and the harness support code cannot dominate the list.

Per file: the raw line count (blank lines and comments included, so it matches
`wc -l`), the module, how many types it declares, how many of those are
top-level, and the growth column.

Per type, with partials merged (every part of `ParsekFlight` is one row):

- **lines**: the sum of each part's declaration span, header line to closing
  brace. Nested types count inside their enclosing type's span as well as in
  their own row, and the namespace and `using` lines outside every type belong
  to no type, so a file's type lines total slightly less than the file.
- **files**: every file a part is declared in, largest part first. The first
  of them is the type's PRIMARY file, which is what the churn, hotspot and
  co-change columns read.
- **methods**, **long methods** (at or above 90 lines) and the longest few by
  name, file and start line. Constructors count: a long one is as good an
  extract-method target as any other body. Declarations with no body
  (interface, abstract, extern, partial) do not; they are tallied separately.
- **coroutines**: methods returning `IEnumerator`, however it is spelled
  (`System.Collections.IEnumerator`, `IEnumerator<T>`: the comparison is on the
  last dotted segment with generic arguments stripped). They are reported and
  never recommended for extraction, because guideline item 6 forbids
  restructuring a coroutine body.
- **fields** and **static state**, counted as two numbers because they are two
  different problems under one owner. **Mutable statics** are the reassignable
  ones: `static`, neither `const` nor `readonly`. A static auto-property with a
  setter (`static bool Armed { get; set; }`) counts, because its backing field
  is exactly that, and so does a field-like `static event`, because
  subscribing mutates it. An expression-bodied property
  (`static Foo Instance => instance;`) does NOT: it is a forward, not a slot,
  and counting its `=` made about a quarter of the tree's "reassignable
  statics" wrong. **Readonly collection statics** are `static readonly` fields
  whose declared type names a mutable collection (`Dictionary`, `List`,
  `HashSet`, `SortedSet`, `Queue`, `Stack`, `Bag`, `Collection`, `Lookup`,
  `StringBuilder`, `Array`) or is an array of anything: the handle is fixed,
  the contents are not, and the static state map the remaining-opportunities
  doc asks for has to cover both. `ReadOnly`, `Immutable` or `Frozen` in the
  type name vetoes the match, so `IReadOnlyList<T>` does not count. `--check`
  prints the pair as `statics=17+35`; S4 and the tier read their sum.
- **pure static pool**: an ESTIMATE of the static methods that could move to an
  `internal static` helper with unit tests, the cheapest extraction the past
  passes did. A method reads as pure when its body passes four name tests: it
  mentions no identifier from the live list (`Vessel`, `ProtoVessel`, `Part`,
  `CelestialBody`, `Orbit`, `FlightGlobals`, `HighLogic`, `GameEvents`,
  `ResearchAndDevelopment`, `Funding`, `Reputation`, `ScreenMessages`,
  `TimeWarp`, `File`, `Resources`, `GameObject`, ... - the full tuple is
  `LIVE_KSP_IDENTIFIERS` in `archview.py`, covering live vessels and parts, the
  world, the scene and career singletons, view and input, Unity and process
  I/O); it does not reach a singleton through `.Instance` or `.fetch`; it names
  none of its own type's static state of either kind, because a method reading
  a shared dictionary is not a pure helper; and it does not use
  `OtherType.member` where `OtherType` is an in-repo type that carries static
  state of its own. `ParsekLog` is the one exemption there: logging never stood
  between a helper and its unit test, because the tests capture the sink. It
  stays an estimate - a helper that reaches live state one call deeper, through
  a parameter, or through a type this scan does not know, still reads as pure -
  so S2 says to verify each candidate before lifting it.
- **nested types** and the number of top-level types in the primary file.
- **skipped members**: headers the scan could not follow to a body, including a
  header whose parsed name is a C# modifier (the shape that used to print a
  110-line method called `static`). The scan prefers under-claiming, so
  anything it cannot delimit confidently is skipped and counted here; `--check`
  prints the total (1 across the current tree).
- **growth**: net lines added (`added - deleted`) in the history window, from
  one extra `git log --numstat` call over `Source/Parsek`, summed over the
  type's files. Binary rows, rename rows and sweep commits (more than 40
  source files, the same rule the co-change history uses) are ignored. With
  git missing the column reads `n/a` and nothing else changes.

### The heuristics' blind spots

Method detection is a regex for a header (`modifiers type Name(`, a tuple type
counting as a type) followed by brace, `=>` or `;` matching, plus a second
pattern for constructors, not a parser. It does not see:

- **destructors, operators and conversions** (no return type, or a keyword
  where the return type would be);
- **properties and indexers with real bodies**; a statement inside a property
  accessor is deliberately not counted as a member, but a field-shaped
  declaration inside one would be counted as a field. A get-only static auto-
  property is not counted as state, and a `readonly` field holding a custom
  class with mutable fields of its own is missed entirely;
- **return types with two or more levels of generic nesting**, which the type
  token does not match (one level of nesting, and one tuple, do match);
- **local functions and lambdas**: they live inside a delimited body, so they
  are part of their host method's line count and never a member of their own;
- **`#if` blocks**: preprocessor lines are stripped before the scan, so both
  arms of a conditional compile count.

Static state is classified by the declared type's NAME, so a `readonly` field
holding a custom class with mutable fields of its own (`private static readonly
GhostCache cache`) reads as neither kind and is missed. So is a mutable
collection hidden behind an interface the veto list catches. Both are
under-claims: the two numbers are a floor on the static state, not a ceiling.

One more keying note, inherited from the type ladder: a size row is keyed by
the ENCLOSING-QUALIFIED name, so the parts of one partial class merge while two
nested types that happen to share a name (`Outer.Handlers`, `Other.Handlers`)
stay two rows. The type graph in `types.json` still keys on the bare name and
merges them, which is a pre-existing approximation this view does not change.

Line numbers are the original file's: block comments and multi-line string
bodies are dropped by the strip, and the offsets that survive carry the count
of what went with them, so a reported `File.cs:1420` opens the right line.

### The rules

Rules are additive: every one that fires adds a row, and each row cites the
numbers that fired it. They are ordered cheapest and safest first. Thresholds
are named constants in `archview.py` and nowhere else; the one module-naming
input, `runtimeCoupled`, is the `[size]` table in `modules.toml`.

| Rule | Fires when | Reads |
| --- | --- | --- |
| S1 | at least one method is at or above 90 lines | a same-file extract-method pass (the Pass 1 shape) is the cheapest slice; names the longest few with file and line, and how many coroutines stay whole |
| S2 | the pure static pool reaches 25 methods or 1,200 lines AND is at least 30% of the type's lines | lift it into an `internal static` helper with unit tests, verifying each candidate first (the pool is a name-scan estimate); no pre-existing access modifier may change (guideline items 7 and 13). The share gate is what makes the rule discriminate: on size alone it fired on 25 of the top 25 types, now on 6 |
| S3 | the type's LARGEST SINGLE FILE reaches 5,000 lines | split that file by responsibility into further partial-class files, which moves no call site; cites the file, its lines, the type's total and how many files already hold parts. A type already spread over six 1,000-line files has done what this rule asks, so it does not fire |
| S4 | 10 or more fields of static state (reassignable plus readonly collections) | build the static mutable state map before moving anything, and keep the type as a compatibility facade in the first slice; cites both numbers |
| S5 | 5 or more nested types, or 3 or more top-level types in the primary file | nested types move to a partial file of the enclosing type itself (the row says whether that type is already `partial`); sibling top-level types move to their own files. The two counts are cited separately, because they are different moves. Sibling advice goes only to the row that OWNS the file (its largest type) and never to a nested type, so a file holding 17 small types gets one S5 row, not 17 |
| S6 | the module is in `[size] runtimeCoupled` | a note, not a slice: needs in-game validation, and log text and rate-limit keys must stay byte-identical |
| S7 | the type is inside the top 10 hotspots | a note: churn times fan-in raises the priority of whatever else fired |

Defaults: large file 1,000 lines, giant 5,000, long method 90, pure pool 25
methods / 1,200 lines / 30% share, 10 fields of static state, 5 nested or 3
top-level types, top-10 hotspot, top N 25.

### Tiers

Four axes, one point each unless stated, and a tier from the sum:

- size: 2 at or above 5,000 lines, 1 at or above 1,000. This reads the type's
  TOTAL lines, not its largest file: a 30,000-line type is a big type however
  many files hold it. S3 is the rule that reads the largest file, because it
  asks for one file to be split;
- long methods: 1 at 3 or more, 2 at 8 or more;
- static state: 1 at 10 or more, counting reassignable statics and readonly
  collections together;
- hotspot: 1 inside the top 10.

**Tier 1** at 4 or more, **Tier 2** at 2 or 3, **watch** below. A tier ranks
reading order, nothing else: the tool never says a split is safe, and an S6
type in Tier 1 is more expensive to touch than a Tier 2 pure one. Read a tier
next to `refactor-remaining-opportunities.md`, which knows which of these are
deliberately deferred.

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
