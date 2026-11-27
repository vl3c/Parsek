using System;

namespace Parsek
{
    /// <summary>
    /// What Parsek's hand-built ghost explosion draws for a blast of a given power, shaped after
    /// the stock explosion <c>FXMonger</c> would pick for the same power. The hand-built explosion
    /// is the last resort of <c>GhostVisualBuilder.SpawnFallbackExplosionFx</c>: it draws only
    /// when no silent copy of stock's explosions is kept yet (the Space Center opened before any
    /// flight in that session).
    ///
    /// Stock's <c>FXMonger.explosions</c> holds seven prefabs (KSP 1.12.5: Thud 0, Explosion 0,
    /// 1, 2, 4, 5, 6) and picks <c>(int)(Clamp01(power) * (n - 1))</c>. Their look, read off the
    /// part parity spike's frames of each prefab (P9 X0, 2026-11-27), and what each tier copies:
    /// <list type="bullet">
    /// <item>0 (Thud): a small grey puff and a few tiny sparks; no flash, no fire.</item>
    /// <item>1: a small grey puff and white streak sparks; no flash, no fire.</item>
    /// <item>2: a short small yellow flash with a little fire, streaks, a grey cloud.</item>
    /// <item>3: a flash, many white streaks, a grey cloud that lingers.</item>
    /// <item>4: a big yellow flash with a little fire that turns quickly into a grey cloud; no streaks.</item>
    /// <item>5: a big flash, orange flames, a grey cloud.</item>
    /// <item>6: an orange-white flash, fire, many white streaks, a lingering grey cloud.</item>
    /// </list>
    /// The sizes are fixed metres per tier, as stock's prefabs are: the craft's size only picks
    /// the tier through the power. Smoke is a translucent mix of dark and light grey.
    /// </summary>
    internal sealed class FallbackExplosionLook
    {
        /// <summary>The number of explosion prefabs stock's FXMonger holds (KSP 1.12.5).</summary>
        internal const int StockPrefabCount = 7;

        /// <summary>Stock's prefab index for this power, 0 (Thud) to 6.</summary>
        internal int Tier;

        /// <summary>Diameter of the opening flash in metres; 0 = no flash.</summary>
        internal float FlashSize;
        internal float FlashLifetime;

        /// <summary>Orange fire particles; 0 = no fire.</summary>
        internal int FlameCount;
        internal float FlameSize;
        internal float FlameLifetime;

        /// <summary>White streak sparks; 0 = none.</summary>
        internal int SparkCount;
        internal float SparkSpeedMin;
        internal float SparkSpeedMax;
        internal float SparkLifetime;

        internal int SmokeCount;
        internal float SmokeSizeMin;
        internal float SmokeSizeMax;
        internal float SmokeSpeedMax;
        internal float SmokeLifetimeMin;
        internal float SmokeLifetimeMax;

        internal bool HasFlash => FlashSize > 0f;
        internal bool HasFire => FlameCount > 0;
        internal bool HasSparks => SparkCount > 0;

        /// <summary>Seconds until the whole effect is gone (the longest part plus a margin).</summary>
        internal float DestroyAfterSeconds =>
            Math.Max(Math.Max(SmokeLifetimeMax, SparkLifetime), Math.Max(FlameLifetime, FlashLifetime)) + 1f;

        /// <summary>
        /// The power a hand-built explosion uses: the caller's FXMonger power, or, when that is
        /// NaN, the craft length over <see cref="GhostAudioPresets.VesselLengthPowerDivisor"/> as
        /// every ghost explosion site computes it.
        /// </summary>
        internal static double ResolvePower(double power, float vesselLength)
        {
            if (!double.IsNaN(power)) return power;
            if (float.IsNaN(vesselLength) || vesselLength <= 0f) return 0.0;
            return Math.Min(1.0, vesselLength / (double)GhostAudioPresets.VesselLengthPowerDivisor);
        }

        /// <summary>The tier for <paramref name="power"/>: stock's own prefab index rule.</summary>
        internal static int ResolveTier(double power)
        {
            int index = StockExplosionPrefabCache.ResolvePrefabIndex(power, StockPrefabCount);
            return index < 0 ? 0 : index;
        }

        /// <summary>The look of a hand-built explosion of <paramref name="power"/>.</summary>
        internal static FallbackExplosionLook Resolve(double power)
        {
            int tier = ResolveTier(power);
            var look = new FallbackExplosionLook { Tier = tier };
            switch (tier)
            {
                case 0:   // Thud: a small puff, a few tiny sparks
                    look.SparkCount = 12; look.SparkSpeedMin = 1.5f; look.SparkSpeedMax = 4f; look.SparkLifetime = 1.2f;
                    look.SmokeCount = 14; look.SmokeSizeMin = 0.8f; look.SmokeSizeMax = 1.6f;
                    look.SmokeSpeedMax = 0.8f; look.SmokeLifetimeMin = 1.5f; look.SmokeLifetimeMax = 2.5f;
                    break;
                case 1:   // a small puff, white streaks
                    look.SparkCount = 40; look.SparkSpeedMin = 5f; look.SparkSpeedMax = 11f; look.SparkLifetime = 1.4f;
                    look.SmokeCount = 16; look.SmokeSizeMin = 1f; look.SmokeSizeMax = 2f;
                    look.SmokeSpeedMax = 1f; look.SmokeLifetimeMin = 1.5f; look.SmokeLifetimeMax = 2.5f;
                    break;
                case 2:   // a short small flash, a little fire, streaks, a grey cloud
                    look.FlashSize = 3.5f; look.FlashLifetime = 0.35f;
                    look.FlameCount = 8; look.FlameSize = 1.8f; look.FlameLifetime = 0.6f;
                    look.SparkCount = 25; look.SparkSpeedMin = 4f; look.SparkSpeedMax = 9f; look.SparkLifetime = 1.2f;
                    look.SmokeCount = 26; look.SmokeSizeMin = 2f; look.SmokeSizeMax = 4f;
                    look.SmokeSpeedMax = 1.5f; look.SmokeLifetimeMin = 1.5f; look.SmokeLifetimeMax = 2.5f;
                    break;
                case 3:   // a flash, many streaks, a lingering grey cloud
                    look.FlashSize = 4.5f; look.FlashLifetime = 0.35f;
                    look.FlameCount = 10; look.FlameSize = 2.2f; look.FlameLifetime = 0.7f;
                    look.SparkCount = 60; look.SparkSpeedMin = 6f; look.SparkSpeedMax = 13f; look.SparkLifetime = 1.8f;
                    look.SmokeCount = 32; look.SmokeSizeMin = 2.5f; look.SmokeSizeMax = 4.5f;
                    look.SmokeSpeedMax = 1.8f; look.SmokeLifetimeMin = 2.5f; look.SmokeLifetimeMax = 3.5f;
                    break;
                case 4:   // a big yellow flash, a little fire, quickly a grey cloud, no streaks
                    look.FlashSize = 11f; look.FlashLifetime = 0.3f;
                    look.FlameCount = 6; look.FlameSize = 3f; look.FlameLifetime = 0.4f;
                    look.SmokeCount = 30; look.SmokeSizeMin = 4f; look.SmokeSizeMax = 7f;
                    look.SmokeSpeedMax = 2f; look.SmokeLifetimeMin = 1f; look.SmokeLifetimeMax = 1.6f;
                    break;
                case 5:   // a big flash, orange flames, a grey cloud
                    look.FlashSize = 14f; look.FlashLifetime = 0.35f;
                    look.FlameCount = 22; look.FlameSize = 4f; look.FlameLifetime = 0.9f;
                    look.SparkCount = 12; look.SparkSpeedMin = 8f; look.SparkSpeedMax = 16f; look.SparkLifetime = 1f;
                    look.SmokeCount = 32; look.SmokeSizeMin = 5f; look.SmokeSizeMax = 8f;
                    look.SmokeSpeedMax = 2.5f; look.SmokeLifetimeMin = 1.2f; look.SmokeLifetimeMax = 2f;
                    break;
                default:  // 6: an orange-white flash, fire, many streaks, a lingering grey cloud
                    look.FlashSize = 9f; look.FlashLifetime = 0.45f;
                    look.FlameCount = 18; look.FlameSize = 3.5f; look.FlameLifetime = 1f;
                    look.SparkCount = 90; look.SparkSpeedMin = 9f; look.SparkSpeedMax = 18f; look.SparkLifetime = 2.2f;
                    look.SmokeCount = 40; look.SmokeSizeMin = 4f; look.SmokeSizeMax = 7f;
                    look.SmokeSpeedMax = 2.5f; look.SmokeLifetimeMin = 2.5f; look.SmokeLifetimeMax = 3.5f;
                    break;
            }
            return look;
        }

        /// <summary>One log-friendly line: tier and which parts draw.</summary>
        internal string Describe()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "tier={0} flash={1} fire={2} sparks={3} smoke={4} size={5:F1}-{6:F1}m",
                Tier,
                HasFlash ? FlashSize.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "m" : "none",
                FlameCount, SparkCount, SmokeCount, SmokeSizeMin, SmokeSizeMax);
        }
    }
}
