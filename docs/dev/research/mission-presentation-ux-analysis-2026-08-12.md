# Mission Presentation — UX Analysis

Analysis of why the Missions tab is hard to parse, what players actually need from it, and a
staged improvement path. Inputs: `missions-ui-structure-extract-2026-08-12.md`,
`recordings-ui-structure-extract-2026-08-12.md`, plus source verification of
`Source/Parsek/MissionComposition.cs` (interval-chaining, steps 5–8) and
`docs/dev/design-mission-abstractions.md` (design intent).

Framing constraint that shapes everything below: **in Basic mode the Missions tab is the only
surface most players ever see** (the Recordings tab and even the tab bar are hidden). It must
stand alone, and today it is a selection-editing surface wearing a presentation surface's
clothes.

---

## 1. Diagnosis — why it's hard to parse

### 1.0 The root cause: a conceptual-model mismatch

A player thinks of a mission as a **narrative over time**: *launched, dropped the booster,
transferred, docked at the station, undocked the lander, landed, Bob went EVA, came home.*
Events in order; vessels are characters that enter and leave the story.

The UI presents a **structural decomposition**: vessels → intervals → roster atoms, where
**time is encoded as nesting depth**. This is not an accident of rendering — it is exactly what
the data pipeline produces. `MissionComposition.cs` step 5 says it outright:

> *"Chain the intervals: each interval's survivor (the next interval) is its first child, so
> the continuing vessel always reads above the pieces that left it."*

And the design doc (`design-mission-abstractions.md`) confirms the composition model was built
so looping has "a defined, player-configurable unit" — i.e. it exists to make the **loop-unit
selection** editable, not to tell the mission's story. The window then asks the player to read
a selection-editing structure as a story, and every specific failure below is a symptom of that
one mismatch.

Universal UI grammar says nesting = containment ("this is part of that"). Here nesting means
"later than" (interval chain), "separated from" (peels), "left during" (EVA), and "is made of"
(roster atoms) — **four different relations expressed with one identical visual device**, and
the player is given no legend.

### 1.1 The specific comprehension failures, ranked by player impact

**F1 — The right-drifting staircase (time as depth). [Highest impact]**
A three-stage vessel renders as three rows, each indented one level deeper, all carrying the
*same vessel name*:

```
└─▼ Kerbal X (pod x1, probe x1, crew x3)     Launch     Decoupled
   ├─▼ Kerbal X (pod x1, crew x3)            Decoupled  Undocked
   │  ├─▼ Kerbal X (pod x1, crew x2)         Undocked   Landed
```

A player reads this as "Kerbal X contains a Kerbal X which contains a Kerbal X" — a nonsense
containment. The correct reading ("the same ship, three consecutive chapters") is only
recoverable by noticing the Start/End times abut, which requires reading four date columns and
doing arithmetic. Long missions with many boundaries drift far right and burn horizontal space
that the columns need. This is the single failure the owner's concern names, and it is the
load-bearing one: every other row type (peels, EVA, atoms, partner journeys) hangs off this
staircase, so misreading it poisons everything below it.

**F2 — Include-checkbox semantics actively mislead. [Highest impact, silent]**
The `[x]` in the `#` slot writes `Mission.ExcludedIntervalKeys`, which is consumed **only by
the loop-unit pipeline** (extract §7.3). It does not hide the ghost, does not stop non-loop
playback, does not touch KSC showcase or map presence — those gate on
`Recording.PlaybackEnabled`, whose *only writers live on the Recordings tab, which Basic mode
hides*. So a Basic-mode player unticks the booster row expecting the booster ghost to vanish,
nothing visible changes, and there is no tooltip, no label, and no other surface to explain
why. Additionally the exclusion is deliberately **non-cascading** (`MissionIntervalSelection`),
so unticking a parent interval leaves its peeled children ticked — the staircase continues
un-dimmed beneath a dimmed ancestor, visually contradicting containment semantics again.
Worst kind of failure: not "hard to read" but "teaches a wrong model and never corrects it."

**F3 — No mission-level summary: status, duration, span, outcome. [High]**
The header bar carries name + eight buttons + loop controls, but **no start, no end, no
duration, no vessel/crew count, no outcome, no "running/finished/looping" state** — even
though `MissionSpanStartUT` and `MissionSpanSeconds` are already computed internally (extract
§6.4, §7.7). The player's first question ("what is this mission, roughly?") can only be
answered by decoding the staircase. The single TTL value hides on the *first composition row*,
not the header, so a collapsed mission shows no schedule at all.

**F4 — Unnamed same-tree dock partners. [High]**
A dock renders as an interval boundary with `StartEvent = "Docked"` and a composition label
that silently grows (`pod x1, crew x1` → `pod x2, probe x1, crew x4`). **Nothing names the
vessel that joined** (extract §7.11); its own pre-dock life is a separate root row or a
separate mission entirely. The most narratively important event in a station mission — the
rendezvous — is presented as an unexplained inventory jump.

**F5 — Composition-diff labels: the player must compute deltas. [High]**
The only signal of *what changed* at a boundary is the difference between two parenthesized
count strings. `(pod x1, probe x1, crew x3)` → `(pod x1, crew x3)`: did a probe leave? which
one? The event word says "Decoupled" but not what decoupled; the peeled child row appears
somewhere below as a sibling, and connecting the two is left to the player. Labels state
absolute inventory when players think in deltas ("dropped the booster", "Bob got out").

**F6 — Loop/schedule state is opaque and conflicts are silent. [Medium-high]**
The loop-period cell is a four-state control whose states (off / auto / manual-amber-clamped /
locked-label like `~13d-1mo (Mun window, varies)`) are distinguished by greyness, editability,
and tint alone — no state is named. TTL's four outputs (`blank` / `not aligned` /
`continuous` / `T- 2h 14m`) are similarly unexplained; "not aligned" is engine vocabulary.
And enabling one mission's loop can **silently untick another mission's loop** (one loop per
tree/spanned set) with log-only feedback — the other row just changes on the next frame
(extract §7.14).

**F7 — "Partner journey" rows: jargon plus hidden side effects. [Medium]**
"Partner journey — Munport Station" is designer vocabulary, off by default, and ticking it
both adds foreign rows *and* can clear loops on the linked mission because the spanned tree
set widened — an invisible coupling between a checkbox and another mission's schedule.

**F8 — Event-word vocabulary. [Medium]**
`"Decoupled"`, `"Undocked"`, `"Broke off"`, `"Broke up"`, `"Switch"`, `"End"`,
`"Boarded"` — terse, objectless, passive. "Undocked" from what? "Switch" of what? Cause-wins-
over-type collapsing means the same word covers different situations. `"TTL"` as a header is a
military abbreviation the code itself comments as "Time to launch". Roster atoms `Pod`,
`Probe`, `Seat` are hardware nouns rendered as siblings of kerbal names.

**F9 — The Missions/Recordings duality (Advanced mode). [Medium]**
The same underlying tree renders as *two disjoint mental models*: composition intervals here,
group/chain/vessel blocks there. Parallel-but-different toggles (`include` vs
`PlaybackEnabled`, `Mission.Archived` vs `Recording.Hidden`, mission Loop vs per-recording
Loop) share names but not effects. An Advanced user who learns one tab is actively misled on
the other. In Basic the duality "disappears" only by orphaning half the controls (F2).

**F10 — The raw `#` column. [Low]**
A per-tree 1-based index, shared by clones, meaningless to the player, yet it occupies the
first sortable column position and shares its slot with the include checkbox on child rows —
so the column header `#` labels *two unrelated things*.

**F11 — Kerbals are structurally buried. [Low-medium]**
"Who is on this mission?" is answered only by crew counts in labels, EVA offshoot rows, and
named roster atoms that appear *only at a bare terminal* (and a single-atom terminal collapses
to a leaf, hiding even that). There is no roster anywhere.

---

## 2. Player mental-model analysis

Questions a player brings to this window, and how the current UI serves them:

| # | Player question | Answerable today? | Effort / failure mode |
|---|---|---|---|
| Q1 | "What does this mission do?" | Partially | Decode the staircase: read every row's events + 4 date columns, mentally re-serialize into a story. High effort; F1/F4/F5 |
| Q2 | "When does it launch next / repeat?" | Yes | TTL on the first row + 4-state period cell; must learn "not aligned"/"continuous" vocabulary. Medium effort; F3/F6 |
| Q3 | "Which ships are in it?" | Mostly | Roots + peel rows show vessels, but interval rows repeat the same vessel 3–5×, inflating the apparent count. Medium; F1 |
| Q4 | "Which kerbals are in it?" | Barely | Counts in labels; names only at terminals/EVA rows, sometimes collapsed away. High; F11 |
| Q5 | "How long does it take? Did it succeed?" | No / barely | Mission duration computed but never rendered; outcome only as deepest rows' End events, no rollup. F3 |
| Q6 | "What happens if I loop it?" | Partially | Period cell + Warp-to preview; the loop-steals-from-sibling side effect is invisible. F6 |
| Q7 | "What does this checkbox do?" | No | No tooltip; the actual effect (loop-unit membership) is not what any player would guess. F2 |
| Q8 | "Why did my ghost disappear?" | **No, on any surface Basic players have** | PlaybackEnabled, supersede, Hidden, debris — all invisible here (extract §7.1–7.5). F2/F9 |
| Q9 | "Who docked with my station / where did the booster land?" | Partner: no. Booster: yes with effort | Same-tree partner never named (F4); peel rows do carry End event + time |
| Q10 | "Is this mission running right now?" | No | No live/active indicator; Status exists only on the Recordings tab |

Score: of ten natural questions, the window answers **two comfortably (Q2, Q3), four with high
effort, and four essentially not at all** — and two of the unanswerable ones (Q7, Q8) are the
ones that generate "the mod is broken" confusion rather than mere friction.

---

## 3. Improvement proposals

### Tier 1 — low-cost: relabel, annotate, summarize (keep the current layout)

All items are string/label/tooltip/one-row changes inside `MissionsWindowUI.cs` +
`MissionComposition.cs`. No data-model change, no persistence change.

**T1.1 — Mission summary line in the header bar.** Fill the header's currently-blank
Start/End/TTL region (or add a second thin line under the title) with:
`Y1 D12 → Y1 D14 · 2d 3h · 3 vessels · 3 crew · Landed`.
*Answers Q1/Q3/Q5/Q10 at a glance, even collapsed.* Data: `MissionSpanStartUT` /
`MissionSpanSeconds` already computed (extract §7.7); vessel count = through-line count; crew =
union of interval `CrewNames`; outcome = terminal of the primary through-line's last interval.
**Data: already available. Cost: ~1 day.**

**T1.2 — Move/duplicate TTL onto the header row** and rename the column `TTL` → `Next launch`.
The countdown belongs to the mission, not to its first vessel row. **Cost: hours.**

**T1.3 — Delta-phrased interval labels.** Keep the composition string but stop repeating the
vessel name down the staircase. First interval: `Kerbal X (pod x1, probe x1, crew x3)`.
Subsequent intervals: lead with the StartEvent phrase + the peeled child's name when
resolvable, composition second:

```
└─▼ Kerbal X — pod x1, probe x1, crew x3          Launch      Decoupled
   ├─▼ after decouple: Kerbal X Booster left       Decoupled   Undocked
   │  ├─▼ after undock: lander departed            Undocked    Landed
```

The peel child's name is already in the same children list (steps 6–7 of the builder), so
naming it in the survivor's label is a local lookup. *Fixes F1's worst reading and F5
entirely.* **Data: already available. Cost: 1–2 days including label-width tuning.**

**T1.4 — Name the same-tree dock partner.** At a Dock/Board merge boundary, resolve the merge
leg's *other* parent via `RecordingTree.BranchPoints` (`ParentRecordingIds` has two entries on
a merge — design doc) and render `Docked with Munport Station` in the Start event cell (wider
cell or tooltip for overflow). *Fixes F4.* **Data: needs a small new derivation (branch-point
parent lookup), no new persisted data. Cost: 1–2 days.**

**T1.5 — Tooltips everywhere they're missing.** Include checkbox:
`"Include this segment in the mission's loop unit. Does not hide the ghost — playback is
controlled per recording (Recordings tab)."` Also: mission title (`double-click to rename`),
Log/Clone/Delete/Warp to/Loop/Archive, and the period cell's four states each getting a
one-line state name (`"Loop off"`, `"Auto period (Settings > Looping)"`, `"Period raised to
fit overlap cap"`, `"Period locked to Mun transfer window"`). *Directly patches F2's
teaching failure and F6's opacity at near-zero cost.* **Cost: hours.**

**T1.6 — Surface loop-conflict outcomes.** When `SetLoopEnabled` clears a sibling's loop, post
a `ScreenMessages` line: `"Loop moved to 'Munport Resupply' — one loop per tree."` The
information already exists at the call site (extract §7.14). **Cost: hours.**

**T1.7 — Retire the `#` column in Basic** (keep in Advanced if the index aids support
conversations); give the include checkbox its own unlabeled slot. Rename
`"Partner journey - X"` → `"Docked partner: X"` with tooltip explaining include-to-show.
**Cost: hours.**

Tier 1 total: roughly a week; every item ships independently.

### Tier 2 — moderate rework: per-vessel timeline rows + a real mission header

**T2.1 — Mission header becomes a two-line summary block.**
Line 1: name + button group (as today). Line 2 (narrative summary, plain label):
`Kerbin → Mun → Kerbin · 2d 3h · Jeb, Bob, Val · Loops every ~6.4d (Mun window) · Next: T-2d 4h`.
Body path from the recordings' segment phase labels (`GetSegmentPhaseLabel` already produces
`"Kerbin -> Jool"` strings on the Recordings tab). *Answers Q1/Q2/Q4/Q5/Q6 with zero
expansion.* **Data: available + one derivation (body path union across members). Cost: 2–3 days.**

**T2.2 — Flatten the staircase: one row per physical vessel, events inline.**
Replace interval-as-children with **through-line-as-row** — the data model already has exactly
this abstraction (`MissionThroughLineBuilder`, extract §3.3: "one continuous controlled
vessel, merging all its legs"). Depth then encodes *only* separation lineage (booster under
the ship it left), never time:

```
1  Kerbal X Mission        Y1D12 → Y1D14 · 2d 3h · Jeb, Bob, Val · Landed          [buttons]
[x] └─▼ Kerbal X            Launch → drop booster → undock lander → Landed (Kerbin)
[x]    ├─ Kerbal X Booster   separated Y1D12 3:22 → Destroyed 9m later
[x]    ├─ Kerbal X Lander    undocked Y1D14 1:07 → Landed (Mun)
[x]    └─ Bob Kerman (EVA)   left Y1D14 2:15 → Recovered 29m later
```

One row per vessel/kerbal; the event chain renders as an inline `A → B → C` phrase built from
the vessel's interval boundaries (all data present per interval: StartEvent/EndEvent/UTs).
Vessel count on screen now matches the player's count. **Where do the interval checkboxes
go?** Expanding a vessel row (caret) reveals its intervals as flat, *equally indented*
sub-rows with checkboxes and the four date columns — the current selection granularity
survives, but as an opt-in detail instead of the default reading surface. Basic mode could
even hide interval sub-rows entirely and offer only per-vessel include (cascading a vessel's
exclusion over its interval keys is a trivial write-through).
*Kills F1, F5, F10; answers Q1/Q3/Q9.* **Data: already available (through-line view +
composition intervals). Cost: ~1–2 weeks — new row builder + reflowed selection UI + tests.**

**T2.3 — Chronological event digest (optional companion).** A collapsed-by-default
`Events (6)` foldout per mission listing boundaries in time order:
`Y1D12 3:22 — Booster separated · Y1D14 1:07 — Lander undocked · Y1D14 2:15 — Bob EVA …`.
This is the narrative view in its purest cheap form; every entry is an existing interval
boundary or peel UT. *Answers Q1 directly.* **Data: available. Cost: 2–3 days.**

**On phase-based top-level grouping** (Launch / Transfer / Surface / Return as the first
hierarchy level): possible via segment-phase labels, but I recommend **against** it as the
primary structure — phases don't nest cleanly across concurrent vessels (the lander is in
"Surface" while the orbiter is in "Orbit"), so it recreates the same relation-overloading
problem on a different axis. Phase belongs in row *coloring* (the Recordings tab's phase
palette already exists) and in the header's body path, not in the tree shape.

### Tier 3 — redesign: timeline / swimlane view

One horizontal lane per through-line (physical vessel), X = UT across the mission span,
intervals as colored bars, docks as lane convergence, peels/undocks as lane splits, kerbals as
thin ribbons riding the lane they're aboard:

```
Kerbal X Mission     Y1 D12 ├──────────────────────────────────────┤ Y1 D14        Loop [x] ~6.4d
                     Launch      drop       dock          undock        land
Kerbal X        ████████████╪═══════════╗▒▒▒▒▒▒▒▒▒▒╔═══════════════████  Landed
  Jeb ▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂
  Bob ▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂▂╲____EVA____╱▂▂▂▂▂▂▂▂▂
Booster         ────────────╪══╗ Destroyed
Munport Station ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒╝▒▒▒▒▒▒▒▒▒▒╚▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  (partner)
```

- Bar color = phase (reuse the Recordings tab palette: atmo blue, exo purple, surface orange).
- Click a bar = toggle that interval's inclusion (replaces the checkbox); hover = tooltip with
  times, events, composition, delta.
- Dock rendered as the partner lane touching the main lane for the docked span; undock as it
  leaving. Cross-tree partners are just another lane, sourced from `ForeignDockLink` +
  the foreign journey's intervals (both already derived, extract §4.4).

**IMGUI feasibility: genuinely fine.** This is `GUI.DrawTexture(rect, whiteTex)` with tint per
bar + `GUI.Label` + per-rect `GUI.tooltip` — no rich widgets needed. Layout math (UT →
pixel) is pure and cacheable per mission per frame, and the composition pipeline already
rebuilds per frame cached by `Time.frameCount`, so the pattern exists. Perf stays linear in
*visible* bars; collapsed missions draw only the header, satisfying the many-missions
constraint. The real costs are design ones: horizontal scale (a 2-hour launch next to a 2-day
coast needs either segmented time or per-phase zoom), hit-testing for selection, and lane
assignment for many concurrent vessels.

**Data-model support:** through-lines with Start/End UTs — available; intervals with
boundaries + events — available; kerbal ribbons — derivable from per-interval `CrewNames` +
EVA legs (approximate but honest); same-tree partner lanes — needs the T1.4 branch-point
parent derivation; **nothing needs new persisted data.**

**Cost: 3–5 weeks** including time-scale design, selection rework, and tests. Recommend it as
an *expandable per-mission detail view* (a `Timeline` foldout under the T2.1 header), not a
replacement for the row table — the table remains the accessible, sortable surface and the
swimlane becomes the comprehension view.

---

## 4. Recommendation — staged path

**Stage 1 (do now): Tier 1, all seven items (~1 week).** Highest confusion-per-dollar:
T1.5 (checkbox tooltip) and T1.6 (loop-conflict message) fix the two *actively misleading*
behaviors; T1.1/T1.2 (summary line + Next launch) fix the "what is this mission" blank; T1.3/
T1.4 (delta labels + named dock partner) make the existing staircase readable without touching
its shape. Every item is independently shippable and none blocks later stages.

**Stage 2 (next): T2.1 + T2.2 (~2–3 weeks).** The header summary block plus the through-line
flattening removes the staircase — the core mismatch — while keeping the interval-level
selection as an expandable detail. This is the stage that actually resolves the owner's stated
concern, and it stands entirely on existing derivations (`MissionThroughLineBuilder` +
`MissionCompositionBuilder`). Add T2.3 (event digest) only if playtesting still shows Q1
friction after T2.2.

**Stage 3 (evaluate, don't commit): Tier 3 swimlane as an optional foldout.** Prototype it for
one mission after Stage 2 has soaked. If Stage 2's inline event phrases + summary header
answer Q1–Q5 in playtesting, the swimlane is a delighter, not a need — spend the 3–5 weeks
only if complex multi-dock station missions remain illegible.

**Do independently of all stages:** resolve the F2 ownership question at the design level.
Either (a) Basic mode gets *some* playback-visibility control on the Missions tab (e.g. the
vessel-row checkbox also writes `PlaybackEnabled` in Basic), or (b) the include checkbox is
explicitly labeled as loop-only everywhere it appears. Option (a) is a behavior change needing
its own design pass (it couples mission selection to per-recording playback state); option (b)
is Stage 1's T1.5. Ship (b) now, decide (a) deliberately.

The through-line abstraction was built for exactly this ("one continuous controlled vessel")
— the presentation just never caught up with the data model. Stage 2 is where they meet.
