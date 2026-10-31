using System;
using System.Collections.Generic;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>One tech node stock unlocked for free, read around the unlock call.</summary>
    internal struct FreeTechUnlock
    {
        internal string TechId;
        /// <summary>The node's own science cost (informational: stock charged none of it).</summary>
        internal float NodeCost;
        /// <summary>The parts the node assigns (<c>AvailablePart.TechRequired</c> == the id).</summary>
        internal List<string> PartNames;
    }

    /// <summary>
    /// Prefix + Postfix on <c>ResearchAndDevelopment.UnlockProtoTechNode(ProtoTechNode)</c>
    /// (todo FREE-TECH-NODE-LOST-AFTER-REWIND). Stock unlocks a node for free there and fires
    /// no <c>GameEvents.OnTechnologyResearched</c> (KSP 1.12.5, decompiled): the method writes
    /// an Available proto node (and, under BypassEntryPurchaseAfterResearch or with no
    /// Funding, the node's parts as purchased) and returns. Its two stock callers are
    /// <c>ProgressNode.AwardProgressRandomTech</c> (the tech gift of a point-of-interest
    /// anomaly) and <c>ResearchAndDevelopment.CheatTechnology</c> (the debug menu). Without
    /// this capture the unlock has no ledger row, so the next tech patch whose baseline does
    /// not hold the node re-locks it for good. The prefix records whether the node was
    /// already Available; the postfix, only when the call made it Available, raises
    /// <see cref="FreeUnlockCaptured"/>, which the subscribed <see cref="GameStateRecorder"/>
    /// files as a TechResearched event with cost 0. Parsek itself never calls this method
    /// (its tech patching writes through <c>SetTechState</c>).
    /// </summary>
    [HarmonyPatch(typeof(ResearchAndDevelopment), nameof(ResearchAndDevelopment.UnlockProtoTechNode))]
    internal static class FreeTechUnlockCapturePatch
    {
        private const string Tag = "FreeTechUnlockCapture";

        /// <summary>Skip reasons of <see cref="DecideCapture"/>.</summary>
        internal const string SkipAlreadyAvailable = "already-available";
        internal const string SkipNotAvailableAfter = "not-available-after";
        internal const string SkipReplaying = "action-replay";
        internal const string SkipNoTechId = "tech-id-missing";

        /// <summary>Raised after stock unlocked one node for free.</summary>
        internal static event Action<FreeTechUnlock> FreeUnlockCaptured;

        internal struct PrefixState
        {
            internal bool Armed;
            internal bool WasAvailable;
            internal string TechId;
        }

        static void Prefix(ResearchAndDevelopment __instance, ProtoTechNode node, out PrefixState __state)
        {
            __state = default(PrefixState);
            if (ParsekGameModeGate.CheckInert("FreeTechUnlockCapturePatch.Prefix")) return; // S9 game-mode gate
            if (__instance == null || node == null) return;
            try
            {
                string techId = node.techID;
                ProtoTechNode current = string.IsNullOrEmpty(techId) ? null : __instance.GetTechState(techId);
                __state = new PrefixState
                {
                    Armed = true,
                    TechId = techId,
                    WasAvailable = current != null && current.state == RDTech.State.Available
                };
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Prefix could not read the node state ({ex.GetType().Name}: {ex.Message}) - not captured");
                __state = default(PrefixState);
            }
        }

        static void Postfix(ResearchAndDevelopment __instance, ProtoTechNode node, PrefixState __state)
        {
            if (!__state.Armed || __instance == null) return;
            try
            {
                ProtoTechNode after = string.IsNullOrEmpty(__state.TechId) ? null : __instance.GetTechState(__state.TechId);
                bool nowAvailable = after != null && after.state == RDTech.State.Available;
                string skip = DecideCapture(__state.TechId, __state.WasAvailable, nowAvailable,
                    GameStateRecorder.IsReplayingActions);
                if (skip != null)
                {
                    ParsekLog.Verbose(Tag,
                        $"Free tech unlock not captured: tech='{__state.TechId ?? "(null)"}' reason={skip}");
                    return;
                }

                Report(new FreeTechUnlock
                {
                    TechId = __state.TechId,
                    NodeCost = node != null ? node.scienceCost : 0f,
                    PartNames = CollectNodePartNames(__state.TechId)
                });
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Postfix failed for tech '{__state.TechId ?? "(null)"}' ({ex.GetType().Name}: {ex.Message}) - not captured");
            }
        }

        /// <summary>
        /// Pure: null when the unlock is captured, else the skip reason. Captured only when the
        /// call took a node from not-Available to Available outside Parsek's own action replay
        /// (a node the career already held is a no-op for stock and for the ledger).
        /// </summary>
        internal static string DecideCapture(string techId, bool wasAvailable, bool nowAvailable, bool replaying)
        {
            if (string.IsNullOrEmpty(techId)) return SkipNoTechId;
            if (replaying) return SkipReplaying;
            if (wasAvailable) return SkipAlreadyAvailable;
            if (!nowAvailable) return SkipNotAvailableAfter;
            return null;
        }

        /// <summary>Raises <see cref="FreeUnlockCaptured"/> (called by the postfix).</summary>
        internal static void Report(FreeTechUnlock unlock)
        {
            var handler = FreeUnlockCaptured;
            if (handler == null)
            {
                ParsekLog.Verbose(Tag,
                    "Free tech unlock with no subscribed recorder - not captured (tech=" +
                    (unlock.TechId ?? "(null)") + ")");
                return;
            }
            handler(unlock);
        }

        private static List<string> CollectNodePartNames(string techId)
        {
            var names = new List<string>();
            if (string.IsNullOrEmpty(techId)) return names;
            try
            {
                var parts = PartLoader.LoadedPartsList;
                if (parts == null) return names;
                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    if (p != null && string.Equals(p.TechRequired, techId, StringComparison.Ordinal))
                        names.Add(p.name ?? "");
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"Part list read failed for tech '{techId}' ({ex.GetType().Name}) - parts left empty");
            }
            return names;
        }

        internal static void ResetForTesting()
        {
            FreeUnlockCaptured = null;
        }

        /// <summary>
        /// In-game test seam: replaces every subscriber of <see cref="FreeUnlockCaptured"/> with
        /// <paramref name="probe"/> until the returned scope is disposed, so a live probe of the
        /// patched stock call never reaches the career's recorder or ledger.
        /// </summary>
        internal static IDisposable SwapSubscribersForTesting(Action<FreeTechUnlock> probe)
        {
            var saved = FreeUnlockCaptured;
            FreeUnlockCaptured = probe;
            return new SubscriberRestore(saved);
        }

        private sealed class SubscriberRestore : IDisposable
        {
            private readonly Action<FreeTechUnlock> saved;
            private bool done;

            internal SubscriberRestore(Action<FreeTechUnlock> saved)
            {
                this.saved = saved;
            }

            public void Dispose()
            {
                if (done) return;
                done = true;
                FreeUnlockCaptured = saved;
            }
        }
    }
}
