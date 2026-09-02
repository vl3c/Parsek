# Cooperative Async Multiplayer - Phase and Task Breakdown

*Master task list for implementing `docs/dev/design-coop-async-multiplayer.md` (design v3). Each task below becomes one `coop-async-multiplayer-task-N-<component>.md` plan, written by a clean-context Plan agent at dispatch time per `docs/dev/development-workflow.md` step 4a, and one TaskCreate entry for the orchestrator.*

**Status:** PLANNED (2026-09-01, revised 2026-09-02 for design v4: full control of foreign vessels, schema generation 5, attribution gate, integration-branch workflow). No implementation started.
**Design authority:** `docs/dev/design-coop-async-multiplayer.md` (section numbers below refer to it).
**Companion artifacts:** `coop-async-multiplayer-inventory.md` (verified mechanics, baseline at kickoff), `coop-async-multiplayer-deferred.md`, and `coop-async-multiplayer-prerefactor.md` (tiered pre-implementation refactors A1-A6 / B1-B3 / C1-C7, each landing before the task it unblocks; the corrections it lists have been applied to the tasks below).

**Branching (maintainer decision 2026-09-02):** all multiplayer work lives on
a long-lived integration branch `coop-multiplayer` cut from `main`. Every
task is implemented on a sub-branch (`coop-multiplayer/<task-id>-<slug>`,
e.g. `coop-multiplayer/m2.4-packet-codec`) and lands via a PR targeting
`coop-multiplayer`, reviewed per the workflow. `main` receives the feature
only when the whole thing is good to merge (one final PR, `coop-multiplayer`
-> `main`). `coop-multiplayer` rebases or merges `main` forward at phase
boundaries so it never drifts far. CI runs on every sub-branch PR because the
Actions workflow triggers on all PRs.

---

## 1. Principles for this breakdown

- **One invariant per task.** If a task needs "and" to describe it, it is split.
- **Pure cores first, thin shells second.** Every decision-bearing piece (codec validation, fence predicate, fold, classifier, reconciliation) is an `internal static` pure core with headless tests before any KSP-facing shell calls it. This is the house pattern and the only way the fold's determinism claim is testable.
- **Each phase is playable.** M1 is inert without a campaign; M2 alone shows friends' missions; M3 pools money; M4 adds interaction; M5 crew; M6 polish. A phase that is not independently playable is mis-cut.
- **Every task lists its done condition** in terms of named tests plus (where applicable) an in-game check; no task is done with a failing or missing test.
- **Serialization tasks get the full cycle** (plan -> plan review -> implement -> review -> fix); pure-core tasks with an obvious pattern may shortcut to implement -> review.

Sizes: S (one agent session, 1-2 files), M (one session, 2-4 files), L (may need a split at plan time; flagged).

---

## 2. Phase 0 - Kickoff (orchestrator, no agents)

| Step | Action | Done |
|------|--------|------|
| 0.1 | Create the integration branch `coop-multiplayer` from `origin/main` and push it; create the dedicated sibling worktree `../Parsek-coop-multiplayer` on it per `.claude/CLAUDE.md` (the session worktree under `.claude/worktrees/` is too deep for the csproj KSP probe and the harness umbrella walk). Task sub-branches are cut from `coop-multiplayer`, never from `main` | branch pushed, worktree exists, `dotnet build` clean |
| 0.2 | Record the baseline in `coop-async-multiplayer-inventory.md`: `dotnet test` count, branch, base commit | inventory header filled |
| 0.3 | (resolved 2026-09-02) Schema generation bumps to 5 in task M2.1, with fixture re-stamping in the same task | - |
| 0.4 | Dispatch two Explore agents to refresh the inventory's line references against the kickoff commit (the seeded references date from 2026-09-01/02) | inventory `Status` column initialized |
| 0.5 | Land the Tier A pre-refactors (`coop-async-multiplayer-prerefactor.md` A1-A6) on `coop-multiplayer` before any M2 task; Tier B before M3; Tier C before M4. A4 and C3 are behavior changes and get their own PRs + CHANGELOG lines | prerefactor doc statuses flipped per tier |

---

## 3. Phase M1 - Identity and campaign store (ships inert)

Goal: a player can name themselves, point at a folder, create or join a campaign. No exchange yet. Everything is behind the campaign link; solo saves are byte-identical in behavior.

| Task | Title | Scope | Files (create / modify) | Tests (what makes each fail) | Depends | Size |
|------|-------|-------|--------------------------|------------------------------|---------|------|
| M1.1 | Player identity + campaign link persistence | `PlayerIdentity` (6.1), PluginData identity cache, campaign-link node in `ParsekScenario` via the `SaveStagingList`/`LoadStagingList` pattern incl. the `RemoveNodes` registration | create `Multiplayer/PlayerIdentity.cs`; modify `ParsekScenario.cs` (OnSave/OnLoad) | `PlayerIdentityRoundTrip` (id stable across load; name editable); `CampaignLinkNodeRoundTrip` (missing node = unlinked; stale node removed on save) | - | S |
| M1.2 | Exchange timestamp + ordering-key core | 6.5: fixed-format UTC timestamps, parse-then-compare, arbitration key `(ticks, ownerPlayerId)`, ledger tiebreak key; refusal on unparseable | create `Multiplayer/ExchangeKeys.cs` | `TimestampOrderingParseThenCompare`; `ArbitrationKeyTotalOrder` (shuffled inputs, one order) | - | S |
| M1.3 | Manifest codecs | `CampaignManifest`, `CheckpointManifest` (highwater), `PlayerManifest` ConfigNode round-trips; safe-write with retry/backoff for shared-folder writes (6.3) | create `Multiplayer/CampaignCodec.cs`; modify `FileIOUtils.cs` (retry wrapper only) | `ManifestRoundTrips`; `SharedWriteRetriesOnShareViolation` (fake IO throws twice, third succeeds; fails if no retry) | M1.2 | S |
| M1.4 | Campaign name slug + path headroom validation | `saveSlug` derivation (reserved names, trailing dots/spaces, case-only collisions, `#N` uniquify), worst-case protocol path length check against the shared root | create `Multiplayer/CampaignPaths.cs` | `CampaignNameSlug`; `PathHeadroomRefusesNearMaxPath` | - | S |
| M1.5 | Campaign creation flow | 7.2 preconditions (journal/marker/provisional checks), ownership stamping where null + `Exported=true`, forced save, checkpoint 0 copy with strip list, highwater, manifest LAST, player.cfg | create `Multiplayer/CampaignStore.cs`; modify `Recording.cs`, `RecordingTree.cs`, `GameAction.cs` (owner + Exported fields: coordinate with M2.1, see note) | `CreateRefusesPreconditions` (each precondition individually); `CreateStampsOnlyNullOwners`; `CreateWritesManifestLast` (crash injected after copy leaves no manifest); `SnapshotStripListApplied` | M1.1, M1.3, M1.4, M2.1 | M |
| M1.6 | Join / rejoin flow | 7.3: manifest gate, existing-subtree detection, clone latest checkpoint into `saves/<slug>/`, ContractSystem node + Applicant strip, `lastSeq` seeding from max(player.cfg, own packet scan), player.cfg write; the full-merge call is a stub until M2 | modify `Multiplayer/CampaignStore.cs`; create `Multiplayer/SaveCloneRegenerator.cs` | `JoinRefusesGenerationMismatch`; `JoinRegeneratesContractsAndApplicants` (cloned .sfs lacks the ContractSystem node and Applicant rows; crew rows intact); `RejoinResumesSeq`; `JoinNeverTouchesExistingSave` | M1.5 | M |
| M1.7 | Settings section | 7.1: name field (first edit mints id), folder path + Validate (existence, writability, campaign presence, headroom, placeholder attributes), Create/Join/Leave/Sync/Checkpoint buttons, inline status text (members + clocks, balance/debt, pending counts, missing parts), relink campaignId check | modify `UI/SettingsWindowUI.cs`; create `Multiplayer/SettingsStatusText.cs` (pure formatter) | `SettingsStatusTextFormats` (pure); `RelinkRefusesDifferentCampaign` | M1.5, M1.6 | M |

Note on M1.5 / M2.1 ordering: the owner/Exported fields are data-model work owned by M2.1; M1.5 is dispatched after M2.1 lands (M2.1 has no behavioral dependency on M1 and can go first).

**Phase done:** a founder creates a campaign in a temp folder and a second local save joins it (in-game test `CampaignCreateJoinRoundTrip` minus the merge assertions, which arrive in M2).

---

## 4. Phase M2 - Recordings as read-only citizens

Goal: peers' missions appear in your save (timeline, Recordings Manager, ghosts, spawns) attributed and fenced. No economy merge, no interaction claims.

| Task | Title | Scope | Files | Tests | Depends | Size |
|------|-------|-------|-------|-------|---------|------|
| M2.1 | Owner / Exported / orphan / foreign-link fields + codecs + generation 5 | 6.2: `OwnerPlayerId` on Recording/Tree/GameAction, `Exported`, `OrphanedByRetiredRecordingId`, `ContinuesForeignRecordingId`, `MissingParts` (runtime-only); ConfigNode round-trips, null-defaulted; `CurrentRecordingSchemaGeneration` 4 -> 5 (section 11) with the committed fixture saves re-stamped (`Fixtures/C1Career`, `Fixtures/C2CareerPostFix`, `harness/fixtures/saves/**`; rerun `harness/tools/build_career_earned_pad.py` so its byte-identity cell stays green) | modify `Recording.cs`, `RecordingTree.cs`, `GameAction.cs`, `RecordingTreeRecordCodec.cs`, `GameActions/Ledger.cs` (action codec), `RecordingStore.cs` (constant); fixture saves | `OwnerFieldsRoundTripAndDefaultNull` (a generation-5 save with no campaign loads with all null); `ExportedFlagPersists`; `Generation4RecordingRejected` (existing gate reasons, new constant) | - | M |
| M2.2 | Ownership fence core + chokepoint guards | 6.4 predicate (`OwnershipFence.CanMutate(owner, surface)`) added as the second rule of the `RecordingMutationGate` from pre-refactor A2 (loop config + every Recordings-table control inherits it), plus guards at the ~11 existing chokepoints: 5 `RecordingStore` group wrappers, `MissionGroupLink.RenameMissionGroup`, `RewindInvoker.CanInvoke`, `SupersedeCommit.CommitSupersede`, `UnfinishedFlightSealHandler.TrySeal` / `UnfinishedFlightStashHandler.TryStash`, `TreeDiscardPurge.PurgeTree`, `ParsekFlight.TryFindCommittedTreeForSpawnedVessel` (a foreign filter here makes "never restores a foreign tree" structural); one-line decisions for the mission-level loop writes and the three internal policy clears; `[OwnershipFence]` Warn on block. (No "recording rename" surface exists.) | create `Multiplayer/OwnershipFence.cs`; modify the chokepoint files above | `OwnershipFenceMatrix`; `FenceBlockAlwaysWarns` (log assertion); per-chokepoint guard tests | M2.1, A2 | M |
| M2.3 | Optimizer freeze | 6.4: one `RecordingOptimizer.IsOptimizationFrozen(rec, out reason)` (Exported or foreign) consulted at FOUR sites: `CanAutoMerge` beside the supersede guard, the loop head of `FindSplitCandidatesForOptimizer` AND its discovery-time `EnsureCheckpointSectionsForTopLevelOrbitSegments(markDirty: true)` call, and the loop head of `TrimBoringTailsForOptimization` (both of the latter rewrite exported bytes). Note `CanAutoSplit` is dead on the optimizer path; the live predicate is `CanAutoSplitIgnoringGhostTriggers` | modify `RecordingStore.Optimization.cs`, `RecordingOptimizer.cs` | `ExportedFreezeIncludesSnapshotHistory`; `OptimizerSkipsForeignAtAllFourSites` | M2.1 | S |
| M2.4 | Packet envelope + payload codec | 6.1 `PacketEnvelope` (all blocks incl. EVENTS/CREW/TIP_CLAIMS/RETIREMENTS placeholders), PAYLOAD hash list, completeness protocol (payload dir first, envelope last), chunked time-budgeted hash verification, id safety via `ValidateRecordingId`, owner consistency, seed stripping | create `Multiplayer/PacketCodec.cs`, `Multiplayer/PacketHasher.cs` | `PacketEnvelopeRoundTrip`; `PayloadWithoutEnvelopeIgnored`; `HashMismatchRefuses`; `ChunkedHashResumesAcrossTicks`; `SeedRowStrippedWithWarn` | M1.2, M2.1 | M |
| M2.5 | Exporter: commit trigger + queue | 7.4 triggers 1 (commit) and 2 (retirement placeholder), seq allocation, payload-then-envelope write, player.cfg update, retry queue with settings-visible pending count, `Exported=true` stamping | create `Multiplayer/PacketExporter.cs`; modify `ParsekFlight.cs` (commit hook), `MergeDialog.Commit.cs` | `ExportWritesEnvelopeLast`; `ExportQueueRetriesWhenFolderOffline`; `ExportStampsExported` | M2.4, M1.5 | M |
| M2.6 | Importer core + journal | 7.5 steps 1-3 and 8: scan, validate (read-only probes `TryProbeTrajectorySidecar` / `TrajectorySidecarBinary.TryValidatePayload` / `TryLoadSnapshotSidecar` in the packet dir; never `AreRecordingFilesCurrentAtPaths`), per-player seq order, seq-gap halt above checkpoint, duplicate-seq refusal, sidecar placement via `SidecarFileCommitBatch.StageWrite(path => File.Copy(...))`, registration through pre-refactor A1's `RegisterCommittedTreeCore(flush: off, baseline: off, milestone: off)` preceded by the owner-name-honoring auto-group and followed by `MarkSupersededTerminalSpawnsForContinuedSources` (registration BEFORE any orphan sweep can run), tree-fragment replacement in seq order (6.6), journal + pending set, apply actions (owner-tagged), raise `CommittedSetChanged` (A5) | create `Multiplayer/PacketImporter.cs`, `Multiplayer/ImportJournal.cs` | `ImportJournalIdempotence`; `SeqGapHaltsPlayer`; `DuplicateSeqRefusesBothKeepsApplied`; `TreeFragmentReconciliation`; `RefusedPacketLeavesNoTrace`; `AppendAtEndPreservesIndices` (engine index map unchanged for pre-existing entries); `ImportNeverRewritesForeignSidecars` | M2.4, A1, A3, A5 | L (split: scan/validate/journal vs registration) |
| M2.7 | Citizenship wiring | `MarkReplayScope` (A5) for imported recordings, `EvaluateAndApplyGhostChains` subscribed to `CommittedSetChanged` (A5), `MissingParts` derivation against `PartLoader` (dot-form), spawn-gate early reject for `MissingParts` as a NEW coded early return (C2 shape; never a reorder of the existing ladder), foreign group naming (owner's `AutoGeneratedRootGroupName` honored, display uniquify), read-only mission seeding | modify `ParsekFlight.cs`, `GhostPlaybackLogic.cs`, `RecordingGroupStore.cs`, `MissionStore.cs` | `ImportedPastRecordingPlaysAndSpawns`; `MissingPartsImportDegrades`; `ForeignGroupNameCollisionUniquifiesDisplayOnly` | M2.6, A5 | M |
| M2.8 | MilestoneStore registration from EVENTS | exporter builds the per-tree game-state event bundle; importer registers `Milestone` entries so `GetCommittedTechIds` / `GetCommittedKerbalHireNames` include foreign items | modify `Multiplayer/PacketExporter.cs`, `Multiplayer/PacketImporter.cs`, `MilestoneStore.cs` | `MilestoneStoreRegistrationFromEvents` | M2.5, M2.6 | S |
| M2.9 | Merge cadence + notifications | 7.5 entry points: OnLoad (after sweeps), scene change into KSC/TS, 90 s poll coroutine in KSC/TS/Map, Sync now; batched ScreenMessage per merge; `[PacketImport]` summary line; world-affecting deferral while a vessel is loaded | modify `ParsekScenario.cs`, `Multiplayer/PacketImporter.cs`; create `Multiplayer/MergeNotifier.cs` (pure batching) | `MergeSummaryLineAlwaysEmitted` (log assertion); `PollSkipsWhenNoNewEnvelopes`; `NotifierBatchesOnePerMerge` | M2.6 | M |
| M2.10 | Attribution (gated) | `ShowOwnerAttribution` pure predicate (campaign linked AND >= 2 distinct registered player ids; sticky once true); timeline owner names + per-player filter; Recordings Manager owner badge + disabled mutation controls; Missions tab read-only foreign trees; nothing renders for solo saves or one-member campaigns (7.12) | create `Multiplayer/OwnerAttribution.cs`; modify `Timeline/TimelineBuilder.cs`, `UI/TimelineWindowUI.cs`, `UI/RecordingsTableUI.cs`, `UI/MissionsWindowUI.cs` | `ShowOwnerAttributionPredicate`; `TimelineEntriesCarryOwnerNameOnlyWhenGated`; `PlayerFilterHidesOthers` | M2.1 | M |
| M2.11 | Two-player fixture v0 + integration | `Generators/` fixture on A6's `MachineContext` (sequential two-machine simulation) and `DerivedCareerState` (sorted canonical serialization): founder save + two packet sets (no claims yet), scale-parameterized recording count; integration tests `JoinBootstrapEqualsIncumbentState` (M2 subset), `RefusedPacketLeavesNoTrace`; first scale measurement rows for section 12 | create `Source/Parsek.Tests/Generators/CampaignFixtureBuilder.cs`, `Source/Parsek.Tests/Multiplayer/*Tests.cs` | M2.6, M2.7, A6 | M |

**Phase done:** in-game, a second save sees the founder's missions as attributed ghosts and spawns; `dotnet test` green; section 12 budgets measured at 200 and 800 recordings and recorded in the inventory.

---

## 5. Phase M3 - The pooled economy

| Task | Title | Scope | Files | Tests | Depends | Size |
|------|-------|-------|-------|-------|---------|------|
| M3.1 | Deterministic sort + ordering freeze | `SortActions` tiebreak (OwnerPlayerId, ActionId); `LedgerRolloutAdoption` / `LedgerRecoveryFundsPairing` refuse or pre-export re-sequencing of Exported rows | modify `GameActions/RecalculationEngine.cs`, `GameActions/LedgerRolloutAdoption.cs`, `GameActions/LedgerRecoveryFundsPairing.cs` | `SortActionsDeterministicAcrossConcatenationOrder`; `ExportedActionOrderingFieldsFrozen` | M2.1 | S |
| M3.2 | Action flush export + import | 7.4 trigger 3 (ACTIONS-only packets, MISSION_NAMES rename carry); importer applies owner-tagged actions, strips seeds | modify `Multiplayer/PacketExporter.cs`, `Multiplayer/PacketImporter.cs` | `ActionFlushPacket`; `SeedExclusion`; `MissionRenameRidesFlush` | M2.5, M2.6 | S |
| M3.3 | Once-ever same-target spend dedup | consumers of pre-refactor B2's per-walk `OnceEverKeySet` in Funds (4 sites) / Science / Facilities / Strategies / Kerbals (hire by name): first effective and charged, duplicates uncharged + ineffective; the charge module and the effect module ask the same key and get the same verdict | modify `GameActions/FundsModule.cs`, `ScienceModule.cs`, `FacilitiesModule.cs`, `StrategiesModule.cs`, `KerbalsModule.cs` | `OnceEverSpendDedup` (incl. the solo duplicate double-charge regression); `ProjectionWalkDoesNotInheritClaims` | B2 | M |
| M3.4 | Campaign walk mandate + reconcile preservation + authoritative merges | two clauses in pre-refactor B1's `RecalcPolicy.Resolve` (force `AtUT` at current UT when linked; OR the merge signal into `AuthoritativeReduction`); `Ledger.Reconcile` preservation = one disjunct at the single local in `OnKspLoad` (`campaignLinked || ...`), asserted for BOTH Reconcile calls (the migration path re-reconciles) | modify `GameActions/LedgerOrchestrator.cs`, `ParsekScenario.cs` | `CampaignReconcilePreservesFuture` (both calls); `CampaignRecalcAlwaysCutoff`; `MergePatchIsAuthoritative` (DrawdownGuard does not floor) | M2.6, B1 | S |
| M3.5 | Baseline pinning | campaign-linked patching selects the checkpoint baseline; post-link local baselines unused for merged facets | modify `GameActions/KspStatePatcher.cs`, `GameStateStore.cs` | `BaselinePinning` | M1.5 | S |
| M3.6 | Debt + credit surfacing | debt warning naming colliding spends, credit-shift notification, Settings balance text, timeline NOW-divider debt text | modify `GameActions/LedgerOrchestrator.cs`, `Multiplayer/MergeNotifier.cs`, `UI/TimelineWindowUI.cs`, `Multiplayer/SettingsStatusText.cs` | `DebtClampAndFill`; `DebtWarningNamesActions` (log assertion); `CreditShiftNotifies` | M3.4 | S |
| M3.7 | Economy integration | fixture gains ledger collisions (overdraft, same-target spends, milestone race) and a peer-ahead clock; `TwoPlayerLedgerMergeConverges` on two simulated machines in opposite orders | modify `Generators/CampaignFixtureBuilder.cs`, tests | `TwoPlayerLedgerMergeConverges`; `PeerAheadFutureRowsSurviveTsLoad` | M3.1-M3.5 | M |

**Phase done:** two saves converge to byte-identical career state from the same packet set in both import orders; a TS load never prunes peer future rows.

---

## 6. Phase M4 - The co-op world (spawns, claims, fold, salvage)

| Task | Title | Scope | Files | Tests | Depends | Size |
|------|-------|-------|-------|-------|---------|------|
| M4.1 | Dock-time parent snapshot stamp | `CreateMergeBranch` step 2 OVERWRITES the closing active parent's `VesselSnapshot` from `pendingDockSelfSnapshot` (already read inside the method; cleared after it returns) and the BACKGROUND parent's from `pendingDockPartnerSnapshot` gated on `pendingDockPartnerSnapshotPid == bgParentRec.VesselPersistentId`; both conditional on the capture having happened (captures only while `data.from.vessel != data.to.vessel`; the retroactive path documents the null), so the segment-start fallback stays load-bearing. Stronger than `CreateSplitBranch`'s copy-when-null; ghost appearance unaffected (`GhostVisualSnapshot` is separate) | modify `ParsekFlight.cs` | `MergeBranchStampsDockTimeParentSnapshot` (parent snapshot UT == dock UT; fails if the segment-start snapshot is kept); `MergeBranchStampsBackgroundParentFromPartnerSnapshot`; `MergeBranchKeepsFallbackWhenCaptureNull` | - | S |
| M4.2 | Tip-claim derivation + tip-advance recompute | exporter derives `TipClaim`s (kinds Dock/Board/Claw from branch points whose partner resolves to a shared recording plus commit-time continued-source marks; Control from foreign-continuation trees; Recover from the recovery hook into the flush packet); claimant computes `advancesTip` (part tree, crew, trajectory within tolerance); importer recomputes and refuses on mismatch | modify `Multiplayer/PacketExporter.cs`, `Multiplayer/PacketImporter.cs`; create `Multiplayer/ClaimDerivation.cs` (pure) | `ClaimDerivedFromDockBranch`; `ControlClaimDerivation`; `EvaGrappleProducesNoClaim`; `AdvancesTipHintRecompute` | M2.5, M2.6, M4.11 | M |
| M4.3 | Fit predicate + conflict classifier | 7.9 inputs map, trajectory tolerance constants, fit clauses (a) and (b) incl. trajectory, transient-vs-tip-advancing, Case 1/2/3 classification (Control form of Case 3) | create `Multiplayer/ConflictClassifier.cs` (pure) | `FitPredicate` (four fixtures); `TransientVisitRequiresUnchangedTrajectory`; `ClassifierCases` | - | S |
| M4.4 | Arbitration fold core | 7.8 steps 1-5: global order, withdrawals by any retirement kind, pending, acceptance (structural advances tip, non-structural annotates), rejection, derived masks, crew ownership hook; canonical chain/tip derivation from exchanged topology only | create `Multiplayer/ArbitrationFold.cs` (pure) | `FoldGlobalOrderAndCascade`; `FoldWithdrawalPermanence`; `WithdrawalByAnyRetirementKind`; `NonStructuralWinnerDoesNotAdvanceTip`; `FoldFreshReplayEqualsIncremental` | M1.2, M4.3 | L (split: chain/tip derivation + acceptance first; masks/pending/withdrawal second) |
| M4.5 | Masks + spawn-ownership reconciliation | fold outputs written to A4's `MaskStore` (bump `MaskStateVersion`): masked recordings excluded by the unified `ComputeExclusions` funnel (ghosts + all six spawn paths incl. C3's leaf spawner) AND by a recording-id exclusion in `ComputeELS` keyed on `MaskStateVersion` (design 7.8 step 4; the ELS contract comment becomes "tombstones or fold masks"); 7.8 step 6 reconciliation through C1's clear/re-point API and C4's `MarkHeld(ReasonMissingParts)`; new coded early rejects on the C2 ladder (`ForeignMasked`, `NotCanonicalTip`) | modify `EffectiveState.cs`, `Multiplayer/MaskStore.cs`, `RecordingStore.SupersedeTerminalSpawn.cs`, `GhostPlaybackLogic.cs`, `TerminalOrbitSpawnSafety.cs` | `MaskedRecordingsExcludedFromErsAndEls`; `SpawnOwnershipReconciliation`; `UnspawnableTipHeldAsDegradedGhost`; `LeafSpawnerHonorsMask` | M4.4, A4, C1, C2, C3, C4 | M |
| M4.6 | Salvage executor: truncation | 7.9 procedure: new head id, `ChildBranchPointId` clear (`RemoveEmptyBranchPoints`), terminal recompute via C5's `TerminalStateRecompute.RecomputeTerminalFromStoredTail`, dock-time snapshot (fallback segment-start), retire window + tail, tombstones, Truncation retirement export, local target instance retirement | create `Multiplayer/SalvageExecutor.cs`; modify `SupersedeCommit.cs` (tombstone reuse) | `SalvageTruncationCut`; `TruncatedHeadIsSpawnable`; `TruncationExportsWithdrawal` | M4.1, M4.4, M4.7, C5 | L |
| M4.7 | Retirement propagation | exporter: rewind/supersede/discard/truncation records; importer: supersede rows, withdrawals into the fold, `OrphanedByRetiredRecordingId` flags, tombstones (C7 has already deleted the halt TODO and reworded its pin) | modify `Multiplayer/PacketExporter.cs`, `Multiplayer/PacketImporter.cs`, `RewindInvoker.cs`, `SupersedeCommit.cs`, `TreeDiscardPurge.cs` | `RetirementOrphanFlagging`; `CrossTreeSupersedeWalkFollows` (fails if a halt is added) | M2.6, M4.4, C7 | M |
| M4.8 | Route endpoint gate | `RouteStore` refuses foreign-owned endpoints at route creation with the owner named (7.11) | modify `Logistics/RouteStore.cs` | `RouteEndpointOwnershipGate` | M2.7 | S |
| M4.11 | Foreign-continuation route + Recover claim hook | after C6: a foreign-target probe placed BEFORE `TryRouteCommittedSpawnedClone` (pre-empting the restore path) dispatching to `StartForeignContinuationSegment` = `StartStandaloneContinuationSegment` with `continuesForeignRecordingId` threaded to `CreateSwitchContinuationSegment` (one optional parameter, one assignment) and the shared tail helper; `SwitchSegmentSession` armed; no-op auto-discard via the existing hooks (zero changes: they key on session + segment); TS/KSC Recover of a foreign spawn mirrors own-vessel recovery and queues a Recover claim (plan-time verification: how own-vessel recovery marks the recording terminal today) | modify `ParsekFlight.cs` (consume), `SwitchSegmentBuilder.cs`, `SwitchSegmentConsume.cs` (new route enum member), recovery hook (`Patches/FlightResultsPatch.cs` or the TS recover path), `Multiplayer/PacketExporter.cs` | `ForeignConsumeRouteNeverRestores` (foreign tree byte-unchanged; new tree owned locally); `ForeignNoOpSegmentDiscards`; `RecoverQueuesClaim` | M2.7, M2.5, C6 | S-M |
| M4.9 | Stale local spawn retirement | on tip advance: despawn the old local instance (no recovery rows), deferred while loaded/nearby; deferred-application queue drained at scene change | modify `Multiplayer/PacketImporter.cs`, `ParsekPlaybackPolicy.cs`, `VesselSpawner.cs` | `StaleSpawnDespawnsWhenUnloaded`; `StaleSpawnDefersWhenLoaded` | M4.5 | S |
| M4.10 | Fold + salvage integration | fixture: one collision per ladder case, a cascade, withdrawal-after-winner-retirement; end-to-end on two simulated machines | modify `Generators/CampaignFixtureBuilder.cs`, tests | `JoinBootstrapEqualsIncumbentState` (full); `VerdictLineCarriesIds` (log assertion) | M4.2-M4.9 | M |

**Phase done:** in-game `ForeignSpawnFlyBlocked` passes; the fixture's three ladder cases resolve identically on both simulated machines; a docked-and-truncated tanker parks spawnable next to the target.

---

## 7. Phase M5 - Crew

| Task | Title | Scope | Files | Tests | Depends | Size |
|------|-------|-------|-------|-------|---------|------|
| M5.1 | CREW block + import-time materialization | exporter emits attributes for not-yet-exported own crew; importer creates missing roster kerbals with name/trait/gender/veteran (extending the `EnsureCrewExistInRoster` mechanism), dismissal-blocked, owner-tagged | modify `Multiplayer/PacketExporter.cs`, `Multiplayer/PacketImporter.cs`, `VesselSpawner.cs` (shared creation core), `KerbalsModule.cs` | `ForeignCrewMaterializedWithAttributes`; `MaterializedCrewDismissalBlocked` | M2.5, M2.6 | M |
| M5.2 | Crew claim rule + derived substitution + assignment gate | fold step 5: earliest-shared packet owns; losers get deterministic stand-in substitution in derived reservation/display state; assignment gate "own or unclaimed" at crew selection; starting four contested | modify `Multiplayer/ArbitrationFold.cs`, `CrewReservationManager.cs`, `KerbalsModule.cs`, `Patches/CrewDialogFilterPatch.cs` | `CrewClaimByShareOrder`; `LoserGetsDeterministicStandIn` (same name on both machines); `AssignmentGateRefusesForeignOwned` | M4.4, M5.1 | M |
| M5.3 | Kerbals window owner tags + conflict flags | owner column, "crew conflict" row flag, materialized-foreign marker | modify `UI/KerbalsWindowUI.cs` | `KerbalsWindowRowsCarryOwner` | M5.2 | S |

---

## 8. Phase M6 - Polish and operations

| Task | Title | Scope | Files | Tests | Depends | Size |
|------|-------|-------|-------|-------|---------|------|
| M6.1 | Founder checkpoints N>0 | 7.13: Create checkpoint (requires empty pending set + all known packets applied), highwater, joiners bootstrap from latest, gap rule relative to highwater | modify `Multiplayer/CampaignStore.cs`, `Multiplayer/PacketImporter.cs`, `UI/SettingsWindowUI.cs` | `CheckpointRequiresQuiescentMerge`; `JoinFromLatestCheckpointSkipsBelowHighwater`; `GapBelowHighwaterIsNormal` | M1.6, M2.6 | M |
| M6.2 | Cloud placeholders + conflict copies | placeholder attribute detection (offline / recall-on-data-access), Warn wording, escalation downgrade; conflict-copy filename recognition Warn | modify `Multiplayer/PacketHasher.cs`, `Multiplayer/PacketImporter.cs` | `PlaceholderDowngradesEscalation`; `ConflictCopyNamesWarnOnce` | M2.4 | S |
| M6.3 | Missing-parts UX + member clocks | per-peer missing-part summary in Settings; `exporterUT` in envelope + member clock display; second-save-same-campaign Warn (edge 36) | modify `Multiplayer/PacketCodec.cs`, `Multiplayer/SettingsStatusText.cs`, `UI/SettingsWindowUI.cs`, `Multiplayer/PlayerIdentity.cs` | `MemberClockFromLatestPacket`; `SecondLinkedSaveWarns` | M1.7, M2.7 | S |
| M6.4 | In-game test category `Multiplayer` | `CampaignCreateJoinRoundTrip`, `ForeignSpawnControlContinues`, `AttributionHiddenUntilSecondMember`, `MergeWhileFlying`; note the `CommittedBatchTallySourceSyncTests` trap in `harness/lib` if the category joins a pinned tally | create `InGameTests/MultiplayerTests.cs` | the four in-game cells | M4.11 | M |
| M6.5 | Scale measurement + conditional TS proto cap | run the parameterized fixture at 200/800/3,000; record section 12 rows in the inventory; implement the per-peer TS proto visibility cap ONLY if the 3,000 row fails its budget | tests + possibly `GhostMapPresence.cs` | measured numbers recorded; `TsProtoCapPerPeer` if implemented | M2.11 | S-M |
| M6.6 | Docs closeout | user guide section (setup, one service per campaign, backups-not-play, folder-loss recovery), CHANGELOG, roadmap status, move plan artifacts to `docs/dev/done/coop-async-multiplayer/` | docs | - | all | S |

---

## 9. Dependency sketch and parallelism

```
M2.1 (fields) ---> M1.5 create ---> M1.6 join ---> M1.7 settings
   |                                     ^
   +---> M2.2 fence   M2.3 freeze        |
   +---> M2.4 codec ---> M2.5 export --> M2.8 events
   |        |            M2.6 import --> M2.7 citizenship --> M2.11 fixture
   |        |                 |
   |        |                 +--------> M2.9 cadence
   +---> M2.10 attribution
M3.1 sort, M3.3 dedup, M3.5 baseline (independent of each other; after M2.1)
M3.2 flush, M3.4 mandate (after M2.6) ---> M3.6 surfacing ---> M3.7 integration
M4.1 stamp, M4.3 classifier (independent)
M4.3 ---> M4.4 fold ---> M4.5 masks/reconcile ---> M4.9 stale spawn
M4.11 foreign route (after M2.5, M2.7) ---> M4.2 claims
M4.2 claims, M4.7 retirements (after M2.6) ---> M4.6 salvage (after M4.1, M4.4, M4.7)
M4.8 route gate (after M2.7)                ---> M4.10 integration
M5.1 ---> M5.2 ---> M5.3
M6.* after their listed deps
```

Parallel-safe sets (distinct files, no shared invariant): {M2.2, M2.3, M2.4, M2.10}; {M3.1, M3.3, M3.5}; {M4.1, M4.3}; {M4.8, M4.9}. Everything touching `PacketImporter.cs` / `PacketExporter.cs` serializes.

Rough count: 38 tasks (7 + 11 + 7 + 11 + 3 + 6, minus the shared M2.1). Four are flagged L and expected to split at plan time into ~8, so plan for ~42 dispatches.

---

## 10. Cross-cutting requirements every task inherits

- Logging per design section 14 (tags `[Campaign]`, `[PacketExport]`, `[PacketImport]`, `[Fold]`, `[Salvage]`, `[MergeEconomy]`, `[OwnershipFence]`, `[ForeignRoster]`); silent branches are review failures.
- ERS/ELS routing discipline: new readers of `CommittedRecordings` / `Ledger.Actions` go through `EffectiveState` or earn a documented `[ERS-exempt]` allowlist entry (the fold's masks are implemented AT the ERS layer).
- No new UI surfaces (existing windows/tooltips/ScreenMessages only), and no
  owner names on any surface unless `ShowOwnerAttribution` is true.
- No ownership guards on stock verbs: any player controls any campaign
  vessel as if their own; the only fenced thing is the owner's stored data
  (design 6.4, 7.7).
- Constants centralized (poll interval, staleness thresholds, retry counts, protocol filenames/extensions, timestamp format) in one `MultiplayerConstants` static class.
- Every new decision core is `internal static` and headlessly tested; `[Collection("Sequential")]` where static stores are touched.
- Each task's final commit leaves `dotnet test` green; docs (CHANGELOG, todo, this file's status) updated per commit when behavior changes.
