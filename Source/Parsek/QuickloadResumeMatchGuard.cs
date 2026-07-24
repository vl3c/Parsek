namespace Parsek
{
    /// <summary>
    /// Why a quickload-resume vessel-match candidate was rejected (or None when acceptable).
    /// </summary>
    internal enum QuickloadResumeRejection
    {
        /// <summary>Candidate is acceptable for a quickload-resume adoption.</summary>
        None = 0,

        /// <summary>
        /// Candidate is the scene-entry FRESH ROLLOUT vessel (a NEW_FROM_FILE / fresh-launch
        /// startup) — by definition a brand-new physical launch, never the vessel a stashed
        /// pending tree was recording.
        /// </summary>
        FreshRollout = 1,

        /// <summary>
        /// Candidate's launch Guid (Vessel.id) conclusively differs from the recording's
        /// RecordedVesselGuid — a different physical launch of (typically) the same craft file.
        /// </summary>
        LaunchGuidMismatch = 2,
    }

    /// <summary>
    /// Pure decision guard for the quickload-resume vessel-match loop
    /// (ParsekFlight.RestoreActiveTreeFromPending).
    ///
    /// BDOCK-1 flight 16 (2026-07-24, analyzer INV4-PARTEVENT-PID red): a same-session
    /// `launch_vessel` FLIGHT->FLIGHT reload was classified as a quickload (the
    /// vesselSwitchPending flag was stale), so the just-committed Station tree was stashed
    /// pending-Limbo and the restore coroutine went looking for the recorded vessel. The
    /// recorded pid did not match the new scene's active vessel, but the SECONDARY NAME
    /// match did ("Kerbal X" == "Kerbal X" — both craft launched from the same .craft
    /// file), so the Station's recording was PID-remapped onto the freshly rolled-out
    /// Interceptor and absorbed its entire flight: part events carrying the Interceptor's
    /// craft-baked pids landed in a recording whose ghost snapshot holds the Station's
    /// live (regenerated) pids, which is exactly the INV4 unresolved-pid fingerprint.
    ///
    /// Contract (see VesselLaunchIdentity and the craft-baked-persistentId gotcha in
    /// .claude/CLAUDE.md): a name or pid match is NEVER proof of same-launch identity.
    /// The launch-unique discriminator is Vessel.id, captured as
    /// Recording.RecordedVesselGuid; the scene-entry fresh-rollout pid
    /// (RecordingStore.SceneEntryFreshRolloutVesselPid) is the cheap same-scene fast
    /// path that identifies a brand-new launch even before guids are compared.
    /// An unknown guid on either side stays inconclusive so legacy recordings keep the
    /// pre-guid pid/name fallback behavior.
    /// </summary>
    internal static class QuickloadResumeMatchGuard
    {
        /// <summary>
        /// Full candidate evaluation: fresh-rollout rejection first (recording-independent,
        /// conclusive), then the launch-guid gate. Returns None when the candidate may be
        /// matched by the existing pid/name logic.
        ///
        /// BY DESIGN this composed verdict is NOT on the live path: the
        /// RestoreActiveTreeFromPending loop calls the two predicates directly
        /// (IsFreshRolloutCandidate to reject the scene-entry fresh rollout for every
        /// recording, then LaunchGuidConclusivelyDiffers per candidate recording) because it
        /// interleaves the guid gate with the EVA-parent walk: a parent whose guid agrees
        /// must still match even when the child recording's guid conclusively differs, so the
        /// loop cannot collapse both predicates into one per-candidate call. EvaluateCandidate
        /// keeps the two rejection reasons composed in a single decision for unit-test
        /// coverage and any future caller that wants the whole verdict at once; it is
        /// deliberately not wired into the loop rather than dead code awaiting a caller.
        /// </summary>
        internal static QuickloadResumeRejection EvaluateCandidate(
            string recordedVesselGuid,
            uint candidatePid,
            string candidateGuid,
            uint sceneEntryFreshRolloutPid)
        {
            if (IsFreshRolloutCandidate(candidatePid, sceneEntryFreshRolloutPid))
                return QuickloadResumeRejection.FreshRollout;
            if (LaunchGuidConclusivelyDiffers(recordedVesselGuid, candidateGuid))
                return QuickloadResumeRejection.LaunchGuidMismatch;
            return QuickloadResumeRejection.None;
        }

        /// <summary>
        /// True when the candidate vessel is the scene-entry fresh rollout captured by
        /// CaptureFreshRolloutVesselPidIfApplicable. A zero captured pid (non-fresh scene
        /// entry, or capture failed) never rejects.
        /// </summary>
        internal static bool IsFreshRolloutCandidate(uint candidatePid, uint sceneEntryFreshRolloutPid)
        {
            return sceneEntryFreshRolloutPid != 0 && candidatePid == sceneEntryFreshRolloutPid;
        }

        /// <summary>
        /// True when the recording's launch guid and the candidate vessel's guid are BOTH
        /// known and differ — a conclusive different-launch verdict. Either side unknown
        /// is inconclusive (returns false), preserving pid/name fallback for legacy data.
        /// </summary>
        internal static bool LaunchGuidConclusivelyDiffers(string recordedVesselGuid, string candidateGuid)
        {
            return VesselLaunchIdentity.GuidsConclusivelyDiffer(recordedVesselGuid, candidateGuid);
        }
    }
}
