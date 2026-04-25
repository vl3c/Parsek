# Plan: Fix "ghosts stuck at pad" under short loop period — via min-period, cap=20, adaptive cadence

Branch: `fix/ghost-freeze-dense-overlap`
Worktree: `Parsek-fix-ghost-freeze-dense-overlap`
Date: 2026-04-18 (rev after user direction)

## Problem

With a short auto-loop period on a long recording (user-reported: period = 1 s
on a 164 s recording), the user sees a stack of ghosts near the launch pad
instead of a cascade along the full trajectory. The ghosts that DO exist are
all in their first few seconds — which for every KSP rocket is visually
indistinguishable from "sitting on the pad" (the recorded vessel needs 1-3 s
to clear launch clamps before any visible motion).

## Contributing factors (from prior investigation)

### Factor A — static-pad visual window at phase 0 (expected behaviour)

Recordings are trimmed of leading stationary points at commit time
(`RecordingStore.cs:276-284` via `TrajectoryMath.FindFirstMovingPoint` at
`TrajectoryMath.cs:86-112`, thresholds "altitude change >= 1 m OR speed >=
5 m/s"). Those thresholds are low: a typical rocket takes ~1-3 s to gain
1 m of altitude, so the first retained trajectory seconds still place the
ghost essentially on the pad. Any ghost at phase 0 looks "on the pad" for
1-3 s. This is correct — the recording literally captured the vessel there.
Nothing in this fix tries to hide or shift that window.

### Factor B — the per-recording cap plus newest-cycle retention

`GhostPlaybackEngine.MaxOverlapGhostsPerRecording = 5`
(`GhostPlaybackEngine.cs:47`) is a hard ceiling on how many simultaneous
ghost clones can exist per recording in the flight scene. It's a pure
performance / memory ceiling (per-ghost rendering, FX, audio, positioner
cost multiplies per clone) — the log already shows `Playback frame budget
exceeded` at 5-8 concurrent ghosts. The cap says nothing about recording
length or trajectory correctness.

Under the cap, `GhostPlaybackLogic.GetActiveCycles` retains the N **newest**
cycles (`firstActiveCycle = lastActiveCycle - maxCycles + 1`,
`GhostPlaybackLogic.cs:279-280`). With period = 1 s and cap = 5 at
currentUT = 100 s, the surviving cycles are 96..100, phases 4, 3, 2, 1, 0 s
— all inside Factor A's 1-3 s static-pad window. Every visible ghost looks
stuck. Older cycles that would be visibly mid-trajectory (phases 5 s .. 99 s)
are culled silently.

## Fix

Three coordinated changes. Together they guarantee:

- No cycle is ever silently culled mid-trajectory (the user's explicit
  "no ghosts disappear without explanation" requirement).
- Short user periods don't produce stacked-at-pad visuals, because the
  engine automatically reduces launch cadence instead of letting the cap
  trigger.
- The per-recording cap stays bounded to its existing memory/perf budget.

### Fix 1 — minimum user-requested loop period 1 s -> 5 s

Change `GhostPlaybackLogic.MinCycleDuration` (`GhostPlaybackLogic.cs:21`)
from `1.0` to `5.0`. Every caller that clamps a user-supplied period
against this constant (`GhostPlaybackLogic.GetActiveCycles:260`,
`ResolveLoopInterval`, and the UI period input) automatically picks up
the new floor.

Rationale: 1 s periods can't produce visually useful output on any
recording — they spawn a new cycle inside the static-pad window of the
previous one. 5 s is the smallest value that lets a typical rocket clear
the pad before the next cycle spawns, and is also the speed threshold
already used by `FindFirstMovingPoint` for first-motion detection —
semantically coherent.

Also enforce this as UI validation in the per-recording period input
(`RecordingsTableUI.cs`) and the global auto-loop period input
(`SettingsWindowUI.cs`). Typing or pasting values below 5 s clamps on
commit, with a VERBOSE log line explaining the clamp.

### Fix 2 — per-recording cap set to 10 in flight and KSC

Change `GhostPlaybackEngine.MaxOverlapGhostsPerRecording`
(`GhostPlaybackEngine.cs:47`) from `5` to `10`, and
`ParsekKSC.MaxOverlapGhostsPerRecording` (`ParsekKSC.cs:47`) from `20`
to `10`. Both scenes now share the same ceiling.

Rationale for 10 (not 20): the log at
`logs/2026-04-18_1106_ghosts-stuck-at-pad/KSP.log` already shows
`Playback frame budget exceeded` warnings firing at 5-8 live ghosts
(lines 15217, 15918, 16460, 16854) under the old cap of 5; 10 is a 2x
bump that stays close to the empirical frame-budget floor rather than
leaping past it. Fix 3 (cadence doubling) picks up the slack — users
who want denser cycles get progressively coarser effective cadence
instead of more simultaneous clones. KSC drops 20 -> 10 for consistency
and because cadence doubling equally protects KSC from unbounded cycle
counts.

The cap stays bumpable in a small follow-up after in-game perf
measurement with cadence doubling in place (post-fix the spawn/destroy
churn pattern changes and FX costs amortize differently).

### Fix 3 — adaptive cadence doubling when user cadence would exceed the cap

Add a pure-static helper:

```csharp
/// <summary>
/// Computes the runtime launch cadence for an overlap-looped recording so
/// that the number of simultaneously-live cycles never exceeds
/// <paramref name="maxCycles"/>. Starts from the user-requested period
/// (clamped to <see cref="MinCycleDuration"/>) and doubles it until the
/// theoretical concurrent-cycle count fits within the cap. Returns the
/// effective cadence in seconds.
/// </summary>
internal static double ComputeEffectiveLaunchCadence(
    double userPeriod, double duration, int maxCycles)
{
    double period = Math.Max(userPeriod, MinCycleDuration);
    if (duration <= 0 || maxCycles <= 0) return period;

    // Concurrent cycle count for period P and duration D is ceil(D / P).
    // Double until ceil(D / P) <= maxCycles.
    while (CeilingDiv(duration, period) > maxCycles)
        period *= 2.0;
    return period;
}

private static long CeilingDiv(double numerator, double denominator)
{
    // Inputs guaranteed positive; integer ceiling over double math.
    return (long)Math.Ceiling(numerator / denominator);
}
```

Call site: in `GhostPlaybackEngine.UpdateOverlapPlayback` (around
`:1020-1050`), replace the raw `intervalSeconds` passed to `GetActiveCycles`
with `ComputeEffectiveLaunchCadence(intervalSeconds, duration,
MaxOverlapGhostsPerRecording)`. The same call happens on the KSC side
(`ParsekKSC.cs:380`).

Behaviour for the user-reported case (period=1 s, duration=164 s,
cap=10 — with Fix 1 also in force):

- Fix 1 clamps user period 1 s to 5 s on input.
- Fix 3 starts at 5 s: ceil(164 / 5) = 33 — exceeds 10.
- Double to 10 s: ceil(164 / 10) = 17 — exceeds 10.
- Double to 20 s: ceil(164 / 20) = 9 — fits.
- Effective cadence = 20 s. 9 cycles live simultaneously, evenly
  distributed across the 164 s trajectory (phases ~0, 20, 40, ...,
  160 s). No cap exceed, no silent cull.

Edge cases:

- User period already large enough that cap is not threatened:
  ceil(D / P) <= cap on first check, return period unchanged (common case).
- Degenerate tiny duration: 164 s / 5 s = 33 cycles, but a 10 s
  recording at 5 s period is ceil(10/5) = 2 cycles — fits on first check.
- Very long recording, very short period: the doubling loop terminates
  when `duration/period <= maxCycles`. Worst case `log2(duration / (min
  period * cap))` iterations — a dozen iterations max for any plausible
  input.

The user-facing effect: "launch at half cadence" (user asked for every
5 s, engine delivers every 10 s, 20 s, etc. to fit the cap). The loop
period the user configured stays stored as-is; the runtime cadence is
auto-adjusted.

### Fix 4 — non-spamming observability for cadence adjustment

Requirement from user: "make sure we have non-spamming observability
into this". When the engine's effective cadence differs from the user's
requested period, emit exactly ONE log line per (recording id, user
period, effective cadence) tuple for the lifetime of the session.
Re-emit only when any of those inputs change.

Implementation: a `Dictionary<int, (double userPeriod, double
effectiveCadence)>` in the engine keyed by recording index. Each
overlap-playback frame, the engine checks whether the current
(userPeriod, effectiveCadence) differs from the last logged tuple for
that recording; if yes, emit one INFO line and store the new tuple.
This is tighter than `VerboseRateLimited` (which re-emits after its
rate-limit window), ensuring the log surfaces EACH cadence change
exactly once and never repeats the same decision.

Log format:

```
[Parsek][INFO][Engine] Loop cadence #{idx} "{vesselName}":
requested={requested}s duration={duration}s cap={cap}
effective={effective}s (cycles={m})
```

Emitted only when the tuple changes, so: once when the recording first
enters overlap playback, once if the user edits the period, once if
duration changes via rewind / edit. Steady-state: silent.

Clamp logs (Fix 1): when UI clamps a user-entered value below 5 s,
one VERBOSE line per edit on commit. Not per frame.

A future UI pass (not in this PR) can show the effective cadence next
to the user's configured value in the Recordings table, so users see
"requested 5 s -> running at 20 s" inline.

## Why this solves the reported bug

| Case | Before | After |
|---|---|---|
| period=1 s on duration=164 s | Cap=5, retain newest 5 cycles at phases 0..4 s, all in the static-pad window — stacked at the pad visual. | Fix 1: period clamps to 5 s. Fix 3: cadence doubled to 10 s. Fix 2: cap at 20 allows all 17 cycles. 17 ghosts evenly spread across trajectory phases 0, 10, 20, ..., 160 s. |
| period=5 s on duration=30 s | Cap=5, retain newest 5 at phases 0..4 s — same stacked-at-pad pattern. | Fix 3: ceil(30/5)=6 within cap=20 on first check — no adjustment. 6 ghosts at phases 0, 5, 10, 15, 20, 25 s, visually spread. |
| period=30 s on duration=60 s (currently typical) | 2 cycles, within cap — unchanged. | Unchanged. |
| period=1 s on duration=20 s (ultra-short fun case) | Cap=5, retain newest 5 at 0..4 s — stack. | Fix 1: clamp to 5 s. Fix 3: 20/5=4 cycles, within cap. 4 ghosts spread across 0, 5, 10, 15 s. |

No cycle is ever silently culled. Frame-budget safety preserved via
Fix 2's cap (never exceeded thanks to Fix 3).

## Files to touch

- `Source/Parsek/GhostPlaybackLogic.cs`
  - Line 21: `MinCycleDuration` `1.0` -> `5.0`.
  - New `internal static ComputeEffectiveLaunchCadence(userPeriod,
    duration, maxCycles)` near `GetActiveCycles`.
- `Source/Parsek/GhostPlaybackEngine.cs`
  - Line 47: `MaxOverlapGhostsPerRecording` `5` -> `20`.
  - `UpdateOverlapPlayback` (`:1020-1050`): call
    `ComputeEffectiveLaunchCadence` and use the returned cadence in
    `GetActiveCycles` and subsequent cycleDuration math.
  - Add the cadence-adjustment INFO/VERBOSE log line (one per
    recording per distinct tuple).
- `Source/Parsek/ParsekKSC.cs`
  - Line 380-ish: same helper call, same log line.
- `Source/Parsek/UI/RecordingsTableUI.cs`
  - Per-recording loop period input: clamp below 5 s on commit, log
    the clamp.
- `Source/Parsek/UI/SettingsWindowUI.cs`
  - Global auto-loop period input: clamp below 5 s on commit, log.
- `Source/Parsek.Tests/GhostPlaybackLogicTests.cs` (or a new file)
  - Unit tests for `ComputeEffectiveLaunchCadence` (see below).
- `CHANGELOG.md` — 1-line user-facing entry.
- `docs/dev/todo-and-known-bugs.md` — new numbered section (#443).

## Test plan

Unit tests on `ComputeEffectiveLaunchCadence`:

1. `WithinCap_ReturnsUserPeriod` — `(period=30, duration=60, cap=20)`
   -> 30. No adjustment.
2. `PeriodBelowMin_ClampedToMin` — `(period=1, duration=164, cap=20)`
   -> 10 (clamped to 5 then doubled once).
3. `ExceedsCap_DoubledUntilFits` — `(period=5, duration=1000, cap=20)`
   -> 80 (5 -> 10 -> 20 -> 40 -> 80; 1000/80 = 12.5, ceil 13, fits).
4. `CapOneDegenerate_DoublesUntilDurationFits` — `(period=5,
   duration=100, cap=1)` -> 160 (single cycle holds the whole
   trajectory).
5. `ZeroDuration_ReturnsMinPeriod` — `(period=5, duration=0, cap=20)`
   -> 5 (early-return on non-positive duration).
6. `ZeroCap_ReturnsMinPeriod` — `(period=5, duration=100, cap=0)` ->
   5 (early-return on non-positive cap).
7. `HugeDuration_Terminates` — `(period=5, duration=1e6, cap=20)` —
   finishes in under 20 doublings.

Existing `GetActiveCycles` tests pass unchanged (signature stable).

In-game manual verification:

1. Short-period dense overlap: record 60 s, set period = 5 s. Expect
   12 ghosts evenly spread 0, 5, 10, ..., 55 s. Each ghost has ~1-3 s
   static-on-pad window but only one at a time is IN that window; rest
   are visibly flying.
2. Stress test: record 164 s, set period = 1 s. Expect clamp to 5 s in
   UI input, engine auto-adjusts to 10 s; 17 ghosts spread across full
   trajectory. Log shows the auto-adjustment message.
3. Very long recording: record 1000 s. Set period = 5 s. Expect
   effective = 80 s, 13 cycles spread.
4. Regression: period = 60 s on 60 s duration. Single primary, no
   overlap. Unchanged behaviour.
5. Regression: period = 30 s on 60 s duration. 2 cycles, unchanged.
6. Regression: watch mode (PR #350) combined with cadence adjustment.
   Verify the cycle-index the user is watching is still valid after
   cadence changes.
7. Frame budget: on the dense case (17 ghosts per recording, one
   recording), observe `Playback frame budget exceeded` frequency vs
   main. Expected to worsen modestly; if regressions are severe, add
   an LOD follow-up.

## CHANGELOG entry (draft)

```
### Bug Fixes
- Ghost playback with short loop periods no longer stacks at the launch pad.
  The minimum loop period is now 5 s (was 1 s), and when the configured
  period would produce more simultaneous ghosts than the engine's
  per-recording cap (now 20, was 5), the launch cadence is automatically
  halved repeatedly until it fits — so ghosts spread visibly across the
  whole trajectory instead of being silently culled. A log line explains
  the cadence adjustment.
```

## docs/dev/todo-and-known-bugs.md entry (draft)

```
## ~~443. Short loop period stacks ghosts at the launch pad (dense-overlap cap + newest-cycle retention)~~

**Source:** user report `2026-04-18`, log at
`logs/2026-04-18_1106_ghosts-stuck-at-pad/KSP.log` (period=1s / duration=164s).

**Symptom:** under aggressive overlap (period << duration), the flight
scene shows 5 ghosts clustered at the launch pad and no ghosts past the
first few seconds of trajectory. User expectation: a staggered cascade
across the whole trajectory.

**Cause:** two factors compound. (A) The static-pad visual window is
1-3 s of every recording (trimmed only when altitude change >= 1 m or
speed >= 5 m/s, `TrajectoryMath.FindFirstMovingPoint` at
`TrajectoryMath.cs:86-112`). (B) `GhostPlaybackEngine
.MaxOverlapGhostsPerRecording = 5` plus `GhostPlaybackLogic.GetActiveCycles`
retaining the N newest cycles means at period=1 s every retained cycle
falls inside the 1-3 s window — stack. Mid- and late-trajectory cycles
are culled silently.

**Fix:** three coordinated changes.
1. `GhostPlaybackLogic.MinCycleDuration` 1.0 -> 5.0; UI clamps below 5 s.
2. `GhostPlaybackEngine.MaxOverlapGhostsPerRecording` 5 -> 20 (matches KSC).
3. New `GhostPlaybackLogic.ComputeEffectiveLaunchCadence` doubles the
   runtime cadence until `ceil(duration / cadence) <= maxCycles`; called
   in `UpdateOverlapPlayback` (and KSC equivalent). INFO log surfaces the
   adjustment so it's never silent. Stored user period is unchanged.

**Status:** ~~Fixed~~. Size: S.
```

## Risks

- **UI regression for users who previously configured period < 5 s.**
  Existing saves with e.g. period=2 s will clamp to 5 s on load. Log
  the clamp. Accept the one-time visible change as the right behaviour.
- **Cadence-doubling surprise.** User sets 5 s, observes effective
  10 s, may think their setting is broken. Mitigated by the INFO log;
  future UI pass can show inline.
- **Frame budget at 17-20 ghosts on one recording.** Existing zone-based
  LOD helps. If perf is bad, follow-up with FX / sampling-stride
  reductions for far-away cycles.
- **MinCycleDuration semantic shift.** Today `MinCycleDuration = 1.0`
  is also the "minimum safe denominator" for loop-phase math. Raising
  to 5.0 widens the defensive bound but should not change any callsite
  semantics — verify each `Math.Max(period, MinCycleDuration)` caller.

## Implementation checklist

1. [ ] Bump `MinCycleDuration` 1 -> 5 in `GhostPlaybackLogic.cs`.
2. [ ] Bump `MaxOverlapGhostsPerRecording` 5 -> 20 in
       `GhostPlaybackEngine.cs`.
3. [ ] Add `ComputeEffectiveLaunchCadence` pure-static helper in
       `GhostPlaybackLogic.cs`.
4. [ ] Wire the helper into `UpdateOverlapPlayback` and the KSC
       equivalent.
5. [ ] Add cadence-adjustment INFO/VERBOSE log (rate-limited).
6. [ ] UI clamps in `RecordingsTableUI.cs` + `SettingsWindowUI.cs`.
7. [ ] 7 unit tests in `GhostPlaybackLogicTests.cs`.
8. [ ] Full suite green.
9. [ ] Build, deploy, verify DLL contains new log string.
10. [ ] In-game: period=1 s on 164 s recording — confirm auto-adjust
        to 10 s and even spread. Watch-mode regression test.
11. [ ] CHANGELOG + todo-and-known-bugs entries in the same commit.
12. [ ] Commit, push, PR, request Opus review.

## Out of scope

- Phase-spread retention (from prior plan revision) — not needed
  because cadence-doubling prevents the cap from being reached in the
  first place.
- Per-cycle LOD / FX suppression — defer to a follow-up if the 20-ghost
  cap causes frame-budget issues in practice.
- Ghost-pooling shared-mesh architecture — long-term work.
- UI label "effective cadence = Xs" next to the user's period — nice
  to have, separate PR.
- Fixing the transient `playbackVel=(0,0,0)` fallback (harmless
  one-frame trajectory-math edge case).
