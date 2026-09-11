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
        {
            request = default(UiPointerRequest);

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
                request = new UiPointerRequest { Park = true, X = ParkX, Y = ParkY };
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
            request = new UiPointerRequest { Park = false, X = x, Y = y };
            rejectReason = null;
            return true;
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
            };
        }
    }
}
