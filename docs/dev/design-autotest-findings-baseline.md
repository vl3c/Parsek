# Design: Per-Save Known-Findings Baseline (Module M-A1 follow-on)

Status: IMPLEMENTED (branch `autotest-baseline`, 2026-07-11). A reporting-layer extension of the Offline Recording
Analyzer (`docs/dev/design-autotest-offline-analyzer.md`, module M-A1). This is the
Step 3 design doc; the motivating facts are recorded below under Problem. Plain
ASCII, no em dashes.

This doc is written against the POST-M-A3 shape: the pure invariant core is being
relocated to `Source/Parsek/Analyzer/` under namespace `Parsek.Analyzer` with the
evaluate step split out as `InvariantEvaluator.Evaluate`; the file-facing driver
is `OfflineAnalyzer.Run` (today `Analyzer.Run`). Members are referenced by name,
not path, so this survives that move. Where a name differs pre/post-move it is
called out once.

---

## Problem

The analyzer's red gate (`AnalysisReport.IsRed`, any FAIL or STALE) is absolute:
one FAIL reds the run. That is correct for the fixture corpus and for fresh
mission-produced saves, but it is wrong for two real, permanent situations.

1. **Baked-in true positives on immutable historical saves.** Five real
   historical saves are permanently RED on `INV2-NO-DOUBLE-COVER`. These are TRUE
   positives: overlapping `TrackSection` spans (a producer bug,
   `OrbitSegmentCheckpointBridge` checkpoint-vs-checkpoint clipping) baked into
   trajectory sidecars. Recorded files are IMMUTABLE (hard rule, no migration);
   even the in-flight producer fix will never green these saves, because the fix
   only changes what NEW recordings write, never what old `.prec` files contain.
   Running the analyzer over any of these five saves as a regression floor is
   useless: it is red every time, for a bug that is already known and already
   filed, drowning any genuinely new finding.

2. **Triage of user bug-report saves.** A user bug-report save arrives carrying
   historical damage (old overlaps, dangling rewind references, a stale
   annotation). The analyst does not want the full damage list on every run; the
   analyst wants "what is NEW here" after they make a change or after the user
   reproduces a bug. Without a baseline, the historical noise buries the one new
   finding that matters.

We need a PER-SAVE KNOWN-FINDINGS BASELINE: a small, human-editable file that
records the findings already known and accepted for one specific save, so a
red gate over that save fails only on findings NOT in the baseline. It must never
silently hide anything, must never apply to a fresh mission save, and must be
authored only by an explicit tool action.

## Terminology

- **Baseline**: a per-save file listing the findings already known and accepted
  for that one save. It changes only which findings count toward RED; it never
  removes a finding from the report.
- **Baseline entry**: one accepted finding, identified by a matching key plus
  provenance (the level it was captured at and a free-text reason).
- **Matching key**: the tuple that decides whether a live finding equals a
  baseline entry: `(RuleId, Target, SectionIndex, MessageDigest)`. See Data Model.
- **MessageDigest**: the finding message with numeric literals masked, so the key
  is stable against value churn but still distinguishes structurally different
  findings on the same target.
- **Baselined finding**: a live finding that matched a baseline entry. It stays in
  the report, carries a `Baselined = true` flag, and does not count toward RED.
- **Baseline mode**: how a run treats baselines. One of `Ignore` (default; never
  loaded), `Apply` (triage/historical; load and mark), `Forbid` (fresh-save guard;
  a baseline present beside the save is itself a FAIL).
- **Stale baseline entry**: a baseline entry that matched no live finding this
  run. Reported INFO so baselines shrink as bugs get fixed, EXCEPT entries over
  immutable-recording findings, which never go stale because the finding never
  disappears.
- **Fresh-save context**: a save produced by a scripted mission in the harness.
  Its gate is absolute and must never be softened by a baseline.

## Mental Model

The baseline is a reporting-layer filter that sits between the pure evaluator's
findings and the verdict/report. Rules, `AnalyzerModel`, and `InvariantEvaluator`
never know baselines exist (this is the load-bearing constraint: the natural seam
is exactly here).

```
   save dir --> SaveDirectoryLoader.Load --> AnalyzerModel (baseline-unaware)
                                                   |
                                                   v
                     InvariantEvaluator.Evaluate(model, rules)   <-- UNCHANGED
                                                   |
                                            List<Finding> + Counts + IsRed
                                                   |
                    +------------------------------+------------------------------+
                    |   BaselineFilter.Apply(report, baseline, mode,              |
                    |                         baselinePresent, loadFaults) <- NEW |
                    |   - Ignore: no-op (default; -WriteBaseline internal pass)   |
                    |   - Apply:  mark matching findings Baselined; add           |
                    |             stale/multi-match/escalation meta-findings      |
                    |   - Forbid: baseline present -> BASELINE-FORBIDDEN FAIL     |
                    |             (fresh mission saves + fixture floor run here)  |
                    +------------------------------+------------------------------+
                                                   |
                                            recompute Counts (+ Baselined tally)
                                            recompute IsRed (non-baselined FAIL/STALE)
                                                   |
                                                   v
                                         ReportWriter.Write (json + txt)
```

Two structural facts drive the whole design:

- **Recordings are immutable; trees mutate.** A `.prec` / `_vessel.craft` /
  `_ghost.craft` file is never rewritten. What CAN shift between runs is tree
  state (MergeState flips Immutable <-> CommittedProvisional on re-fly), the
  ledger (actions appended by rewind layering), supersede rows (added on re-fly),
  and on-disk rewind saves (deleted with a discarded sibling). Re-optimization
  does NOT rewrite a recording in place: `RecordingTreeSplitter` /
  `RecordingOptimizer.SplitAtUT` produce NEW recordings (HEAD/TIP) with NEW ids.
  So a finding's `(recordingId, sectionIndex)` is stable, and a re-optimized
  recording's findings correctly appear under a new Target (unbaselined, i.e.
  surfaced as new).
- **Baseline applies only to explicitly-baselined saves.** The fresh mission save
  gate is absolute. This is encoded structurally by the three-mode design, not by
  convention: the harness verifier runs in `Forbid`, where the mere PRESENCE of a
  baseline file is a FAIL.

## Data Model

### Runtime types

The pure filter and its value types live in `Parsek.Analyzer` (post-M-A3), so the
future in-game H5 category could reuse them. The file codec and the
`-WriteBaseline` builder live in the Tests project alongside `SaveDirectoryLoader`
(they touch the filesystem and `FileIOUtils`).

```
namespace Parsek.Analyzer   (pure, no file I/O)

enum BaselineMode { Ignore = 0, Apply = 1, Forbid = 2 }

struct BaselineKey
    string RuleId
    string Target          // recordingId / treeId / "<save>" / file token
    int    SectionIndex     // -1 when not section-scoped
    string MessageDigest    // NormalizeMessageDigest(finding.Message)
    // value equality (all four, ordinal); GetHashCode over all four

struct BaselineEntry
    string       RuleId
    string       Target
    int          SectionIndex
    string       MessageDigest
    VerdictLevel CapturedLevel   // the level the finding had when baselined
    string       Reason          // free text, per entry (why accepted)

class AnalysisBaseline
    int    BaselineFormatVersion       // schema of the baseline file itself
    string CreatedAtAnalyzerVersion    // AnalysisReport.CurrentAnalyzerVersion at write
    int    SubjectSchemaGeneration     // discovered gen at write (provenance only)
    string CreatedAtUtc                // ISO-8601, provenance only (not in reports)
    string Reason                      // free text, per file
    IReadOnlyList<BaselineEntry> Entries
    // built index: Dictionary<BaselineKey, List<BaselineEntry>> for O(1) match
```

`NormalizeMessageDigest(string)` is a pure static in `Parsek.Analyzer`: it
replaces every maximal run of characters forming a numeric literal
(digits, and an embedded `.`, `-`, `+`, `e`/`E` that binds a number) with a single
`#`. Everything else is copied verbatim. Example:
`"INV2 overlap recording=corpus0 a=[100,200] b=[150,250]"` becomes
`"INV# overlap recording=corpus# a=[#,#] b=[#,#]"`. Masking the id-embedded and
rule-id-embedded digits is harmless: the precise identity is carried by `RuleId`
and `Target` separately, so the digest only needs to preserve the message's
structural skeleton.

### Changes to existing analyzer types

Two additive changes to the report layer (these bump `AnalyzerVersion`, see
Backward Compatibility):

```
struct Finding
    ... existing fields ...
    bool Baselined            // NEW; default false. Set by BaselineFilter.Apply.

struct Counts
    int Fail; int Warn; int Info; int StaleFixture;
    int Baselined             // NEW; count of findings with Baselined == true
    int FailNonBaselined      // NEW; Fail findings with Baselined == false
    int StaleNonBaselined     // NEW; StaleFixture findings with Baselined == false

class AnalysisReport
    bool IsRed => Counts.FailNonBaselined + Counts.StaleNonBaselined > 0
```

The `Counts.From` aggregation gains a `Baselined++` branch and, in the same pass,
increments `FailNonBaselined` / `StaleNonBaselined` only for findings whose
`Baselined == false`. `IsRed` reads exactly those two splits (never the raw
FAIL=/STALE= header tokens, which still include baselined findings). Baselined
findings still increment their own level count (a baselined FAIL is still counted
in `Fail`) AND `Baselined`, so the report shows both "5 FAIL, of which 5 baselined"
rather than hiding them. The per-level non-baselined splits are the SINGLE source of
truth for the gate: they are computed once here, emitted in JSON, and reduced to the
terminal `RED=` token the `.txt` header carries (see Report format additions).

### Report format additions

- `.analysis.json`: each finding object gains a trailing `"baselined": true|false`
  field (fixed position, after `citedContract`); `counts` gains `"baselined"`,
  `"failNonBaselined"`, and `"staleNonBaselined"` fields (fixed position, after
  `"staleFixture"`). Determinism is preserved (booleans/ints, no new volatile
  input).
- `.analysis.txt`: the header line gains ` BASELINED=<n>` after `STALE=<d>`, and a
  terminal ` RED=<0|1>` token as the LAST token on the header line. `RED=` is the
  single source of truth for the gate (`RED=1` iff
  `failNonBaselined + staleNonBaselined > 0`); the earlier `FAIL=`/`STALE=` tokens
  remain raw totals that still include baselined findings, so gates must read `RED=`
  and never recompute red from `FAIL=`/`STALE=`. Each baselined finding line is
  suffixed ` [baselined]` so a human scanning the text sees at a glance which reds
  are accepted. Sort order is unchanged.

### Baseline file (frozen output contract)

`<save>/analysis/baseline.cfg`, a KSP ConfigNode. Format chosen and justified:

- **Location.** Beside the save, in the same `analysis/` folder the CLI writes
  reports into. The reports are named `<leaf>.analysis.json` / `.txt`; the
  baseline is `baseline.cfg`, so report regeneration never clobbers it and it is
  never confused with a report. It travels with the save when a triage copy is
  made (see Edge Cases).
- **Format: ConfigNode `.cfg`, not JSON.** The analyzer has a hand-rolled JSON
  WRITER (`ReportWriter.BuildJson`) but no JSON READER; a baseline must be read
  back, so JSON would force a new parser (new surface, new bug source).
  `ConfigNode.Load` is already a loader dependency, `FileIOUtils` already provides
  safe-write (.tmp + rename), and `.cfg` is the house serialization format:
  diffable, hand-editable, and native to every reader in this codebase. Repeated
  `ENTRY` nodes parse for free.

```
PARSEK_ANALYSIS_BASELINE
{
    baselineFormatVersion = 1
    createdAtAnalyzerVersion = 1
    subjectSchemaGeneration = 4
    createdAtUtc = 2026-07-11T12:00:00Z
    reason = Known historical INV2 overlaps on save 'c1'; producer bug filed in todo doc.
    ENTRY
    {
        ruleId = INV2-NO-DOUBLE-COVER
        target = c1r7
        sectionIndex = 3
        messageDigest = INV# overlap recording=# a=[#,#] b=[#,#]
        capturedLevel = FAIL
        reason = Overlapping checkpoint sections; immutable, will never green.
    }
}
```

Per the house ConfigNode I/O rule, `ConfigNode.Save` writes node CONTENTS only, so
the codec wraps/unwraps the `PARSEK_ANALYSIS_BASELINE` node name explicitly and
never calls `root.GetNode(name)` after `ConfigNode.Load`. All ints use
InvariantCulture.

## Behavior

### The matching key (the central decision)

A baseline entry matches a live finding when all four of
`(RuleId, Target, SectionIndex, MessageDigest)` are equal (ordinal). The tradeoff
the key navigates:

| Key choice | Failure mode |
|---|---|
| `(RuleId, Target)` only (too loose) | A genuinely NEW finding on the same recording (different section, different overlap) is masked. Unacceptable: it defeats "what is NEW". |
| `(RuleId, Target, SectionIndex, full verbatim Message)` (too tight) | Churns whenever a rule's message format changes OR a finding over MUTABLE data (INV8 ledger diff magnitudes, an appended-action count) carries a value that drifts run to run. Every drift un-baselines the entry, re-reddening the run. |
| `(RuleId, Target, SectionIndex, MessageDigest)` (chosen) | Stable across numeric-value drift and float-formatting changes (digits masked); still distinguishes structurally different messages on the same target+section (e.g. INV7 `field=ParentRecordingId` vs `field=SupersedeTargetId`). |

Why the chosen key is precise enough AND stable:

- **Precise.** For the primary case (INV2 over immutable recordings), the input
  bytes never change, so the message is byte-stable (the analyzer already
  guarantees byte-identical reports for identical input). `SectionIndex` plus the
  digested overlap skeleton pins the exact overlap; a new overlap at a different
  section produces a different `SectionIndex` and does not match.
- **Stable.** For the volatile case (INV8 part (b) career-diff WARNs, whose
  message can carry drifting magnitudes as the ledger grows), digit masking keeps
  the key fixed while the underlying value moves. UT-formatting or float-precision
  changes in a rule's message likewise do not churn the key. Note the deliberate
  tradeoff: because digit masking collapses the magnitude, baselining an INV8
  career-diff finding forfeits what-is-new granularity FOR THAT ONE RULE (a later
  run whose diff magnitude changed but whose key is unchanged stays baselined and
  does not resurface). This is accepted only for INV8 (its magnitudes are inherently
  drifting and a re-red on every ledger append would be noise); no other rule trades
  away new-finding detection this way.
- **SectionIndex is safe to include** precisely because recordings are immutable:
  a recordingId's sections never renumber. Re-optimization creates a new
  recordingId, whose findings are a new Target and correctly surface as new.

Two load-bearing facts underpin the key's stability:

- **Findings over immutable data are a frozen set.** Every finding whose Target is
  a `.prec` / snapshot recordingId is emitted from bytes that never change, so both
  its presence and its digest are frozen run to run. This is what makes the primary
  case (the immutable INV2 five) permanently and losslessly baselinable: the key is
  computed over data that is definitionally stable.
- **GUID-hex ids survive digit masking.** `NormalizeMessageDigest` masks numeric
  runs, but a recordingId / treeId is a GUID hex string whose letters (a-f) are
  preserved, so the id never collapses to `#` inside a message; and the id is
  carried verbatim in `Target` regardless. A message that embeds two different GUID
  ids therefore still produces two different digests, so the key never conflates
  findings that differ only by id.

**Placeholder-Target findings are never baselined.** Some findings carry a
placeholder token as their Target instead of a concrete recordingId:
`<tombstone>` and `<supersede>` name MUTABLE row populations (ledger tombstones,
supersede relations) whose membership shifts between runs, and the SAME placeholder
Target is shared across every finding in that population. Both properties defeat the
key: a placeholder Target is not stable and does not uniquely identify a finding.
`BaselineFilter.Apply` therefore refuses to set `Baselined` on any finding whose
Target is a placeholder token (they always gate), and `-WriteBaseline` skips
capturing them, emitting a `Analyzer: baseline write skip placeholder-target
rule=<id> target=<token>` Warn line per skipped finding so the omission is visible.

### Run modes and the fresh-save guard

`OfflineAnalyzer.Run(saveDir, resultsDir, bodyResolver, BaselineMode mode)` gains
the `mode` parameter (defaulting to `Ignore` for source compatibility with
existing callers). After `InvariantEvaluator.Evaluate` produces the report,
`BaselineFilter.Apply(report, baseline, mode, baselinePresent, loadFaults)` runs.
The two extra inputs let the pure filter stay filesystem-free while still reasoning
about the file: `baselinePresent` (a bool the caller sets from whether a
`baseline.cfg` existed beside the save) drives the `Forbid` FAIL and the
`Ignore` `BASELINE-PRESENT-NOT-APPLIED` / `Apply` `BASELINE-NOT-FOUND` INFOs without
the filter touching disk; `loadFaults` (the `faults` list `BaselineCodec.Load`
returned) is folded into `BASELINE-PARSE-FAULT` / `BASELINE-VERSION-FUTURE` /
`BASELINE-ENTRY-MALFORMED` meta-findings. The mode branches are:

- **`Ignore` (default).** The baseline is never loaded; `BaselineFilter.Apply` is a
  no-op except that, if a `baseline.cfg` exists beside the save, it emits one
  `BASELINE-PRESENT-NOT-APPLIED` INFO so the analyst is not surprised the reds
  came back. This is the mode for `-WriteBaseline`'s internal analyzer pass and for
  any legacy caller that does not opt in; routine gated runs use `Apply` or
  `Forbid`.
- **`Apply` (triage / the five historical saves).** Two hard refusals apply FIRST,
  before any matching: if the subject carries a `FixtureStamp != null` (a stamped
  fixture) `Apply` is refused wholesale with a `BASELINE-REFUSED-STAMPED` WARN and
  nothing is baselined; and the filter NEVER sets `Baselined` on a
  `StaleFixture`-level finding even on an unstamped subject (that scope is
  fresh-managed, not baselinable). Otherwise load `baseline.cfg`; for each live
  finding, if its key matches an entry AND the finding's current level does not
  EXCEED the entry's `CapturedLevel`, set `Baselined = true`. The level ordering is
  explicit and total: `Info < Warn < Fail`. `StaleFixture` is NOT part of this
  ordering and is excluded entirely (per S3), so it never participates in an
  escalation comparison. A finding whose level is greater than `CapturedLevel` under
  this ordering is an escalation (see `BASELINE-SEVERITY-ESCALATED`). Recompute
  `Counts` and `IsRed`, then emit the meta-findings below.
- **`Forbid` (harness fresh-save verifier).** If a `baseline.cfg` exists beside the
  save, emit a `BASELINE-FORBIDDEN` FAIL (red) and apply nothing. A fresh
  mission-produced save must not even coexist with a baseline; the guard is
  structural, not a naming convention. If no baseline file exists, `Forbid` is a
  clean no-op.

The harness post-run verifier (plan section 9 step 2) passes `Forbid`. The CI
fixture floor ALSO passes `Forbid`: a fixture corpus must never carry a baseline, so
pinning it to `Forbid` makes a stray `baseline.cfg` beside a fixture a hard FAIL
rather than a silently-ignored file (the fixture corpus stays separately guarded by
STALE-FIXTURE, which is never baselinable). Only ad hoc triage and the five known
historical saves pass `Apply`.

### Plumbing: env var, script flags, and the harness verifier

`BaselineMode` reaches `OfflineAnalyzer.Run` through three distinct callers, each
with its own entry point:

- **Env var (interactive / script-driven Manual runs).** The Manual xUnit entry
  point reads `PARSEK_ANALYZER_BASELINE_MODE`, whose values are `ignore` / `apply`
  / `forbid` (case-insensitive; default `ignore` when unset or unrecognized), and
  passes the parsed `BaselineMode` into `OfflineAnalyzer.Run`. This is the single
  variable the PowerShell wrapper sets.
- **`analyze-recordings.ps1` flags.** `-UseBaseline` sets
  `PARSEK_ANALYZER_BASELINE_MODE=apply` for the Manual analyzer run.
  `-WriteBaseline` first runs the OfflineAnalyzer pass in `ignore` (to see the TRUE,
  unfiltered findings), then invokes the `BaselineBuilder` through a SECOND Manual
  test method, `WriteBaselineForSave`, which reads the just-written report and
  writes `baseline.cfg` via `FileIOUtils`. Keeping the write in its own Manual
  method (not folded into the analyze method) keeps the read-only analyze path free
  of any filesystem-mutating side effect.
- **`Forbid` (programmatic, future harness).** No script sets `forbid`; it is passed
  programmatically by the mission harness verifier. Integration point: the M-A5
  harness verifier calls `OfflineAnalyzer.Run` with `BaselineMode.Forbid` on every
  fresh mission-produced save, so a smuggled baseline reds that run structurally.

The five known historical saves have a dedicated recurring runner: a Manual xUnit
theory (`[Theory]` over the historical save-list, one `[InlineData]` row per save)
runs each in `Apply` mode and asserts the run is green (all its baked-in reds are
baselined and no NEW finding surfaced). A new `scripts/analyze-historical-saves.ps1`
loops the five saves, invoking that theory with `PARSEK_ANALYZER_BASELINE_MODE=apply`
per save, so the historical corpus is a standing regression floor: green means "no
new damage on any of the five", and any red is a genuinely new finding on top of the
already-baselined INV2 overlaps. This is the concrete home for the two motivating
situations in Problem case 1.

### Meta-findings emitted by the filter (Apply mode)

These are analyzer-internal findings, cited to `BaselineFilter.Apply` /
`BaselineCodec.Load`. They are NOT themselves baseline-eligible (they are generated
by the filter, after matching), and they flow through the same deterministic sort.
Meta-finding Targets follow one rule: a per-entry meta (`BASELINE-STALE-ENTRY`,
`BASELINE-MULTI-MATCH`, `BASELINE-SEVERITY-ESCALATED`, `BASELINE-ENTRY-MALFORMED`,
`BASELINE-DUPLICATE-ENTRY`) carries the OFFENDING ENTRY's own `Target`; a file-level
meta (`BASELINE-PARSE-FAULT`, `BASELINE-VERSION-FUTURE`, `BASELINE-FORBIDDEN`,
`BASELINE-NOT-FOUND`, `BASELINE-PRESENT-NOT-APPLIED`, `BASELINE-REFUSED-STAMPED`)
carries the `<save>` token (it is about the file / subject as a whole, not one
entry).

- `BASELINE-STALE-ENTRY` (INFO): an entry matched no live finding this run.
  Baselines shrink as bugs are fixed; the analyst prunes stale entries with
  `-WriteBaseline`. Entries over immutable-recording findings never go stale.
- `BASELINE-MULTI-MATCH` (WARN): one entry matched more than one live finding
  (the digest collapsed two genuinely-distinct findings). One-match-per-entry-per-
  run: the FIRST match in deterministic finding order is marked baselined; every
  surplus match stays UNBASELINED and therefore counts toward the gate. Raised to
  WARN (not INFO) because a surplus non-baselined match can red the run, so the
  collapse is not merely informational; the analyst tightens the entry (usually by
  splitting it) to cover each finding explicitly.
- `BASELINE-SEVERITY-ESCALATED` (WARN): a live finding's key matches an entry but
  its current level EXCEEDS `CapturedLevel` (e.g. `INV3-ABSOLUTE-RANGE` promoted
  WARN -> FAIL after longitude normalization is cited). The finding is NOT
  baselined (it surfaces as new/red); the WARN explains why the stale entry did
  not silence it. This is the guard against a stale low-severity baseline masking
  a newly-promoted FAIL.
- `BASELINE-ENTRY-MALFORMED` (WARN): a single `ENTRY` node missing a required key,
  or carrying an unparsable `capturedLevel`, is dropped; the rest of the baseline
  still applies.
- `BASELINE-DUPLICATE-ENTRY` (WARN): two or more `ENTRY` nodes share an identical
  `BaselineKey`. They are deduped on load (the first is kept, the rest dropped) so
  the built index carries one entry per key; the WARN surfaces the redundant rows so
  the analyst prunes them with `-WriteBaseline`.
- `BASELINE-NOT-FOUND` (INFO): `Apply` requested but no baseline file present;
  every finding surfaces.
- `BASELINE-REFUSED-STAMPED` (WARN): `Apply` requested over a subject carrying a
  `FixtureStamp != null` (a stamped fixture). The whole apply is refused and nothing
  is baselined; the stamped-subject scope is fresh-managed and its STALE-FIXTURE
  gate must never be softened by a baseline. (Independently, the filter never sets
  `Baselined` on a `StaleFixture`-level finding even on an unstamped subject.)

### Loader faults on the baseline file

Baseline loading is `BaselineCodec.Load(path) -> (AnalysisBaseline, faults)`:

- Whole-file unparseable ConfigNode in `Apply` -> `BASELINE-PARSE-FAULT` FAIL
  (red). A broken baseline must never silently un-suppress real reds NOR silently
  suppress; fail loud. (Under `Forbid` the file is never parsed: its mere PRESENCE
  is already a `BASELINE-FORBIDDEN` FAIL, so a parse fault cannot arise there.)
- `baselineFormatVersion` greater than the analyzer understands ->
  `BASELINE-VERSION-FUTURE` FAIL. A future format cannot be safely applied.
- `createdAtAnalyzerVersion` differing from the current report version -> still
  applied (the KEY schema is independent of the REPORT schema) with a note folded
  into a single INFO; only the baseline's own `baselineFormatVersion` gates hard.

### Authoring: `-WriteBaseline` (explicit, never automatic)

`analyze-recordings.ps1 -WriteBaseline` is the only path that creates or updates a
baseline. It runs the analyzer in `Ignore` (so it sees the TRUE, unfiltered
findings), then `BaselineBuilder.FromReport(report, existingBaseline)` writes
`baseline.cfg` via `FileIOUtils` safe-write. Update semantics on an already-
baselined save:

- `-WriteBaseline` captures FAIL and WARN findings ONLY. INFO findings and the
  analyzer's own meta-findings are never written as entries (INFO never gates and is
  low-value to accept; meta-findings are generated by the filter, not baseline-
  eligible). Every captured FAIL/WARN finding becomes an entry (key + current level
  + an auto reason `captured by -WriteBaseline <version>`), except placeholder-
  Target findings, which are skipped per S2.
- Existing entries that STILL match a current finding are preserved, keeping their
  human-authored `reason` text (matched by key).
- Existing entries that match nothing (stale) are PRUNED by default (they cannot be
  pruned if they still match, e.g. the immutable INV2 five, which correctly
  survive). A `-KeepStaleBaselineEntries` switch retains them; this switch is kept as
  optional-v1 / YAGNI-acknowledged (no current workflow needs it, but it is a
  one-line guard against losing a still-relevant-but-momentarily-unmatched entry, so
  it is documented rather than cut).
- The file `reason` and `createdAtUtc` are refreshed; `subjectSchemaGeneration` is
  re-discovered.

`-WriteBaseline` is a triage-only action, but the protection against writing a
baseline into a fresh mission context is STRUCTURAL, not a script pre-check: the
mission harness runs every fresh save through `Forbid`, so any smuggled
`baseline.cfg` reds that run regardless of how it got there. On top of that
structural guard, `-WriteBaseline` itself refuses to run only when
`PARSEK_ANALYZER_BASELINE_MODE=forbid` is set in the invoking environment (a caller
explicitly declaring a forbid context); it does not otherwise try to guess whether a
save is "fresh".

### Gate semantics (summary)

Red iff a NON-baselined FAIL or a NON-baselined STALE-FIXTURE exists
(`Counts.FailNonBaselined + Counts.StaleNonBaselined > 0`). Because the baseline
scope (non-fixture saves) and the STALE-FIXTURE scope (stamped fixture corpus) are
disjoint, a "baselined STALE" never arises; IsRed keeps STALE strict.
`analyze-recordings.ps1 -FailOnRed` reads the terminal `RED=<0|1>` header token and
exits non-zero on `RED=1`. It MUST NOT recompute red from the raw `FAIL=`/`STALE=`
tokens (those still count baselined findings and would spuriously red an
all-baselined save); the `RED=` token is the emitter's single reduction of the
non-baselined splits, so the script and the report can never disagree.

## Edge Cases

Each: scenario -> expected behavior -> v1 or deferred.

1. **Baseline entry for a finding that got FIXED.** A WARN or tree-scoped finding
   the entry covered no longer fires (bug fixed, file cleaned). -> The entry
   matches nothing -> `BASELINE-STALE-ENTRY` INFO; run stays green; `-WriteBaseline`
   prunes it. v1.
2. **Immutable-recording entry that never disappears.** The five INV2 saves. ->
   The finding fires every run, the entry always matches, it never goes stale.
   Distinguished from case 1 by simply matching; no special handling needed, and
   `-WriteBaseline` cannot prune it because it still matches. v1.
3. **One entry matches multiple findings.** The digest collapsed two distinct
   findings on the same target+section. -> One-match-per-entry-per-run: the FIRST
   match in deterministic finding order is marked baselined, the surplus matches
   stay UNBASELINED and gate; `BASELINE-MULTI-MATCH` WARN surfaces the collapse so
   the analyst splits the entry. v1.
4. **Finding "moves section index" after re-optimization.** -> Cannot happen within
   one recordingId (immutable, sections never renumber). Re-optimization emits a
   NEW recordingId (HEAD/TIP); its findings carry a new Target and are unbaselined,
   surfacing as new. This is the correct behavior (a re-fly changed the tree; its
   new findings deserve review). v1.
5. **Tree mutates MergeState (Immutable <-> CommittedProvisional).** -> No baselined
   finding's key depends on MergeState (rule messages do not carry it), so a flip
   does not churn baselines. A flip that ADDS a supersede row yields a NEW INV7
   finding under a new key -> unbaselined -> surfaced. v1.
6. **Baseline file corrupt / hand-edited into invalid ConfigNode.** -> Whole-file
   parse failure in Apply -> `BASELINE-PARSE-FAULT` FAIL (red). Never silently
   proceed as un-baselined (would re-red the known five as if new) or as
   fully-baselined (would hide real reds). Under Forbid the file is never parsed
   (its presence alone is a `BASELINE-FORBIDDEN` FAIL). v1.
7. **Single entry hand-edited to drop a required key.** -> That entry is dropped
   with a `BASELINE-ENTRY-MALFORMED` WARN; the remaining entries apply. Partial
   damage does not void the whole baseline. v1.
8. **Baseline from an older analyzer version.** -> `createdAtAnalyzerVersion`
   differs from current; still applied (key schema is version-independent), folded
   into an INFO. Only a newer `baselineFormatVersion` gates hard
   (`BASELINE-VERSION-FUTURE` FAIL). v1.
9. **Save copied / renamed WITH its baseline.** -> Keys use recordingId + ruleId +
   sectionIndex + digest, never the save NAME, and the baseline is loaded from the
   copy's own `analysis/baseline.cfg`. It matches unchanged; only the report's
   `SaveName` (dir leaf) differs. Save-name-agnostic by design so triage copies
   work. v1.
10. **Baseline present in the fixture-corpus context.** -> The corpus is run in
    `Forbid` by CI; a baseline beside a fixture corpus is a `BASELINE-FORBIDDEN`
    FAIL (fixtures are regenerated, never baselined). STALE-FIXTURE stays strict and
    un-baselinable, so the two never interact. v1.
11. **STALE-FIXTURE interplay.** -> A stamped fixture at the wrong generation
    reports STALE-FIXTURE; `Apply` over a `FixtureStamp != null` subject is refused
    wholesale with a `BASELINE-REFUSED-STAMPED` WARN, and independently the filter
    never sets `Baselined` on a `StaleFixture`-level finding, so a STALE finding is
    never baselined and IsRed keeps STALE strict. v1.
12. **`-WriteBaseline` on an already-baselined save (update).** -> Union: new
    findings added as entries, surviving entries keep their human reason (matched
    by key), stale entries pruned (unless still-matching, e.g. the immutable five).
    Reasons are never lost for a still-firing finding. v1.
13. **Empty baseline file (present, zero ENTRY nodes).** -> Valid; applies nothing
    (equivalent to Ignore but records that a baseline exists). In `Forbid` its mere
    presence is still a FAIL (a fresh save must carry no baseline, empty or not).
    v1.
14. **Baseline entry for a WARN, not a FAIL.** -> Allowed. A baselined WARN is
    marked baselined and dropped from the "new WARN" triage view; IsRed is
    unaffected either way (WARN never reds). Useful for silencing a known-benign
    INV9 missing-rewind WARN during triage. v1.
15. **Baselined-as-WARN entry now matches a FAIL (severity escalation).** -> Not
    baselined; `BASELINE-SEVERITY-ESCALATED` WARN emitted; the FAIL surfaces and
    reds the run. A stale low-severity baseline cannot mask a promoted FAIL. v1.
16. **New finding on an already-baselined target.** -> Same recording, different
    section or different digested message -> new key -> no match -> surfaces
    non-baselined and reds the run if FAIL. This is the entire point (triage:
    "what is NEW"). v1.
17. **`Apply` requested but no baseline file present.** -> `BASELINE-NOT-FOUND`
    INFO; every finding surfaces (run behaves as Ignore). Not a fault. v1.
18. **Baseline present but run in default `Ignore`.** -> Baseline not consulted;
    `BASELINE-PRESENT-NOT-APPLIED` INFO so the analyst knows a baseline exists and
    was deliberately skipped. v1.

## What Doesn't Change

- **Rules, `AnalyzerModel`, and `InvariantEvaluator` are untouched.** No rule reads
  a baseline; the model carries no baseline field; the evaluator's signature and
  output are unchanged. The seam is strictly between the evaluator's findings and
  the report/verdict, so the in-game H5 category reusing `InvariantEvaluator`
  remains baseline-free (in-game there is no per-save baseline concept).
- **Invariant contracts, the RELATIVE / schema-generation / immutable-recording
  contracts, and the report determinism guarantee are all preserved.** The new
  report fields (`Finding.Baselined`, `Counts.Baselined`,
  `Counts.FailNonBaselined`, `Counts.StaleNonBaselined`) are booleans/ints with no
  volatile input, so a given input always yields a byte-identical report
  (per-run determinism holds in every mode). The stronger property is scoped to
  baseline-free saves run in `Ignore` with no `baseline.cfg` present: their
  findings, counts, and gate verdict are UNCHANGED from the pre-feature analyzer
  (modulo the additive schema, where the `RED=` token reduces to the old FAIL/STALE
  gate and the new count fields are zero for baselined tallies). A
  save with a baseline applied deliberately differs (baselined flags, meta-findings,
  the split counts).
- **No production Parsek source changes.** The baseline is a read-side analyzer
  feature. The pure filter types land in `Parsek.Analyzer` alongside the M-A3 move;
  the codec/builder land in the Tests project. No new serialized field on any save
  file, no new public API.
- **The absolute gate for fresh mission saves is preserved and hardened.** `Ignore`
  never softens it; `Forbid` makes a stray baseline a FAIL rather than a silent
  suppression.
- **`.prec` / snapshot / tree / ledger formats are untouched.** The baseline is a
  new sidecar under `analysis/`, never written into the save's Parsek data.

## Backward Compatibility

- **Report schema bump.** Adding `Finding.Baselined`, `Counts.Baselined`,
  `Counts.FailNonBaselined`, `Counts.StaleNonBaselined` (and the `.txt`
  `BASELINED=` header token, the terminal `RED=<0|1>` header token, and the
  ` [baselined]` suffix) changes the `.analysis.json` / `.analysis.txt` contract, so
  `AnalysisReport.CurrentAnalyzerVersion` (a string, currently `"1"`) is bumped to
  `"2"` and the golden-report stability test is updated. The harness parser reading
  `counts.fail` is unaffected (that field is unchanged); a parser wanting the
  accepted-vs-new split reads `counts.failNonBaselined` / `counts.staleNonBaselined`
  (or, for a whole-run gate, the terminal `RED=` token).
- **Baseline file format versioning.** `baselineFormatVersion = 1` is the initial
  contract. A future change bumps it; a baseline whose version exceeds the analyzer
  is a hard `BASELINE-VERSION-FUTURE` FAIL rather than a mis-applied file.
- **Default is Ignore.** The new `mode` parameter on `OfflineAnalyzer.Run` defaults
  to `Ignore`, so any existing caller that does not pass it is byte-identical to
  today. The updated callers opt in explicitly: the Manual env-var test passes
  whatever `PARSEK_ANALYZER_BASELINE_MODE` resolves to (default `ignore`), and the
  CI fixture floor + the M-A5 harness verifier pass `Forbid`.
- **No migration of existing saves.** There is nothing to migrate; a save simply
  has no `baseline.cfg` until `-WriteBaseline` creates one. Consistent with the
  pre-1.0 no-migration policy.

## Diagnostic Logging

The filter and codec log under the existing `Analyzer` subsystem tag, per the house
logging rule (every decision point logs). Batch-counting convention: one summary
line for the per-entry match sweep, plus bounded per-anomaly lines.

- Baseline load, one-shot: `Analyzer: baseline mode=<Ignore|Apply|Forbid>
  present=<bool> entries=<n> formatVersion=<v> createdAtVersion=<s>` (Info).
- Match sweep summary: `Analyzer: baseline applied matched=<n> baselined=<n>
  stale=<n> multiMatch=<n> escalated=<n>` (Info, one line after the sweep).
- Per stale entry: `Analyzer: baseline stale-entry rule=<id> target=<t>#<sec>`
  (Verbose; stale entries are common as bugs get fixed).
- Per multi-match: `Analyzer: baseline multi-match rule=<id> target=<t>#<sec>
  count=<n> baselinedFirst surplusUnbaselined=<n-1>` (Warn; the WARN finding is
  BASELINE-MULTI-MATCH and the surplus reds the run).
- Per severity escalation: `Analyzer: baseline escalated rule=<id> target=<t>#<sec>
  captured=<level> now=<level> notBaselined` (Warn) so a masked-then-promoted FAIL
  is visible in the log even before the report is read.
- Forbid trip: `Analyzer: baseline FORBIDDEN save='<name>' - fresh-save context
  carries a baseline` (Warn), the fresh-save guard made observable.
- Parse fault: `Analyzer: baseline parse-fault path='<file>' reason=<r>` (Warn),
  mirroring the loader's loadFault line.
- Write (`-WriteBaseline`): `Analyzer: baseline write save='<name>' entries=<n>
  new=<a> preserved=<b> pruned=<c> skippedPlaceholder=<d> path='<file>'` (Info,
  one-shot; `skippedPlaceholder` counts the S2 placeholder-Target findings not
  captured, each also logged on its own Warn line).
- IsRed recompute: `Analyzer: verdict red=<0|1> failNonBaselined=<n>
  staleNonBaselined=<s> failBaselined=<m>` (Info) so the baseline's effect on the
  gate is explicit in the log; `red` here is the same reduction the `RED=` header
  token carries (`failNonBaselined + staleNonBaselined > 0`).

## Test Plan

Every test states the bug it catches. Tests live in
`Source/Parsek.Tests/Analyzer/` (pure-filter tests can move to a
`Parsek.Analyzer`-facing test file post-M-A3). Fixtures are hand-built `Finding`
lists and `AnalysisReport`s plus `RecordingBuilder` / `ScenarioWriter` corpora for
the end-to-end and script tests. Classes touching `RecordingStore` / `ParsekLog`
statics use `[Collection("Sequential")]` + `ResetForTesting`.

### Pure matching logic

- **Digest masks numerics, keeps skeleton.** `NormalizeMessageDigest` on two INV2
  messages differing only in UT span values yields the same digest; on messages
  differing in a field name (`field=ParentRecordingId` vs `field=SupersedeTargetId`)
  yields different digests. Fails if the mask is too aggressive (collapses distinct
  findings, masking new ones) or too weak (churns on value drift).
- **Key equality is all-four ordinal.** Two findings equal in
  (RuleId, Target, SectionIndex, digest) match; a difference in any one does not.
  Fails if the key drops a component and either over- or under-matches.
- **Immutable-recording stability.** The same recording analyzed twice produces
  identical keys for its INV2 findings (leans on report determinism). Fails if any
  key component picks up run-to-run state, which would un-baseline the known five
  every run.
- **SectionIndex -1 findings match.** A recording-scoped finding (section -1)
  baselines correctly. Fails if -1 is mishandled as "no section" and mis-keyed.
- **Severity escalation is not masked.** An entry captured at WARN does not
  baseline a now-FAIL finding with the same key; a `BASELINE-SEVERITY-ESCALATED`
  WARN is emitted and the FAIL surfaces. Fails if a stale low-severity baseline
  silently masks a promoted FAIL (the dangerous regression).

### Report / summary rendering

- **Baselined flag + counts.** A report with 5 baselined FAILs shows
  `Counts.Fail == 5`, `Counts.Baselined == 5`, `Counts.FailNonBaselined == 0`,
  each finding `Baselined == true`, and the JSON/txt carry the flag,
  `baselined=5`, `failNonBaselined=0`, and the terminal `RED=0`. Fails if a
  baselined finding is dropped from the report (silent suppression, the forbidden
  behavior) or its flag is lost.
- **Determinism preserved.** Analyzing the same baselined model twice yields
  byte-identical `.analysis.json`. Fails if the baseline pass introduces
  nondeterministic ordering of the meta-findings.
- **AnalyzerVersion frozen at "2".** A golden `.analysis.json` with the `baselined`,
  `failNonBaselined`, and `staleNonBaselined` count fields (and
  `analyzerVersion == "2"`) is asserted field-for-field. Fails if the schema drifts
  without a bump.
- **Human summary contract.** The header matches
  `[Analyzer] save=... FAIL=.. WARN=.. INFO=.. STALE=.. BASELINED=.. RED=<0|1>`
  with `RED=` as the terminal token, and baselined lines carry the ` [baselined]`
  suffix. A five-baselined-FAIL report asserts `RED=0`; a report with one extra
  non-baselined FAIL asserts `RED=1`. Fails if a grep-based triage script breaks on
  format drift or if `RED=` disagrees with the non-baselined splits.

### Gate semantics

- **Non-baselined FAIL reds.** A report with one baselined FAIL and one
  non-baselined FAIL -> `IsRed == true`. Fails if any baselined finding wrongly
  suppresses the gate for unrelated reds.
- **All-baselined FAILs go green.** The five-INV2 historical save with all five
  baselined -> `IsRed == false`, findings still present. Fails if a fully-baselined
  save reds (defeating the feature) or if the findings vanish (silent suppression).
- **Baselined WARN, gate unaffected.** A baselined WARN -> green either way, marked
  baselined. Fails if baselining a WARN is rejected or changes IsRed.
- **STALE stays strict.** A stamped-fixture STALE run cannot be baselined (Apply is
  refused on a stamped subject with a `BASELINE-REFUSED-STAMPED` WARN) and IsRed
  stays true. Fails if a STALE is baselinable.

### Fresh-save refusal (structural guard)

- **Forbid + baseline present -> FAIL.** Running `Forbid` over a save that carries a
  `baseline.cfg` yields a `BASELINE-FORBIDDEN` FAIL and applies nothing.
  Fails if a fresh mission save could ever be softened by a stray baseline (the
  binding constraint).
- **Forbid + no baseline -> clean.** `Forbid` over a baseline-free save is a no-op,
  green. Fails if Forbid spuriously reds a normal fresh save.
- **Ignore never applies.** `Ignore` over a save WITH a baseline leaves every red
  red, emits `BASELINE-PRESENT-NOT-APPLIED` INFO. Fails if the default mode ever
  consults a baseline (`-WriteBaseline`'s internal Ignore pass must see the TRUE,
  unfiltered findings).

### Codec / fault robustness

- **Corrupt whole file -> FAIL, no crash.** A malformed `baseline.cfg` in Apply ->
  `BASELINE-PARSE-FAULT` FAIL, no exception escapes `OfflineAnalyzer.Run`. Fails if
  a broken baseline crashes triage or silently un-/over-suppresses.
- **Malformed single entry -> WARN, rest apply.** An `ENTRY` missing `ruleId` (or
  with an unparsable `capturedLevel`) is dropped with a `BASELINE-ENTRY-MALFORMED`
  WARN; sibling entries still baseline. Fails if one bad entry voids the whole
  baseline.
- **Duplicate entries dedupe with WARN.** Two `ENTRY` nodes with an identical
  `BaselineKey` collapse to one in the built index and emit a
  `BASELINE-DUPLICATE-ENTRY` WARN. Fails if a duplicate key throws on index build or
  is silently double-counted.
- **Future format -> FAIL.** `baselineFormatVersion = 99` -> `BASELINE-VERSION-FUTURE`
  FAIL. Fails if a future baseline is mis-applied.
- **Round-trip.** `BaselineBuilder.FromReport` then `BaselineCodec.Load` yields
  entries whose keys match the original findings. Fails if write/read drops or
  reshapes a key component.

### Log-assertion tests (RewindLoggingTests pattern)

These capture `ParsekLog` output via `TestSinkForTesting` and assert the decision
line was emitted, per the canonical `RewindLoggingTests` pattern (sink in the
constructor, `ResetTestOverrides` in Dispose, `[Collection("Sequential")]`).

- **Escalated-not-baselined logs a Warn line.** An entry captured at WARN against a
  now-FAIL finding emits `Analyzer: baseline escalated rule=... captured=WARN
  now=FAIL notBaselined` at Warn. Fails if a masked-then-promoted FAIL is not
  observable in the log (the escalation would only be visible by reading the
  report, defeating the log-first debugging contract).
- **Forbid trip logs its line.** `Forbid` over a save carrying a `baseline.cfg`
  emits `Analyzer: baseline FORBIDDEN save='...'` at Warn. Fails if the fresh-save
  guard fires without leaving a log trace (a silent structural refusal is
  undebuggable).
- **Match-sweep summary logs once.** An `Apply` run emits exactly one
  `Analyzer: baseline applied matched=<n> baselined=<n> stale=<n> multiMatch=<n>
  escalated=<n>` summary line after the sweep. Fails if the summary is missing
  (no per-run accounting of what the baseline did) or emitted per-entry (log spam
  violating the batch-counting convention).

### Script flag round-trip (end to end)

- **Write then use is green.** `analyze-recordings.ps1 -WriteBaseline` over a
  synthetic corpus that reds on a seeded INV2 overlap, then
  `analyze-recordings.ps1 -UseBaseline -FailOnRed` over the same save exits 0.
  Fails if authored baselines do not actually green a subsequent gated run (the
  core user story).
- **Update preserves reasons, prunes stale, keeps immutable.** A second
  `-WriteBaseline` after one finding is removed and one added: the surviving
  entry's hand-authored `reason` is retained, the removed one is pruned, the new
  one is added, an immutable still-firing entry is kept. Fails if update loses a
  human reason or prunes a still-matching entry.
- **WriteBaseline refuses fresh context.** `-WriteBaseline` with
  `PARSEK_ANALYZER_BASELINE_MODE=forbid` set in the environment refuses to write.
  Fails if the env-var forbid declaration does not block the write. (The primary
  protection is structural: the harness runs fresh saves through `Forbid`, which the
  Forbid tests below cover.)
