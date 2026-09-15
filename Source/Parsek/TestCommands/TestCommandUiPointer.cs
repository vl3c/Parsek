using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>What one <c>UiAction op=pointer</c> call asks for.</summary>
    internal struct UiPointerRequest
    {
        /// <summary>True for <c>park=true</c>: go to the client corner and take no
        /// coordinates.</summary>
        internal bool Park;

        /// <summary>Target X in GAME CLIENT pixels, left to right.</summary>
        internal float X;

        /// <summary>Target Y in GAME CLIENT pixels, TOP-DOWN - the same frame the GUI-tree
        /// dump's <c>rect</c> uses, and NOT Unity's bottom-up mouse frame.</summary>
        internal float Y;

        /// <summary>True for <c>focus=true</c>: bring the game window to the FOREGROUND
        /// before the move. Default false, so every lane written before the arg existed is
        /// byte-identical.</summary>
        internal bool Focus;

        /// <summary>True for <c>nudge=true</c>: after landing, synthesise one relative
        /// (+1,0) then (-1,0) mouse move so the window receives a real
        /// <c>WM_MOUSEMOVE</c> rather than only a new cursor position. Default
        /// false.</summary>
        internal bool Nudge;
    }

    /// <summary>
    /// How <c>focus=true</c> ended up: which rung of the foreground ladder answered, and
    /// whether the game window really is the foreground window afterwards. Reported on the
    /// wire because the whole question
    /// GUI-CENSUS-POINTER-LANDS-BUT-HOVER-DOES-NOT-PAINT asks is whether foreground was
    /// the missing half, and an unreported "we tried" would settle nothing.
    /// </summary>
    internal enum UiPointerFocusOutcome
    {
        /// <summary><c>focus=</c> was absent or false: nothing was attempted.</summary>
        NotRequested = 0,

        /// <summary>The game window was ALREADY the foreground window, so no call was
        /// made. The common case on an operator desktop that just launched the game, and
        /// the reading that makes a failed hover NOT a foreground problem.</summary>
        Already = 1,

        /// <summary>A plain <c>SetForegroundWindow</c> took it.</summary>
        Direct = 2,

        /// <summary><c>SetForegroundWindow</c> was refused and the documented
        /// <c>AttachThreadInput</c> path took it on the retry.</summary>
        Attached = 3,

        /// <summary>Both rungs ran and <c>GetForegroundWindow</c> still names something
        /// else. NOT an error: the move and the read-back are still the verdict, and this
        /// is the measurement the census wanted.</summary>
        Refused = 4,

        /// <summary>No game window handle was resolved, so foreground could not be
        /// asked for at all (the assumed-origin fallback rung).</summary>
        NoWindow = 5,
    }

    /// <summary>
    /// Pure decision / payload half of <c>UiAction op=pointer</c>: the one op that reaches
    /// outside the process, because there is no other way to make Unity's own hit test run.
    ///
    /// <para><b>WHY A REAL CURSOR MOVE.</b> Four Parsek surfaces only ever paint with a
    /// pointer resting inside a control's rect: the IMGUI hover style, <c>GUI.tooltip</c>,
    /// the per-window <c>TooltipEchoBox</c> strip, and <c>DisabledHoverEcho</c>'s
    /// "why is this greyed out?" sentence. All four read
    /// <c>Event.current.mousePosition</c>, which Unity fills from <c>Input.mousePosition</c>
    /// during the pass - a value managed code cannot write. Synthesising an IMGUI event
    /// instead would have to be injected into one specific window's layout at one specific
    /// rect, and would prove nothing about the hit test the player's cursor drives. So the
    /// op moves the OS cursor and then CONFIRMS, from <c>Input.mousePosition</c>, that the
    /// move reached Unity.</para>
    ///
    /// <para><b>TWO COORDINATE FRAMES, and the conversion is the whole hazard.</b> The
    /// wire takes GAME CLIENT pixels with y measured DOWN from the top-left, because that
    /// is the frame the GUI-tree dump's <c>rect</c> reports and therefore the frame a
    /// <c>${step.cx}</c> / <c>${step.cy}</c> chain from <c>op=find</c> produces. Unity's
    /// <c>Input.mousePosition</c> is the same client box with y measured UP from the
    /// bottom-left, so the read-back is converted with
    /// <see cref="GuiYFromUnityMouseY"/> before it is compared. The OS move needs a THIRD
    /// frame - desktop pixels, y down - which the applier reaches by adding the window's
    /// client origin; that part is Windows-specific and lives there.</para>
    ///
    /// <para><b>THE OPERATOR CAVEAT, recorded rather than guarded.</b> A harness run happens
    /// on the operator's own desktop, so this op yanks his cursor mid-run. There is no way
    /// to hover without doing that: a cursor is a single machine-wide resource. The op
    /// therefore logs an Info line every time it moves the pointer, so a run whose captures
    /// look wrong can be read back against "the operator was using the mouse", and a lane
    /// that uses it says so in its header. It is NOT gated on focus: a foreground window
    /// belonging to another process does not block <c>SetCursorPos</c>, so the move lands
    /// either way - but Unity only updates <c>Input.mousePosition</c> while it is receiving
    /// input, so an unfocused game answers <see cref="NotAppliedReason"/> instead of
    /// silently photographing a hover that never happened.</para>
    /// </summary>
    internal static class TestCommandUiPointer
    {
        // ----- arg keys and tokens -----

        internal const string XArg = "x";
        internal const string YArg = "y";
        internal const string ParkArg = "park";

        /// <summary>
        /// <c>focus=true|false</c> (default false): bring the game window to the foreground
        /// before moving.
        ///
        /// <para><b>WHY IT IS OPT-IN AND WHY IT IS NOT THE DEFAULT.</b> This op already
        /// reaches outside the process; foreground reaches further - it STEALS focus from
        /// whatever the operator is doing on his own desktop. It is also, on the evidence,
        /// not obviously the missing half: the four census hover captures all READ BACK
        /// their cursor position, which by this op's own contract means the game was
        /// sampling input. So it ships as a measurement a lane can ask for, with the
        /// outcome reported either way (<see cref="UiPointerFocusOutcome"/>), rather than
        /// as a behaviour change under every existing lane.</para>
        /// </summary>
        internal const string FocusArg = "focus";

        /// <summary>
        /// <c>nudge=true|false</c> (default false): after the cursor lands, send ONE
        /// relative <c>(+1,0)</c> then <c>(-1,0)</c> synthetic move.
        ///
        /// <para>The point is the EVENT, not the position: <c>SetCursorPos</c> warps the
        /// cursor, and Unity's per-frame position sample follows a warp while the window's
        /// mouse event stream does not. A relative <c>SendInput</c> produces a real
        /// <c>WM_MOUSEMOVE</c>, and the pair cancels out so the cursor ends where the move
        /// put it - the hovered control is unchanged and the read-back still compares
        /// against the commanded point.</para>
        /// </summary>
        internal const string NudgeArg = "nudge";

        /// <summary>The two accepted values of both flag args. Shared with
        /// <see cref="ParkTrueToken"/> / <see cref="ParkFalseToken"/> deliberately: one
        /// boolean spelling across the op, so a spec author learns it once.</summary>
        internal const string FlagTrueToken = "true";
        internal const string FlagFalseToken = "false";

        /// <summary>The one accepted <c>park=</c> value. <c>park=false</c> is accepted too
        /// and means "ordinary x/y move", so a generated spec can carry the key
        /// unconditionally.</summary>
        internal const string ParkTrueToken = "true";
        internal const string ParkFalseToken = "false";

        // ----- reject / error reasons -----

        /// <summary>Neither <c>park=true</c> nor an <c>x</c>/<c>y</c> pair. There is no
        /// default position: moving the cursor somewhere the caller did not name would
        /// silently change what every later capture photographs.</summary>
        internal const string ArgMissingReason = "pointer-arg-missing";

        /// <summary>An <c>x</c> / <c>y</c> / <c>park</c> value that did not parse as an
        /// invariant-culture float in range, or a <c>park</c> outside {true,false}.</summary>
        internal const string ArgInvalidReason = "pointer-arg-invalid";

        /// <summary><c>park=true</c> AND an <c>x</c> or <c>y</c>. Refused rather than
        /// resolved by precedence: the two say different things about where the pointer
        /// ends up, and guessing would make a capture's hover state depend on which half of
        /// the step the reader believed.</summary>
        internal const string ArgConflictReason = "pointer-arg-conflict";

        /// <summary>A coordinate outside the live client box. REJECTED: a cursor parked off
        /// the game window hovers nothing, so every capture after it would be silently
        /// hover-free under a step that claims otherwise.</summary>
        internal const string OffScreenReason = "pointer-off-screen";

        /// <summary>PRE-CALL: the game window's client origin could not be resolved, so a
        /// client coordinate cannot be turned into a desktop one. ERROR - the op is
        /// implemented, the host did not answer.</summary>
        internal const string WindowUnresolvedReason = "pointer-window-unresolved";

        /// <summary>PRE-CALL: the cursor-move entry point is unavailable on this platform
        /// (the P/Invoke is <c>user32</c>). ERROR naming the platform, so a Linux reader
        /// does not hunt a Parsek defect.</summary>
        internal const string UnsupportedPlatformReason = "pointer-unsupported-platform";

        /// <summary>POST-SETTLE: the move was issued and <c>Input.mousePosition</c> is still
        /// somewhere else by more than <see cref="ReadBackTolerancePx"/>. The live causes
        /// are a game window that is not receiving input (unfocused, minimised) and a
        /// display-scaling mismatch; both mean the hover this step exists to produce did NOT
        /// happen, so it is an ERROR rather than an OK over a wrong picture.</summary>
        internal const string NotAppliedReason = "pointer-not-applied";

        // ----- bounds and tolerance -----

        /// <summary>Bound on a commanded coordinate. The live client box is the real gate
        /// (<see cref="OffScreenReason"/>); this only keeps a mis-typed exponent out of the
        /// conversion arithmetic.</summary>
        internal const float MaxCoord = 16384f;

        /// <summary>
        /// Read-back tolerance in pixels.
        ///
        /// <para>Two px, and it is not a guess. The client-to-Unity conversion crosses a
        /// y-flip whose exact off-by-one depends on whether the bottom row is 0 or 1
        /// (<see cref="GuiYFromUnityMouseY"/> uses the <c>Event.current.mousePosition</c>
        /// convention, <c>height - y</c>), and <c>SetCursorPos</c> itself lands on whole
        /// desktop pixels. One px would make the op flake on that boundary; anything larger
        /// would let a pointer land off a one-pixel-tall control's rect and still report
        /// OK.</para>
        /// </summary>
        internal const float ReadBackTolerancePx = 2f;

        /// <summary>Where <c>park=true</c> goes: the client's top-left corner. Chosen over
        /// any other corner because it is the one point guaranteed to exist in every window
        /// size, and because the Parsek main window's own default is inset from it
        /// (x=20,y=100), so parking there hovers nothing in a default layout.</summary>
        internal const float ParkX = 0f;
        internal const float ParkY = 0f;

        // ----- parse -----

        /// <summary>
        /// Parses <c>op=pointer</c>'s args. Exactly one of the two shapes must be present:
        /// <c>park=true</c>, or BOTH <c>x</c> and <c>y</c>. A lone <c>x</c> is
        /// <see cref="ArgMissingReason"/> and not a half-move.
        /// </summary>
        internal static bool TryParseRequest(string rawX, string rawY, string rawPark,
                                             out UiPointerRequest request,
                                             out string rejectReason)
            => TryParseRequest(rawX, rawY, rawPark, null, null, out request,
                               out rejectReason);

        /// <summary>
        /// The full parse, with the two opt-in flags.
        ///
        /// <para>Both flags are ORTHOGONAL to the park / x-y shape and to each other: a
        /// <c>park=true focus=true</c> is a legitimate request (take the window foreground,
        /// then park the cursor where it hovers nothing), and so is
        /// <c>focus=true nudge=true</c>, which is the pair the census flies. Neither flag
        /// can make an otherwise-invalid shape valid, so the shape checks stay exactly where
        /// they were and the flags are parsed first only so an invalid flag is reported as
        /// such rather than being masked by a shape reject.</para>
        /// </summary>
        internal static bool TryParseRequest(string rawX, string rawY, string rawPark,
                                             string rawFocus, string rawNudge,
                                             out UiPointerRequest request,
                                             out string rejectReason)
        {
            request = default(UiPointerRequest);

            bool focus, nudge;
            if (!TryParseFlag(rawFocus, out focus) || !TryParseFlag(rawNudge, out nudge))
            {
                rejectReason = ArgInvalidReason;
                return false;
            }

            bool park = false;
            if (rawPark != null)
            {
                if (rawPark == ParkTrueToken) park = true;
                else if (rawPark == ParkFalseToken) park = false;
                else
                {
                    rejectReason = ArgInvalidReason;
                    return false;
                }
            }

            bool hasAnyCoord = rawX != null || rawY != null;
            if (park && hasAnyCoord)
            {
                rejectReason = ArgConflictReason;
                return false;
            }
            if (park)
            {
                request = new UiPointerRequest
                {
                    Park = true, X = ParkX, Y = ParkY, Focus = focus, Nudge = nudge,
                };
                rejectReason = null;
                return true;
            }
            if (rawX == null || rawY == null)
            {
                rejectReason = ArgMissingReason;
                return false;
            }

            float x, y;
            if (!TryParseFinite(rawX, out x) || !TryParseFinite(rawY, out y))
            {
                rejectReason = ArgInvalidReason;
                return false;
            }
            if (x < -MaxCoord || x > MaxCoord || y < -MaxCoord || y > MaxCoord)
            {
                rejectReason = ArgInvalidReason;
                return false;
            }
            request = new UiPointerRequest
            {
                Park = false, X = x, Y = y, Focus = focus, Nudge = nudge,
            };
            rejectReason = null;
            return true;
        }

        /// <summary>Parses one optional boolean flag. Absent is FALSE; anything outside
        /// {true,false} fails, so a <c>focus=1</c> is a REJECTED rather than a silently
        /// ignored arg (the <c>park=</c> rule).</summary>
        internal static bool TryParseFlag(string raw, out bool value)
        {
            value = false;
            if (raw == null) return true;
            if (raw == FlagTrueToken) { value = true; return true; }
            if (raw == FlagFalseToken) { value = false; return true; }
            return false;
        }

        private static bool TryParseFinite(string raw, out float value)
        {
            value = 0f;
            float parsed;
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture,
                                out parsed))
                return false;
            if (float.IsNaN(parsed) || float.IsInfinity(parsed))
                return false;
            value = parsed;
            return true;
        }

        // ----- geometry -----

        /// <summary>
        /// Whether a commanded client point lies inside the live client box. The box is
        /// half-open on the far edges (<c>x &lt; width</c>): a cursor at exactly
        /// <c>width</c> is on the next pixel column, which is outside the window.
        /// </summary>
        internal static bool IsInsideClient(float x, float y, int screenWidth, int screenHeight)
            => screenWidth > 0 && screenHeight > 0
               && x >= 0f && y >= 0f && x < screenWidth && y < screenHeight;

        /// <summary>
        /// Converts Unity's bottom-up <c>Input.mousePosition.y</c> into the top-down client
        /// Y this op's wire uses. This is the SAME expression Unity applies when it fills
        /// <c>Event.current.mousePosition</c> for an IMGUI pass, which is what makes the
        /// read-back a statement about the hit test rather than about the cursor alone.
        /// </summary>
        internal static float GuiYFromUnityMouseY(int screenHeight, float unityMouseY)
            => screenHeight - unityMouseY;

        /// <summary>Read-back predicate: both axes within
        /// <see cref="ReadBackTolerancePx"/> of the commanded point, measured in the
        /// top-down client frame.</summary>
        internal static bool LandedWithinTolerance(
            float commandedX, float commandedY, float observedX, float observedGuiY)
            => Math.Abs(commandedX - observedX) <= ReadBackTolerancePx
               && Math.Abs(commandedY - observedGuiY) <= ReadBackTolerancePx;

        // ----- settle -----

        /// <summary>
        /// One settle poll of <c>op=pointer</c>. It POLLS until the read-back agrees,
        /// rather than reading it once.
        ///
        /// <para><b>WHY THIS IS NOT THE SHARED
        /// <c>TestCommandUiAction.DecideSettlePoll</c>.</b> That one answers Settled the
        /// moment a frame has been drawn, and the applier then read
        /// <c>Input.mousePosition</c> ONCE. But what is being waited for here is not a
        /// frame; it is the OS cursor move reaching Unity's input state, which is a
        /// different pipeline. <c>SetCursorPos</c> returns as soon as the OS has moved the
        /// cursor, and Unity samples the new position on its own next input poll - when
        /// those land one frame apart the single read saw the OLD position and the op
        /// answered <see cref="NotAppliedReason"/>, a hard ERROR, over a move that was
        /// about to be correct. One poll cannot tell that apart from an unfocused game;
        /// several can.</para>
        ///
        /// <para><b>FRAMES BECOME A FLOOR, not the signal.</b> The op still waits for at
        /// least <paramref name="minFrames"/> drawn frames before it will answer OK: the
        /// hover this op exists to produce is painted during an IMGUI pass, and a cursor
        /// that arrived without one has hovered nothing yet. Landing is the ADDITIONAL
        /// condition, so Settled needs both - the same shape
        /// <c>TryCompleteUiActionFind</c> uses, where a capture arriving is the signal and
        /// the frame count is not.</para>
        ///
        /// <para><b>ORDER, the <c>DecideSettlePoll</c> rule.</b> Settled is decided BEFORE
        /// the budget, so a landing on the very poll the budget expired is a success rather
        /// than an ERROR over state that is already correct. A budget expiry with the
        /// cursor still elsewhere stays <see cref="NotAppliedReason"/> - the honest
        /// reading, reported with the last observed position.</para>
        /// </summary>
        /// <param name="landed"><see cref="LandedWithinTolerance"/> for THIS poll's
        /// reading.</param>
        /// <param name="framesElapsed">Frames drawn since the move was issued.</param>
        /// <param name="minFrames">The frame floor (the applier passes
        /// <c>TestCommandUiAction.SettleFrames</c>).</param>
        /// <param name="budgetExpired">Whether the verb's deferral budget has run
        /// out.</param>
        internal static UiActionSettleOutcome DecidePoll(bool landed, int framesElapsed,
                                                        int minFrames, bool budgetExpired)
        {
            if (landed && framesElapsed >= minFrames) return UiActionSettleOutcome.Settled;
            if (budgetExpired) return UiActionSettleOutcome.TimedOut;
            return UiActionSettleOutcome.NotYet;
        }

        // ----- payload -----

        /// <summary>
        /// OK payload: <c>op=pointer x= y= park= sx= sy= via=</c>.
        ///
        /// <para><c>x</c> / <c>y</c> are the READ-BACK client point, not the request, for
        /// the same reason <c>op=rect</c> reports the settled rect: a caller that wants to
        /// know where the cursor ended up must not be handed back its own argument.
        /// <c>sx</c> / <c>sy</c> are the desktop point the move was issued at and
        /// <c>via</c> names how the client origin was resolved, so a one-off landing failure
        /// on a multi-monitor or scaled desktop is diagnosable from the response alone.</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildPayload(
            float observedX, float observedGuiY, bool park, int screenX, int screenY,
            string via)
            => BuildPayload(observedX, observedGuiY, park, screenX, screenY, via,
                            false, false, UiPointerFocusOutcome.NotRequested, false, null);

        /// <summary>
        /// The full payload, with what the two opt-in flags DID.
        ///
        /// <para>Five further keys, and every one is present on every answer (the
        /// <c>describe</c> rule - a missing key is indistinguishable from an older seam
        /// build): <c>focus=</c> / <c>nudge=</c> are what was ASKED for,
        /// <c>fgOutcome=</c> names the rung that answered, <c>fg=</c> is the direct
        /// question <c>GetForegroundWindow() == the game hwnd</c> AFTER the call, and
        /// <c>tooltip=</c> is the hover-echo strip's text as of the settled frame, which is
        /// the thing the whole flag pair exists to make non-empty.</para>
        /// </summary>
        /// <param name="focusRequested">The <c>focus=</c> arg as given.</param>
        /// <param name="nudgeApplied">Whether the relative move was actually sent (false
        /// when <c>nudge=</c> was absent OR when the send was refused).</param>
        /// <param name="focus">Which rung answered.</param>
        /// <param name="foregroundIsGame"><c>GetForegroundWindow()</c> equals the resolved
        /// game window after the call. False when no handle was resolved.</param>
        /// <param name="tooltip">The strip text, or null / empty for none.</param>
        /// <param name="tooltipFrame">The frame that text was observed in, so a reader can
        /// tell a live hover from a value a strip left behind minutes ago. Nothing resets
        /// the latch on a scene change, which is exactly why the frame is on the
        /// wire.</param>
        internal static List<KeyValuePair<string, string>> BuildPayload(
            float observedX, float observedGuiY, bool park, int screenX, int screenY,
            string via, bool focusRequested, bool nudgeApplied,
            UiPointerFocusOutcome focus, bool foregroundIsGame, string tooltip,
            int tooltipFrame = 0)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("op", TestCommandUiAction.PointerOpToken),
                new KeyValuePair<string, string>("x", observedX.ToString("F0", ic)),
                new KeyValuePair<string, string>("y", observedGuiY.ToString("F0", ic)),
                new KeyValuePair<string, string>("park", park ? "true" : "false"),
                new KeyValuePair<string, string>("sx", screenX.ToString(ic)),
                new KeyValuePair<string, string>("sy", screenY.ToString(ic)),
                new KeyValuePair<string, string>("via", via ?? "unknown"),
                new KeyValuePair<string, string>(
                    "focus", focusRequested ? "true" : "false"),
                new KeyValuePair<string, string>("nudge", nudgeApplied ? "true" : "false"),
                new KeyValuePair<string, string>("fgOutcome", FocusOutcomeToken(focus)),
                new KeyValuePair<string, string>("fg", foregroundIsGame ? "true" : "false"),
                new KeyValuePair<string, string>(
                    "tooltip", string.IsNullOrEmpty(tooltip)
                        ? TooltipEchoStripLatch.EmptyTextToken
                        : TooltipEchoStripLatch.FormatForLog(tooltip)),
                new KeyValuePair<string, string>(
                    "tooltipFrame", tooltipFrame.ToString(ic)),
            };
        }

        /// <summary>The wire token for a focus outcome. Lower-case hyphenless words, the
        /// <c>via=</c> spelling, so a log contract can pin one.</summary>
        internal static string FocusOutcomeToken(UiPointerFocusOutcome outcome)
        {
            switch (outcome)
            {
                case UiPointerFocusOutcome.Already: return "already";
                case UiPointerFocusOutcome.Direct: return "direct";
                case UiPointerFocusOutcome.Attached: return "attached";
                case UiPointerFocusOutcome.Refused: return "refused";
                case UiPointerFocusOutcome.NoWindow: return "no-window";
                default: return "not-requested";
            }
        }

        /// <summary>
        /// Which rung of the foreground ladder a live attempt landed on, from the three
        /// facts the applier can observe. Pure so the ladder's own reading is testable
        /// without a window manager.
        /// </summary>
        /// <param name="requested">The <c>focus=</c> arg.</param>
        /// <param name="haveWindow">Whether a game window handle was resolved at all.</param>
        /// <param name="wasAlreadyForeground"><c>GetForegroundWindow()</c> equalled the game
        /// window BEFORE anything was called.</param>
        /// <param name="directTookIt">Foreground equalled the game window after the plain
        /// <c>SetForegroundWindow</c>.</param>
        /// <param name="attachTookIt">Foreground equalled it after the
        /// <c>AttachThreadInput</c> retry.</param>
        internal static UiPointerFocusOutcome ClassifyFocus(
            bool requested, bool haveWindow, bool wasAlreadyForeground,
            bool directTookIt, bool attachTookIt)
        {
            if (!requested) return UiPointerFocusOutcome.NotRequested;
            if (!haveWindow) return UiPointerFocusOutcome.NoWindow;
            if (wasAlreadyForeground) return UiPointerFocusOutcome.Already;
            if (directTookIt) return UiPointerFocusOutcome.Direct;
            if (attachTookIt) return UiPointerFocusOutcome.Attached;
            return UiPointerFocusOutcome.Refused;
        }
    }
}
