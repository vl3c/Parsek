using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure decision / payload half of <c>UiAction op=mock</c>: the GUI state gallery's
    /// primitive. It hands ONE window a synthetic VIEW MODEL while the real IMGUI draw
    /// code computes every rect and every string, so a census can photograph states no
    /// save reaches.
    ///
    /// <para><b>A PAIRED OP, in the <c>raise</c> / <c>dismiss</c> shape.</b> The seam has
    /// no saved-previous-value machinery and no automatic restore anywhere; the house
    /// pattern is an explicit pair plus an unconditional clear on every exit path
    /// (<c>ReleaseRaisedDialogInputLock</c>). So apply is
    /// <c>mockState=&lt;id&gt;</c>, clear is <c>mockState=none</c>, and every exit -
    /// scene change, level loaded, FlushAndQuit, an exception in apply, settle or capture
    /// - clears.</para>
    ///
    /// <para><b>WHY THE ARG IS <c>mockState=</c> AND NOT <c>state=</c>.</b> The design
    /// sketched <c>state=&lt;id&gt;</c>, and the seam cannot spell it that way:
    /// <c>state=</c> is ONE arg key with ONE closed vocabulary across
    /// <c>expand</c> / <c>playback</c> / <c>op=state</c> (<c>true</c> / <c>false</c>,
    /// mirrored by <c>hlib.UIACTION_STATE_VALUES</c> and enforced pre-launch by
    /// <c>VERB_SCOPED_CLOSED_ARGS</c>), so a catalogue id in it is a validation error
    /// before any boot. This is the same rule that made <c>op=find</c>'s control filter
    /// <c>ctrl=</c> rather than <c>kind=</c>. An open-valued selector gets its own key,
    /// exactly as <c>text=</c>, <c>category=</c> and <c>recording=</c> do.</para>
    ///
    /// <para><b>AND WHY <c>op=mock</c> IS NOT IN <c>OpNeedsWindow</c>.</b> The describe
    /// form (<c>op=mock describe=true</c>) reports the catalogue and names no window, so
    /// an unconditional window requirement would refuse it. The window is required for
    /// apply and clear, checked by the applier and mirrored by the harness's own per-op
    /// branch.</para>
    /// </summary>
    internal static class TestCommandUiMock
    {
        // ----- arg keys -----

        /// <summary>The catalogue state id, or <see cref="ClearToken"/>. OPEN valued (an
        /// id is <c>&lt;window&gt;.&lt;family&gt;.&lt;variant&gt;</c>, not a closed set a
        /// spec table can carry), which is why it is not a
        /// <c>VERB_SCOPED_CLOSED_ARGS</c> row.</summary>
        internal const string MockStateArg = "mockState";

        /// <summary>The read-only catalogue report. Closed boolean, the
        /// <c>await=</c> / <c>commit=</c> shape.</summary>
        internal const string DescribeArg = "describe";

        /// <summary>The paired-clear sentinel. A token rather than an empty value, the
        /// describe payload's <c>-</c> rule: a trailing <c>mockState=</c> on the wire
        /// reads as a truncated line.</summary>
        internal const string ClearToken = "none";

        // ----- refusal reasons -----
        //
        // Classified PRE-CALL / POST-CALL / POST-SETTLE the way every other family in
        // this seam declares its reasons, because the class decides whether a refused
        // step left state written.

        /// <summary>PRE-CALL: neither <c>mockState=</c> nor <c>describe=true</c>. There
        /// is no default: applying a guessed state would photograph something the lane
        /// never asked for, and reporting the catalogue by accident would leave a
        /// capture step downstream with nothing mocked.</summary>
        internal const string ArgMissingReason = "mock-arg-missing";

        /// <summary>PRE-CALL: <c>mockState=</c> names no catalogue id. The message
        /// carries the window and how many ids that window has, so a typo is one line to
        /// diagnose.</summary>
        internal const string StateUnknownReason = "mock-state-unknown";

        /// <summary>PRE-CALL: this build has no injection seam for the named window. The
        /// message names the supported set - a phase adds windows, and the refusal has to
        /// say what THIS build carries rather than what the design plans.</summary>
        internal const string WindowUnsupportedReason = "mock-window-unsupported";

        /// <summary>PRE-CALL: the id resolves, and to a state of a DIFFERENT window than
        /// the <c>window=</c> arg. Distinct from <see cref="StateUnknownReason"/> on
        /// purpose: "that id is not a thing" and "that id belongs to another window" send
        /// an author to different fixes.</summary>
        internal const string StateWindowMismatchReason = "mock-state-window-mismatch";

        /// <summary>PRE-CALL: the state declares a scene the game is not in.</summary>
        internal const string RefusedSceneReason = "mock-refused-scene";

        /// <summary>PRE-CALL: a Gloops or flight recording is running. Mirrors
        /// <c>complexity-refused-gloops-recording</c>: a recording is the one live
        /// condition under which nothing automation-only should be swapping a window's
        /// data out from under the recorder.</summary>
        internal const string RefusedRecordingReason = "mock-refused-recording";

        /// <summary>PRE-CALL: a scope is already live. One at a time, by design - two
        /// windows' cache suppressions interacting is a state nobody can reason
        /// about.</summary>
        internal const string RefusedSessionLiveReason = "mock-refused-session-live";

        /// <summary>
        /// PRE-CALL: the CURRENT complexity mode hides the window's launcher, so no player
        /// can have it on screen.
        ///
        /// <para>Basic hides the Kerbals and Career State launchers - the surface-level
        /// visibility predicate decides it - AND the mode switch force-closes both
        /// (<c>ParsekUI.BuildGatedWindowCloseSet</c>), so a Basic apply would photograph a
        /// window the product cannot show, which the coverage audit already classifies as
        /// UNREACHABLE rather than uncaptured. Refused rather than drawn, because the one
        /// thing this feature may not do is put an impossible picture on the mirror. The
        /// applier reaches that predicate through
        /// <c>GuiMockCatalogue.IsMockableInMode</c>, which is where the mode vocabulary
        /// lives; naming it here in prose keeps this file out of the mode gate's
        /// allowlist.</para>
        ///
        /// <para>The design's section 7.1 gave each state an optional <c>Mode</c> field
        /// for this. A per-state pin would have been the weaker answer: it puts the
        /// decision on 46 declarations instead of on the one production predicate that
        /// already owns it, and a new state could still get it wrong. Recorded in
        /// design-gui-state-gallery.md section 18.</para>
        /// </summary>
        internal const string RefusedModeReason = "mock-refused-mode";

        /// <summary>
        /// POST-SETTLE: the frame that settled the apply did not draw the mocked model.
        ///
        /// <para><b>The load-bearing one.</b> Its read-back is DRAW-PRODUCED and must
        /// stay that way: the applier arms
        /// <c>GuiTreeRecorder.ArmForNextRepaint(label, writeToDisk: false)</c> - the
        /// <c>op=find</c> mechanism - and asserts the payload's own derived witness
        /// strings appear in the tree the frame produced. Reading back the field the
        /// applier just wrote is the vacuous read-back <c>op=rect</c> was first written
        /// with and had to be fixed for: it compares a value with itself and cannot fire
        /// on any input.</para>
        ///
        /// <para>It also covers the capture side: an arm that was refused, faulted or
        /// gave up answers this, with the recorder's own outcome on the log line. One
        /// wire reason, several causes, separated in the log - the
        /// <c>find-capture-failed</c> shape.</para>
        /// </summary>
        internal const string NotAppliedReason = "mock-not-applied";

        /// <summary>POST-CALL: the restore closure threw. Logged as an Error, the window
        /// is force-closed and the session is dropped, so the next apply starts from a
        /// clean state rather than from a half-restored one.</summary>
        internal const string RestoreFailedReason = "mock-restore-failed";

        /// <summary>
        /// POST-SETTLE: the scope is live and the window has been OBSERVED to have lost
        /// its mocked model - something outside the declared suppression set wrote the
        /// injected member.
        ///
        /// <para>Distinct from <see cref="NotAppliedReason"/> on purpose: not-applied
        /// means the frame never drew the model, while this means the model was there and
        /// went AWAY, which sends an author to a MISSING SUPPRESSION SITE rather than to
        /// the state or the lane. The case is real rather than defensive: Career State's
        /// cached VM has TWO writers, and the first build suppressed only the rebuild
        /// predicate - so any ledger write nulled a mocked VM mid-scope.</para>
        ///
        /// <para>A clear also reports it, so a lane that never polls an apply still learns
        /// the capture it took was not the state it asked for.</para>
        /// </summary>
        internal const string ScopeBrokenReason = "mock-scope-broken";

        /// <summary>Every refusal token, comma-joined. Echoed in the
        /// <see cref="ArgMissingReason"/> message the way <c>op-arg-invalid</c> echoes
        /// its valid set, and mirrored by <c>hlib.UIACTION_MOCK_REFUSALS</c>.</summary>
        internal static string ValidRefusalNames => string.Join(",", new[]
        {
            ArgMissingReason, StateUnknownReason, WindowUnsupportedReason,
            StateWindowMismatchReason, RefusedSceneReason, RefusedRecordingReason,
            RefusedSessionLiveReason, RefusedModeReason, NotAppliedReason,
            RestoreFailedReason, ScopeBrokenReason,
        });

        // ----- arg parses -----

        /// <summary>What one parsed <c>op=mock</c> call asks for.</summary>
        internal enum MockIntent
        {
            /// <summary>Neither arg: <see cref="ArgMissingReason"/>.</summary>
            None,

            /// <summary>Install the named state.</summary>
            Apply,

            /// <summary><c>mockState=none</c>: the paired clear.</summary>
            Clear,

            /// <summary><c>describe=true</c>: the read-only catalogue report.</summary>
            Describe,
        }

        /// <summary>
        /// Resolves the intent from the two args.
        ///
        /// <para><c>describe=true</c> wins over an absent <c>mockState=</c> only; naming
        /// BOTH is a refusal rather than a silent preference, because the two do opposite
        /// things and guessing which one a spec meant is how a lane photographs the wrong
        /// thing. An invalid <c>describe=</c> value is caught by the seam's closed-arg
        /// table on the harness side and by <see cref="TryParseDescribe"/> here.</para>
        /// </summary>
        internal static bool TryResolveIntent(string rawState, string rawDescribe,
                                              out MockIntent intent, out string rejectReason)
        {
            intent = MockIntent.None;

            bool describe;
            if (!TryParseDescribe(rawDescribe, out describe, out rejectReason))
                return false;

            bool hasState = !string.IsNullOrEmpty(rawState);
            if (describe && hasState)
            {
                rejectReason = ArgMissingReason;
                return false;
            }
            if (describe)
            {
                intent = MockIntent.Describe;
                rejectReason = null;
                return true;
            }
            if (!hasState)
            {
                rejectReason = ArgMissingReason;
                return false;
            }

            intent = string.Equals(rawState, ClearToken, StringComparison.Ordinal)
                ? MockIntent.Clear
                : MockIntent.Apply;
            rejectReason = null;
            return true;
        }

        /// <summary>Parses the optional <c>describe=</c> boolean. Absent is false;
        /// anything but the two tokens reuses the seam's own
        /// <c>state-arg-invalid</c> vocabulary, since this is the same closed
        /// true/false shape every boolean arg in the family has.</summary>
        internal static bool TryParseDescribe(string raw, out bool describe,
                                              out string rejectReason)
        {
            describe = false;
            rejectReason = null;
            if (raw == null) return true;
            if (raw == TestCommandUiState.StateTrueToken) { describe = true; return true; }
            if (raw == TestCommandUiState.StateFalseToken) return true;
            rejectReason = TestCommandUiState.StateArgInvalidReason;
            return false;
        }

        // ----- labels -----

        /// <summary>
        /// The capture label for a state, in the mirror's own grammar
        /// <c>&lt;host&gt;-&lt;window&gt;[-&lt;tab&gt;]-&lt;state&gt;-&lt;mode&gt;</c>
        /// with <c>mock</c> as the host: the state id minus its window prefix, dots
        /// turned into dashes.
        ///
        /// <para><c>logistics.hold.escrow-short</c> -&gt;
        /// <c>mock-logistics-hold-escrow-short-advanced</c>.</para>
        ///
        /// <para>It lives on the PURE side, is echoed in the op's OK payload and is what
        /// the P2 batch verb will derive its per-state label from, so there is one
        /// derivation rather than one per caller. Three hard constraints it must satisfy,
        /// each pinned by a unit cell rather than discovered in a run: at most 96
        /// characters, matching
        /// <c>^[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9-])?$</c>
        /// (<c>TestCommandCaptureScreenshot.IsValidLabel</c>, which is stricter than the
        /// recorder's own sanitiser so an accepted label survives it unchanged), and
        /// UNIQUE within a run - nothing in the harness validates within-run label
        /// uniqueness, and a 300-state gallery would silently overwrite a capture.</para>
        /// </summary>
        internal static string DeriveLabel(string prefix, string stateId, bool basicMode)
        {
            string host = string.IsNullOrEmpty(prefix) ? DefaultLabelPrefix : prefix;
            var sb = new StringBuilder(host.Length + (stateId != null ? stateId.Length : 0) + 12);
            sb.Append(host);
            sb.Append('-');
            sb.Append((stateId ?? string.Empty).Replace('.', '-'));
            sb.Append('-');
            sb.Append(TestCommandUiAction.ModeToken(basicMode));
            return sb.ToString();
        }

        /// <summary>The label host a gallery run uses when the lane names none. It is
        /// what makes a mocked capture file under the mirror's <c>mock</c> dataset rather
        /// than under a real fixture's name.</summary>
        internal const string DefaultLabelPrefix = "mock";

        // ----- payload builders -----

        /// <summary>
        /// OK payload for an APPLY:
        /// <c>op=mock window= mockState= applied=true covers=&lt;n&gt; witness=&lt;n&gt;
        /// label=&lt;derived&gt; mode= frame=&lt;n&gt;</c>.
        ///
        /// <para><c>label=</c> is on the wire because the P2 batch verb and any
        /// step-driven lane both need the SAME derivation, and a spec that typed its own
        /// label would drift from the mirror's parse. <c>witness=</c> is the number of
        /// draw-produced strings the settle asserted: a reader of a passing run can see
        /// that the read-back had something to check.</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildApplyPayload(
            string window, string stateId, int covers, int witnesses, string label,
            bool basicMode, int frame)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                Kv("op", TestCommandUiAction.MockOpToken),
                Kv("window", window ?? string.Empty),
                Kv(MockStateArg, stateId ?? string.Empty),
                Kv("applied", "true"),
                Kv("covers", covers.ToString(ic)),
                Kv("witness", witnesses.ToString(ic)),
                Kv("label", label ?? string.Empty),
                Kv("mode", TestCommandUiAction.ModeToken(basicMode)),
                Kv("frame", frame.ToString(ic)),
            };
        }

        /// <summary>
        /// OK payload for a CLEAR: <c>op=mock mockState=none cleared=&lt;bool&gt;
        /// window=&lt;name|-&gt; state=&lt;id|-&gt;</c>.
        ///
        /// <para>Clearing with nothing live is an OK with <c>cleared=false</c>, not a
        /// refusal - the <c>already=</c> rule. A lane's teardown step must be safe to run
        /// after a state that was never applied, or one refused apply turns every later
        /// step into a cascade of errors.</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildClearPayload(
            bool cleared, string window, string stateId)
            => new List<KeyValuePair<string, string>>
            {
                Kv("op", TestCommandUiAction.MockOpToken),
                Kv(MockStateArg, ClearToken),
                Kv("cleared", cleared ? "true" : "false"),
                Kv("window", string.IsNullOrEmpty(window) ? "-" : window),
                Kv("state", string.IsNullOrEmpty(stateId) ? "-" : stateId),
            };
        /// <summary>
        /// OK payload for DESCRIBE: the catalogue inventory a supervisor reads before
        /// writing a lane.
        ///
        /// <para><c>op=mock describe=true catalogue= states= windows= supported=
        /// live=&lt;stateId|-&gt;</c>, then ONE key per state id
        /// (<c>s0=</c>, <c>s1=</c>, ...). The per-state keys are bounded BY CONSTRUCTION
        /// (the catalogue is a compile-time list), which is the same reason
        /// <c>op=describe</c>'s window list is uncapped and <c>ListHandles</c>' is
        /// not.</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildDescribePayload(
            string catalogueId, IReadOnlyList<string> stateIds, string windows,
            string supported, string liveStateId)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            int count = stateIds != null ? stateIds.Count : 0;
            var payload = new List<KeyValuePair<string, string>>
            {
                Kv("op", TestCommandUiAction.MockOpToken),
                Kv(DescribeArg, "true"),
                Kv("catalogue", catalogueId ?? string.Empty),
                Kv("states", count.ToString(ic)),
                Kv("windows", string.IsNullOrEmpty(windows) ? "-" : windows),
                Kv("supported", string.IsNullOrEmpty(supported) ? "-" : supported),
                Kv("live", string.IsNullOrEmpty(liveStateId) ? "-" : liveStateId),
            };
            for (int i = 0; i < count; i++)
                payload.Add(Kv("s" + i.ToString(ic), stateIds[i] ?? string.Empty));
            return payload;
        }

        // ----- the draw-produced read-back -----

        /// <summary>
        /// Whether every witness string appears in the drawn text set.
        ///
        /// <para>A CONTAINS match rather than an equality, because some witnesses are a
        /// substring of the node that carries them: a Kerbals fold header draws as
        /// <c>"&#x25b6; " + HeaderText</c>, and a collapsed structure label carries its
        /// own <c>xN</c> suffix. Equality would fail those on a perfectly good mock.</para>
        ///
        /// <para>An EMPTY witness list answers FALSE. That is the anti-vacuity direction:
        /// a state whose payload produced nothing drawable must not read as applied, and
        /// the catalogue unit suite refuses such a state outright so the case cannot
        /// reach a run.</para>
        /// </summary>
        internal static bool WitnessesDrawn(IReadOnlyList<string> witnesses,
                                            IReadOnlyList<string> drawnTexts,
                                            out string firstMissing)
        {
            firstMissing = null;
            if (witnesses == null || witnesses.Count == 0)
            {
                firstMissing = "(no witness)";
                return false;
            }
            for (int i = 0; i < witnesses.Count; i++)
            {
                string want = witnesses[i];
                bool found = false;
                if (drawnTexts != null)
                {
                    for (int j = 0; j < drawnTexts.Count && !found; j++)
                    {
                        string drawn = drawnTexts[j];
                        if (drawn != null && drawn.IndexOf(want, StringComparison.Ordinal) >= 0)
                            found = true;
                    }
                }
                if (!found)
                {
                    firstMissing = want;
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Whether NO witness appears in the PRE-APPLY baseline of the same window.
        ///
        /// <para>This is the half that makes the read-back mean something. A witness the
        /// UNMOCKED window already draws witnesses nothing - it is satisfied by any
        /// capture of that window - and the review found seven states in exactly that
        /// shape, witnessing only a launch row every mission run draws. So the applier
        /// captures the window once with its chrome applied and its data untouched, and a
        /// witness present in THAT capture is a catalogue fault rather than a lane
        /// fault.</para>
        ///
        /// <para>An EMPTY baseline passes: a window that drew nothing at all before the
        /// mock (the ordinary case for a window the lane just opened onto an empty save)
        /// shares nothing with the witness set by definition.</para>
        /// </summary>
        internal static bool WitnessesAbsentFromBaseline(IReadOnlyList<string> witnesses,
                                                         IReadOnlyList<string> baseline,
                                                         out string firstShared)
        {
            firstShared = null;
            if (witnesses == null || baseline == null) return true;
            for (int i = 0; i < witnesses.Count; i++)
            {
                string want = witnesses[i];
                if (string.IsNullOrEmpty(want)) continue;
                for (int j = 0; j < baseline.Count; j++)
                {
                    string drawn = baseline[j];
                    if (drawn == null) continue;
                    if (drawn.IndexOf(want, StringComparison.Ordinal) < 0) continue;
                    firstShared = want;
                    return false;
                }
            }
            return true;
        }

        private static KeyValuePair<string, string> Kv(string k, string v)
            => new KeyValuePair<string, string>(k, v);
    }
}
