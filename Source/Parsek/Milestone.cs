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
        public List<GameStateEvent> Events = new List<GameStateEvent>();
        /// <summary>
        /// GAME_STATE_EVENT children whose type this build cannot read
        /// (<see cref="GameStateEvent.IsUnreadableTypeNode"/>), kept verbatim and written back after
        /// <see cref="Events"/>. They are not in <see cref="Events"/>, so no consumer sees them, and
        /// <see cref="LastReplayedEventIndex"/> counts readable events only.
        /// </summary>
        public List<ConfigNode> UnreadableEventNodes = new List<ConfigNode>();
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
            for (int i = 0; i < UnreadableEventNodes.Count; i++)
                node.AddNode(UnreadableEventNodes[i].CreateCopy());
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
                {
                    if (GameStateEvent.IsUnreadableTypeNode(eventNodes[i]))
                        m.UnreadableEventNodes.Add(eventNodes[i].CreateCopy());
                    else
                        m.Events.Add(GameStateEvent.DeserializeFrom(eventNodes[i]));
                }
            }

            return m;
        }
    }
}
