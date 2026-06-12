# Plan: M-MIS-9-R1 - scope the route recovery credit to the creation-time tree

2026-06-12. Fixes the open residual of the M-MIS-9 freeze (todo entry
"M-MIS-9-R1 - Route recovery credit sums the WHOLE source tree").

## The bug

`RouteOrchestrator.EmitPendingRecoveryCredit` sizes the per-cycle KSC recovery
credit via `RouteRunCostCalculator.SumRecoveredCredits` scoped to
`ResolveTreeRecordingIds(route)` = every recording id currently in the route's
source tree. A branch added to the tree AFTER route creation that ends in a
vessel recovery adds its `FundsEarning(Recovery)` rows to the sum and inflates
the route's recurring credit on every subsequent flush. The same inflated sum
also feeds the run-cost DISPLAY (`LogisticsWindowUI` ->
`RouteRunCostCalculator.Compute`), which goes through the same resolver.

## Decision: fix shape (a), implemented as a creation-time tree snapshot

Of the two candidate shapes in the todo entry:

- **(b) cycle attribution is rejected.** Recovery `FundsEarning` rows are
  authored ONCE by the original recorded flight (the player flying home and
  recovering); route cycles do not produce recovery rows at all - they produce
  the already-cycle-keyed `RouteRecoveryCredited` rows. Tagging "the rows a
  cycle produced" has nothing to tag. Making playback emit per-cycle recovery
  rows would be a new ledger row semantic that the rewind cutoff walk and the
  re-fly tombstone path would both have to learn, for no gain: the shipped
  contract (recovery-credit Done entry) is explicitly a CONSTANT per-cycle
  amount, with per-run landing attribution deferred (OQ1).

- **(a) creation-time known-recording union, taken as the whole tree at
  creation.** At route creation the whole source tree IS the known union the
  todo describes (SourceRefs + excluded-key bases + their descendants): it
  contains the post-undock fly-home-and-recover leg, so gotcha G1 (a naive
  member-set filter silently returns ZERO credit) stays satisfied. Deriving
  "descendants" against the CURRENT tree instead would re-include post-creation
  forks rooted in the excluded post-undock subtree - exactly the bug - and
  deriving them against the creation-time topology requires a creation-time
  snapshot anyway. So snapshot the id SET directly; it is simpler and
  positionally immune (recording ids, not /segN composition keys).

## Changes

1. `Route.CreationTreeRecordingIds` - new persisted `HashSet<string>`
   (Ordinal), the source tree's recording ids captured at creation.
2. `RouteBuilder.BuildRoute` - populate it from `committedTree.Recordings`
   keys; when `committedTree` is null (defensive path - the production dialog
   and candidate sources always pass a committed tree) leave the snapshot
   EMPTY so the resolver fails open. A member-id fallback was considered and
   rejected in review: member ids exclude the post-undock recover leg, so if
   the route's tree id resolved later the intersection would silently zero
   the legitimate credit (the G1 regression).
3. `RouteCodec` - new sparse `CREATION_TREE_RECORDINGS` child node with
   repeated `id` values, mirroring `EXCLUDED_INTERVALS` (empty set writes no
   node; missing node loads as empty).
4. `RouteRunCostCalculator.ResolveTreeRecordingIds(Route)` - after resolving
   the current tree's id set, intersect it with the route's creation snapshot
   when the snapshot is non-empty; log the count of post-creation ids dropped.
   An empty/missing snapshot FAILS OPEN to the whole current tree (degenerate
   or pre-field route; preserves G1's never-silently-zero contract), with a
   log line. The candidate-path overload `ResolveTreeRecordingIds(tree)` is
   unchanged: at candidate time, creation is now.

Both production consumers (the credit emit site and the UI run-cost display)
flow through the changed resolver, so the displayed net and the ledger credit
stay consistent.

## Safety invariants preserved

- The credit AMOUNT is still recomputed fresh from ELS at every flush - no
  cached amounts - so the T-CRASH-WINDOW-TOMBSTONE contract (a tombstone in
  the crash window zeroes the owed credit) is untouched.
- Row semantics are unchanged: nothing new for the rewind cutoff walk or the
  re-fly tombstone path to learn. Only the id-scope input to the sum narrows.
- Conservative failure direction: a post-creation re-fly of the recover leg
  tombstones the old rows (supersede) and mints new ids the freeze excludes,
  so the credit drops to zero rather than ever over-crediting. This matches
  the M-MIS-9 freeze doctrine (route economics freeze at creation; member-path
  changes already flip the route off ghost-driving via `RevalidateSources`).

## Tests

- `RouteRunCostCalculatorTests`: creation-snapshot filter drops a
  post-creation id; empty snapshot fails open to the whole tree; a stale
  snapshot id absent from the current tree does not resurface. Log assertions
  on all three.
- `RouteRecoveryCreditTests`: a post-creation recovered branch does not
  inflate the emitted credit; the post-undock recover leg (outside SourceRefs)
  still counts (G1 guard).
- `RouteBuilderTests`: `BuildRoute` captures the snapshot from the committed
  tree; the null-tree path leaves the snapshot empty (fail-open).
- `RouteCodecTests`: non-empty snapshot round-trips; empty snapshot writes no
  node and loads as empty.
