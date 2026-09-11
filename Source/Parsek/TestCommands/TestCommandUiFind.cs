using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>How a <c>op=find</c> text match was made. The response echoes it so a
    /// spec author can tell an exact hit from a loose one without re-reading the tree.</summary>
    internal enum UiFindMatchMode
    {
        None = 0,
        Exact = 1,
        Prefix = 2,
        Contains = 3,
    }

    /// <summary>
    /// One control the find op may answer with, flattened out of the captured GUI tree.
    /// Unity-free on purpose: the match rule is then a pure decision with cells on it, and
    /// the applier's only job is to walk <c>GuiTreeNode</c> children into this shape.
    /// </summary>
    internal struct UiFindCandidate
    {
        /// <summary>The node's drawn text. Never null in a candidate (the flattener skips
        /// text-less nodes: a rect with no label is not addressable by a spec).</summary>
        internal string Text;

        /// <summary>The node's wire kind (<c>GuiTreeAssembler.KindName</c>).</summary>
        internal string Kind;

        /// <summary>Screen-space rect, the dump's own <c>rect</c> frame: client pixels,
        /// y DOWN from the top-left.</summary>
        internal float X;
        internal float Y;
        internal float W;
        internal float H;
    }

    /// <summary>
    /// Pure decision / payload half of <c>UiAction op=find</c>: turn a control's TEXT into
    /// the rect and centre a <c>op=pointer</c> step can be aimed at.
    ///
    /// <para><b>WHY IT EXISTS.</b> <c>op=pointer</c> takes pixels, and a census spec has no
    /// way to know them: a window's layout depends on its rect, its complexity mode, the
    /// save's row counts and the skin. Hard-coding a coordinate would photograph a hover
    /// over whatever moved into that spot. So a lane asks for the control by the text a
    /// reviewer can see, and chains the answer - <c>${stepN.cx}</c> / <c>${stepN.cy}</c>,
    /// the R10 runtime-handle mechanism - straight into the pointer step. The payload keys
    /// are therefore all bare <c>[A-Za-z0-9_]</c> identifiers, because
    /// <c>hlib.HANDLE_REF_RE</c> cannot capture a field whose name carries a dash or a
    /// dot.</para>
    ///
    /// <para><b>THE MATCH IS A LADDER, not a single rule.</b> Exact, then prefix, then
    /// contains - and the FIRST rung with any hit wins outright, rather than all three
    /// being pooled. A window that has both a "Close" button and a "Close all" button must
    /// answer the first for <c>text=Close</c>; pooling would make the answer depend on
    /// draw order, and a spec pinned against it would silently re-aim the moment a row was
    /// added. Case-sensitive, like every other token in this grammar.</para>
    ///
    /// <para><c>index=</c> disambiguates WITHIN the winning rung, in draw order, and is
    /// how a lane addresses the third row's "G" button. Out of range is an error naming
    /// the count rather than a silent clamp to the last one.</para>
    /// </summary>
    internal static class TestCommandUiFind
    {
        // ----- arg keys -----

        internal const string TextArg = "text";

        /// <summary>The control-kind filter. Spelled <c>ctrl</c> and NOT <c>kind</c>:
        /// <c>hlib.VERB_SCOPED_CLOSED_ARGS</c> allows exactly one owner verb per arg key,
        /// and <c>kind=</c> is already <c>ListHandles</c>'. A <c>kind=</c> here would be
        /// rejected pre-launch as "only the ListHandles verb reads it".</summary>
        internal const string CtrlArg = "ctrl";

        internal const string IndexArg = "index";

        /// <summary>Every accepted <c>ctrl=</c> value, in <c>GuiNodeKind</c> order. Mirrors
        /// <c>GuiTreeAssembler.KindName</c>, which is itself the pinned JSON contract, so
        /// the filter speaks the same words the dump a reviewer is reading does.</summary>
        internal static readonly string[] CtrlValues = new[]
        {
            "window", "group", "scrollview", "layoutgroup", "label", "box", "button",
            "repeatbutton", "toggle", "textfield", "buttongrid", "slider", "control",
        };

        /// <summary>Comma-joined <see cref="CtrlValues"/> for the reject message.</summary>
        internal static string ValidCtrlNames => string.Join(",", CtrlValues);

        // ----- reject / error reasons -----

        /// <summary>No <c>text=</c>. Required: a find with no text would answer the first
        /// node in the window, which is never what a lane meant.</summary>
        internal const string TextArgMissingReason = "find-text-arg-missing";

        /// <summary>A <c>ctrl=</c> outside <see cref="CtrlValues"/>.</summary>
        internal const string CtrlArgInvalidReason = "find-ctrl-arg-invalid";

        /// <summary>An <c>index=</c> that is not a non-negative invariant-culture
        /// integer.</summary>
        internal const string IndexArgInvalidReason = "find-index-arg-invalid";

        /// <summary>POST-CAPTURE: the capture succeeded and the named window is NOT in it.
        /// Distinct from <see cref="ControlNotFoundReason"/> on purpose - "the window was
        /// not drawing" and "the window was drawing and has no such control" send an author
        /// to different fixes (open the window first, versus fix the text).</summary>
        internal const string WindowNotDrawnReason = "find-window-not-drawn";

        /// <summary>POST-CAPTURE: the window's subtree carries no node matching the text on
        /// any rung of the ladder. The message names the window and how many text-carrying
        /// candidates were searched, so "0 candidates" (a window drawn but empty) reads
        /// differently from "231 candidates" (a typo).</summary>
        internal const string ControlNotFoundReason = "control-not-found";

        /// <summary>POST-CAPTURE: the text matched, and <c>index=</c> is past the end of
        /// the winning rung. Named separately so a lane sees "there are 2 of these, you
        /// asked for the 5th" rather than "no such control".</summary>
        internal const string IndexOutOfRangeReason = "find-index-out-of-range";

        /// <summary>The in-memory tree capture did not complete (the recorder refused, gave
        /// up with no Repaint, or faulted). ERROR carrying the recorder's own reason.</summary>
        internal const string CaptureFailedReason = "find-capture-failed";

        // ----- parse -----

        /// <summary>Parses the required <c>text=</c>. Empty is refused along with
        /// absent: an empty string prefix-matches every node.</summary>
        internal static bool TryParseText(string raw, out string text, out string rejectReason)
        {
            text = null;
            if (string.IsNullOrEmpty(raw))
            {
                rejectReason = TextArgMissingReason;
                return false;
            }
            text = raw;
            rejectReason = null;
            return true;
        }

        /// <summary>Parses the optional <c>ctrl=</c>. Absent yields null, which means "any
        /// kind".</summary>
        internal static bool TryParseCtrl(string raw, out string ctrl, out string rejectReason)
        {
            ctrl = null;
            rejectReason = null;
            if (raw == null) return true;
            for (int i = 0; i < CtrlValues.Length; i++)
            {
                if (CtrlValues[i] == raw)
                {
                    ctrl = raw;
                    return true;
                }
            }
            rejectReason = CtrlArgInvalidReason;
            return false;
        }

        /// <summary>Parses the optional <c>index=</c>. Absent yields 0 (the first match).</summary>
        internal static bool TryParseIndex(string raw, out int index, out string rejectReason)
        {
            index = 0;
            rejectReason = null;
            if (raw == null) return true;
            int parsed;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture,
                              out parsed) || parsed < 0)
            {
                rejectReason = IndexArgInvalidReason;
                return false;
            }
            index = parsed;
            return true;
        }

        // ----- the match ladder -----

        /// <summary>The rung a single candidate's text sits on for this query, or
        /// <see cref="UiFindMatchMode.None"/>.</summary>
        internal static UiFindMatchMode ClassifyMatch(string candidateText, string wanted)
        {
            if (candidateText == null || wanted == null) return UiFindMatchMode.None;
            if (string.Equals(candidateText, wanted, StringComparison.Ordinal))
                return UiFindMatchMode.Exact;
            if (candidateText.StartsWith(wanted, StringComparison.Ordinal))
                return UiFindMatchMode.Prefix;
            if (candidateText.IndexOf(wanted, StringComparison.Ordinal) >= 0)
                return UiFindMatchMode.Contains;
            return UiFindMatchMode.None;
        }

        /// <summary>Wire token for a rung.</summary>
        internal static string MatchToken(UiFindMatchMode mode)
        {
            switch (mode)
            {
                case UiFindMatchMode.Exact: return "exact";
                case UiFindMatchMode.Prefix: return "prefix";
                case UiFindMatchMode.Contains: return "contains";
                default: return "none";
            }
        }

        /// <summary>
        /// Picks one candidate. Walks the ladder exact -> prefix -> contains and takes the
        /// FIRST rung with any hit, then <paramref name="index"/> within it in draw order.
        /// </summary>
        /// <param name="candidates">The window subtree, flattened in draw order.</param>
        /// <param name="wanted">The <c>text=</c> query.</param>
        /// <param name="ctrl">The <c>ctrl=</c> filter, or null for any kind.</param>
        /// <param name="index">Which hit on the winning rung.</param>
        /// <param name="match">The chosen candidate.</param>
        /// <param name="mode">Which rung won.</param>
        /// <param name="rungCount">How many hits the winning rung had (0 when none did).</param>
        /// <param name="searched">Candidates considered after the kind filter, for the
        /// reject message.</param>
        internal static bool TryPick(IList<UiFindCandidate> candidates, string wanted,
                                     string ctrl, int index,
                                     out UiFindCandidate match, out UiFindMatchMode mode,
                                     out int rungCount, out int searched,
                                     out string rejectReason)
        {
            match = default(UiFindCandidate);
            mode = UiFindMatchMode.None;
            rungCount = 0;
            searched = 0;

            var exact = new List<UiFindCandidate>();
            var prefix = new List<UiFindCandidate>();
            var contains = new List<UiFindCandidate>();
            int count = candidates != null ? candidates.Count : 0;
            for (int i = 0; i < count; i++)
            {
                UiFindCandidate c = candidates[i];
                if (ctrl != null && !string.Equals(c.Kind, ctrl, StringComparison.Ordinal))
                    continue;
                searched++;
                switch (ClassifyMatch(c.Text, wanted))
                {
                    case UiFindMatchMode.Exact: exact.Add(c); break;
                    case UiFindMatchMode.Prefix: prefix.Add(c); break;
                    case UiFindMatchMode.Contains: contains.Add(c); break;
                }
            }

            List<UiFindCandidate> rung;
            if (exact.Count > 0) { rung = exact; mode = UiFindMatchMode.Exact; }
            else if (prefix.Count > 0) { rung = prefix; mode = UiFindMatchMode.Prefix; }
            else if (contains.Count > 0) { rung = contains; mode = UiFindMatchMode.Contains; }
            else
            {
                mode = UiFindMatchMode.None;
                rejectReason = ControlNotFoundReason;
                return false;
            }

            rungCount = rung.Count;
            if (index >= rung.Count)
            {
                rejectReason = IndexOutOfRangeReason;
                return false;
            }
            match = rung[index];
            rejectReason = null;
            return true;
        }

        /// <summary>Centre of a rect, in the same client frame, rounded to the whole pixel
        /// a cursor actually lands on.</summary>
        internal static void CentreOf(UiFindCandidate c, out float cx, out float cy)
        {
            cx = c.X + c.W * 0.5f;
            cy = c.Y + c.H * 0.5f;
        }

        // ----- payload -----

        /// <summary>
        /// OK payload:
        /// <c>op=find window= text= ctrl= match= matches= x= y= w= h= cx= cy=</c>.
        ///
        /// <para><c>text</c> is the node's OWN text, not the query, so a prefix or contains
        /// hit tells the reader what it actually landed on. <c>cx</c> / <c>cy</c> are the
        /// centre, which is the field a pointer step chains; they are separate keys rather
        /// than one <c>centre=x,y</c> because <c>${step.field}</c> substitutes a whole
        /// value and could not split a pair.</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildPayload(
            string window, UiFindCandidate match, UiFindMatchMode mode, int matches)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            float cx, cy;
            CentreOf(match, out cx, out cy);
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("op", TestCommandUiAction.FindOpToken),
                new KeyValuePair<string, string>("window", window ?? string.Empty),
                new KeyValuePair<string, string>("text", match.Text ?? string.Empty),
                new KeyValuePair<string, string>("ctrl", match.Kind ?? string.Empty),
                new KeyValuePair<string, string>("match", MatchToken(mode)),
                new KeyValuePair<string, string>("matches", matches.ToString(ic)),
                new KeyValuePair<string, string>("x", match.X.ToString("F0", ic)),
                new KeyValuePair<string, string>("y", match.Y.ToString("F0", ic)),
                new KeyValuePair<string, string>("w", match.W.ToString("F0", ic)),
                new KeyValuePair<string, string>("h", match.H.ToString("F0", ic)),
                new KeyValuePair<string, string>("cx", cx.ToString("F0", ic)),
                new KeyValuePair<string, string>("cy", cy.ToString("F0", ic)),
            };
        }
    }
}
