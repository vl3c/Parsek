using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>The three FarWatch ops.</summary>
    internal enum FarWatchOp
    {
        Start,
        Stop,
        Status,
    }

    /// <summary>What a pending FarWatch step does on this poll.</summary>
    internal enum FarWatchCompletionDecision
    {
        KeepWaiting,
        CompleteOk,
        CompleteEnded,
    }

    /// <summary>
    /// The <c>FarWatch</c> seam verb (docs/dev/design-far-watch.md 17.4, H7), pure half.
    /// Grammar:
    /// <c>FarWatch op=start (index=&lt;n&gt; | tree=&lt;id|handle&gt;) [mode=follow|hold] [via=seam|map]</c>,
    /// <c>FarWatch op=stop</c>, <c>FarWatch op=status</c>. FLIGHT, TWO-PHASE for start and
    /// stop (120 s): start answers OK once the watch reaches Following, stop once it is back
    /// to Idle; status answers at once (with the return point and the warp limit). Follow moves
    /// the drone with the ghost at any body (step 4), starting the far watch directly on a
    /// ghost past the near-watch range or at another body; hold keeps it beside the ship and
    /// needs no ghost camera. <c>via=map</c> (follow only) presses the map ghost menu's Watch
    /// entry instead of calling the controller (<c>GhostMapWatchHelper.HandleWatchRequest</c>,
    /// the player's map path, design 12.2), and refuses when that entry is not a plain enabled
    /// Watch or does not start a far watch. Every start passes the ship-safety check (design 8.2): an unsafe ship refuses
    /// with <c>unsafe:&lt;reason&gt;</c>.
    ///
    /// While a far watch runs, the dispatcher refuses every verb that could change a vessel,
    /// the scene or a save other than a named residual save (WarpToUT, the player's own rails
    /// warp, passes), with <c>far-watch-active</c>
    /// (<see cref="IsAllowedDuringFarWatch"/>).
    /// </summary>
    internal static class TestCommandFarWatch
    {
        internal const string Verb = "FarWatch";
        internal const string OpKey = "op";
        internal const string IndexKey = "index";
        internal const string TreeKey = "tree";
        internal const string ModeKey = "mode";
        internal const string ViaKey = "via";

        internal const double BudgetSeconds = 120.0;

        internal const string FarWatchActiveReason = "far-watch-active";
        internal const string OpMissingReason = "op-missing";
        internal const string OpInvalidReason = "op-invalid";
        internal const string TargetMissingReason = "target-missing";
        internal const string TargetAmbiguousReason = "target-ambiguous";
        internal const string IndexInvalidReason = "index-invalid";
        internal const string ModeInvalidReason = "mode-invalid";
        internal const string ViaInvalidReason = "via-invalid";
        internal const string ViaMapNeedsFollowReason = "via-map-needs-follow";
        internal const string MapVesselMissingReason = "map-vessel-missing";
        internal const string MapEntryRefusedReasonPrefix = "map-entry-refused:";
        internal const string MapEntryNotFarReason = "map-entry-not-far";
        internal const string NotRunningReason = "far-watch-not-running";
        internal const string AlreadyRunningReason = "far-watch-already-running";
        internal const string NoGhostReason = "no-far-watch-ghost";
        internal const string UnknownTreeReason = "unknown-tree";
        internal const string NoControllerReason = "no-far-watch-controller";
        internal const string EndedReasonPrefix = "far-watch-ended:";
        internal const string TimeoutReason = "far-watch-timeout";

        internal static readonly string[] ArgKeys = { "op", "index", "tree", "mode", "via" };
        internal static readonly string[] OpValues = { "start", "stop", "status" };
        internal static readonly string[] ModeValues = { "follow", "hold" };
        internal static readonly string[] ViaValues = { "seam", "map" };

        /// <summary>Save names a far watch never lets the seam write (9.2: quicksave and autosave off).</summary>
        internal static readonly string[] ForbiddenSaveNames = { "persistent", "quicksave" };

        internal struct Args
        {
            internal FarWatchOp Op;
            internal int Index;
            internal string Tree;
            internal bool Follow;
            /// <summary>Started through the map ghost menu's Watch entry (<c>via=map</c>).</summary>
            internal bool ViaMap;
        }

        /// <summary>Parses the args with no <c>via</c>.</summary>
        internal static string TryParseArgs(string op, string index, string tree, string mode, out Args args)
        {
            return TryParseArgs(op, index, tree, mode, null, out args);
        }

        /// <summary>Parses the args; returns null and fills <paramref name="args"/>, or a refusal reason.</summary>
        internal static string TryParseArgs(string op, string index, string tree, string mode, string via, out Args args)
        {
            args = new Args { Index = -1, Follow = true };
            if (string.IsNullOrEmpty(op))
                return OpMissingReason;
            switch (op)
            {
                case "start": args.Op = FarWatchOp.Start; break;
                case "stop": args.Op = FarWatchOp.Stop; break;
                case "status": args.Op = FarWatchOp.Status; break;
                default: return OpInvalidReason;
            }
            if (!string.IsNullOrEmpty(mode))
            {
                if (mode == "follow") args.Follow = true;
                else if (mode == "hold") args.Follow = false;
                else return ModeInvalidReason;
            }
            if (!string.IsNullOrEmpty(via))
            {
                if (via == "map") args.ViaMap = true;
                else if (via != "seam") return ViaInvalidReason;
            }
            if (args.ViaMap && !args.Follow)
                return ViaMapNeedsFollowReason;
            if (args.Op != FarWatchOp.Start)
                return null;
            bool hasIndex = !string.IsNullOrEmpty(index);
            bool hasTree = !string.IsNullOrEmpty(tree);
            if (hasIndex && hasTree)
                return TargetAmbiguousReason;
            if (!hasIndex && !hasTree)
                return TargetMissingReason;
            if (hasIndex)
            {
                int n;
                if (!int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 0)
                    return IndexInvalidReason;
                args.Index = n;
            }
            else
            {
                args.Tree = tree;
            }
            return null;
        }

        internal struct GhostCandidate
        {
            internal int Index;
            internal bool InScope;
            internal bool HasActiveGhost;
        }

        /// <summary>
        /// The lowest in-scope index with a live ghost, or -1. Any body in either mode (step 4:
        /// a follow watch moves the drone to the ghost's body).
        /// </summary>
        internal static int ResolveTargetIndex(IList<GhostCandidate> candidates, bool follow)
        {
            if (candidates == null)
                return -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                GhostCandidate c = candidates[i];
                if (!c.InScope || !c.HasActiveGhost)
                    continue;
                return c.Index;
            }
            return -1;
        }

        /// <summary>
        /// The map entry's refusal for a <c>via=map</c> start, or null when the press may go
        /// ahead: the entry must read the plain enabled "Watch" (step 4: never "Watch (Too
        /// Far)" / "Watch (Other SOI)") and the press must start a far watch.
        /// </summary>
        internal static string MapEntryRefusal(string label, bool enabled, string reason)
        {
            if (enabled && label == "Watch")
                return null;
            return MapEntryRefusedReasonPrefix + (string.IsNullOrEmpty(reason) ? (label ?? "(none)") : reason);
        }

        /// <summary>
        /// Pure H7 rule: while a far watch runs, only the FarWatch verb itself, the
        /// non-mutating verbs, a SaveGame to a residual name and WarpToUT pass. WarpToUT is a
        /// real rails warp through <c>TimeWarp.SetRate</c>, the player's own warp, so stock's
        /// limit (read at the watched ghost, 8.4) and the return-point ceiling bound it.
        /// </summary>
        internal static bool IsAllowedDuringFarWatch(string verb, string saveName)
        {
            if (string.IsNullOrEmpty(verb))
                return false;
            if (verb == Verb)
                return true;
            if (!TestCommandVerbs.IsStateMutatingVerb(verb))
                return true;
            if (verb == "SaveGame")
                return IsResidualSaveName(saveName);
            if (verb == "WarpToUT")
                return true;
            return false;
        }

        /// <summary>A save the far watch allows: named, and neither persistent nor quicksave.</summary>
        internal static bool IsResidualSaveName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            for (int i = 0; i < ForbiddenSaveNames.Length; i++)
            {
                if (string.Equals(name, ForbiddenSaveNames[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// A pending start completes OK at Following, ends at Idle (the watch aborted); a
        /// pending stop completes OK at Idle. Anything else keeps waiting (the generic
        /// budget converts it to TIMEOUT).
        /// </summary>
        internal static FarWatchCompletionDecision DecideCompletion(FarWatchOp op, FarWatchState state)
        {
            if (op == FarWatchOp.Start)
            {
                if (state == FarWatchState.Following) return FarWatchCompletionDecision.CompleteOk;
                if (state == FarWatchState.Idle) return FarWatchCompletionDecision.CompleteEnded;
                return FarWatchCompletionDecision.KeepWaiting;
            }
            if (op == FarWatchOp.Stop)
                return state == FarWatchState.Idle
                    ? FarWatchCompletionDecision.CompleteOk
                    : FarWatchCompletionDecision.KeepWaiting;
            return FarWatchCompletionDecision.CompleteOk;
        }

        /// <summary>The OK payload: invariant key=value pairs the harness reads.</summary>
        /// <remarks>
        /// <c>returnUT</c> is the far watch's return point (<c>none</c> without one, design 8.5),
        /// <c>returnCause</c> what it is for, and <c>warpLimit</c> what holds warp below the
        /// requested rate (<c>none</c>, <c>stock</c> at the ghost, <c>floor</c> or <c>return</c>,
        /// the Parsek ceiling of 8.4).
        /// </remarks>
        internal static List<KeyValuePair<string, string>> BuildPayload(FarWatchState state, uint dronePid,
            uint shipPid, int ghostIndex, bool follow, FarWatchEndReason lastEnd,
            double returnUT, FarWatchReturnCause returnCause, string warpLimit)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("state", state.ToString()),
                new KeyValuePair<string, string>("dronePid", dronePid.ToString(ic)),
                new KeyValuePair<string, string>("shipPid", shipPid.ToString(ic)),
                new KeyValuePair<string, string>("ghostIndex", ghostIndex.ToString(ic)),
                new KeyValuePair<string, string>("mode", follow ? "follow" : "hold"),
                new KeyValuePair<string, string>("lastEnd", lastEnd.ToString()),
                new KeyValuePair<string, string>("returnUT",
                    double.IsNaN(returnUT) || double.IsInfinity(returnUT) ? "none" : returnUT.ToString("F1", ic)),
                new KeyValuePair<string, string>("returnCause", returnCause.ToString()),
                new KeyValuePair<string, string>("warpLimit", string.IsNullOrEmpty(warpLimit) ? "none" : warpLimit),
            };
        }

        /// <summary>The same pairs as one log fragment.</summary>
        internal static string FormatPayload(List<KeyValuePair<string, string>> payload)
        {
            if (payload == null)
                return string.Empty;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < payload.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(payload[i].Key).Append('=').Append(payload[i].Value);
            }
            return sb.ToString();
        }

        /// <summary>
        /// RecordingState's far-watch fields: <c>dronePid</c> and <c>playerShipPid</c>, appended
        /// only while a drone exists, so a read without one is byte-identical to before.
        /// </summary>
        internal static void AppendDroneFields(List<KeyValuePair<string, string>> payload, bool droneExists,
            uint dronePid, uint shipPid)
        {
            if (payload == null || !droneExists)
                return;
            payload.Add(new KeyValuePair<string, string>("dronePid", dronePid.ToString(CultureInfo.InvariantCulture)));
            payload.Add(new KeyValuePair<string, string>("playerShipPid", shipPid.ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>The ERROR reason a start that ended before Following answers with.</summary>
        internal static string EndedReason(FarWatchEndReason reason)
        {
            return EndedReasonPrefix + reason;
        }
    }
}
