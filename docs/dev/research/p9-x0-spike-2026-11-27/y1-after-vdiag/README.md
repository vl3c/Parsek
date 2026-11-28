# P9 Y1 V-streak diagnostic flight

Lane PPS-20c (untracked spec; deferred `WarpToUT` budgets 540 s, `budgetSeconds` 3000,
`verboseLogging` on), branch `p9-y1-rig-after` at e6427561b, leased copy `stock-minimal`. Run
`2026-11-28_0332_PPS-20c-reentry-rig-stock`: PASS attempt 1, 577 s.

DLL sha256 `a90e267982d34edfdbe1d66bb3fca3c9d5ab6a7c0e0f19fccbb90dbf9da316b5` (equal to the
build; utf16 `pixelsChangedByOcclusion=`, `behindMap=`, `sheathsThisFrame=` present). Re-checked
before launch. No `[ReentryFx]` WARN or ERROR.

The full capture reports are the two `Ghost aero FX capture 'fxr-diag-m6'` /
`'fxr-diag-m375'` lines in `log-FxReentryRig-ReentryFx.txt`.

## Per-mesh probe (m6 / m375)

| mesh | shader | fxPx m6 / m375 | fxSum m6 / m375 | behindMap m6 / m375 |
|---|---|---|---|---|
| 0 'Cylinder' | KSP/Bumped Specular | 13011 / 6326 | 631339 / 260240 | 0.00 / -0.04 m |
| 1 'base' | KSP/Alpha/Cutoff Bumped | 0 / 0 | 0 / 0 | 0.19 / 0.18 m |
| 2 'canopy' | KSP/Alpha/Cutoff Bumped | 0 / 0 | 0 / 0 | 0.31 / 0.31 m |
| 3 'cap' | KSP/Alpha/Cutoff Bumped | 0 / 0 | 0 / 0 | 0.43 / 0.42 m |
| 4 '125' | KSP/Diffuse | 10972 / 12593 | 390015 / 250263 | -0.09 / -0.12 m |
| 5 'fairing' | KSP/Diffuse | 18785 / 11073 | 1218239 / 560605 | -0.21 / -0.25 m |

Header: `velocityCam(slot0) box=32.4 far=50.0`, `slot0SetGlobals=True`, `reversedZ=True`,
`sheathsThisFrame=2` (m6) / `1` (m375). Every mesh sampled the same depth-map texel
(1023,1023, raw 91, mapD 32.16 m). The depth map is a small dark-red disc (red 0..92, 0.034 % of
the 2048 map differs from the corner). Occlusion: `pixelsChangedByOcclusion=6660` (m6) /
`5841` (m375).

## Which mesh forms the V

Most likely mesh 5 'fairing' (KSP/Diffuse, 480 verts): it gives the largest flame sum at both
marks (about half the total at m6), and it sits 0.2 to 0.25 m in front of the depth-map
surface, so occlusion never trims it. Mesh 4 '125' is the next candidate (also in front of the
map). The three chute meshes (base, canopy, cap) sit behind the map and give no flame at all.
