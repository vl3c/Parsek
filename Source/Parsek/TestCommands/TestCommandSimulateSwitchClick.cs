using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Patches;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Which stock-UI click site <c>SimulateStockSwitchClick</c> reproduces. The three
    /// values mirror the three arming patches one-for-one
    /// (<c>MapFocusObjectOnSelectPatch</c>, <c>SwitchIntentTrackingStationFlyPatch</c>,
    /// <c>KscVesselMarkerFlyPatch</c>); v1 implements <see cref="Map"/> only.
    /// </summary>
    internal enum SwitchClickSite
    {
        /// <summary>Map-view context menu "Switch To" (arms <c>StockActionType.MapSwitchTo</c>
        /// from FLIGHT). The only site implemented in v1.</summary>
        Map,

        /// <summary>Tracking Station "Fly" (arms <c>StockActionType.TrackingStationFly</c>
        /// from TRACKSTATION). Reserved: <c>site-not-implemented</c>.</summary>
        TrackingStation,

        /// <summary>KSC vessel-marker "Fly" (arms <c>StockActionType.KscMarkerFly</c> from
        /// SPACECENTER). Reserved: <c>site-not-implemented</c>.</summary>
        Ksc,
    }

    /// <summary>How the caller addressed the switch target.</summary>
    internal enum SwitchClickTargetSelector
    {
        /// <summary>Neither <c>vessel=</c> nor <c>pid=</c> supplied: an arg error.</summary>
        None,

        /// <summary><c>pid=&lt;persistentId&gt;</c>.</summary>
        ByPid,

        /// <summary><c>vessel=&lt;exact name&gt;</c> (percent-encoded on the wire).</summary>
        ByName,
    }

    /// <summary>The outcome of sweeping <c>FlightGlobals.Vessels</c> for the selector.</summary>
    internal enum SwitchClickTargetResolution
    {
        /// <summary>Exactly one non-ghost live vessel matched.</summary>
        Resolved,

        /// <summary>Nothing matched (no live vessel, no ghost).</summary>
        NotFound,

        /// <summary>More than one non-ghost live vessel matched. Only reachable through
        /// <c>vessel=</c>: KSP dedups <c>persistentId</c> among CURRENTLY-LIVE vessels, but
        /// vessel NAMES are not unique at all (two "Debris", two identical craft launches).
        /// Refusing beats silently picking the first one.</summary>
        Ambiguous,

        /// <summary>The only match(es) were Parsek ghost map vessels. A ghost is Parsek's own
        /// rendering artifact, never a switch target - all three stock arming patches guard
        /// it explicitly - so it reports its own reason rather than collapsing to
        /// not-found.</summary>
        GhostOnly,
    }

    /// <summary>
    /// The pre-click verdict for <c>SimulateStockSwitchClick</c> (R12). v1 drives the PLAIN
    /// arm-and-switch path only: every state in which the real
    /// <c>MapFocusObjectOnSelectPatch</c> Prefix would consult, spawn, or defer to a dialog
    /// - and every state in which the switch could not happen at all - is a typed refusal
    /// instead.
    /// </summary>
    internal enum SwitchClickGateDecision
    {
        /// <summary>Drive the plain arm-and-switch path.</summary>
        Proceed,

        /// <summary><c>ParsekScenario.Instance</c> is null, so nothing can hold the marker.
        /// Mirrors the Prefix's own scenario-null gate (which passes the click through
        /// UNARMED - useless to simulate).</summary>
        RefusedScenarioNotReady,

        /// <summary><c>CanSwitchVesselsFar</c> is off. The Prefix's gate 5: stock would not
        /// arm at all, so a "simulated click" that armed anyway would certify a path the
        /// player cannot reach.</summary>
        RefusedCannotSwitchVesselsFar,

        /// <summary>The target IS the active vessel. <c>FlightGlobals.SetActiveVessel</c>
        /// early-returns false on an already-active vessel, so the whole verb would be a
        /// no-op reporting OK - the B10-shaped fail-open.</summary>
        RefusedTargetAlreadyActive,

        /// <summary><c>DecidePreSwitchDialogAction</c> returned <c>OpenDialog</c> (Case A /
        /// B / C). A modal would spawn; no seam verb can answer it.</summary>
        RefusedDialogRequired,

        /// <summary><c>DecidePreSwitchDialogAction</c> returned <c>SkipDialogReEntry</c>: a
        /// merge dialog is ALREADY open. The Prefix falls through to the plain path under an
        /// open modal (a player who clicked anyway); a driven run must not.</summary>
        RefusedDialogPending,

        /// <summary>The target is out of the physics bubble. v1 is in-bubble only: an
        /// unloaded target sends stock through <c>FlightDriver.StartAndFocusVessel</c> - a
        /// FLIGHT scene reload the 2 s marker TTL cannot survive, and which by DESIGN starts
        /// no segment.</summary>
        RefusedTargetUnloaded,
    }

    /// <summary>The parsed <c>vessel=</c> / <c>pid=</c> selector, or the arg error.</summary>
    internal struct SwitchClickTargetDecision
    {
        /// <summary>How to address the target (<see cref="SwitchClickTargetSelector.None"/>
        /// when <see cref="Error"/> is set).</summary>
        public SwitchClickTargetSelector Selector;

        /// <summary>The parsed <c>pid=</c> value (0 unless <see cref="Selector"/> is ByPid).</summary>
        public uint Pid;

        /// <summary>The decoded <c>vessel=</c> value (null unless Selector is ByName).</summary>
        public string Name;

        /// <summary>The terminal refusal <c>msg</c>, or null when the parse succeeded.</summary>
        public string Error;
    }

    /// <summary>
    /// Pure decision + payload helpers for the single-phase <c>SimulateStockSwitchClick</c>
    /// seam verb (R12) - the PROMOTION of the long-reserved verb name.
    ///
    /// <para>WHY IT EXISTS. kRPC's <c>active_vessel</c> setter calls
    /// <c>FlightGlobals.SetActiveVessel</c> directly and bypasses the patched stock handler
    /// entirely, so a switch-segment scenario driven through kRPC certifies the WRONG code
    /// path: no <c>StockActionIntentMarker</c> is armed, the consume site sees
    /// <c>NoIntent</c>, no segment starts, and the scenario goes green while
    /// <c>MapFocusObjectOnSelectPatch</c> rots. This verb reproduces the click's OWN
    /// contract - the marker, with the map site's fields and its 2 s TTL - and then performs
    /// the same <c>SetActiveVessel</c> the patched handler performs.</para>
    ///
    /// <para>V1 SCOPE (deliberately narrow):
    /// <list type="bullet">
    /// <item><c>site=map</c> only. <c>ts</c> / <c>ksc</c> parse but return
    ///   <c>site-not-implemented</c>: both cross scenes into a fresh FLIGHT load and must go
    ///   through their own patched handlers (the TS one runs
    ///   <c>RemoveAllGhostVesselsBeforeStockFly</c>, a live-list/saved-file index desync fix
    ///   a hand-rolled call would reintroduce), so they belong with their own consumers.</item>
    /// <item>The PLAIN arm-and-switch path only. Every dialog branch of the real Prefix is a
    ///   typed refusal (see <see cref="SwitchClickGateDecision"/>), never a driven dialog.</item>
    /// <item>Loaded (in-bubble) targets only.</item>
    /// </list></para>
    ///
    /// <para>CONSUME-OUTCOME CONTRACT. The verb reports what it ARMED and what it OBSERVED
    /// (the post-switch active vessel), never what the consume site decided. The consume runs
    /// synchronously inside <c>SetActiveVessel</c> and is already fully instrumented -
    /// <c>ParsekFlight.TryConsumeStockActionIntent</c> logs its route and, on a refusal,
    /// <c>FormatRefusalDiagnostic</c> + the clear reason - so re-deriving the outcome into the
    /// payload would duplicate a grep-stable signal and invite the two to disagree. NOTE for
    /// spec authors: the consume REFUSES surface targets by design
    /// (<c>Refused_OnSurfaceTarget</c> / <c>on-surface-defer-to-trigger</c>) - the switch
    /// still happens, the SEGMENT does not arm - so a live proof of segment-arming must target
    /// a genuinely FLYING / ORBITING vessel, not one on the pad.</para>
    /// </summary>
    internal static class TestCommandSimulateSwitchClick
    {
        // ----- Terminal msg tokens (grep-stable; the wire contract) -----

        /// <summary><c>site=</c> was present but is not one of the three known spellings.</summary>
        internal const string SiteArgInvalidReason = "site-arg-invalid";

        /// <summary>A known site that v1 does not drive (<c>ts</c> / <c>ksc</c>).</summary>
        internal const string SiteNotImplementedReason = "site-not-implemented";

        /// <summary>Neither <c>vessel=</c> nor <c>pid=</c> was supplied.</summary>
        internal const string TargetArgMissingReason = "target-arg-missing";

        /// <summary><c>pid=</c> was present but is not a <c>uint</c>.</summary>
        internal const string PidArgInvalidReason = "pid-arg-invalid";

        /// <summary><c>vessel=</c> was present but empty (a typo, not an omission).</summary>
        internal const string VesselArgInvalidReason = "vessel-arg-invalid";

        /// <summary>No live vessel matched the selector.</summary>
        internal const string TargetNotFoundReason = "target-not-found";

        /// <summary>More than one live vessel carries that exact name.</summary>
        internal const string TargetAmbiguousReason = "target-name-ambiguous";

        /// <summary>The selector matched only Parsek ghost map vessels.</summary>
        internal const string TargetIsGhostReason = "target-is-ghost";

        /// <summary><c>ParsekScenario.Instance</c> is null.</summary>
        internal const string ScenarioNotReadyReason = "scenario-not-ready";

        /// <summary><c>CanSwitchVesselsFar</c> is off, so stock would not arm.</summary>
        internal const string CannotSwitchVesselsFarReason = "cannot-switch-vessels-far";

        /// <summary>The target is already the active vessel.</summary>
        internal const string TargetAlreadyActiveReason = "target-already-active";

        /// <summary>A pre-switch merge dialog would spawn; paired with <c>case=</c>.</summary>
        internal const string DialogRequiredReason = "dialog-required";

        /// <summary>A merge dialog is already open.</summary>
        internal const string DialogPendingReason = "dialog-pending";

        /// <summary>The target is out of the physics bubble.</summary>
        internal const string TargetUnloadedReason = "target-unloaded";

        /// <summary>Post-arm terminal: <c>SetActiveVessel</c> threw.</summary>
        internal const string SwitchThrewReason = "switch-threw";

        /// <summary>Post-arm terminal: the call returned but the active vessel is not the
        /// target (one of stock's <c>ClearToSave</c> / discovery-level early returns).</summary>
        internal const string SwitchRefusedReason = "switch-refused-by-stock";

        /// <summary>The only route v1 drives. Reported in the payload so a later version that
        /// adds dialog routes stays distinguishable in the same field.</summary>
        internal const string PlainRoute = "plain-arm-and-switch";

        /// <summary>The clear reason the verb uses for its own Postfix-equivalent cleanup.
        /// Byte-identical to <c>MapFocusObjectOnSelectPatch</c>'s Postfix so one grep finds
        /// both.</summary>
        internal const string RefusedNoSwitchClearReason = "refused-no-switch";

        // ----- Arg parsing -----

        /// <summary>
        /// Parses the optional <c>site=</c> arg. FAIL-CLOSED and case-sensitive, exactly like
        /// <c>LoadGame</c>'s <c>scene=</c> and <c>RunTests</c>' <c>isolated=</c>: a lenient
        /// parse would accept a spelling the harness-side validator rejects, and a
        /// silently-defaulted site is the B10-shaped fail-open (the spec believes it drove a
        /// TS Fly, the run drove a map click, the batch reads green).
        ///
        /// <para>ABSENT defaults to <see cref="SwitchClickSite.Map"/> - the only implemented
        /// site, and the one every v1 consumer wants - so a spec need not restate it and
        /// keeps meaning "map" verbatim once <c>ts</c> / <c>ksc</c> land. An EMPTY value is a
        /// typo, not an omission, so it is rejected.</para>
        /// </summary>
        internal static bool TryParseSite(string raw, out SwitchClickSite site)
        {
            site = SwitchClickSite.Map;
            if (raw == null)
                return true;
            if (raw == "map")
                return true;
            if (raw == "ts")
            {
                site = SwitchClickSite.TrackingStation;
                return true;
            }
            if (raw == "ksc")
            {
                site = SwitchClickSite.Ksc;
                return true;
            }
            return false;
        }

        /// <summary>The wire token for a site (what <c>site=</c> accepts, what the payload
        /// reports).</summary>
        internal static string SiteToken(SwitchClickSite site)
        {
            switch (site)
            {
                case SwitchClickSite.TrackingStation: return "ts";
                case SwitchClickSite.Ksc: return "ksc";
                default: return "map";
            }
        }

        /// <summary>True for the sites v1 actually drives.</summary>
        internal static bool IsSiteImplemented(SwitchClickSite site)
            => site == SwitchClickSite.Map;

        /// <summary>Terminal msg for an unparseable <c>site=</c>.</summary>
        internal static string SiteArgInvalidMsg(string raw)
            => SiteArgInvalidReason + " site=" + (raw ?? string.Empty);

        /// <summary>Terminal msg for a known-but-undriven site.</summary>
        internal static string SiteNotImplementedMsg(SwitchClickSite site)
            => SiteNotImplementedReason + " site=" + SiteToken(site);

        /// <summary>
        /// Decides how the caller addressed the target. <c>pid=</c> WINS when both are given:
        /// it is the unambiguous selector (KSP dedups <c>persistentId</c> among live vessels,
        /// while names are freely duplicated), so a spec that supplies both gets the precise
        /// one rather than a refusal it has to debug.
        ///
        /// <para><c>vessel=</c> exists because live pids are not spec-addressable - a spec
        /// author writing a TOML has no way to know the pid a launch will get - which is the
        /// same stable-addressing problem <c>InvokeRewind</c> solved. It is an EXACT ordinal
        /// name match; the percent codec makes spaces and punctuation round-trip losslessly.</para>
        ///
        /// <para>PRESENT-BUT-EMPTY is rejected on both args rather than treated as absent: it
        /// is a template that failed to interpolate, and silently falling back to the other
        /// selector (or to <c>target-arg-missing</c>) would hide that.</para>
        ///
        /// <para>The pid parse is <c>NumberStyles.Integer</c> + <c>InvariantCulture</c> - the
        /// seam's ONE numeric-arg style, shared verbatim with <c>EvaBoard</c>'s
        /// <c>targetPid</c>. It therefore tolerates surrounding whitespace, unlike the
        /// exact-match <c>site=</c> parse. That asymmetry is deliberate: a mis-spelled site
        /// silently drives the WRONG click, while stray whitespace around a number yields the
        /// same pid or no pid at all.</para>
        /// </summary>
        internal static SwitchClickTargetDecision DecideTargetSelector(string vesselArg, string pidArg)
        {
            var decision = new SwitchClickTargetDecision
            {
                Selector = SwitchClickTargetSelector.None,
                Pid = 0u,
                Name = null,
                Error = null,
            };

            if (pidArg != null)
            {
                if (!uint.TryParse(pidArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint pid))
                {
                    decision.Error = PidArgInvalidReason + " pid=" + pidArg;
                    return decision;
                }
                decision.Selector = SwitchClickTargetSelector.ByPid;
                decision.Pid = pid;
                return decision;
            }

            if (vesselArg != null)
            {
                if (vesselArg.Length == 0)
                {
                    decision.Error = VesselArgInvalidReason + " vessel=";
                    return decision;
                }
                decision.Selector = SwitchClickTargetSelector.ByName;
                decision.Name = vesselArg;
                return decision;
            }

            decision.Error = TargetArgMissingReason;
            return decision;
        }

        /// <summary>The selector rendered for a log line / terminal msg
        /// (<c>pid=123</c> or <c>vessel=Kerbal X</c>).</summary>
        internal static string SelectorToken(SwitchClickTargetDecision decision)
        {
            if (decision.Selector == SwitchClickTargetSelector.ByPid)
                return "pid=" + decision.Pid.ToString(CultureInfo.InvariantCulture);
            if (decision.Selector == SwitchClickTargetSelector.ByName)
                return "vessel=" + (decision.Name ?? string.Empty);
            return "target=<none>";
        }

        // ----- Target resolution -----

        /// <summary>
        /// Classifies a sweep of <c>FlightGlobals.Vessels</c>. The GHOST count is carried
        /// separately (rather than simply skipped) so a spec that addressed a Parsek ghost -
        /// which is a real entry in that list and would silently read as "no such vessel" -
        /// gets told exactly that.
        /// </summary>
        internal static SwitchClickTargetResolution ClassifyResolution(int liveMatchCount, int ghostMatchCount)
        {
            if (liveMatchCount > 1)
                return SwitchClickTargetResolution.Ambiguous;
            if (liveMatchCount == 1)
                return SwitchClickTargetResolution.Resolved;
            if (ghostMatchCount > 0)
                return SwitchClickTargetResolution.GhostOnly;
            return SwitchClickTargetResolution.NotFound;
        }

        /// <summary>The terminal msg for a non-<see cref="SwitchClickTargetResolution.Resolved"/>
        /// sweep. Returns null for Resolved (there is nothing to refuse).</summary>
        internal static string ResolutionMsg(
            SwitchClickTargetResolution resolution, SwitchClickTargetDecision decision, int liveMatchCount)
        {
            switch (resolution)
            {
                case SwitchClickTargetResolution.NotFound:
                    return TargetNotFoundReason + " " + SelectorToken(decision);
                case SwitchClickTargetResolution.Ambiguous:
                    return TargetAmbiguousReason + " " + SelectorToken(decision)
                        + " matches=" + liveMatchCount.ToString(CultureInfo.InvariantCulture);
                case SwitchClickTargetResolution.GhostOnly:
                    return TargetIsGhostReason + " " + SelectorToken(decision);
                default:
                    return null;
            }
        }

        // ----- The pre-click gate -----

        /// <summary>
        /// Decide whether the plain arm-and-switch path may run.
        ///
        /// <para>ORDER IS LOAD-BEARING and follows the real Prefix where the two overlap
        /// (scenario -> CanSwitchVesselsFar -> the dialog decision), with the two seam-only
        /// checks placed where they are cheapest and most specific: the already-active check
        /// runs BEFORE the dialog decision (a switch that cannot move the active vessel is a
        /// no-op whatever the dialog machinery would have said, and reporting OK for a no-op
        /// is the failure mode this verb exists to prevent), and the unloaded check runs AFTER
        /// it (so an unloaded target WITH a live recording still reports the more specific
        /// <c>dialog-required case=B-unloaded</c> the design names, rather than collapsing to
        /// the generic scope refusal).</para>
        ///
        /// <para><paramref name="dialogDecision"/> must come from
        /// <c>MapFocusObjectOnSelectPatch.DecidePreSwitchDialogAction</c> itself - the SAME
        /// predicate the Prefix calls, not a restatement of it - so the verb cannot drift out
        /// of step with the product's own case analysis. Its two pass-through values are
        /// <c>NoPriorSession</c> (nothing to ask about) and <c>SkipDialogSameTarget</c> (the
        /// Prefix's own filter: a re-click on the armed session's focused vessel falls through
        /// to the plain path, and the consume site answers it with
        /// <c>duplicate-intent-same-target</c>). Both proceed here for exactly that
        /// reason.</para>
        /// </summary>
        internal static SwitchClickGateDecision DecideSwitchGate(
            bool scenarioReady,
            bool canSwitchVesselsFar,
            bool targetIsActiveVessel,
            MapFocusObjectOnSelectPatch.PreSwitchDialogDecision dialogDecision,
            bool targetIsLoaded)
        {
            if (!scenarioReady)
                return SwitchClickGateDecision.RefusedScenarioNotReady;
            if (!canSwitchVesselsFar)
                return SwitchClickGateDecision.RefusedCannotSwitchVesselsFar;
            if (targetIsActiveVessel)
                return SwitchClickGateDecision.RefusedTargetAlreadyActive;
            if (dialogDecision == MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.OpenDialog)
                return SwitchClickGateDecision.RefusedDialogRequired;
            if (dialogDecision == MapFocusObjectOnSelectPatch.PreSwitchDialogDecision.SkipDialogReEntry)
                return SwitchClickGateDecision.RefusedDialogPending;
            if (!targetIsLoaded)
                return SwitchClickGateDecision.RefusedTargetUnloaded;
            return SwitchClickGateDecision.Proceed;
        }

        /// <summary>
        /// Which pre-switch dialog case a <see cref="SwitchClickGateDecision.RefusedDialogRequired"/>
        /// names. The tokens are BYTE-IDENTICAL to the Prefix's own <c>openCase</c> log
        /// string (<c>A-session</c> / <c>B-unloaded</c> / <c>C-loaded-separate-committed</c>)
        /// so a refusal here and the dialog it predicted grep as one story.
        /// </summary>
        internal static string DialogCaseToken(bool hasActiveSession, bool targetIsSeparateCommittedVessel)
        {
            if (hasActiveSession)
                return "A-session";
            return targetIsSeparateCommittedVessel ? "C-loaded-separate-committed" : "B-unloaded";
        }

        /// <summary>
        /// The terminal REJECTED <c>msg</c> for a gate refusal. The dialog refusal carries
        /// <c>case=&lt;token&gt;</c> (one stable reason prefix a spec can match, plus the
        /// discriminator), mirroring <c>ExitToSpaceCenter</c>'s
        /// <c>dialog-required variant=&lt;token&gt;</c> shape. Percent-encoding of the space
        /// and <c>=</c> happens in the response formatter, not here.
        /// </summary>
        internal static string GateRefusalMsg(SwitchClickGateDecision decision, string dialogCaseToken)
        {
            switch (decision)
            {
                case SwitchClickGateDecision.RefusedScenarioNotReady:
                    return ScenarioNotReadyReason;
                case SwitchClickGateDecision.RefusedCannotSwitchVesselsFar:
                    return CannotSwitchVesselsFarReason;
                case SwitchClickGateDecision.RefusedTargetAlreadyActive:
                    return TargetAlreadyActiveReason;
                case SwitchClickGateDecision.RefusedDialogRequired:
                    return DialogRequiredReason + " case=" + (dialogCaseToken ?? string.Empty);
                case SwitchClickGateDecision.RefusedDialogPending:
                    return DialogPendingReason;
                case SwitchClickGateDecision.RefusedTargetUnloaded:
                    return TargetUnloadedReason;
                default:
                    return string.Empty;
            }
        }

        // ----- Marker construction -----

        /// <summary>
        /// Build the map-site <see cref="StockActionIntentMarker"/>. FIELD-FOR-FIELD what
        /// <c>MapFocusObjectOnSelectPatch</c> builds in BOTH of its arm sites (the Prefix's
        /// own construction and the dialog handlers' <c>ArmIntentAndSwitchTo</c>): the two
        /// constants (<c>MapSwitchTo</c> + source scene <c>Flight</c>) are fixed HERE, and the
        /// four sampled values are passed in by the Unity applier from the same sources the
        /// patch samples (<c>Time.realtimeSinceStartup</c>, <c>Planetarium.GetUniversalTime</c>
        /// with the same null guard, <c>ParsekProcess.ProcessSessionId</c>, the target's
        /// <c>persistentId</c>).
        ///
        /// <para>THE TTL IS NOT AN INPUT and must never become one. It is derived from
        /// <c>Action</c> by <c>StockActionIntentMarker.GetTtlSeconds</c> - 2 s for
        /// <c>MapSwitchTo</c> - and the consume site re-evaluates freshness through
        /// <c>EvaluateStaleness</c> on every path. A seam verb that lengthened the TTL, or
        /// arranged to bypass that evaluation, would certify a marker lifetime no player click
        /// can produce, which is precisely the "certifies the wrong code path" failure this
        /// verb exists to close.</para>
        /// </summary>
        internal static StockActionIntentMarker BuildMapSwitchToMarker(
            Guid intentId, uint targetPersistentId, float capturedRealtime, double capturedUT,
            Guid processSessionId)
        {
            return new StockActionIntentMarker
            {
                IntentId = intentId,
                Action = StockActionType.MapSwitchTo,
                TargetVesselPersistentId = targetPersistentId,
                SourceScene = StockActionSourceScene.Flight,
                CapturedRealtime = capturedRealtime,
                CapturedUT = capturedUT,
                ProcessSessionId = processSessionId,
            };
        }

        // ----- Payload -----

        /// <summary>
        /// The terminal payload. Reports what was ARMED (<c>intentId</c>, <c>targetPid</c>,
        /// <c>targetName</c>, <c>site</c>, <c>route</c>) AND what was OBSERVED after the call
        /// (<c>activeVesselPid</c>, <c>switched</c>).
        ///
        /// <para>The observed pair is deliberate: a payload that reported only "we armed and
        /// called" would be a commanded-not-observed claim, and stock
        /// <c>SetActiveVessel</c> has six documented ways to return false without switching.
        /// <c>switched=false</c> is what turns those into a red instead of a green. The
        /// consume ROUTE is NOT here - see the type doc's consume-outcome contract.</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildPayload(
            string siteToken, uint targetPid, string targetName, Guid intentId, string route,
            uint activeVesselPid, bool switched)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("site", siteToken ?? string.Empty),
                new KeyValuePair<string, string>("targetPid", targetPid.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("targetName", targetName ?? string.Empty),
                new KeyValuePair<string, string>("intentId", intentId.ToString("D", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("route", route ?? string.Empty),
                new KeyValuePair<string, string>("activeVesselPid", activeVesselPid.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("switched", switched ? "true" : "false"),
            };
    }
}
