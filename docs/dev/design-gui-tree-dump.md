# Design: IMGUI tree dump (`GuiTreeRecorder`)

Status: LIVE-PROVEN 2026-09-11. Landed 2026-09-10 on `gui-dump-spike` as a spike; the
`DumpGuiTree` seam verb and the two census lanes' dump steps landed 2026-09-11; the Harmony
interception layer RAN INSIDE KSP on the census's first flight - `2026-09-10_2255` /
`_2256` (GUI-1) and `2026-09-10_2259` / `_2300` (GUI-2) - writing 56 dumps at
`patched=17/17` with every anomaly counter zero, and the in-game `GuiTree` cell passed
inside it. See "What the first flight measured" for the reading and for the two things it
did NOT settle. Both lanes read INVALID on one seam step each, neither of them a dump and
both fixed in the same PR, so the LAYER is proven and the LANES are not yet green.

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

## The patches are OPT-IN, and that is the cost story

The interceptions are NOT part of `com.parsek.mod`'s permanent patch set. No class in
`Patches/GuiTreeRecorderPatches.cs` carries a `[HarmonyPatch]` attribute, so
`ParsekHarmony.Awake`'s assembly sweep - which applies every attributed class it finds,
for the life of the process - cannot discover them. Instead:

- `GuiTreeRecorder.ArmForNextRepaint` calls `GuiTreeRecorderPatches.Apply()`, which
  patches each funnel through the recorder's OWN `Harmony("com.parsek.guitree")`
  instance, one funnel at a time, logging rather than throwing on a failure. It is
  idempotent PER FUNNEL, not globally: a funnel our own owner id already holds is skipped
  (`GuiTreeFunnels.IsPatched`, and `ClassifyFunnelPatchAction` is the pure decision).
  Harmony 2.2.1's `PatchInfo.Add` does NOT deduplicate, so patching a funnel twice
  installs the prefix and postfix twice and records every control twice - and a global
  `Applied` guard could not prevent that on its own, because `Remove()` is allowed to
  fail. `Remove()` clears `Applied` only AFTER `UnpatchAll` RETURNS
  (`RemainsAppliedAfterUnpatch`): a throwing unpatch may have removed some detours and
  left others, so the flag keeps saying "installed" and the next arm re-patches only the
  funnels it no longer owns.
- Immediately after, the arm reads
  `Harmony.GetPatchInfo(...).Owners.Contains("com.parsek.guitree")` for every funnel into
  `GuiTreeFunnels.PatchedAtArm`. That array - not a flush-time reading, which would report
  `false` for everything - is what the JSON's `funnels` block reports as `patched`.
- `FlushCapture` ends with `UnpatchAll("com.parsek.guitree")`; so do `Disarm` and
  `Fault`. Those three are the ONLY removers, and two of them are reachable only from an
  armed or capturing recorder - which is why the arm's own throw path has to call `Disarm`
  itself (below).

**Why this matters.** A Harmony detour on `GUI.DoLabel` is paid by every IMGUI consumer
in the process - KSP's own debug UI, MechJeb, KER, ClickThroughBlocker - on every control
of every event pass, forever. The earlier draft's claim that the cost was "one static
bool read" described the patch BODY and quietly ignored the detour around it. Disarmed,
the cost is now **zero patches**, which is a different claim and a true one. The
`if (!ArmedFlag) return;` at the top of every body still matters, because the patches can
outlive the capture by one frame (below).

**The "am I inside OnGUI" predicate is `GUIUtility.guiDepth > 0`, NOT
`Event.current != null`.** Stated first, because both paragraphs below rest on it and the
first draft got it wrong. Decompiled from the shipped `UnityEngine.IMGUIModule.dll`,
`Event.current`'s getter is `return s_Current;` with no depth gating;
`Event.Internal_MakeMasterEventCurrent` assigns `s_MasterEvent` to `s_Current` on the
first GUI pass, and the setter maps a null assignment straight back to it
(`s_Current = value ?? s_MasterEvent;`). Only `Event.CleanupRoots` ever nulls it, so in
the player `Event.current` is non-null forever after the process draws its first frame -
so a guard built on it answered "inside a GUI pass" from EVERY context, refused every arm,
and left the feature dead on its first flight. Unity's own predicate is
`GUIUtility.guiDepth` (`[NativeProperty("GetGUIState().m_OnGUIDepth", true,
TargetType.Field)] internal static extern int`), the very reading `GUIUtility.CheckOnGUI`
tests with `guiDepth <= 0` before throwing "You can only call GUI functions from inside
OnGUI". `GuiTreeFunnels.GuiDepthGetter()` resolves it, `GuiTreeRecorder.ReadGuiDepth()`
invokes it, and it is re-resolved at every arm like the other reflection probes.

It is an ICall - invokable, never patchable - and it is called through `MethodInfo.Invoke`
rather than a bound delegate: this guard is read at most twice per capture (unlike the clip
probe, which runs per control), so the boxed int costs nothing, and
`Delegate.CreateDelegate` over an ECall is refused outside the declaring module ("ECall
methods must be packaged into a system module", which is what the xUnit host raises).
**Fallback when the depth cannot be read** - unresolvable member, or the invoke throwing,
which is what a headless host does: `ReadGuiDepth` returns
`GuiTreeRecorder.GuiDepthUnavailable` (-1) and `ClassifyInsideGuiPass` treats that as NOT
inside, so an arm proceeds and an unpatch runs immediately. That direction is deliberate:
both call sites are outside a GUI pass BY CONSTRUCTION (the seam / coroutine / Update that
arms, and the LateUpdate pump), the guard is defensive only, and failing the other way
would reproduce the always-refuse bug it replaced. One Warn per arm names the fallback.

**The unpatch is deferred out of the GUI pass.** Rewriting a method the current call
stack is executing is worse than leaving a patch on for one more frame, so
`RequestUnpatch()` checks `guiDepth > 0` and, inside a pass, sets a flag the LateUpdate
pump acts on (`PumpPendingFlush` performs it as soon as `!capturing && !ArmedFlag`).
`Disarm` and `Fault` therefore normally unpatch one frame late; `FlushCapture` already runs
in LateUpdate and unpatches immediately.

**Arming refuses from inside a GUI pass.** `ArmForNextRepaint` returns null and logs
`arm refused reason=inside-gui-pass guiDepth=<n>` when `guiDepth > 0`, for two reasons: it
would install patches on the methods the current stack is running, and a pass already
half-drawn would give a truncated capture. The decision is
`GuiTreeRecorder.ClassifyArmRefusal(bool)` over `ClassifyInsideGuiPass(int)`, both pure and
unit-tested (the second including the -1 fallback); callers arm from Update, a coroutine or
the command seam. The depth that actually answered rides on the arm's Info line
(`armed label=... guiDepth=0 patchedFunnels=...`), so a flight can see WHICH predicate was
used: `0` is a real outside-OnGUI reading, `-1` is the fallback.

One `Event.current` read remains in the recorder, and it answers a different question:
`Accepting()` tests `Event.current.type == EventType.Repaint`. Every caller of it is a
patch body on an IMGUI funnel, so it only ever runs INSIDE a GUI pass, where
`Event.current` is that pass's own event and `.type` is exactly the reading wanted. The
stale-master-event trap cannot mislead a `.type` check reached only from inside a pass.

Gates: `GuiTreeFunnelTests.NoGuiTreePatchClassIsDiscoverableByTheAssemblySweep` re-runs
`ParsekHarmony.Awake`'s own predicate over the assembly and requires it to find none of
these classes, and `TheApplierTableCoversExactlyTheFunnelEnum` keeps the applier's table
and the funnel enum in step.

## Which funnels are patched, and why those

Verified by decompiling the shipped `UnityEngine.IMGUIModule.dll` (KSP 1.12.5, Unity
2019.4) with `ilspycmd -t UnityEngine.GUI` / `GUILayout` / `GUILayoutUtility` /
`GUIClip` / `GUIUtility`, and the IL sizes below with `ilspycmd -il`.

Every `GUILayout` control resolves its rect through `GUILayoutUtility.GetRect` and then
calls the rect-taking `GUI.*` method, so ONE patch per control kind on the `GUI` side
covers both the `GUI` and the `GUILayout` spelling. For example
`GUILayout.Label(GUIContent, GUIStyle, GUILayoutOption[])` is
`GUI.Label(GUILayoutUtility.GetRect(content, style, options), content, style)`, and all
six `GUI.Label` overloads funnel into the one private `GUI.DoLabel`.

The chosen method per funnel is **the deepest funnel that still knows the control kind**.
It is NOT the deepest managed method: `GUIStyle.Draw` is managed, public, and below all
of these, but it knows only a rect and a style. Below the funnels listed here the module
is `[MethodImpl(InternalCall)]` - `GUIStyle.Internal_Draw2`, `GUIClip.Internal_Push`,
`GUI.Internal_DoWindow`, every `*_Injected` - and an ICall has no IL body for Harmony to
rewrite. `GuiTreeFunnelTests.NoFunnelIsAnUnpatchableInternalCall` pins that none of the
17 targets is one.

Target signatures live in exactly one place, `GuiTreeFunnels.Target`, which each patch
class's `TargetMethod()` returns AND which the arm-time funnel report resolves, so a
patched signature and a reported one cannot drift apart.

| Funnel | Signature (decompiled) | Access | IL | Role |
|---|---|---|---|---|
| `GUI.DoWindow` | `static Rect DoWindow(int id, Rect clientRect, WindowFunction func, GUIContent title, GUIStyle style, GUISkin skin, bool forceRectOnLayout)` | private | 26 | window rect + title, keyed by id |
| `GUI.CallWindowDelegate` | `static void CallWindowDelegate(WindowFunction func, int id, int instanceID, GUISkin _skin, int forceRect, float width, float height, GUIStyle style)` | internal, `[RequiredByNativeCode]` | large | opens / closes the window NODE |
| `GUI.BeginGroup` | `static void BeginGroup(Rect position, GUIContent content, GUIStyle style, Vector2 scrollOffset)` | internal | large | group / `GUILayout.BeginArea` |
| `GUI.EndGroup` | `static void EndGroup()` | public | 14 | closes a group - but see the clip rule |
| `GUI.BeginScrollView` | `static Vector2 BeginScrollView(Rect position, Vector2 scrollPosition, Rect viewRect, bool alwaysShowHorizontal, bool alwaysShowVertical, GUIStyle horizontalScrollbar, GUIStyle verticalScrollbar, GUIStyle background)` | internal | large | scroll view |
| `GUI.EndScrollView` | `static void EndScrollView(bool handleScrollWheel)` | public | large | closes a scroll view |
| `GUILayoutUtility.BeginLayoutGroup` | `static GUILayoutGroup BeginLayoutGroup(GUIStyle style, GUILayoutOption[] options, Type layoutType)` | internal | 180 | EVERY horizontal / vertical layout group |
| `GUILayoutUtility.EndLayoutGroup` | `static void EndLayoutGroup()` | internal | 123 | closes one |
| `GUI.DoLabel` | `static void DoLabel(Rect position, GUIContent content, GUIStyle style)` | private | large | Label |
| `GUI.Box` | `static void Box(Rect position, GUIContent content, GUIStyle style)` | public | large | Box, and a STYLED layout group's background |
| `GUI.DoControl` | `static bool DoControl(Rect position, int id, bool on, bool hover, GUIContent content, GUIStyle style)` | private | large | the shared Button / Toggle body |
| `GUI.DoButton` | `static bool DoButton(Rect position, int id, GUIContent content, GUIStyle style)` | internal | 33 | names the kind for `DoControl` |
| `GUI.DoToggle` | `static bool DoToggle(Rect position, int id, bool value, GUIContent content, GUIStyle style)` | internal | 34 | ditto, plus the toggle's value |
| `GUI.DoRepeatButton` | `static bool DoRepeatButton(Rect position, GUIContent content, GUIStyle style, FocusType focusType)` | private | large | RepeatButton, incl. a scrollbar's arrows |
| `GUI.DoTextField` | `static void DoTextField(Rect position, int id, GUIContent content, bool multiline, int maxLength, GUIStyle style, string secureText, char maskChar)` | internal | large | TextField / TextArea / PasswordField |
| `GUI.DoButtonGrid` | `static int DoButtonGrid(Rect position, int selected, GUIContent[] contents, string[] controlNames, int xCount, GUIStyle style, GUIStyle firstStyle, GUIStyle midStyle, GUIStyle lastStyle, ToolbarButtonSize buttonSize, bool[] contentsEnabled)` | private | large | Toolbar / SelectionGrid, as ONE node |
| `GUI.Slider` | `static float Slider(Rect position, float value, float size, float start, float end, GUIStyle slider, GUIStyle thumb, bool horiz, int id, GUIStyle thumbExtent)` | public | large | sliders and, via `GUI.Scroller`, scrollbars |

**Exposure to Mono inlining, corrected.** `DoButton` (33 bytes) and `DoToggle` (34) are
the exposed leaf funnels; `GUI.EndGroup` (14) and `GUI.DoWindow` (26) are the exposed
container ones. `CallWindowDelegate` is `[RequiredByNativeCode]` and invoked FROM native
code, so it cannot be inlined at all. Neither `DoButton` nor `DoToggle` is exposed API -
both are `internal` - which changes nothing about the inlining risk (Mono's inliner does
not care about accessibility) but does mean no third party calls them directly.

### The layout-group swap, and its caller analysis

The first draft patched `GUILayout.BeginHorizontal` / `EndHorizontal` / `BeginVertical` /
`EndVertical`. The two Ends are **8 bytes of IL each** - a single
`call GUILayoutUtility::EndLayoutGroup()` and a `ret` - far inside Mono's inline limit
(20-30 bytes), and Mono's inliner reads a callee's IL from METADATA, not through the
detour, so Harmony patching a small method does not protect it from being inlined into a
caller JITted afterwards. Those two patches were, in all probability, dead.

They are replaced by `GUILayoutUtility.BeginLayoutGroup` (180 bytes) and
`EndLayoutGroup` (123), which every layout group passes through and which are far too
large to inline.

**Who calls them** (grepped over the WHOLE decompiled module, not just `GUILayout`):

| Caller | `layoutType` | Recorded? |
|---|---|---|
| `GUILayout.BeginHorizontal(GUIContent, GUIStyle, GUILayoutOption[])` | `GUILayoutGroup` | yes, `horizontal: true` |
| `GUILayout.BeginVertical(GUIContent, GUIStyle, GUILayoutOption[])` | `GUILayoutGroup` | yes, `horizontal: false` |
| `GUILayout.BeginScrollView(...)` | `GUIScrollGroup` | NO - filtered |

and `EndLayoutGroup` has exactly the three mirroring callers (`EndHorizontal`,
`EndVertical`, `EndScrollView(bool)`).

Two neighbours deliberately do NOT come through here, which is what keeps the filter
simple: **`GUILayout.BeginArea` uses `GUILayoutUtility.BeginLayoutArea`** (a different
method; `EndArea` pops `layoutGroups` by hand and never calls `EndLayoutGroup`), and
**`GUILayoutUtility.BeginWindow` assigns `current.topLevel` directly**. So neither an
area's root layout group nor a window's can be mistaken for a user group - the area is
already recorded as a `group` by `GUI.BeginGroup`, and the window by
`CallWindowDelegate`.

The scroll group IS filtered, by `layoutType.FullName != "UnityEngine.GUILayoutGroup"`,
because `GUI.BeginScrollView` already records the scroll view as its own node. Recording
the carrier too would duplicate the container, and its `EndLayoutGroup` arrives BEFORE
`GUI.EndScrollView` (`GUILayout.EndScrollView` calls them in that order), so the
duplicate's End would close the real scroll view early.

`EndLayoutGroup` takes no arguments, so the recorder pairs it against its own Begin
stack (`layoutGroupStack`, a `List<bool?>`): a `null` entry is a filtered carrier and
emits nothing, a value is the group's ORIENTATION and rides on the End event. That
orientation is load-bearing in the assembler - see rule 1 below.

Read off `__result` in the Begin postfix, by reflection (`GUILayoutGroup` and its
`GUILayoutEntry` base are internal): `rect` and `isVertical`. During Repaint the returned
group is the object the LAYOUT pass created and sized, so both are final. The caller sets
`isVertical` AFTER `BeginLayoutGroup` returns, which does not matter for the same reason.

One thing was lost in the swap: `BeginLayoutGroup` never sees the caller's `GUIContent`,
so a layout group node no longer carries `text`. A STYLED group's content is drawn
through `GUI.Box` and shows up there instead.

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
EXACTLY and cannot be inlined, so it is the load-bearing one: it opens the window node on
its prefix and closes it on its postfix. Only the PREFIX counts a hit
(`RecordWindowEnd` gates through a non-counting `Accepting`), because `hits` is one count
per funnel BODY run and counting both ends of one call made the window funnel read exactly
double its window count. `DoWindow` only contributes the declared rect
and the title, matched to the node by window id, and its 26 bytes may well be inlined. If
it is, the window node still exists, carrying instead an independently measured
`contentOrigin` (`GUIUtility.GUIToScreenPoint(Vector2.zero)` inside the callback) and the
`argSize` Unity handed the callback - which is also the pair the geometry check uses as
its containment box, so nothing load-bearing depends on the declaration.

### Button vs Toggle: the staging hint

`GUI.DoControl` is where both Button and Toggle actually draw, and it cannot tell them
apart - Button reaches it as `DoControl(pos, id, on: false, hover, content, style)` and
Toggle as `DoControl(pos, id, value, hover, content, style)`. So the KIND is staged by
the caller's own patch (`DoButton` / `DoToggle` prefix) and claimed by the `DoControl`
prefix.

That pair is self-healing in both directions, which matters because `DoButton` and
`DoToggle` are 33 and 34 bytes:

- `DoButton`/`DoToggle` bypassed, `DoControl` runs: no hint, so the leaf is emitted with
  the kind classified from the style name (`GuiTreeAssembler.ClassifyFromStyleName`), and
  a Parsek custom style falls through to `control` rather than guessing.
- `DoControl` bypassed, `DoButton`/`DoToggle` runs: their POSTFIX flushes the staged leaf,
  so the control is recorded with the right kind.
- Both bypassed: the control is missing from the dump, and its funnel row reads
  `hits: 0`.

## Armed-frame semantics

- `GuiTreeRecorder.ArmForNextRepaint(label)` refuses from inside a GUI pass, else clears
  the buffers and the per-funnel hit counters, applies the patches, snapshots
  `PatchedAtArm`, sets `ArmedFlag`, and returns the path the dump will be written to.
- The capture only accepts `Event.current.type == EventType.Repaint`. IMGUI runs a Layout
  pass first with the same call sequence but no final rects, and recording it would double
  every node.
- The FIRST accepted event opens the capture: it fixes `captureFrame = Time.frameCount`
  AND reads the two things that have to be read inside the frame being described -
  `Screen.width/height` and `GUI.matrix`. Events from a later frame are refused, so a
  capture is exactly one frame even across several `OnGUI` containers (each becomes its
  own root). Note that a capture is PROCESS-WIDE while it is open: every mod's windows are
  in it, not just Parsek's.
- **An arm that never sees a Repaint gives itself up.** Nothing else would: no capture
  opens, so no flush ever runs, and the interceptions would stay installed on all 17
  funnels for the rest of the process - the exact permanent cost the opt-in design exists
  to avoid. The arm stamps `Time.frameCount`, `HasPendingWork` includes `ArmedFlag` so the
  pump keeps running, and after `GuiTreeRecorder.ArmTimeoutFrames` (900) the pump logs one
  Warn and `Disarm("armed-no-repaint")`s. The decision is the pure
  `ClassifyArmTimeout(framesSinceArm, budget)`; an unreadable frame clock (-1) is NOT a
  timeout, for the same reason the `guiDepth` fallback is "not inside a pass" - refusing on
  an unreadable reading disables the feature. The budget is deliberately clear of the live
  cell's own 300-frame wait after arming, so a give-up cannot fire inside a wait a caller
  is legitimately performing.
- **An arm that THROWS after `Apply()` disarms itself.** The give-up above cannot reach
  that case: it runs only while `ArmedFlag` is set, and the throw window is exactly the
  region between `Apply()` and `ArmedFlag = true` (Apply's own tail after it has set
  `Applied`, and the funnel readback). A throw there would leave 17 detours installed with
  the pump idle for the rest of the session - the same permanent cost, reached a different
  way. `ArmForNextRepaint` therefore wraps that whole region in a `try` whose `catch` logs
  one Error, calls `Disarm("arm-threw")` and RETHROWS: the cleanup is the recorder's
  guarantee to BOTH callers, while the verdict stays the caller's (the seam verb reports
  `gui-tree-faulted`, and repeats the disarm at its own exit, where it is a no-op).
- `GuiTreeRecorderPump` (a `[KSPAddon(EveryScene)]` MonoBehaviour with no `OnGUI`) calls
  `PumpPendingFlush()` from `LateUpdate`. Unity runs `LateUpdate` before the frame's
  `OnGUI` and therefore after the PREVIOUS frame's, which is where a completed capture
  gets assembled, serialised and written - never file I/O inside an IMGUI pass - and where
  a deferred unpatch is performed. The pump returns on a static bool read when there is
  nothing to do.
- **The flush is one synchronous pass, and it hitches.** Assembling a few hundred small
  objects, building a string and writing a file all happen in one LateUpdate. On a big
  window that is a visible stutter, and it is accepted: a capture is a deliberate one-off,
  and splitting it across frames would mean holding the event buffer into a frame where
  the UI has already moved on.
- Any exception inside a record entry point is caught, counted, logged ONCE, and disarms
  the recorder (`GuiTreeRecorder.Fault`). An exception escaping into `OnGUI` would abort
  Unity's GUI pass mid-window and desync the layout cache for the rest of the frame, so
  the recorder gives up rather than retrying.
- One Info line per capture:
  `[Parsek][INFO][GuiTree] label=... windows=N nodes=M events=E strayEnds=.. autoClosed=.. rectRuleInert=.. unclosed=.. faults=.. clipProbe=delegate written=1 path=...`

Output: `<KSP root>/Screenshots/<label>.gui.json`. That directory because the harness
harvests it by mtime; `.gui.json` is in `hlib.ARTIFACT_SHOTS_SUFFIXES`, so a dump travels
with the run's screenshots into `results/<runId>_shots/`. `SanitizeLabel` reduces the
label to `[A-Za-z0-9._-]`, capped at 96 chars, so a caller cannot walk out of the
directory.

The command-seam verb is `DumpGuiTree label=<name>` (2026-09-11, ADDITIVE, 36 implemented
verbs). It arms the recorder from the seam's Update-phase executor - never from inside
OnGUI, which the arm refuses - and is TWO-PHASE: it holds the FIFO head until the recorder
reports THIS arm's dump written, so a following seam step is ordered after the write rather
than racing it and changing the UI the pending capture is about to record. Its OK line
carries `windows`, `nodes`, `patched=<ok>/<of>` and `hits`, which is what lets a spec pin
the funnel reading (`patched=17/17`) as a literal. Full contract:
`design-autotest-command-seam.md` -> `#### DumpGuiTree`.

Three per-arm readings exist on this class FOR that verb, because until it there was no
consumer that needed them out of the log: `LastArmRefusalReason` (why the last arm
refused, cleared at every arm), `LastDisarmReason` (the reason of the last `Disarm` since
the arm - `armed-no-repaint` being the one a caller acts on) and `FaultsSinceArm` (the
per-arm fault counter, distinct from `LastRecordFaults`, which describes the last COMPLETED
capture). A fault does not imply no dump: `Fault` clears `ArmedFlag` but leaves an OPEN
capture for the pump to flush, so a partial tree can still reach disk - which is why the
verb reads the counter rather than inferring "faulted" from a missing file, and why its
poll decides FAULTED ahead of SETTLED.

## Screen-space conversion

Each node carries two rects: `localRect` exactly as the funnel received it, and `rect`
converted for screen space.

Decompiled, the conversion is

```
GUIUtility.GUIToScreenRect(r):
    p = GUIToScreenPoint(new Vector2(r.x, r.y))   // = InternalWindowToScreenPoint(GUIClip.UnclipToWindow(p))
    r.x = p.x; r.y = p.y; return r                // <- WIDTH AND HEIGHT ARE UNTOUCHED
```

`GUIClip.UnclipToWindow` walks the clip stack out to the enclosing window - so it
accounts for every `BeginGroup`, `BeginArea` and `BeginScrollView` push, including the
scroll offset a scroll view pushes as its clip's `scrollOffset` - and applies
`GUI.matrix`; `InternalWindowToScreenPoint` then adds the window's own screen origin.
That is what makes it correct INSIDE a `GUI.Window` callback, where the rects the funnels
see are window-local.

**A clip container's OWN rect is converted in its PREFIX, not its postfix.** Decompiled,
`GUI.BeginGroup` ENDS with `GUIClip.Push(position, scrollOffset, Vector2.zero, false)` and
`GUI.BeginScrollView` with `GUIClip.Push(screenRect, (round(-scroll.x - viewRect.x),
round(-scroll.y - viewRect.y)), Vector2.zero, false)`. Both nodes are recorded from a
POSTFIX - which is load-bearing for the clip depth and for the scrollbar ordering, see the
nesting section - so at that moment the container's own clip is TOPMOST, and the
`UnclipToWindow` walk adds the container's origin, plus a scroll view's scroll offset, a
SECOND time. A group at window-local y=130 would have reported y = window + 130 + 130, and
a scroll view scrolled by 25 px would have reported y = window + 130 + (130 - 25).

So each of the two patch classes carries a `Prefix(Rect position)` that converts the rect
before the push and stacks it (`GuiTreeRecorder.PushContainerScreenRect`, and the stack is
a stack because containers nest), and the postfix pops it and takes its ORIGIN
(`ResolveContainerScreenRect`). The SIZE stays the postfix's on purpose:
`GUIToScreenRect` returns width and height untouched, so the clip cannot corrupt them,
while the `GUI.matrix` scaling the recorder applies to them by hand is read when the
CAPTURE OPENS - and the prefix is gated so that it does NOT open the capture, so a
container that happens to be the first funnel event of a capture would otherwise carry a
size scaled by the reset defaults. Everything else on the event stays as the postfix read
it too: `clipDepth`, `GUI.enabled`, text, style. `localRect` stays the raw `position` the
funnel received. The prefix is gated on armed-and-Repaint but deliberately NOT on
`Accepting`: it emits no node, so counting it would double the container funnel's `hits`
and opening the capture from it would fix `captureFrame` on an event that records nothing.
The pop happens BEFORE `Accepting` in the postfix and unconditionally, because the two
gates are not identical (the per-frame cap, a second frame) and an unbalanced stack would
hand the next container this one's origin.

**Checked in the mirror direction**, since the defect is "a rect converted at postfix time
under a clip the same method pushed", and two other sites convert under a clip:

| Site | Pushes a clip before the conversion? | Verdict |
|---|---|---|
| `GUILayoutUtility.BeginLayoutGroup` postfix (a layout group's rect) | NO - decompiled, it ends `current.layoutGroups.Push(group); current.topLevel = group;` and touches `GUIClip` nowhere, which is exactly why the assembler recovers a layout group's close by rect containment | correct as written |
| `GUI.CallWindowDelegate` prefix (`contentOrigin`) | yes, the NATIVE side pushed the window's clip before the callback - but nothing here converts a rect the window declared. `GUIUtility.GUIToScreenPoint(Vector2.zero)` converts the window's own content origin expressed in the space the children are drawn in, so unclipping it through the window's clip IS the measurement wanted. `argWidth` / `argHeight` are Unity's own numbers, and the declared rect comes from the `GUI.DoWindow` PREFIX unconverted | deliberate, and commented as such |

**The size gets none of that.** Under a non-identity `GUI.matrix` the origin is
transformed and the width and height are not, which would silently produce boxes of the
wrong size. So the recorder reads `GUI.matrix` when the capture opens, multiplies every
recorded width by `m00` and height by `m11`, logs one Warn, and writes the matrix into the
dump's header:

```json
"guiMatrix": {"identity": false, "m00": 1.5, "m11": 1.5, "m03": 0, "m13": 0}
```

so a reader can undo it. (Only the four elements that matter for an axis-aligned rect are
recorded; a rotating or shearing `GUI.matrix` would need more, and nothing in KSP or
Parsek sets one.) The viewer reports a non-identity matrix in its notes strip.

Both endpoints bottom out in ICalls, so none of this can be settled by reading the
assembly: it is a measurement, and the live cell is what takes it. Keeping `localRect`
alongside `rect` is the instrument - a conversion that ever goes wrong shows up as a
disagreement in the dump, and `GuiTreeGeometry.Inspect` turns it into a named failure
("a control's screen rect fell outside its own window") instead of a silently misplaced
overlay.

**The containment box is the MEASURED pair, not the declared rect.** `Inspect` builds it
from the window node's `contentOrigin` + `argSize`, both taken inside the callback the
children were drawn in. The declared `DoWindow` rect is kept as the second reading and
reported alongside, but it cannot be the box: it is unconverted, and it is the rect the
caller passed to `GUILayout.Window` BEFORE this frame moved the window, which makes it one
frame stale for the whole duration of a drag.

## Nesting, and why the tree repairs itself

Explicit Begin/End pairing is not trusted, because the End side of several pairs is a
method Mono is free to inline into its caller (`GUI.EndGroup` is 14 bytes). So every event
also carries `clipDepth`, read from `UnityEngine.GUIClip.Internal_GetCount()` through a
cached `Func<int>` delegate - an ICall can be INVOKED even though it cannot be patched,
and a delegate avoids `MethodInfo.Invoke`'s `object[]` plus boxed return on every recorded
control - and `GuiTreeAssembler` uses it as the authority for clip-pushing containers:

1. An `End` closes the nearest open node of that kind, counting anything closed on the
   way there as `autoClosedByEnd`. An End with NO match closes nothing and is counted as
   `strayEnds` - guessing there would let one stray End reparent the rest of the window.
   A LayoutGroup End additionally matches on ORIENTATION, so a stranded horizontal group
   cannot swallow the End of the vertical group enclosing it; an End with no orientation
   (the recorder's Begin stack was empty) matches either.
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
   and child rects independently). A DEGENERATE group rect - a zero-size carrier, a
   spacer - no longer stops the rule: it is looked past, to the nearest enclosing
   LayoutGroup with a usable rect (the search stops at the enclosing clip container), and
   the whole run closes together when the incoming rect is outside THAT one. When no
   usable rect exists in the run, the event is counted in `rectRuleInert` and nothing is
   guessed; the viewer reports a non-zero count as "layout nesting unverified".
4. Anything still open at stream end is counted as `unclosedAtEnd`.

A clip-pushing Begin records the depth its DIRECT CHILDREN will report, and it gets
that number for free by being a Harmony POSTFIX - `GUI.BeginGroup` and
`GUI.BeginScrollView` are recorded after Unity pushed the clip, so no normalisation
arithmetic is involved. For `GUI.BeginScrollView` the postfix is load-bearing rather
than tidy: the method draws its own two scrollbars BEFORE `GUIClip.Push`, at the OUTER
depth, so a scroll-view node opened on the prefix was closed again by its own scrollbar
and held none of its rows. As a postfix the scrollbars land as SIBLINGS just before the
scroll view, which is where they are actually drawn. `-1` means the probe was
unavailable for that event, and the clip rule then goes quiet for it and falls back to
pairing alone.

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
  "guiMatrix": {"identity": true, "m00": 1, "m11": 1, "m03": 0, "m13": 0},
  "counts": {
    "windows": 1, "nodes": 12, "events": 20,
    "strayEnds": 0, "autoClosedByClip": 0, "autoClosedByEnd": 0,
    "autoClosedByRect": 0, "rectRuleInert": 0, "unclosedAtEnd": 0,
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
infinity serialise as `null`. Strings escape the C escapes, every control character AND
every surrogate as `\uXXXX`: an unpaired surrogate in a label cannot be encoded as UTF-8,
so without that escape one bad character costs the whole dump at `File.WriteAllText`.

The `funnels` block is the feasibility instrument. `patched` is the arm-time
`Harmony.GetPatchInfo` reading, owner-scoped to `com.parsek.guitree`, so `patched: false`
means the signature drifted out from under the patch or the patch failed to apply, and
`patched: true, hits: 0` means the interception was bypassed (inlined, or nothing drew
that control kind that frame).

## The offline viewer

`harness/tools/gui_tree_view.py` (stdlib only) turns a dump into a self-contained HTML
page - inline CSS and JS, no CDN, the screenshot embedded as a data URI when one sits
beside the JSON. Boxes at each node's rect coloured by kind, over the screenshot; a
collapsible tree panel beside it; hovering either side highlights the other.
`--batch <dir>` writes one page per dump plus a `gui-tree-index.html` - NOT
`index.html`, which `tools/gui_contact_sheet.py` owns inside the same `*_shots`
directory a census runs both tools over. Its pure half is unit
tested in `harness/lib/test_gui_tree_view.py` - under `lib/`, not next to the tool,
because CI runs `discover -s lib` and a test beside the tool would never run (the same
placement as `lib/test_contact_sheet.py` for `tools/contact_sheet.py`).

## What the first flight measured

**The interception layer has run inside KSP.** The GUI census's first flight - GUI-1
`2026-09-10_2255` and attempt 2 `_2256`, GUI-2 `2026-09-10_2259` and `_2300` - drove 56
arms in total (23 per GUI-1 attempt: 22 census labels plus the `GuiTree` cell's own probe
window; 5 per GUI-2 attempt) on the `stock-minimal` instance at 1280x720. Artifacts:
`harness/results/<runId>_shots/`. Both lanes read INVALID on one seam step each - a rect
width read-back and Real Spawn Control shutting itself, both fixed in the same PR as this
note, neither a dump - so the layer is proven while the lanes still owe a green verdict.

What IS mechanically proven headlessly, unchanged and still the first line of defence:

- all 17 funnel signatures resolve against the shipped `UnityEngine.IMGUIModule.dll`,
  are static, and are not ICalls or P/Invokes (`GuiTreeFunnelTests`);
- every patch body's declared parameter names and types match its target's, which is
  what Harmony throws on at patch time (`PatchParameterNamesAndTypesMatchTheirTargets`);
- no patch class is discoverable by `ParsekHarmony`'s permanent sweep
  (`NoGuiTreePatchClassIsDiscoverableByTheAssemblySweep`);
- the inside-OnGUI guard's member resolves with its declared signature and is an ICall
  (`TheGuiDepthGuardMemberResolvesWithItsDeclaredSignature`,
  `TheGuiDepthGuardMemberIsAnInternalCall`), and both the predicate and its unreadable-depth
  fallback are pinned
  (`GuiDepthDecidesInsideAGuiPassAndAnUnreadableDepthFallsBackToOutside`);
- the assembler's nesting and all its recovery rules, the JSON escaping and its culture
  invariance (including a strict-parser round trip of a rich document), and the geometry
  derivations the live cell asserts against.

THE WHOLE-FLIGHT NUMBERS, before the per-premise readings: `patched=17/17` on all 56 arms,
with `ok=17 already=0 failed=0 of=17` on every `[GuiTree] patches applied` line and
`patches removed` counted once per arm (23 / 5 per run) - so every interception installed
and none leaked. ZERO `[Parsek][WARN][GuiTree]` or `[ERROR][GuiTree]` lines in any of the
four runs: no arm timeout, no throwing arm, no double patch, no probe failure. Every
anomaly counter zero in every dump - `strayEnds`, `autoClosedByEnd`, `autoClosedByClip`,
`autoClosedByRect`, `unclosedAtEnd`, `recordFaults`, `droppedOverCap`, `rectRuleInert` -
across 21,010 nodes and 26,786 events, so the assembler's recovery rules never had to fire
on a real Parsek window. `guiMatrix.identity = true` everywhere. Node counts per dump ran
from 34 (`ksc-main-basic`) to 4253 (`ksc-testrunner-advanced`).

1. **Mono inlining: MEASURED, per funnel, by HITS** - which is the direct test, since a
   funnel with hits > 0 was reached through its detour rather than inlined at those call
   sites. Over the two lanes' first attempts: `GUI.DoWindow` 79 (26 bytes of IL, the target
   this premise worried about most) against `GUI.CallWindowDelegate` 79;
   `GUILayoutUtility.BeginLayoutGroup` 2809 against `EndLayoutGroup` 2809;
   `GUI.BeginScrollView` 19 against `EndScrollView` 19; `GUI.DoLabel` 4923,
   `GUI.DoControl` 2452, `GUI.DoButton` 2092 (33 bytes), `GUI.DoToggle` 360 (34),
   `GUI.Box` 131, `GUI.DoTextField` 59, `GUI.DoRepeatButton` 29, `GUI.Slider` 14,
   `GUI.DoButtonGrid` 9. Every Begin/End pair balanced EXACTLY, and no funnel in any dump
   read `patched: false`. So the designed fallbacks - window scope from
   `CallWindowDelegate`, a group's close from the clip depth, kind hints degrading to
   style-name classification - were never needed, and the `GuiTreeGeometry.Inspect` /
   `MeasureScrollOffset` window-ID keying (title secondary) was belt and braces rather
   than load-bearing. `GUI.BeginGroup` / `GUI.EndGroup` are the one pair still UNMEASURED:
   both read 0 hits in all 56 dumps and 0 in the cell's own probe
   (`beginGroupHits=0 endGroupHits=0`), so nothing the census drew calls them - see the
   residue below. The escape hatch nobody needed remains `GUIStyle.Draw`, the instance
   method every leaf's Repaint path calls, which yields rect and style for everything at
   the cost of the control kind.
2. **`GUIToScreenRect` inside a window callback: SETTLED, to the pixel.** The live cell's
   PASS line reads `box=[60,60,320,300] (measured contentOrigin+argSize)
   declared=[60,60,320,300]` - the conversion inside a `GUI.Window` callback agrees with
   the rect the probe declared. The double-count fix that landed before the flight (both
   clip-container patch classes converting in a `Prefix`, before `GUIClip.Push`) is
   therefore correct as shipped and not merely plausible.
3. **The scroll view's clip offset DOES reach that conversion.** Same PASS line:
   `row.y=181 scrollView.y=202 offsetAbove=21` - a deliberately scrolled row measured 21 px
   ABOVE its own viewport, which is what a live scroll offset looks like and what a
   swallowed one would not.
4. **The reflection probes all resolved.** `guiDepth=0` on every one of the 56 arms, so the
   `GUIUtility.guiDepth` ICall behind the inside-OnGUI guard answered on the Windows CLR
   rather than falling back to "not inside a GUI pass" (`guiDepth=-1`); `clipProbe=delegate`
   on every arm (premise 6); and the `GUILayoutEntry.rect` / `GUILayoutGroup.isVertical`
   reflection gave 2809 layout-group nodes real rects. The fail-soft behaviours - clip
   depth -1, zero rects, one Warn each, re-resolution at every arm - stay in place and
   stay untested live, which is the right way round.
5. **Cost while armed: STILL UNMEASURED**, along with the arm-time and disarm-time Harmony
   codegen (17 dynamic methods generated and swept per capture, paid twice inside the frame
   that arms and the LateUpdate that flushes). Nothing in the flight instrumented either.
   It does not matter for a one-frame capture, and it scales with the funnel count rather
   than the window size, so a bigger capture does not make it worse; what a future
   measurement would size against is the largest dump the flight wrote,
   `ksc-testrunner-advanced` at 4253 nodes. A caller arming every few frames would pay it
   continuously and should hold the patches instead - still not a mode the recorder offers.
6. **The clip-depth probe binds as a DELEGATE on this runtime.** `clipProbe=delegate` on
   all 56 arms: `Delegate.CreateDelegate` over the `GUIClip.Internal_GetCount` ECall is
   accepted inside KSP, so the `MethodInfo.Invoke` fallback (`GuiTreeRecorder.BindIntProbe`,
   two allocations per recorded control) exists for a refusal that did not happen here. It
   stays, because the refusal is real on other hosts - the xUnit host raises exactly that
   on the sibling `guiDepth` probe - and because the alternative to a fallback is losing
   the clip depths of a whole capture. Headlessly pinned as before
   (`GuiTreeClipProbeBindingTests`).

THE OFFLINE VIEWER READS A REAL DUMP, which it had never had an input for:
`tools/gui_tree_view.py --batch` rendered all 23 of GUI-1's into `<label>.gui.html` plus
`gui-tree-index.html`, in the run's own shots directory.

### Residue after the first flight

- **`GUI.BeginGroup` / `GUI.EndGroup` inlining.** Zero hits across all 56 dumps and the
  cell's probe, so the 14-byte `EndGroup` the design EXPECTS Mono to inline was never
  exercised either way, and its designed clip-depth fallback never ran
  (`autoClosedByClip=0` throughout). A zero is not a bypass - a bypass shows up as an
  unbalanced pair or a stray end, and every such counter is zero - it is an absence of
  calls: nothing Parsek or stock drew in those frames uses `GUI.BeginGroup`. Measuring it
  needs a surface that calls it, which would have to be written for the purpose. Nothing
  rests on it: the clip-depth rule is the recovery path, and it is exercised headlessly.
- **A per-window `GUI.matrix`.** The flight proves only that on `stock-minimal` there is
  one matrix and it is the identity. The header still reads the matrix ONCE, from whichever
  `OnGUI` container drew first, so an install where another addon scales its own window
  would mis-size that addon's nodes. See "Known gaps".
- **`Toolbar` / `SelectionGrid` cells.** `GUI.DoButtonGrid` fired 9 times, so the funnel is
  intercepted and the grid is recorded - as ONE node, because the per-cell rects are
  computed in its private `CalcMouseRects` and the cells draw through `GUIStyle.Draw`. The
  flight neither changed nor refuted this. See "Known gaps".
- **Cost while armed**, as premise 5 above.

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
  This is why the live cell's probe marks its scroll rows `parsekscroll-<i>`, OUTSIDE the
  `parsek-probe-` containment marker: a clipped row is legitimately outside the window's
  content box, so marking it would have made the containment check fail on correct
  behaviour. The scroll-offset reading keys on the full row text instead.
- **PasswordField records the MASKED content.** `secureText` is deliberately not read.
- **`GUI.matrix` is read ONCE, when the capture opens.** It comes from whichever `OnGUI`
  container drew first in the armed frame, and a capture is process-wide, so a per-window
  matrix set by another addon - a scaled MechJeb or KER window next to an unscaled Parsek
  one - is not represented: those nodes' sizes are scaled by the FIRST container's m00 /
  m11. Nothing in KSP or Parsek sets a `GUI.matrix` today, the header records the one that
  was read, and the recorder Warns when it is not the identity.
- **A styled `GUILayout.BeginHorizontal` / `BeginVertical` emits a `box` leaf** with the
  group's own rect, because that is literally how Unity draws the group background
  (`GUI.Box(group.rect, content, style)`). Since the group node is now opened from
  `BeginLayoutGroup`'s postfix - which runs BEFORE the caller draws that background - the
  box arrives as the group's FIRST CHILD, carrying the group's own rect. Real, not a
  duplicate. (Under the old `GUILayout.BeginHorizontal` patch it arrived as the preceding
  SIBLING instead.)
- **A layout group carries no `text`.** `GUILayoutUtility.BeginLayoutGroup` never sees the
  caller's `GUIContent`; a styled group's content shows up on the `box` leaf above.
- **One capture is one frame, and it is process-wide.** A window that only draws on some
  frames, or a control behind a hover state, needs the arm to coincide with it - and every
  other mod's windows are captured alongside Parsek's.

## Files

| File | Role |
|---|---|
| `Source/Parsek/GuiTreeModel.cs` | pure: `GuiRect`, `GuiTreeEvent`, `GuiTreeNode`, `GuiTreeResult`, `GuiTreeAssembler` |
| `Source/Parsek/GuiTreeJson.cs` | pure: hand-rolled invariant-culture JSON writer |
| `Source/Parsek/GuiTreeGeometry.cs` | pure: the containment / per-kind / scroll-offset derivations the live cell asserts |
| `Source/Parsek/GuiTreeFunnels.cs` | the 17 target signatures, wire names, hit counters, arm-time `patched` snapshot |
| `Source/Parsek/GuiTreeRecorder.cs` | arm / record / flush, the Unity seam, and `GuiTreeRecorderPump` |
| `Source/Parsek/Patches/GuiTreeRecorderPatches.cs` | the applier + the 17 attribute-less Harmony patch classes |
| `Source/Parsek/TestCommands/TestCommandDumpGuiTree.cs` | pure: the `DumpGuiTree` seam verb's arg parse, poll decision and payload |
| `Source/Parsek/TestCommands/ParsekTestCommandAddon.DumpGuiTree.cs` | that verb's applier: the arm, the recorder polls, the file stat |
| `Source/Parsek/InGameTests/GuiTreeDumpImguiTest.cs` | the live `GuiTree` cell + its probe window |
| `Source/Parsek.Tests/GuiTree*Tests.cs` | headless coverage of everything above that is pure |
| `harness/tools/gui_tree_view.py` | the offline viewer |
| `harness/lib/test_gui_tree_view.py` | its unit tests (under `lib/`, so CI runs them) |
