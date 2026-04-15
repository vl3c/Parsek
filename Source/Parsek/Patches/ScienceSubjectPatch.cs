using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony postfix on ResearchAndDevelopment.GetExperimentSubject and GetSubjectByID
    /// to inject committed science values into ScienceSubject.science.
    /// This makes KSP's diminishing returns formula correctly compute reduced/zero
    /// remaining science for experiments already committed on the timeline.
    /// </summary>
    [HarmonyPatch(typeof(ResearchAndDevelopment))]
    internal static class ScienceSubjectPatch
    {
        [HarmonyPostfix]
        [HarmonyPatch("GetExperimentSubject",
            typeof(ScienceExperiment),
            typeof(ExperimentSituations),
            typeof(CelestialBody),
            typeof(string),
            typeof(string))]
        static void Postfix_GetExperimentSubject(ScienceSubject __result)
        {
            ApplyCommittedScience(__result);
        }

        [HarmonyPostfix]
        [HarmonyPatch("GetSubjectByID", typeof(string))]
        static void Postfix_GetSubjectByID(ScienceSubject __result)
        {
            ApplyCommittedScience(__result);
        }

        static void ApplyCommittedScience(ScienceSubject subject)
        {
            if (subject == null) return;

            if (!TryResolveCommittedScience(subject.id, out float committedScience))
                return;

            if (subject.science < committedScience)
            {
                GameStateStore.RecordOriginalScience(subject.id, subject.science);
                ParsekLog.VerboseRateLimited("SciencePatch", "apply-committed",
                    $"Applied committed science: {subject.id} {subject.science:F1} → {committedScience:F1}", 30.0);
                subject.science = committedScience;
            }
        }

        /// <summary>
        /// #395: resolves the committed-science value for a subject id using the
        /// authoritative source chain. Returns true and sets <paramref name="value"/>
        /// if a non-zero committed value exists.
        ///
        /// Primary source: <see cref="LedgerOrchestrator.Science"/> (the ScienceModule
        /// that the R&amp;D pool patcher also reads from). Reading from the module
        /// prevents the Archive display from masking a broken ledger — previously,
        /// <see cref="GameStateStore.TryGetCommittedSubjectScience"/> could return a
        /// stale value while the module had zero credited for the same subject, making
        /// one screen wrong and the other right.
        ///
        /// Fallback: the store path, but only when the LedgerOrchestrator is
        /// uninitialized (pre-init loads, sandbox mode). Internal static for testability.
        /// </summary>
        internal static bool TryResolveCommittedScience(string subjectId, out float value)
        {
            value = 0f;
            if (string.IsNullOrEmpty(subjectId)) return false;

            var scienceModule = LedgerOrchestrator.Science;
            if (scienceModule != null && LedgerOrchestrator.IsInitialized)
            {
                double credited = scienceModule.GetSubjectCredited(subjectId);
                if (credited <= 0.0) return false;
                value = (float)credited;
                return true;
            }

            if (GameStateStore.TryGetCommittedSubjectScience(subjectId, out float storeValue))
            {
                value = storeValue;
                return true;
            }

            return false;
        }
    }
}
