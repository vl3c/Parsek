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

`docs/dev/design-gui-state-gallery.md` owns the MOCKED-DATA half: an
automation-only seam that hands a window a synthetic view model so the real draw
code photographs states no fixture save can reach (the 17 hold clauses, the 12
reject reasons, the Career divergence banner, a lost kerbal, a supersede row).
The mirror's side of that contract is BUILT and is section 14 below: a mocked
capture declares itself in its own dump, is filed under `fixture = "mock"` so
Compare can never pair it with a real capture, carries a `MOCKED DATA` badge, and
is counted separately in `gui-mirror-index.json`. Sections 15 and 16 own the other
two halves of the review loop - the notes the owner's feedback comes back
through, and the one-window focus link the round is scoped by - and section 17
owns the three mechanical rules that stop a stale, an empty-hover or a
mislabelled capture reading as coverage.

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
| Where each tab label sits on its bar | measured in the frame: the bright runs inside the grid's own rect |
| A modal's title and button labels | `uiaction dialog open=true ... title=... buttons=A|B` in the log |
| Which dataset a capture is of | `fixture.saveTemplate` in `harness/scenarios/<specId>.toml`, or the dump's own `mock` block (section 14) |
| Whether a hover photographed anything | the pointer op's own `tooltip=` in that run's `KSP.log` (section 17) |
| The Compare notes | `CHANGELOG.md` (current version), the `GUI-*` entries of `docs/dev/todo-and-known-bugs.md`, and the merge commits |
| The Compare numbers | measured off the two dumps being compared |

Two consequences are worth stating because they are the point of the design:

* **The mirror cannot drift.** There is nothing to update when a window changes
  but one line of the layout-epoch table (section 17 (e)); there is otherwise only
  a census to re-fly. A state that was not re-flown after its window changed drops
  off the rail and stays reachable as a Compare BEFORE with the run id it came
  from, which is the honest answer.
* **A click with no capture behind it does nothing but say so.** The state graph
  has an edge only where the destination capture exists. Everything else flashes
  the control and writes `no capture for this state yet` in the status line. Only
  the home window (the seam's first window, `M.seamWindows[0]`, the one the close
  affordance returns to) launches other windows; elsewhere a control whose text
  spells a window's name, such as the Timeline's `Career` view button, is routed
  as a tab or state of its own window or flashed. The page never invents a screen, because a plausible invented screen is worse than a
  gap: a gap sends you to fly a lane, an invention sends you to fix a bug that is
  not there.

`EndToEndRenderTests.test_the_generator_types_no_window_text_of_its_own` is the
mechanical guard on the first claim: it renders a synthetic capture and then
asserts the generator's own SOURCE does not contain the strings the page showed.

### Three things the generator does contain

The layout-epoch table (section 17 (e)): per window, the instant its current
layout was first captured, typed because no capture can say when a re-layout
happened. Then the CSS skin (window chrome, borders, row metrics, the fallback greys) and the
glyph metrics. The metrics are calibrated rather than guessed: the ink extent of
three text runs in `bdk-kerbals-roster-expanded-advanced.png` measures
136 / 144 / 87 px, and Arial at 13px renders them at 136.6 / 144.5 / 86.8, so the
mirror uses 13px. Getting this wrong is visible at once - the dump's rects were
laid out by KSP's own font, so a font that is 8% wide overflows cells the game
fits, and the photo toggle is how it was caught.

### A container's colour is its own padding

The sampler probes only the points inside a node's rect and OUTSIDE every child's,
takes the median by luminance, and falls back to the whole rect when the children
cover it completely. A container's colour is the colour of the pixels its children
do not cover; probing the whole rect made a window take the colour of whatever
opaque child sat under the probe point. The Kerbals window of
`ksc-kerbals-outcomes-advanced` has a 404 px content box over its centre, so that
one capture painted `#292929` while every other Kerbals capture painted `#444444`,
and the same mechanism waited for any container whose sample point fell inside an
opaque child. A leaf has no children, so nothing about labels or buttons changed.

### Rich text

KSP draws a Unity rich-text subset in labels and the dump carries the raw markup -
the Kerbals outcome rows really do read `<b>Jebediah Kerman</b>`. Rendering that as
text shows the tags; rendering it as HTML hands a control's own string the run of
the page. So the four tags Unity supports (`b`, `i`, `color=`, `size=`) are
translated into spans on a whitelist, with the colour and size arguments pattern-
checked, and every other character - including any tag not on the list - lands as a
DOM text node. `innerHTML` is never used anywhere on the page.

### Why colours are sampled from the PNG

The dump records a style NAME, not a colour. The Kerbals Roster tab's clickable
rows are blue, its fold headers white and its idle kerbals grey, and all three are
`kind=label, style=label` in the dump - indistinguishable. Rather than encode a
palette rule that would be a guess about the product, the generator reads the
background and the ink out of each control's own rect in the frame the tree was
dumped on. Status tints and disabled greys come along for free, and a colour the
product changes changes in the mirror on the next census with no code edit.

### Why a tab bar's labels are measured too

A selection grid reports one rect and the SELECTED item's text. Three things
follow, and all three were got wrong once by assuming instead of measuring.

**The other tabs' names come from the captures where THEY were selected**, resolved
PER CAPTURE and newest-first: this capture's own text for its own tab, then the
same dataset and mode, then the same dataset, then anywhere. Resolving it once
globally and first-seen let a pre-rename heading from an older epoch win for every
dataset - the rebuilt Kerbals window rendered "Roster State" / "Mission Outcomes"
over a frame that reads "Roster" / "Flights". The window's own tab list - the
name the rail, the state header, Compare and the status lines give a tab - follows
the same rule: the NEWEST capture carrying the token names it (by capture time,
then run id), never the first one scanned. A note's exported `tab` stays the
token, which is what the seam and a re-flown lane still agree on.

**The selected cell is marked by the RECORDED INDEX when the dump carries one, and by
TOKEN otherwise.** Comparing NAMES is what lost the marker entirely once a name went stale:
no cell was selected at all, which is why names are not in the rule at either tier.

The two tiers are not redundant. The dump's `selectedIndex` (a `buttongrid`'s own selected
cell, since the 2026-09-21 wave) is the frame's own statement about itself, so it holds for
a grid the seam never drove - any `GUI.SelectionGrid` that is not a tab bar, where there is
no `uiaction tab` line to replay at all. The TOKEN tier is the fallback and stays
byte-identical for every capture taken before the key existed, which is every committed one:
it replays the seam's own `uiaction tab window= tab= index=` lines and matches the cell whose
token equals the window's resolved tab.

**The selected cell is the DARK, pushed-in one.** Measured on the
`cek-career-contracts` and `bdk-kerbals-roster` frames, the selected cell has no
top highlight (grey profile 130,30,32,35,40,43,44..60) while the unselected ones
carry the light top edge (14,102,88,78,68,41..59). A brighter selection is the
intuitive guess and the wrong one. The fills themselves are sampled per cell off
the frame, so only the edge is CSS.

**The label positions are measured too.** The product is not uniform about
alignment: the Kerbals bar centres its two labels while the Career bar
left-aligns its four in cells of the same 245 px stride. Centring everything put
the Career strip 87 px right of where the game draws it - measured against the
photo, which is what caught it. So `grid_label_runs` reads the bright runs inside
the grid's own rect out of the frame and the page places each label on its own run,
falling back to a centred equal split only where the run count and the tab count
disagree.

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

Two more consequences of the log winning, both visible in the rail. A capture
labelled `flight-spawncontrol-advanced` surfaces as **window=main,
state=spawncontrol**, because the seam never confirmed an open for that window on
that host - the window closes itself when nothing is in range, so the label names
a window that was not on screen and the log is right. And `parsek-guitree-probe`
files under a window token `parsek` for lack of any log entry at all: the probe
window is not a seam surface, so it has no token, and the host prefix is the only
handle the page has. It is shown in the mirror because it WAS photographed, and
left out of Compare because Compare is about the product's windows.

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

The default dataset is PINNED into the model by the generator, and pinned so that
it is never the mocked one. The RULE is still the same measurement - the fixture
that photographed the most DIFFERENT windows at the Space Center, which on the
present corpus is the operator's own `c1-gui` career, where "most captures" would
pick whichever lane happened to be longest - but it is computed over the REAL
fixtures only and handed to the page as `defaultFixture`, rather than derived in
the page over everything it holds. A ~300-state mocked gallery wins a breadth
contest outright and would silently become the page the owner opens
(`design-gui-state-gallery.md` 7.6). `--default-fixture` overrides it by hand and
refuses a name that is not one of the corpus's datasets.

## 4. Before / after (the Compare view)

Compare shows ONE window: the one selected in the rail, re-filtering when another
is picked there (each rail header carries a `cmp` affordance that jumps straight
to it). Listing every window's keys at once was a page nobody could read. The
whole-program summary table survives as a fold under the window's own section, and
its rows are links into the other windows.

`key = (fixture, window, tab, state, mode, scene)`, plus the catalogue state id
for a mocked capture and nothing for a real one, so every real key is
byte-identical to what it was before the gallery existed. Every capture of one key
is sorted by `capturedUtc`; BEFORE is the earliest, AFTER the latest, and the pair
is reported as CHANGED only when the two trees differ once the sampled colours are
stripped out (a colour-only difference is a screenshot difference, not a layout
one). Both sides are drawn by the same renderer off their own dump, so a
difference visible on the page is a difference in the game.

Each window's section opens with its own COUNTS, measured by
`window_compare_summary`: states real and mocked, changed, unchanged, new, gone,
captures with how many of them superseded, and the flag counts of section 17. The
states are DISTINCT KEYS rather than files - the corpus carries 314 PNGs behind 182
keys, so a file count reads as 1.7x the coverage there is - and NEW and GONE are
read off SPEC RE-FLIGHTS, the only place the corpus has a before and an after of
the same intent: for each spec that photographed this window more than once, the
keys its newest run has and its oldest does not are new, and the reverse are gone.
A spec that flew once contributes neither, because one flight cannot say a state
disappeared. The window's known-uncaptured list is printed under the counts.

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

### The rail folds

Clicking a window header in the left rail SHOWS that window - selecting its
default capture through the same ranking every other click uses - and unfolds its
capture list on the way in. On the window already shown there is nothing to switch
to, so there the click is the fold toggle; that is what keeps the toggle usable at
all. The header keeps its count badge and flips a caret.
Everything starts folded except the window being shown, a capture selected from
anywhere else (a launcher, a tab, the Compare view) unfolds its own window on the
way in, and the folded set is remembered in `localStorage` as a per-viewer
convenience - every access guarded, because a private window or cleared site data
can make the accessor throw and the rail has to come up anyway.

### The rail's order

Each window's rows are ordered by the generator (`rail_rows`, shipped as the
model's `railRows`), never by the page, so a regeneration cannot shuffle them.
There is one row per (tab, state, mode), and it is the CURRENT capture of that
state where one exists. Rows are grouped by tab, tabs in the window's own tab-bar
order (`tab_order`: the seam's `uiaction tab ... index=N`, else the pushed-in cell
`si` of the window's own grid in the capture filed under that tab); a capture with
no tab but a grid showing a known cell joins that cell's group, one with neither
leads the list (the window as it opens), and a tab with no known index follows the
known ones. Within a tab the rows run from the least drawn to the most - the node
count of the subject window's own tree (every Parsek root where the seam's rect
names none) - so reading down a tab shows the window filling in. Basic comes
before Advanced on a tie, then the label, so the order is total. A window with
more than one group gets a thin header per tab, named by the tab's display name,
which folds away with its rows when all of them are hidden.

### What the rail calls things

The rail, the stage header, Compare and the notes list name a window, a tab and a
state by what the game draws, never by the seam token alone (owner, 2026-09-25: a
window whose token is not its title could not be found). All three names are
derived from the dumps, so nothing is typed; the token stays the key of every
link, note and lookup, and the row tooltips carry it.

* **A window** reads by its own title minus the product prefix
  (`window_display_names`). The prefix is `display_title_prefix`: the leading
  words most titles share, because one window of another product family breaks
  the all-titles `title_prefix` the record vocabulary uses. A window titled by its
  subject (the structure window, titled by its mission or route) reads by the one
  title that spells its token and lists the rest in its tooltip; two windows that
  would read the same carry their tokens. Only seam windows are named.
* **A tab** reads by its selection grid's text, else - for a tab row made of
  toggles (the Timeline's views) - by the toggle that tab alone lights
  (`toggle_tab_names`); a grouping toggle lit under several tabs names none.
* **A state** reads by the control its step changed where the capture's own tree
  shows it (`state_display_names`): among the current captures of its window,
  tab and mode, the one toggle it lights (or unlights, read "off") or the one
  button whose text differs from its peers (a glyph swap), asked first of the
  controls every peer agrees on and then of those most do. A count-bearing
  button never names a state, and a name two states would share, or a state that
  differs in several controls, keeps the token.

## 6. The three photo modes

The photograph is there to check the rendering, and the first version checked it
the wrong way: it drew the rendered window ON TOP of the frame, so every string
appeared twice a pixel or two apart. That reads as a rendering fault and hides the
one thing an overlay is good for.

* **off** (the default) - the rendering alone. It is the thing being checked.
* **overlay** - the photograph alone, with the rendered layer collapsed to thin
  outlines: no text, no fills, one box per control, and the outlines can be turned
  off too. A box that lands on its own control in the photograph is the check.
* **side** - the rendering and the photograph next to each other at the same
  scale, in two panes that scroll independently. This is the comparison to reach
  for; the Compare view's per-side button is the same idea, swapping one side of a
  before/after pair for its photograph rather than stacking them.

The crop is drawn at its own pixel size at its own origin, so it is never
stretched or re-aspected; a photo the size budget had to subsample is upscaled by
the same factor on both axes and says so by being soft.

## 7. Size budget

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

The size is measured before the output file is opened, and an over-budget page is
REFUSED rather than written and then complained about: a page that exists is a page
someone opens. A modal capture is the one exception to the crop rule - a
`PopupDialog` is a centred uGUI canvas outside every window rect, so its photo is
the whole frame, captioned as such.

And a frame whose dimensions disagree with the dump's own `screen` is not sampled
at all (`-v` says which): the rects are in the frame the dump was taken at, so a
superSize screenshot would sample the wrong pixels for every control, and scaling
it here would be a guess about which way.

## 8. Foreign windows

Another mod's window was on screen when the census ran and is in the dump. A root
is Parsek's when its `(x, y, w)` matches a rect the seam APPLIED to a window, in
ANY run - collected globally on purpose, because one lane that opened a window
without re-placing it must not demote that window everywhere (that bug hid the
main window from the whole page once). Of what is left, a root repeating
identically under four or more different windows is another mod's chrome (the kRPC
server window, the MechJeb menu button) and is hidden behind the `other mods`
toggle. A root seen under one window only - a group picker, the Gloops recorder,
the watch-mode overlay - is Parsek's and stays.

## 9. Regenerating

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
makes the page about a third of the size. `--default-fixture` pins the dataset the
page opens on (section 3) and `--stamp` pins the generation stamp, which is
otherwise now (UTC) and which travels in the exported notes blob so a verdict
names the corpus it was typed against.

`gui-mirror-index.json` is the coverage record without the geometry: captures per
window, states per window with their fixtures, the known-but-uncaptured states, and
the before/after key table. It is the thing to read when the question is "what is
covered" rather than "what does it look like". Its top level carries
`captureCount`, `mockedCaptureCount`, `distinctKeyCount`,
`supersededCaptureCount`, `hoverNotCapturedCount`, `labelDisagreementCount` and
`defaultFixture`; each window carries the same breakdown plus its
`window_compare_summary`; and each state row carries `captures`, `mocked` and
`superseded`. **`distinctKeyCount` is the coverage number, not `captureCount`.**

## 10. Coverage as of the 2026-09-22 build

314 captures over 16 fixtures and 13 windows, from 32 census runs (GUI-1 through
GUI-23), behind **182 DISTINCT KEYS**. The two numbers are 1.7x apart and the
second is the coverage one: 132 of the captures are SUPERSEDED, a later run of the
same lane having photographed the same key (section 17). A file count was what let
the state audit report 230 captures over 134 distinct labels and read as
coverage that was not there.

Of those 314: **0 mocked** (no gallery lane has flown yet), **8** flagged
`hover not captured` and **12** flagged `label disagrees with the log`. 47 of the
182 keys have both sides and differ.

| Window | Captures | Distinct keys | Superseded | Datasets |
| --- | --- | --- | --- | --- |
| main | 70 | 40 | 30 | 10 |
| missions (incl. Recordings) | 60 | 35 | 25 | 11 |
| timeline | 53 | 27 | 26 | 7 |
| career | 40 | 21 | 19 | 6 |
| kerbals | 34 | 17 | 17 | 5 |
| logistics | 18 | 15 | 3 | 6 |
| settings | 9 | 7 | 2 | 3 |
| gloops | 8 | 5 | 3 | 3 |
| testrunner | 6 | 5 | 1 | 2 |
| structure | 6 | 4 | 2 | 3 |
| testrunnerglobal | 3 | 3 | 0 | 1 |
| GuiTree probe | 3 | 2 | 1 | 2 |
| spawncontrol | 4 | 1 | 3 | 1 |

Nine states are known to the seam and have no capture, and all nine are the same
shape: a tab selected in BASIC mode (Missions' own tab, two Timeline tabs, both
Kerbals tabs, all four Career tabs). Basic draws no tab bar, so the census never
took them. They are listed in the left rail, greyed, printed under each window's
Compare header, and clicking one says why.

One real state the corpus is still missing:
`bdk-kerbals-roster-standin-chain-advanced`, which exists only in a shots
directory nothing indexes. Adding it is a `--shots` argument, not a code change
(`docs/dev/todo-and-known-bugs.md`). MOOT since 2026-09-22: the Kerbals Roster
dropped the chain fold, so that state is gone from the product; and the Kerbals
window now draws in Basic, so the gen7 regeneration (which indexes GUI-11
`2026-09-22_2004`) holds a Basic Kerbals Roster capture and lists only Kerbals'
Basic `outcomes` tab as missing - seven missing states, not nine.

STALE BY FOUR LANES as of the same day: the wave-6 lanes `GUI-24` through
`GUI-27` flew while this was being read and are not in the numbers above. The
generator needs no change to take them - `parse_label` reads its window and tab
vocabularies out of the logs - so the mirror wants one REGENERATION rather than
an edit, and the reading above is left as the reading it was, per this section's
own "as of" contract.

## 11. Fidelity: the page measured against the frame

`harness/tools/gui_mirror_fidelity.py`. Until this existed, "the mirror looks like
the game" was an impression, and the impression had already been wrong three times
in ways the eye caught only by luck: tab headings from an older epoch drawn over a
frame that said something else, a container painted in its own child's colour, text
drawn twice. What an eye finds by luck it misses by luck, so the question needed an
instrument.

### It measures THIS page, not a second renderer

The obvious way to build this - re-implement the layout in Python and compare that
against the frame - measures a program that does not ship, and it agrees with the
page wherever both are wrong for the same reason. So the instrument opens the
generated file itself in a headless Chromium, at a deep link the generator carries
for exactly this purpose:

```
gui-mirror.html#cap=<runId>%2F<label>&bare=1
```

Bare mode renders ONE capture's stage, at 1:1 CSS pixels, at the top-left of the
page, with the rail, header, status line, echo strip, photograph and every
animation off, and pins the stage to the frame the dump was taken at - so pixel
(x, y) in the screenshot is pixel (x, y) in the census PNG. Then it sets
`data-ready` on `<html>`.

Three things keep that honest:

* The bare skin is a separate string (`BARE_CSS`) and every selector in it is
  scoped to the `bare` class, which a test asserts by PARSING the selectors. So a
  page opened without the hash is the page that shipped, mechanically rather than
  by promise.
* `bootBare()` is the first statement of `boot()` and returns false when the hash
  is absent, so there is no second rendering path to drift from the first.
* The ready marker needs no second browser call to read: in bare mode the stage is
  `visibility:hidden` until `data-ready` is set, so a screenshot taken too early
  comes back BLANK, and a blank window rect where the census frame has ink is a
  refusal the instrument reports rather than a measurement it takes.

### The four metrics

All of them are taken INSIDE the Parsek window rects of that capture. Everything
outside is the game's own scene, which the page neither has nor claims.

* **TEXT**, the one that matters. For every text-bearing LEAF control, the ink
  bounding box in the frame against the ink bounding box in the mirror, inside the
  same rect: `dx`, `dy`, the width ratio `wr` (mirror ink width over the frame's),
  and a `clipped` flag. Ink is any pixel whose luminance is more than 45 from the
  rect's OWN median - not from a constant, because the same label style is drawn
  on `#444444` window fill, `#292929` content boxes and `#313131` buttons across
  the corpus. `clipped` is set when the mirror's run ends AT the rect edge while
  the frame's ends inside it; deliberately NOT "the mirror's run is shorter",
  because a font a few per cent wide runs into the edge and is cut there with MORE
  ink than the frame's, which is the exact case that loses characters.
* **FILL**. The median colour of each painted control's own surface - the points
  its children do not cover, the same rule the generator sampled it with - frame
  against mirror, reported as the worst single channel. The mirror sampled those
  colours off that very frame, so this asks whether the page paints what it
  stored. It is the standing guard for the container-took-its-child's-colour
  class.
* **PRESENCE**. Controls whose rect carries ink in the frame and none in the
  mirror, and the reverse, grouped by `kind|style|text` so a group is one fixable
  class instead of a list of coordinates. This is what finds a handle, a tick, an
  icon or a scroll bar the page draws nothing for.
* **WINDOW**. The mean absolute luminance difference over the window rect. For
  RANKING captures only, and not a pass/fail number: a window with a transparent
  gutter has the game's scenery behind it and will never read zero.

Two instrument details, each of which produced a wrong reading before it was
right. The ink is looked for two pixels IN from every rect edge: a button's border
contrasts with its fill as strongly as its label does, and measuring the whole
rect made the frame's ink box the entire 980 px border of the Close button against
the 33 px word inside it - dx 471, `wr` 0.03, and a defect report about a control
that was perfect. And a control is measured only over the part of it that all its
ancestors contain, because that is what the page draws; where the GAME clips
differently (it clips at scroll views and windows, not at every box) that surfaces
as the `clipped` flag instead, which is a thing a reader can act on.

### Running it

```bash
python harness/tools/gui_mirror_fidelity.py \
  --shots "<...>/results/<runId>_<specId>_shots" [--shots ...] \
  --repo . --out-dir <a scratch folder>
```

`report.json` and a self-contained `index.html` land in `--out-dir` and nowhere
else - the run produces one screenshot per capture plus crops and heatmaps, none
of which belongs in the repository, and a test cell fails if any tracked file
under `harness/` or `docs/` is an image or carries an inlined image payload.
`--window`, `--capture` and `--limit` narrow a re-measure to seconds. `--jobs`
(4) is the worker threads, which overlap the browser wait with the measurement -
the Python-bound half - and `--browser-jobs` (1) is how many browser launches
may be in flight at once, which is separate because four heavy pages at once made
Edge exit 0 with no screenshot and no stderr. `--page` reuses an already-built
page instead of generating one, which is what makes a before/after pair
comparable; `--budget-ms`, `--timeout` and `--triples` tune the browser's virtual
time, the wait for a screenshot, and how many worst captures get an image triple.

`--out-dir` receives `report.json`, `index.html` and - unless `--page` names one -
the `mirror-bare.html` the measurement was taken against. The browser's own
working files go to short temp directories and are removed at the end.

The browser is optional equipment: with none installed it exits 3 naming the
paths it probed, and every unit test (`harness/lib/test_gui_mirror_fidelity.py`)
passes without one - which is the machine CI runs on.

One caveat about running a SUBSET. Whether a root belongs to another mod is a
property of the CORPUS: the generator calls it foreign only when the same root
repeats under four or more different windows. A run given a few shots directories
can fall below that and classify nothing - on a four-directory sample a MechJeb
title bar was measured as a Parsek window in 13 captures - so the instrument
warns when the run covers fewer than four windows. Quote numbers from a full-corpus
run.

Two things about driving a headless Chromium on Windows that cost a run each.
`msedge.exe` is a LAUNCHER: it returns in tens of milliseconds and a child writes
the screenshot half a second later, so the driver waits for the file's own IEND
chunk rather than for the process. And the `--user-data-dir` must be an absolute
path in a SHORT directory, one per worker thread: a relative one makes Edge put
"can't read and write to its data directory" on the desktop once per launch, a
profile under a deep scratch path silently exceeds Windows' path limit and the
launch then returns 0 having written nothing, and two threads sharing one profile
make the second Edge hand its URL to the first and exit. The profiles are
therefore `tempfile.mkdtemp` directories, keyed by thread, removed at the end, and
the argv is a list from end to end so no path can be re-split on a space. A launch
that fails twice STOPS the batch rather than repeating the browser's dialog 230
times. The screenshots themselves go to a short temp directory too, numbered
rather than named after their capture: written beside the report they came to 264
characters for the long labels, and those launches returned 0, wrote nothing and
said nothing, which cost 240 s of timeout and retry each before it was understood.
Those temp directories are removed from a `finally`, with a retry and a backoff,
and the run REPORTS any it could not remove: deleting them with
`ignore_errors=True` ran while the browser's children still held files, failed
silently because errors were ignored, and left 43 of them (320 MB) in the owner's
temp directory. And the launch carries no `--hide-scrollbars`: it hid a
difference the page itself had introduced (a native scroll bar over the mirrored
KSP one), which is the opposite of what an instrument is for. A run that cannot
finish exits non-zero - 3 with no browser, 4 when the batch halted part-way - so
a partial corpus cannot be read as a whole one.

### What a metric cannot see, and what to do about it

PRESENCE asks one binary question - are there at least four ink pixels in this
rect - and that is enough for text, where the answer flips when a label is
missing. It is NOT enough for a control whose own border satisfies it whatever is
drawn inside. The slider is the case that proved it: the page drew its handle in
a typed light grey (luminance 185) where KSP's scroll bar thumb has a dark face
(17 to 50) under a one-pixel bevel (85 to 101) over a groove of 45, so the page
moved AWAY from the game while the report counted "slider frame-only 41 -> 0",
and DELETING the handle element entirely moved no number at all.

Two things follow, and they are the general lesson rather than a slider story.
First, a fix and a metric are not independent when the fix is judged by the
metric that motivated it: a class whose count went to zero deserves the question
"what would this number do if I removed the fix?" before it is quoted. Second,
when the answer is "nothing", the metric is wrong for that class and needs one of
its own - here the mean absolute luminance error over the control's rect, plus
the thumb run's position and length where both sides resolve one, which moves
from a resolved `[4, 242]` to unresolved the moment the handle goes.

### What it found, and the corpus before and after

230 captures over 19 census runs, 222 measured (6 carry a stock modal, 2 have no
PNG), 9112 text controls. BEFORE is the page as it rendered on 2026-09-21; AFTER
is the same corpus through the same instrument with the classes below fixed.

RE-MEASURED 2026-09-22 over the wave-5 corpus (32 runs, 314 captures, 305
measured, 12 912 text controls): every AFTER number in the table below held to
the digit at p50 and p95 - dx 2 / 5 px, dy 1 / 4, width ratio 1.000 / 1.111, fill
delta 0 / 0, all sliders resolving a thumb on both sides at a p50 offset of 1 px,
window score p50 9.04 - and the residual's per-capture rate is flat (frame-only
0.52 -> 0.57, clipped 0.28 -> 0.30), which is eleven new lanes photographing
states nothing had measured before. Three new worst-case tails came with them and
are `docs/dev/todo-and-known-bugs.md` T42b items 6 to 8. The table is left as the
reading it was, per the same "as of" contract section 10 keeps.

| Metric | BEFORE | AFTER |
| --- | --- | --- |
| text ink dx, p50 / p95 / worst | 2 / 52 / 643 px | 2 / **5** / **22** px |
| text ink dy, p50 / p95 / worst | 1 / 4 / 16 px | 1 / 4 / 16 px |
| text width ratio, p50 / p95 | 1.000 / 2.214 | 1.000 / **1.111** |
| fill colour delta, p50 / p95 / worst | 0 / 5 / 43 | 0 / **0** / **16** |
| slider luminance error, p50 / p95 / worst | 34.2 / 39.7 / 45.2 | **18.1** / **28.0** / **28.1** |
| sliders whose thumb resolves on BOTH sides | 0 of 41 | **41** of 41, p50 offset 1 px |
| runs the page clipped and the game did not | 554 | **63** |
| ink in the frame, none in the mirror | 587 | **115** |
| ink in the mirror, none in the frame | 122 | 99 |
| window luminance score, p50 | 8.86 | 9.04 |

The two slider rows are the ones this table did not have when the work was first
reported, and they are the reason it did not: see "What a metric cannot see"
above. The thumb-run row is the sharpest single number here - 0 of 41 sliders had
a thumb the frame and the page BOTH resolved before, 41 of 41 do now, and the
page puts it within a pixel of where the game did.

The window score is the one number that went UP, and it is the one number that is
not a quality measure: it is a raw luminance difference over the whole window
rect, and the page now DRAWS things it used to leave blank - scroll bar thumbs,
slider handles, tick marks, the disabled text it had dimmed to nothing. A control
drawn one pixel from where the game drew it scores worse than a control not drawn
at all. It is kept for ranking captures, which is all it was ever for.

Fixed, worst class first, each measured rather than assumed:

1. **A disabled control was dimmed twice** - 377 controls with ink in the frame
   and none on the page. The colours here are sampled per control out of the
   frame, so a disabled control's colour is ALREADY the grey the game drew;
   `opacity:.42` on top of that put the Missions window's disabled interval field
   below the threshold of being visible at all. The opacity now applies only where
   the frame gave no colour to carry the state. `button|button|text`: frame-only
   377 -> 43, clipped 376 -> 15.
2. **A raised control's outline was brighter than its fill** - so the ink
   measurement found the whole button interior instead of its label, and the page
   looked like a web form rather than like KSP. Measured on the ib-logistics
   frame: KSP draws a near-black outline (grey 5 to 25) with a light top bevel
   inside it (88, 71, 61, fading) over a fill of 25 to 76. Buttons, repeat
   buttons, selection-grid cells, button-styled toggles and text fields now carry
   that edge. Corpus width ratio p95 2.214 -> 1.111.
3. **`box`-styled text was aligned by a rule, and KSP has no such rule** - the
   Logistics section heading is CENTRED in its 1358 px box while the sortable
   column headers of the same table are LEFT-ALIGNED in theirs. The page
   left-aligned the first (643 px out) and centred the second. The offset is now
   MEASURED off the frame (`text_ink_offset`), the same move the tab bar's labels
   already used. `label|box|text` worst dx 643 -> 5; `button|box|text` p95 dx
   195 -> 4 and clipped 128 -> 7.

   **Read the dx of those two classes with that in mind.** The page places their
   text at an offset taken off the same PNG the instrument then grades it
   against, so for `label|box` and `button|box` the text dx is a measurement
   graded against itself and near zero by construction. It says the page applied
   what it measured; it does not say the page is in the right place. The
   INDEPENDENT evidence for those classes is the width ratio and the clipped
   flag, neither of which the offset can flatter - and both moved for real
   (clipped 128 -> 7). The same circularity has a second edge: a box that
   another window covers takes its offset from the COVERING window's pixels,
   because the covering window is what is in the frame at that rect. That is one
   more reason the overlap share in section 12 is worth reading before any
   per-class number here.
4. **A toggle in the BUTTON style was drawn as a checkbox** - 987 of the corpus's
   8154 toggles. KSP draws `Toggle(v, text, "button")` as a button that sits
   pushed in while it is on; the page drew "x label" on bare window fill. It now
   takes the button's shape, its sampled fill and the pushed state.
   `toggle|button|text` p50 dx 52 -> 2, width ratio 1.367 -> 1.000.
5. **A slider had no handle and a scroll bar had no bar** - 41 controls. The dump
   records a rect and no value, which section 12 below used to call unfixable
   from the dump; it was never unfixable from the PNG. `slider_thumb_run` reads
   the brightest contiguous run along the control's own long axis, which resolves
   on 81 of the corpus's 137 sliders - including the Settings ghost-audio slider,
   at 152 of its 227 px groove. The groove is also oriented by the rect's own
   aspect now: 130 of the 142 sliders are VERTICAL scroll bars and the page was
   drawing a horizontal bar across their middle.
6. **A scroll view did not scroll** - 52 of them carry content below the fold, up
   to 23 529 px of it, and `overflow:hidden` made every row of it unreachable.
   The children are absolutely positioned inside their own containing block, so
   the rects ARE the extent and no content sizer is needed. `&scroll=<px>` on the
   bare link scrolls every scroll view before the page marks itself ready, so
   that "reachable" is a thing a screenshot can show rather than a claim about
   CSS.
7. **A toggle's tick was an ASCII `x`** - the right state in the wrong shape. It
   is drawn in CSS now. This is the one fix with no measurement behind it, and
   the one that is still not the game's own glyph.

Two things the numbers say that the list does not. The fill metric reaching a p95
of ZERO is the quiet one: wherever the page stores a colour it now paints that
colour, which is the standing guard against the
container-took-its-child's-colour class returning. And 429 more ink pairs are
measurable after than before (8227 against 8656) - the page draws text in places
where it used to draw nothing at all.

What the residual is made of, so the next pass starts in the right place: of the
115 controls with ink in the frame and none on the page, 46 (40%) sit in a region
two Parsek windows both cover, and of the 63 clipped runs, 32 (51%) do. Neither
the page nor the game publishes its window stacking, so those readings are about
which window won rather than about the rendering. The rest, and the four
differences that are real, are section 12 and
`docs/dev/todo-and-known-bugs.md` T42b.

## 12. Known differences from the frame

MEASURED by section 11's instrument over the whole corpus, not read off the
side-by-side by eye. The three entries this section used to carry were the eye's
reading, and two of them were wrong about what was possible: the slider's handle
and the box alignment were both derivable from the PNG all along, and both are
fixed. What is left:

1. **A label that WRAPS in the game is drawn on one line.** KSP's label styles
   word-wrap; the page sets `white-space:pre`. The Logistics route cell that reads
   `> Route: KSC` / `-> Duna` over two lines in the frame is one line on the page,
   which is most of the residual `dy` of about 10 px on those rows. The rect
   height over the line height says how many lines the game used, so it IS
   derivable - but it moves every multi-line cell on the page and wants its own
   pass with the instrument beside it.
2. **KSP's own font metrics are not Arial's.** 13 px Arial renders three measured
   corpus runs at 136.6 / 144.5 / 86.8 px against a measured 136 / 144 / 87, and
   the corpus width ratio is 1.000 at the median and 1.111 at p95 - but the tails
   are real, and a long single line still ends a character or two early or late.
   A real fix means shipping KSP's font metrics. A control whose style departs
   from the skin's font (the dump's `fontSize` / `fontStyle` keys, see
   `design-gui-tree-dump.md`) is drawn at that size and weight, pixels 1:1 -
   the page's `fs` / `fw` keys via `compact_font` and `applyFont`, window titles
   included; everything else keeps the calibrated 13 px default. That reaches
   further than the main window: Parsek's shared opaque window style carries an
   explicit size-14 title while the stock skin's window size is 0, so every
   Parsek window title now draws at 14 px, and the many size-10 and size-11
   labels draw at those sizes in Arial, whose small-size metrics against KSP's
   font are not measured.
3. **A toggle's tick is a CSS checkmark, not KSP's skin texture.** Right state,
   right box, drawn shape. The texture is in no census artifact, so there is
   nothing to derive it from.
4. **A control hidden behind another Parsek window is still measured, and drawn
   from the wrong window.** The dump records every window's controls including the
   covered ones, and neither side publishes its stacking order. 170 of the 222
   measured captures have more than one Parsek window; 40% of the residual
   frame-only controls and 51% of the residual clipped runs sit in a region two
   windows both cover. This is the largest single share of what is left, and it
   is an instrument fix (record which root a control came from) rather than a
   page fix.
5. **A capture with a stock modal is not measured at all** - 6 of 230. A
   `PopupDialog` is a centred uGUI canvas that overdraws the window rects in the
   FRAME and appears in no control tree, so every rect under it would read as a
   difference the page could not have avoided. The page shows the photograph for
   those, which is the honest rendering.

`docs/dev/todo-and-known-bugs.md` T42b carries the same list as work items.

### What "unchanged" does not cover

The changed test strips the sampled colours before comparing (`_strip` drops `bg`
and `fg`), so **a colour-only change pairs as UNCHANGED**. That is deliberate: two
runs of one lane differ in their pixels for reasons that are not the product -
scenery, time of day, a ghost drifting behind the window - and pairing those as
changes would bury the layout differences the view exists for. The cost is real
though: a window whose only change was a text colour (a status turning red, a row
becoming a link) reads as unchanged here. Put the two sides in side-by-side photo
mode to see it.

## 13. What this page is not

* Not a status authority. `docs/dev/autotest-status.md` owns the census's status
  and `design-gui-inventory.md` owns the structural map.
* Not a substitute for a census. It can only show what was photographed; a window
  changed and not re-flown reads as unchanged, which is why every capture carries
  its run id on the page.
* Not a player-facing surface, and not a new UI surface in Parsek: it is a harness
  tool that renders artifacts, and it ships no game code.

## 14. Mocked captures

A gallery lane (`design-gui-state-gallery.md`) hands a window a synthetic view
model and photographs the REAL draw code drawing a state no save can reach. Those
captures live in the SAME page as the real ones, badged, because the owner's
question is about one window at a time and splitting the page would make him
remember which half he was in.

**A mocked capture declares itself in its DUMP, never in its name.** The recorder
writes an additive `mock` block at the unchanged `parsek-gui-tree/1` schema id -
`stateId`, `window`, `catalogue`, `states`, `covers` - and ABSENT means real,
which is every committed capture. `mock_provenance` reads it and answers None for
an absent block, a block that is not an object, and a block with no `stateId`; a
non-numeric `states` or a `covers` that is not a list degrades to 0 and `[]`
rather than throwing.

It has to be the dump and not the label because the mirror derives a capture's
dataset from the lane's `fixture.saveTemplate`, and a gallery lane HAS one - it
needs a loaded game. Without the block its captures would file under a real
fixture's name and pair against real captures in Compare, which is the one lie
this page must not tell.

**The facets come out of the catalogue state id.** `mock_facets` reads
`<window>.<family>.<variant>`: the block's `window` field is the authority for the
window token, the leading tail token is the tab when it is in that window's own tab
vocabulary (the same rule `parse_label` applies), and what is left is the state
with dots turned into dashes. The mode still comes from the log. Nothing is read
off the label, so a mocked capture carries no label-versus-log disagreement by
construction.

**`fixture` becomes `mock`, and that is what isolates Compare.** `fixture` is
already part of `key_of`, so a mocked BEFORE can only ever pair with a mocked
AFTER - structurally, not by a filter somebody has to remember. The catalogue state
id is appended to the key as well, so two states of the same window and tab whose
derived state tails agree still cannot pair with each other; real keys gain no
segment and stay byte-identical.

**Badged in four places**, all from one flag function: the rail entry (and the
window header carries `N mocked` beside its capture count), the stage header, the
status line, and the Compare rows including both sides of a pair. The badge's
tooltip names the state id, the catalogue, the state count and the branch keys the
state claims - all of it out of the block.

**And `mock` is never the default view.** See section 3.

`gui-mirror-index.json` carries `mockedCaptureCount` at the top level, a
`capturesMocked` per window, and `mocked: true` on the state rows, so "how much of
this is real" is answerable without opening the page.

## 15. The notes the round comes back through

The owner reviews a window, and what he types has to reach an agent in a later
session. The page is a static file and must stay one, so the mechanism is
`localStorage` plus a `<pre>`: no server, no upload, no download link (a viewer
sandbox blocks one, and a blocked link is worse than a box you can select).

**One field per row.** Every state view and every Compare pair gets a verdict
(`keep` / `change` / `unsure`, or none) and a one-line note, stored under
`(run pair, window, tab, state, mode, fixture)`. The run pair is what makes a
Compare note about THIS before and THIS after: re-fly the lane and the pair
changes, so last round's verdict does not silently attach itself to a picture the
owner has not seen. A state view uses its own run id for both halves.

**Every storage access is in try/catch, and a refusal is said out loud.** A fold
that does not persist is a nuisance and the rail says nothing about it; a VERDICT
that does not persist is lost work, so a refused write puts
`this browser refused storage - export before you close the tab` beside the field
and repeats it in the export panel. The page renders correctly with no storage at
all.

**Export is one blob in two forms.** `Export notes` serialises every non-empty
note for the CURRENTLY SELECTED window, or for all windows, as JSON and as a
markdown table in a `<pre>`, with a copy button. The clipboard API is refused in
some viewers, so the fallback is visible: the `<pre>` is selected for the reader
and is `user-select:all` anyway.

The blob is a SCHEMA, because it leaves the page and is read back by something
with nothing else to key on. `NOTES_SCHEMA = "parsek-gui-mirror-notes/1"`:

```json
{"schema": "parsek-gui-mirror-notes/1",
 "pageSchema": "parsek-gui-mirror/1",
 "generatedUtc": "2026-09-22T00:00:00Z",
 "scope": "timeline", "count": 1,
 "notes": [{"key": "<runPair>|<window>|<tab>|<state>|<mode>|<fixture>",
            "window": "timeline", "tab": "overview", "state": "live",
            "mode": "advanced", "fixture": "b1-pad-craft", "mocked": false,
            "mockState": "", "beforeId": "<runId>/<label>",
            "afterId": "<runId>/<label>",
            "verdict": "change", "note": "one line"}]}
```

`generatedUtc` is the PAGE's generation stamp, not the export time: it says which
corpus the owner was looking at. The row carries the capture ids of both sides, the
five facets, the dataset, the mocked flag and the state id, which is what makes a
verdict actionable in a session that never saw the page.

The row's FIELD SET is `NOTES_FIELDS` in the generator and is forwarded into the
page as `notesFields`, which the JS iterates to build a row. So the blob the page
writes and the blob `parse_notes_blob` reads back cannot drift apart, and the
round trip is a unit cell rather than a hope.

**Import** is a textarea and a `merge` button: paste a blob back and it merges by
key, replacing a row already there. It accepts the whole blob, a bare list of rows
or a single row, rebuilds a missing key from the row's own facets, and DROPS a row
with nothing to key on while saying how many it dropped - never merging one under
a made-up key.

## 16. One window at a time

The owner's ruling (`design-gui-state-gallery.md` 12) is that the review runs a
window at a time. Two affordances:

* **`#win=<token>`**, or `#win=<token>&view=compare`, opens the page already
  scoped to one window, in mirror or Compare view. The rail shows that window
  alone with a `show every window` row out of it, and the link is printed in a
  read-only field so it can be pasted into a message. A token no capture is of is
  SAID in the status line rather than silently ignored, because the whole page
  would otherwise look like the answer to a link that missed.
* **the per-window Compare header** of section 4.

The older deep link is untouched, and that matters more than it looks: the
fidelity instrument photographs `#cap=<runId>%2F<label>&bare=1`, so `bootBare()`
is still the first statement of `boot()` and returns before any of the above is
reached. A bare page renders exactly what it rendered before - one stage, no rail,
no header, no notes field - and every new element is in the `body.bare` hide list,
which the "every bare rule is scoped to the bare class" cell keeps mechanical.

## 17. Retiring false coverage

Five rules, none of which names a label. A label typed into the generator is a
label that rots, and the state audit's own finding was that the mirror was
reporting 230 captures over 134 distinct labels with 8 of them photographing
nothing.

**(a) SUPERSEDED: a later capture exists for the same key.** Every capture of a
key but the latest is marked `supersededBy` that one. The mirror shows the latest
- `pick` filters superseded captures out of the pool, and so does the capture the
page OPENS on - the rail lists the current one per state and never a superseded
one, and coverage counts DISTINCT KEYS rather than files. The superseded capture
stays reachable as its pair's BEFORE, because it is the evidence of what
changed. This is what retires the audit's four stale labels with
no label named: wave 5 re-flew their lanes, so
`ksc-settings-advanced` / `-basic`, `ksc-career-milestones-advanced`,
`ksc-kerbals-roster-advanced` / `-outcomes-advanced`,
`fs-kerbals-outcomes-empty-advanced` and
`play-kerbals-outcomes-flight-advanced` are all superseded by construction -
verified on the corpus, 132 of 314 captures.

**(b) HOVER NOT CAPTURED, from the log.** A pointer step that moved the cursor
onto a control (`park=false`) logs its own result, and where that line carries
`tooltip=-` the game's `GUI.tooltip` was EMPTY when the frame was taken: the
capture is the window's idle state under a hover label. The flag is set from that,
excluded from the state counts and from Compare, and greyed in the rail.

The pointer state is ONE-SHOT: it belongs to the frame taken right after the op,
never to every later capture of the run.

The older census runs predate the `tooltip=` key, and an absent statement is not
an empty tooltip. The fallback there is the capture's own TREE: a hover frame that
is byte-identical to another capture of the same run, sampled colours stripped,
photographed nothing its sibling did not - which is exactly the audit's measured
reading that `b1-main-disabledecho-spawncontrol-advanced` is text-identical to its
`b1-main-idle-advanced` sibling. Between the two arms all four hover labels are
flagged (8 captures across the runs that took them): two from the log, six from
the tree. With neither a `tooltip=` key nor an identical sibling, nothing is
claimed.

**(c) LABEL DISAGREES WITH THE LOG.** The log-over-label rule already decided the
filing; this reports the disagreement so a reader knows the row he is looking at
is filed under something its own name denies. Three arms, in
`label_log_disagreements`: the label names a window the log contradicts; the label
names a tab the log contradicts; or the label names NO tab - which reads as the
window's default, index 0 - while the log selected a later one. The third arm is
the quiet form and the one that matters: it is what catches `ksc-timeline-basic`
(really the Re-Fly tab) and `ksc-missions-basic` / `bd-missions-basic`, the audit's
mislabelled captures. A label whose leading state token turned out to be a tab's
DISPLAY name is not a disagreement and is suppressed by the alias that resolved
it. On the corpus: 12 captures, all of them tab arms.

What (c) CANNOT see is a content mislabel - a state token that names something the
tree does not show, like `fs-timeline-overview-empty-advanced`, which is not the
empty branch, or `b1-missions-recordings-live-advanced`, which shows an empty tab.
The seam log says which window and tab were open; it says nothing about what the
rows in them read. Judging that would mean typing the word `empty` into the
generator, which is the one thing this page may not do.

**(d) RETIRED: the state's own lane no longer produces it.** (a) needs a later
capture of the SAME key, so a state a re-flown lane simply stopped photographing
stayed the current picture forever - the Kerbals "owner chain" from GUI-11's
2026-09-15_1744 run, absent from the rebuilt window's 2026-09-22_2004 run. A capture
of scenario S at run R is retired (`mark_retired`) when a NEWER run of S that can
speak for the lane reproduced neither its key nor its label. Only a complete run
speaks: run.py's result JSON beside the shots directory with a PASS verdict and
at least one capture, or - where no result is readable - a run that photographed
the same window. A run that failed or went INVALID never retires anything, since a
lane that died half way lacks captures for a reason that is not the product. A
state a later run photographed again is not retired, the retirement is dated from
the first witnessing run, and a superseded capture is not also retired. A retired
capture is treated like the other stale rows: `pick` leaves it out (so it is never
a window's default), the rail does not list it, it is badged "no longer captured
by S since R", and its key drops out of the window's state counts. On the gen9
corpus it retires exactly one capture, that owner chain.

**(e) OLD LAYOUT: drawn before the window's current layout existed.** The rail
lists the windows as they are today, never a state drawn with an older layout of
the window (owner, 2026-09-24). (a) and (d) cannot see a re-layout: a state no
lane re-flew after the change has no later capture of its key and its own lane
never flew again, so it stayed current. PR #1792 moved the Timeline from three
filter rows to two, and the rail listed the six GUI-6 / GUI-7 / GUI-19 Timeline
states from 2026-09-15 and 2026-09-21 in the three-row layout beside the new
two-row captures. Two passes mark a capture `outdated`:

* `prune_removed_tabs` - the window lost a TAB. A tab none of whose captures is
  live (each superseded or retired) is removed; a capture of the window taken up
  to that tab's last capture was drawn with it and is `outdated.tabs`. Captures
  after it lose the tab from their assembled tab bar. Only superseded and retired
  captures count here, never an old-layout one: a tab whose only captures predate
  a re-layout still exists.
* `mark_layout_epochs` - any other re-layout, against the explicit
  `LAYOUT_EPOCHS` table in the generator: window token -> the UTC instant from
  which its captures show the current layout. A capture of that window EARLIER
  than the instant is `outdated.epoch`; one taken at the instant or after it is
  current; a window with no entry, and a capture with no readable `capturedUtc`,
  are not judged. The flag is orthogonal to (a) and (d).

An `outdated` capture stays a Compare BEFORE picture, badged "old layout (had T)"
or "old layout (before #N)"; the rail never lists it, `pick` leaves it out so a
window never opens on it (unless nothing else of that state exists), and its key
drops out of the window's state counts. The index carries `outdatedCaptureCount`,
`outdatedKeyCount` and the `layoutEpochs` the page was judged against.

The table is the one piece of product history the corpus cannot tell, so it is
typed, and **every PR that changes a window's layout updates it** in the same
commit. The instant is the `startedUtc` (run.py's result JSON) of the first
census run that PR flew on the new layout, NOT its merge time: a window-round PR
re-flies its lanes before it merges, and a merge-time floor would mark that proof
itself as the old layout (for #1792 it would have hidden all 46 two-row
captures). The floor is a time, not a build: a lane flown after it from a branch
that does not carry the change still reads as current, so re-fly from a branch
that does. On the 2026-09-24 corpus the career-round3 runs (2026-09-22_2051 to
_2054) are that case for the Kerbals window - flown after #1762's instant from a
branch without it - and are harmless only because later runs superseded them.

| Window | Instant (UTC) | PR, merged | First run on the new layout |
| --- | --- | --- | --- |
| `main` | 2026-09-22T18:41:11Z | #1755, 2026-09-22T19:16:54Z | GUI-1-census-ksc 2026-09-22_1841 |
| `kerbals` | 2026-09-22T20:04:25Z | #1762, 2026-09-22T20:38:41Z | GUI-11-census-kerbals-crewed 2026-09-22_2004 |
| `timeline` | 2026-09-24T19:42:55Z | #1809 (after #1792) | GUI-24-census-timeline-filters 2026-09-24_1942 |
| `career` | 2026-09-24T15:22:01Z | #1796, 2026-09-24T16:18:48Z | GUI-15-census-career-contracts 2026-09-24_1522 |

Each boundary was checked against the captures' own chrome: every Timeline
capture from 2026-09-23_2134 on draws the `Career` view toggle and the one
`Time: ...` toggle, and every earlier one draws the `Last Day ... All` preset row;
the Kerbals roster before 2026-09-22_2004 carries a `Since` column; the flight
main window before 2026-09-22_1841 draws the `Status` block. On that corpus the
epochs take 32 states off the rail that no current capture covers: 21 of the main
window (the GUI-10 dialogs, the GUI-9 map-scope states, the GUI-7 recording
states, the GUI-6 playback states), 5 of Kerbals (GUI-6) and 6 of Timeline (GUI-6,
GUI-7, GUI-19). Re-flying GUI-6, GUI-7, GUI-9, GUI-10 and GUI-19 on the current
build puts them back.
