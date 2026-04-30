namespace Parsek
{
    /// <summary>
    /// Classifies recording events to determine whether they require ghost visual updates
    /// (re-ghosting). Used by the chain walker to decide which recordings need ghost rebuilds
    /// when walking a recording tree.
    /// </summary>
    internal static class GhostingTriggerClassifier
    {
        /// <summary>
        /// Returns true if the given part event type changes vessel geometry or visual state
        /// enough to require a ghost rebuild. Returns false only for purely cosmetic events
        /// (lights, thermal animations) that do not affect ghost structure.
        /// Conservative: unknown future event types return true.
        /// </summary>
        internal static bool IsGhostingTrigger(PartEventType type)
        {
            switch (type)
            {
                // Cosmetic-only events — no ghost rebuild needed
                case PartEventType.LightOn:
                case PartEventType.LightOff:
                case PartEventType.LightBlinkEnabled:
                case PartEventType.LightBlinkDisabled:
                case PartEventType.LightBlinkRate:
                case PartEventType.ThermalAnimationHot:
                case PartEventType.ThermalAnimationCold:
                case PartEventType.ThermalAnimationMedium:
                    return false;

                // All structural / mechanical / deployment events — trigger ghost rebuild
                case PartEventType.Decoupled:
                case PartEventType.Destroyed:
                case PartEventType.ParachuteDeployed:
                case PartEventType.ParachuteCut:
                case PartEventType.ShroudJettisoned:
                case PartEventType.EngineIgnited:
                case PartEventType.EngineShutdown:
                case PartEventType.EngineThrottle:
                case PartEventType.ParachuteDestroyed:
                case PartEventType.DeployableExtended:
                case PartEventType.DeployableRetracted:
                case PartEventType.GearDeployed:
                case PartEventType.GearRetracted:
                case PartEventType.CargoBayOpened:
                case PartEventType.CargoBayClosed:
                case PartEventType.FairingJettisoned:
                case PartEventType.RCSActivated:
                case PartEventType.RCSStopped:
                case PartEventType.RCSThrottle:
                case PartEventType.Docked:
                case PartEventType.Undocked:
                case PartEventType.InventoryPartPlaced:
                case PartEventType.InventoryPartRemoved:
                case PartEventType.RoboticMotionStarted:
                case PartEventType.RoboticPositionSample:
                case PartEventType.RoboticMotionStopped:
                case PartEventType.ParachuteSemiDeployed:
                    return true;

                default:
                    // Conservative: unknown future event types are treated as ghosting triggers
                    ParsekLog.Verbose("ChainWalker",
                        $"IsGhostingTrigger: unknown PartEventType {type} ({(int)type}), treating as trigger");
                    return true;
            }
        }

        /// <summary>
        /// Returns true if the given branch point type represents a vessel-claiming event
        /// (one where parts transfer between vessels, requiring ghost updates on both sides).
        /// Returns false for boundary-only events (launch, terminal, breakup) that don't
        /// transfer parts.
        /// Conservative: unknown future types return false (non-claiming by default).
        /// </summary>
        internal static bool IsClaimingBranchPoint(BranchPointType type)
        {
            switch (type)
            {
                // Claiming events — parts transfer between vessels
                case BranchPointType.Dock:
                case BranchPointType.Board:
                case BranchPointType.Undock:
                case BranchPointType.EVA:
                case BranchPointType.JointBreak:
                    return true;

                // Non-claiming events — boundary markers only
                case BranchPointType.Launch:
                case BranchPointType.Terminal:
                case BranchPointType.Breakup:
                    return false;

                default:
                    ParsekLog.Verbose("ChainWalker",
                        $"IsClaimingBranchPoint: unknown BranchPointType {(int)type}, returning false");
                    return false;
            }
        }

        /// <summary>
        /// Returns true if the given segment event type changes vessel structure enough
        /// to require a ghost rebuild. Returns false for crew/controller state changes
        /// that don't affect visual geometry.
        /// Conservative: unknown future types return true.
        /// </summary>
        internal static bool IsGhostingSegmentEvent(SegmentEventType type)
        {
            switch (type)
            {
                // Structural changes — require ghost rebuild
                case SegmentEventType.PartDestroyed:
                case SegmentEventType.PartRemoved:
                case SegmentEventType.PartAdded:
                    return true;

                // Non-structural state changes — no ghost rebuild
                case SegmentEventType.ControllerChange:
                case SegmentEventType.ControllerDisabled:
                case SegmentEventType.ControllerEnabled:
                case SegmentEventType.CrewLost:
                case SegmentEventType.CrewTransfer:
                case SegmentEventType.TimeJump:  // recording discontinuity marker, not a physical state change
                    return false;

                default:
                    // Conservative: unknown future segment event types are treated as triggers
                    ParsekLog.Verbose("ChainWalker",
                        $"IsGhostingSegmentEvent: unknown SegmentEventType {type} ({(int)type}), treating as trigger");
                    return true;
            }
        }

        /// <summary>
        /// Scans a recording's part events and segment events for any that would trigger
        /// a ghost rebuild. Returns true if at least one ghosting trigger is found.
        /// Handles null event lists gracefully.
        /// </summary>
        internal static bool HasGhostingTriggerEvents(Recording rec)
        {
            bool result = false;
            int partCount = 0;
            int segCount = 0;

            if (rec.PartEvents != null)
            {
                partCount = rec.PartEvents.Count;
                for (int i = 0; i < rec.PartEvents.Count; i++)
                {
                    if (IsGhostingTrigger(rec.PartEvents[i].eventType))
                    {
                        result = true;
                        break;
                    }
                }
            }

            if (!result && rec.SegmentEvents != null)
            {
                segCount = rec.SegmentEvents.Count;
                for (int i = 0; i < rec.SegmentEvents.Count; i++)
                {
                    if (IsGhostingSegmentEvent(rec.SegmentEvents[i].type))
                    {
                        result = true;
                        break;
                    }
                }
            }
            else if (rec.SegmentEvents != null)
            {
                segCount = rec.SegmentEvents.Count;
            }

            ParsekLog.VerboseOnChange("ChainWalker",
                identity: $"trigger|{rec.RecordingId}",
                stateKey: $"{result}|{partCount}|{segCount}",
                message: $"HasGhostingTriggerEvents: rec={rec.RecordingId} found={result} (scanned {partCount} part events, {segCount} segment events)");

            return result;
        }
    }
}
