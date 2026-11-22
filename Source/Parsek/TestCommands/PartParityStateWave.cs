using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The pure decisions of the live state engine (design-part-ghost-parity.md Appendix D.3
    /// and D.5) that <see cref="PartParityStateDriver"/> and the sweep runner read: the state
    /// wave's slot rule and composition, the wave admission, the refused-press check, the
    /// filler node of a stacked context, the stepped prime schedules of the two judged ghost
    /// routes, and the flags a state case writes. Plain values only, so xUnit drives every one.
    /// </summary>
    internal static class PartParityStateWave
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        // ----- the slot rule (D.3) -----

        /// <summary>The slots an orbit state wave uses, on ONE side of the host, inner first:
        /// every other slot of <see cref="PartParitySweepPlan.SlotPositionsM"/>, so the cases
        /// sit 80 m apart and 60 m or more from the host (a deployed part reaches past the
        /// stowed bounds the stowed slot plan reads).</summary>
        internal static readonly double[] StateSlotPositionsM =
        {
            PartParitySweepPlan.SlotPositionsM[0], PartParitySweepPlan.SlotPositionsM[2],
        };

        /// <summary>Along-track offsets for <paramref name="count"/> state cases: +60, -60,
        /// +140, -140, at most <see cref="PartParitySweepPlan.MaxStateWaveCases"/>.</summary>
        internal static List<double> PlanStateWave(int count)
        {
            var offsets = new List<double>();
            int n = Math.Min(Math.Max(0, count), PartParitySweepPlan.MaxStateWaveCases);
            for (int i = 0; i < n; i++)
            {
                double pos = StateSlotPositionsM[i / 2];
                offsets.Add(i % 2 == 0 ? pos : -pos);
            }
            return offsets;
        }

        // ----- wave composition (D.3) -----

        /// <summary>The wave kind of a stowed case (the stowed waves of today).</summary>
        internal const string KindStowed = "stowed";

        /// <summary>The wave kind of a case: <c>stowed</c> for a case with no state option,
        /// else <c>state:&lt;bucket&gt;</c>, so a state wave holds one bucket and its
        /// worst case is that bucket's.</summary>
        internal static string WaveKind(PartParityCaseKey key)
        {
            if (key?.StateOption == null) return KindStowed;
            return "state:" + PartParityStateRules.BucketFor(key.StateOption.Family).ToString();
        }

        /// <summary>
        /// The queue positions the next wave takes, from <paramref name="cursor"/> (design 5.3,
        /// D.3). A case owed a solo run flies alone. A stowed wave is the leading run of stowed
        /// cases not owed a solo run, up to <paramref name="maxStowed"/> (exactly today's
        /// rule). A state wave takes the case at the cursor and then the next cases of the SAME
        /// kind further down the queue (not owed a solo run), up to
        /// <paramref name="maxState"/>, so a wave holds one bucket even where the stable order
        /// interleaves families.
        /// </summary>
        internal static List<int> PickWave(IList<string> kinds, IList<bool> solo, int cursor,
            int maxStowed, int maxState)
        {
            var picked = new List<int>();
            if (kinds == null || solo == null || cursor < 0 || cursor >= kinds.Count) return picked;
            if (solo[cursor]) { picked.Add(cursor); return picked; }
            string kind = kinds[cursor];
            if (kind == KindStowed)
            {
                for (int i = cursor; i < kinds.Count && picked.Count < maxStowed; i++)
                {
                    if (solo[i] || kinds[i] != KindStowed) break;
                    picked.Add(i);
                }
                return picked;
            }
            for (int i = cursor; i < kinds.Count && picked.Count < maxState; i++)
                if (!solo[i] && kinds[i] == kind) picked.Add(i);
            return picked;
        }

        /// <summary>Moves the picked positions to the cursor, in their order, keeping the
        /// order of the rest (a stable partition). Returns the reordered list.</summary>
        internal static List<T> BringToCursor<T>(IList<T> queue, int cursor, IList<int> picked)
        {
            var result = new List<T>(queue.Count);
            var chosen = new HashSet<int>(picked);
            for (int i = 0; i < cursor; i++) result.Add(queue[i]);
            foreach (int i in picked) result.Add(queue[i]);
            for (int i = cursor; i < queue.Count; i++)
                if (!chosen.Contains(i)) result.Add(queue[i]);
            return result;
        }

        /// <summary>
        /// Whether the next wave may start (D.3): a stowed wave by today's rule (the elapsed
        /// time plus the last wave's wall); a state wave only when the elapsed time plus the
        /// wave's own worst case (<see cref="PartParitySweepPlan.WaveWorstCaseSeconds"/>) stays
        /// inside the box. The first wave of a step always starts.
        /// </summary>
        internal static bool AdmitWave(string kind, double elapsedSeconds, int maxSeconds,
            double lastWaveSeconds, int cases, bool anySunTracker, IEnumerable<string> families = null)
        {
            if (kind == null || kind == KindStowed)
                return TestCommandPartParitySweep.ShouldStartWave(elapsedSeconds, maxSeconds, lastWaveSeconds);
            return PartParitySweepPlan.ShouldStartStateWave(elapsedSeconds, maxSeconds,
                PartParitySweepPlan.WaveWorstCaseSeconds(BucketOfKind(kind), cases, anySunTracker, families));
        }

        /// <summary>The bucket a <c>state:&lt;bucket&gt;</c> kind names (Long when unreadable).</summary>
        internal static PartParityStateBucket BucketOfKind(string kind)
        {
            const string prefix = "state:";
            if (kind != null && kind.StartsWith(prefix, StringComparison.Ordinal))
            {
                string b = kind.Substring(prefix.Length);
                foreach (PartParityStateBucket v in Enum.GetValues(typeof(PartParityStateBucket)))
                    if (v.ToString() == b) return v;
            }
            return PartParityStateBucket.Long;
        }

        // ----- the press (D.3) -----

        /// <summary>How many frames after the press the module must have moved (else refused).</summary>
        internal const int RefuseCheckFrames = 2;

        /// <summary>
        /// The press was refused (D.3): <see cref="RefuseCheckFrames"/> frames after an invoked
        /// press, the module's state signature is unchanged, no Animation of the part plays and
        /// the state is not reached. A press that answered missing / threw is decided at once.
        /// </summary>
        internal static bool PressRefused(string signatureBefore, string signatureNow,
            bool anyAnimationPlaying, bool reached)
        {
            if (reached || anyAnimationPlaying) return false;
            return string.Equals(signatureBefore ?? string.Empty, signatureNow ?? string.Empty, StringComparison.Ordinal);
        }

        // ----- the stacked context (D.3) -----

        /// <summary>The filler part a stacked context puts on each jettison node.</summary>
        internal const string FillerPartName = "fuelTankSmallFlat";

        /// <summary>The filler's node that meets the subject's node: a subject node above the
        /// part's origin (<paramref name="subjectNodeY"/> &gt; 0, a <c>top</c> node) takes the
        /// filler's <c>bottom</c>, any other its <c>top</c>.</summary>
        internal static string FillerAttachNode(double subjectNodeY) => subjectNodeY > 0.0 ? "bottom" : "top";

        // ----- the stepped primes (D.5) -----

        /// <summary>How many 1 s primes follow a sun tracker's first prime (10 steps cover
        /// 180 deg at the 20 deg/s slew).</summary>
        internal const int SunTrackerPrimeSteps = 10;

        /// <summary>The spacing of the stepped primes, at the synthesized-motion sample cap
        /// (<c>GhostPlaybackLogic.MaxSynthSampleSeconds</c>, 1 s): a longer step re-seeds and
        /// skips, so one late prime never tracks.</summary>
        internal const double PrimeStepSeconds = 1.0;

        /// <summary>The pad after the clamped clip before the event route's prime (D.5).</summary>
        internal const double EventPrimePadSeconds = 1.0;

        /// <summary>
        /// The event route's prime UTs (D.5): <c>tE</c> = the last event's UT (the stowed seed
        /// UT when no event was recorded) + the clamped clip + 1 s, so the animated deploy path
        /// completes; then, for a sun tracker, 10 more primes 1 s apart.
        /// </summary>
        internal static List<double> EventRoutePrimeUTs(double seedUT, IList<double> eventUTs,
            double clampedClipSeconds, bool sunTracker)
        {
            double last = seedUT;
            if (eventUTs != null)
                foreach (double u in eventUTs)
                    if (!double.IsNaN(u) && u > last) last = u;
            double clip = double.IsNaN(clampedClipSeconds) || clampedClipSeconds < 0.0 ? 0.0 : clampedClipSeconds;
            return Stepped(last + clip + EventPrimePadSeconds, sunTracker);
        }

        /// <summary>The start route's prime UTs (D.5): <c>t1</c> (the first frame a player
        /// sees), then, for a sun tracker, 10 more primes 1 s apart.</summary>
        internal static List<double> StartRoutePrimeUTs(double startUT, bool sunTracker)
            => Stepped(startUT, sunTracker);

        private static List<double> Stepped(double first, bool sunTracker)
        {
            var uts = new List<double> { first };
            if (sunTracker)
                for (int k = 1; k <= SunTrackerPrimeSteps; k++) uts.Add(first + k * PrimeStepSeconds);
            return uts;
        }

        // ----- flags (D.3 to D.6) -----

        /// <summary>A sun tracker's start route this far (degrees) from the real pivot at its
        /// first frame is reported (<c>sun-tracker-first-frame:&lt;deg&gt;</c>, report-only).</summary>
        internal const double SunTrackerFirstFrameReportDeg = 1.0;

        /// <summary>The inputs of a state case's flags, all read live by the driver.</summary>
        internal sealed class FlagInputs
        {
            internal bool Pressed;
            internal bool Reached;
            internal int EventsRecorded;
            internal string GhostEvent;
            internal string GhostStart;
            internal bool? StartFirstFrameAtTarget;
            internal double? SunFirstFrameDeltaDeg;
            internal bool StillTracking;
            internal int ContextHidden;
            internal int DebrisHidden;
        }

        /// <summary>
        /// The flags and tags a state case writes (D.3 to D.6), in a stable order:
        /// <c>state-not-recorded</c> (a press reached the state, no event recorded),
        /// <c>state-not-modelled:&lt;route&gt;:&lt;outcome&gt;</c> per judged route not applied,
        /// <c>start-pose-not-first-frame</c>, <c>sun-tracker-first-frame:&lt;deg&gt;</c>,
        /// <c>still-tracking</c>, <c>context-hidden:&lt;n&gt;</c>, <c>jettison-debris-hidden:&lt;n&gt;</c>.
        /// A route outcome that is null (the route was not built) writes nothing.
        /// </summary>
        internal static List<string> StateFlags(FlagInputs f)
        {
            var flags = new List<string>();
            if (f == null) return flags;
            if (f.Pressed && f.Reached && f.EventsRecorded == 0) flags.Add(PartParityStateRules.FlagStateNotRecorded);
            string ev = PartParityStateRules.StateNotModelledFlag(PartParityStateRules.RouteEvent, f.GhostEvent);
            if (ev != null) flags.Add(ev);
            string st = PartParityStateRules.StateNotModelledFlag(PartParityStateRules.RouteStart, f.GhostStart);
            if (st != null) flags.Add(st);
            if (f.StartFirstFrameAtTarget == false) flags.Add(PartParityStateRules.FlagStartPoseNotFirstFrame);
            if (f.SunFirstFrameDeltaDeg.HasValue && f.SunFirstFrameDeltaDeg.Value > SunTrackerFirstFrameReportDeg)
                flags.Add(PartParityStateRules.FlagSunTrackerFirstFramePrefix + f.SunFirstFrameDeltaDeg.Value.ToString("0.0", Ic));
            if (f.StillTracking) flags.Add(PartParityStateRules.TagStillTracking);
            if (f.ContextHidden > 0) flags.Add(PartParityStateRules.TagContextHiddenPrefix + f.ContextHidden.ToString(Ic));
            if (f.DebrisHidden > 0) flags.Add(PartParityStateRules.TagJettisonDebrisHiddenPrefix + f.DebrisHidden.ToString(Ic));
            return flags;
        }

        /// <summary>The <c>stateProof.events</c> rows of a list of recorded part events.</summary>
        internal static List<PartParityStateEvent> ProofEvents(IList<PartEvent> events)
        {
            var list = new List<PartParityStateEvent>();
            if (events == null) return list;
            foreach (PartEvent e in events)
                list.Add(new PartParityStateEvent
                {
                    Type = e.eventType.ToString(), Ut = e.ut, Pid = e.partPersistentId, Midx = e.moduleIndex,
                });
            return list;
        }

        /// <summary>The <c>stateProof</c> of a state or context case that crashed before its
        /// sidecar (design D.2): its option, never reached, no press result; null for a stowed
        /// case (no option).</summary>
        internal static PartParityStateProof CrashedStateProof(PartParityStateOption o)
        {
            if (o == null) return null;
            return new PartParityStateProof
            {
                Family = o.Family, Module = o.Module, ModuleIndex = o.ModuleIndex, State = o.State,
                Action = o.Action, Reached = false,
                Nodes = o.NodeNames != null && o.NodeNames.Count > 0
                    ? o.NodeNames.ConvertAll(n => new PartParityContextNode { Node = n, Part = FillerPartName })
                    : null,
            };
        }

        /// <summary>The id of the same part and skin at <c>stowed</c> / <c>alone</c> (the
        /// viewer pairs a state case with it); null for a stowed / alone case itself.</summary>
        internal static string StowedCaseId(PartParityCaseKey key)
        {
            if (key == null) return null;
            string state = key.State ?? TestCommandPartParitySweep.DefaultState;
            string context = key.Context ?? TestCommandPartParitySweep.DefaultContext;
            if (state == TestCommandPartParitySweep.DefaultState && context == TestCommandPartParitySweep.DefaultContext)
                return null;
            return TestCommandPartParitySweep.CaseId(new PartParityCaseKey
            {
                Part = key.Part, Source = key.Source, Skin = key.Skin, SkinOrdinal = key.SkinOrdinal,
                State = TestCommandPartParitySweep.DefaultState, Context = TestCommandPartParitySweep.DefaultContext,
            });
        }

        /// <summary>One <c>case &lt;id&gt;: state ...</c> Verbose line's body: the press, the
        /// wait and both ghost routes.</summary>
        internal static string DescribeState(string caseId, PartParityStateProof p, string status)
        {
            var sb = new StringBuilder("case ");
            sb.Append(caseId ?? "<none>").Append(": state family=").Append(p?.Family ?? "<none>")
                .Append(" module=").Append(p?.Module ?? "<none>")
                .Append(" midx=").Append(p?.ModuleIndex.HasValue == true ? p.ModuleIndex.Value.ToString(Ic) : "-")
                .Append(" state=").Append(p?.State ?? "<none>")
                .Append(" action=").Append(p?.Action ?? "<none>")
                .Append(" result=").Append(p?.ActionResult ?? "<none>")
                .Append(" reached=").Append(p?.Reached.HasValue == true ? (p.Reached.Value ? "true" : "false") : "-")
                .Append(" waitS=").Append(p?.WaitS.HasValue == true ? p.WaitS.Value.ToString("0.00", Ic) : "-")
                .Append(" endpoint=").Append(p?.EndpointField ?? "-").Append('=').Append(p?.EndpointValue ?? "-")
                .Append(" events=").Append(p?.Events != null ? p.Events.Count.ToString(Ic) : "-")
                .Append(" ghostEvent=").Append(p?.GhostEvent ?? "-")
                .Append(" ghostStart=").Append(p?.GhostStart ?? "-")
                .Append(" status=").Append(status ?? "<none>");
            return sb.ToString();
        }
    }
}
