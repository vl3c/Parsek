# P9 Y1 fairing-fix confirm flight

Lane PPS-20c (untracked spec; deferred `WarpToUT` budgets 540 s, `budgetSeconds` 3000,
`verboseLogging` on), branch `p9-y1-rig-after` at 1f26c8647, leased copy `stock-minimal`. Run
`2026-11-28_0404_PPS-20c-reentry-rig-stock`: PASS attempt 1, 578 s.

DLL sha256 `c68b701e39123eef60df362ba6ab683257e443686a32bec19b161d48fcb8ee92` (equal to the
build; utf16 `carried off`, `pixelsChangedByOcclusion=` present). Re-checked before launch.
No `[ReentryFx]` WARN or ERROR.

`contact-real-before-after-fairing.png`: rows stock real vessel (this flight), BEFORE ghost
(y1-before, main) and AFTER ghost (this flight); columns 72 km, Mach 6, 3.75, 3, 2.5, 2.

## The heat shield fairing

- Recorder, at the booster separation (ut about 49.5): `Part event: ShroudJettisoned
  'HeatShield1' pid=3827787522` and `Jettison shroud of 'HeatShield1' pid=3827787522 carried off:
  carrier=OtherVessel carrierPart='Decoupler.1' (stock re-parented it onto the bottom-node part;
  isJettisoned stays false)`.
- Replay: `apply family=ShroudJettisoned surface=jettison-panel rec=1 pid=3827787522 applied=1`.
- The m6 and m375 capture reports now list five meshes: 'Cylinder', 'base', 'canopy', 'cap',
  '125'. The 'fairing' mesh is gone.
- Ghost probe rect in the no-fire frames equals the real one: 72 km `[524,282,36,87]` both;
  Mach 2.5 `[524,352,36,17]` both; Mach 2 real `[524,352,36,17]`, ghost `[524,351,36,18]`.

## Look against stock (side camera)

- The V streak at the base is gone at Mach 6, 3.75 and 3.
- 72 km, Mach 2.5, Mach 2: no flame on either side. Match.
- Mach 6: same orange colour, same streaky shell, same trail direction, the same base width,
  about the same length and brightness as the real probe's flame.
- Mach 3.75: same colour, direction and length; a little wider and brighter than stock's.
- Mach 3: faint on both, similar.

## Timing (uncapped; mark ghosts onMs/offMs/deltaMs, sheaths drawn)

```
above 1 1.439/1.469/-0.030 0 | 4 1.400/1.417/-0.018 0 | 8 1.385/1.409/-0.024 0
m6    1 2.056/1.776/+0.280 1 | 4 2.249/1.495/+0.754 4 | 8 2.746/1.495/+1.251 8
m375  1 1.892/1.542/+0.350 1 | 4 2.301/1.527/+0.774 4 | 8 2.778/1.671/+1.107 8
m3    1 1.852/1.498/+0.354 1 | 4 2.424/1.521/+0.903 4 | 8 2.711/1.499/+1.212 8
m25   1 1.562/1.487/+0.076 1 | 4 1.449/1.460/-0.011 1 | 8 1.416/1.455/-0.040 1
m2    1 1.494/1.499/-0.005 1 | 4 1.454/1.456/-0.002 1 | 8 1.412/1.448/-0.036 1
```
