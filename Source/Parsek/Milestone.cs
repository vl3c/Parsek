using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal class Milestone
    {
        public string MilestoneId;
        public double StartUT;
        public double EndUT;
        public string RecordingId;
        public uint Epoch;
        public List<GameStateEvent> Events = new List<GameStateEvent>();
        public bool Committed;
        public int LastReplayedEventIndex;

        public void SerializeInto(ConfigNode node)
        {
            var ic = CultureInfo.InvariantCulture;
            node.AddValue("id", MilestoneId ?? "");
            node.AddValue("startUT", StartUT.ToString("R", ic));
            node.AddValue("endUT", EndUT.ToString("R", ic));
            node.AddValue("recordingId", RecordingId ?? "");
            node.AddValue("committed", Committed.ToString());
            node.AddValue("lastReplayedIdx", LastReplayedEventIndex.ToString(ic));

            for (int i = 0; i < Events.Count; i++)
            {
                ConfigNode eventNode = node.AddNode("GAME_STATE_EVENT");
                Events[i].SerializeInto(eventNode);
            }
        }

        public static Milestone DeserializeFrom(ConfigNode node)
        {
            var ic = CultureInfo.InvariantCulture;
            var m = new Milestone();

            m.MilestoneId = node.GetValue("id") ?? "";

            string startStr = node.GetValue("startUT");
            if (startStr != null)
                double.TryParse(startStr, NumberStyles.Float, ic, out m.StartUT);

            string endStr = node.GetValue("endUT");
            if (endStr != null)
                double.TryParse(endStr, NumberStyles.Float, ic, out m.EndUT);

            m.RecordingId = node.GetValue("recordingId") ?? "";

            string epochStr = node.GetValue("epoch");
            if (epochStr != null)
                uint.TryParse(epochStr, NumberStyles.Integer, ic, out m.Epoch);

            string committedStr = node.GetValue("committed");
            if (committedStr != null)
                bool.TryParse(committedStr, out m.Committed);

            string replayIdxStr = node.GetValue("lastReplayedIdx");
            if (replayIdxStr != null)
                int.TryParse(replayIdxStr, NumberStyles.Integer, ic, out m.LastReplayedEventIndex);

            ConfigNode[] eventNodes = node.GetNodes("GAME_STATE_EVENT");
            if (eventNodes != null)
            {
                for (int i = 0; i < eventNodes.Length; i++)
                    m.Events.Add(GameStateEvent.DeserializeFrom(eventNodes[i]));
            }

            GameStateEvent.NormalizeLegacyPartPurchaseCostsForLoad(
                m.Events, $"milestone:{m.MilestoneId ?? "(unknown)"}");

            return m;
        }
    }
}
