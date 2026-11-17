# Spike S6: race mark survival, menu entry, create a save (<YYYY-MM-DD>)

Draft in the template of `r0-spikes.md` section 6.4. After the PC run (`PC-RUN.md`) it is
filled in and moved to `docs/dev/research/mp-spike-S6-<YYYY-MM-DD>/README.md`. Every
`<...>` is a value read from the raw artifacts; each Measurements row names the log line
(`[Parsek][INFO][Spike.S6] ...` in KSP.log, read through `check_mark.py log`) or the CSV
column (`<instance>\parsek-spike\S6\*.csv`, moved to `$R`) that gives it.

Verdict: <PASS | FAIL | PARTIAL>. <One sentence.>

## Question

Does a scenario node whose module type is not installed survive every way a save is
rewritten (stock and Parsek), what is the earliest hook that can read it, can a main menu
entry be cloned with keyboard navigation, can a race save be created and started from a
typed dialog, can the stock difficulty panel be opened over that dialog and its values
read back, and where the tech tree the game loads and each part's gameplay values can be
read. Settles 6.2 and 7.2; waits: R1-03, R1-10, R1-14, R1-15, R1-19, R1-20. It also
answers the in-game VERIFY items of `r1-mode-and-setup.md` section 3.8 (the decompile
answered the others there).

## Decides

Design 6.2 (race mark) and 7.2 (menu and setup, race content table). Waiting tasks:
R1-03 (mark), R1-10 (create), R1-14 (menu, dialog), R1-15 (difficulty panel or presets),
R1-19 (content table), R1-20 (content check); R2-02 (Kerbin check, the bodies row); the
in-game VERIFY items of `r1-mode-and-setup.md` 3.8 (rows V1-V6 below).

## Build

spike/mp-r0 at <sha, `$R\spike-commit.txt`>; deployed DLL sha256 <hash,
`$R\deployed-dll-sha256.txt`>; instances `spike-r0-content` (lane, step (f)) and
`spike-r0` (main menu, hand steps); KSP 1.12.5, DLCs yes (both, junctioned), mods
<from `automation\spike-r0-content\provision-manifest.json`: 000_Harmony, ModuleManager
4.2.3, ClickThroughBlocker, ToolbarControl, KSPCommunityFixes, CommunityDeltaVMaps,
kRPC, TestingTools, MechJeb2, KRPC.MechJeb, Parsek>.

## Procedure as run

1. Build with `-p:SkipKspDeploy=true`, re-provision `spike-r0-content` with
   `--parsek-dll`, copy the DLL into `spike-r0`, pin the hash (PC-RUN 1-2).
2. Compose the template with `make_template.py` (the committed `cs-career-mun4` career,
   its three shared craft, `template\mark.cfg` inserted at GAME depth) and stage it as
   the operator-local fixture `spike-s6-mark` (PC-RUN 3). Change from the plan: the
   template is composed rather than committed a second time (4.9 MB fixture).
3. Lane `SPIKE-S6-mark-survives` on `spike-r0-content` with `PARSEK_SPIKE=S6`, with
   KSPCF, then with `GameData\KSPCommunityFixes` moved out of the instance and back
   (PC-RUN 4-5). Change from the plan: the lane runs on `spike-r0-content`, not
   `spike-r0`, because the template is a stock-tree career (as RW-5); Discard Re-fly
   is driven by the lane (`ReFlyRevert discard`), not done by hand.
4. Main menu AUTO runs on `spike-r0`: edit, then cancel (PC-RUN 6-7). The four planned
   edits are set on the panel's own `GameParameters` (its private `gPars`) and the panel
   is closed through its private `Dismiss(bool)`; the auto resume repeats stock's
   load-dialog call sequence without the `KSPUpgradePipeline` step.
5. Content runs on `spike-r0-content`: clean, ReStock, ReStock + ReStock+, two patches
   (PC-RUN 8-12).
6. HAND steps H1-H7 on `spike-r0` (PC-RUN part E).
7. Not run: the crash-reconcile path (`RunTestBatchCrashReconcile`), which needs a
   killed in-game test batch; <run by hand or listed as a follow-up>.

## Measurements

Lane rows read `lane1-kspcf\check-log.txt` / `check-tree.txt` and the same for
`lane2-nokspcf`; the savewatch rows are also `savewatch.csv` (columns `trigger`, `file`,
`game`, `marks`, `hash`).

| # | Measure | Value | Pass number | Result |
|---|---------|-------|-------------|--------|
| M1 | Mark in `persistent.sfs` after the seam LoadGame + SaveGame | `savewatch ... file=persistent.sfs ... marks=<n> hash=<h>` | marks=1 hash=33f1d41c85676b3f | <> |
| M2 | Mark in `quicksave.sfs` (seam quicksave) | `savewatch ... file=quicksave.sfs ...` | same | <> |
| M3 | Mark after quickload and re-save | savewatch after `MissionMark s6-quickloaded` | same | <> |
| M4 | Mark after Rewind to Launch (Parsek's rewind save copies) | savewatch lines after `invokerewindtolaunch invoking` | same | <> |
| M5 | Mark in the rewind point `.sfs` | `savewatch ... file=Parsek/RewindPoints/<rpId>.sfs ...` | same | <> |
| M6 | Mark after Discard Re-fly (`RevertInterceptor` writes persistent.sfs) | `DiscardReFly: rewind point game written to persistent.sfs`, then the next `savewatch ... persistent.sfs` | same | <> |
| M7 | Mark after Revert to Launch | savewatch after `reverttolaunch complete ... situation=PRELAUNCH` | same | <> |
| M8 | Every written file, run with KSPCF | `check-tree.txt`: `tree: files=<n> failed=<f>` | failed=0, result=PASS | <> |
| M9 | Every written file, run without KSPCF | lane2 `check-tree.txt` | failed=0, result=PASS | <> |
| M10 | Lane verdict, both runs | `Classify verdict=<v> scenario=SPIKE-S6-mark-survives` | PASS, PASS | <> |
| M11 | Earliest hook that reads the mark, per entry way | `earliest cycle=<c> source=<create/LoadGameCfg> hook=<h> scene=<s>`; `hooks.csv` columns `hook`, `scene`, `mark` | a hook at or before `Game.Start` | <> |
| M12 | `HighLogic.CurrentGame` is the entered game at `onGameSceneLoadRequested` | `hook ... name=onGameSceneLoadRequested ... game=<id> cycleGame=<id>` | game == cycleGame | <> |
| M13 | Proto added before `Game.Start` instantiated in its target scenes (V1) | `proto-load module=SpikeS6ProbeScenario ... instantiated=True`, `probe OnLoad scene=SPACECENTER`; for the mark `proto-load module=ParsekRaceSpikeMark ... instantiated=False` with marks=1 kept | True, and the mark kept | <> |
| M14 | Menu clone shows (V4) | `menu clone ok stageTwo=<a>-><b> index=<i> label=Multiplayer order=[...]`, `menu clone label after 60 frames=Multiplayer`, H1 screenshot | b=a+1, label Multiplayer | <> |
| M15 | Clone takes a mouse click (H1) | `menu tap via=mouse` | present | <> |
| M16 | Clone takes arrows + Enter (H2; driven: `auto: nav reached the clone after Down x<n>`) | `menu clone hovered=True`, `menu tap via=enter arrowsUsed=True` | present | <> |
| M17 | Text field takes a pasted 200-character path (V2, H3) | `dialog folder changed len=0->200 equalsPrimed=True`; driven `auto: folder input set=True readBackLen=200 equals=True` | 200, True | <> |
| M18 | Save created and started from the dialog | `create save=<s> folderLen=200 folderEqualsPrimed=True mode=CAREER ... mark=1 probe=1`; `create.csv` | mark=1 | <> |
| M19 | Created save resumes with the mark (driven resume and H6) | `resume: LoadSFSFile ... nodeMarks=1`, `earliest ... source=LoadGameCfg`, `sc-arrival ... mark=1` | 1 | <> |
| M20 | Panel opens over the dialog (V3) | `difficulty shown panel=GameDifficulty ... dialogAlive=True ... panelOnTop=<b>`, H4 screenshot | True | <> |
| M21 | Four changed values come back in the callback's object (V3) | `difficulty accepted=True sameObject=<b> changedFromDefaults=<n> [...]`; `difficulty.csv` rows `phase=callback-accepted` | n=4, the four planned keys, sameObject=True | <> |
| M22 | The other 36 keep their defaults | the same line's list holds only the four keys | 36 unchanged | <> |
| M23 | The four reach the created game at the Space Center (V3) | `difficulty check phase=sc-created planned=4 problems=<p> result=<r>` and `phase=sc-after-resume`; `difficulty.csv` rows `phase=sc-created` | problems=0 PASS, both phases | <> |
| M24 | Cancel leaves the defaults (AUTO and H5) | `difficulty accepted=False ... changedFromDefaults=0`; `difficulty check ... choice=Cancel ... movedFromDefaults=0 result=PASS` | 0 | <> |
| M25 | Stock difficulty window of the created save shows the four | H4 screenshot, looked at | four values as set | <> |
| M26 | Dialog and panel follow the UI scale (H7) | `dialog open ... uiScale=<u> lossyScale=<l>`, `difficulty shown ... uiScale=<u> panelLossyScale=<l>` at 1.0 and 1.5; screenshots | lossyScale grows with uiScale | <> |
| M27 | `FlightGlobals.Bodies` at the main menu (V5, R2-02) | `bodies scene=MAINMENU count=<n> home=<b> homeRadius=<r>`; `bodies.csv` | 17, Kerbin, 600000 | <> |
| M28 | Main-menu tree equals the R&D building's | `rnd-tree source=rnd-open ... vsMainMenu=<n> result=<r>`; `content-<run>\rnd.txt` | 0 differences, PASS, every run | <> |
| M29 | The two patches show as differences | `diff-clean-vs-patches.txt` | basicRocketry cost 77, liquidEngine maxThrust 999 | <> |
| M30 | ReStock changes no gameplay value and moves no node | `diff-clean-vs-restock.txt`, `diff-clean-vs-restockplus.txt` | none (else listed for the owner) | <> |
| M31 | Dump holds every part of the four sets | `content mainmenu label=<run> ... parts=<n> partsNoConfig=<n> partUpgrades=<n> experiments=<n> ...`; `content.csv` | partsNoConfig=0, all counts above 0 except partUpgrades | <> |
| M32 | ReStock+ parts whose variants change gameplay values | `content-restockplus\variants.txt` | (recorded) | <> |
| M33 | Stock ships a `PARTUPGRADE` node | `content mainmenu label=clean ... partUpgrades=<n>` | (recorded) | <> |
| M34 | No patch target missing (both builds' reflection) | `patch summary applied=6 missing=0`; no `patch target missing` | 6, 0 | <> |
| M35 | Crash-reconcile rewrite | not run (needs a killed batch) | - | NOT RUN |

Pass rule (copied from `r0-spikes.md` S6): the node is byte-identical (after whitespace
normalisation) in every file written by every path, with and without KSPCF; the menu
entry shows, takes a mouse click and arrow keys plus Enter; the dialog's text field takes
a pasted 200-character path; the created save loads with the mark present at the
reported hook (a proto added before `Game.Start` is instantiated in its target scenes);
the difficulty panel opens over the dialog; all four changed values, one per part, come
back in the `onDismiss` lines, the other thirty-six keep their defaults, and all four are
the values of the created game's parameters at the Space Center; Cancel leaves the
defaults; and the panel and the dialog follow the stock UI scale; `FlightGlobals.Bodies`
is populated at the main menu; the tree read at the main menu equals the one the R&D
building shows, both patches show as differences, and the dump holds every part of the
four sets.

## What the design must change

<section, old wording, new wording; or "nothing">

## Fallback taken

<none, or which one and why>. The fallbacks, copied from `r0-spikes.md` S6: the node is
dropped by a path: the mark moves to a side file in the save folder or into Parsek's own
scenario node (raise to the owner, since 6.2 chose a separate module so older builds keep
it). Menu clone fails: an IMGUI button on the main menu, drawn through the UI-scale host.
Difficulty panel fails (it does not open over a mod dialog, or its `DifficultyParams`
and `CareerParams` values do not come back): the setup dialog offers the four stock
presets instead (Easy, Normal, Moderate, Hard; owner, 2026-11-15, question Q-17), and
R1-15 takes that shape. If only the two custom nodes do not come back in the callback's
object, the panel stays (it covers the first two parts) and R1-10 reads the two nodes
with `CustomParams<T>()` from the callback's `GameParameters`, which the decompile shows
is the object the panel edits; the spike re-runs the step with that read. If they still
do not come back, or do not reach the created game, the presets replace the panel,
because a race cannot leave part of its difficulty unrecorded; the spike report says
which case holds. Tree read (f) differs from the R&D building's: the check of R1-20
moves to the first Space Center entry, before any save is written, and reads
`AssetBase.RnDTechTree.GetTreeNodes()` there.

## Raw artifacts

`<umbrella>/logs/spikes/S6/<YYYY-MM-DD>/`: `spike-commit.txt`,
`deployed-dll-sha256.txt`, `build-dll.txt`, `dev-dll-before.txt` / `-after.txt`,
`template\`, `template-check.txt`, `lane1-kspcf\` and `lane2-nokspcf\` (KSP.log, the
produced save, `parsek-spike-S6\` with the CSVs and `snapshots\`, `check-log.txt`,
`check-tree.txt`), `auto-edit\`, `auto-cancel\`, `content-clean\`, `content-restock\`,
`content-restockplus\`, `content-patches\` (dumps, `rnd.txt`, `variants.txt`),
`diff-clean-vs-*.txt`, `restock-folders.txt`, `hand\` (KSP.log, `shots\`,
`check-log.txt`, `check-tree.txt`); console logs in `<umbrella>/logs/console/<date>/`
(`spike-S6-*.log`). Curated into the report folder: the two lanes' `check-tree.txt`, the
content dumps of each step (f) run (R1-19's generator input), the screenshots.

## Follow-ups

<new todo or owner question, if any>. Known before the run: M35 (crash reconcile) is not
covered by the lane; the probe scenario type `SpikeS6ProbeScenario` is registered with
`ScenarioCreationOptions.None` even when the spike is unarmed (it is never added to a
game unless the armed Create adds it).
