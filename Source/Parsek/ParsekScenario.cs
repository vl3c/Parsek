using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// ScenarioModule that persists committed recordings to save games.
    /// Handles OnSave/OnLoad to serialize trajectory data into ConfigNodes.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames,
        GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION)]
    public class ParsekScenario : ScenarioModule
    {
        public override void OnSave(ConfigNode node)
        {
            // Clear any existing recording nodes
            node.RemoveNodes("RECORDING");

            var recordings = RecordingStore.CommittedRecordings;
            Debug.Log($"[Parsek Scenario] Saving {recordings.Count} committed recordings");

            for (int r = 0; r < recordings.Count; r++)
            {
                var rec = recordings[r];
                ConfigNode recNode = node.AddNode("RECORDING");
                recNode.AddValue("vesselName", rec.VesselName);
                recNode.AddValue("pointCount", rec.Points.Count);

                for (int i = 0; i < rec.Points.Count; i++)
                {
                    var pt = rec.Points[i];
                    ConfigNode ptNode = recNode.AddNode("POINT");
                    ptNode.AddValue("ut", pt.ut.ToString("R"));
                    ptNode.AddValue("lat", pt.latitude.ToString("R"));
                    ptNode.AddValue("lon", pt.longitude.ToString("R"));
                    ptNode.AddValue("alt", pt.altitude.ToString("R"));
                    ptNode.AddValue("rotX", pt.rotation.x.ToString("R"));
                    ptNode.AddValue("rotY", pt.rotation.y.ToString("R"));
                    ptNode.AddValue("rotZ", pt.rotation.z.ToString("R"));
                    ptNode.AddValue("rotW", pt.rotation.w.ToString("R"));
                    ptNode.AddValue("body", pt.bodyName);
                    ptNode.AddValue("funds", pt.funds.ToString("R"));
                    ptNode.AddValue("science", pt.science.ToString("R"));
                    ptNode.AddValue("rep", pt.reputation.ToString("R"));
                }
            }
        }

        public override void OnLoad(ConfigNode node)
        {
            var recordings = RecordingStore.CommittedRecordings;
            recordings.Clear();

            ConfigNode[] recNodes = node.GetNodes("RECORDING");
            Debug.Log($"[Parsek Scenario] Loading {recNodes.Length} committed recordings");

            for (int r = 0; r < recNodes.Length; r++)
            {
                var recNode = recNodes[r];
                var rec = new RecordingStore.Recording
                {
                    VesselName = recNode.GetValue("vesselName") ?? "Unknown"
                };

                ConfigNode[] ptNodes = recNode.GetNodes("POINT");
                for (int i = 0; i < ptNodes.Length; i++)
                {
                    var ptNode = ptNodes[i];
                    var pt = new ParsekSpike.TrajectoryPoint();

                    double.TryParse(ptNode.GetValue("ut"), out pt.ut);
                    double.TryParse(ptNode.GetValue("lat"), out pt.latitude);
                    double.TryParse(ptNode.GetValue("lon"), out pt.longitude);
                    double.TryParse(ptNode.GetValue("alt"), out pt.altitude);

                    float rx, ry, rz, rw;
                    float.TryParse(ptNode.GetValue("rotX"), out rx);
                    float.TryParse(ptNode.GetValue("rotY"), out ry);
                    float.TryParse(ptNode.GetValue("rotZ"), out rz);
                    float.TryParse(ptNode.GetValue("rotW"), out rw);
                    pt.rotation = new Quaternion(rx, ry, rz, rw);

                    pt.bodyName = ptNode.GetValue("body") ?? "Kerbin";

                    double funds;
                    double.TryParse(ptNode.GetValue("funds"), out funds);
                    pt.funds = funds;

                    float science, rep;
                    float.TryParse(ptNode.GetValue("science"), out science);
                    float.TryParse(ptNode.GetValue("rep"), out rep);
                    pt.science = science;
                    pt.reputation = rep;

                    rec.Points.Add(pt);
                }

                if (rec.Points.Count > 0)
                {
                    recordings.Add(rec);
                    Debug.Log($"[Parsek Scenario] Loaded recording: {rec.VesselName}, " +
                        $"{rec.Points.Count} points, UT {rec.StartUT:F0}-{rec.EndUT:F0}");
                }
            }
        }
    }
}
