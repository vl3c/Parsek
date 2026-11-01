using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Contracts;

namespace Parsek
{
    // Science pool patch, its pending-adjustment folds, and per-subject science.
    internal static partial class KspStatePatcher
    {
        /// <summary>
        /// Pure decision core for <see cref="PatchScience"/> (apply-boundary test seam,
        /// audit rec #5). Takes the live science pool as a PARAMETER instead of reading
        /// <c>ResearchAndDevelopment.Instance</c>, so the compute-delta -> drawdown-clamp ->
        /// no-op / suspicious-drawdown composition is unit-testable headlessly. The live
        /// wrapper performs the side effects (clamp WARN+toast, AddScience, logs) from the
        /// returned flags. No KSP singletons touched.
        /// </summary>
        internal readonly struct SciencePoolPatchDecision
        {
            /// <summary>True when |delta| >= the 0.001 no-op threshold (a write is needed).</summary>
            internal readonly bool ShouldWrite;
            /// <summary>The float delta passed to AddScience (effectiveTarget - currentScience).</summary>
            internal readonly float Delta;
            /// <summary>The post-clamp target (the value the pool reaches), kept as a double for the logs.</summary>
            internal readonly double EffectiveTarget;
            /// <summary>True when the symmetric drawdown guard clamped the target (up to or down to live).</summary>
            internal readonly bool Clamped;
            /// <summary>Which direction the guard clamped (Up = kept earnings, Down = held the spent value, None = no clamp).</summary>
            internal readonly ClampDirection Direction;
            /// <summary>True when a needed write is a >10% drawdown of a non-trivial pool.</summary>
            internal readonly bool SuspiciousDrawdown;

            internal SciencePoolPatchDecision(
                bool shouldWrite, float delta, double effectiveTarget,
                bool clamped, ClampDirection direction, bool suspiciousDrawdown)
            {
                ShouldWrite = shouldWrite;
                Delta = delta;
                EffectiveTarget = effectiveTarget;
                Clamped = clamped;
                Direction = direction;
                SuspiciousDrawdown = suspiciousDrawdown;
            }
        }

        /// <summary>
        /// Resolves the science-pool patch outcome from the live pool + the recalc target +
        /// the (pending-adjusted) running balance. Mirrors <see cref="PatchScience"/>'s
        /// arithmetic exactly: drawdown-guard the target, derive the float delta against the
        /// post-clamp target, decide no-op vs write on the 0.001 threshold, and flag a
        /// suspicious >10% drawdown only when a write is actually needed. Pure: no singletons.
        /// </summary>
        internal static SciencePoolPatchDecision ResolveSciencePoolPatch(
            float currentScience, double targetScience, double runningScience,
            bool authoritativeReduction)
        {
            double effTarget = ApplyDrawdownGuard(
                targetScience, runningScience, currentScience, 0.001,
                authoritativeReduction, "science", out bool clamped, out ClampDirection direction);
            float delta = (float)(effTarget - (double)currentScience);
            bool shouldWrite = Math.Abs(delta) >= 0.001f;
            bool suspicious = shouldWrite && IsSuspiciousDrawdown(delta, currentScience);
            return new SciencePoolPatchDecision(shouldWrite, delta, effTarget, clamped, direction, suspicious);
        }

        /// <summary>
        /// Patches KSP's science pool to match the module's available science.
        /// Uses AddScience with a computed delta to reach the target value.
        /// No-op if ResearchAndDevelopment.Instance is null (sandbox mode).
        /// </summary>
        internal static void PatchScience(
            ScienceModule science,
            bool suppressSuspiciousDrawdownWarnings = false,
            bool authoritativeReduction = false)
        {
            if (science == null)
            {
                ParsekLog.Warn(Tag, "PatchScience: null ScienceModule — skipping");
                return;
            }

            if (ResearchAndDevelopment.Instance == null)
            {
                VerboseStablePatchState("patch-skip|science|rnd", "rnd-null",
                    "PatchScience: ResearchAndDevelopment.Instance is null (sandbox mode) — skipping");
                return;
            }

            // Expected during early load: DeferredSeedAndRecalculate will re-trigger
            // once KSP singletons report non-zero values. See bug #392.
            if (!science.HasSeed)
            {
                VerboseStablePatchState("patch-skip|science|seed", "missing-seed",
                    "PatchScience: module has no ScienceInitial seed — skipping to preserve KSP values");
                return;
            }

            // A singleton present but not yet loaded (an in-session load: R&D loads after
            // ParsekScenario) holds no value of this save; measuring it would read stock's
            // own load as a leak. Its OnLoad overwrites the pool anyway, and the in-session
            // repatch runs once it has loaded.
            if (!CurrencyScenarioReadiness.IsScenarioModuleLoaded(ResearchAndDevelopment.Instance))
            {
                VerboseStablePatchState("patch-skip|science|not-loaded", "not-loaded",
                    "PatchScience: ResearchAndDevelopment has not finished loading - skipping " +
                    "(the stock pool reference measures only a loaded pool)");
                return;
            }

            float currentScience = ResearchAndDevelopment.Instance.Science;
            bool allowNegativeCurrency = ReadAllowNegativeCurrency();

            // The three pending adjusters (a stock KSC science credit, tech-unlock debit or
            // strategy exchange that hit the live pool while its ledger row is still catching
            // up) are something stock applied: the stock pool reference steps their change,
            // and the write target takes them as before. The strategy adjuster and the
            // pending-adjusted running balance must resolve the IDENTICAL pending amount
            // (STRATEGY-PREFIX-HOLDBACK-PERMANENT), so the basis is computed ONCE.
            double preStrategyRunning = ComputePreStrategyPendingAdjustedRunningScience(science);
            double runningScience = ComputePendingAdjustedRunningScience(
                preStrategyRunning, currentScience);
            double pendingAdjustNow = runningScience - science.GetRunningScience();

            StockPoolMeasurement measurement = MeasureStockPool(
                StockPool.Science, "Science", science, currentScience, pendingAdjustNow,
                authoritativeReduction, 0.001,
                science.GetProjectionMinBalance() + pendingAdjustNow, out List<StockPoolRow> referenceRows);

            double targetWithoutLeak = StockPoolReference.ResolveCurrencyWriteTarget(
                science.GetProjectionMinBalance(), science.GetRunningScience(), currentScience,
                allowNegativeCurrency, 0.0);
            double targetScience = StockPoolReference.ResolveCurrencyWriteTarget(
                science.GetProjectionMinBalance(), science.GetRunningScience(), currentScience,
                allowNegativeCurrency, measurement.Adjustment);
            targetScience = AdjustSciencePatchTargetForPendingRecentTechResearch(
                targetScience, currentScience);
            targetScience = AdjustSciencePatchTargetForPendingRecentScienceEarning(
                targetScience, currentScience);
            targetScience = AdjustSciencePatchTargetForPendingStrategyScienceDebit(
                targetScience, currentScience, preStrategyRunning);
            if (measurement.GapAdopted)
                targetScience = currentScience;

            // The guard is the reference: the target already carries this patch's adjustment
            // (later walks carry its row), so the pure core runs with its running-vs-live
            // clamp bypassed.
            SciencePoolPatchDecision decision = ResolveSciencePoolPatch(
                currentScience, targetScience, runningScience, authoritativeReduction: true);
            EmitStockPoolLeak("Science", measurement, runningScience, targetWithoutLeak,
                decision.EffectiveTarget, "Kept your earned science",
                "Held your science at the spent value", perSubjectScienceNote: true);
            NoteScienceTakeBackShortfall(measurement, runningScience);

            if (!decision.ShouldWrite)
            {
                CommitStockPool(StockPool.Science, referenceRows, measurement, currentScience,
                    pendingAdjustNow);
                VerboseStablePatchState("patch-noop|science", decision.EffectiveTarget.ToString("F1", IC),
                    $"PatchScience: balance unchanged (current={currentScience.ToString("F1", IC)}, " +
                    $"target={decision.EffectiveTarget.ToString("F1", IC)})");
            }
            else
            {
                // #402 parity: WARN when science drawdown >10% of a non-trivial pool.
                if (!suppressSuspiciousDrawdownWarnings && decision.SuspiciousDrawdown)
                {
                    ParsekLog.Warn(Tag,
                        $"PatchScience: suspicious drawdown delta={decision.Delta.ToString("F1", IC)} " +
                        $"from current={currentScience.ToString("F1", IC)} " +
                        $"(>10% of pool, target={decision.EffectiveTarget.ToString("F1", IC)}) — " +
                        $"earning channel may be missing. HasSeed={science.HasSeed}");
                }
                ResearchAndDevelopment.Instance.AddScience(decision.Delta, TransactionReasons.None);

                float afterScience = ResearchAndDevelopment.Instance.Science;
                CommitStockPool(StockPool.Science, referenceRows, measurement, afterScience,
                    pendingAdjustNow);
                ParsekLog.Info(Tag,
                    $"PatchScience: {currentScience.ToString("F1", IC)} -> {afterScience.ToString("F1", IC)} " +
                    $"(delta={decision.Delta.ToString("F1", IC)}, target={decision.EffectiveTarget.ToString("F1", IC)})");

                // LedgerTrace Tier-C: reconcile the computed target against the live
                // read-back we just took (afterScience). A drift beyond the science
                // tolerance is a ledger-vs-truth anomaly (the write did not land where
                // the recalc intended). Reuses afterScience; no second read.
                if (LedgerTrace.IsEnabled)
                {
                    EmitPoolReadbackAnomaly("science", decision.EffectiveTarget, afterScience);
                }
            }

            // Always patch per-subject credited totals — individual subjects may have changed
            // even when the total balance is unchanged (e.g., one experiment reverted, another added)
            PatchPerSubjectScience(science);
        }

        /// <summary>
        /// Keeps recent stock tech-unlock debits authoritative while the matching
        /// <c>TechResearched</c> ledger action is still catching up.
        /// </summary>
        internal static double AdjustSciencePatchTargetForPendingRecentTechResearch(
            double targetScience,
            float currentScience)
        {
            if (targetScience <= (double)currentScience)
                return targetScience;

            double pendingDebit = LedgerOrchestrator.GetPendingRecentKscTechResearchScienceDebit();
            if (pendingDebit <= 0.0)
                return targetScience;

            double adjustedTarget = targetScience - pendingDebit;
            if (adjustedTarget < (double)currentScience)
                adjustedTarget = (double)currentScience;

            if (adjustedTarget < targetScience)
            {
                ParsekLog.Verbose(Tag,
                    $"PatchScience: holding back {pendingDebit.ToString("F1", IC)} pending tech-unlock science " +
                    $"(current={currentScience.ToString("F1", IC)}, " +
                    $"ledgerTarget={targetScience.ToString("F1", IC)}, " +
                    $"adjustedTarget={adjustedTarget.ToString("F1", IC)})");
            }

            return adjustedTarget;
        }

        /// <summary>
        /// Keeps a recent stock science credit (VesselRecovery / ScienceTransmission)
        /// authoritative while the matching <c>ScienceEarning</c> ledger action is still
        /// catching up. Without this, a recalc triggered by a co-incident event (e.g. the
        /// first science milestone) firing between KSP crediting the science and the ledger
        /// ingesting it would transiently claw the pool back down to the stale pre-credit
        /// total. Mirror of <see cref="AdjustSciencePatchTargetForPendingRecentTechResearch"/>
        /// for the opposite (ledger-behind-KSP) direction.
        /// </summary>
        internal static double AdjustSciencePatchTargetForPendingRecentScienceEarning(
            double targetScience,
            float currentScience)
        {
            if (targetScience >= (double)currentScience)
                return targetScience;

            double pendingCredit = LedgerOrchestrator.GetPendingRecentKscScienceCredit();
            if (pendingCredit <= 0.0)
                return targetScience;

            double adjustedTarget = targetScience + pendingCredit;
            if (adjustedTarget > (double)currentScience)
                adjustedTarget = (double)currentScience;

            if (adjustedTarget > targetScience)
            {
                ParsekLog.Verbose(Tag,
                    $"PatchScience: holding forward {pendingCredit.ToString("F1", IC)} pending science earning " +
                    $"(current={currentScience.ToString("F1", IC)}, " +
                    $"ledgerTarget={targetScience.ToString("F1", IC)}, " +
                    $"adjustedTarget={adjustedTarget.ToString("F1", IC)})");
            }

            return adjustedTarget;
        }

        /// <summary>
        /// Keeps a recent stock strategy currency-exchange science debit (Patents
        /// Licensing) authoritative while the matching
        /// <see cref="GameActionType.StrategyScienceDebit"/> ledger row is still catching
        /// up. Same shape and same direction as
        /// <see cref="AdjustSciencePatchTargetForPendingRecentTechResearch"/>, but the
        /// race it covers is the LIVE-RECORDER one: KSP debits the pool the moment the
        /// exchange fires and the ledger row only lands at recording commit, so without
        /// this the patch would refund the traded-away science on every recalc in between
        /// (and the drawdown guard would clamp + toast the player each time).
        /// </summary>
        /// <param name="preStrategyRunning">The running balance with the two sibling pending
        /// adjusters folded in and this one not yet applied (see
        /// <see cref="ComputePreStrategyPendingAdjustedRunningScience"/>). Feeds
        /// <c>LedgerOrchestrator.ResolveEffectiveUnmatchableStrategyScienceBaseline</c>'s
        /// overhang test, so the target adjuster and the guard discriminator resolve the
        /// same pending amount on a pre-capture-fix save.</param>
        internal static double AdjustSciencePatchTargetForPendingStrategyScienceDebit(
            double targetScience,
            float currentScience,
            double preStrategyRunning)
        {
            if (targetScience <= (double)currentScience)
                return targetScience;

            double pendingDebit = LedgerOrchestrator.GetPendingUncommittedStrategyScienceDebit(
                preStrategyRunning, currentScience);
            if (pendingDebit <= 0.0)
                return targetScience;

            double adjustedTarget = targetScience - pendingDebit;
            if (adjustedTarget < (double)currentScience)
                adjustedTarget = (double)currentScience;

            if (adjustedTarget < targetScience)
            {
                ParsekLog.Verbose(Tag,
                    $"PatchScience: holding back {pendingDebit.ToString("F1", IC)} pending strategy-exchange science " +
                    $"(current={currentScience.ToString("F1", IC)}, " +
                    $"ledgerTarget={targetScience.ToString("F1", IC)}, " +
                    $"adjustedTarget={adjustedTarget.ToString("F1", IC)})");
            }

            return adjustedTarget;
        }

        /// <summary>
        /// The pending-adjusted realized running science balance — the exact value
        /// <see cref="PatchScience"/> uses as its drawdown-guard discriminator (the raw
        /// <c>GetRunningScience()</c> folded with the three in-flight pending
        /// adjusters). Extracted so the rewind read-back guard reads a byte-identical
        /// value: comparing the recalc target against anything but the same realized
        /// running quantity would diverge by a transient ledger-catch-up credit / debit.
        /// </summary>
        /// <param name="currentLiveScience">The live science pool. Load-bearing since
        /// STRATEGY-PREFIX-HOLDBACK-PERMANENT: the strategy pending amount is resolved
        /// against how far the reconstruction already sits above live, so the discriminator
        /// cannot be computed without it. <see cref="PatchScience"/> passes the same
        /// <c>ResearchAndDevelopment.Instance.Science</c> it clamps against; the rewind
        /// read-back guard passes the same value so its byte-identity contract holds.</param>
        internal static double ComputePendingAdjustedRunningScience(
            ScienceModule science, double currentLiveScience)
        {
            return ComputePendingAdjustedRunningScience(
                ComputePreStrategyPendingAdjustedRunningScience(science), currentLiveScience);
        }

        /// <summary>
        /// The same fold over an ALREADY-COMPUTED basis. <see cref="PatchScience"/> uses this
        /// overload so the basis it hands the target adjuster and the basis behind its
        /// drawdown-guard discriminator are literally the same local, rather than two
        /// evaluations that merely agree today: the two sibling helpers folded into the basis
        /// are recent-UT-WINDOW queries, exactly the class of thing that can start returning
        /// a different number when re-derived a few statements later.
        /// </summary>
        internal static double ComputePendingAdjustedRunningScience(
            double preStrategyRunning, double currentLiveScience)
        {
            return preStrategyRunning
                - LedgerOrchestrator.GetPendingUncommittedStrategyScienceDebit(
                    preStrategyRunning, currentLiveScience);
        }

        /// <summary>
        /// The running science balance with the two SIBLING pending adjusters folded in and
        /// the strategy one not yet applied. This is the basis the strategy pending amount
        /// is resolved against (its overhang-vs-live test) and the value it is then
        /// subtracted from, so both consumers - the patch-target adjuster and the guard
        /// discriminator - must derive their pending amount from THIS number or they will
        /// disagree on a pre-capture-fix save.
        /// </summary>
        internal static double ComputePreStrategyPendingAdjustedRunningScience(ScienceModule science)
        {
            return science.GetRunningScience()
                + LedgerOrchestrator.GetPendingRecentKscScienceCredit()
                - LedgerOrchestrator.GetPendingRecentKscTechResearchScienceDebit();
        }

        /// <summary>
        /// Pure decision core for one subject inside <see cref="PatchPerSubjectScience"/>
        /// (apply-boundary test seam, audit gap 1 / rec #5). Takes the live subject science +
        /// scienceCap as PARAMETERS instead of reading the live <c>ScienceSubject</c>. This is
        /// the audit's HIGHEST-risk apply path: per-subject credited science is written
        /// UNCLAMPED (the drawdown guard does NOT cover it), so the resulting value equals the
        /// target verbatim even on a drawdown. The scientific-value (diminishing-returns)
        /// factor is recomputed from the target and the cap, floored at 0. Pure: no singletons.
        /// </summary>
        internal readonly struct SubjectSciencePatchDecision
        {
            /// <summary>True when |currentScience - targetScience| &gt; the 0.001f per-subject threshold.</summary>
            internal readonly bool ShouldWrite;
            /// <summary>The credited-science target written verbatim to the subject (UNCLAMPED).</summary>
            internal readonly float TargetScience;
            /// <summary>The diminishing-returns factor: 1 - target/cap when cap &gt; 0 (floored at 0), else 0.</summary>
            internal readonly float ScientificValue;

            internal SubjectSciencePatchDecision(bool shouldWrite, float targetScience, float scientificValue)
            {
                ShouldWrite = shouldWrite;
                TargetScience = targetScience;
                ScientificValue = scientificValue;
            }
        }

        /// <summary>
        /// Resolves one subject's per-subject science patch outcome. Mirrors the inner loop of
        /// <see cref="PatchPerSubjectScience"/> exactly: write iff the live value differs from
        /// the target by more than 0.001, write the target UNCLAMPED, and recompute
        /// scientificValue = 1 - target/scienceCap (cap &gt; 0) floored at 0, else 0. Pure.
        /// </summary>
        internal static SubjectSciencePatchDecision ResolveSubjectSciencePatch(
            float currentScience, float targetScience, float scienceCap)
        {
            bool shouldWrite = Math.Abs(currentScience - targetScience) > 0.001f;
            float scientificValue;
            if (scienceCap > 0f)
                scientificValue = 1f - (targetScience / scienceCap);
            else
                scientificValue = 0f;
            if (scientificValue < 0f) scientificValue = 0f;
            return new SubjectSciencePatchDecision(shouldWrite, targetScience, scientificValue);
        }

        /// <summary>
        /// Outcome of the "the ledger knows this subject but R&amp;D does not" branch of
        /// <see cref="PatchPerSubjectScience"/>. Pure: no singletons, no reflection.
        /// </summary>
        internal readonly struct MissingSubjectCreationDecision
        {
            /// <summary>True when a placeholder <c>ScienceSubject</c> should be created and registered.</summary>
            internal readonly bool ShouldCreate;
            /// <summary>The credited-science target the created subject is patched to.</summary>
            internal readonly float TargetScience;
            /// <summary>
            /// The cap the created subject is constructed with. Never below
            /// <see cref="TargetScience"/>, so stock's diminishing-returns math stays sane
            /// even when the recorded <c>subjectMaxValue</c> is missing or nonsensical.
            /// </summary>
            internal readonly float ScienceCap;
            /// <summary>Grep-stable reason token; "create" when <see cref="ShouldCreate"/> is true.</summary>
            internal readonly string Reason;

            internal MissingSubjectCreationDecision(
                bool shouldCreate, float targetScience, float scienceCap, string reason)
            {
                ShouldCreate = shouldCreate;
                TargetScience = targetScience;
                ScienceCap = scienceCap;
                Reason = reason;
            }
        }

        /// <summary>
        /// Decides whether a ledger-known subject that R&amp;D has no row for should be
        /// CREATED rather than silently skipped.
        ///
        /// <para>
        /// Why this exists: a Re-Fly restores R&amp;D from the rewind-point quicksave, so a
        /// subject first earned AFTER the rewind point by a branch that SURVIVES the merge is
        /// absent from the live subject table while the surviving ledger still credits it.
        /// The old code counted that as <c>notFound</c> and skipped, leaving the Science
        /// Archive under-reporting the subject. The player then re-runs the experiment, stock
        /// awards from a fresh zero-science subject, the ledger clamps the new row to the
        /// remaining headroom, and the next non-authoritative recalc's drawdown guard clamps
        /// the pool UP to the live value — the over-award becomes permanent.
        /// </para>
        ///
        /// <para>
        /// Only a POSITIVE ledger target is created. A zero/absent target means "this subject
        /// should read as un-earned", and an absent live row already reads that way — creating
        /// an empty placeholder would add a Science Archive entry the player never earned.
        /// </para>
        ///
        /// Pure: no singletons, no reflection, no Unity.
        /// </summary>
        internal static MissingSubjectCreationDecision ResolveMissingSubjectCreation(
            string subjectId, bool hasLedgerState, double creditedTotal, double maxValue)
        {
            if (string.IsNullOrEmpty(subjectId))
                return new MissingSubjectCreationDecision(false, 0f, 0f, "no-subject-id");
            if (!hasLedgerState)
                return new MissingSubjectCreationDecision(false, 0f, 0f, "no-ledger-state");
            if (double.IsNaN(creditedTotal) || double.IsInfinity(creditedTotal))
                return new MissingSubjectCreationDecision(false, 0f, 0f, "non-finite-target");
            if (creditedTotal <= 0.0)
                return new MissingSubjectCreationDecision(false, 0f, 0f, "zero-target");

            float target = (float)creditedTotal;
            float cap = (double.IsNaN(maxValue) || double.IsInfinity(maxValue))
                ? target
                : (float)maxValue;
            if (cap < target)
                cap = target;
            return new MissingSubjectCreationDecision(true, target, cap, "create");
        }

        /// <summary>
        /// Reflection handle on <c>ResearchAndDevelopment.scienceSubjects</c>, the private
        /// <c>Dictionary&lt;string, ScienceSubject&gt;</c> backing <c>GetSubjectByID</c>.
        /// Verified against the decompiled KSP 1.12.5 <c>Assembly-CSharp</c>: the field is
        /// <c>private Dictionary&lt;string, ScienceSubject&gt; scienceSubjects</c>, and the only
        /// insert seam (<c>getScienceSubject</c>) is private and MUTATES an existing row's
        /// title/cap/dataScale, which is not what a re-assert wants.
        /// </summary>
        private static Dictionary<string, ScienceSubject> GetScienceSubjectsDictionary()
        {
            if (ResearchAndDevelopment.Instance == null)
                return null;

            var field = typeof(ResearchAndDevelopment).GetField(
                "scienceSubjects",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null)
            {
                EmitScienceSubjectsReflectionWarnOnce(
                    "field typeof(ResearchAndDevelopment).GetField(\"scienceSubjects\") returned null");
                return null;
            }

            var value = field.GetValue(ResearchAndDevelopment.Instance)
                as Dictionary<string, ScienceSubject>;
            if (value == null)
            {
                EmitScienceSubjectsReflectionWarnOnce(
                    "field.GetValue returned null or wrong type for \"scienceSubjects\"");
            }
            return value;
        }

        // Internal so xUnit can drive the one-shot warn path without booting R&D singletons.
        internal static void EmitScienceSubjectsReflectionWarnOnce(string detail)
        {
            if (scienceSubjectsReflectionWarnEmitted)
                return;
            scienceSubjectsReflectionWarnEmitted = true;
            ParsekLog.Warn(Tag,
                "PatchPerSubjectScience: reflection lookup for ResearchAndDevelopment.scienceSubjects " +
                $"failed (one-shot per session) — {detail}. Ledger-known subjects that R&D has no row " +
                "for cannot be created; they fall back to being counted as notFound and skipped.");
        }

        /// <summary>
        /// Builds a placeholder <c>ScienceSubject</c> for <paramref name="subjectId"/> and inserts
        /// it into R&amp;D's private subject dictionary. Returns null when the reflection handle is
        /// unavailable or the insert throws.
        ///
        /// <para>
        /// The placeholder carries <c>dataScale</c>/<c>subjectValue</c> of 1 because the ledger
        /// does not record them. That is safe: the first real re-run of the experiment routes
        /// through <c>ResearchAndDevelopment.GetExperimentSubject</c> -&gt;
        /// <c>getScienceSubject</c>, which — verified in the decompiled 1.12.5 source — refreshes
        /// <c>title</c>/<c>scienceCap</c>/<c>subjectValue</c>/<c>dataScale</c> from the freshly
        /// built subject while PRESERVING the <c>science</c> we re-asserted. Stock self-heals the
        /// placeholder's cosmetic fields; the credited total is what we own.
        /// </para>
        /// </summary>
        private static ScienceSubject TryCreateAndRegisterSubject(
            string subjectId, MissingSubjectCreationDecision decision)
        {
            var dictionary = GetScienceSubjectsDictionary();
            if (dictionary == null)
                return null;

            try
            {
                string title = null;
                try
                {
                    title = ScienceUtil.GenerateLocalizedTitle(subjectId);
                }
                catch (Exception titleEx)
                {
                    ParsekLog.Verbose(Tag,
                        $"PatchPerSubjectScience: GenerateLocalizedTitle threw for subjectId='{subjectId}': " +
                        $"{titleEx.Message} — falling back to the raw id");
                }
                if (string.IsNullOrEmpty(title))
                    title = subjectId;

                var created = new ScienceSubject(subjectId, title, 1f, 1f, decision.ScienceCap);
                dictionary[subjectId] = created;
                return created;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"PatchPerSubjectScience: creating a placeholder subject for subjectId='{subjectId}' " +
                    $"threw: {ex.Message} — skipping (subject stays absent from the Science Archive)");
                return null;
            }
        }

        /// <summary>
        /// Patches per-subject credited totals in KSP's R&amp;D system to match the module's
        /// derived state. After rewind, the Science Archive must show correct per-subject progress.
        /// Updates both the credited science and the scientific value (diminishing returns factor).
        /// A ledger-known subject with a positive credited total that R&amp;D has no row for is
        /// CREATED (see <see cref="ResolveMissingSubjectCreation"/>) rather than skipped.
        /// No-op if ResearchAndDevelopment.Instance is null (sandbox mode).
        /// </summary>
        internal static void PatchPerSubjectScience(ScienceModule science)
        {
            if (science == null)
            {
                ParsekLog.Warn(Tag, "PatchPerSubjectScience: null ScienceModule — skipping");
                return;
            }

            if (ResearchAndDevelopment.Instance == null)
            {
                VerboseStablePatchState("patch-skip|subject-science|rnd", "rnd-null",
                    "PatchPerSubjectScience: ResearchAndDevelopment.Instance is null — skipping");
                return;
            }

            var subjects = science.GetAllSubjects();
            var subjectIds = BuildSubjectIdsForPatch(
                subjects,
                GameStateStore.GetCommittedScienceSubjectIds());
            int patchedSubjects = 0;
            int skippedSubjects = 0;
            int notFoundSubjects = 0;
            int clearedSubjects = 0;
            int createdSubjects = 0;
            var changedSubjects = new List<string>();

            foreach (var subjectId in subjectIds)
            {
                ScienceModule.SubjectState state;
                bool hasCurrentState = subjects.TryGetValue(subjectId, out state);

                var kspSubject = ResearchAndDevelopment.GetSubjectByID(subjectId);
                if (kspSubject == null)
                {
                    // The ledger credits this subject but R&D has no row for it — the
                    // post-rewind "surviving branch earned it after the RP" case. Create the
                    // row so the re-assert below lands; a non-creatable case degrades to the
                    // historical notFound skip.
                    var creation = ResolveMissingSubjectCreation(
                        subjectId,
                        hasCurrentState,
                        hasCurrentState ? state.CreditedTotal : 0.0,
                        hasCurrentState ? state.MaxValue : 0.0);
                    if (creation.ShouldCreate)
                        kspSubject = TryCreateAndRegisterSubject(subjectId, creation);

                    if (kspSubject == null)
                    {
                        notFoundSubjects++;
                        continue;
                    }
                    createdSubjects++;
                }

                float targetScience = hasCurrentState ? (float)state.CreditedTotal : 0f;
                SubjectSciencePatchDecision decision = ResolveSubjectSciencePatch(
                    kspSubject.science, targetScience, kspSubject.scienceCap);
                if (decision.ShouldWrite)
                {
                    // Read the live value BEFORE the write so the log captures old->new.
                    float oldScience = kspSubject.science;
                    kspSubject.science = decision.TargetScience;
                    kspSubject.scientificValue = decision.ScientificValue;
                    patchedSubjects++;
                    if (!hasCurrentState)
                        clearedSubjects++;
                    changedSubjects.Add(
                        $"{subjectId}:{oldScience.ToString("R", IC)}->{targetScience.ToString("R", IC)}");

                    // LedgerTrace Tier-B (per-subject change line) + Tier-C (read-back
                    // reconcile of the UNCLAMPED gap-1 per-subject write).
                    if (LedgerTrace.IsEnabled)
                    {
                        LedgerTrace.EmitOnChange("subject-science", subjectId,
                            $"{oldScience.ToString("R", IC)}->{targetScience.ToString("R", IC)}");

                        double subTol = LedgerTrace.ResourceTolerance("subject-science");
                        if (LedgerTrace.IsSubjectScienceDrift(targetScience, kspSubject.science, subTol))
                            LedgerTrace.EmitAnomaly("subject-science", subjectId, "ledger-vs-truth",
                                $"target={LedgerTrace.FormatDouble(targetScience, "F3")} " +
                                $"actual={LedgerTrace.FormatDouble(kspSubject.science, "F3")} " +
                                $"delta={LedgerTrace.FormatDouble(targetScience - kspSubject.science, "F3")} " +
                                $"tol={LedgerTrace.FormatDouble(subTol, "R")}");
                    }
                }
                else
                {
                    skippedSubjects++;
                }
            }

            string changedSubjectsSample =
                ComposeBoundedIdentitySample(changedSubjects, IdentitySampleCap);
            ParsekLog.Info(Tag,
                $"PatchPerSubjectScience: patched={patchedSubjects.ToString(IC)}, " +
                $"cleared={clearedSubjects.ToString(IC)}, " +
                $"created={createdSubjects.ToString(IC)}, " +
                $"skipped={skippedSubjects.ToString(IC)}, " +
                $"notFound={notFoundSubjects.ToString(IC)}, " +
                $"totalSubjects={subjectIds.Count}" +
                (changedSubjectsSample.Length > 0
                    ? $", changedSubjects=[{changedSubjectsSample}]"
                    : string.Empty));

            if (changedSubjects.Count > 0)
                ParsekLog.Verbose(Tag,
                    $"PatchPerSubjectScience: all {changedSubjects.Count.ToString(IC)} changed subject(s): " +
                    string.Join(", ", changedSubjects));
        }

        internal static HashSet<string> BuildSubjectIdsForPatch(
            IReadOnlyDictionary<string, ScienceModule.SubjectState> currentSubjects,
            IEnumerable<string> previouslyCommittedSubjectIds)
        {
            var subjectIds = new HashSet<string>(StringComparer.Ordinal);
            if (currentSubjects != null)
            {
                foreach (var kvp in currentSubjects)
                {
                    if (!string.IsNullOrEmpty(kvp.Key))
                        subjectIds.Add(kvp.Key);
                }
            }
            if (previouslyCommittedSubjectIds != null)
            {
                foreach (var subjectId in previouslyCommittedSubjectIds)
                {
                    if (!string.IsNullOrEmpty(subjectId))
                        subjectIds.Add(subjectId);
                }
            }
            return subjectIds;
        }
    }
}
