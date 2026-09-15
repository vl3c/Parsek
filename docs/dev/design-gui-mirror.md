# The GUI mirror (`harness/tools/gui_mirror.py`)

One self-contained HTML page that behaves like Parsek's GUI, built entirely out of
the census captures. It exists so a layout question ("is that column under its
heading?", "what does the Roster tab look like with three stand-ins?", "what did
this window look like before the fix?") can be answered by clicking, in a browser,
without booting KSP - and so the answer is always the game's, never a drawing of
it.

Status authority for the census program itself stays `docs/dev/autotest-status.md`;
the measured structural map of the windows stays `docs/dev/design-gui-inventory.md`;
the dump format is `docs/dev/design-gui-tree-dump.md`. This doc owns the mirror.

## 1. Generated, not written

The page contains no window layout, no control label, no tooltip text and no
colour that a human typed. Everything comes out of an artifact a census lane
produced:

| Fact on the page | Where it comes from |
| --- | --- |
| Control geometry (rects, column widths, insets, row stride) | `<label>.gui.json`, the `parsek-gui-tree/1` control tree |
| Control kind, style name, enabled state, toggle value, tooltip, text | the same dump |
| The colour of every control and every glyph | the matching `<label>.png`, sampled inside each control's own rect |
| Which window, tab, complexity mode and scene a capture IS | that run's `KSP.log` (`uiaction open/close/tab/rect/complexity/describe`, paired to `capturescreenshot ok label=`) |
| A tab's display name | the capture in which THAT tab was selected (`buttongrid.textValue`) |
| A modal's title and button labels | `uiaction dialog open=true ... title=... buttons=A|B` in the log |
| Which dataset a capture is of | `fixture.saveTemplate` in `harness/scenarios/<specId>.toml` |
| The Compare notes | `CHANGELOG.md` (current version), the `GUI-*` entries of `docs/dev/todo-and-known-bugs.md`, and the merge commits |
| The Compare numbers | measured off the two dumps being compared |

Two consequences are worth stating because they are the point of the design:

* **The mirror cannot drift.** There is nothing to update when a window changes;
  there is only a census to re-fly. A window that changed and was not re-flown
  shows its last capture with the run id it came from, which is the honest answer.
* **A click with no capture behind it does nothing but say so.** The state graph
  has an edge only where the destination capture exists. Everything else flashes
  the control and writes `no capture for this state yet` in the status line. The
  page never invents a screen, because a plausible invented screen is worse than a
  gap: a gap sends you to fly a lane, an invention sends you to fix a bug that is
  not there.

`EndToEndRenderTests.test_the_generator_types_no_window_text_of_its_own` is the
mechanical guard on the first claim: it renders a synthetic capture and then
asserts the generator's own SOURCE does not contain the strings the page showed.

### Two things the generator does contain

The CSS skin (window chrome, borders, row metrics, the fallback greys) and the
glyph metrics. The metrics are calibrated rather than guessed: the ink extent of
three text runs in `bdk-kerbals-roster-expanded-advanced.png` measures
136 / 144 / 87 px, and Arial at 13px renders them at 136.6 / 144.5 / 86.8, so the
mirror uses 13px. Getting this wrong is visible at once - the dump's rects were
laid out by KSP's own font, so a font that is 8% wide overflows cells the game
fits, and the photo toggle is how it was caught.

### Why colours are sampled from the PNG

The dump records a style NAME, not a colour. The Kerbals Roster tab's clickable
rows are blue, its fold headers white and its idle kerbals grey, and all three are
`kind=label, style=label` in the dump - indistinguishable. Rather than encode a
palette rule that would be a guess about the product, the generator reads the
background and the ink out of each control's own rect in the frame the tree was
dumped on. Status tints and disabled greys come along for free, and a colour the
product changes changes in the mirror on the next census with no code edit.

## 2. The label-to-state grammar

Census labels are `<host>-<window>[-<tab>][-<state>]-<mode>`:

```
ksc  - main                        - advanced
ksc  - missions - recordings       - advanced
cek  - career   - facilities-level0- advanced      (tab facilities, state level0)
ib   - logistics- linkpicker       - advanced      (no tab; linkpicker is a state)
b1   - main     - disabledecho-spawncontrol-advanced
dlg  - wipemilestones                              (no window token, no mode)
scope- map-all-hidden                              (no window token, no mode)
```

`parse_label` resolves it against vocabularies the logs produced, never against a
typed table: `<window>` counts as a window only if the seam ever reported opening
it, and `<tab>` only if the seam ever reported selecting it ON THAT WINDOW. What
is left is the state, joined with `-`. A host whose second token is no known
window (`dlg-`, `scope-`) yields `window=None` rather than filing a modal under a
window it never stood over.

**The log wins where the two disagree.** The label is a filename; the seam's own
`uiaction` lines are what was on screen. One case where they differ on purpose: the
Kerbals rebuild renamed the second tab's HEADING to `Flights` while its seam token
stayed `outcomes`, so `bdk-kerbals-flights-folded-advanced` is
`tab=outcomes, tabAlias=flights, state=folded`. Reading the label alone would file
one tab as two.

## 3. The fallback rule

A window is shown from the selected dataset when that dataset has a capture of it.
Otherwise the page falls back to the first fixture, in the order the runs were
ingested, that does have one - and says so in the status line, naming both
fixtures. Silence there would be the one way this page could lie about what it is
showing.

The default dataset is derived, not pinned: the fixture that photographed the most
DIFFERENT windows at the Space Center, which on the present corpus is the
operator's own `c1-gui` career. "Most captures" would pick whichever lane happened
to be longest.

## 4. Before / after (the Compare view)

`key = (fixture, window, tab, state, mode, scene)`. Every capture of one key is
sorted by `capturedUtc`; BEFORE is the earliest, AFTER the latest, and the pair is
reported as CHANGED only when the two trees differ once the sampled colours are
stripped out (a colour-only difference is a screenshot difference, not a layout
one). Both sides are drawn by the same renderer off their own dump, so a
difference visible on the page is a difference in the game.

`scene` is in the key deliberately. The same window at the Space Center and in
flight is two pictures, not a change; pairing them reported three false changes
before the facet was added.

Beside each pair the page prints:

* one WHAT and one HOW, from the CHANGELOG entry of the current version that names
  that window most directly (title match first, then `Changed` before `Fixed`
  before `Added`), truncated - with every matching entry in full behind a fold;
* one WHY, from the struck `GUI-*` todo entry's `Fix:` line;
* the ids of the OPEN `GUI-*` entries that name it, so a window with no change
  shows what is still filed against it rather than just "no change yet";
* the merge commits whose diff touched this window's OWN UI source - the PR
  numbers, with the file that earned each one. Matching merge subjects and bodies
  by name instead listed about thirty PRs per window, most of which never went near
  it;
* and the numbers MEASURED off the two dumps: node counts, text-line counts, and
  the worst header-to-cell `dx`/`dw` per column, which is the same measurement the
  alignment todo entry tabulates. Measuring beats quoting: if a later change
  re-breaks a column, the page says so on the next census without anyone editing a
  record.

Attribution is still by name-match, so it is generous rather than precise: an entry
naming two windows appears under both. Three filters keep it usable - a record must
name the window in its title (or a todo's id or opening sentence) or name it twice;
a window with a stable captured title is matched on that title's words rather than
on the seam token; and a window whose title VARIES with its subject (Structure is
titled by the route it shows) is matched on the token alone, because its titles
name kerbals and vessels. Generous-but-filtered is the right failure direction for
a page whose job is to put the record next to the picture.

## 5. The tooltip echo strip

Hovering any control writes its real tooltip into the strip of the window the
control is in - the same box the game writes it into - and clears it on the way
out. The strip is identified from the capture, not from a per-window table: it is
the last empty-text `label` with style `box` in the window, which is exactly what
`TooltipEchoBox` draws (one permanently visible box-styled label, empty when
nothing is hovered), and its own rect gives the line count (38 px = two lines,
23 px = one).

Overflow follows `TooltipEchoBox` too: text that does not fit the strip's line
count is NOT ellipsised - the box switches to one unwrapped line and scrolls it,
which the mirror reproduces with a CSS translate. The page-level echo line under
the stage is kept as well, because it is readable when the window's own strip is
scrolled off the top of a tall capture.

## 6. Size budget

The page must be ONE file with no external requests, so the photos are inlined and
therefore rationed. 16 MB is the ceiling; `--budget-mb` moves it and the tool exits
2 rather than writing an over-budget page.

Photos are cropped to the bounding box of the Parsek windows in that frame (the
scenery behind them is not what anyone is comparing), re-encoded with each colour
channel floored to a multiple of 8 - invisible on a flat IMGUI skin, roughly half
the bytes - and the encoder walks
`(quant 8, full size) -> (16, full) -> (24, full) -> (24, half)` until the payload
fits. On the 192-capture corpus the first step fits: about 8.7 MB of photos and
about 6.2 MB of control trees, 14.9 MB total.

The PNG codec is stdlib `zlib` + `struct` (read, crop, subsample, re-encode);
`harness/` is stdlib-only and Pillow is not available.

## 7. Foreign windows

Another mod's window was on screen when the census ran and is in the dump. A root
is Parsek's when its `(x, y, w)` matches a rect the seam APPLIED to a window, in
ANY run - collected globally on purpose, because one lane that opened a window
without re-placing it must not demote that window everywhere (that bug hid the
main window from the whole page once). Of what is left, a root repeating
identically under four or more different windows is another mod's chrome (the kRPC
server window, the MechJeb menu button) and is hidden behind the `other mods`
toggle. A root seen under one window only - a group picker, the Gloops recorder,
the watch-mode overlay - is Parsek's and stays.

## 8. Regenerating

The page is NOT committed; the generator, its tests and this doc are. Regenerate
after a census, pointing `--shots` at every shots directory worth including and
`--repo` at the checkout the Compare notes should be read from:

```bash
python harness/tools/gui_mirror.py \
  --shots "<...>/results/<runId>_<specId>_shots" \
  --shots "<...>/results/<runId>_<specId>_shots" \
  --repo . --out gui-mirror.html --index gui-mirror-index.json
```

Later runs of the same lane may be passed alongside earlier ones; that is what
populates Compare. `--no-photos` drops the photo toggle and the dialog crops and
makes the page about a third of the size.

`gui-mirror-index.json` is the coverage record without the geometry: captures per
window, states per window with their fixtures, the known-but-uncaptured states, and
the before/after key table. It is the thing to read when the question is "what is
covered" rather than "what does it look like".

## 9. Coverage as of the 2026-09-15 build

192 captures over 7 fixtures and 12 windows, from 16 census runs (GUI-1 through
GUI-11).

| Window | Captures | Distinct states | Datasets |
| --- | --- | --- | --- |
| main | 56 | 27 | all 7 |
| missions (incl. Recordings) | 39 | 14 | all 7 |
| timeline | 33 | 10 | 6 |
| kerbals | 22 | 14 | 5 |
| career | 19 | 11 | 4 |
| logistics | 7 | 6 | 3 |
| structure | 5 | 3 | 3 |
| gloops | 4 | 1 | 2 |
| spawncontrol | 3 | 1 | 1 |
| settings | 2 | 2 | 1 |
| testrunner | 1 | 1 | 1 |
| GuiTree probe | 1 | 1 | 1 |

15 keys have both sides and differ (Missions/Recordings alignment and both group
pickers, Structure on a mission, all four Career tabs, the Kerbals rebuild on
Roster and Flights, Spawn Control, and the main window in flight with ghosts).

Nine states are known to the seam and have no capture, and all nine are the same
shape: a tab selected in BASIC mode (Missions' own tab, two Timeline tabs, both
Kerbals tabs, all four Career tabs). Basic draws no tab bar, so the census never
took them. They are listed in the left rail, greyed, and clicking one says why.

## 10. What this page is not

* Not a status authority. `docs/dev/autotest-status.md` owns the census's status
  and `design-gui-inventory.md` owns the structural map.
* Not a substitute for a census. It can only show what was photographed; a window
  changed and not re-flown reads as unchanged, which is why every capture carries
  its run id on the page.
* Not a player-facing surface, and not a new UI surface in Parsek: it is a harness
  tool that renders artifacts, and it ships no game code.
