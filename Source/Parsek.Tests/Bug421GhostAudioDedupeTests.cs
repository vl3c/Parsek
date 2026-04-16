using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug #421: GhostAudio "AudioClip not found" warning fires once per audio
    /// event rather than once per (ghost, pid, clipPath). On a looping showcase
    /// recording with a missing stock clip (e.g. sound_IonEngine) this produced
    /// a steady trickle of identical warnings — seven in 3.5 minutes on the
    /// reported session. The fix is a per-ghost dedupe set keyed by (pid, clipPath)
    /// that emits ParsekLog.Warn on the first hit and silently drops repeats until
    /// the ghost is destroyed. A follow-up ghost spawn (e.g. a loop rebuild on the
    /// same slot) gets a fresh chance to warn.
    ///
    /// The dedupe is cosmetic — investigating whether the stock ion engine cfg
    /// actually ships the "sound_IonEngine" clip, or whether the ghost audio
    /// preset map is synthesizing the wrong key, is tracked as follow-up work on
    /// the #421 entry in docs/dev/todo-and-known-bugs.md.
    /// </summary>
    [Collection("Sequential")]
    public class Bug421GhostAudioDedupeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug421GhostAudioDedupeTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GhostVisualBuilder.ResetMissingAudioClipWarningsForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GhostVisualBuilder.ResetMissingAudioClipWarningsForTesting();
        }

        private int CountMissingClipWarnsFor(string clipPath, uint pid)
        {
            string clipFragment = $"'{clipPath}'";
            string pidFragment = $"pid={pid}";
            return logLines.Count(l =>
                l.Contains("[WARN]") &&
                l.Contains("[GhostAudio]") &&
                l.Contains("AudioClip not found") &&
                l.Contains(clipFragment) &&
                l.Contains(pidFragment));
        }

        [Fact]
        public void FirstWarn_ForNewGhostPidClip_Emits()
        {
            GhostVisualBuilder.WarnMissingAudioClipOnce(
                "ghost_42", persistentId: 100000,
                clipPath: "sound_IonEngine", partName: "ionEngine");

            Assert.Equal(1, CountMissingClipWarnsFor("sound_IonEngine", 100000));
            Assert.Contains(logLines, l => l.Contains("ionEngine"));
        }

        [Fact]
        public void SecondWarn_ForSameGhostPidClip_IsSuppressed()
        {
            GhostVisualBuilder.WarnMissingAudioClipOnce(
                "ghost_42", 100000, "sound_IonEngine", "ionEngine");
            GhostVisualBuilder.WarnMissingAudioClipOnce(
                "ghost_42", 100000, "sound_IonEngine", "ionEngine");

            // Exactly one emission total — second call was silently deduped.
            Assert.Equal(1, CountMissingClipWarnsFor("sound_IonEngine", 100000));
        }

        [Fact]
        public void RepeatedWarns_ForSameGhostPidClip_EmitExactlyOnce()
        {
            // The reported bug was 7 identical warns in 3.5 minutes — simulate that
            // and assert the dedupe collapses them to one.
            for (int i = 0; i < 7; i++)
            {
                GhostVisualBuilder.WarnMissingAudioClipOnce(
                    "ghost_42", 100000, "sound_IonEngine", "ionEngine");
            }

            Assert.Equal(1, CountMissingClipWarnsFor("sound_IonEngine", 100000));
        }

        [Fact]
        public void DifferentClipName_OnSameGhostAndPid_EmitsSeparately()
        {
            // A single pid may carry more than one engine module with distinct clip
            // paths. Dedupe must not over-collapse across clip names.
            GhostVisualBuilder.WarnMissingAudioClipOnce(
                "ghost_42", 100000, "sound_IonEngine", "ionEngine");
            GhostVisualBuilder.WarnMissingAudioClipOnce(
                "ghost_42", 100000, "sound_rocket_hard", "ionEngine");

            Assert.Equal(1, CountMissingClipWarnsFor("sound_IonEngine", 100000));
            Assert.Equal(1, CountMissingClipWarnsFor("sound_rocket_hard", 100000));
        }

        [Fact]
        public void DifferentPid_OnSameGhostAndClip_EmitsSeparately()
        {
            // Two engine parts on the same ghost, both missing the same clip, must
            // each warn once (they're separate diagnostics from the user's POV).
            GhostVisualBuilder.WarnMissingAudioClipOnce(
                "ghost_42", 100000, "sound_IonEngine", "ionEngine");
            GhostVisualBuilder.WarnMissingAudioClipOnce(
                "ghost_42", 101111, "sound_IonEngine", "ionEngine");

            Assert.Equal(1, CountMissingClipWarnsFor("sound_IonEngine", 100000));
            Assert.Equal(1, CountMissingClipWarnsFor("sound_IonEngine", 101111));
        }

        [Fact]
        public void DifferentGhost_WithSamePidAndClip_EmitsSeparately()
        {
            // Two ghosts with overlapping pid spaces (showcase ghosts always start
            // at pid=100000) must not share a dedupe set.
            GhostVisualBuilder.WarnMissingAudioClipOnce(
                "ghost_42", 100000, "sound_IonEngine", "ionEngine");
            GhostVisualBuilder.WarnMissingAudioClipOnce(
                "ghost_43", 100000, "sound_IonEngine", "ionEngine");

            // Two emissions total for pid=100000 + sound_IonEngine — one per ghost.
            Assert.Equal(2, CountMissingClipWarnsFor("sound_IonEngine", 100000));
        }

        [Fact]
        public void ClearMissingAudioClipWarnings_AllowsSubsequentWarnToFireAgain()
        {
            GhostVisualBuilder.WarnMissingAudioClipOnce(
                "ghost_42", 100000, "sound_IonEngine", "ionEngine");
            Assert.Equal(1, CountMissingClipWarnsFor("sound_IonEngine", 100000));

            // Simulate the ghost being destroyed (loop rebuild, etc.).
            GhostVisualBuilder.ClearMissingAudioClipWarnings("ghost_42");

            // A fresh spawn with the same root name gets to warn once more.
            GhostVisualBuilder.WarnMissingAudioClipOnce(
                "ghost_42", 100000, "sound_IonEngine", "ionEngine");

            Assert.Equal(2, CountMissingClipWarnsFor("sound_IonEngine", 100000));
        }

        [Fact]
        public void ClearMissingAudioClipWarnings_DoesNotAffectOtherGhosts()
        {
            GhostVisualBuilder.WarnMissingAudioClipOnce(
                "ghost_42", 100000, "sound_IonEngine", "ionEngine");
            GhostVisualBuilder.WarnMissingAudioClipOnce(
                "ghost_43", 100000, "sound_IonEngine", "ionEngine");

            GhostVisualBuilder.ClearMissingAudioClipWarnings("ghost_42");

            // ghost_43's dedupe entry is still in place — a repeat call should suppress.
            GhostVisualBuilder.WarnMissingAudioClipOnce(
                "ghost_43", 100000, "sound_IonEngine", "ionEngine");

            // Two total emissions: the original pair. ghost_43's third call was deduped.
            Assert.Equal(2, CountMissingClipWarnsFor("sound_IonEngine", 100000));
        }

        [Fact]
        public void ClearMissingAudioClipWarnings_NullRootName_IsNoOp()
        {
            // Defensive: callers pass state.ghost?.name which can be null if the GO
            // was already destroyed. Clearing with null must not throw and must not
            // affect real buckets.
            GhostVisualBuilder.WarnMissingAudioClipOnce(
                "ghost_42", 100000, "sound_IonEngine", "ionEngine");

            GhostVisualBuilder.ClearMissingAudioClipWarnings(null);

            // ghost_42's bucket still holds the entry, so a repeat is deduped.
            GhostVisualBuilder.WarnMissingAudioClipOnce(
                "ghost_42", 100000, "sound_IonEngine", "ionEngine");

            Assert.Equal(1, CountMissingClipWarnsFor("sound_IonEngine", 100000));
        }

        [Fact]
        public void ResetMissingAudioClipWarningsForTesting_ClearsEveryBucket()
        {
            GhostVisualBuilder.WarnMissingAudioClipOnce(
                "ghost_42", 100000, "sound_IonEngine", "ionEngine");
            GhostVisualBuilder.WarnMissingAudioClipOnce(
                "ghost_43", 100000, "sound_rocket_hard", "liquidEngine");
            Assert.Equal(1, CountMissingClipWarnsFor("sound_IonEngine", 100000));
            Assert.Equal(1, CountMissingClipWarnsFor("sound_rocket_hard", 100000));

            GhostVisualBuilder.ResetMissingAudioClipWarningsForTesting();

            // After reset every (ghost, pid, clip) combination warns fresh again.
            GhostVisualBuilder.WarnMissingAudioClipOnce(
                "ghost_42", 100000, "sound_IonEngine", "ionEngine");
            GhostVisualBuilder.WarnMissingAudioClipOnce(
                "ghost_43", 100000, "sound_rocket_hard", "liquidEngine");

            Assert.Equal(2, CountMissingClipWarnsFor("sound_IonEngine", 100000));
            Assert.Equal(2, CountMissingClipWarnsFor("sound_rocket_hard", 100000));
        }

        [Fact]
        public void WarnMessage_IncludesClipPathPartNameAndPid()
        {
            // Dedupe must not change the user-visible warn format — the log line
            // still carries the three IDs needed for diagnosis.
            GhostVisualBuilder.WarnMissingAudioClipOnce(
                "ghost_42", 100000, "sound_IonEngine", "ionEngine");

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[GhostAudio]") &&
                l.Contains("AudioClip not found") &&
                l.Contains("'sound_IonEngine'") &&
                l.Contains("'ionEngine'") &&
                l.Contains("pid=100000"));
        }
    }
}
