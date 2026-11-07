using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Parsek
{
    public partial class ParsekFlight
    {

        /// <summary>
        /// Creates the placed part's tree member one frame after the placement: in the
        /// placement frame the parts exist but have not started, and the snapshot and
        /// first pose are taken from the settled vessel.
        /// </summary>
        internal IEnumerator DeferredGroundPartPlacement(
            string treeId, string parentRecId, uint vesselPid, double placementUT,
            GroundPartPlacementSource source)
        {
            yield return null;

            if (activeTree == null || activeTree.Id != treeId)
            {
                ParsekLog.Info("Flight",
                    $"Ground part placement dropped: active tree changed before the deferred frame " +
                    $"(vesselPid={vesselPid} tree={treeId})");
                yield break;
            }
            Recording parentRec;
            if (!activeTree.Recordings.TryGetValue(parentRecId, out parentRec) || parentRec == null)
            {
                ParsekLog.Warn("Flight",
                    $"Ground part placement dropped: parent recording {parentRecId} left the tree " +
                    $"(vesselPid={vesselPid})");
                yield break;
            }
            Vessel v = FlightRecorder.FindVesselByPid(vesselPid);
            if (v == null || v.rootPart == null)
            {
                ParsekLog.Warn("Flight",
                    $"Ground part placement dropped: placed vessel pid={vesselPid} is gone before the deferred frame");
                yield break;
            }
            if (activeTree.BackgroundMap.ContainsKey(vesselPid))
            {
                ParsekLog.Verbose("Flight",
                    $"Ground part placement: vessel pid={vesselPid} already a tree member, skipping");
                yield break;
            }

            SurfaceCapture.CreateGroundPartMember(parentRec, v, placementUT, source);
        }
    }
}
