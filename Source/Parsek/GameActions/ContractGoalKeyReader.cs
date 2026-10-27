using System;
using System.Collections.Generic;
using Contracts;

namespace Parsek
{
    /// <summary>
    /// Reads a live stock contract's goal key (<see cref="ContractGoalKey"/>) through the
    /// contract's own public members, the same values stock saves (no <c>Contract.Save</c> on
    /// a contract still being generated). The twin doors read it only when a committed future
    /// accept of that type carries a goal key (<see cref="ReadIfJudged"/>).
    /// </summary>
    internal static class ContractGoalKeyReader
    {
        /// <summary>The live contract's goal key, or null for a type with none.</summary>
        internal static string FromLiveContract(Contract contract)
        {
            if (contract == null)
                return null;
            switch (contract.GetType().Name)
            {
                case ContractGoalKey.ExplorationType:
                {
                    var exploration = contract as FinePrint.Contracts.ExplorationContract;
                    if (exploration == null || exploration.TrackingParameters == null)
                        return null;
                    var milestones = new List<KeyValuePair<string, int>>();
                    for (int i = 0; i < exploration.TrackingParameters.Count; i++)
                    {
                        var p = exploration.TrackingParameters[i];
                        if (p == null || p.milestone == null || p.milestone.body == null)
                            continue;
                        milestones.Add(new KeyValuePair<string, int>(
                            p.milestone.type.ToString(), p.milestone.body.flightGlobalsIndex));
                    }
                    return ContractGoalKey.ForExploration(milestones);
                }
                case ContractGoalKey.PlantFlagType:
                {
                    var flag = contract as Contracts.Templates.PlantFlag;
                    if (flag == null || flag.TargetBody == null)
                        return null;
                    return ContractGoalKey.ForPlantFlag((int)flag.Prestige, flag.TargetBody.flightGlobalsIndex);
                }
                case ContractGoalKey.CometSampleType:
                {
                    var comet = contract as FinePrint.Contracts.CometSampleContract;
                    return comet == null ? null : ContractGoalKey.ForCometSample(comet.CometPersistentId);
                }
                default:
                    return null;
            }
        }

        /// <summary>
        /// The doors' read: the contract's goal key when a committed future accept of its type
        /// carries one at <paramref name="nowUT"/>
        /// (<see cref="ContractTwinRule.AnyFutureGoalHolderOfType"/>), else null without
        /// touching the contract. A read that throws is logged and judged on the stock id only.
        /// </summary>
        internal static string ReadIfJudged(Contract contract, CommittedFutureIndex index, double nowUT, string door)
        {
            if (contract == null)
                return null;
            string typeName = contract.GetType().Name;
            if (!ContractTwinRule.AnyFutureGoalHolderOfType(index, typeName, nowUT))
                return null;
            try
            {
                return FromLiveContract(contract);
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited(ContractTwinRule.Tag, "goal-read-" + door,
                    $"{door}: reading the goal key of a {typeName} threw ({ex.Message}) - judged on the stock id only");
                return null;
            }
        }
    }
}
