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
        [HarmonyPatch("GetSubjectByID")]
        static void Postfix_GetSubjectByID(ScienceSubject __result)
        {
            ApplyCommittedScience(__result);
        }

        static void ApplyCommittedScience(ScienceSubject subject)
        {
            if (subject == null) return;

            float committedScience;
            if (!GameStateStore.TryGetCommittedSubjectScience(subject.id, out committedScience))
                return;

            if (subject.science < committedScience)
            {
                ParsekLog.VerboseRateLimited("SciencePatch", "apply-committed",
                    $"Applied committed science: {subject.id} {subject.science:F1} → {committedScience:F1}", 30.0);
                subject.science = committedScience;
            }
        }
    }
}
