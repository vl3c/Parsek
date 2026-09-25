using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>The editor building a <c>GoToEditor</c> call enters.</summary>
    internal enum EditorRouteFacility
    {
        None = 0,
        /// <summary>The Vehicle Assembly Building (<c>VehicleAssemblyBuilding</c>).</summary>
        VAB,
        /// <summary>The Spaceplane Hangar (<c>SpacePlaneHangarBuilding</c>).</summary>
        SPH,
    }

    /// <summary>What one settle poll of <c>GoToEditor</c> concludes.</summary>
    internal enum GoToEditorPollOutcome
    {
        /// <summary>Keep polling.</summary>
        NotYet,
        /// <summary>The editor is up and a craft was requested: issue the load now.</summary>
        IssueCraftLoad,
        /// <summary>The editor (and the requested craft, if any) is settled: terminal OK.</summary>
        Ready,
        /// <summary>The scene settled back at MAINMENU: terminal ERROR.</summary>
        ReturnedToMenu,
        /// <summary>The budget expired first: terminal ERROR.</summary>
        TimedOut,
    }

    /// <summary>The pre-launch verdict for <c>LaunchFromEditor</c>.</summary>
    internal enum LaunchFromEditorGate
    {
        /// <summary>Press the Launch button.</summary>
        Proceed,
        /// <summary>Not in the editor scene.</summary>
        WrongScene,
        /// <summary>No editor, or the editor holds no ship with parts.</summary>
        NoShip,
        /// <summary>The <c>EDITOR_LAUNCH</c> control lock is held: stock greys the button.</summary>
        LaunchLocked,
        /// <summary><c>site=</c> names a site the current editor cannot launch from.</summary>
        SiteInvalid,
        /// <summary>Vessels stand on the launch site: stock's LaunchSiteClear check would
        /// open its recover-the-obstruction dialog, which no seam verb can answer.</summary>
        SiteObstructed,
    }

    /// <summary>
    /// Pure half of the two scene-route seam verbs that reach the vehicle editor
    /// (coverage wave 12, D14 <c>scene-editor</c>). Before them <c>DecideLoadRoute</c>
    /// reached only FLIGHT, SPACECENTER and TRACKSTATION, and no verb could put a run in
    /// the VAB or SPH or launch from there, so the scene transition a player makes most
    /// often (Space Center -> editor -> launch) had never run under the harness.
    ///
    /// <para><c>GoToEditor facility=&lt;VAB|SPH&gt; [craft=&lt;name&gt;]</c> runs at the Space
    /// Center. It clicks the building through stock's own entry
    /// (<c>SpaceCenterBuilding.EnterBuilding</c> -> the building's <c>OnClicked</c>, which
    /// saves persistent and calls <c>EditorDriver.StartEditor</c>), and when
    /// <c>craft=</c> is given it then loads that craft through the craft browser's own
    /// Normal-load body (<c>EditorLogic.LoadShipFromFile</c>). The craft is looked up the
    /// way the load dialog lists it: the save's own <c>Ships/&lt;facility&gt;</c>, the other
    /// facility's folder, then the stock craft under the KSP root.</para>
    ///
    /// <para><c>LaunchFromEditor [site=&lt;name&gt;]</c> runs in the editor and presses the
    /// Launch button (<c>EditorLogic.launchVessel</c>; with <c>site=</c>, the launch-site
    /// picker's <c>launchVessel(siteName)</c>). Two refusals guard the modals stock would
    /// otherwise raise and no seam verb can answer: a held <c>EDITOR_LAUNCH</c> lock (the
    /// button is disabled) and an obstructed site (the recover-the-obstruction dialog,
    /// whose proceed branch would RECOVER vessels). The terminal waits for FLIGHT with
    /// the launched vessel active.</para>
    ///
    /// <para>Both are TWO-PHASE and neither parses a save off disk, so they sit in the
    /// ExitToSpaceCenter budget class rather than LoadGame's. The vocabularies below are
    /// mirrored by <c>hlib.EDITORROUTE_*</c>.</para>
    /// </summary>
    internal static class TestCommandEditorRoute
    {
        internal const string GoToEditorVerb = "GoToEditor";
        internal const string LaunchFromEditorVerb = "LaunchFromEditor";

        internal const string FacilityArg = "facility";
        internal const string CraftArg = "craft";
        internal const string SiteArg = "site";

        /// <summary>Facility tokens, in <see cref="EditorRouteFacility"/> order. Case-sensitive,
        /// spelled the way stock's <c>EditorFacility</c> enum and <c>Ships/</c> folders are.</summary>
        internal static readonly string[] FacilityTokens = { "VAB", "SPH" };

        // ---- GoToEditor refusal / error reasons ----
        internal const string FacilityArgMissingReason = "goeditor-facility-arg-missing";
        internal const string FacilityArgInvalidReason = "goeditor-facility-arg-invalid";
        internal const string CraftArgInvalidReason = "goeditor-craft-arg-invalid";
        internal const string GoWrongSceneReason = "goeditor-wrong-scene";
        internal const string CraftNotFoundReason = "goeditor-craft-not-found";
        internal const string BuildingNotFoundReason = "goeditor-building-not-found";
        internal const string FacilityClosedReason = "goeditor-facility-closed";
        internal const string GoReturnedToMenuReason = "goeditor-returned-to-menu";
        internal const string GoNotSettledReason = "goeditor-not-settled";

        // ---- LaunchFromEditor refusal / error reasons ----
        internal const string LaunchWrongSceneReason = "launchfromeditor-wrong-scene";
        internal const string LaunchNoShipReason = "launchfromeditor-no-ship";
        internal const string LaunchLockedReason = "launchfromeditor-launch-locked";
        internal const string LaunchSiteInvalidReason = "launchfromeditor-site-invalid";
        internal const string LaunchSiteObstructedReason = "launchfromeditor-site-obstructed";
        internal const string LaunchReturnedToMenuReason = "launchfromeditor-returned-to-menu";
        internal const string LaunchNotSettledReason = "launchfromeditor-not-settled";

        /// <summary>Every refusal / error reason of both verbs, for the hlib mirror.</summary>
        internal static readonly string[] Reasons =
        {
            FacilityArgMissingReason, FacilityArgInvalidReason, CraftArgInvalidReason,
            GoWrongSceneReason, CraftNotFoundReason, BuildingNotFoundReason,
            FacilityClosedReason, GoReturnedToMenuReason, GoNotSettledReason,
            LaunchWrongSceneReason, LaunchNoShipReason, LaunchLockedReason,
            LaunchSiteInvalidReason, LaunchSiteObstructedReason,
            LaunchReturnedToMenuReason, LaunchNotSettledReason,
        };

        /// <summary>Frames a settled editor must have drawn before the terminal, so the
        /// crew manifest and part list a following launch reads are populated.</summary>
        internal const int MinSettleFrames = 3;

        internal static string FacilityToken(EditorRouteFacility facility)
        {
            int i = (int)facility - 1;
            return i >= 0 && i < FacilityTokens.Length ? FacilityTokens[i] : "none";
        }

        /// <summary>The OTHER editor's folder: the load dialog lists both tabs in either
        /// building.</summary>
        internal static EditorRouteFacility OtherFacility(EditorRouteFacility facility)
            => facility == EditorRouteFacility.SPH ? EditorRouteFacility.VAB : EditorRouteFacility.SPH;

        /// <summary>Parses <c>facility=</c> (required) and <c>craft=</c> (optional). A craft
        /// name is a bare file stem: a path separator or a <c>..</c> run is refused so the
        /// lookup can never leave the craft folders.</summary>
        internal static bool TryParseGoToEditor(
            string rawFacility, string rawCraft,
            out EditorRouteFacility facility, out string craft,
            out string rejectReason, out string detail)
        {
            facility = EditorRouteFacility.None;
            craft = null;
            rejectReason = null;
            detail = null;

            if (string.IsNullOrEmpty(rawFacility))
            {
                rejectReason = FacilityArgMissingReason;
                detail = "valid=" + string.Join(",", FacilityTokens);
                return false;
            }
            int f = Array.IndexOf(FacilityTokens, rawFacility);
            if (f < 0)
            {
                rejectReason = FacilityArgInvalidReason;
                detail = "facility=" + rawFacility + " valid=" + string.Join(",", FacilityTokens);
                return false;
            }
            facility = (EditorRouteFacility)(f + 1);

            if (rawCraft != null)
            {
                if (!IsSafeCraftName(rawCraft))
                {
                    rejectReason = CraftArgInvalidReason;
                    detail = "craft=" + rawCraft;
                    return false;
                }
                craft = rawCraft;
            }
            return true;
        }

        /// <summary>True for a non-blank bare file stem (no directory part, no <c>..</c>,
        /// no character <c>Path</c> rejects in a file name).</summary>
        internal static bool IsSafeCraftName(string craft)
        {
            if (string.IsNullOrEmpty(craft) || craft.Trim().Length == 0) return false;
            if (craft.Contains("..")) return false;
            if (craft.IndexOf('/') >= 0 || craft.IndexOf('\\') >= 0 || craft.IndexOf(':') >= 0)
                return false;
            return craft.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0;
        }

        /// <summary>
        /// The ordered craft-file candidates the lookup tries: the save's
        /// <c>Ships/&lt;facility&gt;</c>, the save's other folder, then the stock
        /// <c>Ships/&lt;facility&gt;</c> and stock other folder under the KSP root. That is the
        /// order the craft browser offers them in (the player's own craft first, the Stock
        /// tab last). Roots are joined with <c>/</c> so the list is platform-neutral; the
        /// applier resolves each through the file system.
        /// </summary>
        internal static List<string> CraftCandidates(
            string saveShipsRoot, string stockShipsRoot, EditorRouteFacility facility, string craft)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(craft)) return list;
            string file = craft + ".craft";
            string own = FacilityToken(facility);
            string other = FacilityToken(OtherFacility(facility));
            foreach (string root in new[] { saveShipsRoot, stockShipsRoot })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string trimmed = root.TrimEnd('/', '\\');
                list.Add(trimmed + "/" + own + "/" + file);
                list.Add(trimmed + "/" + other + "/" + file);
            }
            return list;
        }

        /// <summary>The first candidate <paramref name="exists"/> accepts, or null.</summary>
        internal static string ResolveCraftPath(
            string saveShipsRoot, string stockShipsRoot, EditorRouteFacility facility, string craft,
            Func<string, bool> exists)
        {
            if (exists == null) return null;
            foreach (string candidate in CraftCandidates(saveShipsRoot, stockShipsRoot, facility, craft))
                if (exists(candidate)) return candidate;
            return null;
        }

        /// <summary>
        /// Decide one <c>GoToEditor</c> poll. Order: a MAINMENU settle is the fast failure;
        /// then the editor must be up (scene EDITOR with a started <c>EditorLogic</c> and
        /// no side panel sliding); a requested craft is loaded ONCE, after the editor is
        /// up, because the load restarts the editor inside the scene and would be undone
        /// by a scene start that had not finished; readiness then needs the craft on the
        /// stage and <see cref="MinSettleFrames"/> drawn frames in the current phase. The
        /// budget is the catch-all.
        /// </summary>
        internal static GoToEditorPollOutcome DecideGoToEditorPoll(
            TestCommandScene scene, bool editorUp, bool craftRequested, bool craftLoadIssued,
            bool craftOnStage, int framesInPhase, bool budgetExpired)
        {
            if (scene == TestCommandScene.MainMenu)
                return GoToEditorPollOutcome.ReturnedToMenu;
            if (scene == TestCommandScene.Editor && editorUp && framesInPhase >= MinSettleFrames)
            {
                if (craftRequested && !craftLoadIssued)
                    return GoToEditorPollOutcome.IssueCraftLoad;
                if (!craftRequested || craftOnStage)
                    return GoToEditorPollOutcome.Ready;
            }
            return budgetExpired ? GoToEditorPollOutcome.TimedOut : GoToEditorPollOutcome.NotYet;
        }

        /// <summary>
        /// The launch gate, in the order a player meets it: the scene, a ship to launch,
        /// the Launch button being enabled, the chosen site being one this editor offers,
        /// and the site being clear. The obstruction check is stock's own
        /// (<c>ShipConstruction.FindVesselsLandedAt</c>, the predicate
        /// <c>LaunchSiteClear.Test</c> uses), sampled by the applier.
        /// </summary>
        internal static LaunchFromEditorGate DecideLaunchGate(
            TestCommandScene scene, bool hasEditor, int shipPartCount, bool launchUnlocked,
            bool siteValid, int obstructingVessels)
        {
            if (scene != TestCommandScene.Editor)
                return LaunchFromEditorGate.WrongScene;
            if (!hasEditor || shipPartCount <= 0)
                return LaunchFromEditorGate.NoShip;
            if (!launchUnlocked)
                return LaunchFromEditorGate.LaunchLocked;
            if (!siteValid)
                return LaunchFromEditorGate.SiteInvalid;
            if (obstructingVessels > 0)
                return LaunchFromEditorGate.SiteObstructed;
            return LaunchFromEditorGate.Proceed;
        }

        /// <summary>The stable reason for a non-proceed gate, or null for Proceed.</summary>
        internal static string GateReason(LaunchFromEditorGate gate)
        {
            switch (gate)
            {
                case LaunchFromEditorGate.WrongScene: return LaunchWrongSceneReason;
                case LaunchFromEditorGate.NoShip: return LaunchNoShipReason;
                case LaunchFromEditorGate.LaunchLocked: return LaunchLockedReason;
                case LaunchFromEditorGate.SiteInvalid: return LaunchSiteInvalidReason;
                case LaunchFromEditorGate.SiteObstructed: return LaunchSiteObstructedReason;
                default: return null;
            }
        }

        /// <summary>
        /// Decide the launch completion. OK needs FLIGHT, a loaded game AND the launched
        /// vessel active: FLIGHT settles a frame or two before <c>FlightGlobals</c> has an
        /// active vessel, and a terminal that raced it would hand the next step a flight
        /// with nothing in it. MAINMENU is the fast failure; the budget the catch-all.
        /// </summary>
        internal static LoadCompletionDecision DecideLaunchCompletion(
            double elapsedSeconds, TestCommandScene scene, bool gameLoaded, bool activeVesselReady,
            double budgetSeconds)
        {
            LoadCompletionDecision d = TestCommandLoadGame.DecideLoadCompletion(
                elapsedSeconds, scene, gameLoaded, budgetSeconds, TestCommandScene.Flight);
            if (d == LoadCompletionDecision.CompleteOk && !activeVesselReady)
                return elapsedSeconds >= budgetSeconds
                    ? LoadCompletionDecision.LoadTimeout
                    : LoadCompletionDecision.StillWaiting;
            return d;
        }

        // ---- log lines and payloads ----

        internal static string FormatGoStartLine(
            EditorRouteFacility facility, string craft, string scene, string craftPath)
            => "goeditor start facility=" + FacilityToken(facility)
               + " craft=" + Token(craft)
               + " scene=" + Token(scene)
               + " craftPath=" + Token(craftPath);

        internal static string FormatGoCompleteLine(
            EditorRouteFacility facility, string craft, string scene, int parts, string shipName,
            double elapsedSeconds)
            => "goeditor complete facility=" + FacilityToken(facility)
               + " craft=" + Token(craft)
               + " scene=" + Token(scene)
               + " parts=" + parts.ToString(CultureInfo.InvariantCulture)
               + " ship=" + Token(shipName)
               + " elapsed=" + elapsedSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s";

        internal static List<KeyValuePair<string, string>> BuildGoPayload(
            EditorRouteFacility facility, string craft, string scene, int parts, string shipName)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("scene", scene ?? string.Empty),
                new KeyValuePair<string, string>("facility", FacilityToken(facility)),
                new KeyValuePair<string, string>("craft", Token(craft)),
                new KeyValuePair<string, string>("parts", parts.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("ship", Token(shipName)),
            };

        internal static string FormatLaunchStartLine(
            string ship, int parts, string site, int crew, string scene)
            => "launchfromeditor start ship=" + Token(ship)
               + " parts=" + parts.ToString(CultureInfo.InvariantCulture)
               + " site=" + Token(site)
               + " crew=" + crew.ToString(CultureInfo.InvariantCulture)
               + " scene=" + Token(scene);

        internal static string FormatLaunchCompleteLine(
            string vessel, uint pid, string site, string situation, double elapsedSeconds)
            => "launchfromeditor complete scene=FLIGHT vessel=" + Token(vessel)
               + " pid=" + pid.ToString(CultureInfo.InvariantCulture)
               + " site=" + Token(site)
               + " situation=" + Token(situation)
               + " elapsed=" + elapsedSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s";

        internal static List<KeyValuePair<string, string>> BuildLaunchPayload(
            string vessel, uint pid, string site, string situation)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("scene", "FLIGHT"),
                new KeyValuePair<string, string>("vessel", Token(vessel)),
                new KeyValuePair<string, string>("pid", pid.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("site", Token(site)),
                new KeyValuePair<string, string>("situation", Token(situation)),
            };

        private static string Token(string s) => string.IsNullOrEmpty(s) ? "-" : s;
    }
}
