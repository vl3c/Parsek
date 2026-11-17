namespace Parsek.InGameTests.Spikes.S6
{
    // R0 spike branch only (spike/mp-r0, never merged). Plan: r0-spikes.md, task S6 step (c).
    //
    // The installed twin of the race mark. The mark's module type is deliberately missing (the
    // "older build" case), so it can only show that its node survives; this module is added to
    // the created game the same way (a ProtoScenarioModule in game.scenarios before
    // Game.Start) and shows whether such a proto is instantiated in its target scenes.
    // ScenarioCreationOptions.None: KSP never adds it to a game by itself, so an unarmed DLL
    // meets it only in a save the armed spike created; it then keeps its node verbatim and
    // logs nothing.
    [KSPScenario(ScenarioCreationOptions.None, GameScenes.SPACECENTER, GameScenes.EDITOR, GameScenes.FLIGHT, GameScenes.TRACKSTATION)]
    public class SpikeS6ProbeScenario : ScenarioModule
    {
        private ConfigNode kept;

        public override void OnLoad(ConfigNode node)
        {
            kept = node != null ? node.CreateCopy() : null;
            if (SpikeHost.IsArmed(SpikeS6.Id))
                SpikeS6.Info("probe OnLoad scene=" + HighLogic.LoadedScene + " marker="
                    + (node != null ? node.GetValue("marker") ?? "<none>" : "<null>"));
        }

        public override void OnSave(ConfigNode node)
        {
            if (kept == null || node == null) return;
            foreach (ConfigNode.Value v in kept.values)
                if (v.name != "name" && v.name != "scene" && !node.HasValue(v.name)) node.AddValue(v.name, v.value);
            foreach (ConfigNode child in kept.nodes) node.AddNode(child.CreateCopy());
        }
    }
}
