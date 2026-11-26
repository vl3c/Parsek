using System;
using System.Collections.Generic;
using Parsek.InGameTests;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// One picture side of an FX case (design-part-ghost-parity.md E.3): the real part, or one
    /// ghost, with how its pixels are taken. A control may show one part on two sides (an intact
    /// and a broken ghost, or the real part's even and odd samples), so a side is named by its
    /// atlas / sidecar key (<see cref="PartParityFxRules.Sides"/>) and not by what it shows.
    /// </summary>
    internal sealed class PartParityFxSide
    {
        /// <summary>One of <see cref="PartParityFxRules.Sides"/>.</summary>
        internal string Name;

        /// <summary>The ghost this side shows; null for the real part.</summary>
        internal GhostCoOrbitalRig.LiveGhost Ghost;

        /// <summary>The route this side is compared to the <c>real</c> side under, null for the
        /// real side itself (<c>event</c>, <c>start</c>, <c>ghost-all</c>).</summary>
        internal string Route;

        /// <summary>Renderers this side never shows (an rcs ghost's systems of nozzles stock did
        /// not fire).</summary>
        internal readonly HashSet<Renderer> Excluded = new HashSet<Renderer>();

        /// <summary>The broken plume controls: <c>ctl-fx-plume-hue</c>, <c>ctl-fx-smoke-dropped</c>.</summary>
        internal bool RotateHue;
        internal bool HideSmoke;

        /// <summary><c>ctl-empty</c>: nothing of this side is shown.</summary>
        internal bool HideAll;

        /// <summary><c>ctl-fx-real-vs-real</c>: this side takes only the samples of one parity
        /// (-1 = every sample).</summary>
        internal int Parity = -1;

        /// <summary>When the side's clock started: the real press, or the ghost's prime.</summary>
        internal float StartRealtime;

        // ----- warm-up (E.2) -----
        internal readonly List<KeyValuePair<float, double>> Signal = new List<KeyValuePair<float, double>>();
        internal string SignalName;
        internal double LongestLifetime;
        internal bool Settled;
        internal double? WarmupS;
        internal double? PlateauWaitS;

        // ----- the accumulation (E.3), per view and layer -----
        internal readonly Dictionary<string, PartParityFxSampleSet> Sets =
            new Dictionary<string, PartParityFxSampleSet>(StringComparer.Ordinal);

        /// <summary>Particles read per sample frame, part space.</summary>
        internal readonly List<IList<PartParityFxParticle>> ParticleSamples = new List<IList<PartParityFxParticle>>();

        /// <summary>The particles of the warm-up ticks, for the far framing.</summary>
        internal readonly List<IList<PartParityFxParticle>> WarmupParticles = new List<IList<PartParityFxParticle>>();

        /// <summary>The structure and renderer keys recorded at the capture.</summary>
        internal readonly Dictionary<string, List<string>> LayerKeys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        internal List<string> Inventory = new List<string>();

        /// <summary>Index-parallel to <see cref="Inventory"/>: the system's renderer is enabled
        /// and active.</summary>
        internal readonly List<bool> InventoryRendered = new List<bool>();

        internal static string SetKey(string view, string layer) => view + "|" + layer;
    }

    /// <summary>
    /// Everything one FX case owns beyond its state run (design E): the option it drives, the
    /// hold, the engine / RCS / heat drive, the sides, the framing, the accumulated pictures and
    /// the numbers its sidecar writes. Null on every other case.
    /// </summary>
    internal sealed class PartParityFxRun
    {
        internal PartParityFxOption Option;

        /// <summary>The control this case stands for (<c>ctl-fx-...</c>, <c>ctl-heat-...</c>,
        /// <c>ctl-plate-...</c>, the two coverage ones); null for a part case.</summary>
        internal string Control;

        /// <summary>The plate case: P6's own light press, its plate picture instead of the body.</summary>
        internal bool Plate => Option != null && Option.Family == PartParityFxRules.FamilyLight;

        /// <summary>A stochastic case (plume, rcs): layers, warm-up and accumulation.</summary>
        internal bool Stochastic => Option != null && (Option.Family == PartParityFxRules.FamilyPlume
                                                       || Option.Family == PartParityFxRules.FamilyRcs);

        internal PartParityFxHold Hold;

        /// <summary>Where an air case is held; null in orbit.</summary>
        internal PartParityHeldSlot HeldSlot;

        // ----- the drive (fx.drive, fx.engineMode, fx.rcs) -----
        internal ModuleEngines Engine;
        internal ModuleRCS Rcs;
        internal PartModule Heat;
        internal string EngineMode;
        internal bool CheatOn;
        internal bool? DriveIgnited;
        internal double? DriveFinalThrust;
        internal double? DriveCurrentThrottle;
        internal double? DriveRecordedPower;
        internal double? DriveMainThrottle;
        internal bool? DriveIndependentThrottle;

        /// <summary>A main-throttle case's input while it is armed; null before and after.</summary>
        internal FlightInputCallback MainThrottleInput;

        /// <summary>Its vessel's throttle right after the release.</summary>
        internal float? MainThrottleAfter;

        /// <summary>The RCS poll's own debounce frame counts (the recorder's <c>rcsActiveFrameCount</c>).</summary>
        internal readonly Dictionary<ulong, int> RcsFrameCounts = new Dictionary<ulong, int>();
        internal readonly List<PartEvent> EventBuffer = new List<PartEvent>();

        /// <summary>The engine poll's own settled-throttle state (the recorder's <c>engineThrottleStill</c>).</summary>
        internal readonly Dictionary<ulong, EngineThrottleStill> EngineStill = new Dictionary<ulong, EngineThrottleStill>();

        internal FlightInputCallback RcsInput;
        internal PartParityRcsChoice RcsChoice;
        internal readonly List<int> RcsFired = new List<int>();
        internal readonly List<int> RcsPredicted = new List<int>();
        internal int RcsNozzles;
        internal int RcsGhostHidden;
        internal double RcsPowerRatio = double.NaN;

        // ----- geometry (part-local) -----
        internal double[] Nozzle;
        internal double[] Exhaust;
        internal List<double[]> ThrustPoints = new List<double[]>();
        internal double NozzleDiameterM = double.NaN;
        internal PartParityAabb NearBox;
        internal PartParityAabb BottomBox;
        internal PartParityAabb FarBox;
        internal bool FramingClamped;

        // ----- sides, results -----
        internal readonly List<PartParityFxSide> Sides = new List<PartParityFxSide>();
        internal GhostCoOrbitalRig.LiveGhost BrokenGhost;
        internal float WarmupStartRealtime;
        internal bool WarmupDone;
        internal float LastPrimeRealtime;
        internal int SampleCount;
        internal readonly Dictionary<string, PartParityFxAxis> Axes = new Dictionary<string, PartParityFxAxis>(StringComparer.Ordinal);
        internal readonly List<PartParityFxViewResult> Results = new List<PartParityFxViewResult>();
        internal readonly Dictionary<string, List<byte[]>> Strip = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);
        internal int CullingMask;
        internal bool Captured;
        internal int TilePx;
        internal float NextTickRealtime;
        internal ParticleSystem.Particle[] ParticleBuffer;

        /// <summary>Every side's whole-set tile per FX atlas row and view, kept until the atlas is
        /// written.</summary>
        internal readonly Dictionary<string, Dictionary<string, byte[]>> AtlasTiles =
            new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);

        // ----- structure (E.4): each missing / extra entry is a JSON object {key, rendered}
        // (F4 reads rendered=false as an asset that draws nothing) -----
        internal readonly List<PartParityFxStructureEntry> FxMissingOnGhost = new List<PartParityFxStructureEntry>();
        internal readonly List<PartParityFxStructureEntry> FxExtraOnGhost = new List<PartParityFxStructureEntry>();
        internal List<string> FxDiffers = new List<string>();
        internal List<string> LightsDiffer = new List<string>();

        // ----- the plate (E.2) -----
        internal GameObject PlateObject;
        internal Material PlateMaterial;
        internal double? PlateLitPct;
        internal string PlateLightType;
        internal double? PlateRange;
        internal double? PlateSpotAngle;
        internal int? PlateCullingMask;
        internal bool PlateLit;
        internal bool PlateViewsFail;

        /// <summary>The control's evidence, written as a flag (<see cref="PartParityFxControls"/>).</summary>
        internal PartParityFxControlEvidence Evidence;

        /// <summary>Every side's light-transform read at the capture.</summary>
        internal bool GhostLightsOff;

        internal PartParityFxSide Side(string name)
        {
            foreach (PartParityFxSide s in Sides) if (s.Name == name) return s;
            return null;
        }
    }
}
