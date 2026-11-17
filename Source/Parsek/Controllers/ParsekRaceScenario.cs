using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The race mark's scenario module (design 6.2): a second, tiny scenario module whose node
    /// holds the <see cref="RaceLink"/> and the children later tasks own. It keeps the loaded node
    /// verbatim and writes it back verbatim, so keys and children this build does not know (a
    /// newer build's) survive every save, and a handler only ever rewrites its own child.
    ///
    /// <para>No code path in single player creates the module: stock instantiates it only from a
    /// <c>SCENARIO</c> proto already in <c>game.scenarios</c> (a race save gets its proto before
    /// it first starts, R1-10). Instance state only: nothing here survives the instance, so the
    /// Cold load roots do not change.</para>
    /// </summary>
    // The creation option must keep stock from adding this module to a game on its own
    // (GamePersistence.UpdateScenarioModules adds only for an AddToNew* / AddToExisting* option)
    // while keeping a proto the save already carries. It appears only here; RaceModeTests
    // .MarkSurvivesSaveLoad is the live proof.
    [KSPScenario(ScenarioCreationOptions.None,
        GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION, GameScenes.EDITOR)]
    public sealed class ParsekRaceScenario : ScenarioModule
    {
        internal const string Tag = "Race";

        private ConfigNode keptNode;
        private RaceLink link;
        private bool held;
        private List<IRaceScenarioChild> childHandlers = new List<IRaceScenarioChild>();

        /// <summary>The parsed race mark, or null when the mark is held verbatim (inert, newer, invalid).</summary>
        internal RaceLink Link => link;

        public override void OnAwake()
        {
            // The children of the race scenario node register here, one handler each with its own
            // LoadReconcilePolicy row (R3E-15 adds RACE_FACTS). Empty until then.
            childHandlers = new List<IRaceScenarioChild>();
        }

        public override void OnLoad(ConfigNode node)
        {
            keptNode = node != null ? node.CreateCopy() : null;
            link = null;
            if (ParsekGameModeGate.CheckInert("ParsekRaceScenario.OnLoad"))
            {
                held = true;
                LogHeld("inert");
                return;
            }
            string reason;
            if (!RaceLinkCodec.TryRead(keptNode, out link, out reason))
            {
                held = true;
                LogHeld(reason);
                return;
            }
            held = false;
            int handled = RaceScenarioChildren.DispatchLoad(keptNode, childHandlers);
            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "Mark loaded: raceId={0} playerId={1} slot={2} format={3} children={4} handled={5}",
                link.RaceId, link.PlayerId, link.SiteSlot, link.RaceLinkFormat,
                keptNode.nodes.Count, handled));
        }

        public override void OnSave(ConfigNode node)
        {
            int handlerWritten;
            int written = RaceScenarioChildren.WriteBack(
                keptNode, node, held ? null : childHandlers, out handlerWritten);
            ParsekLog.Verbose(Tag, string.Format(CultureInfo.InvariantCulture,
                "Mark saved: {0} entr{1} written back, {2} child(ren) by handler{3}",
                written, written == 1 ? "y" : "ies", handlerWritten,
                keptNode == null ? " (no node was loaded)" : held ? " (held verbatim)" : ""));
        }

        private void LogHeld(string reason)
        {
            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "Mark held verbatim: reason={0} values={1} nodes={2}",
                reason,
                keptNode != null ? keptNode.values.Count : 0,
                keptNode != null ? keptNode.nodes.Count : 0));
        }

        internal void AddChildHandlerForTesting(IRaceScenarioChild handler)
        {
            childHandlers.Add(handler);
        }
    }
}
