# KSP ProgressNode & Milestone API Investigation

## Summary

KSP's milestone system is a tree of `ProgressNode` objects owned by the `ProgressTracking` ScenarioModule singleton. Each node has a string `Id` property that matches exactly the `MilestoneId` strings Parsek stores in GameActions (captured from `OnProgressComplete` callbacks). **Reversal is feasible but requires reflection**: the `reached` and `complete` fields are private with no public setters or un-achieve methods. The recommended approach is ConfigNode round-trip (save, scrub unreached nodes, reload) or direct field reflection. Rewards are NOT stored on nodes; they are computed dynamically by `ProgressUtilities` and applied immediately via `Funding`/`Reputation`/`ResearchAndDevelopment` singletons. Parsek already patches those singletons separately, so milestone patching only needs to fix the achieved/unreached flags.

## Tree Structure

### Top-level architecture

```
ProgressTracking.Instance           (ScenarioModule singleton)
  └── achievementTree               (ProgressTree — flat list of top-level nodes)
       ├── FirstLaunch              (ProgressNode, id="FirstLaunch")
       ├── CrewRecovery             (ProgressNode subclass, id="firstCrewToSurvive")
       ├── TowerBuzz                (ProgressNode subclass)
       ├── RecordsAltitude          (ProgressNode subclass)
       ├── RecordsDepth             (ProgressNode subclass)
       ├── RecordsSpeed             (ProgressNode subclass)
       ├── RecordsDistance          (ProgressNode subclass)
       ├── ReachSpace               (ProgressNode subclass, id="ReachedSpace" in save)
       ├── KSCLanding               (TargetedLanding, id="KSC")
       ├── runwayLanding            (TargetedLanding, id="Runway")
       ├── launchpadLanding         (TargetedLanding, id="LaunchPad")
       ├── POI* (many)              (PointOfInterest nodes)
       └── CelestialBodySubtree[]   (one per body: Kerbin, Mun, Minmus, ...)
            └── Subtree children:
                 ├── Landing
                 ├── Splashdown
                 ├── Science
                 ├── Orbit
                 ├── Flyby
                 ├── Escape
                 ├── ReturnFrom
                 ├── Flight
                 ├── SurfaceEVA
                 ├── FlagPlant
                 ├── Rendezvous
                 ├── Docking
                 ├── CrewTransfer
                 └── StationaryOrbit (if applicable)
```

### ProgressTree internals

`ProgressTree` is essentially a `List<ProgressNode>` wrapper with:
- `AddNode(ProgressNode)` — adds to internal list
- `this[int i]` — index-based access
- `this[string s]` — linear scan by `node.Id == s`
- `Count` property
- `GetEnumerator()` — allows foreach

Each `ProgressNode` has its own `Subtree` (`ProgressTree`) which contains child nodes. This makes the structure a **recursive tree** — nodes at any depth can have children.

### Iteration pattern

To iterate **all** nodes recursively:

```csharp
void VisitAll(ProgressTree tree, Action<ProgressNode> visitor)
{
    for (int i = 0; i < tree.Count; i++)
    {
        var node = tree[i];
        visitor(node);
        if (node.Subtree != null && node.Subtree.Count > 0)
            VisitAll(node.Subtree, visitor);
    }
}
```

KSP itself uses this pattern in `RecurseCheatProgression`.

### `ProgressTracking.Update()` — active polling

Every frame, `ProgressTracking.Update()` iterates all vessels via `achievementTree.IterateVessels(vessel)`, which invokes `OnIterateVessels` callbacks on nodes that have them. This is how milestones auto-detect achievements. **Important for Parsek**: if we un-complete a node, the vessel-iteration loop may immediately re-trigger it if the conditions are still met (e.g., a vessel is still in orbit of a body). This is a key risk for reversal.

## Node Identification

### The `Id` property

Each `ProgressNode` has a **read-only** `string Id` property backed by a private `string id` field set in the constructor:

```csharp
private string id;
public string Id => id;

public ProgressNode(string id, bool startReached) {
    this.id = id;
    reached = startReached;
}
```

### How IDs map to Parsek's MilestoneId

Parsek captures milestone IDs from `OnProgressComplete` via `node.Id`:

```csharp
// GameStateRecorder.cs:638
string milestoneId = node.Id ?? "";
```

This means **Parsek's MilestoneId IS the ProgressNode.Id**. The mapping is 1:1 identity. No translation layer needed.

### How IDs appear in save files

Save file node names match `ProgressNode.Id` exactly. From `ProgressTree.Save()`:

```csharp
progressNode.Save(node.AddNode(progressNode.Id));
```

Example save data shows:
- `FirstLaunch` (top-level node)
- `RecordsAltitude`, `RecordsSpeed`, etc. (top-level)
- `ReachedSpace` (top-level — note: the field name is `reachSpace` but the Id string is `"ReachedSpace"` based on save output)
- `Kerbin` (CelestialBodySubtree, Id = body name)
- `Landing`, `Splashdown`, `Science` (children of body subtree)

### Hierarchical lookup

`ProgressTracking.FindNode(params string[] search)` does hierarchical lookup:
- `FindNode("Kerbin")` finds the CelestialBodySubtree for Kerbin
- `FindNode("Kerbin", "Landing")` finds Landing under Kerbin's subtree
- Recursively descends through `Subtree` at each depth level

**Critical question**: When `OnProgressComplete` fires for a nested node like Kerbin/Landing, what does `node.Id` return? It returns just `"Landing"` (the node's own Id), NOT the full path. But since `Landing` can exist under multiple bodies (Kerbin, Mun, etc.), the bare Id alone is **ambiguous**.

The save file shows nested structure: `Kerbin > Landing`, `Mun > Landing`, etc. If Parsek only stores the leaf Id (e.g., `"Landing"`), we cannot uniquely identify which body's Landing node was achieved.

**Action required**: Verify whether Parsek's recorded MilestoneId for body-specific milestones includes the body prefix or just the leaf. If just the leaf, we need to enhance capture to include the path (e.g., `"Kerbin/Landing"`) or augment with the body name.

## Achievement State

### Fields

```csharp
private bool reached;     // node has been reached (first stage)
private bool complete;    // node has been completed (second stage)
protected double AchieveDate;  // UT of achievement

public bool IsReached => reached;      // read-only
public bool IsComplete => complete;    // read-only
public bool IsCompleteManned { get; protected set; }   // can be set by subclasses
public bool IsCompleteUnmanned { get; protected set; } // can be set by subclasses
```

### State progression

A node goes through these states:
1. **Unreached** — `reached=false, complete=false` (initial state)
2. **Reached** — `reached=true, complete=false` (via `Reach()`)
3. **Complete** — `reached=true, complete=true` (via `Complete()`, which auto-calls `Reach()` if needed)

Additionally, `IsCompleteManned`/`IsCompleteUnmanned` track whether completion was with or without crew.

### Mutation methods (forward direction only)

- `Reach()` — sets `reached=true`, fires `GameEvents.OnProgressReached`, sets `AchieveDate`
- `Complete()` — auto-reaches if not reached, sets `complete=true`, fires `GameEvents.OnProgressComplete`, sets `AchieveDate`
- `CheatComplete()` — sets both manned+unmanned flags, then calls `Complete()`
- `Achieve()` — only fires `GameEvents.OnProgressAchieved` (does NOT change any flags)

### No reverse API

There is **no** `Uncomplete()`, `Unreach()`, `SetComplete(false)`, or any method to reverse a node's state. The `reached` and `complete` fields are **private** with no setters. The only way to reverse state is:
1. **Reflection** — directly set the private fields
2. **ConfigNode round-trip** — save the tree, remove/modify nodes, reload
3. **Full tree regeneration** — destroy and recreate `ProgressTracking.Instance` (nuclear option)

## Reversal Feasibility

### Option A: Reflection (recommended)

Set private fields directly via reflection:

```csharp
var reachedField = typeof(ProgressNode).GetField("reached", BindingFlags.NonPublic | BindingFlags.Instance);
var completeField = typeof(ProgressNode).GetField("complete", BindingFlags.NonPublic | BindingFlags.Instance);

// Un-complete a node
reachedField.SetValue(node, false);
completeField.SetValue(node, false);
node.IsCompleteManned = false;    // public setter (protected set)
node.IsCompleteUnmanned = false;  // public setter (protected set)
```

**Pros**: Simple, direct, no save/load overhead, surgical.
**Cons**: Requires reflection (but Parsek already uses Harmony which is far more invasive). Does not fire any events (which is actually what we want during patching).

### Option B: ConfigNode round-trip

1. Save the tree: `achievementTree.Save(tempNode)`
2. Strip nodes that should not be achieved from `tempNode`
3. Regenerate the tree: `achievementTree = generateAchievementsTree()` (but this is private)
4. Reload: `achievementTree.Load(tempNode)`

**Problem**: `generateAchievementsTree()` is private on `ProgressTracking`. We could invoke it via reflection, but the tree construction also wires up event handlers (`OnDeploy`, `OnStow`, `OnIterateVessels`) which must be properly managed.

**Problem**: `ProgressTree.Load()` only loads nodes that are **present** in the ConfigNode. It does NOT reset nodes that are missing. From the code:

```csharp
public void Load(ConfigNode node) {
    for (int i = 0; i < nodes.Count; i++) {
        if (node.HasNode(progressNode.Id))
            progressNode.Load(node.GetNode(progressNode.Id));
        // Missing nodes are simply NOT loaded — they keep whatever state they had
    }
}
```

And `ProgressNode.Load()` unconditionally sets `reached = true` at the top. It cannot load an unreached state. This means **ConfigNode round-trip cannot un-achieve nodes**. The Load method only knows how to set nodes as reached/completed, never unreached.

### Option C: Harmony patch to suppress re-triggering

Instead of reversing state, we could suppress the `ProgressTracking.Update()` vessel iteration that auto-detects achievements. But this doesn't help with nodes that are already flagged as complete.

### Recommendation: Option A (reflection)

Reflection is the only viable approach for true reversal. It's simple, compatible with Parsek's existing architecture (IsReplayingActions suppression), and avoids the broken ConfigNode round-trip path.

### Re-triggering risk

After un-completing a node via reflection, `ProgressTracking.Update()` will call `IterateVessels` on every vessel every frame. If the conditions for the milestone are still met (e.g., a vessel is in orbit), the node will immediately re-trigger.

**Mitigation**: The `IsReplayingActions` flag already suppresses `OnProgressComplete` callbacks in `GameStateRecorder`. But KSP's own `ProgressTracking` callbacks will still fire and re-complete the node. We may need to:
1. Only un-complete milestones that genuinely should not exist in the new timeline
2. Accept that live vessels may immediately re-trigger milestones (which is actually correct behavior if the conditions are met)
3. Consider suppressing the iteration briefly during patching (set a flag, skip `Update()` for one frame)

In practice, this may be a non-issue: the main use case is rewind, where the player is going back to an earlier state. If a vessel still exists that satisfies the milestone, the milestone SHOULD remain achieved. The milestones that truly need reversal are ones where the triggering vessel no longer exists in the rewound state.

## Rewards

### Reward computation (NOT stored on nodes)

Rewards are computed dynamically via `ProgressUtilities.WorldFirstStandardReward()` and `ProgressUtilities.WorldFirstIntervalReward()`:

```csharp
protected void AwardProgressStandard(string description, ProgressType progress, CelestialBody body = null) {
    float funds = ProgressUtilities.WorldFirstStandardReward(ProgressRewardType.PROGRESS, Currency.Funds, progress, body);
    float science = ProgressUtilities.WorldFirstStandardReward(ProgressRewardType.PROGRESS, Currency.Science, progress, body);
    float reputation = ProgressUtilities.WorldFirstStandardReward(ProgressRewardType.PROGRESS, Currency.Reputation, progress, body);
    AwardProgress(description, funds, science, reputation, body);
}
```

### Reward application

`AwardProgress()` directly mutates KSP singletons:
- `Funding.Instance.AddFunds(funds, TransactionReasons.Progression)`
- `ResearchAndDevelopment.Instance.AddScience(science, TransactionReasons.Progression)`
- `Reputation.Instance.AddReputation(reputation, TransactionReasons.Progression)`

These are fire-and-forget — there is no undo mechanism. But Parsek already patches Funds, Science, and Reputation to computed targets via `PatchFunds`/`PatchScience`/`PatchReputation`. So reward reversal is already handled by the resource modules. **Milestone patching only needs to fix the achieved flags, not the reward amounts.**

### Reward values in Parsek

Parsek's `GameAction.MilestoneFundsAwarded` and `GameAction.MilestoneRepAwarded` are captured as 0 (see `GameStateRecorder.OnProgressComplete` comments). The `MilestonesModule` uses these for first-tier deduplication only; actual fund/rep values flow through `FundsModule` and `ReputationModule` from the `FundsEarning`/`ReputationEarning` actions that KSP generates via the `Funding`/`Reputation` callbacks.

## Save/Load Serialization

### Save format

`ProgressTree.Save()` only saves **reached** nodes:

```csharp
public void Save(ConfigNode node) {
    for (int i = 0; i < nodes.Count; i++) {
        if (progressNode.IsReached)
            progressNode.Save(node.AddNode(progressNode.Id));
    }
}
```

Each reached node is saved as a ConfigNode named by its Id. Unreached nodes are simply omitted.

### Node save format

`ProgressNode.Save()` writes one of:
- `completedManned = <AchieveDate>` (complete + manned only)
- `completedUnmanned = <AchieveDate>` (complete + unmanned only)
- `completed = <AchieveDate>` (complete + both manned and unmanned)
- `reached = <AchieveDate>` (reached but not complete)

### Node load format

`ProgressNode.Load()` unconditionally sets `reached = true`, then checks for the most specific completion key:
1. `reached` value -> just reached
2. `completed` value -> complete + manned + unmanned
3. `completedManned` value -> complete + manned only
4. `completedUnmanned` value -> complete + unmanned only

**Key insight**: The Load method cannot represent an unreached state. If a ConfigNode exists for a node, that node is at minimum reached. The only way to have an unreached node is to not have a ConfigNode for it at all — which is what `ProgressTree.Load` naturally handles (it skips nodes without matching ConfigNodes). But as noted above, skipping doesn't RESET already-reached nodes.

### Subclasses may have additional serialization

`OnLoad(ConfigNode)` and `OnSave(ConfigNode)` are virtual methods that subclasses override. For example:
- Record nodes (RecordsAltitude, etc.) likely save `record = <value>`
- CelestialBodySubtree saves its children recursively
- PointOfInterest may save discovery state

## Key Subclasses

Based on the DLL analysis and `ProgressTracking.generateAchievementsTree()`:

| Class | Id example | Description |
|-------|-----------|-------------|
| `FirstLaunch` | `"FirstLaunch"` | First vessel launch |
| `CrewRecovery` | `"firstCrewToSurvive"` | First crew recovery |
| `TowerBuzz` | `"TowerBuzz"` | Fly close to KSC tower |
| `RecordsAltitude` | `"RecordsAltitude"` | Altitude records (interval-based) |
| `RecordsDepth` | `"RecordsDepth"` | Depth records |
| `RecordsSpeed` | `"RecordsSpeed"` | Speed records |
| `RecordsDistance` | `"RecordsDistance"` | Distance records |
| `ReachSpace` | `"ReachedSpace"` | First time reaching space |
| `TargetedLanding` | `"KSC"`, `"Runway"`, `"LaunchPad"` | Landing at specific KSC locations |
| `PointOfInterest` | body+name, e.g. `"KSC2"`, `"IslandAirfield"` | Easter eggs, discoverable sites |
| `CelestialBodySubtree` | body name, e.g. `"Kerbin"`, `"Mun"` | Per-body subtree container |
| `Landing` | `"Landing"` | First landing (per body) |
| `Splashdown` | `"Splashdown"` | First splashdown (per body) |
| `Science` | `"Science"` | First science (per body) |
| `Orbit` | `"Orbit"` | First orbit (per body) |
| `Flyby` | `"Flyby"` | First flyby (per body) |
| `Escape` | `"Escape"` | First escape from body SOI |
| `ReturnFrom` | `"ReturnFrom"` | Return from body |
| `Flight` | `"Flight"` | First flight at body |
| `SurfaceEVA` | `"SurfaceEVA"` | First EVA on surface (per body) |
| `FlagPlant` | `"FlagPlant"` | First flag plant (per body) |
| `Rendezvous` | `"Rendezvous"` | First rendezvous at body |
| `Docking` | `"Docking"` | First docking at body |
| `CrewTransfer` | `"CrewTransfer"` | First crew transfer at body |

## Implementation Plan for Parsek

### Phase 1: Verify MilestoneId uniqueness

Before implementing, verify in-game what `node.Id` returns for body-specific milestones when `OnProgressComplete` fires. If the Id is just `"Landing"` (without body prefix), we need to either:
- **Enhance capture**: Store `bodyName + "/" + node.Id` as the MilestoneId
- **Use `FindNode`**: Look up nodes via the hierarchical `FindNode("Kerbin", "Landing")` path, which requires knowing the body

Looking at the save file data, the body subtree node Id is the body name (`"Kerbin"`), and its children have leaf Ids (`"Landing"`, `"Science"`, etc.). The `OnProgressComplete` event fires with the leaf node, so **the Id alone may be ambiguous**.

However, examining `generateAchievementsTree()` more carefully: top-level nodes like `FirstLaunch`, `RecordsAltitude`, `ReachSpace` have unique Ids. The ambiguity only exists for body subtree children (Landing, Science, etc.). Since these children are nodes within a CelestialBodySubtree, and each body's subtree is separate, the node object itself is unique even if its Id string is not globally unique.

**For patching, we iterate all nodes and match by object identity/path, not by bare Id.**

### Phase 2: Implement `PatchMilestones`

```csharp
internal static void PatchMilestones(MilestonesModule milestones)
{
    if (milestones == null) { /* warn, return */ }
    if (ProgressTracking.Instance == null) { /* verbose, return */ }

    var tree = ProgressTracking.Instance.achievementTree;
    int setCount = 0, clearCount = 0, skipCount = 0;

    // Cache reflection fields once
    var reachedField = typeof(ProgressNode).GetField("reached",
        BindingFlags.NonPublic | BindingFlags.Instance);
    var completeField = typeof(ProgressNode).GetField("complete",
        BindingFlags.NonPublic | BindingFlags.Instance);

    // Build set of credited milestone Ids from the module
    // (MilestonesModule.creditedMilestones is private — need accessor or expose)
    HashSet<string> credited = GetCreditedMilestoneIds(milestones);

    // Recurse the tree
    PatchNodeRecursive(tree, credited, reachedField, completeField,
        "", ref setCount, ref clearCount, ref skipCount);

    ParsekLog.Info(Tag,
        $"PatchMilestones: set={setCount}, cleared={clearCount}, " +
        $"unchanged={skipCount}, credited={credited.Count}");
}

static void PatchNodeRecursive(ProgressTree tree, HashSet<string> credited,
    FieldInfo reachedField, FieldInfo completeField,
    string pathPrefix,
    ref int setCount, ref int clearCount, ref int skipCount)
{
    for (int i = 0; i < tree.Count; i++)
    {
        var node = tree[i];
        string fullPath = string.IsNullOrEmpty(pathPrefix)
            ? node.Id : pathPrefix + "/" + node.Id;

        bool shouldBeAchieved = credited.Contains(fullPath) || credited.Contains(node.Id);
        bool isAchieved = node.IsComplete;

        if (shouldBeAchieved && !isAchieved)
        {
            // Need to set as complete (forward case — rare during rewind)
            node.Complete();  // or use reflection to avoid event firing
            setCount++;
        }
        else if (!shouldBeAchieved && isAchieved)
        {
            // Need to un-complete (the rewind case)
            reachedField.SetValue(node, false);
            completeField.SetValue(node, false);
            node.IsCompleteManned = false;
            node.IsCompleteUnmanned = false;
            clearCount++;
        }
        else
        {
            skipCount++;
        }

        // Recurse into subtree
        if (node.Subtree != null && node.Subtree.Count > 0)
            PatchNodeRecursive(node.Subtree, credited, reachedField, completeField,
                fullPath, ref setCount, ref clearCount, ref skipCount);
    }
}
```

### Phase 3: MilestonesModule accessor

Add a method to `MilestonesModule` to expose the credited set for patching:

```csharp
internal HashSet<string> GetCreditedMilestoneIds()
{
    return new HashSet<string>(creditedMilestones);
}
```

### Phase 4: Milestone ID format decision

Decide whether to store bare Ids or full paths. Options:
- **Bare Id**: Simpler, works for top-level nodes, ambiguous for body subtree children
- **Full path**: `"Kerbin/Landing"`, unambiguous, requires capture-side change
- **Hybrid**: Try bare Id first, fall back to path lookup — fragile

**Recommendation**: Use full path (`"BodyName/NodeId"` for body children, bare Id for top-level). This requires a one-time change to `GameStateRecorder.OnProgressComplete` to detect whether the node is inside a `CelestialBodySubtree` and prefix accordingly.

## Risks and Open Questions

### 1. MilestoneId ambiguity (MUST RESOLVE)

Is `node.Id` for `Kerbin/Landing` just `"Landing"` or something more specific? The save data and code strongly suggest it's just `"Landing"`. If so, we need path-qualified Ids before milestone patching can work reliably. This is a recording format change — existing recordings with bare `"Landing"` Ids would be ambiguous.

**Verification**: Add a debug log in `OnProgressComplete` that prints the full node ancestry. Or check if `CelestialBodySubtree` nodes use a body-prefixed Id.

### 2. Re-triggering after reversal

After clearing a milestone via reflection, `ProgressTracking.Update()` iterates all vessels every frame and may immediately re-trigger the milestone. For milestones tied to active vessels (orbit, etc.), this is actually correct. For milestones tied to historical events (first launch, records), they won't re-trigger unless conditions are met again.

**Mitigation**: For the patching window, we could temporarily null out `OnIterateVessels` callbacks or set a suppression flag. But this adds complexity. Practically, the re-triggering issue only matters for milestones that would be re-achieved anyway.

### 3. Record nodes (RecordsAltitude, etc.)

Record nodes have additional state (record values). Un-completing a record node may also need to reset the record value. Subclass `OnSave`/`OnLoad` methods may store this. Reflection would need to target subclass-specific fields.

### 4. CelestialBodySubtree special behavior

`CelestialBodySubtree` has a `LinkBodyHome` method that wires up cross-references. Un-completing body nodes may break these links. Needs investigation.

### 5. Event suppression during patching

When we call `node.Complete()` for forward-patching (setting milestones that should exist), it fires `GameEvents.OnProgressComplete`, which `GameStateRecorder` would capture as a new event. The existing `IsReplayingActions` flag suppresses this. For the reverse direction (reflection), no events fire, so no suppression needed.

### 6. Manned vs. unmanned tracking

The current `MilestonesModule` does not track whether a milestone was achieved manned or unmanned. For full fidelity reversal, we would need to store this. For V1, treating all achievements as "both manned and unmanned" is acceptable.

### 7. AchieveDate

When forward-patching a milestone, `Complete()` sets `AchieveDate = Planetarium.GetUniversalTime()`. This may not match the original achievement time from the ledger. For full fidelity, we could set `AchieveDate` via reflection to the GameAction's UT. Low priority.
