# Spike S3: the four looks, replacement sets, a missing DLC, a suit on a ghost kerbal (<YYYY-MM-DD>)

Verdict: OWED (draft). The code, the two test sets, the run sheet and the summarizer are
ready. Every measured cell below is OWED until `PC-RUN.md` has been run on the owner's PC.

This draft becomes `docs/dev/research/mp-spike-S3-<YYYY-MM-DD>/README.md` (plan 6.4). Fill
it from `summary-s3.md` (run sheet step 23), the screenshots and the notes of steps 8 and
10.

## Question
What each of the four stock suits looks like on the player's own roster; the four UI
colors; whether two `SUITCOMBOS` replacement sets load; how a game without a DLC loads a
kerbal saved with that DLC's suit; how to put a suit's texture on a ghost kerbal without
editing shared materials. Settles 7.4 (table, colors) and A4's shape. Waits: R5-08,
R5-10, R5-11, R5-12, R4-10; soft: R5-02 (A4), R5-04, R5-07, R5-09.

## Decides
- Design 7.4: the look table (suit, combo, EVA part per look) and the four UI colors.
- A4's shape: per-ghost material copies, what they cost, and who destroys them (R5-02).
- R5-08 / R5-10 / R5-11 / R5-12: dressing the roster, replacement sets, the no-DLC load.
- R4-10: the four colors on the map.

## Build
- `spike/mp-r0` at OWED (it must contain `spike/mp-r0-s3`).
- Deployed DLL sha256: OWED (`$RS\deployed-dll-sha256.txt`, one line per instance).
- Instances `spike-r0` (DLCs: OWED) and `spike-r0-nodlc` (no Breaking Ground, no Making
  History), KSP 1.12.5, mods: OWED (from each instance manifest), plus the two test sets
  in `GameData\ParsekSpikeS3` of both.
- Env: `PARSEK_SPIKE=S3`, `PARSEK_TEST_COMMANDS=1`.

## Procedure as run
As in `spikes/r0/S3/PC-RUN.md`, steps 0 to 24, three KSP sessions (about 118 minutes, of
which 5 minutes are by hand: step 8, the Astronaut Complex suit picker, and step 10, IVA).

1. Session A (`spike-r0`, no sets): the stock combo count at the main menu.
2. Sets installed in both instances; session B: combo count with the sets, a new sandbox
   `SPIKE-S3-LOOKS`, dress phase 1 (crew 0 to 3 in Vintage, Future, Slim, Default; two new
   applicants in the two test sets), saved as `s3-phase1` and loaded; Astronaut Complex
   shots and the picker by hand; Kerbal X launch; IVA by hand; EVA of three kerbals
   (logged and photographed); the map background shot; the ghost kerbals; dress phase 2
   (test sets on two of the crew), saved as `s3-phase2`, loaded, two EVAs.
3. Session C (`spike-r0-nodlc`): `s3-phase1` loaded from a copy; roster and Astronaut
   Complex; launch; EVA of the saved-Vintage kerbal; the ghost kerbals again; quit (it
   saves) and the written save collected for the write-back check.

Changes from the plan (each with its reason):
- **Ghost kerbals are built directly, not played from a recording.** No committed fixture
  holds a recorded EVA (`eva2-lko-crewed` has 0 recordings), so `BuildGhostKerbal` builds a
  one-part vessel node per EVA part through the production ghost builder
  (`TryBeginTimelineGhostBuild` / `AdvanceTimelineGhostBuild` / `CompleteTimelineGhostBuild`),
  10 plain builds and 10 build-plus-swap builds per part, and photographs the dressed rows.
- **The tests run in a batch, by category.** The seam's `RunTests` selects by category and
  skips `AllowBatchExecution = false`, so the tests are batch-enabled and the flight-only
  ghost test has its own category `SpikeS3Ghost` (plan 6.2 named one test file).
- **Dressing is saved to its own file.** Batch isolation restores the batch-start state
  (a flight batch quickloads it back), so `ApplySuitToRoster` saves `s3-phase<N>.sfs` and
  the sheet loads it before looking.
- **Colors are sampled from the suit textures in game**, not by hand from screenshots
  (the screenshots are still taken; a hand `colors.csv` can override, step 23).
- **The test sets are flat recolors** (bands plus a corner mark) on the stock layout keys,
  not a recolored stock atlas: stock art cannot be committed.

## Measurements
### From `summary-s3.md`
| Measure | Value | Pass number | Result |
|---------|-------|-------------|--------|
| `COMBOS`: extra combos, baseline / with sets | OWED / OWED | grows by 4 (2 sets x 2 genders) | OWED |
| `DRESS`: dress steps, phase 1 (6) and phase 2 (3) | OWED | every step `ok=true` | OWED |
| `LOOKS`: each of the four suits on EVA with its own part | OWED | all four | OWED |
| `NODLC`: save loads, each kerbal's suit and EVA part logged | OWED | loads; fallback named | OWED |
| `WRITEBACK`: suit in the no-DLC written save | OWED | reported (`suit-kept` / `suit-rewritten`) | OWED |
| `GHOST-SHARED`: prefab shared material id and texture after 10 builds | OWED | unchanged | OWED |
| `GHOST-COST`: worst swap median per ghost build (ms) | OWED | <= 2.0 | OWED |
| `GHOST-PARTS`: EVA parts found / sphere fallbacks, DLC | OWED | 8 / 0 | OWED |
| `GHOST-PARTS [nodlc]` | OWED | reported | OWED |
| `COLORS`: contrast vs map background, pairwise hue | OWED | >= 3:1 each; >= 30 deg apart | OWED |
| Screenshots | OWED | 16 or more | OWED |

### The look table (7.4)
| Look | `KerbalSuit` | EVA part (male / female) | Sampled color | Contrast vs map | Fallback without its DLC |
|---|---|---|---|---|---|
| Default | Default | `kerbalEVA` / `kerbalEVAfemale` | OWED | OWED | none (always installed) |
| Slim | Slim | `kerbalEVASlimSuit` / `kerbalEVASlimSuitFemale` | OWED | OWED | none (always installed) |
| Vintage | Vintage | `kerbalEVAVintage` / `kerbalEVAfemaleVintage` | OWED | OWED | OWED (predicted Default) |
| Future | Future | `kerbalEVAFuture` / `kerbalEVAfemaleFuture` | OWED | OWED | OWED (predicted Default) |

### Ghost cost per EVA part (from the `ghost part:` lines)
OWED: part, found / fallback, build median and max (ms), swap median and max (ms),
materials, renderers, roles matched, texture ok; both instances.

### Hand notes
- Step 8 (picker): do the two test sets appear, with their icons, for both genders? OWED.
- Step 10 (IVA): does an IVA or portrait show a combo's texture? OWED (predicted no).

## What the design must change
Known before the run (decompile of KSP 1.12.5 `Assembly-CSharp.dll`):
- **7.4, where combos come from.** The stock and default combos are serialized on the
  `SuitCombos` prefab; only `ExtraCombos` is rebuilt from `SUITCOMBOS` / `SUITCOMBO`
  config nodes at `OnGameDatabaseLoaded`. A replacement set adds combos; it cannot change
  the stock four.
- **A combo is name, displayName, suitType, gender, suitTexture, normalTexture, sprite,
  primaryColor, secondaryColor.** Validation rejects only an empty string for displayName,
  suitType, gender or name; a missing key passes (logs `Suit combo <displayName>/<name>
  validated.`). R5-11 must check its own sets.
- **Slim is always installed** (no DLC gate); Default and Slim never fall back.
- **The `suit` setter silently refuses a suit whose DLC is not installed**, the getter
  answers Default for it, and `ProtoCrewMember.Save` writes `suit` through the getter while
  writing `comboId`, `suitTexturePath`, `normalTexturePath` and `spritePath` verbatim. So a
  no-DLC game that saves turns a Vintage or Future kerbal's `suit` into Default for good,
  keeping the combo id (the `WRITEBACK` cell measures it). If confirmed, the plan's
  fallback applies: the race stores the look itself and re-applies it.
- **EVA texture swap:** `KerbalEVA.OnStart` uses the stock textures when
  `SuitTexturePath` is null; otherwise it loads `GameDatabase.GetTexture` (default on a
  miss) onto the helmet, body and neck-ring instance materials with texture scale
  (1, -1). A4 must use the same scale.
- **IVA and portraits use `textureStandard` / `textureVeteran` only** (`Kerbal.SetupKerbalModel`),
  so a combo's texture is predicted to show only on EVA (step 10 checks it).
- **The crew list clears a missing `SpritePath`** on the kerbal with a warning (a
  mutation, not a read).
- **A4: a ghost clone shares the prefab's materials** (`GhostVisualBuilder.PartInfoBuilders.cs:766`);
  a swap through `renderer.materials` creates per-ghost copies that A4 must destroy when
  the ghost is destroyed (the `material census:` line counts them).
OWED after the run: the fallback suit the no-DLC game names, the four colors, and any
part that fell back to a sphere.

## Fallback taken
OWED. Planned: if `WRITEBACK` reads `suit-rewritten`, the race stores the look separately
(`player.cfg`) and re-applies it; if a DLC EVA part is missing on the no-DLC instance, the
ghost uses the base suit model with the color only (A4 records the substitution).

## Raw artifacts
`<umbrella>/logs/spikes/S3/<YYYY-MM-DD>/`:
- `a-baseline-KSP.log`, `b-KSP.log`, `c-nodlc-KSP.log`, the seam responses per session.
- `b-parsek-spike-S3/` and `c-parsek-spike-S3/` (the combo, roster, color and ghost CSVs),
  `c-nodlc-persistent.sfs`.
- `shots/` (18 screenshots of the spike saves), `summary-s3.md`, `deployed-dll.txt`,
  `deployed-dll-sha256.txt`.
These are spike saves only; none of the owner's own saves is read. Curated into this
folder: `summary-s3.md` and the screenshots.

## Follow-ups
OWED. Owner questions raised by the draft:
- When a player without a DLC joins a race where a kerbal wears that DLC's suit, should
  that kerbal quietly wear the base suit, or should the race remember the look and give it
  back when the DLC is present?
- Which four colors stand for the four looks on the map and in the lists (the run proposes
  the sampled ones)?
- Should a ghost kerbal in a suit the player cannot load show the base suit in the right
  color, or the base suit unchanged?
