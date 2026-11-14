using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Where a shared part-state poll writes: the event list, the log tag, and the wording of
    /// each log line that differs between the active-vessel recorder and the background
    /// recorder. <see cref="FlightRecorder"/> and <see cref="BackgroundRecorder"/> each own one;
    /// the loops in <see cref="PartStatePoller"/> never name either recorder's state type.
    /// </summary>
    internal abstract class PartPollSink
    {
        /// <summary>The <c>ParsekLog</c> subsystem tag for every line the poll writes.</summary>
        internal abstract string Tag { get; }

        /// <summary>The list a new part event is appended to (read at each append).</summary>
        internal abstract List<PartEvent> Events { get; }

        /// <summary>Text after <c>pid=N</c> on a plain part-event line.</summary>
        internal abstract string EventSuffix();
    }

    /// <summary>
    /// The per-part polling loops both recorders share. Each method walks a vessel's parts,
    /// classifies one module family, runs the family's transition helper against the caller's
    /// per-vessel tracking containers, and appends any event to the sink. Sampling policy
    /// (when and whether a vessel is polled) stays with the recorders.
    /// </summary>
    internal static class PartStatePoller
    {
        internal static void PollGear(Vessel v, HashSet<uint> deployedGear, double ut, PartPollSink sink)
        {
            if (v == null || v.parts == null) return;

            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null) continue;

                for (int m = 0; m < p.Modules.Count; m++)
                {
                    var wheel = p.Modules[m] as ModuleWheels.ModuleWheelDeployment;
                    if (wheel == null) continue;

                    FlightRecorder.ClassifyGearState(wheel.stateString, out bool isDeployed, out bool isRetracted);

                    // Skip transitional states ("Deploying"/"Retracting"); only fire on endpoints
                    if (!isDeployed && !isRetracted) continue;

                    var evt = FlightRecorder.CheckGearTransition(
                        p.persistentId, p.partInfo?.name ?? "unknown", isDeployed, deployedGear, ut);
                    if (evt.HasValue)
                    {
                        sink.Events.Add(evt.Value);
                        ParsekLog.Verbose(sink.Tag, $"Part event: {evt.Value.eventType} '{evt.Value.partName}' " +
                            $"pid={evt.Value.partPersistentId}{sink.EventSuffix()}");
                    }
                    break; // one deployment module per part
                }
            }
        }
    }
}
