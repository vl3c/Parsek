# Plan: Persistence-based discriminator for `RecordingOptimizer` env-class splits

Branch for the implementation: `feat/optimizer-persistence-split`.

This plan replaces the reverted PR #625 PartEvent-window gate with a
persistence-based discriminator that suppresses graze-pattern boundaries
without sacrificing the per-phase loop split that
[`parsek-flight-recorder-design.md` §9A.5](../../parsek-flight-recorder-design.md)
codifies as the whole point of `SplitEnvironmentClass`.

Companion artifacts:

- [`docs/dev/research/optimizer-meaningful-split-rule.md`](../research/optimizer-meaningful-split-rule.md) — historical research note for the reverted PartEvent gate (PR #625). The "Conclusion reverted" callout at the top of that note points to this plan and explains why the §5 S5 "one-way vs oscillating" signal that the note dropped is the right idea after all.
- [`docs/dev/todo-and-known-bugs.md`](../todo-and-known-bugs.md) #632 — the post-revert acceptance entry. Closes when this plan ships.
- [`extending-rewind-to-stable-leaves.md`](../research/extending-rewind-to-stable-leaves.md) §S16 — original symptom report (eccentric grazing chain bloat).

---

## 1. Problem

`RecordingOptimizer.FindSplitCandidatesForOptimizer`
([Source/Parsek/RecordingOptimizer.cs:210](../../../Source/Parsek/RecordingOptimizer.cs:210))
splits a recording at every `TrackSection` boundary where
`SplitEnvironmentClass(env)` differs (or where the body changes — #251). Today
this is a pure geometric check: the boundary fires whenever a vessel crosses
the 70 km atmosphere line or the airless-body approach line, regardless of
what the trajectory does on either side.

The geometric check produces three classes of outcome:

1. **Real phase change.** Ascent through 70 km, deorbit through 70 km, vacuum
   landing through approach altitude. The vessel persists in the new env class
   (until end of recording, surface contact, or another real phase change).
   The player wants this to split into per-phase chain segments so each phase
   has its own loop toggle.
2. **Graze pair.** Eccentric atmo-grazing periapsis pass; aerobrake pass with
   atmo entry/exit close to 70 km; airless-body grazing flyby below approach
   altitude. The vessel re-emerges within seconds. Two adjacent boundaries
   (entry + exit) are both bookkeeping artifacts — the player thinks of the
   whole pass as one segment.
3. **Producer-C boundary seam.** A focused background vessel transitions to
   on-rails AND the env class differs across the boundary AND the on-rails
   state will not produce a playable payload. `BackgroundRecorder.FlushLoadedStateForOnRailsTransition`
   ([BackgroundRecorder.cs:3001](../../../Source/Parsek/BackgroundRecorder.cs:3001))
   emits a single-frame `Background`/`Absolute` `TrackSection` at the boundary
   to record the env state. Recording typically ends immediately after. This
   is bookkeeping, not a phase change.

Today's geometric check splits cases 2 and 3 the same way as case 1, which is
how S16 (the eccentric grazing chain explosion) was originally reported.

PR #625 attempted to fix this with a PartEvent ±5 s window check. That gate
also suppressed case 1 for two extremely common gameplay phases — passive
deorbit reentries (engines off well before 70 km, parachutes deploy at ~5 km,
thermal events fire deep in atmo, all outside the ±5 s window) and staged
ascents with a coast through 70 km (engine cutoff at ~50 km, circularization
at ~80 km apoapsis, both engine events outside the window). #625 was reverted
in #628.

## 2. Mental model

A boundary is a real phase change iff the vessel **persists in the new env
class** for a sustained time, OR transitions to surface, OR end-of-recording
arrives without a return to the previous env class. A boundary is a graze iff
the vessel **returns to the previous env class within seconds** — i.e. one
side of the boundary is bracketed by the same env class on both sides
(`A → B → A` shape).

Visual intuition for an eccentric grazing recording (atmo periapsis):

```
 sections:   ...  Exo[long]   Atmo[≤K]   Exo[long]   Atmo[≤K]   Exo[long] ...
 boundaries:           ↑          ↑          ↑          ↑          ↑
                       graze      graze      graze      graze      graze
```

Every boundary has at least one side that is a "brief" (< K seconds) section
bracketed by the same class on the other side. All of them suppress.

Visual intuition for a real ascent:

```
 sections:   Surface[short]   Atmo[long]   Exo[long]   ...
 boundaries:        ↑              ↑
                   split         split
                  (Surface       (Atmo→Exo,
                   involved)     both sides long)
```

Neither side of the Atmo→Exo boundary is brief, so the predicate splits.

Visual intuition for a passive deorbit reentry:

```
 sections:   Exo[long]   Atmo[medium]   Surface[long]
 boundaries:      ↑              ↑
                split          split
               (Exo→Atmo,      (Surface
                Atmo not       involved)
                bracketed
                by Exo)
```

The Atmo section is not bracketed by Exo — it ends in Surface (or end of
recording while still in Atmo). The vessel does not return to Exo. The
predicate splits.

Visual intuition for an aerobrake pass:

```
 sections:   Exo[long]   Atmo[≤K]   Exo[long]
 boundaries:      ↑          ↑
               graze       graze
              (Atmo brief, bracketed by Exo)
```

Both boundaries suppress — the whole aerobrake stays as one segment, which is
the player intent (single aerobrake pass = single recording phase).

## 3. Discriminator specification

For a candidate boundary at section index `s` in `rec.TrackSections` (the
transition from `prev = sections[s-1]` to `next = sections[s]`):

```
1. If prev.IsBoundarySeam OR next.IsBoundarySeam:
                                             → not a split candidate  (Producer-C
                                                                       seam,
                                                                       see §5).
                                                                       Hard
                                                                       override
                                                                       — wins
                                                                       even on
                                                                       body /
                                                                       Surface /
                                                                       ExoPropulsive.
2. If env class unchanged AND body unchanged:→ not a boundary, skip
3. If body changed:                          → split  (#251 unchanged)
4. If prev or next is Surface (class 2):     → split  (already gated upstream
                                                       by Vessel.Situations +
                                                       debounce — keep
                                                       always-meaningful)
5. If prev or next is ExoPropulsive:         → split  (engine firing at the
                                                       crossing — direct
                                                       gameplay event,
                                                       keep S3 short-circuit)
6. Apply the persistence predicate (§3.1).   → split or suppress
```

The seam check is step 1 — a hard "always wins" override regardless of
env class on either side. The contract for `IsBoundarySeam` is "this section
is a recorder bookkeeping artifact, never a split candidate, full stop." The
later sections (§5, §13, todo #632) are written to that contract; placing
the seam check below body-change or Surface short-circuits would silently
narrow the contract for any future producer that sets the flag across one of
those transitions. Producer C today only emits seams on a loaded→on-rails
transition where the body and Surface state cannot change at the seam, but
the ordering must stay correct for future producers that might.

Body change, Surface, and ExoPropulsive remain hard short-circuits for the
non-seam path. Only the otherwise-noisy `Atmospheric ↔ ExoBallistic` and
`Approach ↔ ExoBallistic` pairs reach the persistence predicate.

### 3.1 Persistence predicate

A boundary at index `s` is a **graze pattern** iff EITHER of the following
holds:

Both clauses operate on `SplitEnvironmentClass`-collapsed *runs* of
adjacent same-class sections, not on raw single sections — see §3.2 for
why the collapse-walk is required.

- **(A) Forward-bracket**:
  - Walk forward from `s` while the next section's split class equals
    `next`'s split class:

    ```
    int nextRunEndIdx = s;
    while (nextRunEndIdx + 1 < sections.Count
           && SplitEnvironmentClass(sections[nextRunEndIdx + 1].environment)
              == SplitEnvironmentClass(next.environment))
        nextRunEndIdx++;
    ```

  - Cumulative duration of the run:
    `nextRunCumDur = sections[nextRunEndIdx].endUT - next.startUT`.
  - Bracket index: `bracketIdx = nextRunEndIdx + 1`.
  - **Forward bracket fires** iff
    `nextRunCumDur < K` AND
    `bracketIdx < sections.Count` AND
    `SplitEnvironmentClass(sections[bracketIdx].environment) == SplitEnvironmentClass(prev.environment)`.

  Reads: "The new section, plus any same-split-class neighbours that
  follow it, is briefer than K and is followed by a section in the same
  split class as the previous section." Catches the
  `prev → [brief run of next-class] → prev'` shape — typically the
  boundary INTO a graze.

- **(B) Backward-bracket**:
  - Walk backward from `s-1` (which is `prev`) while the section before
    is in `prev`'s split class:

    ```
    int prevRunStartIdx = s - 1;
    while (prevRunStartIdx - 1 >= 0
           && SplitEnvironmentClass(sections[prevRunStartIdx - 1].environment)
              == SplitEnvironmentClass(prev.environment))
        prevRunStartIdx--;
    ```

  - Cumulative duration of the run:
    `prevRunCumDur = prev.endUT - sections[prevRunStartIdx].startUT`.
  - Bracket index: `bracketIdx = prevRunStartIdx - 1`.
  - **Backward bracket fires** iff
    `prevRunCumDur < K` AND
    `bracketIdx >= 0` AND
    `SplitEnvironmentClass(sections[bracketIdx].environment) == SplitEnvironmentClass(next.environment)`.

  Reads: "The previous section, plus any same-split-class neighbours
  that precede it, is briefer than K and is preceded by a section in
  the same split class as the next section." Catches the
  `next' → [brief run of prev-class] → next` shape — typically the
  boundary OUT of a graze.

If either bracket condition holds, the boundary is a graze pattern → suppress.
Otherwise → split.

**Bounded walk cost.** The walk is per-boundary O(M) where M is the
length of the same-split-class run. Cumulative cost across all
boundaries in a recording is O(N) (each section visited at most twice —
once forward from a left-side boundary, once backward from a right-side
boundary). No worse than the legacy `for s = 1..N` linear scan.

**`K = BriefSectionMaxSeconds = 120.0`**, exposed as `internal const double`
for tests. The name reads with the predicate ("the section is brief if its
duration < K"), not against it. Rationale for 120 s:

- Aerobraking passes typically last 30–90 s of atmo time → suppressed (graze).
- Eccentric Pe dips on Kerbin grazing orbits last 30–60 s → suppressed.
- LKO orbital periods are ~30 min, well above K → grazing exits never
  bracket-match across orbits.
- A real suborbital arc that holds Exo above 70 km for >120 s before reentry
  splits cleanly (the apogee Exo segment is not brief).
- **Tourist-hop boundary case (calibration check):** Karman-line tourist hops
  cross 70 km going up and again coming down. Exo dwell is governed by
  `t = 2 * v_y(70km) / g` where `v_y(70km) = sqrt(2 * g * (apogee - 70 km))`.
  On Kerbin (`g ≈ 9.81 m/s²`):

  | apogee | Exo dwell | Predicate result |
  |---|---|---|
  | 90 km  | ~56 s   | suppressed (one segment, intuitive) |
  | 120 km | ~88 s   | suppressed |
  | 150 km | ~113 s  | suppressed (just under K) |
  | 175 km | ~134 s  | **split** (Exo phase becomes its own segment) |
  | 200 km | ~152 s  | split |

  Stock contracts often request 100–200 km altitude, so a meaningful slice
  of "tourist hops" will fall on either side of K. This is acceptable — a
  hop with a sustained Exo phase reasonably IS a real flight phase from the
  player's perspective. **`K = 120 s` is a starting point. Revisit with
  playtest data per §12 if the split point feels wrong.**

The 120 s window is wider than the 5 s window in PR #625 by design — the
predicate asks "is this section brief enough to be a recorder bookkeeping
artifact rather than a phase?", not "is there a discrete event near the
crossing?".

### 3.2 Why the bracket walks `SplitEnvironmentClass` runs (not single-step)

A naive single-step bracket (`sections[s+1]` for forward, `sections[s-2]`
for backward) misclassifies graze patterns whenever the brief phase or
its bracket spans multiple same-`SplitEnvironmentClass` sections. Two
recorder mechanisms produce same-split-class adjacent sections that the
optimizer's `envChanged` check ignores but a single-step bracket lookup
would trip over:

1. **Raw `SegmentEnvironment` transitions within a split class.**
   `SplitEnvironmentClass` collapses `ExoBallistic` and `ExoPropulsive`
   into class 1 and `SurfaceMobile`/`SurfaceStationary` into class 2.
   But TrackSections are emitted on raw `SegmentEnvironment` transitions
   ([FlightRecorder.UpdateEnvironmentTracking](../../../Source/Parsek/FlightRecorder.cs:5640),
   [BackgroundRecorder.OnBackgroundPhysicsFrame](../../../Source/Parsek/BackgroundRecorder.cs:1450)),
   not on `SplitEnvironmentClass` transitions. A vessel that toggles
   thrust above 70 km produces `ExoBallistic → ExoPropulsive → ExoBallistic`
   — three sections, all split class 1. The `envChanged` check correctly
   treats those internal seams as non-boundaries, but a single-step bracket
   at a separate Atmo↔Exo boundary would land on whichever raw-env section
   happens to be at `s+1` / `s-2`, even though logically the whole class-1
   run is the bracket.

2. **Forced section breaks that restart the same `SegmentEnvironment`.**
   Vessel-switch seams and producer-attributed source changes can close
   the current TrackSection and open a new one with the same
   `SegmentEnvironment` value — producing two adjacent sections in the
   same raw env class. The `envChanged` check skips the seam (env didn't
   change), so the optimizer never considers the seam as a candidate. But
   a single-step bracket at a nearby Atmo↔Exo boundary would land on the
   seam-split half of the brief phase rather than on the bracket section,
   missing the enclosing class.

   Concrete misclassification under single-step lookup:
   `Exo[long], Atmo[20s], Atmo[20s_forced_break], Exo[long]` — both Atmo
   sections are class 0, the trailing Exo is class 1. Single-step at the
   first Exo→Atmo boundary looks at `sections[s+1]` = Atmo (class 0),
   which equals `next` class but NOT `prev` class — bracket fails, the
   boundary splits despite being a single 40 s graze pass.

The collapse-walk in §3.1 fixes both mechanisms uniformly: walk forward
through same-split-class neighbours of `next` (or backward through
same-split-class neighbours of `prev`), accumulate duration, then check
the FIRST DIFFERENT-CLASS section against the bracket's class. The walk
also has a built-in upper bound — it stops as soon as cumulative duration
crosses K, at which point the run is "long enough to be a phase" and no
bracket check is needed.

**Why not a fully unbounded forward scan ("does the vessel ever return
to `prev` env class within K seconds?")?** Two reasons:

- A multi-step scan that crosses different classes would fold distinct
  phases into a "graze" decision when the player almost certainly wants
  splits — e.g. `Atmo → Exo → Atmo → Surface` within K seconds (a rapid
  suborbital that ends in landing) shouldn't be collapsed to one segment.
- The collapse-walk on same-split-class runs is sufficient to capture
  every realistic graze pattern (the brief phase plus its bracket are both
  in their own split classes; only intra-class fragmentation needs
  collapsing). A more permissive scan would suppress real phase
  transitions just because they cluster in time.

The walk is local to the same-split-class run on each side of the
candidate boundary. It does not cross into a different class, and that
boundary is what every bracket check tests against.

### 3.3 Edge of recording

If the forward walk reaches `sections.Count` without finding a section in
a different split class (i.e., the new section's class run extends to
end-of-recording), clause (A) cannot fire — there is no bracket section
to check. Symmetrically, if the backward walk reaches index 0 without
finding a different-class section, clause (B) cannot fire.

In both cases the predicate falls through to "split" (no graze pattern
detected). This is the conservative default: end-of-recording typically
indicates the recording terminated in the new env class (the vessel reached
orbit / landed / scene-exited mid-flight), all of which are real phase
changes from the player's perspective.

The Producer-C seam is the deliberate exception: a brief section at the END
of a recording with no follow-up looks identical to a real terminal phase
change under the persistence predicate alone. §5 handles this with an
explicit `IsBoundarySeam` flag at step 1 of the §3 ordering, ahead of the
persistence predicate.

## 4. Worked examples

Predicate traces walk the §3 ordering top-down (seam → not-a-boundary →
body → Surface → ExoPropulsive → persistence) and use the collapse-walk
semantics of §3.1.

| Recording shape (sections) | Boundaries (s) | Predicate trace | Result |
|---|---|---|---|
| `Surface,Atmo[long],Exo[long]` | s=1 (Sur→Atm), s=2 (Atm→Exo) | s=1: Surface short-circuit. s=2: forward walk stops at s+1 (no more sections); next is long, no bracket. Backward walk stops at prev=Atmo (s-2=Surface, different class); prev is long, no bracket. | both split ✓ |
| `Surface,Atmo[long],Exo[long]` (LKO ascent, engines on at 70 km) | s=2 (Atm→ExoPropulsive) | ExoPropulsive short-circuit. | split ✓ |
| `Surface,Atmo[long],Exo[long]` (LKO ascent, coast through 70 km) | s=2 (Atm→ExoBallistic) | persistence predicate; both runs long. | split ✓ (the case PR #625 broke) |
| `Exo[long],Atmo[medium],Surface[long]` (passive deorbit reentry) | s=1 (Exo→Atm), s=2 (Atm→Sur) | s=1: forward walk through Atmo run stops at Surface (class 2). bracketIdx=Surface, class 2 ≠ prev class 1. No bracket. Backward walk: prev=Exo[long] not brief. s=2: Surface short-circuit. | both split ✓ (the other case PR #625 broke) |
| `Exo[long],Atmo[40s],Exo[long],Atmo[40s],Exo[long]` (eccentric Pe grazing) | s=1, s=2, s=3, s=4 | s=1: forward walk through Atmo[40s] → bracket=Exo, class 1 = prev class 1. Forward bracket → suppress. s=2: backward walk through Atmo[40s] → bracket=Exo (s=0), class 1 = next class 1. Backward bracket → suppress. s=3, s=4: symmetric. | all suppress ✓ (the S16 case) |
| `Exo[long],Atmo[60s],Exo[long]` (single aerobrake pass) | s=1, s=2 | s=1: forward walk → Exo bracket, suppress. s=2: backward walk → Exo bracket, suppress. | both suppress ✓ |
| **`Exo[long],Atmo[20s],Atmo[20s_forced_break],Exo[long]`** (vessel-switch seam mid-graze) | s=1, s=3 (s=2 is Atmo→Atmo, not an env-class boundary) | s=1: forward walk through both Atmo sections (cumDur=40s<K) → bracket=Exo[long], class 1 = prev class 1. Forward bracket → suppress. s=3: backward walk through both Atmo sections (cumDur=40s<K) → bracket=Exo (s=0), class 1 = next class 1. Backward bracket → suppress. | both suppress ✓ (collapse-walk handles forced same-env breaks) |
| **`Exo[long],ExoPropulsive[5s burn],ExoBallistic[5s coast],Atmo[20s graze],Exo[long]`** (thrust toggles before a graze) | only s=3 is an env-class boundary (s=1,2,4 are intra-class) | s=3 (Exo→Atmo): forward walk through Atmo[20s] → bracket=Exo (s=4), class 1 = prev class 1. Forward bracket → suppress. (Backward walk through ExoBallistic[5s], ExoPropulsive[5s], ExoBallistic[long] = whole class-1 run; cumDur >> K so backward bracket can't fire even if classes matched.) | suppress ✓ |
| `Surface,Atmo[long],Exo[40s],Atmo[long],Surface` (Karman-line tourist hop, ≤150 km apogee) | s=1, s=2, s=3, s=4 | s=1, s=4: Surface short-circuit. s=2: forward walk through Exo[40s] → bracket=Atmo, class 0 = prev class 0. Forward bracket → suppress. s=3: backward walk → bracket=Atmo, class 0 = next class 0. Backward bracket → suppress. | s=1,4 split; s=2,3 suppress ✓ |
| `Surface,Atmo[long],Exo[300s],Atmo[long],Surface` (real suborbital arc with sustained apogee) | s=1, s=2, s=3, s=4 | s=2: forward walk cumDur=300s>K, no forward bracket. Backward walk: prev=Atmo[long] not brief. s=3: prev=Exo[300s] not brief. | all split ✓ |
| **`Exo[long],Atmo[40s],Atmo[40s_break],Atmo[40s_break],Exo[long]`** (cumulative-too-long) | s=1, s=4 | s=1: forward walk through three Atmo sections, cumDur=120s. cumDur < K is FALSE (strict <). No forward bracket. s=4: symmetric. | both split ✓ (sustained Atmo run is a real phase) |
| `Kerbin Atmo,Mun ExoBallistic` (SOI traversal mid-coast, body change) | s=1 | body change short-circuit. | split ✓ (#251) |
| `Loaded[long, Exo],Absolute[1 frame, Atmo, IsBoundarySeam=true]` (Producer C) | s=1 | seam short-circuit (§5). | not a candidate ✓ |
| `Loaded[long, Exo],Absolute[1 frame, Atmo, IsBoundarySeam=false]` (theoretical: seam without flag) | s=1 | persistence predicate; forward walk: s+1 doesn't exist; no forward bracket. Backward walk: prev long, no fire. Falls through to split. | split (legacy fallback for old recordings — accepted; see §6) |

## 5. Producer-C seam handling

The optimizer cannot reliably distinguish a "brief terminal section that's
really a phase change" from a "brief terminal section that's a recorder
bookkeeping artifact" without an explicit signal. The clean fix is at the
producer.

### 5.1 New field: `TrackSection.IsBoundarySeam`

Add a `bool IsBoundarySeam` field to `TrackSection`
([TrackSection.cs:50](../../../Source/Parsek/TrackSection.cs:50)). Defaults
to `false`. Set to `true` only by
`BackgroundRecorder.FlushLoadedStateForOnRailsTransition` when emitting the
no-payload boundary section
([BackgroundRecorder.cs:3019-3027](../../../Source/Parsek/BackgroundRecorder.cs:3019)):

```csharp
if (persistNoPayloadBoundarySection)
{
    StartBackgroundTrackSection(loadedState, nextEnv, ReferenceFrame.Absolute, ut);
    AddFrameToActiveTrackSection(loadedState, boundaryPoint);
    var section = loadedState.currentTrackSection;
    section.isBoundarySeam = true;          // NEW
    loadedState.currentTrackSection = section;
    CloseBackgroundTrackSection(loadedState, ut);
    ...
}
```

(Mutating a struct field requires the round-trip via `loadedState.currentTrackSection`
since `TrackSection` is a `struct`.)

### 5.2 Optimizer treatment

The seam check is **step 1** of the §3 ordering — a hard "always wins"
override regardless of env class on either side:

```csharp
if (prev.isBoundarySeam || next.isBoundarySeam)
    return false;   // bookkeeping artifact, never split here
```

It precedes the body-change short-circuit, the Surface short-circuit, and
the ExoPropulsive short-circuit. See §3 for the rationale: the contract
for `IsBoundarySeam` is "this section is a recorder bookkeeping artifact,
never a split candidate, full stop." Today's Producer-C cannot emit seams
across body changes or Surface transitions, but the contract must hold for
future producers.

### 5.3 Serialization (text + binary, both mandatory)

Production writes both text and binary sidecars for every committed
recording with `RecordingFormatVersion >= 2` via
`RecordingStore.WriteTrajectorySidecar`. The binary codec is positional
(no key/value layer), so adding a `bool` field to the binary `TrackSection`
record requires a **mandatory** binary format version bump — without the
bump, normal saves drop `isBoundarySeam` on round-trip and Producer-C seams
split again after reload (which is exactly the bug this plan fixes).

#### Text codec (sparse, forward-tolerant)

`SerializeTrackSections`
([TrajectoryTextSidecarCodec.cs:1337](../../../Source/Parsek/TrajectoryTextSidecarCodec.cs:1337))
follows a sparse pattern — fields are written only when non-default. Add:

```csharp
if (track.isBoundarySeam)
    tsNode.AddValue("seam", "1");
```

`DeserializeTrackSections`
([TrajectoryTextSidecarCodec.cs:1436](../../../Source/Parsek/TrajectoryTextSidecarCodec.cs:1436))
reads the value defaulting to `false` when absent:

```csharp
if (tsNode.HasValue("seam") && tsNode.GetValue("seam") == "1")
    section.isBoundarySeam = true;
```

No format version bump needed for the text codec — old loaders silently
ignore unknown keys.

#### Binary codec (positional, requires mandatory version bump)

Bump the binary format version. Suggested name and value (final names
chosen during implementation; pick whichever fits existing constants in
[`RecordingStore.cs:57-61`](../../../Source/Parsek/RecordingStore.cs:57)
naming style):

```csharp
internal const int BoundarySeamFlagBinaryVersion = 8;     // gated read/write
internal const int CurrentBinaryFormatVersion    = 8;     // bumped from 7
```

`WriteTrackSections`
([TrajectorySidecarBinary.cs:627](../../../Source/Parsek/TrajectorySidecarBinary.cs:627))
emits the byte only on `binaryVersion >= 8`:

```csharp
if (binaryVersion >= BoundarySeamFlagBinaryVersion)
    writer.Write(track.isBoundarySeam);   // 1 byte
```

`ReadTrackSections`
([TrajectorySidecarBinary.cs:653](../../../Source/Parsek/TrajectorySidecarBinary.cs:653))
reads the byte on `>= 8`, defaults to `false` on `< 8`:

```csharp
section.isBoundarySeam = (binaryVersion >= BoundarySeamFlagBinaryVersion)
    ? reader.ReadBoolean()
    : false;
```

Default-false on legacy reads has zero behaviour change for legacy
recordings — but new saves carry the flag forward correctly, which is the
whole point of the change. Any code that pins the binary format version
elsewhere (e.g. a probe / signature byte) needs a parallel update; the
implementation phase audits the call sites.

**Test #25 (binary round-trip) is mandatory**, not conditional. See §9.3.

### 5.4 Why a flag, not a heuristic

A heuristic ("if the brief section is at end-of-recording and is `Background`/`Absolute`
with one frame") would work for the current Producer-C path but couples the
optimizer to a recorder implementation detail. The flag makes the contract
explicit. Future producers that need similar treatment (e.g. a seam emitted
at scene-exit, or a future "checkpoint marker" producer) only need to set the
flag, no optimizer change.

## 6. Backward compatibility

The optimizer split pass runs in `RecordingStore.RunOptimizationPass`
([RecordingStore.cs:1970-2080](../../../Source/Parsek/RecordingStore.cs:1970))
on every load. Already-split chain segments carry distinct `SegmentPhase`
tags and `CanAutoMerge`
([RecordingOptimizer.cs:34-70](../../../Source/Parsek/RecordingOptimizer.cs:34))
requires equal phase, so the new (stricter) rule cannot retroactively
re-merge already-split chains. Forward-only by construction — same property
PR #625 had.

**Recordings affected on next load:**

- Existing recordings already split under the old liberal rule: unchanged.
  Each half is `< 2` env-class-distinct sections and isn't a re-evaluation
  target.
- Existing recordings that were not yet optimizer-split (because they had
  ghosting-trigger events blocking `CanAutoSplit`, or short halves, etc.):
  re-evaluated under the new rule. If they contain a graze pattern that the
  old rule would have split, they now stay whole. If they contain a real
  phase change, they still split. **Net effect on legacy recordings: same or
  fewer splits.** No re-merging.
- New recordings with a Producer-C seam emitted under the new code: the seam
  has `isBoundarySeam = true`, optimizer skips the boundary, no spurious
  split. New recordings without the seam (legacy producers, future producers
  that don't set the flag): the brief terminal section falls through to
  split under the persistence-predicate edge-of-recording rule (§3.3). For
  Producer-C recordings written under the OLD code (pre-this-plan), the
  seam will not have the flag set — it will be split by the optimizer on
  next load. This is the same behaviour Producer-C recordings have today, so
  no regression.

## 7. Data model changes

| Type | Field | Type | Default | Serialized |
|---|---|---|---|---|
| `TrackSection` | `isBoundarySeam` | `bool` | `false` | text: sparse `seam=1` key (no version bump); binary: 1 byte at v8, gated by `BoundarySeamFlagBinaryVersion = 8` (mandatory bump from v7) |

Binary `CurrentBinaryFormatVersion` bumps from 7 to 8.
`MeaningfulBoundaryWindowSeconds` from PR #625 is gone (revert removed it).
This plan introduces `BriefSectionMaxSeconds = 120.0` — the name reads with
the predicate ("section is brief if duration < K"), is not a substring of
the reverted symbol so grep stays clean, and avoids the misleading
"meaningful" prefix that confused the PR #625 design.

## 8. Diagnostic logging

Per the project logging requirements, every decision point in
`FindSplitCandidatesForOptimizer` must be loggable. The PR #625 design's
pattern of accept-side per-candidate Verbose lines + per-recording aggregate
suppression-counter line is the right shape; reuse it.

```
[Parsek][VERBOSE][Optimizer] Split candidate (BodyChange): rec=<id> sec=<s> splitUT=<ut>
                                          (SurfaceInvolved)
                                          (ExoPropulsiveAtCrossing)
                                          (PersistedPhaseChange)

[Parsek][VERBOSE][Optimizer] Split suppressed: rec=<id> evaluated=<n>
                                                grazeForward=<x> grazeBackward=<y>
                                                seamSkipped=<z>
                                                splittableButRejected=<w>
```

Discriminator enum (replaces the PR #625 `SplitBoundaryReason` shape):

```csharp
internal enum SplitBoundaryReason
{
    NotABoundary = 0,                   // env unchanged AND body unchanged
    BodyChange,                         // #251 — always-meaningful
    SurfaceInvolved,                    // class 2 (Surface) on either side
    ExoPropulsiveAtCrossing,            // S3 short-circuit — engine firing
    PersistedPhaseChange,               // persistence predicate accepted
    SuppressedGrazeForward,             // forward bracket fired (clause A)
    SuppressedGrazeBackward,            // backward bracket fired (clause B)
    SuppressedBoundarySeam              // §5 — Producer-C seam flag set
}
```

The accept-side log fires once per accepted split candidate (bounded by the
`break` after one split per recording per pass × `maxSplitsPerPass` cap from
`RunOptimizationSplitPass`). The aggregate suppression-counter log fires once
per recording when at least one boundary was suppressed; per-boundary
suppression logs are intentionally suppressed because an eccentric grazing
recording can present hundreds of suppressed boundaries (CLAUDE.md "Batch
counting convention").

Producer-C seam emit also gets a dedicated log line, already present today
([BackgroundRecorder.cs:3024-3026](../../../Source/Parsek/BackgroundRecorder.cs:3024)).
Extend it to include the new `seam=1` flag:

```
[Parsek][INFO][BgRecorder] Persisted no-payload on-rails boundary section: pid=<x>
                            <prev>-><next> at UT=<ut> (seam=1)
```

## 9. Test plan

All xUnit cases land in `Source/Parsek.Tests/RecordingOptimizerTests.cs`
under a new `#region Persistence-based split predicate`. Existing test
fixtures (`MakeRecordingWithSections`) extend to accept variable
section-duration parameters and a new optional `bool isBoundarySeam` per
section.

### 9.1 Unit tests — predicate logic

Each test specifies exactly which clause (A, B, both, or neither) is
expected to fire, and what regression it guards against.

1. **`Persistence_AscentLongAtmoLongExo_Splits`** — `Surface,Atmo[300],Exo[600]`. Both s=1 (Surface short-circuit) and s=2 (no bracket either side, both long) split. **Guards** the "engine-off coast through 70 km" regression that PR #625 caused.
2. **`Persistence_DeorbitLongExoMediumAtmoLongSurface_Splits`** — `Exo[1800],Atmo[120],Surface[60]`. s=1 splits (forward neighbour is Surface, class 2 ≠ Exo class 1, no bracket); s=2 Surface short-circuit. **Guards** the passive-deorbit regression.
3. **`Persistence_EccentricGrazing_4Crossings_AllSuppress`** — `Exo[1500],Atmo[40],Exo[1500],Atmo[40],Exo[1500]`. All 4 boundaries suppressed via clause A or B. **Guards** the S16 chain-explosion case.
4. **`Persistence_AerobrakeSinglePass_BothBoundariesSuppress`** — `Exo[1000],Atmo[60],Exo[1000]`. s=1 suppress (clause A); s=2 suppress (clause B). **Guards** the "single aerobrake = single segment" intent.
5. **`Persistence_KarmanLineHop_ExoBracketed_SuppressesExoSplits`** — `Surface,Atmo[300],Exo[40],Atmo[200],Surface`. s=1,4 Surface short-circuit; s=2 suppress (clause A, forward neighbour Atmo); s=3 suppress (clause B, backward neighbour Atmo). **Guards** sub-orbital tourist hop intent.
6. **`Persistence_RealSuborbitalArc_LongApogee_AllSplit`** — `Surface,Atmo[300],Exo[300],Atmo[200],Surface` with K=120. s=2 splits (next 300>K); s=3 splits (prev 300>K). **Guards** that K=120 doesn't suppress real suborbital arcs.
7. **`Persistence_BoundarySeam_NotASplitCandidate`** — `Loaded[long,Exo],Absolute[1 frame,Atmo,seam=true]`. s=1 not a candidate (seam short-circuit, §5). **Guards** the Producer-C path.
8. **`Persistence_BoundarySeamFlagOnEitherSide_NotASplitCandidate`** — symmetric variant where the *previous* section has `isBoundarySeam=true`. Symmetric short-circuit. **Guards** the bidirectional flag check.
9. **`Persistence_ExoPropulsiveAtCrossing_AlwaysSplits_RegressionOfS3`** — `Atmo[300],ExoPropulsive[600]`. S3 short-circuit. **Guards** the "engine-on at boundary" case stays a guaranteed split.
10. **`Persistence_BodyChange_SameEnvClass_Splits_RegressionOf251`** — Kerbin `ExoBallistic`,Mun `ExoBallistic`. #251 short-circuit. **Guards** SOI traversal stays a split.
11. **`Persistence_BodyChange_AndClassChange_NoBracket_Splits`** — Kerbin `ExoBallistic`,Mun `Atmospheric`. Body change short-circuits before persistence predicate runs. **Guards** the predicate ordering — body change beats env class.
12. **`Persistence_ApproachExoGrazing_BothSuppress`** — Mun `Exo[1000],Approach[40],Exo[1000]`. Approach↔Exo goes through the same gate as Atmo↔Exo (eccentric Mun grazing case). **Guards** the airless-body-graze symmetric coverage.
13. **`Persistence_ApproachToSurface_AlwaysSplits`** — Mun `Approach[60],Surface`. s=1 Surface short-circuit. **Guards** the Surface bucket isn't accidentally squeezed by the predicate.
14. **`Persistence_EndOfRecording_BriefNextNoFollowup_Splits`** — `Exo[1500],Atmo[40,EOR]` (only 2 sections, recording ends in Atmo). s=1 falls through to split (no s+1 to bracket-match, conservative default). **Guards** the §3.3 edge-of-recording rule.
15. **`Persistence_EndOfRecording_BriefNextWithSeamFlag_Suppressed`** — same as 14 but with `isBoundarySeam=true` on the brief Atmo. Seam short-circuit. **Guards** the §5 explicit override of the §3.3 fallback.
16. **`Persistence_BoundaryAtIndexOne_NoBackwardLookup_DoesntCrash`** — recording with sections `[Atmo[300],Exo[600]]`, boundary at s=1. `s-2 < 0` so clause B can't fire; only clause A is evaluated. **Guards** array-index safety on minimal recordings.
17. **`Persistence_BriefSectionWithMinimalDuration_Suppresses`** — recording with a single 0.4 s middle section bracketed by long same-class neighbours. Predicate suppresses via collapse-walk clause A or B (the single brief section is the entire run). **Guards** that the algorithm correctly handles minimum-duration brief sections.
18. **`Persistence_ForcedBreakSameEnvMidGraze_CollapseWalkSuppresses`** — `Exo[1500],Atmo[20s],Atmo[20s],Exo[1500]`. Two adjacent same-`SegmentEnvironment` Atmo sections (vessel-switch / source-change forced break). s=1 (Exo→Atmo): forward walk through both Atmo sections (cumDur=40s<K), bracket=Exo class 1=prev class 1, suppress. s=2 (Atmo→Atmo) is not an env-class boundary, skipped. s=3 (Atmo→Exo): backward walk through both Atmo sections, bracket=Exo (s=0)=next class, suppress. **Guards** the same-`SegmentEnvironment` forced-break case the §3.2 collapse-walk fixes — a single-step bracket would land on the seam-split half and split both boundaries.
19. **`Persistence_ThrustToggleAdjacentToGraze_CollapseWalkSuppresses`** — `ExoBallistic[1500],ExoPropulsive[5s],ExoBallistic[5s],Atmo[20s],ExoBallistic[1500]`. ExoBallistic↔ExoPropulsive seams at s=1 and s=2 are not env-class boundaries (both class 1, `envChanged=false`). s=3 (ExoBallistic→Atmo): forward walk through Atmo[20s] alone (next is Exo class 1, walk stops), bracket=Exo class 1=prev class 1, suppress. **Guards** that ExoBallistic↔ExoPropulsive thrust-toggle seams within an Exo run don't disrupt the bracket — the s=3 boundary's `prev` is `ExoBallistic[5s]` but its split class still equals the long Exo run's class.
20. **`Persistence_CumulativeRunBeyondK_Splits`** — `Exo[1500],Atmo[40s],Atmo[40s],Atmo[40s],Exo[1500]`. Three same-`SegmentEnvironment` Atmo sections (multiple forced breaks), cumDur=120s. cumDur < K is FALSE (strict `<`). s=1: forward walk cumDur=120s, no forward bracket, falls through to split. s=4: symmetric, split. **Guards** that the strict `<` cumulative-duration check works correctly at the K boundary — a sustained Atmo run is a real phase, not a graze.
21. **`Persistence_AggregateSuppressionLog_FiresOncePerRecording`** — log-assertion test: 4 grazing boundaries in one recording → exactly one `Split suppressed: rec=…` aggregate line, no per-boundary suppression spam. **Guards** the CLAUDE.md "Batch counting convention" pattern.
22. **`Persistence_AcceptanceLog_EmitsDiscriminator`** — log-assertion test: one accepted Atmo→Exo (long, long) → one `Split candidate (PersistedPhaseChange): …` line. **Guards** that the new acceptance reason is wired into the log.

### 9.2 Integration tests — round-trip through `RunOptimizationPass`

Extend the existing `RecordingStoreTests` patterns (the test class touching
`RecordingStore` static state already has `[Collection("Sequential")]`) to
end-to-end-verify chain shape:

23. **`OptimizationPass_PassiveDeorbitReentry_ProducesAscentExoReentryChain`** — synthetic recording: `Surface, Atmo[long], ExoBallistic[long], Atmo[medium], Surface`. After `RunOptimizationPass`, chain has 4 segments (surface-launch / atmo-ascent / exo-orbit / atmo-reentry-and-landing). Each has its own `SegmentPhase`.
24. **`OptimizationPass_EccentricGrazing_StaysOneSegment`** — synthetic recording: 4 atmo↔exo oscillations, no Surface. After pass, chain length 1 (no splits).
25. **`OptimizationPass_ProducerCSeam_NotSplit_NoChainGrowth`** — synthetic recording with the seam-flag Producer-C shape. After pass, single recording (no chain expansion).

### 9.3 Serialization round-trip (mandatory)

Both text and binary codec coverage is mandatory because production writes
both sidecars (`RecordingStore.WriteTrajectorySidecar` runs both for every
recording with `RecordingFormatVersion >= 2`). A single-codec test would
let the binary codec silently drop the flag and Producer-C seams would
split again after reload — exactly the bug this plan fixes.

26. **`TrackSection_BoundarySeamFlag_RoundTripsThroughTextCodec`** — write a recording with a seam-flagged section through `TrajectoryTextSidecarCodec`, load, verify the flag survives. **Guards** the text codec sparse-field write/read path.
27. **`TrackSection_BoundarySeamFlag_DefaultsFalseOnLegacyTextLoad`** — load a text-codec recording without the `seam` key, verify `isBoundarySeam == false`. **Guards** forward-tolerance for legacy text recordings.
28. **`TrackSection_BoundarySeamFlag_RoundTripsThroughBinaryCodec`** — **mandatory.** Write through `TrajectorySidecarBinary` at `binaryVersion = BoundarySeamFlagBinaryVersion`, read back, verify flag survives. **Guards** the binary codec positional write/read path — without this test, a regression in binary version gating would silently drop the flag on every save and the bug-fix property would not hold.
29. **`TrackSection_BoundarySeamFlag_DefaultsFalseOnLegacyBinaryLoad`** — write a recording at `binaryVersion = 7`, read with the new code, verify `isBoundarySeam == false` and that the next field (`anchorVesselId`) deserializes at the correct offset. **Guards** the binary version-gate read path — catches the worst-case regression where a v7 reader on a v8 file would desynchronize positionally.

### 9.4 In-game smoke test

One `[InGameTest(Category = "Optimizer", Scene = GameScenes.FLIGHT)]` test in
`Source/Parsek/InGameTests/RuntimeTests.cs` (or a new file under that dir):

30. **`RealAscentReentry_ProducesPerPhaseChain_InGame`** — automate a stock craft launch through 70 km with engines firing through the boundary (forces ExoPropulsive → S3 short-circuit case), circularize, deorbit (passive coast through 70 km on the way down), parachute, land. After landing, call `RecordingStore.RunOptimizationPass()`. Assert the chain has at least 4 segments (pad / atmo-ascent / exo-orbit / atmo-reentry-and-landing). **Guards** against future recorder changes that produce TrackSection boundary UTs the predicate doesn't expect.

### 9.5 Tests to remove / update

- `EccentricOrbitOptimizerInvariantTests` already covers the on-rails case (PR #622). No changes — those tests are about the recorder pathway, not the optimizer predicate.
- The PR #625 `MeaningfulGate_*` tests are already gone (revert removed them).

## 10. Implementation phases

The work splits cleanly into three independently-reviewable phases on a
single branch. Each phase is one commit; each commit must pass `dotnet
build` and `dotnet test` before the next is started.

### Phase 1 — `TrackSection.IsBoundarySeam` field, producer wiring, codec updates

- Add `bool isBoundarySeam` field to `TrackSection` struct, default `false`.
- Wire `BackgroundRecorder.FlushLoadedStateForOnRailsTransition` to set the
  flag on the no-payload boundary section (§5.1). Update its log line to
  include `(seam=1)`.
- Text codec: `SerializeTrackSections` / `DeserializeTrackSections` add
  sparse `seam` key (§5.3). No format version bump for the text codec —
  forward-tolerant.
- **Binary codec: mandatory format version bump.** Add
  `BoundarySeamFlagBinaryVersion = 8`, bump `CurrentBinaryFormatVersion`
  to 8. Gate write on `>= 8`, gate read on `>= 8` (default-false on `< 8`).
  Audit any other code that pins the binary format version (probes,
  signatures) for parallel updates (§5.3).
- Test generator: extend `Source/Parsek.Tests/Generators/RecordingBuilder.cs`
  to accept an optional `bool isBoundarySeam` per section, so synthetic
  Producer-C-seam recordings can be constructed in xUnit fixtures.
- Verify `Recording.DeepCopyTrackSections` and `SessionMerger.CloneTrackSections`
  propagate the new struct field (default struct-copy semantics should
  carry it, but explicit verification belongs in this phase, not as a
  closeout assumption).
- ParsekScenario audit: `TrackSection.isBoundarySeam` lives inside the
  `TrackSections` list, which is `.prec` sidecar-serialized (not `.sfs`-
  serialized), so `ParsekScenario.OnSave/OnLoad` does not need an update.
  Note this in the commit message so reviewers don't need to re-derive it.
- Tests: 26 (text round-trip), 27 (legacy text default), 28 (binary
  round-trip — **mandatory**), 29 (legacy binary default + positional
  desync check — **mandatory**).

This phase changes serialized byte size for new recordings (the v8 binary
format includes one extra byte per `TrackSection`) and changes the
`CurrentBinaryFormatVersion` constant. Otherwise behaviour-neutral: the
optimizer does not yet read the flag, so existing optimizer behaviour is
preserved. Validates both serialization stories before any logic change
depends on them.

### Phase 2 — Persistence predicate in `RecordingOptimizer`

- Add `BriefSectionMaxSeconds = 120.0` const (semantic name — see §3.1).
- Add `SplitBoundaryReason` enum with the 8 values from §8.
- Add helper `IsSplittableEnvOrBodyBoundary(rec, s, out reason)` that
  encodes the §3 ordering (**seam → not-a-boundary → body → Surface →
  ExoPropulsive → persistence**). The seam check is step 1, ahead of all
  always-split short-circuits, per §3 / §5.2.
- Add helper `IsGrazePattern(rec, s, out grazeReason)` that implements
  the §3.1 (A) and (B) bracket clauses with the same-`SplitEnvironmentClass`
  collapse-walk (§3.2). The walk MUST collapse adjacent same-split-class
  sections, not just look at single-step neighbours — single-step lookup
  misclassifies forced-break and thrust-toggle cases (see test #18, #19).
- Wire `FindSplitCandidatesForOptimizer` to call the helper, accumulate
  per-recording aggregate suppression counters, emit accept-side per-candidate
  Verbose log + aggregate suppression-counter Verbose log.
- Leave `FindSplitCandidates` (legacy/test-only path) untouched — it keeps
  the pre-predicate "always split on env change" semantics, same as today.
- Tests: 1–22 (unit + log assertions).

### Phase 3 — Integration tests + closeout

- Add tests 23, 24, 25 (round-trip through `RunOptimizationPass`).
- Add in-game smoke test 30.
- Update `CHANGELOG.md` (`0.9.1 / Bug Fixes`, replacing the revert bullet
  with the redesign bullet). Call out the binary format bump (`v7 → v8`)
  and the upgrade-time effects (legacy Producer-C seam recordings will see
  one extra spurious split per seam on first load after upgrade — same
  behaviour they have today, no regression, but visible enough to mention).
- Update `docs/dev/todo-and-known-bugs.md` — mark #632 as ~~done~~,
  reference this plan and the implementation PR.
- Update `docs/dev/research/optimizer-meaningful-split-rule.md` — change
  the "Conclusion reverted" callout to "Superseded by
  `docs/dev/plans/optimizer-persistence-split.md`".
- Update `.claude/CLAUDE.md` "On-rails BG vessels emit no env-classified
  TrackSections" note to add the new invariant: "Producer-C seams carry
  `isBoundarySeam=true`; the optimizer skips boundaries on either side of
  a flagged section as a hard `IsSplittableEnvOrBodyBoundary` step-1
  override."

## 11. What this plan does NOT change

- `SplitEnvironmentClass` taxonomy. `Atmospheric / Exo* / Surface* /
  Approach` mapping is unchanged. The predicate is layered on top.
- `EnvironmentDetector.Classify` and `EnvironmentHysteresis`. Recorder-side
  classification and debounce stay as-is.
- `CanAutoSplit` and `CanAutoSplitIgnoringGhostTriggers` ghosting-trigger
  gates. The predicate decides what's a split *candidate*; these decide
  what's actually split. Both stay applicable.
- `CanAutoMerge`. The merge gate stays unchanged. Forward-only application
  rests on its existing `SegmentPhase` equality check.
- `EccentricOrbitOptimizerInvariantTests` and the
  `BackgroundOnRailsState` field-set invariant. The on-rails case is
  guarded structurally; this plan adds an orthogonal layer for the
  loaded-physics case.
- `FindSplitCandidates` (the legacy test-only path used by the ghost
  chain walker). It keeps pre-predicate behaviour. Future cleanup may
  remove it once we're confident no production caller depends on the
  pre-predicate semantics.

## 12. Out of scope / future work

- **Revisiting `K`.** 120 s is a starting point grounded in the worked
  examples in §4, not playtest data. After this lands, capture chain
  shapes from a few real save sessions and verify the predicate matches
  intent. Adjust if needed.
- **Cross-recording graze detection.** The predicate is local to one
  recording's TrackSections list. A vessel that grazes Pe across multiple
  recordings (e.g. after a vessel switch + back) is not unified by this
  plan. Acceptable v1 limitation — multiple recordings with one boundary
  each don't suffer the chain-explosion symptom that motivated the change.
- **Forward-look for "vessel ever returns within K seconds" multi-step
  scan.** Considered in §3.2 and rejected: the recorder hysteresis already
  guarantees brief sections are exactly one section wide, and a multi-step
  scan would fold distinct phases together. If a future refactor changes
  the hysteresis contract, revisit.
- **Producer-side suppression of the seam emit.** §5 routes the seam
  through the optimizer-skip path with a flag. An alternative is to never
  emit the seam at all (the env-class transition is bookkeeping; if the
  optimizer is going to ignore it, why emit it). Not pursued because the
  seam carries a real env state useful for diagnostics and possibly
  playback fidelity. Revisit if disk space or load complexity becomes a
  concern.
- **Persistence threshold per env-class pair.** A single `K` for both
  Atmo↔Exo and Approach↔Exo is the simplest design. If playtest shows the
  Mun-grazing case wants a different threshold than Kerbin (e.g. because
  Mun orbital periods differ from Kerbin), introduce a small lookup table.
  Not needed for v1.

## 13. Risk assessment

- **Risk:** the persistence-window predicate suppresses a real phase change
  the player wants split.
  **Likelihood:** low. §4's worked examples cover the obvious cases. K=120s
  is far above all the brief-pattern durations and far below most sustained
  phase durations. **Known soft case:** Karman-line tourist hops with apogee
  >150 km cross the K threshold and do split — the §3.1 calibration table
  documents this; it is acceptable because a sustained Exo phase IS a real
  flight phase from the player's perspective.
  **Mitigation:** logging; unit tests 1–22 cover the discriminator clauses
  exhaustively (including the collapse-walk same-class cases via tests #18,
  #19, #20); in-game smoke test 30 covers the end-to-end real-flight path.

- **Risk:** the `IsBoundarySeam` flag is set on a section that *should* be a
  split candidate (e.g. a future producer mistakenly sets the flag).
  **Likelihood:** very low. Only one producer sets the flag today; new
  producers must opt in.
  **Mitigation:** the flag's only effect is to skip the boundary at the
  optimizer level. A misuse produces fewer splits, not more — the failure
  mode is the player having fewer chain segments, which is recoverable
  (manual split UI exists).

- **Decision (committed, not a risk):** binary format version bump from v7
  to v8. `TrajectorySidecarBinary` is positional and cannot preserve
  forward-compat for an added field without a bump. Phase 1 ships
  `BoundarySeamFlagBinaryVersion = 8` as a mandatory part of the
  serialization story; tests 28 and 29 lock down both write and read paths.
  Audit any other code that pins the binary format version (probes,
  signatures, `RecordingStore.cs:57-61` named constants) for parallel
  updates during implementation.

- **Risk:** a graze pattern that the predicate suppresses turns out to be
  a real phase change the player wanted to loop separately (e.g. a player
  who specifically wants their aerobrake passes as separate loop segments;
  an aborted Mun landing that the predicate folds back into a "graze flyby"
  pattern).
  **Likelihood:** very low — the design intent (per-phase loop) is for the
  *common* gameplay flow, not connoisseur edge cases. Aborted Mun landings
  are the most plausible miss; players expecting a separate chain segment
  for the abort phase can use the manual split UI.
  **Mitigation:** the manual split UI lets a player force a split. If
  playtest reveals demand, add a setting to widen `K` or disable the
  predicate per-recording.
