using System;
using System.Collections.Generic;
using Upgradeables;

namespace Parsek
{
    internal enum ReplayDecision
    {
        Skip,
        Act,
        Fail
    }

    internal static class ActionReplay
    {
        /// <summary>
        /// Returns true if the given event type is one that ActionReplay can replay.
        /// </summary>
        internal static bool IsReplayableEvent(GameStateEventType type)
        {
            switch (type)
            {
                case GameStateEventType.TechResearched:
                case GameStateEventType.PartPurchased:
                case GameStateEventType.FacilityUpgraded:
                case GameStateEventType.CrewHired:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Iterates committed milestones and replays any unreplayed replayable events.
        /// Sets suppression flags around the replay loop to prevent recording replayed
        /// actions as new game state events and to bypass blocking Harmony patches.
        /// </summary>
        /// <param name="milestones">Milestones to replay. Events within each milestone
        /// must be sorted by UT (guaranteed by MilestoneStore.CreateMilestone).</param>
        /// <param name="maxUT">Only replay events with ut &lt;= maxUT. Events exactly at
        /// maxUT ARE included (uses strict greater-than comparison for the cutoff).</param>
        internal static void ReplayCommittedActions(IReadOnlyList<Milestone> milestones, double maxUT = double.MaxValue)
        {
            if (milestones == null || milestones.Count == 0) return;

            // Count unreplayed replayable events across all committed milestones
            int totalActions = 0;
            int milestoneCount = 0;
            for (int i = 0; i < milestones.Count; i++)
            {
                var m = milestones[i];
                if (!m.Committed) continue;

                int unreplayed = 0;
                for (int j = m.LastReplayedEventIndex + 1; j < m.Events.Count; j++)
                {
                    if (m.Events[j].ut > maxUT) break;
                    if (IsReplayableEvent(m.Events[j].eventType))
                        unreplayed++;
                }

                if (unreplayed > 0)
                {
                    totalActions += unreplayed;
                    milestoneCount++;
                }
            }

            if (totalActions == 0) return;

            string utNote = maxUT < double.MaxValue ? $" (maxUT={maxUT:F0})" : "";
            ParsekLog.Info("ActionReplay",
                $"Replaying {totalActions} unreplayed actions from {milestoneCount} milestones{utNote}");

            int techCount = 0;
            int partCount = 0;
            int facilityCount = 0;
            int crewCount = 0;
            int skipCount = 0;
            int failCount = 0;

            GameStateRecorder.IsReplayingActions = true;
            GameStateRecorder.SuppressCrewEvents = true;
            try
            {
                for (int i = 0; i < milestones.Count; i++)
                {
                    var m = milestones[i];
                    if (!m.Committed) continue;

                    int newLastReplayed = m.LastReplayedEventIndex;
                    for (int j = m.LastReplayedEventIndex + 1; j < m.Events.Count; j++)
                    {
                        var evt = m.Events[j];
                        if (evt.ut > maxUT) break;
                        newLastReplayed = j;
                        if (!IsReplayableEvent(evt.eventType)) continue;

                        switch (evt.eventType)
                        {
                            case GameStateEventType.TechResearched:
                                AccumulateReplayResult(ReplayTechUnlock(evt, out var techSkip), techSkip,
                                    ref techCount, ref skipCount, ref failCount);
                                break;
                            case GameStateEventType.PartPurchased:
                                AccumulateReplayResult(ReplayPartPurchase(evt, out var partSkip), partSkip,
                                    ref partCount, ref skipCount, ref failCount);
                                break;
                            case GameStateEventType.FacilityUpgraded:
                                AccumulateReplayResult(ReplayFacilityUpgrade(evt, out var facSkip), facSkip,
                                    ref facilityCount, ref skipCount, ref failCount);
                                break;
                            case GameStateEventType.CrewHired:
                                AccumulateReplayResult(ReplayCrewHire(evt, out var crewSkip), crewSkip,
                                    ref crewCount, ref skipCount, ref failCount);
                                break;
                        }
                    }

                    // Mark events up to maxUT as replayed
                    m.LastReplayedEventIndex = newLastReplayed;
                }
            }
            finally
            {
                GameStateRecorder.IsReplayingActions = false;
                GameStateRecorder.SuppressCrewEvents = false;
            }

            ParsekLog.Info("ActionReplay",
                $"Replay complete: {techCount} tech, {partCount} parts, {facilityCount} facilities, {crewCount} crew ({skipCount} skipped, {failCount} failed)");
        }

        /// <summary>
        /// Unlocks a tech node programmatically WITHOUT deducting science.
        /// Uses ProtoTechNode + UnlockProtoTechNode to bypass RDTech.UnlockTech
        /// which would deduct science (already handled by budget deduction).
        /// </summary>
        internal static bool ReplayTechUnlock(GameStateEvent e, out bool skipped)
        {
            skipped = false;
            string techId = e.key;

            var decision = DecideTechReplay(techId,
                ResearchAndDevelopment.Instance != null
                && ResearchAndDevelopment.GetTechnologyState(techId) == RDTech.State.Available);

            if (decision == ReplayDecision.Skip)
            {
                skipped = true;
                ParsekLog.Info("ActionReplay",
                    $"Tech unlock: '{techId}' — already researched, skipping");
                return true;
            }

            if (decision == ReplayDecision.Fail)
            {
                ParsekLog.Warn("ActionReplay", "Tech unlock: empty techId — FAILED");
                return false;
            }

            if (ResearchAndDevelopment.Instance == null)
            {
                ParsekLog.Warn("ActionReplay",
                    $"Tech unlock: '{techId}' — R&D instance null, FAILED");
                return false;
            }

            try
            {
                var protoNode = new ProtoTechNode
                {
                    techID = techId,
                    state = RDTech.State.Available,
                    partsPurchased = new List<AvailablePart>()
                };

                string partsStr = GameStateEventDisplay.ExtractDetailField(e.detail, "parts");
                if (!string.IsNullOrEmpty(partsStr))
                {
                    var partNames = partsStr.Split(',');
                    for (int i = 0; i < partNames.Length; i++)
                    {
                        string partName = partNames[i].Trim();
                        if (string.IsNullOrEmpty(partName)) continue;
                        var partInfo = PartLoader.getPartInfoByName(partName);
                        if (partInfo != null)
                            protoNode.partsPurchased.Add(partInfo);
                        else
                            ParsekLog.Verbose("ActionReplay",
                                $"Tech unlock: part '{partName}' not found in PartLoader — skipped");
                    }
                }

                ResearchAndDevelopment.Instance.UnlockProtoTechNode(protoNode);
                ParsekLog.Info("ActionReplay",
                    $"Tech unlock: '{techId}' — success ({protoNode.partsPurchased.Count} parts)");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("ActionReplay",
                    $"Tech unlock: '{techId}' — FAILED: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Pure decision logic for tech replay — testable without KSP runtime.
        /// </summary>
        internal static ReplayDecision DecideTechReplay(string techId, bool isAlreadyResearched)
        {
            if (string.IsNullOrEmpty(techId)) return ReplayDecision.Fail;
            if (isAlreadyResearched) return ReplayDecision.Skip;
            return ReplayDecision.Act;
        }

        #region Phase 2: Part Purchase

        /// <summary>
        /// Purchases a part programmatically. In most career games, parts auto-unlock
        /// with their tech node. For "Entry Purchase Required" mode, this makes the
        /// part available without additional fund deduction.
        /// </summary>
        internal static bool ReplayPartPurchase(GameStateEvent e, out bool skipped)
        {
            skipped = false;
            string partName = e.key;

            if (string.IsNullOrEmpty(partName))
            {
                ParsekLog.Warn("ActionReplay", "Part purchase: empty partName — FAILED");
                return false;
            }

            try
            {
                var ap = PartLoader.getPartInfoByName(partName);
                bool partExists = ap != null;
                bool isAlreadyPurchased = partExists
                    && ResearchAndDevelopment.Instance != null
                    && ResearchAndDevelopment.PartModelPurchased(ap);

                var decision = DecidePartReplay(partName, partExists, isAlreadyPurchased);

                if (decision == ReplayDecision.Skip)
                {
                    skipped = true;
                    if (!partExists)
                        ParsekLog.Warn("ActionReplay",
                            $"Part purchase: '{partName}' — part not found (PartLoader), skipping");
                    else
                        ParsekLog.Info("ActionReplay",
                            $"Part purchase: '{partName}' — already purchased, skipping");
                    return true;
                }

                if (ResearchAndDevelopment.Instance != null
                    && ResearchAndDevelopment.IsExperimentalPart(ap))
                {
                    // Part is in experimental list (unlocked but not purchased) — remove
                    // from experimental to mark as fully purchased
                    ResearchAndDevelopment.RemoveExperimentalPart(ap);
                    ParsekLog.Info("ActionReplay",
                        $"Part purchase: '{partName}' — success (removed from experimental)");
                    return true;
                }

                // Part tech may not be researched yet (will be after tech replay).
                // If the part isn't in any category (experimental or purchased), try to
                // add it as experimental — this makes it available in the editor.
                if (ResearchAndDevelopment.Instance != null)
                {
                    ResearchAndDevelopment.AddExperimentalPart(ap);
                    ParsekLog.Info("ActionReplay",
                        $"Part purchase: '{partName}' — success (added as experimental)");
                    return true;
                }

                ParsekLog.Warn("ActionReplay",
                    $"Part purchase: '{partName}' — R&D instance null, FAILED");
                return false;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("ActionReplay",
                    $"Part purchase: '{partName}' — FAILED: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Pure decision logic for part purchase replay.
        /// </summary>
        internal static ReplayDecision DecidePartReplay(string partName, bool partExists, bool isAlreadyPurchased)
        {
            if (string.IsNullOrEmpty(partName)) return ReplayDecision.Fail;
            if (!partExists) return ReplayDecision.Skip;
            if (isAlreadyPurchased) return ReplayDecision.Skip;
            return ReplayDecision.Act;
        }

        #endregion

        #region Phase 3: Facility Upgrade

        /// <summary>
        /// Upgrades a facility to the level recorded in the committed event.
        /// IsReplayingActions must be set to bypass FacilityUpgradePatch.
        /// </summary>
        internal static bool ReplayFacilityUpgrade(GameStateEvent e, out bool skipped)
        {
            skipped = false;
            string facilityId = e.key;

            if (string.IsNullOrEmpty(facilityId))
            {
                ParsekLog.Warn("ActionReplay", "Facility upgrade: empty facilityId — FAILED");
                return false;
            }

            try
            {
                if (!ScenarioUpgradeableFacilities.protoUpgradeables.TryGetValue(
                        facilityId, out var proto)
                    || proto.facilityRefs == null || proto.facilityRefs.Count == 0)
                {
                    ParsekLog.Info("ActionReplay",
                        $"Facility upgrade: '{facilityId}' — facility not found, skipping " +
                        "(expected in Flight scene where facility refs are unavailable)");
                    skipped = true;
                    return true;
                }

                var facility = proto.facilityRefs[0];
                int currentLevel = facility.FacilityLevel;
                int maxLevel = facility.MaxLevel;
                int targetLevel = ComputeTargetLevel(e.valueAfter, maxLevel);

                var decision = DecideFacilityReplay(facilityId, currentLevel, targetLevel);
                if (decision == ReplayDecision.Skip)
                {
                    skipped = true;
                    ParsekLog.Info("ActionReplay",
                        $"Facility upgrade: '{facilityId}' — already at level {currentLevel}, skipping");
                    return true;
                }

                facility.SetLevel(targetLevel);
                ParsekLog.Info("ActionReplay",
                    $"Facility upgrade: '{facilityId}' level {currentLevel} → {targetLevel} — success");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("ActionReplay",
                    $"Facility upgrade: '{facilityId}' — FAILED: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Converts a normalized level (0.0-1.0) to an integer level.
        /// </summary>
        internal static int ComputeTargetLevel(double normalizedLevel, int maxLevel)
        {
            int level = (int)Math.Round(normalizedLevel * maxLevel);
            return Math.Max(0, Math.Min(level, maxLevel));
        }

        /// <summary>
        /// Pure decision logic for facility upgrade replay.
        /// </summary>
        internal static ReplayDecision DecideFacilityReplay(
            string facilityId, int currentLevel, int targetLevel)
        {
            if (string.IsNullOrEmpty(facilityId)) return ReplayDecision.Fail;
            if (currentLevel >= targetLevel) return ReplayDecision.Skip;
            return ReplayDecision.Act;
        }

        #endregion

        #region Phase 4: Crew Hire

        /// <summary>
        /// Hires a crew member with the name and trait from the committed event.
        /// SuppressCrewEvents must be set to prevent re-recording.
        /// </summary>
        internal static bool ReplayCrewHire(GameStateEvent e, out bool skipped)
        {
            skipped = false;
            string kerbalName = e.key;

            if (string.IsNullOrEmpty(kerbalName))
            {
                ParsekLog.Warn("ActionReplay", "Crew hire: empty kerbalName — FAILED");
                return false;
            }

            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                ParsekLog.Warn("ActionReplay",
                    $"Crew hire: '{kerbalName}' — CrewRoster null, FAILED");
                return false;
            }

            var decision = DecideCrewReplay(kerbalName, roster[kerbalName] != null);
            if (decision == ReplayDecision.Skip)
            {
                skipped = true;
                ParsekLog.Info("ActionReplay",
                    $"Crew hire: '{kerbalName}' — already in roster, skipping");
                return true;
            }

            try
            {
                ProtoCrewMember newCrew = roster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew);
                if (newCrew == null)
                {
                    ParsekLog.Warn("ActionReplay",
                        $"Crew hire: '{kerbalName}' — GetNewKerbal returned null, FAILED");
                    return false;
                }

                newCrew.ChangeName(kerbalName);

                string trait = GameStateEventDisplay.ExtractDetailField(e.detail, "trait");
                if (!string.IsNullOrEmpty(trait))
                    KerbalRoster.SetExperienceTrait(newCrew, trait);

                newCrew.rosterStatus = ProtoCrewMember.RosterStatus.Available;

                ParsekLog.Info("ActionReplay",
                    $"Crew hire: '{kerbalName}' trait={trait ?? "?"} — success");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("ActionReplay",
                    $"Crew hire: '{kerbalName}' — FAILED: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Pure decision logic for crew hire replay.
        /// </summary>
        internal static ReplayDecision DecideCrewReplay(string kerbalName, bool alreadyInRoster)
        {
            if (string.IsNullOrEmpty(kerbalName)) return ReplayDecision.Fail;
            if (alreadyInRoster) return ReplayDecision.Skip;
            return ReplayDecision.Act;
        }

        #endregion

        #region Replay Helpers

        /// <summary>
        /// Accumulates the result of a single replay action into the running counters.
        /// </summary>
        internal static void AccumulateReplayResult(
            bool success, bool wasSkipped,
            ref int successCount, ref int skipCount, ref int failCount)
        {
            if (wasSkipped)
                skipCount++;
            else if (success)
                successCount++;
            else
                failCount++;
        }

        #endregion
    }
}
