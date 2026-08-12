# The Dres program: a downloaded craft flown to Dres, recorded, replayed — and the defect that found

**Base: `main`.** Five steps, twenty flights, one product defect found and fixed, two follow-ups filed.

The program's question was narrow: *can the harness fly a human-built craft off KerbalX to another planet, and does Parsek record and replay that correctly?* The answer is yes on both counts, and getting there produced one real Parsek fix plus three harness-side defects in copied constants.

---

## What ships

| Artefact | What it is |
|---|---|
| `craft/Duna Rocket.craft` → `bdock-forge-base/Ships/VAB/` | KerbalX (Steltuck), pure stock 1.12.1, 92 parts, committed **byte-identical** to its download (sha256 `f664d7ce…5fc2`). No MechJeb edit — capability is install-side. |
| `FORGE-b18-dres-pad` + `fixtures/saves/b18-dres-pad` | The pad fixture. Sixth consumer of `forge_station` + `harvest_bdock_station` — no new mission, no new tool. |
| `B18-dres-lko-ascent` | Ascent-works proof. Reuses `b2_lko_ascent` unchanged. |
| `B19-dres-orbit` + `b19_dres_orbit` | Kerbin→Sun→Dres transfer, capture, mid-mission commit. Carries the suite's first **pre-transfer JETTISON phase** and first **target-SOI approach warp clamp**. |
| `fixtures/saves/dres-orbit-recorded` | B19's green flight, harvested `--keep-parsek`. |
| `V9-dres-player-loop` | The loop-unit lane. Found the defect; now the regression floor for its fix. |
| `V10-dres-loop-arrival` | The final measurement: tilt disposition, arrival geometry, ready line. |

Two shared machine additions, both **default-off** so every existing lane is byte-identical: the JETTISON phase and the approach warp clamp.

---

## Per-scenario measured evidence

### B18 — ascent (PASS attempt 1, 334 s; confirmed 334 s)

Orbit **82,927 × 76,893 m, ecc 0.00444, inc 0.090°**. Reproduced to ~0.03%: the confirmation run read 82,939 × 76,875, ecc 0.004460, and **`lf` residual 2,240.00 units on both** — exactly the craft file's Mk1-fuselage total, i.e. the ascent spends **zero** nuclear propellant. Recordings pinned {3,3}: 1 main + 2 radial-booster debris, `ProcessBreakupEvent` firing exactly twice.

Derived: LV-N stage 18.220 t wet / 7.020 t burnout → **7,483 m/s** at Isp 800 s.

### B19 — Dres orbit (PASS attempt 1, 2,981 s)

Six runs. Two were **operator-killed** (both read `PARSEK-FAIL`/`MISSION-FLAKE` with `ConnectionResetError` — what killing KSP produces, not evidence about Parsek); one was rejected pre-boot by spec validation; three were genuine flakes that each bought a finding.

Final: park **1,110.4 × 1,089.7 km, ecc 0.0084, ORBITING at Dres**, commit `terminalOrbitBody=Dres`, **3,933 m/s** still aboard after a 1,717.4 m/s capture. Jettison: 2 pops, hand-off at `avThr=60000.000` (the LV-N by thrust signature), `lf=2240.000`.

### V9 — loop unit (PASS; armed)

Pre-fix: FAITHFUL, `transferMemberSegs=0`, cadence = span. Post-fix: **`ENGAGED re-aim Kerbin->Dres via Sun`**, `supported=True target=Dres`, `cadence=22,785,806.611915223` = exactly **2 × synodic**, `loiterCuts=1 cutSeconds=8,436,248`.

### V10 — final measurement (PASS; armed)

```
tilt-correction inc-before=13.1958 bound=5.5000 targetInc=5.0000
                incAch=3.6426 inc-after=NaN state=retained reason=unreachable-plane

synth geometry: departUT=31276743 arrivalUT=43162645 soiEntryUT=43144578
                | xfer-vs-Kerbin@depart=0m | xfer-vs-Dres@arrival=0m
                | xfer-vs-Dres@soi=32832839m (SOI=32832840)

re-aimed transfer ready: departUT=31276742.826743945 tof=11885901.821472509
                devFromRecorded=0s soiEntryUT=43144577.773317166 encounter=Dres
```

At Dres's **5°** the achievability gate is unsafe exactly as at Eve's 2.1° — and the tilt-retention fix's third arm **retains** rather than declining. This is that fix's first probe beyond Eve, and it holds. The re-aimed conic meets Dres's SOI **to within one metre**.

---

## Findings ledger

| # | Finding | Status |
|---|---|---|
| 1 | **`OPTIMIZER-SPLIT-DEFEATS-REAIM-CLASSIFIER`** — the load-time optimizer split an interplanetary recording at its on-rails SOI handoff (an `ExoPropulsive` label on a *packed* checkpoint section), so the re-aim classifier found no member holding the whole transfer and a real Kerbin→Dres mission replayed FAITHFUL. | **FIXED** on `dres-split-cohesion` (stacked PR) |
| 2 | An **already-`Assigned` kerbal cannot be seated** by `launch_vessel`, and it fails *silently* — an empty pod, an uncontrollable rocket, and the kerbal left `state=Missing`. The committed `gs1-two-stage-pad` fixture has the same empty pod; GS-1 never noticed because its craft is probe-controlled. | Fixed in this program's fixture; the GS-1 observation recorded |
| 3 | **`planTimeoutSeconds` is game seconds** and PLAN-CORRECTION is entered off a ×100,000 coast — one poll frame (measured 39,001 / 41,001 game s) expired a 300 s budget before MechJeb got a frame to plan. Both correction rounds returned `nodeDv=nan`; no encounter ever formed. | Fixed (300 → 300,000) |
| 4 | **The coast's final warp step swallowed an entire SOI approach.** The native warp stopped exactly where it promised (`tts=29.904 warp=NONEx1.000`) and the *next* poll advanced 27,596 game s across the boundary *and* the ~25,000 s approach (`ttPe=-9,741`). Clamping in-SOI factors could never help. | Fixed (lead ≥ 2 warp frames + a new approach ceiling) |
| 5 | An untested **`soiLeadSeconds` schema max of 3,600** forbade every value large enough to do the job — 3,600 game s is ~4% of one full-rate poll frame. | Fixed (max → 1,000,000) |
| 6 | `REAIM-CLASSIFIER-FRAGILE-TO-MEMBER-SPLITS` — the classifier still needs the whole transfer in one member, and the preserved burn-split contract means the failure can recur. | **Filed**, with Design C's viability and a cheap "make it loud" interim |
| 7 | `RECORDER-LABELS-ON-RAILS-CHECKPOINTS-EXOPROPULSIVE` — the label that caused #1 is still emitted. | **Filed**, deliberately not acted on |

Findings 3–5 are all defects in constants **copied from B16** rather than tolerance choices — a pattern worth noting for the next lane author.

---

## Honest deviations

- **Two B19 runs were killed by me**, not by the product. Both read `PARSEK-FAIL`; both are `ConnectionResetError` kill artifacts. The first kill prevented a destructive jettison (a blind 4-pop count would have shed the LV-N's own drop tanks and fired the pod decoupler); the second was a budget re-size after measuring a phase cost the original sizing missed.
- **The jettison was redesigned mid-program.** A fixed pop count cannot be right: how many stages separate the park from the transfer engine depends on how far autostage already walked the list, which depends on park altitude. B18's 80 km park left the Mainsail live (4 stages); B19's 700 km ascent left the **Skipper** live (2). It is now evidence-driven — pop, observe, stop on the live-thrust signature.
- **Phase 2's dev-instance load check** was delivered by the V9/V10 flights on the automation instance rather than an interactive dev boot I cannot drive unattended. Same code path, greppable logs.
- **V10 does not arm the seam-endpoint census**, and the trade is stated in the spec: reaching `evaluated=1 outsideSoi=0` (measured twice) requires a pre-D0 jump, and every pre-D0 jump reproduces the **filed V8 line-blink detector gap** (three placements tried, same `sinceFrames=1 body=Sun`). The armed lane is the green one; the same arrival claim is carried by the synth-geometry token.
- **The TS lane (V8T pattern) was skipped** by token discipline — no tilt or census finding made Dres-specific TS evidence necessary. Recorded in the status doc row.
- **A sibling worktree clobbered the automation DLL twice** mid-program. Caught both times by the pre-flight hash check; every reported flight ran a hash-verified DLL.

---

## Verification

`dotnet test` **20,184 passed / 0 failed / 1 skipped**. Harness: lib **1,284** · missions **1,496** · provision **237** — all OK.

Regression flights, all PASS with no movement: V8, V8F, V8T, V6M, V2.

Status doc: scenario total 100 → 105, Live-proven 57 → 62.
