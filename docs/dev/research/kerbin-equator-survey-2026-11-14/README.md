# Kerbin equator terrain survey, 2026-11-14

Spike S1 of `docs/dev/design-competitive-multiplayer.md` (section 7.3): is there dry land for
three more space centers on the KSC's latitude, a half turn and the two quarter turns away?

Measured on the owner's PC with `harness/tools/kerbin_equator_scan.py` against the
provisioned stock-minimal instance (KSP 1.12.5, kRPC 0.5.4 `surface_height`), Space Center
scene of a throwaway sandbox save. About 88,500 samples in 11 seconds. Heights are metres
above sea level; a negative height is sea floor.

Files: `summary.txt` / `summary.json` (the tool's own shortlist), `coarse.csv.gz` (every
0.1 degree of longitude on 11 latitude rows from -0.57 to +0.43), `ksc_fine.csv` and
`fine_*.csv` (0.025 degree grids, 7.5 km around the KSC and around each shortlisted spot),
`biomes.csv`.

## Findings

- **Half of the KSC latitude is dry.** Dry stretches at least one degree long, by longitude:
  -180.0 to -131.8, -131.5 to -129.4, -113.1 to -107.1, -88.8 to -74.5 (the KSC's own
  coast), -41.2 to -37.4, -12.4 to -11.3, 1.0 to 5.6, 18.2 to 107.9, 110.0 to 111.8,
  119.6 to 122.5, 176.8 to 179.9.
- **The KSC itself:** pad 72.4 m, runway 68.4 m; mean 65.9 m within 1.5 km of the footprint
  centre. Stock terrain around it is not flat: the height range is 415 m within 3 km and
  the sea takes 35 percent of the 5 km circle. The KSC stands on a flattened patch, so
  natural flatness is not required of a site, only dry ground under the patch.
- **Opposite point (105.36 E): dry.** The exact point is rough upland at 270 to 400 m.
  Lower and gentler ground lies 1.3 degrees east (106.7 E): mean 135 m, range 121 m within
  3 km, dry within 5 km. The coast is at 107.9 E.
- **Eastern quarter point (15.36 E): open sea.** The nearest land is 3.4 degrees east
  (18.8 E), a plateau at about 520 m (range 102 m within 3 km). The nearest low ground is
  on the other side, 11.6 degrees west (3.8 E): mean 143 m, range 234 m.
- **Western quarter point (-164.64): dry highlands** at about 650 m, rough at the exact
  point (range 290 m within 3 km). A flatter spot lies 2.3 degrees east (-162.3): 676 m,
  range 87 m. Ground as low as the KSC's is 32 degrees away (-132.4: mean 69 m).

So a site can stand within 0 to 3.4 degrees of every ideal point, but none of those spots
is near the KSC's 66 m: the natural ground there is 135 m, about 520 m and about 650 m.
Ground at the KSC's height exists only far from two of the three points. Whether the sites
keep their local height, are cut down to the KSC's height, or move to low ground is an owner
decision (design section 13).

## Caveats

- `biomes.csv` is NOT reliable. `biome_at` labelled several dry spots at 190 to 630 m as
  "Water" and put Badlands beside the KSC, which stock Kerbin does not have. Treat every
  biome name in this folder as unverified; biomes are spike S2 and need an in-game check.
- The heights are stock terrain as the game computes it at any distance (the terrain
  function, not loaded scenery). They include the stock KSC's own flattened patch.
- The shortlist in `summary.txt` scores flatness first; the text above re-reads
  `coarse.csv.gz` for the nearest and the lowest dry ground instead.
