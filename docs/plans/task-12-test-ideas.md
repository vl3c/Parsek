# Task 12: Test + Logging Ideas — Refined

Refined after Opus review. Each test states: what it tests, what bug it catches, why it's not vacuous (what would make it fail). Tests marked COVERED are excluded. Tests that pass by default are excluded.

---

## Part A: Logging Gaps to Fill

These are code paths that need verbose logging ADDED before log-verification tests can work. Each gap describes what should be logged and why.

### A1. RecordingTree.Save — log summary

**Location:** `RecordingTree.cs:37` — `Save(ConfigNode treeNode)` has zero logging.

**Add:** At the end of `Save`, log tree name, recording count, branch point count, ResourcesApplied state.

**Why:** Diagnosing save corruption requires knowing what was written. Currently invisible.

### A2. RecordingTree.Load — log summary

**Location:** `RecordingTree.cs:69` — `Load(ConfigNode treeNode)` has zero logging.

**Add:** After loading, log tree ID, recording count loaded, branch point count, which optional fields were present vs defaulted (e.g., "resourcesApplied: present=true" vs "resourcesApplied: missing, defaulted to false").

**Why:** Diagnosing load failures from old saves requires knowing what was found in the ConfigNode.

### A3. ApplyTreeResourceDeltas — log outer loop decisions

**Location:** `ParsekFlight.cs:3989` — `ApplyTreeResourceDeltas(double currentUT)` iterates committed trees silently.

**Add:** For each tree: log whether skipped (already applied), waiting (currentUT <= treeEndUT), or applying. Use `ParsekLog.VerboseRateLimited` since this runs every frame.

**Why:** When a tree's resources don't apply, there's no trace of why. Was the tree already applied? Is UT not past the end yet? Currently invisible.

### A4. ApplyTreeLumpSum — log clamping

**Location:** `ParsekFlight.cs:4023-4042` — resource deltas are clamped to prevent negative balances, but this is silent.

**Add:** When `delta` is clamped (i.e., the original delta would make the balance negative), log both the original and clamped values.

**Why:** Player sees fewer funds deducted than expected. Without a log line, the cause (clamping) is invisible.

### A5. PositionGhostAtSurface — log positioning

**Location:** `ParsekFlight.cs:5570` — positions a ghost at a surface location with zero logging.

**Add:** Log body name, lat/lon/alt, and whether body was found.

**Why:** Invisible landed ghosts are a likely failure mode. No diagnostic trace currently exists for this code path.

### A6. FindCommittedTree — log miss

**Location:** `ParsekFlight.cs:4056` — returns null silently when tree is not found.

**Add:** When returning null, log the requested treeId and the number of committed trees checked.

**Why:** `TakeControlOfGhost` and `ApplyTreeResourceDeltas` silently skip when `FindCommittedTree` returns null. If a tree is missing from the committed list, there's no trace.

### A7. ResourceBudget.ComputeTotal — log per-tree cost breakdown

**Location:** `ResourceBudget.cs:201-208` — tree loop adds costs without logging which tree contributed what.

**Add:** Inside the tree loop, log each tree's name, delta values, whether applied (skipped) or contributing, and the cost added.

**Why:** Budget mismatch is hard to diagnose without knowing which tree contributed which cost component.

### A8. RecordingStore.CommitTree — use structured logging

**Location:** `RecordingStore.cs:354` — uses legacy `Log($"[Parsek] Committed tree...")` which strips the prefix.

**Improvement:** Already functional but could be enhanced to use `ParsekLog.Info("RecordingStore", ...)` directly for consistency. Low priority.

### A9. FinalizeTreeRecordings — log per-recording detail

**Location:** `ParsekFlight.cs:3254` — iterates each recording but only logs at entry/exit, not per-recording.

**Add:** For each recording in the tree, log: recording ID, vessel name, point count, orbit segment count, has terminal state?, has snapshot?, is leaf?

**Why:** When a recording is finalized incorrectly (e.g., missing terminal state, missing snapshot), there's no per-recording trace.

### A10. CommitTreeFlight — log ResourcesApplied and marking detail

**Location:** `ParsekFlight.cs:3130-3137` — sets ResourcesApplied and marks LastAppliedResourceIndex, but doesn't log the counts.

**Add:** Log how many recordings had their LastAppliedResourceIndex marked, and confirm ResourcesApplied=true.

---

## Part B: Unit Tests — Pure Methods (directly callable)

### B1. FormatDuration — all branches

**Method:** `MergeDialog.FormatDuration(double)` — `internal static`, directly testable.

**Branches to cover:**
| Input | Expected | Branch exercised |
|-------|----------|-----------------|
| `double.NaN` | `"0s"` | NaN guard (line 416) |
| `double.PositiveInfinity` | `"0s"` | Infinity guard (line 416) |
| `-5` | `"0s"` | Negative clamp (line 417) |
| `0` | `"0s"` | < 60 path |
| `45` | `"45s"` | < 60 path, nonzero |
| `60` | `"1m"` | Exact boundary: 60 < 60 is false → falls to < 3600 path, s=0 |
| `61` | `"1m 1s"` | < 3600 path with seconds remainder |
| `3600` | `"1h"` | Exact boundary: 3600 < 3600 is false → falls to else, min=0 |
| `3661` | `"1h 1m"` | Else path with minutes remainder |
| `86400` | `"24h"` | Large value (1 day) |

**What bug it catches:** Off-by-one at 60/3600 boundaries, wrong string format, missing guard.

**Why not vacuous:** Each case has a unique expected string. If any branch is wrong, the expected string won't match.

### B2. GetLeafSituationText — all 8 terminal states + fallbacks

**Method:** `MergeDialog.GetLeafSituationText(Recording)` — `internal static`, directly testable.

**Cases:**
| TerminalState | TerminalOrbitBody | TerminalPosition.body | Expected |
|---------------|-------------------|----------------------|----------|
| `Orbiting` | `"Kerbin"` | — | `"Orbiting Kerbin"` |
| `Orbiting` | `null` | — | `"Orbiting unknown"` |
| `Landed` | — | `"Mun"` | `"Landed on Mun"` |
| `Landed` | — | (no TerminalPosition) | `"Landed on unknown"` |
| `Splashed` | — | `"Kerbin"` | `"Splashed on Kerbin"` |
| `SubOrbital` | `"Kerbin"` | — | `"Sub-orbital, Kerbin"` |
| `SubOrbital` | `null` | — | `"Sub-orbital, unknown"` |
| `Destroyed` | — | — | `"Destroyed"` |
| `Recovered` | — | — | `"Recovered"` |
| `Docked` | — | — | `"Docked"` |
| `Boarded` | — | — | `"Boarded"` |
| `null` | — | — (VesselSituation="FLYING") | `"FLYING"` |
| `null` | — | — (VesselSituation=null) | `"Unknown"` |

**What bug it catches:** Missing switch case, null body fallback returning empty string instead of "unknown", wrong prefix for SubOrbital (comma vs space).

**Why not vacuous:** 13 cases, each with a unique expected string derived from the source code's switch statement.

### B3. ComputeTotal — multiple trees, mixed ResourcesApplied

**Method:** `ResourceBudget.ComputeTotal` — `internal static`, directly testable.

**Setup:** Tree A: `ResourcesApplied=true`, `DeltaFunds=-3000`. Tree B: `ResourcesApplied=false`, `DeltaFunds=-7000`. No standalone recordings.

**Assert:** `budget.reservedFunds == 7000` (only tree B contributes).

**What bug it catches:** ResourcesApplied flag check missing/inverted. If both counted → 10000. If neither → 0. If inverted → 3000.

**Why not vacuous:** The only way to get exactly 7000 is correct ResourcesApplied filtering.

### B4. IsSpawnableLeaf — exhaustive truth table

**Method:** `RecordingTree.IsSpawnableLeaf(Recording)` — `internal static`, directly testable.

**The method has 3 sequential guards: ChildBranchPointId != null → false, terminal state in {Destroyed, Recovered, Docked, Boarded} → false, VesselSnapshot == null → false, else → true.**

**Cases not covered by existing tests:**
| ChildBPId | TerminalState | Snapshot | Expected | Existing Coverage |
|-----------|---------------|----------|----------|-------------------|
| null | Orbiting | non-null | true | `IsSpawnableLeaf_OrbitingWithSnapshot_True` |
| null | Destroyed | non-null | false | `IsSpawnableLeaf_DestroyedWithSnapshot_False` |
| null | Docked | non-null | false | `IsSpawnableLeaf_DockedWithSnapshot_False` |
| null | Boarded | non-null | false | `IsSpawnableLeaf_BoardedWithSnapshot_False` |
| "bp1" | null | non-null | false | `IsSpawnableLeaf_HasChild_False` |
| null | null | non-null | true | `IsSpawnableLeaf_NoTerminalState_WithSnapshot_True` |
| null | null | null | false | MISSING |
| null | Recovered | non-null | false | MISSING |
| null | Landed | non-null | true | MISSING |
| null | Splashed | non-null | true | MISSING |
| null | SubOrbital | non-null | true | MISSING |

**Add the 5 MISSING cases.** Especially `Recovered` (the only non-spawnable terminal state not explicitly tested) and `null snapshot with no terminal state` (tests the snapshot guard independently).

**What bug it catches:** A new TerminalState value being incorrectly filtered, or the snapshot guard being accidentally removed.

### B5. RebuildBackgroundMap — realistic multi-level integration

**Method:** `RecordingTree.RebuildBackgroundMap()` — instance method on plain class, testable.

**Setup:** A tree with:
- root (pid=1, ChildBranchPointId="bp1") — excluded: has child
- child1 (pid=2, TerminalStateValue=Destroyed) — excluded: terminated
- child2 (pid=3, TerminalStateValue=null, ChildBranchPointId="bp2") — excluded: has child
- grandchild1 (pid=4, TerminalStateValue=Docked) — excluded: terminated
- grandchild2 (pid=5, TerminalStateValue=null, no child) — included IF not ActiveRecordingId
- ActiveRecordingId = "grandchild2" — excluded: is active

**Assert:** BackgroundMap is empty (every recording excluded for a different reason).

**Then set ActiveRecordingId to "root" (something else).** grandchild2 should now be in BackgroundMap.

**Assert:** BackgroundMap contains exactly {5: "grandchild2"}.

**What bug it catches:** One exclusion rule hiding another. The test has exactly one recording per exclusion reason, so if any rule is missing, the wrong recording appears in the map.

**Why not vacuous:** The "all excluded" case tests that no false positives leak through. The "one included" case tests that the inclusion logic works when exclusion doesn't apply.

### B6. GetAllLeaves vs GetSpawnableLeaves — Recovered leaf

**Setup:** Tree with 2 leaves: leaf1 (Recovered, no snapshot), leaf2 (Orbiting, with snapshot).

**Assert:** `GetAllLeaves().Count == 2`, `GetSpawnableLeaves().Count == 1` (only leaf2).

**What bug it catches:** `GetAllLeaves` accidentally filtering Recovered, or `GetSpawnableLeaves` accidentally including it. Since Destroyed is tested in `GetAllLeaves_IncludesDestroyed` but Recovered is not, this covers the gap.

### B7. GetSpawnableLeaves after dock merge — DAG case

**Setup:** Use the existing merge integration tree structure (root → split → 2 children → dock → merged child). Give the merged child a snapshot and TerminalState.Orbiting.

**Assert:** `GetSpawnableLeaves()` returns exactly 1 recording (the merged child). Both docked parents are excluded. Root is excluded (has child).

**What bug it catches:** DAG structure confusing the leaf detection. Parents with Docked terminal state accidentally being returned.

---

## Part C: Log-Verification Tests (via TestSinkForTesting)

These tests call real methods with `ParsekLog.SuppressLogging = false`, `VerboseOverrideForTesting = true`, `TestSinkForTesting` capturing lines, and assert specific log content.

### C1. CommitTree logs tree name and recording count

**Setup:** Build tree with 3 recordings, name "Mun Mission". Set `RecordingStore.SuppressLogging = false` and `ParsekLog.SuppressLogging = false`. Attach `TestSinkForTesting`.

**Call:** `RecordingStore.CommitTree(tree)`

**Assert:** Captured lines contain one matching `"Committed tree 'Mun Mission' (3 recordings)"`.

**Why not vacuous:** If the log format changes, the recording count is wrong, or the tree name is not interpolated, this fails. Tests the existing log line at RecordingStore.cs:354.

### C2. StashPendingTree logs tree name

**Setup:** Same as C1 but call `RecordingStore.StashPendingTree(tree)`.

**Assert:** Captured lines contain `"Stashed pending tree 'Mun Mission' (3 recordings)"`.

**Tests existing log at RecordingStore.cs:377.**

### C3. DiscardPendingTree logs tree name

**Setup:** Stash a tree, then call `DiscardPendingTree()`.

**Assert:** Captured lines contain `"Discarded pending tree 'Mun Mission'"`.

**Tests existing log at RecordingStore.cs:408.**

### C4. ComputeTotal tree loop — per-tree verbose log (REQUIRES A7 LOGGING)

**Setup:** 2 trees. Tree A: applied. Tree B: not applied, DeltaFunds=-2000. Set `VerboseOverrideForTesting = true`.

**Call:** `ResourceBudget.ComputeTotal(recordings, milestones, trees)`

**Assert:** Log contains a line for tree A indicating it was skipped (applied). Log contains a line for tree B indicating its cost contribution.

**Depends on:** Logging gap A7 being filled first.

### C5. RecordingTree.Save logs summary (REQUIRES A1 LOGGING)

**Setup:** Tree with 2 recordings, 1 branch point, ResourcesApplied=true.

**Call:** `tree.Save(node)` with TestSinkForTesting active.

**Assert:** Log contains tree name, recording count=2, branch point count=1, resourcesApplied=true.

**Depends on:** Logging gap A1 being filled first.

### C6. RecordingTree.Load logs summary (REQUIRES A2 LOGGING)

**Setup:** Build a ConfigNode, call `RecordingTree.Load(node)`.

**Assert:** Log contains tree ID and recording count loaded.

**Depends on:** Logging gap A2 being filled first.

---

## Part D: Edge Case Tests

### D1. Tree with zero recordings — GetSpawnableLeaves and RebuildBackgroundMap

**Setup:** Empty tree (Recordings dictionary has 0 entries).

**Assert:** `GetSpawnableLeaves()` returns empty list (not null, not crash). `RebuildBackgroundMap()` does not crash. `BackgroundMap` is empty.

**Existing `RecordingTree_EmptyTree_SaveLoadDoesNotCrash` only tests save/load.** This extends to the query methods.

### D2. RecordingTree.Load with unknown/extra fields (forward compat)

**Setup:** ConfigNode with standard fields PLUS `treeNode.AddValue("futureFeatureFlag", "true")` and `treeNode.AddValue("newMetric", "42.5")`.

**Call:** `RecordingTree.Load(node)`

**Assert:** No crash, all standard fields load correctly, unknown fields silently ignored.

**What bug it catches:** Someone adds strict parsing that rejects unknown keys (breaking forward compatibility).

### D3. CommitTree with null tree — no crash, no state change

**Setup:** `RecordingStore.CommitTree(null)`.

**Assert:** `CommittedRecordings.Count` unchanged. `CommittedTrees.Count` unchanged. No crash.

**Tests the null guard at RecordingStore.cs:335.**

### D4. Tree budget with all-terminal leaves — delta still counts

**Setup:** Tree with `DeltaFunds=-3000`, `ResourcesApplied=false`. All leaves are Destroyed or Recovered (no spawnable leaves).

**Call:** `ResourceBudget.ComputeTotal` with this tree.

**Assert:** `reservedFunds == 3000`. The tree's resource delta is counted regardless of leaf terminal states.

**What bug it catches:** Someone adding terminal-state filtering at the budget level (tree delta should always count if not applied).

### D5. BackgroundMap populated correctly after save/load round-trip

**Setup:** Tree with one background-eligible recording: non-active, non-terminated, no child branch, pid=42. Save the tree, then Load it.

**Assert:** After load, `tree.BackgroundMap` contains key 42.

**What bug it catches:** `RebuildBackgroundMap()` not called during Load, or called before recordings are populated. Tests the call at RecordingTree.cs:116.

---

## Part E: Synthetic Tree Recordings (for in-game KSP testing)

### E1. Simple Undock Tree

**Structure:** Root recording (composite vessel, t+30 to t+60) → split at t+60 → active child (upper stage, t+60 to t+120, orbit) + background child (lower stage, t+60 to t+120, surface orbit-only).

**Vessel:** FleaRocket-based, 2 parts (pod + SRB), decouple at split.

**Purpose:** Verify two ghosts appear: root plays then splits, children play independently. Background child ghost positioned from orbit data.

### E2. EVA Tree

**Structure:** Root recording (vessel on pad, t+150 to t+180) → EVA at t+180 → vessel continues (t+180 to t+240) + EVA kerbal walks (t+180 to t+240).

**Purpose:** Verify EVA child recording playback, crew handling (EVA kerbal ghost separate from vessel ghost).

### E3. Destruction Tree

**Structure:** Root recording → split at t+270 → child A continues (t+270 to t+330, orbiting) + child B destroyed at t+300 (terminal state, no spawn).

**Purpose:** Verify destroyed child ghost stops playing at t+300, no spawn attempted. Only child A spawns.

---

## Summary: Final Test Count

| Section | Tests | Notes |
|---------|-------|-------|
| B: Pure method tests | 7 | B1-B7, all directly callable |
| C: Log verification | 6 | C1-C6 (C4-C6 need logging additions first) |
| D: Edge cases | 5 | D1-D5 |
| **Subtotal new tests** | **18** | |
| E: Synthetic recordings | 3 builders | For manual KSP testing |
| A: Logging additions | 10 gaps | Production code changes |

## Decision Points

1. **Rate-limited logging for per-frame tree guards?** The 7 `ApplyResourceDeltas` guard sites that skip tree recordings fire every frame. Adding `VerboseRateLimited` logging there adds noise. Recommendation: skip — the `ApplyTreeResourceDeltas` outer loop logging (A3) covers the important decision points.

2. **Should `FormatDuration` drop seconds for h+m results?** Currently `FormatDuration(3661)` returns `"1h 1m"` (drops the 1 second). This is intentional — at hour scale, seconds are noise. The test should match this behavior.

3. **Synthetic tree recording offsets.** Existing recordings use +30/+60/.../+240 from baseUT=17000. Tree recordings should use +270/+300/+330 (continuing the sequence). The undock tree at +30 to +120 would OVERLAP with existing KSC Hopper (+60) and Flea Flight (+90). Use non-overlapping offsets: E1 at +270, E2 at +390 (leaves 120s gap), E3 at +510.
