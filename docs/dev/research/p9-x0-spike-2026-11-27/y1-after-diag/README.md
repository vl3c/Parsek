# P9 Y1 after-diag flight (aero FX diagnostics at m6 and m375)

Lane PPS-20c (untracked spec; deferred `WarpToUT` budgets 540 s, the validator's cap,
`budgetSeconds` 3000; the spec already sets `verboseLogging = true` on both legs, unchanged),
branch `p9-y1-rig-after` at 218497290, leased copy `stock-minimal`. Run
`2026-11-28_0126_PPS-20c-reentry-rig-stock`: PASS attempt 1, 580 s.

DLL sha256 `9a24529243a0cccb641007db8ed8287855f391a1928c110bf6ed3de6282f4b57` (equal to the
build; utf16 `fxr-diag files written=`, `diag mode restored to Normal`, `fxCopyRenders=`,
`Ghost aero FX camera state: ` and `after(Y1: every ghost draws its own sheath` all present).
Re-checked before launch.

Files: `fxr-diag-{m6,m375}-{fx,plain,depth,sanity,nodepth}.png`, the normal real / ghost / diff
frames at m6 and m375, `log-FxReentryRig-ReentryFx.txt` (every `[FxReentryRig]`, `[ReentryFx]`
and `Ghost aero FX` line, untruncated), `result.json`, `KSP.log.gz`.

## Reading of the diag pictures (same at m6 and m375)

- **fx** (offscreen render, stock replacement shader, ghost values, no scene depth): a stock-like
  orange flame shell trailing up from the probe, about the size and colour of stock's real flame.
  Report: covered 1.78 % (m6) / 1.52 % (m375), maxRGB (255,224,86) / (255,255,110).
- **plain** (offscreen, no replacement, magenta): the probe's silhouette in solid magenta at the
  right screen position (260 px).
- **depth** (velocity depth map 0, 2048): one red disc in the centre on black, the ghost seen
  along its velocity (red 0..241, 4.0 % differ from the corner).
- **sanity** (on screen, unlit magenta): NO magenta on screen. Only the dim probe body and the
  watch-mode label.
- **nodepth** (on screen, no scene depth): no flame on screen, same as sanity.

So the material, the per-ghost values, the meshes, their matrices and the depth map all work when
rendered offscreen, but nothing the Parsek ghost aero FX camera draws reaches the screen. The
normal ghost shots at m6 and m375 are still dark (`sheath=Draw`; probe-rect diff 434 / 439, the
same as the first after flight).
