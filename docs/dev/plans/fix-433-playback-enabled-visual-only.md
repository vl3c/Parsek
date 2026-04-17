# Fix #433 — `PlaybackEnabled` toggle should be visual-only

**Branch:** `fix/433-playback-enabled-visual-only` (off `origin/main`)
**Worktree:** `C:/Users/vlad3/Documents/Code/Parsek/Parsek-433-playback-enabled-visual-only`
**Ships independently:** #432 runs in parallel but touches disjoint files; no rebase coordination required.

## Invariant

> `Recording.PlaybackEnabled = false` means "hide this recording's ghost during playback" and nothing else. Every career-state effect — ledger actions, resource deltas, crew reservations, vessel spawn at ghost-end, and the career-window timeline display — stays active regardless of the flag. The KSC tracking-station visibility (`ShouldShowInKSC`) is also visual and legitimately gated by the flag.

## Current-state audit (verified against HEAD `0380df61` on 2026-04-17)

The TODO entry at `docs/dev/todo-and-known-bugs.md:106` captures the intent accurately; the file:line citations have drifted 8-80 lines. Verified positions and findings follow; each bullet ends with the call-site disposition.

### Confirmed visual-only reads — KEEP

- `Recording.cs:116` — field declaration with a misleading one-liner. `public bool PlaybackEnabled = true;  // false = skip ghost during playback`. **KEEP field, UPDATE comment.**
- `ParsekFlight.cs:8755` (TODO said 8763) — `skipGhost = !hasData || !rec.PlaybackEnabled || externalVesselSuppressed` inside `ComputePlaybackFlags`. **KEEP** — the flag is the primary visual gate and its sole legitimate career-side effect (spawn suppression) gets rerouted (see next bullet).
- `GhostPlaybackEngine.cs:291-299` (TODO said 276-285) — `if (f.skipGhost) { destroy + continue; }` early-return inside the main per-ghost loop. **MODIFY** — keep the destroy+skip for every cause; add a narrow past-end completion detour gated on `traj.PlaybackEnabled == false` (the *sole* career-neutral skip reason). The other skipGhost causes (`!hasData`, `externalVesselSuppressed`) must keep today's silent-skip — they describe recordings with no spawnable trajectory. `HandlePastEndGhost` at `GhostPlaybackEngine.cs:730` already guards the visual bits (`if (ghostActive)` at :735), so the event path works with `state: null, ghostActive: false`. See Vessel-spawn gate §1 below for the narrowed code shape.
- `ParsekKSC.cs:495` — `ShouldShowInKSC` returns false for `!rec.PlaybackEnabled`. **KEEP as-is** — legitimate tracking-station visual gate.
- `ParsekKSC.cs:158` — caller-side iteration of `ShouldShowInKSC` in `Update()`. **MODIFY carefully** — `ShouldShowInKSC` folds three causes (`!PlaybackEnabled`, non-Kerbin, `Points.Count < 2`) and only the first is a career-neutral visibility choice. Factor `ShouldShowInKSC` into a structural predicate (`IsKscStructurallyEligible`) + a visibility check and branch on each cause separately: structural-reject keeps today's cleanup-only behaviour; `PlaybackEnabled=false` gets the new past-end spawn detour. See Vessel-spawn gate §3 below.
- `ParsekScenario.cs:2698-2699` and `RecordingTree.cs:433-434, 774` — ConfigNode serialization of the flag. **KEEP** — persistence is neutral.
- `Recording.cs:529` (`Clone` copies the flag), `Recording.cs:777` (`IPlaybackTrajectory.PlaybackEnabled => PlaybackEnabled`) — **KEEP** — plumbing for the visual read path.

### Career-state reads gating on `PlaybackEnabled` or `IsChainFullyDisabled` — DROP

- `ParsekFlight.cs:8729-8731` — `chainLoopOrDisabled = rec.IsChainRecording && (IsChainLooping(rec.ChainId) || IsChainFullyDisabled(rec.ChainId))`. Flows into `ShouldSpawnAtRecordingEnd` at `:8737` and surfaces via `TrajectoryPlaybackFlags.isChainLoopingOrDisabled` at `:8760`. **DROP** the `IsChainFullyDisabled` OR-branch.
- `ParsekFlight.cs:11201-11203` — same expression inside `CollectNearbySpawnCandidates` (Real Spawn Control window). **DROP** the OR-branch.
- `GhostPlaybackLogic.cs:3136-3138` — same expression inside `ShouldSpawnAtKscEnd`. **DROP** the OR-branch.
- `GhostPlaybackLogic.cs:2968-3027` — `ShouldSpawnAtRecordingEnd` parameter `isChainLoopingOrDisabled` (:2971, :2983), gate at :3024 with reason `"chain looping or fully disabled"`. **RENAME** parameter to `isChainLooping`, update gate reason to `"chain looping"`. No semantics change — `IsChainFullyDisabled` is no longer on any caller path.
- `GhostPlaybackEvents.cs:28` — `public bool isChainLoopingOrDisabled;` on `TrajectoryPlaybackFlags`. **RENAME** to `isChainLooping`.
- `KerbalsModule.cs:50` — `public bool IsDisabledChain;` on `RecordingMeta`. **REMOVE field.**
- `KerbalsModule.cs:128-135` — `bool isDisabled = RecordingStore.IsChainFullyDisabled(chainId);` and the `IsDisabledChain = isDisabled,` assignment inside `PrePass`. **REMOVE** both lines.
- `KerbalsModule.cs:176-177` — `// Skip disabled chain recordings` + `if (meta.IsDisabledChain) return;`. **REMOVE** both lines.
- `KerbalsModule.cs:26` — the comment "Excludes loop and disabled-chain recordings" on the `allRecordingCrew` field. **UPDATE** — only loop recordings are excluded after the fix.
- `TimelineBuilder.cs:139-143` — career-window timeline entry. `if (!rec.PlaybackEnabled) disabledSkipped++;` then gates the `VesselSpawn` timeline entry emission on `rec.PlaybackEnabled`. The career timeline reflects what actually happens; the vessel spawns regardless of the flag, so the entry must appear. **DROP** both the counter increment and the `rec.PlaybackEnabled &&` clause in the if-condition at :143. Remove the corresponding `disabledSkipped` log counter.

### Likely KEEP — user-intent preservation, not career state

- `RecordingOptimizer.cs:55` — `if (!a.PlaybackEnabled || !b.PlaybackEnabled) return false;` inside `CanAutoMerge`. **KEEP.** Auto-merge bails when two consecutive segments differ in any user-set flag, because the merged recording can only carry one value and silently picking one discards the user's setting. Same principle as the adjacent `Hidden`, `LoopPlayback`, and `LoopAnchorVesselId` blockers. This is workflow hygiene, not a career-state gate — merging a disabled segment away would lose the user's visual choice without the fix being able to prevent it.

### Resolved decisions

- `RecordingStore.cs:1372` — inside `IsChainLooping`: `rec.ChainId == chainId && rec.ChainBranch == 0 && rec.PlaybackEnabled && rec.LoopPlayback`. **DROP** the `rec.PlaybackEnabled &&` clause. `IsChainLooping` feeds `ShouldSpawnAtRecordingEnd` in both Flight (`ParsekFlight.cs:8731, :11203`) and KSC (`GhostPlaybackLogic.cs:3138`); keeping the clause preserves the exact invariant violation the fix removes (disabling a solo loop segment would flip the chain from "loop, no spawn" to "spawn at tip" — career state changing via a visual toggle). Post-fix: a chain is looping iff any branch-0 segment has `LoopPlayback=true`, independent of visibility. If a visual-only "is any loop segment actually rendering" query is wanted later, add it as a separately-named helper.
- `RecordingStore.cs:1381-1395` — `IsChainFullyDisabled`. After the four career-state callers (`ParsekFlight.cs:8731`, `:11203`, `GhostPlaybackLogic.cs:3138`, `KerbalsModule.cs:128`) are gone, the function has no production callers. **DELETE** the function and the three `ChainTests.cs` tests at `:1346, :1374, :1379` that only exercise it. If a legitimate UI caller appears later, reintroduce it under an explicitly-visual name (e.g. `IsChainVisuallyHidden`) rather than resurrecting the career-gate identifier.

## Vessel-spawn gate — where and what shape

**There is no explicit `if (rec.PlaybackEnabled) emit VesselSpawn` in code.** The design-doc wording at `docs/dev/done/design-timeline.md:102` is Career-window timeline UI language. In code, the spawn suppression happens **indirectly** via two independent paths:

1. **Flight scene, standalone or non-chain disabled recording:** `ComputePlaybackFlags` sets `skipGhost=true` → the engine early-returns at `GhostPlaybackEngine.cs:291-299` → `HandlePastEndGhost` at `:730` is never reached → `OnPlaybackCompleted` event never fires → `ParsekPlaybackPolicy.HandlePlaybackCompleted` at `ParsekPlaybackPolicy.cs:203` never runs → the spawn branch at `:247` (`if (evt.Flags.needsSpawn && evt.PastEffectiveEnd)`) never runs. Net: vessel silently does not spawn. Shape: *implicit side-effect of the visibility gate.*

2. **Flight scene, chain-disabled recording:** `ComputePlaybackFlags` at `ParsekFlight.cs:8729-8731` computes `chainLoopOrDisabled = true` via the `IsChainFullyDisabled` OR-branch → `ShouldSpawnAtRecordingEnd` at `GhostPlaybackLogic.cs:3024` returns `(false, "chain looping or fully disabled")` → `TrajectoryPlaybackFlags.needsSpawn = false` → policy's spawn branch is a no-op. Shape: *predicate inside a larger conditional, three call sites*.

3. **KSC scene:** `ShouldShowInKSC` at `ParsekKSC.cs:495` returns false → `ParsekKSC.cs:158-171` skips the iteration → `TrySpawnAtRecordingEnd` at `:305/:317` is never reached. Shape: *side-effect of visibility iteration.*

**Fix shape per path — narrow to the `PlaybackEnabled=false` cause only; do NOT broaden to every skip reason.**

Today's `skipGhost` lumps three causes (`!hasData`, `!rec.PlaybackEnabled`, `externalVesselSuppressed`) and today's `!ShouldShowInKSC` lumps three (`!rec.PlaybackEnabled`, non-Kerbin body, `Points.Count < 2`). Only the `PlaybackEnabled=false` cause gets a past-end spawn detour; the structural causes (`!hasData`, non-Kerbin, too-short) must continue to skip silently — they describe recordings with no valid trajectory / no KSC render path, not visibility choices.

1. **Flight, standalone, disabled:** At `GhostPlaybackEngine.cs:291`, split the skipGhost branch. Only the `PlaybackEnabled=false` sub-case fires the past-end completion. The engine already sees the trajectory via `IPlaybackTrajectory.PlaybackEnabled` (`IPlaybackTrajectory.cs:55`); read it directly:
   ```csharp
   if (f.skipGhost)
   {
       if (ghostStates.ContainsKey(i))
       {
           DestroyAllOverlapGhosts(i);
           DestroyGhost(i, traj, f, reason: "disabled/suppressed");
       }
       // Only the PlaybackEnabled=false cause is career-neutral → still fire completion
       // so the policy can run spawn. !hasData and externalVesselSuppressed are
       // structural and must keep their silent-skip behaviour.
       bool pastEnd = ctx.currentUT >= traj.EndUT;
       bool pastEffectiveEnd = ctx.currentUT > f.chainEndUT;
       bool hiddenByPlaybackToggle = !traj.PlaybackEnabled;
       bool hasPointData = traj.Points != null && traj.Points.Count > 0;
       if (hiddenByPlaybackToggle
           && hasPointData
           && (pastEnd || pastEffectiveEnd)
           && !completedEventFired.Contains(i)
           && !earlyDestroyedDebrisCompleted.Contains(i))
       {
           HandlePastEndGhost(i, traj, f, ctx, state: null, ghostActive: false, hasPointData);
       }
       continue;
   }
   ```
   The `hasPointData` guard preserves behaviour for a trajectory that is *both* disabled and has no points — still a no-op. `HandlePastEndGhost` adds the event to `deferredCompletedEvents` at `:747`, which fires through `OnPlaybackCompleted` at `:489` later in the same frame. `ParsekPlaybackPolicy.HandlePlaybackCompleted` at `:247` reads `evt.Flags.needsSpawn` — true after fix (2) removes the chain-disabled suppressor — and spawns the vessel via `host.SpawnVesselOrChainTipFromPolicy` at `:286`. No changes needed in the policy. The policy only dereferences `evt.State` on ghost-active paths, so `state: null` is safe.

   **Alternative considered and rejected:** adding a dedicated `flags.hiddenByPlaybackToggle` field in `ComputePlaybackFlags`. Cleaner in principle, but widens the TrajectoryPlaybackFlags surface and test-seed count. The `traj.PlaybackEnabled` read in the engine is a single-line, locally-scoped branch and stays within the IPlaybackTrajectory contract. Revisit only if a second consumer needs the distinction.

2. **Flight, chain-disabled:** Drop `|| RecordingStore.IsChainFullyDisabled(rec.ChainId)` at `ParsekFlight.cs:8731` and `ParsekFlight.cs:11203`, and drop the KSC mirror at `GhostPlaybackLogic.cs:3138`. Rename the downstream `isChainLoopingOrDisabled` parameter to `isChainLooping` in `ShouldSpawnAtRecordingEnd` (`GhostPlaybackLogic.cs:2971, :2983`) and the `TrajectoryPlaybackFlags` field at `GhostPlaybackEvents.cs:28`. Update the suppression reason string at `:3024-3027` to `"chain looping"` and the doc-comment at `:2967`. Test seed sites (`RewindTimelineTests.cs`, `SpawnSafetyNetTests.cs`, `ChainSpawnSuppressionTests.cs`, `CommittedRecordingImmutabilityTests.cs`, `GhostOnlyRecordingTests.cs`, `PlaybackTrajectoryTests.cs`, `MergeDialog.cs:177`) must rename the named argument; this is mechanical.

3. **KSC, disabled:** `ShouldShowInKSC` at `ParsekKSC.cs:493-500` folds three causes together (`!rec.PlaybackEnabled` at `:495`, non-Kerbin at `:498`, `Points.Count < 2` at `:496`). Only the first is a career-neutral visibility choice; the other two describe recordings with no KSC render path at all and have never had a KSC spawn side-effect. The fix must NOT call `TrySpawnAtRecordingEnd` for the non-Kerbin or too-short cases — `ShouldSpawnAtKscEnd` at `GhostPlaybackLogic.cs:3122` never re-checks those conditions, so unguarded insertion would start spawning vessels for recordings that were hidden for structural reasons.

   Factor `ShouldShowInKSC` into a two-predicate shape:
   ```csharp
   internal static bool IsKscStructurallyEligible(Recording rec)
   {
       if (rec.Points == null || rec.Points.Count < 2) return false;
       if (rec.Points[0].bodyName != "Kerbin") return false;
       return true;
   }
   internal static bool ShouldShowInKSC(Recording rec)
       => IsKscStructurallyEligible(rec) && rec.PlaybackEnabled;
   ```
   Then at `ParsekKSC.cs:158-172`, handle the three outcomes explicitly:
   ```csharp
   if (!IsKscStructurallyEligible(rec))
   {
       // Non-Kerbin or insufficient data — not renderable, no KSC spawn path.
       // Today's cleanup-only behaviour.
       if (kscGhosts.ContainsKey(i)) { DestroyKscGhost(kscGhosts[i], i); kscGhosts.Remove(i); loggedGhostSpawn.Remove(i); }
       DestroyAllKscOverlapGhosts(i);
       continue;
   }
   if (!rec.PlaybackEnabled)
   {
       // Hidden visually only — career effect still applies. Clean up and,
       // if past-end, fire the same spawn path the visible branch would.
       if (kscGhosts.ContainsKey(i)) { DestroyKscGhost(kscGhosts[i], i); kscGhosts.Remove(i); loggedGhostSpawn.Remove(i); }
       DestroyAllKscOverlapGhosts(i);
       if (currentUT > rec.EndUT)
           TrySpawnAtRecordingEnd(i, rec);
       continue;
   }
   ```
   `TrySpawnAtRecordingEnd` at `:802` already dedups per `RecordingId` via `kscSpawnAttempted.Add` at `:814`, and `ShouldSpawnAtKscEnd` at `GhostPlaybackLogic.cs:3122` returns `needsSpawn=false` if `currentUT < rec.EndUT`, so the `currentUT > rec.EndUT` guard is belt-and-braces.

   **External callers of `ShouldShowInKSC`**: the existing callers at `ParsekKSC.cs:88` (ctor ghost count log) and the test at `KscSpawnTests.cs:338-344` keep the combined predicate semantics — they want "is this recording eligible AND enabled?", which is exactly what `ShouldShowInKSC` still returns post-factoring.

## `KerbalsModule.cs:176-177` — why the early return is there and why it goes

Git blame: `77f5bf379` (Vlad Ciobanu, 2026-04-04) "T42: convert KerbalsModule to IResourceModule". The commit message notes "PrePass reads RecordingStore for mutable recording metadata (loop/chain/disabled status)". The `IsDisabledChain` branch was carried over from the prior static-bridge implementation as a presumed symmetry with the `IsLoop` skip at `:174` — both treated as reasons to not reserve crew. The loop skip at `:174` has a separate, still-valid rationale (looping chains have no real "end" for the crew to return from, so crew stays reserved at `PositiveInfinity` elsewhere via the `chainHasLoop` branch at `:202-207`). The disabled-chain skip, by contrast, conflates visibility with career state and violates the deterministic-timeline principle: the kerbals were committed to that mission, the ledger's `KerbalAssignment` action is present, and the crew should be reserved regardless of whether anyone ever replays the ghost.

Dropping lines 176-177 makes `ProcessAction` behaviour uniform: reservations follow `KerbalAssignment` actions on the committed ledger, period.

**Knock-on cleanup:** lines 128-135 stop computing `IsDisabledChain`, and line 50 removes the field. Line 26 comment updates to remove "disabled-chain".

## `IsChainFullyDisabled` audit — per-caller disposition

| Call site | Today | Disposition | Reason |
|---|---|---|---|
| `ParsekFlight.cs:8731` | OR-clause in `chainLoopOrDisabled` | **DROP OR-branch** | Gates spawn — career state |
| `ParsekFlight.cs:11203` | OR-clause in `isChainLoopingOrDisabled` | **DROP OR-branch** | Gates spawn via RSC window — career state |
| `GhostPlaybackLogic.cs:3138` | OR-clause in KSC spawn | **DROP OR-branch** | Gates spawn at KSC — career state |
| `KerbalsModule.cs:128` | `PrePass` reads into `meta.IsDisabledChain` | **DROP** | Feeds only the `:177` career-state gate |
| `RecordingStore.cs:1381-1395` | function body | **KEEP FUNCTION, no callers** | Dead for production, retained for potential UI use (see Ambiguous section) |
| `ChainTests.cs:1346, 1374, 1379` | unit tests | **KEEP** | Pure predicate coverage |

Partial-disable chains (some segments enabled, some disabled) are already handled correctly because each recording's crew is enumerated individually by `ExtractRawCrewFromRecording` at `KerbalsModule.cs:139` and each `KerbalAssignment` action is processed individually. The `allRecordingCrew` set already aggregates per-recording. The `:177` early return is specifically the all-off special case; removing it makes behavior uniform for 0%/50%/100%-disabled chains.

## Interaction with #432 and #431

- **#431** has merged to `main` via PR #332 (2026-04-17). Its `GameStateRecorder.Emit` funnel + `recordingId` tagging work is orthogonal to `PlaybackEnabled`. No coordination.
- **#432** (`Parsek-432-gloops-no-events` worktree, open) touches `GameStateRecorder.cs`, `GameActions/LedgerOrchestrator.cs`, `RecalculationEngine.cs`, and adds a `GetActiveRecordingGhostOnlyFlag` accessor on `ParsekFlight`. The `IsGhostOnly` flag and `PlaybackEnabled` flag are orthogonal: `IsGhostOnly` suppresses event capture and ledger application entirely (Gloops never produced career effects in the first place); `PlaybackEnabled` only ever gated visibility + the accidental career leaks fixed here. Surface areas do not overlap.
- **#434** (revert auto-discard) depends on #431 and is unrelated to `PlaybackEnabled`.

## Test matrix

New file: `Source/Parsek.Tests/PlaybackEnabledScopeTests.cs`, `[Collection("Sequential")]`, `IDisposable` for `ParsekLog.TestSinkForTesting` + `RecordingStore.ResetForTesting` + `MilestoneStore.ResetForTesting()` in dispose. One test per dropped gate + regression guards per existing-correct gate. Log-line assertions use the repo's standard pattern (see `RewindLoggingTests.cs`).

1. `DisabledStandalone_PastEnd_FiresPlaybackCompletedWithNeedsSpawn` — seed a single disabled recording with a valid snapshot, advance engine past `rec.EndUT`, assert `OnPlaybackCompleted` event fires, `evt.Flags.needsSpawn=true`, `evt.GhostWasActive=false`. Log-assert: `[Engine]` line about firing completion with `ghostActive=false`.
1a. `NoRenderableData_PastEnd_DoesNotFirePlaybackCompleted` — regression guard for the narrowed skipGhost branch: `traj.PlaybackEnabled=true` but `!HasRenderableGhostData(traj)`, advance past end, assert no `OnPlaybackCompleted` fires and no spawn attempt.
1b. `ExternalVesselSuppressed_PastEnd_DoesNotFirePlaybackCompleted` — same shape, `externalVesselSuppressed=true` in flags, assert the silent-skip behaviour is preserved.
2. `DisabledStandalone_PolicyHandlesCompletion_SpawnCalled` — wire the policy to the engine (existing test helper), past-end a disabled recording, assert `host.SpawnVesselOrChainTipFromPolicy` was called via a test double.
3. `DisabledChain_AllOff_ShouldSpawnAtRecordingEnd_ReturnsTrue` — replaces today's `KscSpawnTests.ShouldSpawnAtKscEnd_ChainFullyDisabled_ReturnsFalse`. With `isChainLooping=false` (fully-disabled is no longer a special case), assert `ShouldSpawnAtRecordingEnd` returns `needsSpawn=true` for the chain tip.
4. `DisabledChain_CrewReservationsStillApply` — invert `KerbalReservationTests.Recalculate_SkipsDisabledChains` (`:194-207`). After fix, `IsKerbalAvailable("Jeb")` returns **false** (crew is reserved). Log-assert: `[KerbalsModule]` reservation line for the disabled-chain recording.
5. `PartiallyDisabledChain_EnabledSegmentsReserveTheirCrew` — regression guard. 3-segment chain, middle segment disabled, assert crew for all 3 segments is reserved.
6. `DisabledRecording_LedgerActionsStillApplied` — codify today's correct behaviour. Build a recording with `KerbalAssignment`, `FundsDelta`, `ContractAccepted` actions, set `PlaybackEnabled=false`, run `RecalculationEngine`, assert every action remains effective on the ledger.
7. `DisabledRecording_ResourceBudgetStillSubtracts` — exists in shape at `ResourceBudgetTests.cs:813-824`; add a log-assert for the cost aggregation line to pin the invariant.
8. `DisabledChain_ResourceBudgetStillSubtracts` — exists at `ResourceBudgetTests.cs:826-845`; add log-assert.
9. `KscScene_DisabledRecording_ShouldShowInKSC_False` — regression, existing.
9a. `KscScene_ShouldShowInKSC_FactoringEquivalence` — for every combination of `(PlaybackEnabled, Points.Count>=2, bodyName=="Kerbin")`, assert `ShouldShowInKSC(r) == IsKscStructurallyEligible(r) && r.PlaybackEnabled`.
10. `KscScene_DisabledRecording_PastEnd_TrySpawnAtRecordingEndReached` — new. Simulate KSC `Update()` with a past-end disabled but structurally-eligible recording; assert the spawn path fires (observable via `kscSpawnAttempted` set membership + log line).
10a. `KscScene_NonKerbinRecording_PastEnd_DoesNotSpawn` — regression guard for the narrowed KSC branch: past-end, `PlaybackEnabled=true`, body="Mun"; assert `TrySpawnAtRecordingEnd` is NOT reached (no `kscSpawnAttempted` entry, no spawn log).
10b. `KscScene_TooFewPointsRecording_PastEnd_DoesNotSpawn` — same shape, `Points.Count=1`; assert no spawn.
11. `FlightScene_DisabledRecording_SkipGhostStillDestroysVisual` — regression. Past-end + pre-end cases, `ghostStates` for the disabled index is empty after the frame.
12. `TimelineBuilder_DisabledRecording_StillEmitsVesselSpawnEntry` — build a `committedRecordings` list containing a disabled recording with a spawnable terminal state, run the Recording Collector, assert `TimelineEntryType.VesselSpawn` appears for the recording at `rec.EndUT`.
13. `IsChainLooping_DisabledLoopSegment_ReturnsTrue` — invariant test. Chain with a single branch-0 segment, `LoopPlayback=true`, `PlaybackEnabled=false`; assert `IsChainLooping` returns true. Paired negative: `ShouldSpawnAtRecordingEnd` for that chain's tip returns `(false, "chain looping")` — no silent career flip when the player disables the loop visual.
14. `RecordingOptimizer_CanAutoMerge_DisabledSegment_ReturnsFalse` — regression guard that the `PlaybackEnabled` blocker in `CanAutoMerge` is still enforced post-refactor.
15. `IsChainFullyDisabled_Deleted` — sanity: production code compiles without the symbol. Ancillary to the deletion; handled by the build + targeted grep at commit time (no dedicated test needed; the three `ChainTests.cs` tests at `:1346, :1374, :1379` are removed in the same commit).

## Docs to update in the implementation commits

- `Source/Parsek/Recording.cs:116` — replace comment with `"false = hide ghost during playback; does not affect ledger actions, vessel spawn, crew reservations, or resource budget"`.
- `Source/Parsek/KerbalsModule.cs:26` — remove "disabled-chain" from the `allRecordingCrew` summary comment.
- `Source/Parsek/GhostPlaybackLogic.cs:2967` — update `<param name="isChainLooping">` doc to describe the renamed flag.
- `docs/dev/done/design-timeline.md:102` — remove the `if (rec.PlaybackEnabled)` qualifier on `VesselSpawn` emission; the Recording Collector emits `VesselSpawn` for every non-mid-chain recording with a spawnable terminal state regardless of the flag.
- `docs/user-guide.md` — Recordings Manager section, leftmost enable checkbox description: "The enable checkbox hides the ghost visual — nothing else. Resources, contracts, crew, and the final vessel still follow the committed mission." Include a paragraph warning players who used the checkbox as a de-facto "skip this recording" (see Edge cases below).
- `CHANGELOG.md` — one line under the current version: `Fixed: disabling a recording's ghost no longer silently drops its vessel spawn, crew reservations, or career effects — the checkbox is purely visual.` Place under the same release as #431 / #432 / #434.
- `docs/dev/todo-and-known-bugs.md:106-155` — strike the entry with a status note listing the dropped gates, renamed parameter, and the `IsChainLooping` disposition that shipped.

## Edge cases

- **Player relying on `PlaybackEnabled=false` as a de-facto "skip this recording"** — contradicts the invariant. Post-fix, a player who disabled a recording to avoid its resource cost will find the cost reappears. User-guide note (above) plus CHANGELOG line head this off; the TODO's Out-of-Scope correctly forbids retroactive reconciliation of saves, so no migration is needed. Document this as expected behaviour: if a player wants to exclude a recording's career effects, the answer is Delete (post-commit) or Discard (pre-commit), not the enable checkbox.
- **Merge-dialog per-recording persist-vs-ghost-only decision** — nullifies `VesselSnapshot` at commit time (`MergeDialog.cs` → `RecordingStore.CommitPendingTree` path). Independent of `PlaybackEnabled` — `ShouldSpawnAtRecordingEnd` at `GhostPlaybackLogic.cs:2987` returns `(false, "no vessel snapshot")` before any `PlaybackEnabled`-adjacent check. No interaction.
- **PID dedup at spawn site** — `ShouldSpawnAtRecordingEnd` at `:3097-3100` returns false if `rec.SpawnedVesselPersistentId != 0`. A disabled recording that re-enters past-end after a previous spawn (e.g. player toggled the checkbox repeatedly across scene loads) will be correctly no-op'd by the PID dedup.
- **Breakup chain (`isDebris=true`)** — `ShouldSpawnAtRecordingEnd` gates debris at `:3061-3064`; orthogonal to `PlaybackEnabled`.
- **Destroyed terminal state** — `TerminalState.Destroyed` is already gated at `:3068-3077`; post-fix, a disabled recording with destroyed terminal still does not spawn.

## Out of scope (reaffirmed from TODO)

- A separate "skip career effects" toggle. Violates the deterministic-timeline principle; the correct player affordances are Delete (post-commit) and Discard (pre-commit).
- Retroactive reconciliation of saves where a disabled recording's vessel "should have" spawned but didn't. Player can toggle back on to trigger the next past-end spawn cycle, or rewind through the recording.
- Removing `IsChainFullyDisabled` entirely (covered in Ambiguous section — user decides).
- Any work on `LoopPlayback`, `LoopStartUT`, `LoopEndUT`, `LoopAnchorVesselId`, `LoopIntervalSeconds` semantics — those are separate features with their own rules.

## Risks

- **Renaming `isChainLoopingOrDisabled` → `isChainLooping` touches ~15 call sites across production and tests.** Mechanical but wide; a `dotnet build` must pass before commit. No API breakage (all `internal`).
- **Narrowing the skipGhost and `!ShouldShowInKSC` branches to the `PlaybackEnabled=false` cause only** is the core behavioral work. The temptation when removing the bug is to fire spawn for every skipped recording — resist it. The other causes (`!hasData`, `externalVesselSuppressed`, non-Kerbin at KSC, `Points.Count < 2`) describe recordings with no valid trajectory / no KSC render path and must not acquire a spawn side-effect.
- **`HandlePastEndGhost` with `state: null`** — confirmed safe: the policy only dereferences `evt.State` on ghost-active paths and guards otherwise. No additional null-checking needed.
- **KSC spawn timing** — `TrySpawnAtRecordingEnd` already dedups via `kscSpawnAttempted` at `:814`; the per-frame cost of hitting the new spawn detour for a disabled recording is O(1).
- **`ShouldShowInKSC` factoring** — the extracted `IsKscStructurallyEligible` is new public-to-file surface. Keep it `internal static` to match `ShouldShowInKSC` and add a unit test verifying the decomposition (`ShouldShowInKSC(r) == IsKscStructurallyEligible(r) && r.PlaybackEnabled` holds for all truth-table inputs).

## Commits

Plan to sequence as three commits on `fix/433-playback-enabled-visual-only`:

1. `fix(#433): drop IsChainFullyDisabled gates from spawn and crew reservation` — OR-branch drops + parameter rename + `KerbalsModule` cleanup + tests (#3, #4, #5).
2. `fix(#433): fire PlaybackCompleted for past-end disabled ghosts` — `GhostPlaybackEngine` skipGhost split + KSC spawn insertion + tests (#1, #2, #10, #11).
3. `fix(#433): timeline entry + doc updates` — `TimelineBuilder` drop, comment / design-doc / user-guide / CHANGELOG / todo updates, test #12.

Each commit builds + tests clean. Each commit updates `CHANGELOG` and `todo-and-known-bugs.md` per the per-commit-docs rule in `.claude/CLAUDE.md`.
