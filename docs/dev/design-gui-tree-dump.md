# Design: IMGUI tree dump (`GuiTreeRecorder`)

Status: SPIKE, landed 2026-09-10 on `gui-dump-spike`. The pure layer is unit-tested;
the Harmony interception layer has NEVER RUN INSIDE KSP - see "What is unproven".

## The problem

Parsek's UI is roughly 30k lines of `GUILayout` / `GUI` calls across `ParsekUI.cs` and
`UI/*.cs`. An IMGUI window has no retained structure: there is no widget tree to walk,
no `GameObject` hierarchy, no accessibility surface. The only artefact of a window is
the pixels it drew that frame. An agent supervising work on those windows can read the
source and it can read a screenshot, and neither tells it what the window actually
CONTAINED at run time - which rows a filter left visible, which button was greyed out,
which tooltip a control published, what nests inside what.

This records one frame's worth of that, as JSON, so the structure can be read as a tree
and rendered as boxes over the matching screenshot.

## The hard constraint

**ZERO changes to the draw code.** Nothing under `UI/` or in `ParsekUI.cs` knows the
recorder exists. 30k lines cannot be instrumented by hand, an instrumented copy would
rot immediately, and a per-control call at every draw site would be a permanent tax on
every frame of normal play. The whole capture is therefore a Harmony interception of
UnityEngine's own IMGUI funnels, armed for a single frame and inert otherwise.

## Which funnels are patched, and why those

Verified by decompiling the shipped `UnityEngine.IMGUIModule.dll` (KSP 1.12.5, Unity
2019.4) with `ilspycmd -t UnityEngine.GUI` / `GUILayout` / `GUILayoutUtility` /
`GUIClip` / `GUIUtility`.

Every `GUILayout` control resolves its rect through `GUILayoutUtility.GetRect` and then
calls the rect-taking `GUI.*` method, so ONE patch per control kind on the `GUI` side
covers both the `GUI` and the `GUILayout` spelling. For example
`GUILayout.Label(GUIContent, GUIStyle, GUILayoutOption[])` is
`GUI.Label(GUILayoutUtility.GetRect(content, style, options), content, style)`, and all
six `GUI.Label` overloads funnel into the one private `GUI.DoLabel`.

The chosen method per funnel is the DEEPEST MANAGED one. Below these the module is all
`[MethodImpl(InternalCall)]` - `GUIStyle.Internal_Draw2`, `GUIClip.Internal_Push`,
`GUI.Internal_DoWindow`, every `*_Injected` - and an ICall has no IL body for Harmony to
rewrite. `GuiTreeFunnelTests.NoFunnelIsAnUnpatchableInternalCall` pins that none of the
19 targets is one.

Target signatures live in exactly one place, `GuiTreeFunnels.Target`, which each patch
class's `TargetMethod()` returns AND which the arm-time funnel report resolves, so a
patched signature and a reported one cannot drift apart.

| Funnel | Signature (decompiled) | Access | Role |
|---|---|---|---|
| `GUI.DoWindow` | `static Rect DoWindow(int id, Rect clientRect, WindowFunction func, GUIContent title, GUIStyle style, GUISkin skin, bool forceRectOnLayout)` | private | window rect + title, keyed by id |
| `GUI.CallWindowDelegate` | `static void CallWindowDelegate(WindowFunction func, int id, int instanceID, GUISkin _skin, int forceRect, float width, float height, GUIStyle style)` | internal, `[RequiredByNativeCode]` | opens / closes the window NODE |
| `GUI.BeginGroup` | `static void BeginGroup(Rect position, GUIContent content, GUIStyle style, Vector2 scrollOffset)` | internal | group / `GUILayout.BeginArea` |
| `GUI.EndGroup` | `static void EndGroup()` | public | closes a group |
| `GUI.BeginScrollView` | `static Vector2 BeginScrollView(Rect position, Vector2 scrollPosition, Rect viewRect, bool alwaysShowHorizontal, bool alwaysShowVertical, GUIStyle horizontalScrollbar, GUIStyle verticalScrollbar, GUIStyle background)` | internal | scroll view |
| `GUI.EndScrollView` | `static void EndScrollView(bool handleScrollWheel)` | public | closes a scroll view |
| `GUILayout.BeginHorizontal` | `static void BeginHorizontal(GUIContent content, GUIStyle style, params GUILayoutOption[] options)` | public | horizontal layout group |
| `GUILayout.EndHorizontal` | `static void EndHorizontal()` | public | closes it |
| `GUILayout.BeginVertical` | `static void BeginVertical(GUIContent content, GUIStyle style, params GUILayoutOption[] options)` | public | vertical layout group |
| `GUILayout.EndVertical` | `static void EndVertical()` | public | closes it |
| `GUI.DoLabel` | `static void DoLabel(Rect position, GUIContent content, GUIStyle style)` | private | Label |
| `GUI.Box` | `static void Box(Rect position, GUIContent content, GUIStyle style)` | public | Box, and a STYLED layout group's background |
| `GUI.DoControl` | `static bool DoControl(Rect position, int id, bool on, bool hover, GUIContent content, GUIStyle style)` | private | the shared Button / Toggle body |
| `GUI.DoButton` | `static bool DoButton(Rect position, int id, GUIContent content, GUIStyle style)` | internal | names the kind for `DoControl` |
| `GUI.DoToggle` | `static bool DoToggle(Rect position, int id, bool value, GUIContent content, GUIStyle style)` | internal | ditto, plus the toggle's value |
| `GUI.DoRepeatButton` | `static bool DoRepeatButton(Rect position, GUIContent content, GUIStyle style, FocusType focusType)` | private | RepeatButton |
| `GUI.DoTextField` | `static void DoTextField(Rect position, int id, GUIContent content, bool multiline, int maxLength, GUIStyle style, string secureText, char maskChar)` | internal | TextField / TextArea / PasswordField |
| `GUI.DoButtonGrid` | `static int DoButtonGrid(Rect position, int selected, GUIContent[] contents, string[] controlNames, int xCount, GUIStyle style, GUIStyle firstStyle, GUIStyle midStyle, GUIStyle lastStyle, ToolbarButtonSize buttonSize, bool[] contentsEnabled)` | private | Toolbar / SelectionGrid, as ONE node |
| `GUI.Slider` | `static float Slider(Rect position, float value, float size, float start, float end, GUIStyle slider, GUIStyle thumb, bool horiz, int id, GUIStyle thumbExtent)` | public | sliders and, via `GUI.Scroller`, scrollbars |

### The window path, including ClickThruBlocker

Parsek's windows all go through `ClickThruBlocker.GUILayoutWindow` (18 call sites).
Decompiled, that is:

```
ClickThruBlocker.GUILayoutWindow(id, rect, func, text, style, options)
  -> GUILayout.Window(id, screenRect, func, text, style, options)
  -> GUILayout.DoWindow(...)            // wraps func in a LayoutedWindow
  -> GUI.Window(id, screenRect, wrapper.DoWindow, content, style)
  -> GUI.DoWindow(...)                  // <- PATCHED (declaration)
  -> GUI.Internal_DoWindow(...)         // extern; the native window host
  -> GUI.CallWindowDelegate(...)        // <- PATCHED (scope); called BY the native side
  -> func(id)                           // the window body
```

`GUI.DoWindow` is the single funnel for all six `GUI.Window` overloads and for
`GUILayout.Window`, so `ClickThruBlocker` needs no patch of its own.

The two are patched for different reasons. `CallWindowDelegate` brackets the window body
EXACTLY and is a large method, so it is the load-bearing one: it opens the window node on
its prefix and closes it on its postfix. `DoWindow` only contributes the declared rect
and the title, matched to the node by window id. If `DoWindow` were ever bypassed the
window node still exists, carrying instead an independently measured `contentOrigin`
(`GUIUtility.GUIToScreenPoint(Vector2.zero)` inside the callback) and the `argSize`
Unity handed the callback.

### Button vs Toggle: the staging hint

`GUI.DoControl` is where both Button and Toggle actually draw, and it cannot tell them
apart - Button reaches it as `DoControl(pos, id, on: false, hover, content, style)` and
Toggle as `DoControl(pos, id, value, hover, content, style)`. So the KIND is staged by
the caller's own patch (`DoButton` / `DoToggle` prefix) and claimed by the `DoControl`
prefix.

That pair is self-healing in both directions, which matters because both `DoButton` and
`DoToggle` are two-statement methods:

- `DoButton`/`DoToggle` bypassed, `DoControl` runs: no hint, so the leaf is emitted with
  the kind classified from the style name (`GuiTreeAssembler.ClassifyFromStyleName`), and
  a Parsek custom style falls through to `control` rather than guessing.
- `DoControl` bypassed, `DoButton`/`DoToggle` runs: their POSTFIX flushes the staged leaf,
  so the control is recorded with the right kind.
- Both bypassed: the control is missing from the dump, and its funnel row reads
  `hits: 0`.

## Armed-frame semantics

- `GuiTreeRecorder.ArmForNextRepaint(label)` sets `ArmedFlag`, clears the buffers and the
  per-funnel hit counters, and returns the path the dump will be written to.
- Every patch body's first statement is `if (!GuiTreeRecorder.ArmedFlag) return;`. That
  is one static bool read, and it is the entire cost while disarmed - on every IMGUI
  control of every frame, which is why the flag is a plain static field and not a
  property, a settings lookup or an event.
- The capture only accepts `Event.current.type == EventType.Repaint`. IMGUI runs a Layout
  pass first with the same call sequence but no final rects, and recording it would double
  every node.
- The FIRST accepted event fixes `captureFrame = Time.frameCount`. Events from a later
  frame are refused, so a capture is exactly one frame even across several `OnGUI`
  containers (Parsek has one per window-owning MonoBehaviour; each becomes its own root).
- `GuiTreeRecorderPump` (a `[KSPAddon(EveryScene)]` MonoBehaviour with no `OnGUI`) calls
  `PumpPendingFlush()` from `LateUpdate`. Unity runs `LateUpdate` before the frame's
  `OnGUI` and therefore after the PREVIOUS frame's, which is where a completed capture
  gets assembled, serialised and written - never file I/O inside an IMGUI pass. The pump
  returns on a static bool read when nothing is pending.
- Any exception inside a record entry point is caught, counted, logged ONCE, and disarms
  the recorder (`GuiTreeRecorder.Fault`). An exception escaping into `OnGUI` would abort
  Unity's GUI pass mid-window and desync the layout cache for the rest of the frame, so
  the recorder gives up rather than retrying.
- One Info line per capture:
  `[Parsek][INFO][GuiTree] label=... windows=N nodes=M events=E strayEnds=.. autoClosed=.. unclosed=.. faults=.. written=1 path=...`

Output: `<KSP root>/Screenshots/<label>.gui.json`. That directory because the harness
already harvests it by mtime, so a dump travels with the run's screenshots for free.
`SanitizeLabel` reduces the label to `[A-Za-z0-9._-]`, capped at 96 chars, so a caller
cannot walk out of the directory.

There is deliberately NO command-seam verb yet. The API is `internal static` and a verb
is a separate change (a sibling branch owns the seam dispatcher).

## Screen-space conversion

Each node carries two rects: `localRect` exactly as the funnel received it, and `rect`
converted with `GUIUtility.GUIToScreenRect`.

Decompiled, that conversion is

```
GUIUtility.GUIToScreenPoint(p) => InternalWindowToScreenPoint(GUIClip.UnclipToWindow(p))
```

`GUIClip.UnclipToWindow` walks the clip stack out to the enclosing window - so it
accounts for every `BeginGroup`, `BeginArea` and `BeginScrollView` push, including the
scroll offset a scroll view pushes as its clip's `scrollOffset` - and
`InternalWindowToScreenPoint` then adds the window's own screen origin. That is what
makes it correct INSIDE a `GUI.Window` callback, where the rects the funnels see are
window-local.

Both endpoints bottom out in ICalls, so this cannot be settled by reading the assembly:
it is a measurement, and the live cell is what takes it. Keeping `localRect` alongside
`rect` is the instrument - a conversion that ever goes wrong shows up as a disagreement
in the dump, and `GuiTreeGeometry.Inspect` turns it into a named failure ("a control's
screen rect fell outside its own window") instead of a silently misplaced overlay.

The window node additionally carries `contentOrigin`, an independent
`GUIToScreenPoint(Vector2.zero)` taken inside the callback.

## Nesting, and why the tree repairs itself

Explicit Begin/End pairing is not trusted, because the End side of several pairs is a
method Mono is free to inline into its caller (`GUI.EndGroup` is two statements;
`GUILayout.EndHorizontal` is one). So every event also carries `clipDepth`, read from
`UnityEngine.GUIClip.Internal_GetCount()` by reflection - an ICall can be INVOKED even
though it cannot be patched - and `GuiTreeAssembler` uses it as the authority for
clip-pushing containers:

1. An `End` closes the nearest open node of that kind, counting anything closed on the
   way there as `autoClosedByEnd`. An End with NO match closes nothing and is counted as
   `strayEnds` - guessing there would let one stray End reparent the rest of the window.
2. Before any Begin or Leaf, open Window / Group / ScrollView nodes the incoming
   event's `clipDepth` proves are gone are closed (`autoClosedByClip`), along with any
   LayoutGroups stranded above them. The comparison is ASYMMETRIC, and the asymmetry is
   what makes the rule work without a reliable End. A clip container's recorded
   `clipDepth` and a clip-container Begin's `clipDepth` are both CHILD depths, so two
   SIBLING containers carry the same number and a nested one carries a strictly greater
   one - a container Begin therefore closes an open container at EQUAL depth. A leaf, or
   a LayoutGroup Begin, carries the depth it was drawn AT, which equals its parent's
   child depth - so for those, equal means "still inside" and only a strictly smaller
   depth closes. One comparison for both either loses every sibling group whose End was
   inlined, or evicts every leaf from the container it belongs to.
3. A LayoutGroup pushes no clip, so its recovery is rect containment: it closes when the
   next event's rect falls outside it by more than
   `GuiTreeAssembler.LayoutGroupContainmentSlackPx` (4 px, because GUILayout rounds group
   and child rects independently). Degenerate rects - zero-size carriers, spacers - never
   trigger it.
4. Anything still open at stream end is counted as `unclosedAtEnd`.

A clip-pushing Begin records the depth its DIRECT CHILDREN will report, and it gets
that number for free by being a Harmony POSTFIX - `GUI.BeginGroup` and
`GUI.BeginScrollView` are recorded after Unity pushed the clip, so no normalisation
arithmetic is involved. For `GUI.BeginScrollView` the postfix is load-bearing rather
than tidy: the method draws its own two scrollbars BEFORE `GUIClip.Push`, at the OUTER
depth, so a scroll-view node opened on the prefix was closed again by its own scrollbar
and held none of its rows. As a postfix the scrollbars land as SIBLINGS just before the
scroll view, which is where they are actually drawn. `-1` means the probe was
unavailable, and the clip rule then goes quiet and falls back to pairing alone.

Every repair is counted and reported, so a reader can tell a clean capture
(`strayEnds: 0, autoClosed*: 0, unclosedAtEnd: 0`) from a patched-but-inlined one.

## JSON schema (`parsek-gui-tree/1`)

```json
{
  "schema": "parsek-gui-tree/1",
  "label": "parsek-guitree-probe",
  "capturedUtc": "2026-09-10T10:11:12Z",
  "frame": 4242,
  "screen": {"width": 1920, "height": 1080},
  "screenshotHint": "parsek-guitree-probe.png",
  "counts": {
    "windows": 1, "nodes": 12, "events": 20,
    "strayEnds": 0, "autoClosedByClip": 0, "autoClosedByEnd": 0,
    "autoClosedByRect": 0, "unclosedAtEnd": 0,
    "recordFaults": 0, "droppedOverCap": 0
  },
  "funnels": [
    {"name": "GUI.DoLabel", "patched": true, "hits": 5}
  ],
  "roots": [
    {
      "kind": "window",
      "rect": [10.5, 20.25, 300, 400],
      "localRect": [10.5, 20.25, 300, 400],
      "clipDepth": 1,
      "style": "window",
      "enabled": true,
      "text": "Parsek",
      "windowId": 90210,
      "contentOrigin": [10.5, 40.5],
      "argSize": [300, 400],
      "children": []
    }
  ]
}
```

Always present on a node: `kind`, `rect`, `localRect`, `clipDepth`, `style`, `enabled`,
`text` (`null` for an icon-only control - an absent text is information), `children`.
Present when applicable: `tooltip`, `value` (a toggle's state), `textValue` (a text
field's content, a slider's value, a button grid's selected label), `controlId`,
`windowId`, `horizontal` (a layout group's orientation), `contentOrigin`, `argSize`.

`kind` is one of `window`, `group`, `scrollview`, `layoutgroup`, `label`, `box`,
`button`, `repeatbutton`, `toggle`, `textfield`, `buttongrid`, `slider`, `control`. The
strings are a contract (`GuiTreeAssembler.KindName`), not a `ToString()`.

`rect` is `[x, y, w, h]` in screen space, y DOWN from the top-left - the same frame as a
screenshot's pixels. Numbers are invariant-culture, rounded to 2 decimals; NaN and
infinity serialise as `null`.

The `funnels` block is the feasibility instrument. `patched` is read from
`Harmony.GetPatchInfo` at arm time, so `patched: false` means the signature drifted out
from under the patch, and `patched: true, hits: 0` means the interception was bypassed
(inlined, or nothing drew that control kind that frame).

## The offline viewer

`harness/tools/gui_tree_view.py` (stdlib only) turns a dump into a self-contained HTML
page - inline CSS and JS, no CDN, the screenshot embedded as a data URI when one sits
beside the JSON. Boxes at each node's rect coloured by kind, over the screenshot; a
collapsible tree panel beside it; hovering either side highlights the other.
`--batch <dir>` writes one page per dump plus an `index.html`. Its pure half is unit
tested in `harness/tools/test_gui_tree_view.py`.

## What is unproven

**The interception layer has never run inside KSP.** The spike's author cannot launch the
game. What IS mechanically proven, headlessly:

- all 19 funnel signatures resolve against the shipped `UnityEngine.IMGUIModule.dll`,
  are static, and are not ICalls or P/Invokes (`GuiTreeFunnelTests`);
- every patch body's declared parameter names and types match its target's, which is
  what Harmony throws on at patch time (`PatchParameterNamesAndTypesMatchTheirTargets`);
- the assembler's nesting and all four recovery rules, the JSON escaping and its culture
  invariance, and the geometry derivation the live cell asserts against.

What only a flight can settle:

1. **Mono inlining.** Harmony rewrites a method; a caller Mono already JITted with that
   method inlined keeps the old code, and Mono's inliner works from IL, so a small callee
   can be inlined into a caller JITted after the patch. The four two-statement targets
   (`GUI.DoWindow`, `GUI.EndGroup`, `GUILayout.EndHorizontal` / `EndVertical`) plus
   `GUI.DoButton` / `GUI.DoToggle` are the exposed ones. Every one of them has a designed
   fallback (the window scope comes from `CallWindowDelegate`; the End pairs are repaired
   from clip depth or rect containment; the kind hint degrades to style-name
   classification), and the `funnels` block names any that were bypassed. Should a
   fallback prove insufficient, the escape hatch is `GUIStyle.Draw` - the instance method
   every leaf's Repaint path calls, moderately sized and public - which yields rect and
   style for everything at the cost of losing the control kind.
2. **`GUIToScreenRect` inside a window callback**, as above.
3. **The `GUIClip.Internal_GetCount` probe** resolving at all, and the
   `GUILayoutUtility.topLevel` / `GUILayoutEntry.rect` reflection that gives layout
   groups their rect. Both fail soft: a missing clip probe reports `clipDepth: -1`
   everywhere, a missing layout-rect probe records zero rects for layout groups, and
   either logs one Warn.
4. **Cost while armed.** One frame's worth of allocation for a few hundred small objects.
   Never measured, and it does not matter for a one-frame capture - but arming it every
   frame would be a different feature with a different budget.

## Known gaps

- **Toolbar / SelectionGrid are ONE node.** Per-cell rects are computed inside
  `GUI.DoButtonGrid`'s private `CalcMouseRects` and the cells draw through
  `GUIStyle.Draw`, below the managed surface. The node carries the grid rect and the
  selected item's label.
- **`GUI.DrawTexture`, `GUI.Label` with a Texture, and anything drawn straight through a
  `GUIStyle` are not captured.** Icon-only content records `text: null`.
- **No z-order across windows.** Roots are in the order Unity ran the window callbacks,
  which is draw order, but a window that overlaps another is not marked as occluding it.
- **Scroll-clipped children are recorded, not culled.** A row scrolled out of a scroll
  view still records its rect, which will lie outside the scroll view's. The viewer draws
  it anyway; the scroll view's own rect is the clip bound if a consumer wants to cull.
- **PasswordField records the MASKED content.** `secureText` is deliberately not read.
- **A styled `GUILayout.BeginHorizontal` / `BeginVertical` emits a `box` leaf** with the
  group's own rect, because that is literally how Unity draws the group background
  (`GUI.Box(group.rect, content, style)`). It arrives BEFORE the group node - the group
  is recorded from a postfix, so its rect can be read off the layout cache - so it reads
  as the sibling immediately preceding the group. Real, not a duplicate.
- **One capture is one frame.** A window that only draws on some frames, or a control
  behind a hover state, needs the arm to coincide with it.

## Files

| File | Role |
|---|---|
| `Source/Parsek/GuiTreeModel.cs` | pure: `GuiRect`, `GuiTreeEvent`, `GuiTreeNode`, `GuiTreeResult`, `GuiTreeAssembler` |
| `Source/Parsek/GuiTreeJson.cs` | pure: hand-rolled invariant-culture JSON writer |
| `Source/Parsek/GuiTreeGeometry.cs` | pure: the containment / depth derivation the live cell asserts |
| `Source/Parsek/GuiTreeFunnels.cs` | the 19 target signatures, wire names, hit counters, `patched` probe |
| `Source/Parsek/GuiTreeRecorder.cs` | arm / record / flush, the Unity seam, and `GuiTreeRecorderPump` |
| `Source/Parsek/Patches/GuiTreeRecorderPatches.cs` | the 19 Harmony patch classes |
| `Source/Parsek/InGameTests/GuiTreeDumpImguiTest.cs` | the live `GuiTree` cell + its probe window |
| `Source/Parsek.Tests/GuiTree*Tests.cs` | headless coverage of everything above that is pure |
| `harness/tools/gui_tree_view.py` | the offline viewer |
| `harness/tools/test_gui_tree_view.py` | its unit tests |
