# Analysis: Should the Kerbals window move into Career State as a 5th tab?

**Context:** after shipping the Career State window (#416), the question raised is whether the existing Kerbals window should collapse into it as another tab, to reduce main-window button count and put "career-scoped info" in one place.

**Recommendation: KEEP SEPARATE.** The button-count saving is marginal, and the consolidation regresses the freshly-shipped Fates→Timeline flow more than it cleans up the main window. There are better tools for the underlying concern ("minimize main-window complexity"). Details below.

---

## The core trade

| | **Consolidate (Kerbals → 5th tab)** | **Keep separate** |
|---|---|---|
| Main-window buttons (Flight) | 6 | 7 |
| Main-window buttons (KSC) | 4 | 5 |
| Windows open at once | 1 (Career State only) | 2 (Kerbals + Career State) if the user wants both |
| Fates → Timeline scroll workflow | User must select Kerbals tab first, then click row. Two user actions before the cross-link fires. | Click Fates row — instant scroll. Windows can sit side-by-side. |
| Design-intent alignment | Reverses the doc (`docs/dev/plans/career-state-window.md §1`) and spec (`docs/dev/todo-and-known-bugs.md §416`) which explicitly split roster-scoped from career-scoped. | Matches stated intent. |
| Refactor size | ~704 LOC moved, tests adjusted, tab width recalibrated, per-tab scroll needed | Zero |
| IMGUI regression risk | Medium — width math, scroll-position behavior, tab label truncation all change | None |
| Discoverability (new user) | A user looking for "crew roster" searches tabs inside "Career State" — non-obvious grouping | "Kerbals" is its own button, topic-labelled |

## Against consolidation

1. **The design intent is explicit.** Both `docs/dev/plans/career-state-window.md §1` and `docs/dev/todo-and-known-bugs.md §1228` state that Kerbals is *intentionally roster-scoped* and that Career State exists precisely because those four modules are career-scoped. Merging them reverses a choice that was reasoned through in two separate doc passes.

2. **The fresh Fates→Timeline flow takes a hit.** Phase 4 (commit `2bac6694`) just shipped clickable Fates rows that scroll the Timeline. That flow's sweet spot is a user with **Kerbals + Timeline side-by-side**: see a Fates row, click, watch Timeline scroll. Merged layout forces the user to (a) open Career State, (b) click the Kerbals tab, (c) click the row. The feature still works, but its ergonomics are worse on day one. It's a bad trade to regress a brand-new shipped feature in exchange for a button-count tweak.

3. **The button-count benefit is small.** Main window today: 7 buttons in Flight, 5 in KSC. After merge: 6 / 4. That's one button. It doesn't transform the main-window visual budget. See Alternatives below for bigger wins.

4. **Content dimensions don't match cleanly.**
   - Kerbals default window: 320 × 400 px.
   - Career State default window: 820 × 400 px (needed for the 4-tab toolbar and the multi-column row layout introduced in Phase 2b).
   - A unified window at 820 × 400 means Kerbals content gets stretched out across wider space than it was designed for (indented tree structure with section headers that were tuned to 320 px). A 900 × 400 to fit the 5th tab cleanly makes the mismatch worse.
   - Shared scroll position stops working cleanly across tabs with very different content heights. Current Career State uses a single `careerStateScrollPos`; merged would need `Vector2[]` per tab.

5. **Scope label drift.** "Career State" as a name stops being literal if one tab is a crew roster. Either rename the window (loses the clear identity we just built) or the Kerbals tab sits there as a conceptual outlier.

6. **Refactor surface area exceeds payoff.** ~704 LOC moved, three constructor call-sites adjusted in ParsekUI/Flight/KSC, existing KerbalsWindowUITests need their instance construction pattern updated, tab-switch scroll behavior reworked. All for -1 button. The error bar on "did I regress anything?" is wider than the gain.

## For consolidation (weaker)

1. Both windows already share the same cache-invalidation channel (`LedgerOrchestrator.OnTimelineDataChanged`), so unifying cache management is trivial.
2. The "how's my career doing?" mental-model *could* be argued as a single question — roster + facilities + contracts + strategies are all one save-scoped picture.
3. A single window is a simpler mental model for an occasional player who doesn't want to find two things.

These are real but not decisive. (1) is a plumbing convenience, not a user-facing benefit. (2) ignores that the doc already considered and rejected this framing. (3) is soft and symmetric — some players want fewer buttons, others want fewer tabs to hunt through.

## Better alternatives for "minimize main-window UI complexity"

If the underlying goal is fewer buttons on the main window, there are higher-ROI moves that don't touch two working windows:

1. **Demote `Settings` to a gear icon in the window title.** Settings is a rare click; a small gear in the top-right of the main ParsekUI window frees the whole button row. -1 button, zero functional change, zero refactor.

2. **Group InFlight-only buttons under a single "Flight Tools" header.** `Real Spawn Control` and `Gloops Flight Recorder` are both InFlight-only. A single collapsible mini-section (`▾ Flight tools`) wrapping both drops visual noise during the KSC-to-Flight transition when the button set suddenly grows. Could also include a compact mini-button row instead of two stacked wide buttons.

3. **Tighten `SpacingLarge` (currently 10f) to 5-6f.** Pure vertical-rhythm tweak. Reclaims ~20-30 px across the button column without removing anything.

4. **Move `Gloops Flight Recorder` entirely into the Recordings window** as a header button next to filters. Conceptually it's "manual recording controls," and the Recordings window is where committed recordings already live. This is a larger refactor but genuinely consolidates manual-capture UX.

## What I'd actually ship

None of the above right now — this PR (#330) is large enough already and the polish agenda should wait for playtesting feedback. But if the user comes back saying "the main-window clutter is real, do something," **option 1 (settings gear)** is the highest payoff per hour.

## Related v2 items (if we ever do consolidate)

If a future Parsek version goes the "one career dashboard" direction, the right move is probably a bigger rethink: one consolidated window with a left sidebar listing sections (Contracts / Strategies / Facilities / Milestones / Kerbals / Recordings Overview) rather than a flat 5-tab bar. That's a design-pass feature, not an incremental #416 follow-up.

## Summary

- **Don't consolidate now.** The design explicitly split roster-scoped from career-scoped, Phase 4's Fates→Timeline ergonomics would regress, and the button-count win is small.
- **If main-window clutter is the real pain**, a settings-gear promotion or a Flight-tools mini-group reclaims more space without touching two working windows.
- **Revisit as a v2 redesign** if we ever do a "one career dashboard" sidebar pass.
