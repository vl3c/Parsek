using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The kerbal personal-parachute deployment state the seam reads, mirroring stock
    /// <c>ModuleParachute.deploymentStates</c> one-for-one. Declared here (rather than
    /// referencing the KSP enum) so the whole decision core stays pure and xUnit-drivable
    /// without Assembly-CSharp.
    /// </summary>
    internal enum EvaChuteState
    {
        /// <summary>Packed. The only state <c>Deploy()</c> accepts.</summary>
        Stowed,

        /// <summary>ARMED. <c>Deploy()</c> lands here synchronously; the module then opens
        /// the canopy on its own once static pressure clears <c>minAirPressureToOpen</c>
        /// (0.04 atm on the stock kerbalEVA chute) or altitude drops under
        /// <c>deployAltitude</c> (1000 m), the kerbal is moving faster than 1 m/s, and the
        /// shock-temperature check reads SAFE.</summary>
        Active,

        /// <summary>Streamer open. The canopy is verifiably out.</summary>
        SemiDeployed,

        /// <summary>Full canopy (below the module's 1000 m deployAltitude).</summary>
        Deployed,

        /// <summary>Cut. Stock <c>UpdateCut</c> auto-cuts below <c>autoCutSpeed</c>
        /// (0.5 m/s on the kerbalEVA chute), so a LANDED kerbal reads CUT - which is why
        /// the canopy observation is LATCHED rather than re-read at completion.</summary>
        Cut,

        /// <summary>The module could not be read (fail-closed: never counts as open).</summary>
        Unknown,
    }

    /// <summary>The two-phase <c>EvaChuteDeploy</c> completion outcome (EVA-4).</summary>
    internal enum EvaChuteCompletionDecision
    {
        /// <summary>The canopy has not been observed open yet, or the optional
        /// touchdown wait has not landed: keep polling.</summary>
        StillWaiting,

        /// <summary>Canopy verified open, the touchdown wait (when requested) satisfied
        /// with the kerbal alive, and the scene settled: terminal OK.</summary>
        CompleteOk,

        /// <summary>The budget expired with the canopy never observed open (the module
        /// stayed ARMED: shielded, sub-1 m/s, or the shock-temperature gate never read
        /// SAFE): terminal ERROR (msg=eva-chute-canopy-timeout). Never a false OK on an
        /// armed-but-never-opened chute.</summary>
        CanopyTimeout,

        /// <summary>The canopy opened but the budget expired before the kerbal reached a
        /// landed/splashed situation: terminal ERROR (msg=eva-chute-down-timeout).</summary>
        DownTimeout,

        /// <summary>The EVA kerbal vessel went away (part destroyed on impact, or the
        /// kerbal died): terminal ERROR (msg=eva-chute-kerbal-lost). A dead kerbal is a
        /// mission failure, never a survivable-landing OK.</summary>
        KerbalLost,
    }

    /// <summary>
    /// Pure decision helpers for the two-phase <c>EvaChuteDeploy</c> seam verb (EVA-4,
    /// design-autotest-eva-missions.md "EVA-4 atmospheric chute").
    ///
    /// STOCK GROUND TRUTH (decompiled KSP 1.12.5, Assembly-CSharp):
    /// <list type="bullet">
    /// <item>The kerbal's chute is <c>ModuleEvaChute : ModuleParachute</c>, declared on the
    /// kerbalEVA PART itself (Squad/Parts/Prebuilt/kerbalEVA.cfg, deployAltitude=1000,
    /// minAirPressureToOpen=0.04, autoCutSpeed=0.5, chuteMaxTemp=650). The
    /// <c>Squad/Parts/Cargo/Parachute</c> "evaChute" part is CARGO - it only has to sit in
    /// the kerbal's <c>ModuleInventoryPart</c> for the module to switch on
    /// (KerbalEVA.UpdatePackModels -> <c>evaChute.SetEVAChuteActive(hasChute &amp;&amp;
    /// CanCrewMemberUseParachute())</c>, KerbalEVA.cs:1650-1662).</item>
    /// <item>Both stock player paths call the SAME public method. The keybind path is
    /// <c>On_semi_deploy_parachute.OnCheckCondition</c> (KerbalEVA.cs:9552-9590), which
    /// checks module-enabled + STOWED + <c>EVA_ChuteDeploy</c> key + VesselUnderControl +
    /// NOT <c>JetpackDeployed</c>, then calls <c>evaChute.Deploy()</c>. The PAW path is the
    /// <c>[KSPEvent] Deploy()</c> on ModuleParachute itself. So calling <c>Deploy()</c> IS
    /// the stock click (M-C2 contract), and this verb mirrors the keybind path's guards as
    /// its own refusals rather than inventing new ones.</item>
    /// <item><c>Deploy()</c> is SYNCHRONOUS and only ARMS: STOWED -> ACTIVE in-call
    /// (ModuleParachute.cs:205-260). It silently returns when the state is not STOWED or
    /// the part is shielded from airstream, so the applier RE-READS the state right after
    /// the call - an action being called is never evidence it happened.</item>
    /// <item>The canopy then opens on the module's OWN FixedUpdate gate
    /// (ModuleParachute.cs:1255-1290): static pressure &gt; minAirPressureToOpen OR below
    /// deployAltitude, AND speed &gt; 1 m/s, AND <c>automateSafeDeploy &gt;=
    /// deploymentSafeState</c> (0 = deploy only while SAFE), AND <c>DeploySafe</c>
    /// (which reads UNSAFE for the first 1.0 s after the part unpacks). Nothing about that
    /// is instant, which is why the canopy stage is a BOUNDED WAIT on the observed
    /// deployment state and not a post-call assertion.</item>
    /// <item>Once the module reaches SEMIDEPLOYED it calls
    /// <c>kerbalEVA.OnParachuteSemiDeployed()</c> -> <c>fsm.RunEvent(On_semi_deploy_parachute)</c>.
    /// That FSM event is registered on ONLY <c>st_ragdoll</c> and <c>st_idle_fl</c>
    /// (KerbalEVA.cs:9600), and <c>KerbalFSM.RunEvent</c> for an unregistered event is a
    /// SILENT no-op (KerbalFSM.cs:298-311) - the same class of trap that produced EVA-1's
    /// false-positive ladder release. <c>On_ladderLetGo.GoToStateOnEvent = st_idle_fl</c>
    /// (KerbalEVA.cs:8678), so a VERIFIED EvaExit ladder release (release=true) is what
    /// puts the kerbal in a receptive state: this verb must run AFTER an EvaExit that
    /// reported <c>released=true</c>.</item>
    /// </list>
    /// </summary>
    internal static class TestCommandEvaChuteDeploy
    {
        // ----- Refusal reasons (wire msg= tokens; stable, greppable, no side effect) -----

        /// <summary>The active vessel is not an EVA kerbal.</summary>
        internal const string RefusalNotEva = "not-eva";

        /// <summary>The kerbal part carries no ModuleEvaChute at all (a modded / stripped
        /// EVA part).</summary>
        internal const string RefusalNoChuteModule = "no-eva-chute-module";

        /// <summary>The module exists but is switched OFF: no evaChute cargo part in the
        /// kerbal's inventory, or the crew member fails the EVAChuteSkill check. This is
        /// the fixture-contract failure (the roster kerbal lost its default inventory).</summary>
        internal const string RefusalChuteUnavailable = "eva-chute-unavailable";

        /// <summary>The chute is not STOWED (already armed, open, or cut): re-arming is
        /// not a thing the stock path can do, so refuse rather than no-op into a false OK.</summary>
        internal const string RefusalNotStowed = "eva-chute-not-stowed";

        /// <summary>The jetpack is deployed. Stock refuses the chute here with
        /// <c>#autoLOC_8004227</c> (KerbalEVA.cs:9575); the seam mirrors that refusal
        /// instead of silently bypassing it via the direct <c>Deploy()</c> call.</summary>
        internal const string RefusalJetpackDeployed = "eva-jetpack-deployed";

        /// <summary><c>Deploy()</c> returned with the state still STOWED - a silent stock
        /// refusal (shielded from airstream with shieldedCanDeploy=false). No side
        /// effect, so it rides the REJECTED family.</summary>
        internal const string RefusalArmRefused = "eva-chute-arm-refused";

        // ----- Terminal ERROR msg tokens -----

        internal const string ErrorCanopyTimeout = "eva-chute-canopy-timeout";
        internal const string ErrorDownTimeout = "eva-chute-down-timeout";
        internal const string ErrorKerbalLost = "eva-chute-kerbal-lost";

        /// <summary>
        /// The situations that satisfy the DOWN terminal for a kerbal under canopy. Both
        /// are real survivable arrivals; anything else (FLYING, SUB_ORBITAL, ...) keeps the
        /// bounded wait running.
        /// </summary>
        internal static readonly string[] DownSituations = { "LANDED", "SPLASHED" };

        /// <summary>
        /// Consecutive polls that must read the kerbal gone before the verb calls it lost.
        /// The aliveness read is a live Unity/KSP sample (vessel present, parts present,
        /// crew aboard, roster not Dead), and a single-frame blip during a scene / physics
        /// transition would otherwise red an otherwise-good run. A real death is permanent,
        /// so it survives the debounce; 3 polls is a few frames, negligible against the
        /// verb's budget.
        /// </summary>
        internal const int KerbalLossDebouncePolls = 3;

        /// <summary>True iff the kerbal should be treated as PRESENT this poll: either the
        /// live read says alive, or the run of consecutive gone-reads has not yet reached
        /// <paramref name="debouncePolls"/>. Debounce absorbs transient unreadability
        /// without ever masking a real loss (a destroyed part never comes back).</summary>
        internal static bool KerbalTreatedAlive(bool aliveThisPoll, int consecutiveGonePolls,
                                                int debouncePolls)
            => aliveThisPoll || consecutiveGonePolls < debouncePolls;

        /// <summary>True iff the canopy is verifiably OUT (streamer or full dome). Every
        /// other state - including <see cref="EvaChuteState.Active"/> (merely ARMED) and
        /// <see cref="EvaChuteState.Unknown"/> (unreadable) - reads closed, so an armed
        /// chute that never opens can never satisfy the completion.</summary>
        internal static bool IsCanopyOpen(EvaChuteState state)
            => state == EvaChuteState.SemiDeployed || state == EvaChuteState.Deployed;

        /// <summary>True iff <paramref name="situation"/> is a landed/splashed arrival
        /// (exact ordinal match against <see cref="DownSituations"/>). Null / empty /
        /// unknown reads false (fail-closed: an unreadable situation never ends the wait).</summary>
        internal static bool IsDownSituation(string situation)
        {
            if (string.IsNullOrEmpty(situation)) return false;
            for (int i = 0; i < DownSituations.Length; i++)
            {
                if (string.Equals(DownSituations[i], situation, System.StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Decide whether the arm may proceed, or the typed refusal reason (null = proceed).
        /// Evaluated in the stock keybind path's own order so the seam refuses for exactly
        /// the reasons a player's key press would be ignored, most-specific-cause first:
        /// not an EVA kerbal, no module, module switched off (no chute in inventory), not
        /// STOWED, jetpack deployed.
        ///
        /// The one keybind guard with NO analogue here is <c>VesselUnderControl</c>: that
        /// is the KEYBIND's own gate (it decides whether a key press reaches the kerbal at
        /// all - an uncontrollable vessel must not respond to input), and this verb does
        /// not go through input. It drives the MODULE directly, the same way the PAW
        /// <c>[KSPEvent] Deploy()</c> does, and the PAW path has no such check either.
        /// Mirroring it would invent a refusal neither stock path applies to a direct
        /// module call, and would red an EVA kerbal that is perfectly able to chute.
        /// </summary>
        internal static string DecideArmRefusal(
            bool activeVesselIsEva, bool hasChuteModule, bool chuteModuleEnabled,
            EvaChuteState state, bool jetpackDeployed)
        {
            if (!activeVesselIsEva) return RefusalNotEva;
            if (!hasChuteModule) return RefusalNoChuteModule;
            if (!chuteModuleEnabled) return RefusalChuteUnavailable;
            if (state != EvaChuteState.Stowed) return RefusalNotStowed;
            if (jetpackDeployed) return RefusalJetpackDeployed;
            return null;
        }

        /// <summary>
        /// Decide whether the synchronous <c>Deploy()</c> call ACTUALLY armed the chute.
        /// <c>Deploy()</c> sets STOWED -> ACTIVE in-call, so a post-call read that is still
        /// STOWED means stock silently refused. Reading ACTIVE (or anything further along,
        /// if a fast frame already opened the canopy) means the arm took.
        /// </summary>
        internal static bool ArmTook(EvaChuteState postCallState)
            => postCallState != EvaChuteState.Stowed && postCallState != EvaChuteState.Unknown;

        /// <summary>
        /// Decide the two-phase EvaChuteDeploy completion. Ordering mirrors
        /// <see cref="TestCommandEvaExit.DecideEvaExitCompletion"/>: the hard loss first,
        /// then positive completion, then the budget, then StillWaiting.
        ///
        /// <paramref name="canopyVerified"/> is a LATCH the applier sets the first poll it
        /// observes an open canopy - it is NOT re-read at completion, because the stock
        /// module auto-CUTS below 0.5 m/s (ModuleParachute.UpdateCut), so a kerbal standing
        /// on the ground reads CUT and a re-read would fail a landing that actually worked.
        ///
        /// The survivability conjunct is split across TWO aliveness bits, deliberately:
        /// <list type="bullet">
        /// <item><paramref name="kerbalTreatedAlive"/> is the DEBOUNCED bit
        /// (<see cref="KerbalTreatedAlive"/>): false only after
        /// <see cref="KerbalLossDebouncePolls"/> consecutive gone-reads, so a transient
        /// unreadable sample cannot red a good descent. It drives ONLY
        /// <see cref="EvaChuteCompletionDecision.KerbalLost"/>.</item>
        /// <item><paramref name="kerbalAliveThisPoll"/> is the RAW live read, and it is a
        /// required conjunct of CompleteOk. Without it "KerbalLost beats everything" would
        /// only hold AFTER the debounce expires: a kerbal dying INSIDE the debounce window
        /// (canopy already latched, scene settled, and with awaitDown=false no landing to
        /// wait for) would satisfy every other conjunct on the very poll the loss began and
        /// complete OK. Requiring the raw bit makes the poll that first reads the kerbal
        /// gone unable to certify success, whatever the debounce says.</item>
        /// </list>
        /// A dead kerbal is a FAILURE, never the DOWN success this verb reports; a poll
        /// that is unsure yields StillWaiting (or, past budget, an honest timeout), never OK.
        /// </summary>
        internal static EvaChuteCompletionDecision DecideChuteCompletion(
            double elapsed, bool kerbalTreatedAlive, bool kerbalAliveThisPoll,
            bool canopyVerified, bool awaitDown, bool down, bool sceneSettled, double budget)
        {
            if (!kerbalTreatedAlive)
                return EvaChuteCompletionDecision.KerbalLost;
            bool downSatisfied = !awaitDown || down;
            if (kerbalAliveThisPoll && canopyVerified && downSatisfied && sceneSettled)
                return EvaChuteCompletionDecision.CompleteOk;
            if (elapsed >= budget)
                return canopyVerified
                    ? EvaChuteCompletionDecision.DownTimeout
                    : EvaChuteCompletionDecision.CanopyTimeout;
            return EvaChuteCompletionDecision.StillWaiting;
        }

        /// <summary>The terminal ERROR msg token for a non-OK, non-waiting decision
        /// (null for StillWaiting / CompleteOk).</summary>
        internal static string ErrorTokenFor(EvaChuteCompletionDecision decision)
        {
            switch (decision)
            {
                case EvaChuteCompletionDecision.CanopyTimeout: return ErrorCanopyTimeout;
                case EvaChuteCompletionDecision.DownTimeout: return ErrorDownTimeout;
                case EvaChuteCompletionDecision.KerbalLost: return ErrorKerbalLost;
                default: return null;
            }
        }

        /// <summary>Terminal completion payload. <c>canopy</c> reports the LATCHED
        /// verified-open observation (never a bare called-Deploy bit), <c>chuteState</c>
        /// the live deployment state at completion (commonly Cut after touchdown, by the
        /// stock auto-cut), and <c>down</c>/<c>situation</c> the arrival evidence.</summary>
        internal static List<KeyValuePair<string, string>> BuildCompletePayload(
            string kerbal, uint evaPid, bool canopyVerified, EvaChuteState chuteState,
            bool down, string situation)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("kerbal", kerbal ?? string.Empty),
                new KeyValuePair<string, string>("evaPid",
                    evaPid.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("canopy", canopyVerified ? "true" : "false"),
                new KeyValuePair<string, string>("chuteState", chuteState.ToString()),
                new KeyValuePair<string, string>("down", down ? "true" : "false"),
                new KeyValuePair<string, string>("situation", situation ?? string.Empty),
            };
    }
}
