using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>A stock KSP screen the <c>StockScreen</c> verb drives.</summary>
    internal enum StockScreenKind
    {
        None = 0,
        /// <summary>The R&amp;D tech tree (<c>RnDBuilding</c>).</summary>
        RnD,
        /// <summary>The Astronaut Complex, from the Space Center building or, in the
        /// VAB/SPH, from the crew panel's Astronaut Complex button.</summary>
        Astronaut,
        /// <summary>Mission Control (<c>MissionControlBuilding</c>).</summary>
        MissionControl,
        /// <summary>Administration (<c>AdministrationFacility</c>).</summary>
        Administration,
        /// <summary>A KSC building's right-click context menu (<c>KSCFacilityContextMenu</c>).</summary>
        FacilityMenu,
        /// <summary>The launch pad's craft picker with its crew list (<c>VesselSpawnDialog</c>).</summary>
        LaunchSite,
        /// <summary>The VAB with a craft from the save's <c>Ships/VAB</c> loaded.</summary>
        Editor,
        /// <summary>The VAB/SPH crew panel (<c>CrewAssignmentDialog</c>).</summary>
        CrewDialog,
    }

    /// <summary>What one <c>StockScreen</c> call does to its screen.</summary>
    internal enum StockScreenAct
    {
        None = 0,
        Open,
        Close,
        /// <summary>Select a row the way a click on it does (a Mission Control contract, an
        /// Administration strategy, an R&amp;D node, a launch-site craft).</summary>
        Select,
        /// <summary>Hover a row or control so its stock tooltip draws: the OS cursor is moved
        /// onto it and the tooltip is spawned through stock's own <c>UIMasterController</c>.</summary>
        Hover,
    }

    /// <summary>One parsed <c>StockScreen</c> call.</summary>
    internal struct StockScreenRequest
    {
        internal StockScreenKind Screen;
        internal StockScreenAct Act;
        /// <summary>The row: a tech id, contract guid, strategy config name, facility id,
        /// craft name or kerbal name, depending on the screen. Null when absent.</summary>
        internal string Item;
        /// <summary>A part name (the runtime dot form) for a part-tooltip hover, or null.</summary>
        internal string Part;
        /// <summary>A Mission Control tab token, or null.</summary>
        internal string Pane;
    }

    /// <summary>What one settle poll of a <c>StockScreen</c> call concludes.</summary>
    internal enum StockScreenPollOutcome
    {
        NotYet,
        Ready,
        TimedOut,
    }

    /// <summary>
    /// Pure half of the <c>StockScreen</c> seam verb (automation-only, armed by
    /// <c>PARSEK_TEST_COMMANDS</c> like every seam verb): the GUI census's way onto the STOCK
    /// KSP screens that Parsek annotates (docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md).
    /// It opens a screen through stock's own entry point (the building's
    /// <c>EnterBuilding</c> / <c>OnRightClick</c>, <c>EditorDriver.StartAndLoadVessel</c>,
    /// <c>EditorLogic.SelectPanelCrew</c>), selects a row through the row's own radio
    /// button, and hovers a control by moving the OS cursor onto it and spawning its stock
    /// tooltip. It adds no rule of its own and changes no career state: no accept, hire,
    /// research, purchase or upgrade is ever pressed.
    ///
    /// <para>The vocabularies below are mirrored by <c>hlib.STOCKSCREEN_*</c>
    /// (<c>StockScreenSourceSyncTests</c> keeps them byte-equal).</para>
    /// </summary>
    internal static class TestCommandStockScreen
    {
        internal const string Verb = "StockScreen";

        internal const string ScreenArg = "screen";
        internal const string ActArg = "act";
        internal const string ItemArg = "item";
        internal const string PartArg = "part";
        internal const string PaneArg = "pane";

        /// <summary>Screen tokens, in <see cref="StockScreenKind"/> order.</summary>
        internal static readonly string[] ScreenTokens =
        {
            "rnd", "astronaut", "missioncontrol", "administration", "facilitymenu",
            "launchsite", "editor", "crewdialog",
        };

        /// <summary>Act tokens, in <see cref="StockScreenAct"/> order.</summary>
        internal static readonly string[] ActTokens = { "open", "close", "select", "hover" };

        /// <summary>Mission Control tabs, as <c>StockUiDecorationQuery</c> names them lower-cased.</summary>
        internal static readonly string[] PaneTokens = { "available", "active", "archive" };

        // ---- refusal reasons (hlib maps each to a driver-* subkind) ----
        internal const string ScreenArgMissingReason = "stockscreen-screen-arg-missing";
        internal const string ScreenArgInvalidReason = "stockscreen-screen-arg-invalid";
        internal const string ActArgMissingReason = "stockscreen-act-arg-missing";
        internal const string ActArgInvalidReason = "stockscreen-act-arg-invalid";
        internal const string ActUnsupportedReason = "stockscreen-act-unsupported";
        internal const string ItemArgMissingReason = "stockscreen-item-arg-missing";
        internal const string PaneArgInvalidReason = "stockscreen-pane-arg-invalid";
        internal const string PaneNotForScreenReason = "stockscreen-pane-not-for-screen";
        internal const string PartNotForScreenReason = "stockscreen-part-not-for-screen";
        internal const string WrongSceneReason = "stockscreen-wrong-scene";
        internal const string NotOpenReason = "stockscreen-not-open";
        internal const string AlreadyOpenReason = "stockscreen-already-open";
        internal const string EntryNotFoundReason = "stockscreen-entry-not-found";
        internal const string ItemNotFoundReason = "stockscreen-item-not-found";
        internal const string NoTooltipReason = "stockscreen-no-tooltip";
        internal const string CareerOnlyReason = "stockscreen-career-only";
        internal const string OpenFailedReason = "stockscreen-open-failed";
        internal const string NotSettledReason = "stockscreen-not-settled";

        /// <summary>Every refusal / error reason, for the hlib mirror.</summary>
        internal static readonly string[] Reasons =
        {
            ScreenArgMissingReason, ScreenArgInvalidReason, ActArgMissingReason, ActArgInvalidReason,
            ActUnsupportedReason, ItemArgMissingReason, PaneArgInvalidReason, PaneNotForScreenReason,
            PartNotForScreenReason, WrongSceneReason, NotOpenReason, AlreadyOpenReason,
            EntryNotFoundReason, ItemNotFoundReason, NoTooltipReason, CareerOnlyReason,
            OpenFailedReason, NotSettledReason,
        };

        /// <summary>Frames a settled screen must have drawn before the terminal: stock
        /// builds its rows over the frames after the spawn event and a capture that follows
        /// must see them.</summary>
        internal const int MinSettleFrames = 3;

        internal static string ScreenToken(StockScreenKind screen)
        {
            int i = (int)screen - 1;
            return i >= 0 && i < ScreenTokens.Length ? ScreenTokens[i] : "none";
        }

        internal static string ActToken(StockScreenAct act)
        {
            int i = (int)act - 1;
            return i >= 0 && i < ActTokens.Length ? ActTokens[i] : "none";
        }

        /// <summary>
        /// The (screen, act) pairs the verb implements. Everything else is a typed REJECTED.
        /// </summary>
        internal static bool Supports(StockScreenKind screen, StockScreenAct act)
        {
            switch (screen)
            {
                case StockScreenKind.RnD:
                    return act == StockScreenAct.Open || act == StockScreenAct.Close
                        || act == StockScreenAct.Select || act == StockScreenAct.Hover;
                case StockScreenKind.Astronaut:
                    return act == StockScreenAct.Open || act == StockScreenAct.Close || act == StockScreenAct.Hover;
                case StockScreenKind.MissionControl:
                    return act == StockScreenAct.Open || act == StockScreenAct.Close || act == StockScreenAct.Select;
                case StockScreenKind.Administration:
                    return act == StockScreenAct.Open || act == StockScreenAct.Close || act == StockScreenAct.Select;
                case StockScreenKind.FacilityMenu:
                    return act == StockScreenAct.Open || act == StockScreenAct.Close || act == StockScreenAct.Hover;
                case StockScreenKind.LaunchSite:
                    return act == StockScreenAct.Open || act == StockScreenAct.Close
                        || act == StockScreenAct.Select || act == StockScreenAct.Hover;
                case StockScreenKind.Editor:
                    return act == StockScreenAct.Open || act == StockScreenAct.Close || act == StockScreenAct.Hover;
                case StockScreenKind.CrewDialog:
                    return act == StockScreenAct.Open || act == StockScreenAct.Hover;
                default:
                    return false;
            }
        }

        /// <summary>
        /// True when the call needs <c>item=</c>: every select; every hover except a part
        /// hover (which names its part through <c>part=</c>) and the facility menu (which has
        /// one hover target, Upgrade); opening a facility menu (which building) and the editor
        /// (which craft).
        /// </summary>
        internal static bool NeedsItem(StockScreenKind screen, StockScreenAct act, bool hasPart)
        {
            if (act == StockScreenAct.Select) return true;
            if (act == StockScreenAct.Hover)
                return !hasPart && screen != StockScreenKind.FacilityMenu;
            if (act == StockScreenAct.Open)
                return screen == StockScreenKind.FacilityMenu || screen == StockScreenKind.Editor;
            return false;
        }

        /// <summary><c>part=</c> is read only by a hover on R&amp;D (its node panel's part list)
        /// or the editor (its part list).</summary>
        internal static bool AcceptsPart(StockScreenKind screen, StockScreenAct act)
        {
            return act == StockScreenAct.Hover
                && (screen == StockScreenKind.RnD || screen == StockScreenKind.Editor);
        }

        /// <summary><c>pane=</c> is read only by Mission Control's open and select.</summary>
        internal static bool AcceptsPane(StockScreenKind screen, StockScreenAct act)
        {
            return screen == StockScreenKind.MissionControl
                && (act == StockScreenAct.Open || act == StockScreenAct.Select);
        }

        /// <summary>
        /// The scene a call must run in. The Astronaut Complex and a close are valid in both
        /// the Space Center and the editor; the crew panel, a part hover and the editor's
        /// own close only in the editor; everything else only at the Space Center.
        /// </summary>
        internal static bool IsValidScene(StockScreenRequest r, TestCommandScene scene)
        {
            bool ksc = scene == TestCommandScene.SpaceCenter;
            bool editor = scene == TestCommandScene.Editor;
            switch (r.Screen)
            {
                case StockScreenKind.Astronaut:
                    return ksc || editor;
                case StockScreenKind.CrewDialog:
                    return editor;
                case StockScreenKind.Editor:
                    return r.Act == StockScreenAct.Open ? ksc : editor;
                default:
                    return ksc;
            }
        }

        /// <summary>Parses the args. On failure <paramref name="rejectReason"/> is the
        /// typed reason and <paramref name="detail"/> names the offending value.</summary>
        internal static bool TryParse(
            string rawScreen, string rawAct, string rawItem, string rawPart, string rawPane,
            out StockScreenRequest request, out string rejectReason, out string detail)
        {
            request = new StockScreenRequest();
            rejectReason = null;
            detail = null;

            if (string.IsNullOrEmpty(rawScreen))
            {
                rejectReason = ScreenArgMissingReason;
                detail = "valid=" + string.Join(",", ScreenTokens);
                return false;
            }
            int s = Array.IndexOf(ScreenTokens, rawScreen);
            if (s < 0)
            {
                rejectReason = ScreenArgInvalidReason;
                detail = "screen=" + rawScreen + " valid=" + string.Join(",", ScreenTokens);
                return false;
            }
            if (string.IsNullOrEmpty(rawAct))
            {
                rejectReason = ActArgMissingReason;
                detail = "valid=" + string.Join(",", ActTokens);
                return false;
            }
            int a = Array.IndexOf(ActTokens, rawAct);
            if (a < 0)
            {
                rejectReason = ActArgInvalidReason;
                detail = "act=" + rawAct + " valid=" + string.Join(",", ActTokens);
                return false;
            }
            request.Screen = (StockScreenKind)(s + 1);
            request.Act = (StockScreenAct)(a + 1);
            if (!Supports(request.Screen, request.Act))
            {
                rejectReason = ActUnsupportedReason;
                detail = "screen=" + rawScreen + " act=" + rawAct;
                return false;
            }

            request.Item = string.IsNullOrEmpty(rawItem) ? null : rawItem;
            request.Part = string.IsNullOrEmpty(rawPart) ? null : rawPart;
            if (request.Part != null && !AcceptsPart(request.Screen, request.Act))
            {
                rejectReason = PartNotForScreenReason;
                detail = "screen=" + rawScreen + " act=" + rawAct;
                return false;
            }
            if (!string.IsNullOrEmpty(rawPane))
            {
                if (!AcceptsPane(request.Screen, request.Act))
                {
                    rejectReason = PaneNotForScreenReason;
                    detail = "screen=" + rawScreen + " act=" + rawAct;
                    return false;
                }
                if (Array.IndexOf(PaneTokens, rawPane) < 0)
                {
                    rejectReason = PaneArgInvalidReason;
                    detail = "pane=" + rawPane + " valid=" + string.Join(",", PaneTokens);
                    return false;
                }
                request.Pane = rawPane;
            }
            if (request.Item == null && NeedsItem(request.Screen, request.Act, request.Part != null))
            {
                rejectReason = ItemArgMissingReason;
                detail = "screen=" + rawScreen + " act=" + rawAct;
                return false;
            }
            return true;
        }

        /// <summary>
        /// One settle poll: ready once the screen's own readiness signal holds AND at least
        /// <see cref="MinSettleFrames"/> frames have drawn since the call; timed out once the
        /// verb's budget is spent first.
        /// </summary>
        internal static StockScreenPollOutcome DecidePoll(bool ready, int framesElapsed, bool budgetExpired)
        {
            if (ready && framesElapsed >= MinSettleFrames) return StockScreenPollOutcome.Ready;
            return budgetExpired ? StockScreenPollOutcome.TimedOut : StockScreenPollOutcome.NotYet;
        }

        internal static List<KeyValuePair<string, string>> BuildPayload(
            StockScreenRequest r, string scene, string detailKey, string detailValue, int frames)
        {
            var payload = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("screen", ScreenToken(r.Screen)),
                new KeyValuePair<string, string>("act", ActToken(r.Act)),
                new KeyValuePair<string, string>("item", Token(r.Item)),
                new KeyValuePair<string, string>("part", Token(r.Part)),
                new KeyValuePair<string, string>("pane", Token(r.Pane)),
                new KeyValuePair<string, string>("scene", scene ?? "-"),
                new KeyValuePair<string, string>("frames", frames.ToString(CultureInfo.InvariantCulture)),
            };
            if (!string.IsNullOrEmpty(detailKey))
                payload.Add(new KeyValuePair<string, string>(detailKey, Token(detailValue)));
            return payload;
        }

        /// <summary>A payload / log token: spaces replaced so a value stays one field.</summary>
        internal static string Token(string value)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            return value.Replace(' ', '_');
        }

        /// <summary>The one Info line every call writes before it acts.</summary>
        internal static string FormatStartLine(StockScreenRequest r, string scene)
        {
            return "stockscreen start screen=" + ScreenToken(r.Screen)
                + " act=" + ActToken(r.Act)
                + " item=" + Token(r.Item)
                + " part=" + Token(r.Part)
                + " pane=" + Token(r.Pane)
                + " scene=" + (scene ?? "-");
        }

        /// <summary>The Info line an OK terminal writes.</summary>
        internal static string FormatOkLine(StockScreenRequest r, string scene, string detailKey, string detailValue, int frames)
        {
            return "stockscreen ok screen=" + ScreenToken(r.Screen)
                + " act=" + ActToken(r.Act)
                + " item=" + Token(r.Item)
                + " part=" + Token(r.Part)
                + " pane=" + Token(r.Pane)
                + " scene=" + (scene ?? "-")
                + " frames=" + frames.ToString(CultureInfo.InvariantCulture)
                + (string.IsNullOrEmpty(detailKey) ? "" : " " + detailKey + "=" + Token(detailValue));
        }
    }
}
