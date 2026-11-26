using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>How the <c>EvaMove</c> verb moves the kerbal.</summary>
    internal enum EvaMoveMode
    {
        /// <summary>The jetpack: deploy it if stowed, thrust for the duration, optionally stow.</summary>
        Jetpack,

        /// <summary>A walk on the ground in the kerbal's own horizontal frame.</summary>
        Walk,
    }

    /// <summary>A direction in the kerbal's own frame, frozen in world space at the arm.</summary>
    internal enum EvaMoveDir
    {
        Forward,
        Back,
        Left,
        Right,
        Up,
        Down,
    }

    /// <summary>Where the two-phase move is.</summary>
    internal enum EvaMovePhase
    {
        /// <summary>The input is held: the patch feeds the direction every physics frame.</summary>
        Driving,

        /// <summary>The input is released; a requested stow is pressed, then the scene settles.</summary>
        Released,
    }

    /// <summary>One completion poll's outcome.</summary>
    internal enum EvaMoveDecision
    {
        KeepWaiting,

        /// <summary>The duration elapsed: release the input.</summary>
        ReleaseDrive,

        /// <summary>Press the jetpack toggle to stow the pack.</summary>
        FireStow,

        CompleteOk,

        /// <summary>The kerbal vessel went away (killed, boarded by something else).</summary>
        KerbalLost,

        /// <summary>The kerbal ragdolled while the input was held.</summary>
        Ragdolled,

        /// <summary>The input was held for the whole duration and nothing happened: the jetpack
        /// never read thrusting, or the walk never moved the kerbal.</summary>
        NoEffect,

        /// <summary>The stow toggle was pressed and the pack stayed out.</summary>
        StowRefused,

        Timeout,
    }

    /// <summary>
    /// Pure core of the <c>EvaMove</c> seam verb: drive the active EVA kerbal with the
    /// player's own movement input for a set number of game seconds, by jetpack or on foot,
    /// so the recorder captures a live jetpack flight or walk (test-gap list 2026-11-18,
    /// "Walking and jetpack EVA recorded live").
    ///
    /// <para>STOCK GROUND TRUTH (decompiled KSP 1.12.5 <c>KerbalEVA</c>):</para>
    /// <list type="bullet">
    /// <item><c>FixedUpdate</c> zeroes <c>tgtRpos</c> and <c>packTgtRPos</c>, calls
    /// <c>HandleMovementInput</c> (the key reads: <c>EVA_Pack_*</c> add the kerbal's
    /// <c>transform.forward / right / up</c> to <c>packTgtRPos</c>, <c>EVA_forward</c> etc.
    /// build <c>tgtRpos</c>), then runs the FSM. So input can only be fed between those two
    /// calls: <see cref="EvaMoveInputPatch"/> is a Postfix on <c>HandleMovementInput</c>,
    /// armed for one kerbal part by this verb and inert otherwise.</item>
    /// <item>The jetpack: <c>UpdatePackLinear</c> (run by the floating FSM states) applies
    /// <c>packTgtRPos * thrustPercentage * linPower</c> as a part force when the pack is
    /// deployed, fuelled and the kerbal not ragdolled, and adds the burn to
    /// <c>fuelFlowRate</c>; <c>JetpackIsThrusting</c> is recomputed every FixedUpdate from
    /// that flow. That public field is exactly what the recorder's EVA poll debounces into
    /// <c>EvaJetpackThrustStarted</c> / <c>Stopped</c>, so the verb's own proof of effect is
    /// the same field, latched on any poll.</item>
    /// <item>The pack toggle: the public <c>ToggleJetpack()</c> runs the FSM event
    /// <c>On_packToggle</c> (registered on <c>st_idle_gr</c>, <c>st_idle_b_gr</c>,
    /// <c>st_idle_fl</c>, <c>st_weldAcquireHeading</c>; any other state is a silent no-op),
    /// which flips <c>JetpackDeployed</c> in the same call. So the verb re-reads the flag after
    /// the press and refuses with no side effect when it did not move.</item>
    /// <item>The walk: <c>SetWaypoint(pos)</c>, the public call stock's own kerbal-follow
    /// uses, sets <c>tgtRpos</c> / <c>tgtFwd</c> / <c>tgtUp</c> exactly as the walk keys do;
    /// the ground FSM walks while <c>tgtRpos</c> is non-zero.</item>
    /// </list>
    ///
    /// <para>GRAMMAR: <c>EvaMove mode=jetpack|walk dir=forward|back|left|right|up|down
    /// seconds=&lt;0.1..30&gt; [stow=true|false]</c>. <c>up</c> / <c>down</c> are jetpack only;
    /// <c>stow</c> is jetpack only (default false). The direction is the kerbal's own frame at
    /// the arm, frozen in world space for the whole move, so a burst and its reverse cancel
    /// even if the kerbal turns. TWO-PHASE, RequiresFlight with the not-eva defer.</para>
    ///
    /// <para>OK payload: <c>kerbal= evaPid= mode= dir= seconds= startUT= endUT= effect=
    /// deployed= refPid= refDistStart= refDistEnd= refSpeed=</c>. <c>startUT</c> /
    /// <c>endUT</c> are the game UTs the input was held between, so a later step can warp the
    /// replay to them (<c>${label.startUT}</c>); the <c>ref*</c> keys measure the kerbal
    /// against the nearest loaded non-EVA vessel (<c>refPid=0</c> and <c>-1</c> distances
    /// when there is none), which is what an <c>EvaBoard</c> after the move needs.</para>
    /// </summary>
    internal static class TestCommandEvaMove
    {
        internal const string Verb = "EvaMove";

        internal const string ModeKey = "mode";
        internal const string DirKey = "dir";
        internal const string SecondsKey = "seconds";
        internal const string StowKey = "stow";

        internal static readonly string[] ArgKeys = { "mode", "dir", "seconds", "stow" };
        internal static readonly string[] ModeValues = { "jetpack", "walk" };
        internal static readonly string[] DirValues = { "forward", "back", "left", "right", "up", "down" };

        internal const double MinSeconds = 0.1;
        internal const double MaxSeconds = 30.0;

        /// <summary>Stow presses before the verb gives up on a pack that stays out.</summary>
        internal const int MaxStowFires = 3;

        /// <summary>Polls between stow presses.</summary>
        internal const int StowRefirePolls = 30;

        /// <summary>The walk's proof of effect: the kerbal moved at least this far.</summary>
        internal const double MinWalkMeters = 0.2;

        // ----- Refusals (REJECTED, nothing changed) -----
        internal const string ModeInvalidReason = "evamove-mode-invalid";
        internal const string DirInvalidReason = "evamove-dir-invalid";
        internal const string SecondsInvalidReason = "evamove-seconds-invalid";
        internal const string StowInvalidReason = "evamove-stow-invalid";
        internal const string NotEvaReason = "evamove-not-eva";
        internal const string NoJetpackReason = "evamove-no-jetpack";
        internal const string NoFuelReason = "evamove-no-fuel";
        internal const string RagdollReason = "evamove-ragdoll";
        internal const string NotOnGroundReason = "evamove-not-on-ground";
        internal const string PackRefusedReason = "evamove-pack-refused";

        // ----- ERROR terminals (after the input was held) -----
        internal const string ThrewReason = "evamove-threw";
        internal const string KerbalLostReason = "evamove-kerbal-lost";
        internal const string RagdolledReason = "evamove-ragdolled";
        internal const string NoEffectReason = "evamove-no-effect";
        internal const string StowRefusedReason = "evamove-stow-refused";
        internal const string TimeoutReason = "evamove-timeout";

        internal static readonly string[] ArgReasons =
        {
            "evamove-mode-invalid",
            "evamove-dir-invalid",
            "evamove-seconds-invalid",
            "evamove-stow-invalid",
        };

        internal static readonly string[] Reasons =
        {
            "evamove-not-eva",
            "evamove-no-jetpack",
            "evamove-no-fuel",
            "evamove-ragdoll",
            "evamove-not-on-ground",
            "evamove-pack-refused",
        };

        internal static readonly string[] ErrorReasons =
        {
            "evamove-threw",
            "evamove-kerbal-lost",
            "evamove-ragdolled",
            "evamove-no-effect",
            "evamove-stow-refused",
            "evamove-timeout",
        };

        /// <summary>The parsed arguments of one step.</summary>
        internal struct Args
        {
            internal EvaMoveMode Mode;
            internal EvaMoveDir Dir;
            internal double Seconds;
            internal bool Stow;
        }

        /// <summary>
        /// Parses the four args, fail-closed and case-sensitive (a lenient parse would accept
        /// a spelling the harness validator rejects). Returns the refusal reason, or null.
        /// </summary>
        internal static string TryParseArgs(string mode, string dir, string seconds, string stow, out Args args)
        {
            args = default(Args);
            if (mode == "jetpack") args.Mode = EvaMoveMode.Jetpack;
            else if (mode == "walk") args.Mode = EvaMoveMode.Walk;
            else return ModeInvalidReason;

            if (!TryParseDir(dir, out args.Dir)) return DirInvalidReason;
            if (args.Mode == EvaMoveMode.Walk && (args.Dir == EvaMoveDir.Up || args.Dir == EvaMoveDir.Down))
                return DirInvalidReason;

            if (seconds == null
                || !double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out args.Seconds)
                || double.IsNaN(args.Seconds) || args.Seconds < MinSeconds || args.Seconds > MaxSeconds)
                return SecondsInvalidReason;

            if (stow == null) args.Stow = false;
            else if (stow == "true") args.Stow = true;
            else if (stow == "false") args.Stow = false;
            else return StowInvalidReason;
            if (args.Stow && args.Mode != EvaMoveMode.Jetpack) return StowInvalidReason;
            return null;
        }

        internal static bool TryParseDir(string raw, out EvaMoveDir dir)
        {
            dir = EvaMoveDir.Forward;
            switch (raw)
            {
                case "forward": dir = EvaMoveDir.Forward; return true;
                case "back": dir = EvaMoveDir.Back; return true;
                case "left": dir = EvaMoveDir.Left; return true;
                case "right": dir = EvaMoveDir.Right; return true;
                case "up": dir = EvaMoveDir.Up; return true;
                case "down": dir = EvaMoveDir.Down; return true;
                default: return false;
            }
        }

        /// <summary>The wire spelling of a direction (the inverse of <see cref="TryParseDir"/>).</summary>
        internal static string DirToken(EvaMoveDir dir) => DirValues[(int)dir];

        internal static string ModeToken(EvaMoveMode mode) => ModeValues[(int)mode];

        /// <summary>
        /// The direction as signs on the kerbal's (forward, right, up) axes, the axes the
        /// stock <c>EVA_Pack_*</c> keys add. The applier turns them into one world vector at
        /// the arm.
        /// </summary>
        internal static void AxisSigns(EvaMoveDir dir, out int forward, out int right, out int up)
        {
            forward = 0; right = 0; up = 0;
            switch (dir)
            {
                case EvaMoveDir.Forward: forward = 1; break;
                case EvaMoveDir.Back: forward = -1; break;
                case EvaMoveDir.Right: right = 1; break;
                case EvaMoveDir.Left: right = -1; break;
                case EvaMoveDir.Up: up = 1; break;
                case EvaMoveDir.Down: up = -1; break;
            }
        }

        /// <summary>
        /// The live-state refusal at the arm, in the stock key path's order (most specific
        /// first), or null to proceed. <paramref name="packDeployed"/> matters only for the
        /// fuel check: a stowed pack is deployed by the verb.
        /// </summary>
        internal static string DecideArmRefusal(EvaMoveMode mode, bool activeIsEva, bool ragdoll,
            bool hasJetpack, double fuel, bool onGround)
        {
            if (!activeIsEva) return NotEvaReason;
            if (ragdoll) return RagdollReason;
            if (mode == EvaMoveMode.Jetpack)
            {
                if (!hasJetpack) return NoJetpackReason;
                if (!(fuel > 0.0)) return NoFuelReason;
                return null;
            }
            if (!onGround) return NotOnGroundReason;
            return null;
        }

        /// <summary>
        /// One completion poll. Ordering: the lost kerbal beats everything, then the held
        /// input's own exits (ragdoll, the duration, the budget), then the released phase's
        /// proof of effect, the requested stow and the settle.
        /// </summary>
        internal static EvaMoveDecision Decide(EvaMovePhase phase, bool kerbalAlive, bool ragdoll,
            double ut, double endUT, bool effectSeen, bool stowRequested, bool packDeployed,
            int stowFires, int pollsSinceStow, bool sceneSettled, double elapsed, double budget)
        {
            if (!kerbalAlive) return EvaMoveDecision.KerbalLost;
            if (phase == EvaMovePhase.Driving)
            {
                if (ragdoll) return EvaMoveDecision.Ragdolled;
                if (ut >= endUT) return EvaMoveDecision.ReleaseDrive;
                return elapsed >= budget ? EvaMoveDecision.Timeout : EvaMoveDecision.KeepWaiting;
            }

            if (!effectSeen) return EvaMoveDecision.NoEffect;
            if (stowRequested && packDeployed)
            {
                if (stowFires <= 0) return EvaMoveDecision.FireStow;
                if (stowFires < MaxStowFires && pollsSinceStow >= StowRefirePolls)
                    return EvaMoveDecision.FireStow;
                if (stowFires >= MaxStowFires && pollsSinceStow >= StowRefirePolls)
                    return EvaMoveDecision.StowRefused;
                return elapsed >= budget ? EvaMoveDecision.Timeout : EvaMoveDecision.KeepWaiting;
            }
            if (!sceneSettled)
                return elapsed >= budget ? EvaMoveDecision.Timeout : EvaMoveDecision.KeepWaiting;
            return EvaMoveDecision.CompleteOk;
        }

        /// <summary>The ERROR msg token for a terminal decision (null for the non-terminal ones
        /// and CompleteOk).</summary>
        internal static string ErrorTokenFor(EvaMoveDecision decision)
        {
            switch (decision)
            {
                case EvaMoveDecision.KerbalLost: return KerbalLostReason;
                case EvaMoveDecision.Ragdolled: return RagdolledReason;
                case EvaMoveDecision.NoEffect: return NoEffectReason;
                case EvaMoveDecision.StowRefused: return StowRefusedReason;
                case EvaMoveDecision.Timeout: return TimeoutReason;
                default: return null;
            }
        }

        /// <summary>The walk's proof of effect: the kerbal moved at least
        /// <see cref="MinWalkMeters"/> (NaN reads as not moved).</summary>
        internal static bool WalkMoved(double movedMeters) => movedMeters >= MinWalkMeters;

        /// <summary>The deferral budget: the duration plus a minute for the toggle, the stow
        /// presses and the settle.</summary>
        internal const double BudgetSeconds = MaxSeconds + 60.0;

        internal static string FormatStartLine(string kerbal, uint evaPid, Args a, double startUT,
            bool packDeployedBefore, uint refPid, double refDist)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "evamove start kerbal='" + (kerbal ?? string.Empty) + "'"
                + " evaPid=" + evaPid.ToString(ic)
                + " mode=" + ModeToken(a.Mode)
                + " dir=" + DirToken(a.Dir)
                + " seconds=" + a.Seconds.ToString("0.###", ic)
                + " stow=" + (a.Stow ? "true" : "false")
                + " startUT=" + startUT.ToString("F2", ic)
                + " packBefore=" + (packDeployedBefore ? "true" : "false")
                + " refPid=" + refPid.ToString(ic)
                + " refDist=" + FormatMeters(refDist);
        }

        internal static List<KeyValuePair<string, string>> BuildOkPayload(string kerbal, uint evaPid, Args a,
            double startUT, double endUT, bool effectSeen, bool packDeployed, uint refPid,
            double refDistStart, double refDistEnd, double refSpeed)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("kerbal", kerbal ?? string.Empty),
                new KeyValuePair<string, string>("evaPid", evaPid.ToString(ic)),
                new KeyValuePair<string, string>("mode", ModeToken(a.Mode)),
                new KeyValuePair<string, string>("dir", DirToken(a.Dir)),
                new KeyValuePair<string, string>("seconds", a.Seconds.ToString("0.###", ic)),
                new KeyValuePair<string, string>("startUT", startUT.ToString("F2", ic)),
                new KeyValuePair<string, string>("endUT", endUT.ToString("F2", ic)),
                new KeyValuePair<string, string>("effect", effectSeen ? "true" : "false"),
                new KeyValuePair<string, string>("deployed", packDeployed ? "true" : "false"),
                new KeyValuePair<string, string>("refPid", refPid.ToString(ic)),
                new KeyValuePair<string, string>("refDistStart", FormatMeters(refDistStart)),
                new KeyValuePair<string, string>("refDistEnd", FormatMeters(refDistEnd)),
                new KeyValuePair<string, string>("refSpeed", FormatMeters(refSpeed)),
            };
        }

        /// <summary>Invariant two-decimal metres; NaN or negative (no reference) reads -1.</summary>
        internal static string FormatMeters(double v)
            => double.IsNaN(v) || double.IsInfinity(v) || v < 0.0
                ? "-1"
                : v.ToString("F2", CultureInfo.InvariantCulture);
    }
}
