using System;
using System.Globalization;

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
    /// 1, 2, 4, 5, 6) and picks <c>(int)(Clamp01(power) * (n - 1))</c>. The sizes and times below
    /// are measured off the ground pictures of each prefab (throwaway rig FX9-4, 2026-11-27: each
    /// kept stock copy set off on the grass 57 m from a fixed camera, extents and colours read from
    /// the frame difference every 0.25 s):
    /// <list type="bullet">
    /// <item>0 (Thud): a grey puff about 7 m wide that fades by 2 s; no fire.</item>
    /// <item>1: a dark grey puff about 10 m wide and fast debris; gone by 1.5 s; no fire.</item>
    /// <item>2: a yellow fireball growing to 15 m over 0.5 s, turning red-brown, then a dark grey
    /// cloud about 20 m wide that fades by 2 s.</item>
    /// <item>3: the same with an 18 m fireball, many streaks and the cloud lasting to 2.5 s.</item>
    /// <item>4: a white flash growing to 21 m in 0.25 s, then a lighter grey cloud gone by 1.5 s.</item>
    /// <item>5: a white starburst about 40 m wide, a 25 m orange fireball at 0.5 s, then a dark
    /// cloud over 30 m wide gone by 1.5 s.</item>
    /// <item>6: a yellow fireball growing to 30 m over 0.75 s, many streaks, then a dark grey cloud
    /// about 20 m wide fading by 3 s.</item>
    /// </list>
    /// Diameters are the visible size in metres, fixed per tier as stock's prefabs are: the craft's
    /// size only picks the tier through the power. Smoke is a mix of dark and lighter grey.
    /// </summary>
    internal sealed class FallbackExplosionLook
    {
        /// <summary>The number of explosion prefabs stock's FXMonger holds (KSP 1.12.5).</summary>
        internal const int StockPrefabCount = 7;

        /// <summary>Stock's prefab index for this power, 0 (Thud) to 6.</summary>
        internal int Tier;

        /// <summary>Visible diameter of the white opening flash in metres; 0 = no flash.</summary>
        internal float FlashDiameter;
        internal float FlashLifetime;

        /// <summary>Visible diameter of the yellow fireball in metres; 0 = no fire.</summary>
        internal float FireDiameter;
        internal float FireLifetime;
        internal int FireCount;

        /// <summary>White streak sparks; 0 = none.</summary>
        internal int SparkCount;
        internal float SparkSpeedMin;
        internal float SparkSpeedMax;
        internal float SparkLifetime;

        /// <summary>Visible diameter of the smoke cloud in metres.</summary>
        internal float SmokeDiameter;
        internal int SmokeCount;
        internal float SmokeLifetimeMin;
        internal float SmokeLifetimeMax;
        /// <summary>The grey levels the smoke puffs are picked between (0 black, 1 white).</summary>
        internal float SmokeDarkGrey;
        internal float SmokeLightGrey;

        internal bool HasFlash => FlashDiameter > 0f;
        internal bool HasFire => FireDiameter > 0f && FireCount > 0;
        internal bool HasSparks => SparkCount > 0;

        /// <summary>Seconds until the whole effect is gone (the longest part plus a margin).</summary>
        internal float DestroyAfterSeconds =>
            Math.Max(Math.Max(SmokeLifetimeMax, SparkLifetime), Math.Max(FireLifetime, FlashLifetime)) + 1f;

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
            var look = new FallbackExplosionLook { Tier = tier, SmokeDarkGrey = 0.24f, SmokeLightGrey = 0.5f };
            switch (tier)
            {
                case 0:   // Thud: a grey puff, a few small sparks
                    look.SparkCount = 12; look.SparkSpeedMin = 2f; look.SparkSpeedMax = 5f; look.SparkLifetime = 1f;
                    look.SmokeDiameter = 7f; look.SmokeCount = 18; look.SmokeLifetimeMin = 1.4f; look.SmokeLifetimeMax = 2f;
                    look.SmokeDarkGrey = 0.34f; look.SmokeLightGrey = 0.6f;
                    break;
                case 1:   // a dark grey puff, fast streaks
                    look.SparkCount = 40; look.SparkSpeedMin = 8f; look.SparkSpeedMax = 18f; look.SparkLifetime = 1.3f;
                    look.SmokeDiameter = 10f; look.SmokeCount = 22; look.SmokeLifetimeMin = 1f; look.SmokeLifetimeMax = 1.5f;
                    break;
                case 2:   // a fireball, streaks, a dark grey cloud
                    look.FlashDiameter = 7f; look.FlashLifetime = 0.2f;
                    look.FireDiameter = 15f; look.FireLifetime = 0.75f; look.FireCount = 30;
                    look.SparkCount = 25; look.SparkSpeedMin = 6f; look.SparkSpeedMax = 14f; look.SparkLifetime = 1.2f;
                    look.SmokeDiameter = 18f; look.SmokeCount = 30; look.SmokeLifetimeMin = 1.5f; look.SmokeLifetimeMax = 2f;
                    break;
                case 3:   // a bigger fireball, many streaks, a lingering cloud
                    look.FlashDiameter = 8f; look.FlashLifetime = 0.2f;
                    look.FireDiameter = 18f; look.FireLifetime = 0.8f; look.FireCount = 34;
                    look.SparkCount = 60; look.SparkSpeedMin = 8f; look.SparkSpeedMax = 16f; look.SparkLifetime = 1.8f;
                    look.SmokeDiameter = 19f; look.SmokeCount = 34; look.SmokeLifetimeMin = 2f; look.SmokeLifetimeMax = 2.6f;
                    break;
                case 4:   // a white flash, a brief small fire, a lighter grey cloud, no streaks
                    look.FlashDiameter = 20f; look.FlashLifetime = 0.35f;
                    look.FireDiameter = 10f; look.FireLifetime = 0.35f; look.FireCount = 14;
                    look.SmokeDiameter = 20f; look.SmokeCount = 32; look.SmokeLifetimeMin = 1f; look.SmokeLifetimeMax = 1.3f;
                    look.SmokeDarkGrey = 0.34f; look.SmokeLightGrey = 0.62f;
                    break;
                case 5:   // a huge white flash, an orange fireball, a big dark cloud
                    look.FlashDiameter = 38f; look.FlashLifetime = 0.35f;
                    look.FireDiameter = 25f; look.FireLifetime = 0.8f; look.FireCount = 40;
                    look.SparkCount = 12; look.SparkSpeedMin = 10f; look.SparkSpeedMax = 20f; look.SparkLifetime = 1f;
                    look.SmokeDiameter = 30f; look.SmokeCount = 40; look.SmokeLifetimeMin = 1f; look.SmokeLifetimeMax = 1.4f;
                    break;
                default:  // 6: a big long fireball, many streaks, a lingering dark cloud
                    look.FlashDiameter = 12f; look.FlashLifetime = 0.3f;
                    look.FireDiameter = 28f; look.FireLifetime = 1.1f; look.FireCount = 45;
                    look.SparkCount = 90; look.SparkSpeedMin = 10f; look.SparkSpeedMax = 20f; look.SparkLifetime = 2.2f;
                    look.SmokeDiameter = 22f; look.SmokeCount = 40; look.SmokeLifetimeMin = 2.2f; look.SmokeLifetimeMax = 2.8f;
                    break;
            }
            return look;
        }

        /// <summary>One log-friendly line: tier and which parts draw.</summary>
        internal string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "tier={0} flash={1} fire={2} sparks={3} smoke={4:F0}m",
                Tier,
                HasFlash ? FlashDiameter.ToString("F0", CultureInfo.InvariantCulture) + "m" : "none",
                HasFire ? FireDiameter.ToString("F0", CultureInfo.InvariantCulture) + "m" : "none",
                SparkCount, SmokeDiameter);
        }
    }
}
