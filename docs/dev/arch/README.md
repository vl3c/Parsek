# Parsek architecture viewer

A small, dependency-free tool that shows the Parsek codebase as a handful of
modules and the references between them. The `--check` report never fails;
the `scripts/arch` test suite gates the project-declared boundaries (the
forbidden edges and the declared layers, see "Declared layers"). It is modelled on Robert C. Martin's
`arch-view` and `dependency-checker`, adapted from Clojure namespaces to C#
folders and file-name groups.

Everything lives in `scripts/arch/`:

| File | Role |
| --- | --- |
| `modules.toml` | The hand-authored input that maps source paths to module names. |
| `atlas.toml` | The atlas prose: the lede, the section notes, the module summaries, the glossary, the upward-edge readings, the reading order, the main findings and the ranked opportunities (see "Editing the atlas prose"). |
| `archview.py` | Extracts the module graph and the type ladder, builds the co-change history, writes `edges.json`, `types.json`, `history.json` and the views, and prints the `--check` report. |
| `test_archview.py` | Unit tests plus a smoke test over the real `Source/Parsek` tree. |
| `layers.py`, `layer_pins.toml`, `test_semantic_layers.py` | The declared layers: the backward / lateral split, the per-pair ratchet and its machine-written pins, the forbidden-edge gates (see "Declared layers"). |
| `logtags.py`, `log_tags.json`, `test_iltags.py` | The ParsekLog subsystem tags read from the compiled IL, with the modules that write each one; the committed map the harness lane health reads (see "Log tags"). |

The generated views are written to `docs/dev/arch/` and are gitignored (they
go stale on every commit that touches `Source/`; regenerate before reading):

| File | View |
| --- | --- |
| `edges.json` | The module data: file counts and metrics, and every module edge with weight, `cyclic` and `upward` flags, referencing files and referenced type names. |
| `types.json` | The type data: name, module, primary file, every file the type is declared in, kind, modifiers, bases, enclosing, role, level, knot, sublevel, fan-in, fan-out, references to and references from; plus a top-level `levels` summary with the max level and the count per level. |
| `history.json` | The co-change history (see "Change history"): the anchor and the 30-day, 90-day and 18-month windows, the rename accounting, per-file churn ranks, type hotspots, module commit counts, module-pair Jaccard, the cross-module file pairs that keep changing together, and the change kinds of the merged PRs. |
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

The checker is **report-only**: it always exits 0. What gates is the
`scripts/arch` test suite CI runs (placement contract, knot canaries, the
residue ratchet below, every module in a declared layer, the backward-pair
ratchet and the forbidden edges in "Declared layers"). A text scan cannot see conditional compilation or
references built through reflection, and it cannot tell a call through a
forwarder from a dependency on the forwarder's type; the semantic graph below
reads the compiler's output instead.

## The semantic graph (compiled IL)

`scripts/arch/ilgraph.py` reads the IL of the built `Parsek.dll` (an `ikdasm`
dump, the disassembler `scripts/refactor/il_compare.py` already uses, from
mono-devel) and returns, per source type, the in-assembly types its compiled
code uses: the declaring type of every member an instruction names, types an
instruction names as its operand, and the types in the type's own
declarations (base list, fields, own signatures, locals, attributes). A
called member's parameter and return types are not counted, and
compiler-generated closures and state machines are credited to the source
type that wrote them. Nodes are the text model's own (same names, same
modules), so the two graphs differ only in edges.

A **forwarder** is a method whose body is argument loads, at most a read of its
own type's service field or getter, and exactly one call (or construction, or
static field access) on another in-assembly type. A call to it is credited to
the type the forward chain ends in, when every overload of that name and
arity forwards to the same type. Deleting a forwarder and calling the service
directly therefore leaves the graph unchanged, and adding one cannot hide an
edge: the store's metric forwarders stop moving the residue. W4c (2026-11-08)
deleted the 11 of them and pointed every caller at its service: the semantic
residue stayed 150 and the text residue rose 179 -> 193.

Why IL and not Roslyn: the DLL is already built by every CI run and every
local test run, `ikdasm` is already a tooling dependency, the parser is
stdlib Python that the tests drive with inline IL text, and the compiler has
already resolved overloads, extension methods, `var` and inferred generics.
Roslyn would need the Microsoft.CodeAnalysis packages and a second
compilation of every file against the KSP references.

Run it:

```bash
python3 scripts/arch/semantic.py --il Source/Parsek/bin/Debug/Parsek.dll [--edges]
python3 scripts/arch/archview.py --check --il Source/Parsek/bin/Debug/Parsek.dll
```

The report prints the knot, greedy and replayed residue for the text graph,
the raw IL graph and the semantic (forwarder-resolved) graph, then classifies
every edge the text and IL graphs disagree on (`semantic.py`'s docstring lists
the reasons: constants, enum members and `nameof` the compiler inlines, nested
type qualifiers, text false positives such as object-initializer members or a
stock type's nested name, and on the IL side members reached through a value
the source never names, `var` locals, inferred generic arguments and base
lists the text scan never reads). `--edges` lists every edge and, for each type
in one residue and not the other, the disagreeing edges that touch it.

CI runs `scripts/arch/test_semantic.py` after the build with `PARSEK_ARCH_IL`
pointing at the built DLL (its real-tree cells, the residue ratchet among
them, skip only when that is unset).
On the refactor branch at 668a03255 (2026-11-08, after wave 2 W1c and W3b):
text residue 179, semantic residue 150 (greedy and replayed with
`SEMANTIC_CUT_ORDER`), knot 432 against 390; text-only edges 712, IL-only 637,
every one classified. Two text false
positives that held weight: the UI node `State` (StockUiReasonTooltip's private
nested class) is matched by every `Contract.State` / `RDTech.State` token in
GameActions and Patches (foreign-qualifier), and `Hold` (RewindVesselHold's
nested class) by enum members such as `ChainBridgeAction.Hold`
(qualified-member). The one compiled edge into `Hold` is RewindLauncher's
`var` list of holds. W1b (#2464) moved the hold record out of ReFlyInvokeHold,
deleting the text edge `ReFlyInvokeHold -> Hold` (only the qualifier of
`ReFlyInvokeHold.Entry`): the text residue fell 272 -> 263 (knot 459 -> 450)
and the semantic residue stayed 250, which never counted that edge.

## Member facts (compiled IL)

`scripts/arch/ilfacts.py` turns the same parse (`ilgraph.parse_il` records
the member facts in the pass that builds the type uses; there is one IL
parser) into a facts model keyed by member, the input of "Code facts" below.
The facts layer itself gates no property of the code. Its real-tree cells (`RealTreeFactsTests`,
run by the compiled-IL CI step) check the extractor itself: every source type is a
facts type, member keys are unique, every in-assembly call resolves to a key, and
all compiler-generated code folds into a source method. A red there means the
extractor cannot model something in the assembly, and the fix belongs in the
tool. A type the source scan cannot place keeps `module: null` and is reported,
not failed.

- **Types** by full IL path: namespace plus nesting
  (`Parsek.Logistics.RouteEndpointTransfer/VesselIdentity` is not
  `Parsek.VesselIdentity`). Kind, header flags, base type, custom attributes,
  and the module: `modules.toml` assigns the type's source file, found by
  namespace and nesting in a scan of the source tree; a nested type takes its
  enclosing type's. `module: null` when the scan cannot say (the summary
  prints how many).
- **Fields** by `Type::name`: static, initonly, literal, compiler-generated,
  the property it backs (`<X>k__BackingField`), event backing, attributes;
  for a static field the methods that read it (`ldsfld`), write it
  (`stsfld`) and take its address (`ldsflda`, a by-ref write the store scan
  alone misses), and `defaultedBy`, the address-takers that follow the
  `ldsflda` with `initobj`: that pair is how C# compiles `x = default` or
  `x = null` on a value-type or nullable static, a write with no `stsfld`.
- **Methods** by `Type::Name(param types)` (``Name``N`` when generic), the
  parameters normalized so a declaration and a call site print the same text.
  Instruction count, decisions (conditional branches + distinct `switch`
  targets + catch and filter handlers; complexity is decisions + 1), the
  in-assembly members called (`call` / `callvirt` / `newobj` / `ldftn` /
  `ldvirtftn`), static reads / writes / address-takes, the external types
  named by the body, the locals and the signature (`assembly|type`, the BCL
  left out), custom attributes (the roots an uncalled-code report needs:
  `HarmonyPrefix`, `InGameTestAttribute`; the type's own, such as
  `HarmonyPatch` and `KSPAddon`, are on its type entry), and calls into
  another assembly's `Parsek` types (from the test DLL, for test reach), the
  outside members it names (`externalMembers`), and its identifier-shaped
  `ldstr` literals (`strings`, what reflection by name and `nameof` leave).
  A type also carries its interfaces and `namedByOthers` (how many other
  types name it in IL); a static field carries `receiverCalls`, the outside
  calls (or `[]` element stores) whose receiver is its value or a value read
  out of it (see "Static state" for the rule), and `directReceiverCalls`, the
  subset whose receiver is its own value.
  Generated code no single method owns credits its calls and static access
  to `Type::(generated)`, so no caller or writer is lost.
- **Compiler-generated code** (lambdas in `<>c`, closures, local functions,
  iterator and async state machines) is folded into the source method that
  creates it, found through IL references (the `newobj`, `ldftn` or call
  that reaches it, and through a generated type's constructor everything in
  that type), never by the mangled name. A method's `instructions` and
  `decisions` are its own body; `generated` holds what was folded in, and its
  calls, statics and external types include it. Generated names never appear
  as keys: their ordinals (`<Run>d__594`) shift when a method is added above
  them and differ between Debug and Release. The constructors of a `<>c`
  lambda cache shared by several methods belong to none of them and are
  counted on the type under `unattributed`.

Run it:

```bash
python3 scripts/arch/ilfacts.py Source/Parsek/bin/Debug/Parsek.dll              # summary
python3 scripts/arch/ilfacts.py Source/Parsek/bin/Debug/Parsek.dll --json facts.json --summary
python3 scripts/arch/ilfacts.py Source/Parsek.Tests/bin/Debug/net472/Parsek.Tests.dll --modules none
```

The input is an assembly (disassembled with `ikdasm`) or a ready `.il` dump;
`--modules none` skips the module map for an assembly whose source is not
under `Source/Parsek`. The summary counts production types (a module, not a
`[tooling]` one): reassignable statics, static readonly collections,
reassignable statics written by another production outer type (by `stsfld`,
plus `ldsflda`, plus a static auto-property setter), methods by complexity
(own body, with generated code folded in, and every IL method on its own),
and the instructions in generated code. On b5877e51f (2026-11-11, Debug): 865
reassignable statics (55 of them static auto-property backing fields), 307
readonly collections, 51 / 55 / 59 cross-type writers, complexity >= 20 / 50 /
100 600 / 30 / 5 own body, 615 / 32 / 5 folded, 604 / 31 / 5 per IL method;
24,381 of 1,274,131 production instructions in generated code (1.9%).

Debug against Release (b5877e51f, both through this tool): the same type,
field and method keys; static read / write / address sets and method
attributes identical; calls differ in 1 of 22,046 methods, decisions in 69
(0.3%), the folded decisions in 4; instructions are 22% fewer in Release;
locals' external types differ in 530 methods (typed locals the optimizer
drops); static auto-property backing fields lose their
`DebuggerBrowsableAttribute`. The summary's static-state lines are equal and
its complexity lines 5 lower at >= 20. Measure on the Debug DLL CI builds,
never pin a number from a Release build, and gate nothing on instruction
counts. CI's compiled-IL step runs `test_ilfacts.py`'s real-tree
cells with `test_semantic.py` (structure only: every source type keyed and
mapped to a module, unique keys, every call resolved, no generated key,
entry-point attributes read) over the one parse `ilgraph.load_parsed` caches.

## Code facts (compiled IL)

`scripts/arch/ilanalysis.py` derives five reports from the facts model, pure
functions with unit cells (`test_ilanalysis.py`); every list a rule reads
lives in `scripts/arch/facts.toml`, beside `modules.toml`. Production means a
type whose source file maps to a non-tooling module.

- **Static state**, per module and per outer type: reassignable statics
  (static auto-property backing fields included, other compiler-generated
  and literal fields out), static readonly collections whose contents a
  method other than a static constructor changes, and statics written by
  another production outer type (`stsfld`, or a call to its static
  auto-property setter; a type nested in the owner is the owner; tooling
  writers such as InGameTests do not count). An `ldsflda` (taking the
  static's address) is not a write: C# emits it to read a member of a
  value-type static or call a method on it (`Ledger.StateVersion.ToString(ic)`).
  It is reported on its own ("address taken by another production type");
  a `ref` / `out` / `Interlocked` write through another type's static takes
  that path too, and the gate does not see it.

  "Changes" is a call named in `[statics] mutating_members` (or an element
  store, `[]`) whose receiver is the static. `ilgraph.StaticReceiverTracker`
  follows the value an `ldsfld` pushes down the evaluation stack, through
  `dup`, a cast, a local it is stored in, and values read out of it (an
  element, a field, an instance call's result: `Table[i].Add(x)` and
  `sb.Append(a).Append(b)` count), and records the call that consumes it as
  `this` (or the element store that consumes it as the array). A read that
  hands the value on is not a change: `foreach`, `Contains`, `Count`,
  `Table[i]` or the static itself passed as an ordinary argument
  (`ReceiverTests`). Passed to an in-assembly method that is resolved
  directly, the callee's own calls on that parameter count, to a fixpoint
  across such calls. Residual limits: where the tracker cannot follow (a
  branch or `leave`, an opcode with no stack effect it knows, a `starg` /
  `stfld` / `stsfld` / indirect store of the value, a `ref` local) it falls
  back to the old rule, any such call within 8 instructions after the read,
  so a call near the read can still count; a call through an interface or
  virtual slot does not reach the implementation's body; a call on a value
  read out of the static counts whether or not it shares state with it
  (`Table.ToList().Add(x)` counts); a value stored in another field, a
  closure or a collection is no longer followed.
- **Complexity**: decisions + 1 with the method's lambdas, local functions and
  iterator bodies folded in, ranked, counted at 20 / 50 / 100.
- **Game coupling** ("can reach the game"): a method names a type of
  `[coupling] game_assemblies` that is not in `inert_types` (vector,
  quaternion and colour structs, Mathf, ConfigNode, game enums, attributes),
  or a member in `live_members` (the `extern` members of those inert types,
  such as `Quaternion.Euler`), in its body or signature (locals are left out:
  Release drops them), or it calls a method that does. A call to an interface
  or virtual member reaches every implementation and override in the
  assembly; calls into `not_followed` types (`ParsekLog`, which writes
  through a test sink) are not followed. Per module, the share of
  instructions in such methods. It means can reach on some path, not needs.
- **Uncalled code**: production methods the roots do not reach in
  Parsek.dll. Live is a fixpoint. It starts from every non-production
  method (tooling such as InGameTests is live, so its calls count), the
  generated code no single method owns, and the production methods a root
  rule keeps on its own: static constructor; member of a delegate type; any
  method attribute not in `ignored_attributes` (Harmony, InGameTest, KSPEvent
  ...); a Harmony patch method by name on a `[HarmonyPatch]` type, or on a
  type another type names (`typeof` handed to Harmony) that declares a static
  `Prefix` / `Postfix` / `Transpiler` / `Finalizer`; a Unity message by name
  on a type whose base chain leaves the assembly for a non-BCL type;
  `ToString` / `Equals` / `GetHashCode` / `Finalize` and operators; an
  override of an outside method; an implementation of an outside interface
  (implicit or explicit); a constructor of a type that is named by another
  type, has an engine base, is an attribute or carries a type attribute;
  compiler-generated. It grows by the calls of live methods and by the rules
  that read use, each a step of the same fixpoint: an override or
  implementation of a slot that is called (or root-kept); an abstract or
  interface member with a live implementation (this keeps the declaration,
  not its other implementations); the sibling accessor of a live accessor;
  the constructor of a type with a live member; a method whose name (or
  property name) is an identifier string literal in a live method. Every
  step reads only live methods, so deleting a dead method never makes another
  one dead, and a chain called only from dead code is dead as a whole
  (`DeletingDeadCodeTests` holds this on fixtures and, under
  `PARSEK_ARCH_IL`, on 50+ methods of the real tree). Each rule is reported
  with the live methods it alone keeps (no live caller), tried in the order
  above. When unsure a rule roots. With the test facts the dead set splits
  into what no unit test calls either and what a test calls (test hooks by
  name, `test_hook_suffixes`, against the rest). A type that a dead method
  alone names still counts as named (the facts count type naming per type,
  not per method).
- **Test reach**: per module, the share of production methods and of their
  instructions a test method calls directly (a call on an interface or
  virtual member credits its implementations), and the methods with 30 or
  more decisions (folded) no test calls. Not coverage. It needs the test DLL's
  facts, which CI does not parse (about a minute: 19 s `ikdasm`, 31 s parse).
  Per production method it also counts the distinct methods of the test
  assembly that call it directly (`testCallers`; a test method calling it
  twice is one, and a call on a slot counts for the slot and each
  implementation and override, as above), and which production types have at
  least one method a test calls directly.

  **Where a test is worth most** ranks the production methods with a decision
  (folded) by one score, report only (test reach moves with every test, so
  nothing gates it):

  `score = decisions x 2 (when no test method calls the method directly, else
  x 1) x (1 + min(commits, 20) / 20) x (1 - 0.8 x line share)`

  where `commits` is the commits of the method's type's busiest source file in
  the Atlas history window (18 months, renames followed, the history's sweep rule: a commit
  touching more than 40 source files is left out), and `line share` is the
  method's line coverage from the snapshot below when the snapshot keeps the
  method, else its type's, and the factor is 1 when there is no snapshot.
  Each factor is monotone: more decisions or more commits never lower the
  score, a direct test caller or more covered lines never raise it. A fully
  covered method keeps a fifth of its score, because coverage counts lines
  that ran, not lines a test checked. The IL does not say which file of a partial type a method
  sits in, so a type spanning several files takes the max of its files'
  counts (not the sum: one commit editing two parts is one change); a nested
  type takes its own declaring file, and a type the source scan does not
  declare its nearest declared enclosing type's (`ilfacts.files_for`, the
  scan that places types in modules; a real-tree cell holds that every
  production type finds a file). The cap of 20 is the 90th percentile of
  per-file commits in the window at 2026-11-11, so the churn factor separates
  the busiest tenth of the files from the rest and no further. The table
  prints every input as a column (method, module, decisions, direct test
  callers, file commits, whether a test reaches the type, the line coverage:
  the method's, else the type's marked `type`, blank without a snapshot), so a
  ranking the owner disagrees with can be read off it. Churn follows files, not code: a
  method moved into a file a recent split created (`ParsekFlightSplitBrancher.cs`
  has 3 commits in the window, `ParsekFlight.cs` 1,305) starts from that
  file's short history. The Atlas shows the top 25,
  `ilanalysis.py --tests` the top 10 (`--no-history` leaves the commits
  column blank, read as 0). At 12b559f64 (2026-11-11, Debug): 12,349
  candidates over 6,205 commits since 2025-05-01; the top three are
  `Logistics.RouteAnalysisEngine::AnalyzeWindows` (141 decisions, no direct
  test caller, 29 commits), `EngineFxBuilder::TryBuildEngineFX` (140, none,
  37) and `ParsekScenario::OnLoad` (107, none, 558) with no snapshot; with the
  first real snapshot (12a312a09, type shares only) `TryBuildEngineFX` leads
  (its type 16% covered, 490.2), then `GhostTrajectoryPolylineRenderer/Driver`'s
  `LateUpdate` (268.0) and `OnMapCameraPreCull` (224.0) (type 0%), and
  `AnalyzeWindows` (type 93%) leaves the top 10. B6 (2026-11-12) split
  `AnalyzeWindows` into an orchestrator (complexity 21) and 14 private steps
  (the highest, `BuildEligibleResult`, 26), so it left the complexity watch.

```bash
python3 scripts/arch/ilanalysis.py Source/Parsek/bin/Debug/Parsek.dll
python3 scripts/arch/ilanalysis.py Source/Parsek/bin/Debug/Parsek.dll \
    --tests Source/Parsek.Tests/bin/Debug/net472/Parsek.Tests.dll --json report.json
```

An input is an assembly, an `.il` dump or an `ilfacts.py --json` file. On
4629fbfcf (2026-11-11, Debug, main's code): 865 reassignable statics, 217 of
307 readonly collections mutated, 55 statics written by another production
type and 6 whose address another production type takes; complexity 615 / 32
/ 5; 46% of production instructions can reach the game (highest Patches 83%,
Config 66%, Controllers 64%; lowest Core 6%, Trajectory 25%, Missions 26%);
943 methods the roots do not reach (22,756 instructions): 234 that no unit
test calls either (7,611 instructions), 709 a test calls (257 hooks by name);
84 methods with 30+ decisions no unit test calls. The rules alone kept 1,263
live methods (Harmony 310, `.cctor` 279, dispatch 272, strings 112, delegates
88 ...). After B7 deleted the uncalled code (2026-11-12): 787 methods the roots
do not reach (17,484 instructions): 78 that no unit test calls either (2,339
instructions: 32 test hooks by name, 8 that source gates pin (the `ParsekUI`
hover check and `LogisticsWindowUI.DetailLine(GUIContent)`), 38 reached only
through a method a test calls), 709 a test calls. After B6 split
`RouteAnalysisEngine::AnalyzeWindows` (2026-11-12): complexity 614 / 31 / 4.

### Coverage snapshot

Test reach says which methods a test calls; it cannot say which lines ran.
Real line coverage comes from a unit-test run with coverlet, which only the
owner's Windows machine makes (the cloud and CI run the suite under mono with
no coverlet), so a run is folded into a small committed file,
`scripts/arch/coverage_snapshot.json` (schema `parsek-coverage/1`), that the
Atlas and the metrics row read. Refresh it after a Debug build:

```bash
pwsh -File scripts/test-coverage.ps1        # Windows: writes TestResults/Coverage/coverage..cobertura.xml
python scripts/arch/coverage.py TestResults/Coverage/coverage..cobertura.xml \
    --write-snapshot --commit <sha the run measured> --date <YYYY-MM-DD>
git add scripts/arch/coverage_snapshot.json  # and commit it
```

`scripts/arch/coverage.py` (stdlib only, runs on Windows) streams the
Cobertura file (`iterparse`, each class cleared once counted; coverlet's file
for this assembly is about 80 MB), reads only the `Parsek` package, and
counts lines itself (`hits` > 0) rather than trusting the rounded per-class
rates. It folds compiler-generated classes (`<>c`, `<>c__DisplayClassN_M`,
the iterator and async state machines `<Name>d__N`, with a `/`, `+` or `.`
separator) into their source type, as `ilfacts` folds generated code, drops a
generic arity from every segment (coverlet writes ``Parsek.GreyLedger`1``,
the facts key it `Parsek.GreyLedger`), sums the class elements of a partial
type (one per file), and counts a line once per type (by file and number).
The file name has two dots because `test-coverage.ps1` passes coverlet the
output prefix `<dir>/coverage.` and coverlet appends `.cobertura.xml`.

It also keeps lines per source method, keyed `Type::Method` by NAME from each
method's own lines. Generated code folds by name, where `ilfacts` follows
call references: a state machine's `MoveNext` (`Type/<Run>d__N`, an async
lambda's `<<Run>b__0>d`) is `Run`, a lambda `<Run>b__N_M` (in `<>c` or a
`<>c__DisplayClassN_M`) is `Run`, a local function `<Run>g__Local|N_M` is
`Run`, and anything else generated (a closure class's constructor, the `<>c`
static constructor, a state machine's other members) counts for the type
only. Overloads share a name and are summed: `Type::Run` is every `Run`, the
one limit of a name key. Coverlet's per-method `complexity` is summed the same
way and only methods at 10 or more are kept; when the snapshot would exceed
500 KB the threshold rises one complexity value at a time until it fits, and
the write line says so (`--method-complexity-min`, `--max-kb`). Checked on a
synthetic coverlet-shaped file built from every method of the Debug DLL at
12b559f64 (26,725, generated ones under their IL names): the name fold agrees
with `ilfacts`' reference attribution on every generated method both attribute
(1,791; the other 2,635 generated methods are the constructors and plumbing
that stay type-only), and 2,687 of the 2,693 production methods at folded
complexity 10 or more join their snapshot row (all 615 at 20 or more); the
six misses are iterators whose enumerator plumbing `ilfacts` folds in and the
name fold leaves to the type, which lifts them over the threshold on one side
only. They fall back to the type's share. It is tolerant: unknown elements and
attributes are ignored, an unreadable line is skipped, and missing branch
data makes the type's branches unknown (null), not 0. Its summary prints the
class elements' sums as written beside the file's root totals and the
difference, never a failure. The snapshot holds the commit and date given on
the command line, the configuration, per type lines and branches valid /
covered, and per kept method `[covered, valid]` lines with the threshold
(`methodComplexityMin`), with sorted keys and one method or type per line so a
refresh diffs cleanly. The method block is additive: a snapshot written
before it still reads, with no method shares (the table then uses the type's
and the coverage line says to regenerate).
Every cell runs on a hand-written fixture in coverlet's shape
(`scripts/arch/fixtures/coverage_sample.cobertura.xml`, `test_coverage.py`).

With a snapshot, "Code facts" shows a per-module table (lines covered /
valid, line and branch share, production types only) headed "measured at
<commit>, <date>", always older than the page, fills the risk table's line
coverage column and its score factor, and counts staleness in one line: types
with instrumented code in the analysed DLL that the snapshot does not list
(new since), snapshot types the DLL lacks (renamed or deleted since), and
apart from both, types whose only code is the constructor the compiler writes
for a class that declares none (a 4-instruction `.ctor()` with no source line,
which coverlet never lists: 126 of the 128 unlisted types against the first
real snapshot, the other two being the generic types the arity fold now
joins). Staleness never reds. Without a snapshot everything says "not measured".
`archview.py` and `ilanalysis.py` read the default path, `--coverage PATH`
overrides it; `metrics.py` reads the default path.

### The ratchets

CI's compiled-IL step runs `test_ilanalysis.RealTreeRatchetTests` over the
built Debug DLL (the step's `test_[ims]*.py` pattern; `WorkflowTests` reds if
it stops matching). The pins are `scripts/arch/facts_pins.toml`, the one place
the bounds live. The ratchets bind static write sets, call sets and
per-method decisions, never instruction counts or shares. A Debug and a
Release build of one compiler agree on those; two compilers do not quite
(CI's SDK and this container's 8.0.134 built identical source to 614 and 615
methods at complexity 20 or more), which the complexity watch band absorbs.

- `crossTypeStaticWrites`: the statics written by another production type
  may not outnumber `bound` (55). A red names the new keys and their writers.
- `complexityWatch` (the complexity gate): every method (by `Type::Name`, so
  overloads and a parameter change do not move it) at complexity 45 or more
  is pinned (53), the gate 50 minus a watch band of 5
  (`COMPLEXITY_WATCH_BAND`). A method that is not pinned reds when it
  reaches 50, so a compiler that counts a pinned method a decision or two
  higher cannot red the check, while a method growing from below 45 to 50
  still does. A pinned method may grow past 50 without a red. Leaving the
  band needs no re-pin; a rename or a move into another type does.
- `uncalledInAssembly`: the methods the roots do not reach in Parsek.dll,
  test hooks by name left out, may not outnumber `bound` (491). It reads
  Parsek.dll alone, so a production method only a test calls counts too:
  that is code the game never runs (B7). Deleting dead code cannot raise it.

What the two counted gates guarantee is that the count does not rise. A
swap inside a bound passes (one key leaves, another enters), and once a
count falls the slack is not enforced until someone re-pins: a later PR may
add entries up to the old bound without a red.

A red prints what moved, by how much, which members, and the re-pin
command. Re-pin after a Debug build of the tree, never from a Release build:

```bash
python3 scripts/arch/ilanalysis.py Source/Parsek/bin/Debug/Parsek.dll --write-pins
```

Re-pin in the PR that lowers a count (the step log prints the slack), so the
bound follows it down, and raise one only for a deliberate change the PR
explains.

## Static lifetimes (compiled IL)

`scripts/arch/lifetimes.toml` (hand-edited) gives the mutable statics of
production code a declared lifetime. The population is the one "Static state"
above reports: every reassignable static and every static readonly collection
outside a static constructor makes the receiver of a `[statics]
mutating_members` call or an element store, by the rule and limits there
(1,079 on 2026-11-11). The
lifetimes, each with its meaning in the file:

| Lifetime | Means |
| --- | --- |
| `process` | lives as long as KSP: caches keyed on a version, counters, subscriptions, installed rules |
| `save` | the loaded save's own state, from one Cold load to the next; a Cold load clears it |
| `load-transition` | armed or captured before a load, consumed by the next OnLoad or the scene it opens |
| `scene` | torn down or reset on a scene change |
| `scoped` | set and restored around one call (try/finally, a Dispose, a Harmony Finalizer) |
| `test-hook` | a seam only tests set (a provider, an override, a fault injector, a logging switch) |

A static gets its lifetime from a field entry under its type's `[[type]]`
(`name = "lifetime: reason"`; an auto-property by its property name, as the
reset allowlist does), else from a `[[pattern]]` its name matches (`*ForTesting`,
`*ForTests`), else from its type's `default`. Every entry carries a reason.
Tagging is honest, not complete: a static nobody has read the write sites of
stays untagged, and the untagged set is pinned. `[cold_load.roots]` names the
methods a Cold load runs (`ParsekScenario.OnAwake`, `OnLoad`,
`ResetSaveScopedSessionState`, `ParsekSettings.OnLoad`), each with the reason
stock calls it.

What reds, in CI's compiled-IL step (`test_static_lifetimes.RealTreeLifetimeTests`,
pins in `scripts/arch/lifetime_pins.toml`; its `WorkflowTests` cell reds if the
step's pattern stops matching the file):

- **A new untagged static.** `[untagged]` is a set: a static with no lifetime
  that is not listed reds, naming it. A renamed untagged static is a new one.
- **coldLoadMisses**: a `save` or `load-transition` static with no clear in
  the transitive call closure of the roots (calls, plus every implementation
  or override of a called slot) and no verified `[cold_load.cleared_by]`
  entry. A clear is a write that can empty or replace the value: an
  `stsfld`, a call to its auto-property setter, `ldsflda` + `initobj`, or a
  call of a `[cold_load.clearing_members]` member (only `Clear`; each member
  carries its reason) whose receiver is the static's own value
  (`directReceiverCalls`: followed down the stack, or the static passed as
  itself to a parameter the callee clears; never an element read out of it,
  and never the window fallback). An append, an element store, a
  `Remove(key)` or a read is no clear (`ColdClearTests`). A
  `[cold_load.cleared_by]` entry names the closure method that clears a
  static by a route the rule cannot read, with its reason; every entry is
  checked: the method exists, is in the closure, and itself reads, writes or
  takes the address of the static (a helper handed the static as an argument
  does not qualify; name the caller that passes it). The check shows the
  method touches the static on the Cold path, not that it empties it: the
  reason is the claim a person reads.
  `RecordingStore.committedRecordings` has one: the Cold branch's
  `ClearCommittedInternal` empties it with a `RemoveAt` loop.
- **testHookWrittenByProduction**: a `test-hook` static that another production
  outer type writes (any write above).
- **testWrittenUntagged**: an untagged static that Parsek.Tests.dll writes
  directly (`stsfld`, or a call to a static setter: the statics that setter's
  body stores). `scripts/arch/clrmeta.py` reads the test assembly's metadata
  tables and method bodies in under a second, with no disassembly.
- **resetGateDisagreement**: a static of a `SaveScopedSessionResetTests` type
  that `ResetSaveScopedSessionState` resets (it is not in
  `scripts/session-reset-allowlist.txt`) but is not tagged `save` or
  `load-transition`.

Each finding list is pinned with `facts_pins.toml`'s contract: the count may
not rise, a red names the new keys and the re-pin command, and slack after a
fall is reported until a re-pin. The DLL-free suite reds on a malformed
declaration (an unknown lifetime or key, a type declared twice, a field tagged
twice, a missing reason, a root that is no method key) and on a disagreement
with the C# reset gate, whose type list (`InventoryTypes`) and allowlist are
parsed with comments and strings stripped: every gated type is declared with a
cleared default, every allowlisted field has a field entry, and a field entry
in a gated type that a Cold load does not clear names an allowlisted field. The
real-tree cells also red when the declaration names a type, field or root the
assembly lacks.

What cannot red, each held by a cell: tagging or deleting a pinned untagged
static (`test_tagging_a_pinned_untagged_static_passes`,
`test_deleting_a_pinned_untagged_static_passes`); renaming a static its type
default tags (`test_renaming_a_static_tagged_by_its_type_default_passes`); a
swap inside a finding's bound (`test_a_swap_inside_a_finding_bound_passes`); a
`process`, `scene`, `scoped` or `test-hook` static no load clears
(`test_no_load_clear_reds_only_a_save_or_load_transition_static`);
a test hook written by its own type, by a method named as a test hook, or by
tooling such as InGameTests
(`test_a_test_hook_written_by_another_production_type`); a test that only reads
a static or takes its address
(`test_test_writes_reads_stsfld_and_static_setters_only`).

Limits:

- The closure is reachability. A clear anywhere the roots reach satisfies
  `coldLoadMisses`, even one behind a branch the Cold load never takes or in a
  method reached for another reason
  (`test_a_write_off_any_branch_counts_the_same`). It proves a clear is
  reachable, not that it runs on an ordinary Cold load. In particular a
  `ResetForTesting` body, and everything the test-batch crash reconcile runs
  (`OnLoad` -> `RunTestBatchCrashReconcile` -> ... ->
  `RecordingStore.ResetForTestingInternal`), count as Cold clears when the
  closure reaches them. Three statics pass only that way:
  `DiscardReFlyLoadIntent.armedTarget` (inert while `armed` is false, which
  the consume clears), `ParsekScenario.CrashReconcileInProgress` (the crash
  reconcile's own flag) and `RecordingStore.committedRecordings`, whose
  ordinary clear is its `cleared_by` entry.
- A clear is a write that CAN empty or replace the value, not one that does:
  an `stsfld` of any value counts, and a `Clear` on a static stays a clear
  even if the value is refilled before the load returns.
- A write the facts cannot see reads as no write: reflection, a `ref` / `out`
  pass of the static's address (only `ldsflda` + `initobj` counts;
  `test_an_address_read_is_no_clearing_write`), a write from another assembly.
  Such a static reds `coldLoadMisses` until it is tagged otherwise or written
  visibly.
- The test-assembly reader skips member references whose parent is a generic
  instantiation (a static of a generic Parsek type written from a test), and
  `testWrittenUntagged` checks only untagged statics.
- A lifetime tag is a claim with a reason, read by a person; only the four
  findings above check one against the IL, and the headless cells below
  check the `save` / `load-transition` ones by running the reset.

The C# side runs what this side only reaches. `SaveScopedSessionResetTests`
reads this file: `ColdResetCells_CoverEverySaveScopedType` reds on a type
tagged `save` or `load-transition` (by default or by a field entry) that
neither `InventoryTypes` (reset by `ResetSaveScopedSessionState`) nor
`ColdLoaderCells` (reset by its own Cold-path call: a loader given a save that
holds nothing, a consume, a drop) names, and `ColdLoader_ResetsEverySaveScopedStatic`
dirties every such static of each `ColdLoaderCells` store, runs the helper and
the store's call, and reads it back; what it leaves set today is pinned in
`ColdLoaderKnownGaps` (todo COLD-RESET-LEFTOVERS). Both reset cells read
through a store's save-scoped holder, a static of a nested class named
`ColdResetState` (todo ARCH-COLD-RESET-HOLDERS): the holder's instance fields
are slots keyed `Store.field`, so the IL population counts the holder as one
`save` static while the cells check every field in it. The report prints the
holders and the fields they carry by lifetime (`ColdResetState holders: N
statics holding M fields`), and a holder tagged anything but `save` or
`load-transition` is a declaration problem
(`test_a_holder_tagged_as_surviving_the_load_reds`).

Tag a static by adding a field entry (or a type default) with the reason its
write sites support, then re-pin after a Debug build:

```bash
python3 scripts/arch/lifetimes.py Source/Parsek/bin/Debug/Parsek.dll              # report against the pins
python3 scripts/arch/lifetimes.py Source/Parsek/bin/Debug/Parsek.dll --write-pins # re-pin (Debug only; refused from a Release path)
```

The test assembly comes from its Debug build path (`--tests` to override);
pinning refuses while the declaration has a problem or a finding is
unmeasured. `archview.py --check --facts <dll>` prints the counts as STATIC
LIFETIMES, the Atlas draws them under "Code facts", and the metrics row
carries them as `facts.lifetimes`.

At the pin (2026-11-11, Debug): 1,079 statics, 569 tagged (process 89, save
134, load-transition 55, scene 10, scoped 26, test-hook 255; by a field entry
141, a pattern 204, a type default 224), 510 untagged; the Cold closure holds
5,456 of 17,263 production methods; the test assembly writes 300 Parsek.dll
statics directly. Findings: coldLoadMisses 9 (the `WarpToTimeRequest` pending
and deferred-warp fields, tagged `load-transition` from their own doc comment,
which no load clears: todo WARP-REQUEST-SURVIVES-A-FAILED-REWIND), the other
three 0. Re-pinned the same day after that fix (the Cold reset now drops both
requests, and `WarpToTimeRequest` joined the C# reset gate): coldLoadMisses 0,
the closure 5,473 of 17,267 production methods, the other counts unchanged.
A static named `*ForTesting` that production code of its own type writes as
real state (a cached probe, an input-lock mirror, instrumentation
counters) has a field entry tagged by what those writers do; the pattern
would call it a test hook, and testHookWrittenByProduction only looks at other
types.

## Declared layers

The Atlas's "upward" edges read the measured instability: an edge is upward
when a more stable module references a less stable one. That direction is a
number that moves. When two instabilities drift past each other an edge flips
with no code change near it (GameActions -> Crew did during the refactor
waves), so nothing can be ratcheted on it. The declared layers say the
intended direction instead, and change only when someone edits them.

`modules.toml` `[layers]` lists the layers lowest first (`order`) and puts
every production module in exactly one of them (`[layers.modules]`); tooling
modules have none. A module may depend on modules in its own layer and in
lower layers.

| Layer | Modules | What it is |
| --- | --- | --- |
| floor | LogIO, Config, Core, Trajectory | the vocabulary every module leans on: logging and file I/O, settings, the Core kernel files, the trajectory math and data |
| model | Recording, GameActions, Crew | the persistent model: recordings and their store, the ledger, the kerbal state the ledger books (`KerbalsModule`) |
| features | Ghost, Rewind, Missions, Logistics, MapRender, SwitchIntent, Reaim, Rendering, Display | what the mod does with the model |
| presentation | UI, Timeline | the windows and the timeline rows they show |
| entry | Controllers, Patches | what KSP calls: the scene controllers (the composition roots that build the windows) and the Harmony patches |

The assignment follows the module descriptions (`atlas.toml`, the map reading
above) and, among honest placements, the one with the fewest backward type
pairs on the semantic graph of 2026-11-11. Three were close calls: Crew sits
in the model because `KerbalsModule` is a ledger resource module (in features
it would add 18 backward type pairs); Logistics sits in features although its
instability is the lowest outside the floor (in the model it would save 3 type
pairs but add 3 module pairs, and a supply route is a feature); UI sits below
the controllers because the controllers build the windows, so a window
reaching back into a controller (26 type pairs) is the edge to undo. Core
stays in the floor although `GroupHierarchyStore` and `MilestoneStore` reach
up into Recording, Rewind, GameActions and Controllers: those are the edges
the declaration exists to show.

- **Backward**: an edge from a module to one in a higher layer. **Lateral**:
  an edge between two modules of one layer, reported and never ratcheted.
- **The measure** of a module pair is the number of DISTINCT (from type, to
  type) pairs between them on the compiled-IL semantic graph (the
  forwarder-resolved graph of "The semantic graph"), so a new call site
  between two types already paired, a comment, or a type moved between files
  of one module moves nothing.
- **The ratchet**: `test_semantic_layers.RealTreeLayerTests`, in CI's
  compiled-IL step (`test_[ims]*.py`; its `WorkflowTests` cell reds if the
  pattern stops matching), reads `scripts/arch/layer_pins.toml`, one entry
  per backward module pair with its `count` and, for the failure message
  only, its type `pairs`. A backward module pair may not count more type
  pairs than its pin, and a backward module pair with no pin reds; the
  message names the new type pairs (those not in the pin's list) and the
  re-pin command. The gate reads counts only, so renaming a type reds
  nothing, and deleting a type can only lower a count. Two changes that add
  no dependency can still move a pair, and both are a re-pin the PR explains:
  a body change that makes a method a forwarder, or stops it being one,
  re-credits its callers to the other end of the forward (deleting the one
  log line beside a forwarded call moves the callers' pair to the target's
  module, see "The semantic graph"), and moving a type to another MODULE,
  even one in the same layer, carries its pairs to that module's pins. A
  dependency reached only through a type name the semantic graph cannot key
  (a name declared in two modules, or one under four characters) is
  invisible to this ratchet and to the IL forbidden gate, as it is to the
  residue ratchet. As with `facts_pins.toml`, a
  swap inside a count passes and slack after a fall is not enforced until a
  re-pin: re-pin in the PR that lowers a count, and raise one only for a
  deliberate dependency the PR explains.
- **Every module in a layer**: `test_semantic_layers.CommittedDeclarationTests`
  (and `test_archview`'s real-tree cell over the scanned modules) reds the
  DLL-free suite on a production module without a layer, a module in two
  layers, a tooling module with a layer, or a pin for a pair the declaration
  no longer calls backward.
- **Forbidden edges**: `[forbidden]` now gates. The DLL-free suite reds when
  the text scan's module edges cross one
  (`test_archview.test_no_forbidden_edge_is_crossed_on_the_text_scan`), and
  the compiled-IL step when the semantic graph does; a spec that is malformed
  or names no module reds too, so a typo or a renamed module cannot switch the
  gate off. Both are at none.
- **Compiler differences**: the edges come from the IL operands, locals and
  signatures of the Debug build CI makes. A Release build of the pinned tree
  reads the same 52 backward module pairs and 440 type pairs (only 3 lateral
  type pairs fewer, typed locals the optimizer drops); a compiler that emits
  one more typed local could add a pair, which the Debug-only pin rule and a
  re-pin in that PR absorb, the same exposure the residue ratchet has.

The text view: `archview.py --check` prints "Backward edges by DECLARED layer"
over the text type graph (with the module edge's file-reference weight)
right after the instability-based "Upward edges by MEASURED instability", and
the Atlas's "Where the layering breaks" draws both tables, labelled. The text
graph counts some pairs the compiler never emits and misses others (see
"What the scan is, and is not"), so its numbers differ from the pins; the
ratchet reads the IL.

```bash
python3 scripts/arch/layers.py Source/Parsek/bin/Debug/Parsek.dll               # report against the pins (always exits 0; the suite is the gate)
python3 scripts/arch/layers.py Source/Parsek/bin/Debug/Parsek.dll --write-pins  # re-pin (Debug build only)
```

At the pin (2026-11-11): 52 backward module pairs, 440 type pairs (the
heaviest Rewind -> Controllers 49, Recording -> Logistics 45, Ghost ->
Controllers 40, Recording -> Controllers 40, Recording -> Rewind 38), 54
lateral module pairs. Moving a module to another layer is a re-pin in the
same PR.

## Headless modules

Some modules exist to hold rules, math and data: code that should run with
no game loaded, so a unit test can drive it. `modules.toml`
`[headless.modules]` declares them, one reason each, and CI keeps their reach
into the live game from growing.

**The measure** is the game-coupling rule of "Code facts": a method can reach
the game when it names a live game type or member (body or signature), or
calls a method that can. Per production module, at the pin (Debug,
`python3 scripts/arch/headless.py <dll>`): the share of methods that can
reach the game, of those that do it directly (the method itself names a live
type or member) and of those that do it only through a callee, then the same
for instructions, and the leaks of the headless ones (`*`):

| Module | Methods | Can reach | Direct | Only via a callee | Instructions | Can reach | Direct | Leaks |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Core * | 191 | 7% | 3% | 4% | 10,202 | 7% | 3% | 14 |
| Trajectory * | 269 | 21% | 17% | 4% | 18,722 | 22% | 10% | 77 |
| LogIO | 95 | 15% | 7% | 7% | 4,890 | 23% | 13% |  |
| Missions * | 624 | 15% | 7% | 8% | 58,391 | 26% | 7% | 148 |
| GameActions | 2,137 | 18% | 8% | 10% | 147,933 | 28% | 14% |  |
| Timeline * | 92 | 12% | 0% | 12% | 6,653 | 29% | 0% | 5 |
| Logistics | 962 | 18% | 10% | 8% | 87,287 | 31% | 15% |  |
| MapRender | 723 | 15% | 7% | 8% | 28,526 | 32% | 18% |  |
| Recording | 2,565 | 25% | 17% | 8% | 203,797 | 38% | 26% |  |
| Crew | 286 | 25% | 17% | 8% | 22,147 | 39% | 21% |  |
| Rewind | 1,025 | 23% | 10% | 13% | 91,620 | 39% | 13% |  |
| SwitchIntent | 62 | 26% | 3% | 23% | 6,456 | 39% | 1% |  |
| Reaim * | 197 | 13% | 6% | 8% | 15,736 | 47% | 20% | 32 |
| Rendering | 203 | 23% | 15% | 7% | 17,240 | 48% | 28% |  |
| UI | 1,946 | 34% | 24% | 10% | 110,334 | 56% | 45% |  |
| Ghost | 2,115 | 37% | 26% | 11% | 172,408 | 57% | 46% |  |
| Display | 221 | 32% | 27% | 5% | 20,416 | 61% | 57% |  |
| Controllers | 2,555 | 40% | 28% | 12% | 209,110 | 64% | 51% |  |
| Config | 89 | 26% | 7% | 19% | 2,893 | 66% | 7% |  |
| Patches | 796 | 68% | 47% | 21% | 36,191 | 83% | 65% |  |

At the pin: 276 leaks in 122 methods, 29 seam calls.
The live things leaked to most: `UnityEngine.Object` 46, `CelestialBody` 43, `Orbit` 37, `Vessel` 18, `Contracts.Contract` 13, `KSP.UI.Screens.MissionControl` 10, `PatchedConicSolver` 6, `ProtoVessel` 6, `UnityEngine.UI.Button` 6, `VesselNaming` 5.

**What is declared, and why.** The choice reads each module's description
(`atlas.toml`) first and the share second: a module whose job is the live
game is not headless however small its number, and a module whose job is
rules is headless however long its leak list.

- **Core**: the kernel vocabulary (branch points, launch identity, the pure
  snapshot operations over ConfigNode, milestone and group stores); its few
  live touches (save paths, a body or transform read, the Re-Fly marker
  check) are its leaks.
- **Trajectory**: pure math over orbits and samples and the trajectory data
  vocabulary. Its live parts are by name: `PatchedConicSnapshot` (reads the
  stock patched-conic solver) and `OrbitReseed`; the leak list names them.
- **Missions**: the Missions tab model (composition trees, structure,
  periodicity, per-vessel rows). 85% of its methods cannot reach the game; the
  rest sit mostly in four types whose job is live and that were placed here by
  file name: `MissionControlStockUi` (the stock Mission Control annotation),
  `FlightGlobalsBodyInfo` (the live `IBodyInfo`), `VesselNameLiveSync` and
  `VesselRenameWriter`. They hold 142 of its 148 leaks, and are the obvious
  first moves.
- **Timeline**: builds the timeline's entries from ledger and recording
  events. None of its methods names a game type itself, but its display text
  calls live helpers in other modules: the time format (`ParsekTimeFormat`
  reads `GameSettings`), the facility and strategy name lookups, and the
  unfinished-flight resolver. Those five calls are its leaks.
- **Reaim**: re-aim planning (descent triggers, loiter compression, arrival
  hold), rules over recorded data; its live reads of bodies and orbits are its
  leaks.

Left out, with the reason: **Rendering** (its share of methods is close to
Reaim's, but its job is anchoring drawn trajectories to the live world:
`AnchorPropagator` and `ProductionAnchorWorldFrameResolver` are live by
design), **Logistics** (the route orchestrator and the live delivery-capacity
probe drive live vessels), **GameActions** (`KspStatePatcher` and the resource
modules patch KSP state), **MapRender** (the map probes and traces read the
live scene), **LogIO** (file I/O; ParsekLog is not followed by the rule
anyway), **Crew** (the roster), **Config** (reads the live game's settings).

**A leak** is one (headless method, live thing) pair, the method keyed by
`Type::Name`:

- `game:<Type>`: the method names a live game type in its body or signature
  (a member of a non-inert type counts as its type; a live member of an inert
  type is `game:UnityEngine.Quaternion::Euler`);
- `call:<Type>::<Name>`: the method calls a method outside the headless
  modules that can reach the game, or a slot declared outside them that a
  live implementation outside them fills, or a constructor or static method of
  a type outside them that fills a headless slot with live code, or a method
  outside them that hands such an implementation back (below);
- `static:<Type>::<Field>`: the method reads a static field of such a type
  (a singleton `Instance`).

Generated code no single source method owns (a nested non-capturing lambda
referenced only from inside the shared `<>c` cache, a field-initializer
lambda the compiler copies into two constructors) stays on its type's
pseudo-method `Type::(generated)` in the facts, and the gate treats that
pseudo-method as one more method of the type: its leaks are keyed
`Type::(generated) -> ...`. On today's tree the headless modules have seven
such pseudo-methods, all `<>c` constructors that name nothing live.

A callee INSIDE the headless modules adds no leak. If it can reach the game
it has a leak of its own further down the headless chain, so one new
`ParsekSettings.Current` read in a shared headless helper reds once, at the
helper, not at its hundred callers. If it is a slot (an interface or virtual
member) declared in a headless module and filled by a live implementation
elsewhere, the call is a **seam**: the caller is handed the implementation (a
parameter, an instance field), which is the shape the gate asks a leak to
take. Seam calls are counted and listed (`--leaks`), not gated: `IBodyInfo`,
declared in a headless module, is filled by `FlightGlobalsBodyInfo` and by an
in-game test's double, so `MissionLoopUnitBuilder`'s body queries are seams,
and `FlightGlobalsBodyInfo`'s own reads of `CelestialBody` are its leaks. A
seam the headless method wires itself is not a seam: constructing the live
implementation (`new KspClock()`), calling its static factory, or reading its
static singleton (`KspClock.Instance`) is a leak at that construction or
read, whatever slot the call then goes through. A type fills a slot when it
or a base it does not override there does (the first filler up the base chain
is the one a slot call runs). The wiring is walked over callers the way the
game reach is: a method outside the headless modules that constructs such a
type, calls its static members or reads its static fields hands the live
implementation back, and so does one calling such a method, so a call to a
factory on a third type (`Services.Clock => new KspClock()`) or to a locator
calling that factory is a `call:` leak whose detail names the path and the
live type. So once `FlightGlobalsBodyInfo` leaves Missions,
`FlightGlobalsBodyInfo.Instance.Radius(...)` in a headless method still reds. Every live headless method must reach a leak or a seam
through headless calls (`unexplained`, empty, held by a real-tree cell), so
the leak rule cannot drift from the coupling rule unseen.

`UnityEngine.Object` leads because `== null` on a Unity object calls into the
engine.

**The numbers in this section are the pin's.** `--write-pins` records the
report in a `[measured]` block of `headless_pins.toml` (the table, the totals,
the most-leaked things), and `test_ilheadless.ReadmeTests` (DLL-free) reds
when the table rows or the two lines under it are not exactly what that block
renders (`headless.readme_lines`), or when the block does not count its own
pinned leaks. The block is not gated against a build: the shares of every
module move with any change, so they are the measure at the last re-pin, and
a re-pin that moves them must move this section too.

**What reds**, in CI's compiled-IL step (`test_ilheadless.RealTreeHeadlessTests`,
pins in `scripts/arch/headless_pins.toml`; its `WorkflowTests` cell reds if
the step's pattern stops matching the file), each shown by a mutant of the
real facts and by an inline-IL cell:

- a headless method newly naming a live Unity or KSP type
  (`test_a_headless_method_newly_naming_a_game_type_reds`);
- a headless method newly calling a live method of a module that is not
  headless (`test_a_headless_method_newly_calling_a_live_method_of_another_module_reds`);
- generated code no method owns newly naming a live type
  (`test_generated_code_no_method_owns_is_gated_on_its_type`; inline:
  `test_a_nested_lambda_left_to_its_type_reds`,
  `test_a_field_initializer_lambda_in_two_constructors_reds`);
- a headless method constructing a live implementation of a headless slot
  (`test_constructing_a_live_implementation_of_a_headless_slot_reds`; inline,
  with the static factory and the static singleton:
  `test_a_live_slot_implementation_hard_wired_reds_in_every_form`; a factory
  on a third type, a locator calling it and a subclass inheriting a live fill:
  `test_a_live_implementation_handed_back_by_other_live_code_reds`);
- a rename of a leaking method, or of the live method it calls: the pins are a
  set keyed by name, so the renamed leak is new and needs a re-pin, as a
  renamed complexity-watch method does (`test_a_rename_of_a_leaking_method_reds`).

The failure names the headless method, the live thing, how that thing
reaches the game (`calls X, which reaches the game: X -> Y -> game:HighLogic`)
and the choices: move the call behind an injected seam, add the type to
`[coupling] inert_types` in `facts.toml` with a reason if it really works with
no game loaded, or re-pin with the reason in the PR.

The DLL-free suite reds on: a malformed declaration (no `[headless.modules]`,
a name no rule declares, a tooling module, a missing reason, an unknown key;
`DeclarationTests`); a module added to or removed from the declaration
without a re-pin (the pins record the module list); any edit of the
`[coupling]` lists of `facts.toml` (`game_assemblies`, `inert_types`,
`live_members`, `not_followed`) without a re-pin, because the pins record them
too (`test_a_widened_inert_list_reds_until_a_repin`); and this section's
numbers drifting from the pins (`ReadmeTests`). Without the coupling record a
widened inert list would pass the set gate, since leaks only vanish
(`test_a_widened_inert_list_hides_leaks_from_the_ratchet_alone`), and the
re-pin diff shows the entries that changed.

**What cannot red**, each held by a cell: deleting a method, leaking or not
(slack is reported until a re-pin; `test_deleting_a_leaking_method_passes_and_reports_slack`);
adding a pure method, or a call to an inert type such as `Vector3` or
`ConfigNode` (`test_adding_a_pure_method_or_an_inert_call_passes`); a new
call from a headless method to a live headless method
(`test_a_new_call_to_a_live_headless_method_adds_no_leak`); a new call through
a headless seam, a parameter change of a leaking method (overloads merge by
name), and a compiler choosing another shape for generated code: a `<>c`
lambda cache, a closure class and a local function give the same leaks
(`test_what_must_not_red`,
`test_every_compiler_shape_of_generated_code_gives_the_same_leaks`); an
implementation handed in as a parameter or held in an instance field
(`test_an_injected_implementation_stays_a_seam`); unattributed generated code
that names nothing live (`test_a_pseudo_method_names_no_leak_when_its_code_is_pure`);
a call to live-module code that hands back no live implementation, or to a
subclass overriding the live fill with pure code
(`test_live_code_that_hands_back_no_live_implementation_stays_out`).
A pinned leak that disappears passes; a re-pin drops it.

Limits:

- "Can reach", not "needs": a method is live when SOME path reaches the
  game, however unlikely the branch, so a leak can sit behind a guard that
  never fires without a game.
- Interface and virtual calls reach every implementation in the assembly,
  tooling included (an in-game test's double counts), except through a seam,
  which is never a leak. A seam is an honest exit only when the implementation
  is handed in: the gate shows a call moved behind a seam as gone even though
  the live implementation still runs in the game. Injection through a static
  assigned elsewhere (a static field or property of the slot type that some
  other code sets, `Services.Clock = new KspClock()` at startup) is counted as
  a seam: the wiring rule follows constructions and static reads of the live
  type itself, not stores into a slot-typed static, and that is its known
  limit. Generated code no method owns outside the headless modules does not
  start a wiring path either.
- The unattributed pseudo-method gives the leak to the type, not to the
  method that wrote the lambda, and the coupling shares of "Code facts" do not
  follow it (a source method that calls only into it reads as unable to reach
  the game).
- A delegate is followed only where it is created (an `ldftn` of a live
  method is a call); invoking a delegate the method was handed reaches
  nothing. ParsekLog is not followed (its test sink). Reflection and
  string-keyed lookups are invisible.
- Overloads merge by name: a second overload of a leaking method that leaks to
  the same thing adds nothing.
- The leak sets are not monotone in the declaration: a call into a module that
  stops being headless becomes a leak of its caller (dropping Missions reds
  Reaim's pins as well), so adding or removing a module is a re-pin of the
  whole file.

```bash
python3 scripts/arch/headless.py Source/Parsek/bin/Debug/Parsek.dll                # table, leak count, pins check
python3 scripts/arch/headless.py Source/Parsek/bin/Debug/Parsek.dll --leaks        # every leak and seam, with how it reaches the game
python3 scripts/arch/headless.py Source/Parsek/bin/Debug/Parsek.dll --write-pins   # re-pin (Debug only; refused from a Release path)
```

`archview.py --check --facts <dll>` prints the table and the leak count as
HEADLESS MODULES, the Atlas draws them under "Code facts", and the metrics row
carries them as `facts.headless`. The gate adds about 6 s to CI's
compiled-IL step (measured after the shared parse): the leaks come out of the
game reach `ilanalysis.report` already computes, and each real-tree mutant
reruns the reach, which a per-pass memo of the name-pattern match keeps near
0.1 s (`direct_game_refs`' `memo`; the coupling answers are unchanged).

## Log tags (compiled IL)

Every Parsek log line is `[Parsek][LEVEL][Tag] message`. The harness counts
lines per tag in each flight's KSP.log (`harness/lib/runindex.py`, the lane
health); `scripts/arch/logtags.py` says which modules write each tag, so a count
by tag becomes a count by module and "which lanes exercise module X" has an
answer.

It walks every method of the ikdasm dump and follows the evaluation stack to
each call of a `ParsekLog` emitter (`Info`, `Verbose`, `Warn`, `Error`, the
rate-limited and on-change variants, and `Write`, whose tag is its second
argument). The tag argument resolves when it is a literal (a `const` compiles to
one), a local that only ever held one literal, a `static readonly` string whose
type constructor stores exactly one literal, or a parameter of a forwarding
helper (`FileIOUtils.SafeWriteConfigNode(..., tag)`, the `PartStateSeeder`
seeders), whose own callers then count as sites of the literal they pass, to a
fixed point. Two values that meet at a branch target merge to unknown; a call
result or a field is never guessed. The unresolved sites are listed by method
under `nonLiteral`. A site belongs to the module `modules.toml` gives the calling
type's source file.

```bash
python3 scripts/arch/logtags.py Source/Parsek/bin/Debug/Parsek.dll           # summary: tags, sites, top tags
python3 scripts/arch/logtags.py Source/Parsek/bin/Debug/Parsek.dll --write   # refresh scripts/arch/log_tags.json
```

`log_tags.json` (schema `parsek-log-tags/1`) holds per tag its site count, the
modules with their site counts and the levels (`mixed` for a helper that writes
two), plus the non-literal sites and the forwarding helpers; sorted, no paths,
so it changes only when the logging code does. The harness reads the FILE and
never imports this module. Staleness is report-only on both sides: the harness
counts a tag the map lacks as unmapped, and `test_iltags.py`'s real-tree cell
(run by the compiled-IL CI step, `-p 'test_[ims]*.py'`) prints the tags new or
gone against the build and fails only when the extractor stops modelling the
assembly. Refresh the file after a change that adds or moves log tags. At the
first write: 284 tags, 9294 sites with a literal tag, 13 non-literal sites, 46
forwarding helpers.

## Metrics history

Every number above exists only for the commit it is run on. The history keeps
one machine-readable row per commit pushed to `main`, so trends can be drawn
later. It gates nothing: no check reads it, and no row can fail a PR.

- **Where.** Branch `metrics` (an orphan branch: no source, only `README.md`
  and the history), file `metrics.jsonl`: one JSON object per line, in the
  order the rows arrived, at most one row per commit sha. The same branch's
  `flights/` folder holds the harness run index (flight results, published by
  `harness/tools/run_index.py publish`, commit subjects `flights: `); the
  metrics job commits only `metrics.jsonl` and never touches it
  (harness/README.md, "The run index and lane health").
- **What a row holds** (schema `parsek-metrics/1`; `scripts/arch/metrics.py`'s
  docstring lists every key): `commit` (sha, committer date in UTC),
  `measured` (which halves were measured), `text` (the text scan: production
  files and lines per module, module count, forbidden-edge violations, upward
  edges, the declared-layer numbers, knot sizes, greedy and replayed residue,
  first cut), `semantic` (the semantic report's headline: knot and residues
  for the text, raw IL and semantic graphs, edge counts, forwarder rewrites,
  the declared-layer numbers the layer ratchet reads), `facts` (the code facts:
  the three ratchet sets' sizes, the static-state totals, complexity at 20 /
  50 / 100, game coupling overall and per module, unreached methods and their
  instructions, test reach per module (with `types`, the production types
  and those a test reaches directly), `lifetimes`, the static-lifetime
  counts of "Static lifetimes", `headless`, the leak counts of "Headless
  modules", and `coverage`, the committed coverage snapshot's commit, date,
  line share and line share per module (null without a snapshot; the
  snapshot's commit and date, not the row's), all additive keys), `sizes` (`scripts/count-code.py`:
  lines per area and the test totals) and `history` (additive: the "Change
  history" anchor, rename accounting, and per window its start, commits,
  units, fix units and units by kind, plus fix units per module for 90 days
  and 18 months; null when the history was not measured, the kinds null when
  only they were not). Nothing is measured by new logic: the
  row is those tools' own output, built in one process so `Parsek.dll` is
  disassembled and parsed once. Without a DLL the `semantic` and `facts`
  halves are null; without the test DLL `facts.testReach` is.
- **How it gets there.** Rows exist for pushes to `main` only. On such a
  push CI's `tests` job builds the row (with test reach) after the
  compiled-IL step and uploads it as the `metrics-row` artifact (14 days);
  both steps are `continue-on-error`, so the history can never turn the
  required check red. The `metrics` job (the workflow's only job with write
  access, main pushes only) then appends it with
  `scripts/arch/metrics_append.py publish`: a worktree of the checkout on the
  branch (created as an orphan when missing), append, commit as
  `github-actions[bot]` (`metrics: <short sha>`), push, and on a rejected push
  re-fetch, re-append and retry. A sha already in the file changes nothing.
  When the row step failed there is no artifact and the `metrics` job fails
  at its download, which is where a broken tool shows. The job is
  `continue-on-error` too: its failure is a red job in the run's job list,
  and the run's conclusion (and the README badge) stays that of `tests`.
- **Pull requests** keep no row. Their compiled-IL step only proves that a
  row can still be built: `test_metrics.RealTreeRowTests` assembles one (no
  test reach) from the model, semantic result and facts the step's other
  real-tree cells already hold, for about 10 s.
- **Gaps.** A commit can have no row. GitHub keeps at most one pending run
  per concurrency group, so when merges land back to back a queued `metrics`
  job (or workflow run) can be cancelled, and a failed row step leaves none.
  Read the history as a sample of `main`, not a complete list.
- **Reading it.**

```bash
git fetch origin metrics
git show origin/metrics:metrics.jsonl | python3 -c "
import json, sys
for line in sys.stdin:
    r = json.loads(line)
    print(r['commit']['date'], r['commit']['sha'][:9], r['text']['knot'],
          (r['semantic'] or {}).get('ratchetResidue'), (r['facts'] or {}).get('ratchets'))"
```

Build a row locally (after a Debug build; about 100 s, 70 s more with the
test DLL):

```bash
python3 scripts/arch/metrics.py --sha "$(git rev-parse HEAD)" --date "$(git show -s --format=%cI HEAD)" \
    --dll Source/Parsek/bin/Debug/Parsek.dll [--tests-dll Source/Parsek.Tests/bin/Debug/net472/Parsek.Tests.dll]
```

A key may be added without a schema change; renaming or removing one bumps
the schema id, and `metrics_append.py` refuses a schema it does not know.
Rows from a Release build or another compiler are not comparable (see "Member
facts"); CI measures the Debug build.

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
- `--facts PATH` (default off): Parsek.dll, an `.il` dump or an
  `ilfacts.py --json` file; draws the atlas's "Code facts" section, which
  otherwise says "not measured" (the text scan needs no DLL).
- `--test-facts PATH`: the test assembly (or its dump or facts JSON), for
  test reach and the test-only split in that section.
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
upward edge by measured instability (a more stable module depending on a less
stable one), the declared layers with every backward and lateral module pair
of the text type graph (see "Declared layers"), every
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
with its weight, referenced types and referencing files (the suite reds on
any), and last the SIZE
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
so the kernel's 469-type knot collapses into level 10 and only the types above
it (levels 11 to 14 here, mostly patches and entry points) rise above. A knot
is why that level is flat: the levelling cannot order types that depend on each
other.

`types.json` records this per type: `knot` is the 1-based index of the type's
component in largest-first order, or null outside a knot, and `sublevel` is
the type's level inside its knot after the cut edges are removed (again null
outside). On the current tree, the refactor branch claude/project-thread-bkwmrr,
the knots are one of 469, then of 4 and 3 (re-derived 2026-11-06 on main's 1.0.0
code plus the branch's re-cuts: placing 50 root files the map had skipped took
the kernel to 584 on 2026-11-01 and merged the 7-type stock-screen decoration
knot into it; teaching the scanner that a field, property or enum member
spelled like a type is not a reference to it split off a 3-type stock-UI grey
knot, leaving 542 on main's code. Making `Recording` plain data again and
moving `GameAction`'s real `CaptureSequence` call to the leaf
`CaptureSequenceCodec` take it to 457 here; those re-cuts are not on main. Moving the
segment labels out of `RecordingStore` into `RecordingSegmentLabels`, which still reaches
the knot through `RecordingOptimizer`, adds one: 458; moving the chain queries into
`RecordingChainQueries` adds one more: 459; moving the rewind spawn scope into
`RewindSpawnScope` adds one more: 460; the file janitor in `RecordingFileJanitor`
one more: 461. File-placement PR 8's `LiveVesselSnapshot` adds none: the
capture entry points that reach knot members stay on `FlightRecorder`, so it
sits on no cycle; the optimization pass in `CommittedListOptimizer` one
more: 462; the restore attempt in `CommittedTreeRestoreAttempt` one more: 463;
the pending-tree slot in `PendingTreeSlot` one more: 464; the switch-segment
attempts in `SwitchSegmentAttempts` one more: 465; the tree discard in
`TreeDiscardService` one more: 466; the tree commit in `TreeCommitService`
one more: 467; the rewind carry in `RewindCarry` one more: 468; the rewind launcher in
`RewindLauncher` one more: 469).
It was 301, 7, 4, 2 and 2 on 2026-09-25; 286, 4, 2 and 2 on 2026-09-22; and
391, 4 and 2 before the 2026-09-14 pass made `ParsekLog` and `Recording` leaves.

`--check` first lists the first 10 knots (all three on the current tree) with
their size and module breakdown, then for the largest knot it shows the hubs
(the members other members reference most, with their in-knot references) and a
**greedy cut sequence**: each step picks, among the highest-fan-in members, the
sink whose outgoing references, when removed, break the knot the most, and
reports the references that were dropped. On main's code, `542 -> cut Recording -> 496`
means removing `Recording`'s two references inside the knot
(`LaunchSiteOnlyGhost`, `SidecarVersions`) splits off 46 members, and the
second row, `GameAction` (`CaptureSequence`), 40 more; this branch makes both
cuts in code, so its sequence starts at `469 -> cut Route -> 451`, then
`451 -> cut VesselLaunchIdentity -> 433` (its `RecordingSidecarStore` and `VesselNameStore`
references; the flight controller was second until a 2026-11-07 pure move deleted
one unrelated reference and reordered the greedy steps). Some cuts are
cheap inversions like those - one or two references between hubs holding a
whole region together, and those are the references a refactor should look at
first - while the rest touch many small references at once (the `RecordingStore`
cut drops 24 on the current tree), and the named
sink there often needs a new abstraction (an interface, a data boundary) rather
than moved edges. The table is a research list, not a verdict - the tool never says a
reference is wrong, only that it holds a cycle together. Nothing in this phase
changes any dependency; it reports them.

The residue after the last cut is a ratchet. Since 2026-11-07 (W4a2) it reads
the **semantic graph** (next section's "The semantic graph", read from the
compiled IL), in `scripts/arch/test_semantic.py`
(`test_the_semantic_residue_ratchet`, bound 150, the semantic residue on
668a03255, a tree that still passed the text bound 179): refactor work must
not loosen it, not even for a new type that only splits an existing residue
member - keep such a type off the cycle or make it a partial of a type already
in the knot. A call through a forwarder counts there as a call to the
forwarded type, so adding or deleting a forwarder no longer moves it. The
ratchet runs where `PARSEK_ARCH_IL` names a built DLL: in CI after the build,
and locally with
`PARSEK_ARCH_IL=Source/Parsek/bin/Debug/Parsek.dll python3 -m unittest discover -s scripts/arch -q`
after a Debug build of the tree under test (a stale DLL measures old code).
Measure the Debug DLL, the one CI builds: a Release DLL can read lower,
because the optimizer drops typed locals the source names (on 5dc4ebd78 the
Release build has 19 fewer semantic edges than Debug and 11 text edges no IL
explains; the residue happened to be 250 in both), so a bound pinned from a
Release build can red CI. Without `PARSEK_ARCH_IL` the ratchet cell skips, so
`WorkflowRunsTheRatchetTests` reads `.github/workflows/tests.yml` and reds when
no step after the build runs the semantic cells with `PARSEK_ARCH_IL` set to
the built Debug DLL (step deleted, commented out, conditional,
`continue-on-error`, re-pointed, or a discover pattern that skips the file).

Because the greedy sequence is a heuristic, deleting a real reference can
change which sink wins an early step and leave a larger residue after the same
12 steps, so the ratchet measures the smaller of the greedy residue and the
**replayed residue** (`replay_cut_residue`): the largest component left after
removing the outgoing references of a fixed, pinned cut order
(`SEMANTIC_CUT_ORDER` in `semantic.py`, the semantic greedy order on
668a03255). For a fixed set of cut types, deleting a reference can only shrink
the remaining components, so a pure move that removes a reference can never red
the ratchet; adding a reference, or a new type that sits inside the residue,
still can. The printed and documented sequence stays the greedy one.

The text scan's own residue (`test_archview.py`'s
`test_cut_sequence_halves_the_largest_knot`, replayed over
`archview.RATCHET_CUT_ORDER`, 179 on 668a03255) is still measured and
reported, no longer bounded: it moves when a forwarder is added or deleted.

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
`VesselLaunchIdentity`, `VesselSnapshotOps`, `MilestoneStore`,
`GroupHierarchyStore`, `InventoryManifest`, `PlaybackTrajectoryBoundsResolver`):
used across many modules, so no owner has a majority, and that is the intended
meaning of Core from here on.

`VesselSnapshotOps.cs` is the ninth and the newest: the 2026-09-14
`VesselSpawner` split created it as the pure-over-ConfigNode subset the ledger,
Logistics, the route proof and the UI all call, and named it in the `Core` rule
in the same PR. The 2026-11-07 placement PR 6 moved the crew-in-snapshot and
situation helpers into it as well (18 declarations; the two crew members that
read the roster or `CrewReservationManager` stayed in `VesselSpawner`, which
keeps it out of the type knot), and PR 7 the position-stamp, rotation-node,
validation and canonical-orbit writers (28 declarations). Its R2 evidence then
reads `externalRefs=11 share=0.45 top=Logistics(5) second=Controllers(2)`,
which still passes R2 (the top module has at least twice the second's
references), so the placement report lists it under "Placement rules whose destination does not
match the current map": the mechanical rule would send it to Logistics, the
operator decision keeps it in the kernel. That line is the standing note about
the disagreement, not a defect.

Placement PR 8 moved the live-capture half of `VesselSpawner` into a new
`LiveVesselSnapshot.cs` (42 declarations: `TryBackupSnapshot`, `SnapshotVessel`,
the snapshot normalizers, the body-name / body-index registry with its three
test hooks, and the whole max-distance family). Its R0 rule places it in
`Recording`, whose recorders, finalizers and Re-Fly invoker are its callers.
As a new type on a cycle it added a knot node and loosened the residue ratchet
by one, so a follow-up moved the four members that carry its knot edges
(`TryBackupSnapshot`, `SnapshotVessel` and the two private helpers behind
the first) onto `FlightRecorder` as the partial `FlightRecorder.LiveSnapshot.cs`;
`LiveVesselSnapshot` keeps the rest and sits on no cycle.
Placement PR 9 then took `VesselSpawner.cs` itself off the `Core` list: an R0
rule places it in `Controllers`, whose scene controllers run the spawn,
respawn and end-of-recording paths it keeps (it references 12 `Controllers`
types). Upward edges went 83 / 812 -> 81 / 786: `Core -> Recording` 25 -> 12,
`Core -> Controllers` 13 -> 1, and the `Core -> Crew` and `Core -> Ghost` edges
gone, against `Ghost -> Controllers` 50 -> 58, `Recording -> Controllers`
39 -> 43 and one more each from `Rewind` and `Crew`. PR 10 split the class
into partial files (`VesselSpawner.cs` plus eight `VesselSpawner.<Area>.cs`,
written by `scripts/refactor/split_vesselspawner.py`); the same R0 rule names
them, and no edge moves.

## The atlas

`atlas.html` is the narrative page over the other views and the one findings
report: headline tiles (source files, production types, types with no
dependencies, the largest knot's size and share, and, when git history is
available, the highest module co-change ratio and the share of commits that
touch the most-churned type), the "Main findings" list, the map inline, the
module directory grouped by layer, the top hub vocabulary, the largest knot
with its cut table, the weighted upward edges and the backward edges of the
declared layers, co-change, the largest files
and types, "Code facts" (the compiled-IL analyses above, drawn when
`--facts` is given), "Opportunities, ranked", and a reading order. Everything numeric,
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

Numbers written inside prose are checked only where a pattern in
`PROSE_NUMBER_CLAIMS` (archview.py) knows what they count: production
modules, upward module edges, tooling files, the largest knot, the residue
after the pinned cut order, production types, methods of 90 lines or more and
production files of 1,000 lines or more. A prose string that carries a
`YYYY-MM-DD` date is a dated snapshot, so a mismatch there is history and is
only listed ("dated prose numbers the model has moved past"); a string with no
date claims to describe the tree, so a mismatch there is listed as an
undated prose number and fails `test_atlas_prose_matches_the_tree`. Other
numbers are not checked; the generated numbers around them are. `--check`
does not silently show stale prose: its ATLAS section (after KNOTS) lists
production modules without a summary, glossary entries naming a type that is
not in the model, glossary entries outside the live top 18 (informational),
upward readings whose edge no longer exists, reading-order types not in
the model, opportunity `types` entries not in the model, and the two prose
number lists; it then prints
the findings review date with its age in days, plus a line when that is older
than 30 days (`FINDINGS_STALE_DAYS`). All of it is informational: none of it
fails the check (the real-tree tests gate on some of it).

## Change history

The module map says which files reference each other; `git log` says which
files actually change together, how often, and what kind of change it was.
`history.json` is built from three windows over one history read, plus a
change-kind pass over the merged pull requests.

### The commit read and renames

One `git log --no-merges --date-order -M -l0 --name-status -- Source/Parsek`
call reads every commit since the long window's start (a two-day margin goes
to git, because `--since` keeps the wall-clock time of day, and the exact cut
is then made on the committer date). Only `.cs` files count. The walk runs
newest first and keeps one map, old path to the path the file has at the
commit read:

- `R<score> old new` sends every OLDER commit of `old` to wherever `new` ends
  up, so a file renamed twice resolves to its final name, and a module move
  (`Ghost/X.cs` from `X.cs`) leaves the per-file counts, hotspots and module
  tables exactly as they were. The rename commit itself counts as a change of
  the file when it edited it (`R099` and below).
- A late commit is followed too. The log is in date order, not topological,
  so a branch forked before a rename can edit the old name with a LATER
  date than the rename: the walk meets that commit first and resolves it to
  the old name, which no longer exists. Such a commit (its name not alive at
  the commit read, not ended by a deletion) takes the name the old path
  resolved to at the rename, which already carries every newer rename and
  deletion, so a file a deletion ended stays ended. The hop is taken only
  when the name left exactly once in the window (one rename away, no
  deletion); a name renamed twice, or renamed and deleted, cannot say which
  file the late commit meant and stays unresolved (dropped). Hops repeat
  while the name is still not alive and stop on a name already visited. Every
  branch open across the module move is this case.
- `R100` is a pure move and NOT a change: it is not counted for the file, and
  a commit made only of pure moves is not counted at all ("pure-move commits
  not counted"). It also never makes a sweep.
- Only a deletion ends a path's identity for older commits: a `D`, or the
  old side of an `R` (whose older commits go with the renamed file). A path
  deleted and created again later does not inherit the dead file's history,
  and neither does a path reused after a rename. An add alone (`A`, the new
  side of an `R` or `C`) ends nothing: two parallel branches that each
  created the same file show two adds and no delete, and every commit of
  both is that file's.
- The final path must exist at the commit read (`git ls-tree -r HEAD --
  Source/Parsek`). A commit whose every changed file is deleted code is
  dropped and counted ("commits dropped"); a commit that also touched a live
  file keeps only the live ones. This also catches a file deleted on a
  merged side branch, which `--no-merges` never shows as a `D`.
- A commit changing more than 40 source files (`HISTORY_SWEEP_LIMIT`) is a
  sweep (a rename wave or a bulk edit) and is skipped, because it would
  dominate every co-change number; the count and the largest one are
  recorded. Pure moves are not changes, so a wave of `R100` moves is never a
  sweep.

`git log --numstat` for the growth column reads the same way (`-M -l0`, both
`old => new` and `dir/{a => b}/f.cs` path forms parsed) and resolves through
the same map, so a renamed file's growth follows it.

**Splits are not renames.** A partial file born beside its base
(`ParsekFlight.MergeFlow.cs` next to `ParsekFlight.cs`) is a new file: its
history starts at its first commit, and everything before the split stays
with the base. The split is counted, not followed ("Splits not followed": the
partial files, the base files, and the distinct commits that touched a base
before its first split; the per-base rows count a commit once per base it
touched, so their sum is larger). Copy detection was measured on 2026-11-12 and rejected: `-C`
and `-C -C` both find the same 10 copies in the window and no more renames
(25 either way), all of them a Phase B lift or a large extraction scoring
55-79% (`ParsekFlight.Spawning.cs` to `ParsekFlightSpawnHost.cs`, `ParsekUI.cs`
to `UI/RecordingsTableUI.cs`); crediting the source's history to the copy
would count each commit twice, `-C -C` costs about 24 s of git time, and a
copy is a split by another name.

### The windows

Three windows, all anchored to the committer date of the commit read, never
to the wall clock, so a rebuild of an old commit reads the same numbers:
**30 days** and **90 days** (from the anchor minus 30 / 90 days) and **18
months** (from the first day of the month 18 months before the anchor, the
window the co-change pairs, the module table and item 7's ranking read). The
page and `--check` both print the anchor and the three starts. The windows
are nested; each window's commit count is every kept commit in it.

The payload records the anchor, the windows, the kept commit count, the
sweeps, the rename accounting, and these arrays:

- **files**: commits per production file in each window, last-touched date,
  and a churn rank (18 months);
- **hotspots**: `file commits * fan-in` for every production type, top 30
  (18 months), with the 30- and 90-day products beside it, which finds types
  that are both widely referenced and frequently edited.
  For a partial class, `file commits` is the **union** of the commits that
  touched any file the type is declared in: a commit that edited two parts of
  the same type counts once, where summing the per-file counts would count it
  twice. Attributing the type to one file instead (the first the walk met)
  read `GhostMapPresence` as a 2-commit file and dropped both it and
  `ParsekFlight` out of this table entirely, which is how the 2026-09-14
  hotspot reading came to say almost nothing about them. A file that declares
  several types lends its commits to each of them, so two types in one file
  carry the same commit count;
- **modules**: commits touching each module, per window;
- **modulePairs**: for every production pair, the commits touching both, the
  union, and their Jaccard ratio (18 months);
- **filePairs**: the top 40 cross-module file pairs with at least 5 shared
  commits - files the map calls separate that keep changing together;
- **windows**: per window, its start, commit count and the top 15 files and
  hotspots.

Tooling modules are excluded everywhere. A co-change number is evidence, not
a verdict: two files can share commits because one change touches both sides
of a real boundary, and that is what the list is for.

### Change kinds

A commit count says how often a file changed, not why. The change-kind pass
reads the first-parent chain of the commit read
(`git log --first-parent --diff-merges=first-parent -M --name-status`): each
first-parent commit is one **unit**, a PR merge (`Merge pull request #N`, the
PR title the body's first line, the PR's file set its first-parent diff), a
direct commit, or any other merge ("merge", a branch synced with main). Its
kind comes from the CHANGELOG lines the unit added, else from its paths.

**CHANGELOG lines.** A second call reads every unit's `CHANGELOG.md` patch
(`-U0 --no-renames`) from the first commit and rebuilds the file oldest
first; each added line is placed in the post-image, so its `###` section is
the one it was really added under, even inside a hunk that shows no heading.
The rebuild must equal `git cat-file -p HEAD:CHANGELOG.md` byte for byte;
when it does not (a history rewrite, an unreadable patch) the kinds read
"not measured", never a guess. Sections, matched on the lower-cased heading,
first match wins:

| Heading | Kind |
|---|---|
| `Highlights` | ignored (it repeats entries listed elsewhere) |
| contains `fix` (`Fixed`, `Bug Fixes`, `Fixes`) | fix |
| `Added`, `Features`, `New Features` | feature |
| `Changed`, `Enhancements`, `Improvements`, `Breaking Changes`, `UI`, `Settings`, `Defaults`, or contains `improvements` | change |
| `Dev`, `Internals`, `Tests`, `Test coverage`, `Documentation`, `Developer tools`, `Code quality`, `Code refactor`, `Architecture`, `Maintenance`, `Log hygiene`, `Log cleanup` | dev |
| anything else (the early topic headings) | other |

A bullet whose bold lead says it is dev work (`- **Dev: ...**`,
`**Automated testing:**`, `**Harness:**`, `**Tests:**`, `**Tooling:**`,
`**Internal:**`, `**Housekeeping:**` and their variants) is dev whatever
section holds it. A unit that added lines under several kinds carries all of
them; its **primary** kind is the first in fix > feature > change > dev >
other.

**Paths, when the unit added no CHANGELOG line.** Every path the unit touched
falls in one class: docs (`*.md`, `docs/`, `LICENSE`), tests
(`Source/Parsek.Tests/`, `Source/Parsek/InGameTests/`), harness (`harness/`),
code (the rest of `Source/Parsek/`, `GameData/`), tooling (everything else).
Production code with no CHANGELOG line is **unlabelled** (counted honestly,
never guessed). Otherwise docs are set aside (docs move with every change);
one remaining class names the kind (tests, harness, tooling), none leaves
docs, two or more is mixed, and a unit with no path is empty.

A **fix unit** is one whose kinds include fix, whatever its primary kind.
Units are attributed to the current names of the Source/Parsek files they
changed (renames followed over the first-parent chain, pure moves not
counted, more than 40 source files a sweep counted by kind but attributed
nowhere). The Atlas history section and `--check` show the units per window
by kind, "where fixes land" (fix units, units and commits per module, 90 days
and 18 months) and the top files by fix units. All of it is report only:
nothing pins a kind count, because a wording change in one CHANGELOG entry
moves it.

### Not measured

A shallow clone (`git rev-parse --is-shallow-repository`), a missing git, or
a failed read gives "not measured" with the reason, in `history.json`, the
Atlas, `--check` and the metrics row (`history: null`), never a table of
zeros. CI checks out with `fetch-depth: 0`.

### Known limits

- `--no-merges --date-order` walks a side branch's commits in committer-date
  order, not topologically. An edit of the old name on a parallel branch
  that lands later in date order than the rename is followed by the late
  rule above when the name left once; when it was renamed twice, or renamed
  and deleted, the commit is dropped. The 2026-11-12 read has no late pair,
  so the rule moved nothing there; the module move makes one of every branch
  open across it. A file deleted only by a merge commit and never re-created
  is caught by the alive filter.
- The walk sees no ancestry, so a deletion that happened only inside a merge
  commit (never a `D` row under `--no-merges`) is invisible to it: if the
  path is later created again as a different file, the dead file's older
  commits are credited to the new one. The rule takes that error (same path,
  usually the same role) over the opposite one: ending the identity at every
  add dropped the older branch's commits of a file two parallel branches
  each created, which on 2026-11-12 cost 7 live files 10 commits
  (`TrackSection.cs`, `SegmentEvent.cs`, `KerbalEndState.cs`,
  `KerbalsModule.cs` and three in `GameActions/`). The 6 paths deleted and
  created again in the window all show their `D`, so they keep the
  protection.
- The growth numstat sums non-merge commits, so whatever the merges that
  joined parallel branches did is not read: a file both branches created
  counts its creation twice (`GameActions/GameAction.cs` sums +4741 against
  2309 lines today), and so does the same edit made on both branches, while
  a conflict resolution's own lines are missing. Net lines added in a window
  cannot exceed the file, so the size view caps a file's growth at its
  current line count, marks the row with `*`, and says how many rows were
  capped: 82 of the production files on 2026-11-12, the median by 24 lines,
  `GameAction.cs` by 2432, `Logistics/RouteOrchestrator.cs` by 918,
  `RelativeAnchorResolver.cs` by 726, `ParsekFlight.cs` by 688. Below the
  cap the column still carries the same error; read it as a trend, not a
  count.
- A first-parent merge that is not a PR merge ("merge") carries everything
  the other side brought in, so its paths are broad; there was one in 18
  months.
- On a pull request's CI the commit read is the merge ref, which is itself a
  unit with the PR's whole diff.
- The CHANGELOG is the only kind source: a fix PR whose entry was filed under
  Changed counts as change, and a production PR without an entry is
  unlabelled.

`--check` prints the HISTORY section after ATLAS: the anchor and windows, the
rename accounting, the 30- and 90-day top files and hotspots, the 18-month
hotspots, pairs and forbidden-edge co-changes, then the change kinds. The
atlas has a "What changes together" section between "Where the layering
breaks" and "Largest files and types": the busiest files and the hotspots
with three window columns, the module pairs and cross-module file pairs, the
change kinds table, "where fixes land" per module and the top fix files.

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
  one extra `git log --numstat -M` call over `Source/Parsek`, summed over the
  type's files. A renamed file's rows follow it to its current name (the
  change history's rename map); binary rows and sweep commits (more than 40
  source files, the same rule the co-change history uses) are ignored, and a
  commit of deleted code only is not counted, so the commit count equals the
  history's. A file's growth is capped at its line count (`*`, see "Known
  limits" under "Change history"). With git missing the column reads `n/a`
  and nothing else changes.

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
  exist. The checker reports each one it finds, and the arch suite reds on it
  (see "Declared layers").
- `[layers]`: `order` (lowest first) and `[layers.modules]`, one list of
  modules per layer. A new production module gets a layer in the same commit,
  or the suite reds (see "Declared layers").
- `[allowed] edges = [...]`: while empty, the allowlist section is skipped.
  Once any edge is listed, every production edge not listed is reported,
  which turns the checker into a strict allowlist without changing its
  report-only exit code.
