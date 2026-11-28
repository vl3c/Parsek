using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace Parsek
{
    /// <summary>
    /// Draws stock's reentry and condensation effect on ghosts (part parity P9 Y1,
    /// docs/dev/design-part-ghost-parity.md G.7 "Y1 design"). One per flight scene, created by the
    /// first ghost that offers a sheath. It reuses stock's pieces: the <c>FXCamera</c> and
    /// <c>FXDepthCamera</c> cameras (copied, so stock's own stay untouched), their replacement
    /// shaders, the effect texture and both <c>AeroFXState</c> look sets on
    /// <c>AerodynamicsFX</c>. Its FX camera sees only <see cref="SheathLayer"/>, on which the ghost
    /// meshes are submitted with <c>Graphics.DrawMesh</c> for that camera alone, each with a
    /// <c>MaterialPropertyBlock</c> carrying the values stock's <c>FXCamera.OnPreRender</c> sets as
    /// globals, computed from the ghost's own conditions, and each ghost renders its own velocity
    /// depth map at stock's occlusion size. Every ghost wears its own fire even while stock's is
    /// on (owner ruling 2026-11-28 01:06); stock's FX camera and velocity camera would also paint
    /// the active vessel's fire on a ghost in their reach, so while they are on, the ghost
    /// renderers in reach are switched off (<c>Renderer.forceRenderingOff</c>) for those two
    /// cameras' renders only, through <see cref="StockFxMaskLedger{T}"/>. Stock's scene depth
    /// camera still sees the ghosts, so a ghost hull still hides the ship's flames behind it.
    /// Runs its LateUpdate after the ghosts' final placement (ParsekFlight's LateUpdate) and after
    /// stock's <c>AerodynamicsFX.LateUpdate</c>, which decides whether stock's own effect is on.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    internal sealed class GhostAeroFxRenderer : MonoBehaviour
    {
        /// <summary>
        /// The layer the ghost sheath submissions use. Unity's built-in layer 6 has no name in
        /// KSP and nothing in the flight scene sits on it, so the copied FX camera sees only the
        /// ghost meshes Parsek hands it.
        /// </summary>
        internal const int SheathLayer = 6;

        private static GhostAeroFxRenderer instance;
        private static bool constantsRead;
        private static AeroFxConstants cachedConstants = AeroFxConstants.StockDefaults;

        private struct Offer
        {
            public ReentryFxInfo info;
            public Transform root;
            public float fx;
            public float aeroState;
            public Vector3 flowDirection;   // unit surface velocity of the ghost
            public int recIdx;
            public bool isolated;
        }

        // FXCamera.velocityCam is private; it is the second stock camera a ghost is hidden from.
        private static readonly FieldInfo StockVelocityCamField = typeof(FXCamera).GetField(
            "velocityCam", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly List<Offer> offers = new List<Offer>();
        private int offersFrame = -1;
        // Every ghost the engine drove this frame, burning or not: stock's fire must paint none.
        private readonly List<GameObject> maskGhosts = new List<GameObject>();
        private int maskGhostsFrame = -1;
        private readonly List<Renderer> rendererScratch = new List<Renderer>();
        private readonly StockFxMaskLedger<Renderer> mask = new StockFxMaskLedger<Renderer>(
            r => r == null || r.forceRenderingOff,
            (r, off) => { if (r != null) r.forceRenderingOff = off; });
        private bool subscribed;
        private bool maskingLogged;
        // One depth map per ghost drawn this frame, at stock's occlusion size, pooled across frames.
        private readonly List<RenderTexture> depthPool = new List<RenderTexture>();
        private readonly List<float> depthPoolLastUsed = new List<float>();
        private int depthPoolResolution;
        private readonly List<MaterialPropertyBlock> blocks = new List<MaterialPropertyBlock>();

        // The sheaths a frame's decisions chose, submitted only once the cameras are set.
        private struct PreparedSheath
        {
            public Offer offer;
            public int slot;
            public AeroFxDrawValues values;
            public int lod;
            public float box;
            public float far;
            // The velocity camera's world-to-camera matrix for this slot's own depth map (the
            // shared camera moves on to the next ghost), read by a rig capture's probe.
            public Matrix4x4 worldToVelocity;
        }
        private readonly List<PreparedSheath> prepared = new List<PreparedSheath>();
        private int lastSubmitted;
        // Whether this frame's slot-0 sheath also set the FX values as globals (stock's camera off).
        private bool slot0SetGlobals;

        private AerodynamicsFX stockFx;
        private FXCamera stockCamera;
        private Camera stockCameraComponent;
        private FXDepthCamera stockDepth;
        private Camera stockVelocityCamera;
        private bool velocityCamMissingLogged;
        private Light stockLight;
        private Camera fxCamera;
        private Camera depthCamera;
        private Camera velocityCamera;
        private Light ghostLight;
        private AeroFxLook condensation;
        private AeroFxLook heat;
        private bool assetsReady;
        private float nextResolveTime;
        private int failedResolves;
        private string missingPiece;
        private bool missingWarned;
        private bool camerasOn;
        private int lightOwnerRecIdx = int.MinValue;
        // Main-thread CPU time of one drawing frame (velocity renders + submission), for the
        // frame line; GPU time is what the rig's on / off frame timing measures.
        private readonly System.Diagnostics.Stopwatch frameWatch = new System.Diagnostics.Stopwatch();

        // The layers stock's FX camera draws, read each frame; a ghost mesh on another layer
        // wears no fire, as on stock's own vessel.
        private int fxLayerMask = ~0;
        // Rig-only diagnostics (GhostAeroFxDiagnosticMode); play runs Normal and never captures.
        private GhostAeroFxDiagnosticMode diagMode = GhostAeroFxDiagnosticMode.Normal;
        private Action<GhostAeroFxDiagnosticCapture> pendingCapture;
        private string pendingCaptureLabel;
        private int pendingCaptureFrame;
        private Offer captureOffer;
        private bool haveCaptureOffer;
        private Material sanityMaterial;
        private bool sanityMaterialMissingLogged;
        // Cumulative renders of the two copies and the frames they were switched on: one render
        // of each per such frame is the healthy reading of the frame line.
        private int fxCopyRenders;
        private int depthCopyRenders;
        private int framesCopiesOn;
        private bool cameraStateLogged;
        private bool fxCopyReenabledLogged;

        /// <summary>
        /// The <c>AerodynamicsFX</c> constants from the loaded <c>PhysicsGlobals</c>, read once a
        /// game has loaded them; Physics.cfg's shipped values until then.
        /// </summary>
        internal static AeroFxConstants Constants
        {
            get
            {
                if (constantsRead) return cachedConstants;
                try
                {
                    if (TryReadPhysicsGlobalsCore(out AeroFxConstants read))
                    {
                        cachedConstants = read;
                        constantsRead = true;
                        ParsekLog.Verbose("ReentryFx", string.Format(CultureInfo.InvariantCulture,
                            "Aero FX constants read from PhysicsGlobals: thermal Mach {0:F2}..{1:F2} " +
                            "velocityExp={2:F2} density {3:G4}*rho^{4:G3} + {5:G4}*rho^{6:G3} " +
                            "fadeStart={7:G4} machFade {8:G4}..{9:G4}",
                            read.ThermalStartMach, read.ThermalFullMach, read.VelocityExponent,
                            read.DensityScalar1, read.DensityExponent1, read.DensityScalar2,
                            read.DensityExponent2, read.DensityFadeStart, read.MachFxFadeStart,
                            read.MachFxFadeEnd));
                    }
                }
                catch (Exception)
                {
                    // Headless (no Unity / KSP runtime): the shipped Physics.cfg values stand.
                }
                return cachedConstants;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryReadPhysicsGlobalsCore(out AeroFxConstants read)
        {
            read = AeroFxConstants.StockDefaults;
            if (PhysicsGlobals.Instance == null) return false;
            read = new AeroFxConstants
            {
                ThermalStartMach = PhysicsGlobals.AeroFXStartThermalFX,
                ThermalFullMach = PhysicsGlobals.AeroFXFullThermalFX,
                VelocityExponent = PhysicsGlobals.AeroFXVelocityExponent,
                DensityScalar1 = PhysicsGlobals.AeroFXDensityScalar1,
                DensityExponent1 = PhysicsGlobals.AeroFXDensityExponent1,
                DensityScalar2 = PhysicsGlobals.AeroFXDensityScalar2,
                DensityExponent2 = PhysicsGlobals.AeroFXDensityExponent2,
                DensityFadeStart = PhysicsGlobals.AeroFXDensityFadeStart,
                MachFxFadeStart = PhysicsGlobals.AeroFXMachFXFadeStart,
                MachFxFadeEnd = PhysicsGlobals.AeroFXMachFXFadeEnd,
            };
            return true;
        }

        /// <summary>
        /// A ghost's request for its sheath (and the light) this frame. Free for a ghost whose
        /// scalar is 0 and for a ghost with no built root (headless tests); the live half queues
        /// the offer for this frame's LateUpdate, creating the renderer in a flight scene.
        /// </summary>
        internal static void OfferSheath(ReentryFxInfo info, GameObject ghost, float fx,
            float aeroState, Vector3 surfaceVelocity, int recIdx, bool isolated)
        {
            if (info == null || ReferenceEquals(ghost, null) || !(fx > 0f)) return;
            OfferSheathCore(info, ghost, fx, aeroState, surfaceVelocity, recIdx, isolated);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void OfferSheathCore(ReentryFxInfo info, GameObject ghost, float fx,
            float aeroState, Vector3 surfaceVelocity, int recIdx, bool isolated)
        {
            if (ghost == null) return;
            GhostAeroFxRenderer renderer = EnsureInstance();
            if (renderer == null) return;
            int frame = Time.frameCount;
            if (renderer.offersFrame != frame)
            {
                renderer.offers.Clear();
                renderer.offersFrame = frame;
            }
            Vector3 flow = surfaceVelocity.sqrMagnitude > 1e-6f
                ? surfaceVelocity.normalized
                : ghost.transform.up;
            renderer.offers.Add(new Offer
            {
                info = info,
                root = ghost.transform,
                fx = Mathf.Clamp01(fx),
                aeroState = Mathf.Clamp01(aeroState),
                flowDirection = flow,
                recIdx = recIdx,
                isolated = isolated,
            });
        }

        /// <summary>
        /// The engine drove this ghost this frame (any ghost, burning or not): while stock's fire
        /// is on, its renderers in stock's reach are hidden from stock's two FX cameras.
        /// </summary>
        internal static void NoteGhost(GameObject ghost)
        {
            if (ReferenceEquals(ghost, null)) return;
            NoteGhostCore(ghost);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void NoteGhostCore(GameObject ghost)
        {
            if (ghost == null) return;
            GhostAeroFxRenderer renderer = EnsureInstance();
            if (renderer == null) return;
            int frame = Time.frameCount;
            if (renderer.maskGhostsFrame != frame)
            {
                renderer.maskGhosts.Clear();
                renderer.maskGhostsFrame = frame;
            }
            renderer.maskGhosts.Add(ghost);
        }

        /// <summary>
        /// FX rig only: switches the renderer's diagnostic mode (see
        /// <see cref="GhostAeroFxDiagnosticMode"/>). Returns false outside a flight scene.
        /// </summary>
        internal static bool SetDiagnosticModeForTesting(GhostAeroFxDiagnosticMode mode)
        {
            GhostAeroFxRenderer renderer = EnsureInstance();
            if (renderer == null)
            {
                ParsekLog.Info("ReentryFx", $"Ghost aero FX diagnostic mode {mode} refused: no renderer");
                return false;
            }
            renderer.diagMode = mode;
            ParsekLog.Info("ReentryFx", $"Ghost aero FX diagnostic mode -> {mode}");
            return true;
        }

        /// <summary>
        /// FX rig only: on the next frame a ghost sheath draws, renders Parsek's FX camera alone
        /// into a texture twice (stock's replacement shader with the ghost's values, then the
        /// plain bright material with no replacement), reads back the ghost's velocity depth map,
        /// dumps every camera's state and hands the pixels and the report to
        /// <paramref name="done"/> on the main thread. Gives up after
        /// <see cref="GhostAeroFx.DiagnosticCaptureTimeoutFrames"/> frames without a drawing frame.
        /// </summary>
        internal static bool RequestDiagnosticCaptureForTesting(string label,
            Action<GhostAeroFxDiagnosticCapture> done)
        {
            if (done == null) return false;
            GhostAeroFxRenderer renderer = EnsureInstance();
            if (renderer == null)
            {
                ParsekLog.Info("ReentryFx", $"Ghost aero FX capture '{label}' refused: no renderer");
                return false;
            }
            if (renderer.pendingCapture != null)
                ParsekLog.Info("ReentryFx",
                    $"Ghost aero FX capture '{renderer.pendingCaptureLabel}' replaced by '{label}' before it ran");
            renderer.pendingCapture = done;
            renderer.pendingCaptureLabel = string.IsNullOrEmpty(label) ? "capture" : label;
            renderer.pendingCaptureFrame = Time.frameCount;
            ParsekLog.Info("ReentryFx", $"Ghost aero FX capture '{renderer.pendingCaptureLabel}' armed");
            return true;
        }

        private static GhostAeroFxRenderer EnsureInstance()
        {
            if (instance != null) return instance;
            if (!HighLogic.LoadedSceneIsFlight || FlightCamera.fetch == null) return null;
            var go = new GameObject("Parsek GhostAeroFx");
            instance = go.AddComponent<GhostAeroFxRenderer>();
            ParsekLog.Verbose("ReentryFx", "Ghost aero FX renderer created for the flight scene");
            return instance;
        }

        private void OnEnable()
        {
            if (subscribed) return;
            Camera.onPreCull += OnAnyCameraPreCull;
            Camera.onPostRender += OnAnyCameraPostRender;
            subscribed = true;
        }

        private void OnDisable()
        {
            Unsubscribe();
            RestoreMask("disable");
        }

        private void OnDestroy()
        {
            // A rig waiting on a capture gets its callback, not a hang.
            if (pendingCapture != null) FinishCapture(null, "abandoned: renderer destroyed");
            Unsubscribe();
            RestoreMask("destroy");
            DestroyCopies();
            if (instance == this) instance = null;
            ParsekLog.Verbose("ReentryFx", "Ghost aero FX renderer destroyed");
        }

        private void Unsubscribe()
        {
            if (!subscribed) return;
            Camera.onPreCull -= OnAnyCameraPreCull;
            Camera.onPostRender -= OnAnyCameraPostRender;
            subscribed = false;
        }

        /// <summary>
        /// Every camera's cull: stock's FX or velocity camera hides the candidate ghost
        /// renderers, any other camera (the main camera first of all) restores them.
        /// </summary>
        private void OnAnyCameraPreCull(Camera cam)
        {
            bool stockCam = !ReferenceEquals(cam, null)
                && (ReferenceEquals(cam, stockCameraComponent) || ReferenceEquals(cam, stockVelocityCamera));
            if (mask.MaskedCount == 0 && (!stockCam || mask.CandidateCount == 0)) return;
            try
            {
                mask.PreCull(stockCam);
            }
            catch (Exception e)
            {
                RestoreMask("pre-cull failure");
                ParsekLog.WarnRateLimited("ReentryFx", "aero-mask-failed",
                    $"Ghost aero FX mask failed, ghosts restored: {e.GetType().Name}: {e.Message}", 30.0);
            }
        }

        private void OnAnyCameraPostRender(Camera cam)
        {
            if (!ReferenceEquals(cam, null))
            {
                if (ReferenceEquals(cam, fxCamera)) fxCopyRenders++;
                else if (ReferenceEquals(cam, depthCamera)) depthCopyRenders++;
            }
            if (mask.MaskedCount == 0) return;
            try
            {
                mask.PostRender();
            }
            catch (Exception e)
            {
                ParsekLog.WarnRateLimited("ReentryFx", "aero-mask-restore-failed",
                    $"Ghost aero FX mask restore failed: {e.GetType().Name}: {e.Message}", 30.0);
            }
        }

        private void RestoreMask(string reason)
        {
            try
            {
                int restored = mask.Restore();
                if (restored > 0 && reason != null)
                    ParsekLog.Verbose("ReentryFx", string.Format(CultureInfo.InvariantCulture,
                        "Ghost aero FX mask restored {0} renderer(s) on {1}", restored, reason));
            }
            catch (Exception e)
            {
                ParsekLog.WarnRateLimited("ReentryFx", "aero-mask-restore-failed",
                    $"Ghost aero FX mask restore failed: {e.GetType().Name}: {e.Message}", 30.0);
            }
        }

        /// <summary>Destroys the camera copies, the light and every pooled depth map.</summary>
        private void DestroyCopies()
        {
            if (velocityCamera != null) velocityCamera.targetTexture = null;
            ReleaseDepthPool();
            // The two copies live beside stock's cameras, not under this object.
            if (fxCamera != null) Destroy(fxCamera.gameObject);
            if (depthCamera != null) Destroy(depthCamera.gameObject);
            if (velocityCamera != null) Destroy(velocityCamera.gameObject);
            if (ghostLight != null) Destroy(ghostLight.gameObject);
            if (sanityMaterial != null) Destroy(sanityMaterial);
            sanityMaterial = null;
            fxCamera = null;
            depthCamera = null;
            velocityCamera = null;
            ghostLight = null;
            camerasOn = false;
            lightOwnerRecIdx = int.MinValue;
        }

        private void ReleaseDepthPool()
        {
            for (int i = 0; i < depthPool.Count; i++)
            {
                RenderTexture rt = depthPool[i];
                if (rt == null) continue;
                rt.Release();
                Destroy(rt);
            }
            depthPool.Clear();
            depthPoolLastUsed.Clear();
        }

        /// <summary>Finds stock's pieces and builds the copies once; names the first missing piece.</summary>
        private bool ResolveStock()
        {
            if (assetsReady && stockCamera != null && stockFx != null && stockCameraComponent != null)
                return true;
            // Stock's AerodynamicsFX can appear a few frames into the scene; look again at most
            // every two seconds, never every frame.
            if (Time.unscaledTime < nextResolveTime) return false;
            nextResolveTime = Time.unscaledTime + 2f;
            assetsReady = false;
            missingPiece = null;

            stockFx = FindObjectOfType<AerodynamicsFX>();
            if (stockFx == null) missingPiece = "AerodynamicsFX";
            else if (stockFx.fxCamera == null) missingPiece = "FXCamera";
            else if (stockFx.fxCamera.ReplacementShaders == null || stockFx.fxCamera.ReplacementShaders.Length == 0)
                missingPiece = "FXCamera.ReplacementShaders";
            else if (stockFx.fxDepthCamera == null || stockFx.fxDepthCamera.depthCamera == null)
                missingPiece = "FXDepthCamera";
            else if (stockFx.fxDepthCamera.ReplacementShader == null)
                missingPiece = "FXDepthCamera.ReplacementShader";
            else if (stockFx.Condensation == null || stockFx.ReentryHeat == null)
                missingPiece = "AeroFXState";
            else if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.Depth))
                missingPiece = "depth render texture support";

            if (missingPiece != null)
            {
                failedResolves++;
                // The first miss can be the first frames of the scene; a second one, two seconds
                // later, is a missing piece.
                if (failedResolves >= 2 && !missingWarned)
                {
                    missingWarned = true;
                    ParsekLog.Warn("ReentryFx",
                        $"Ghost reentry sheath unavailable: stock {missingPiece} not found; ghosts keep " +
                        "their glow and char but draw no sheath");
                }
                return false;
            }

            stockCamera = stockFx.fxCamera;
            stockCameraComponent = stockCamera.GetComponent<Camera>();
            stockDepth = stockFx.fxDepthCamera;
            stockLight = stockFx.fxLight;
            stockVelocityCamera = null;
            velocityCamMissingLogged = false;
            if (stockCameraComponent == null)
            {
                missingPiece = "FXCamera camera";
                if (!missingWarned)
                {
                    missingWarned = true;
                    ParsekLog.Warn("ReentryFx",
                        "Ghost reentry sheath unavailable: stock FXCamera has no Camera; ghosts draw no sheath");
                }
                return false;
            }
            condensation = ReadLook(stockFx.Condensation);
            heat = ReadLook(stockFx.ReentryHeat);
            BuildCopies();
            assetsReady = true;
            ParsekLog.Info("ReentryFx", string.Format(CultureInfo.InvariantCulture,
                "Ghost aero FX renderer ready: shaders={0} detailLevel={1} layer={2} ('{3}') " +
                "stockFxMask=0x{4:X} stockLight={5}",
                stockCamera.ReplacementShaders.Length, stockFx.detailLevel, SheathLayer,
                LayerMask.LayerToName(SheathLayer), stockCameraComponent.cullingMask,
                stockLight != null ? stockLight.type.ToString() : "none"));
            return true;
        }

        private static AeroFxLook ReadLook(AeroFXState s)
        {
            return new AeroFxLook
            {
                ColorMin = s.color != null ? s.color.min : Color.black,
                ColorMax = s.color != null ? s.color.max : Color.black,
                Length = Range(s.length),
                EdgeFade = Range(s.edgeFade),
                Falloff1 = Range(s.falloff1),
                Falloff2 = Range(s.falloff2),
                Falloff3 = Range(s.falloff3),
                Intensity = Range(s.intensity),
                Wobble = Range(s.wobble),
                LightPower = Range(s.lightPower),
            };
        }

        private static AeroFxRange Range(MinMaxFloat f)
            => f != null ? new AeroFxRange(f.min, f.max) : new AeroFxRange(0f, 0f);

        private void BuildCopies()
        {
            // A second resolve (stock's pieces replaced) builds fresh copies; drop the old ones.
            RestoreMask("rebuild");
            DestroyCopies();
            Transform parent = stockCameraComponent.transform.parent;

            var depthGo = new GameObject("Parsek ghost aero depth camera");
            depthGo.transform.SetParent(parent, false);
            depthCamera = depthGo.AddComponent<Camera>();
            depthCamera.CopyFrom(stockDepth.depthCamera);
            depthCamera.enabled = false;

            var fxGo = new GameObject("Parsek ghost aero FX camera");
            fxGo.transform.SetParent(parent, false);
            fxCamera = fxGo.AddComponent<Camera>();
            fxCamera.CopyFrom(stockCameraComponent);
            fxCamera.cullingMask = 1 << SheathLayer;
            fxCamera.enabled = false;

            // Stock's velocity camera, as FXCamera.Start builds it; rendered by hand per ghost.
            var velGo = new GameObject("Parsek ghost aero velocity camera");
            velGo.transform.SetParent(transform, false);
            velocityCamera = velGo.AddComponent<Camera>();
            velocityCamera.orthographic = true;
            velocityCamera.cullingMask = 1;
            velocityCamera.orthographicSize = GhostAeroFx.StockVelocityCamMinBox;
            velocityCamera.nearClipPlane = 0f;
            velocityCamera.farClipPlane = GhostAeroFx.StockVelocityCamRange;
            velocityCamera.clearFlags = CameraClearFlags.SolidColor;
            velocityCamera.backgroundColor = Color.white;
            velocityCamera.depthTextureMode = DepthTextureMode.Depth;
            velocityCamera.enabled = false;

            var lightGo = new GameObject("Parsek ghost aero light");
            lightGo.transform.SetParent(transform, false);
            ghostLight = lightGo.AddComponent<Light>();
            if (stockLight != null)
            {
                ghostLight.type = stockLight.type == LightType.Directional ? LightType.Point : stockLight.type;
                ghostLight.cullingMask = stockLight.cullingMask;
                ghostLight.renderMode = stockLight.renderMode;
                ghostLight.range = stockLight.range;
            }
            else
            {
                ghostLight.type = LightType.Point;
            }
            ghostLight.shadows = LightShadows.None;
            ghostLight.enabled = false;
        }

        private void LateUpdate()
        {
            // Nothing stays masked into a new frame, whatever happened to the last render.
            RestoreMask("late-update");
            try
            {
                DriveFrame();
            }
            catch (Exception e)
            {
                // A failure here must not repeat as an exception every frame: drop the frame's
                // sheaths and masks, switch the copies off and say why at most every 30 s.
                offers.Clear();
                maskGhosts.Clear();
                mask.ClearCandidates();
                prepared.Clear();
                SetCameras(false);
                SetLight(false, int.MinValue);
                FinishCapture(null, "frame failed: " + e.GetType().Name);
                ParsekLog.WarnRateLimited("ReentryFx", "aero-frame-failed",
                    $"Ghost aero FX frame failed, no ghost sheath this frame: {e.GetType().Name}: {e.Message}", 30.0);
            }
        }

        private void DriveFrame()
        {
            int frame = Time.frameCount;
            prepared.Clear();
            slot0SetGlobals = false;
            if (offersFrame != frame) offers.Clear();
            if (maskGhostsFrame != frame) maskGhosts.Clear();
            mask.ClearCandidates();

            if (offers.Count == 0 && maskGhosts.Count == 0)
            {
                SetCameras(false);
                SetLight(false, int.MinValue);
                TrimDepthPool(0, Time.unscaledTime);
                DrivePendingCapture(null, 0, frame);
                return;
            }

            bool ready = ResolveStock();
            bool mapView = MapView.MapIsEnabled;
            bool thermalDebug = PhysicsGlobals.ThermalColorsDebug;
            int detailLevel = stockFx != null ? stockFx.detailLevel : 0;
            bool stockOn = ready && stockCameraComponent != null && stockCameraComponent.enabled;
            bool stockLightAvailable = ready && stockLight != null;
            fxLayerMask = ready && stockCameraComponent != null ? stockCameraComponent.cullingMask : ~0;
            haveCaptureOffer = false;
            FlightCamera flightCamera = FlightCamera.fetch;
            Camera mainCamera = flightCamera != null ? flightCamera.mainCamera : null;
            if (mainCamera == null)
            {
                offers.Clear();
                maskGhosts.Clear();
                SetCameras(false);
                SetLight(false, int.MinValue);
                return;
            }
            frameWatch.Reset();
            frameWatch.Start();
            float reach = GhostAeroFx.StockReach(flightCamera.Distance);
            Vector3 cameraPos = mainCamera.transform.position;
            float now = Time.unscaledTime;

            int maskedGhosts = CollectMaskCandidates(stockOn, cameraPos, reach);

            int drawn = 0, outOfReach = 0, isolated = 0, other = 0;
            bool haveLight = false;
            float lightDistance = 0f;
            Offer lightOffer = default;
            Bounds lightBounds = default;

            for (int i = 0; i < offers.Count; i++)
            {
                Offer o = offers[i];
                if (o.root == null || o.info == null) continue;
                int visibleMeshes = TryGhostBounds(o.info, fxLayerMask, out Bounds bounds);
                bool haveBounds = visibleMeshes > 0;
                Vector3 center = haveBounds ? bounds.center : o.root.position;
                float radius = haveBounds ? bounds.extents.magnitude : Mathf.Max(1f, o.info.vesselLength * 0.5f);
                float distance = Vector3.Distance(cameraPos, center);
                bool inReach = GhostAeroFx.WithinReach(distance, radius, reach);

                GhostAeroSheathDecision decision = GhostAeroFx.DecideSheath(o.fx, mapView, thermalDebug,
                    detailLevel, ready, inReach, visibleMeshes, o.isolated);
                if (decision == GhostAeroSheathDecision.Draw)
                {
                    if (DrawSheath(o, bounds, distance, drawn, stockOn, now))
                    {
                        if (drawn == 0) { captureOffer = o; haveCaptureOffer = true; }
                        drawn++;
                    }
                    else
                        decision = GhostAeroSheathDecision.NoMeshes;
                }
                switch (decision)
                {
                    case GhostAeroSheathDecision.Draw: break;
                    case GhostAeroSheathDecision.OutOfReach: outOfReach++; break;
                    case GhostAeroSheathDecision.Isolated: isolated++; break;
                    default: other++; break;
                }
                NoteDecision(o, decision, distance, reach);

                GhostAeroLightDecision light = GhostAeroFx.DecideLight(o.fx, mapView,
                    stockLightAvailable, inReach, visibleMeshes);
                if (light == GhostAeroLightDecision.Offer && !o.isolated
                    && GhostAeroFx.LightOfferWins(haveLight, lightDistance, distance))
                {
                    haveLight = true;
                    lightDistance = distance;
                    lightOffer = o;
                    lightBounds = bounds;
                }
            }

            SetCameras(drawn > 0);
            if (drawn > 0)
            {
                framesCopiesOn++;
                DriveCameras(mainCamera, reach);
                if (!cameraStateLogged)
                {
                    cameraStateLogged = true;
                    ParsekLog.Info("ReentryFx", "Ghost aero FX camera state: " + DescribeCameras(mainCamera));
                }
            }
            DrivePendingCapture(mainCamera, drawn, frame);
            // Last, so no camera write or manual render of this frame can come after it.
            lastSubmitted = drawn > 0 ? SubmitPrepared() : 0;
            prepared.Clear();
            if (haveLight) DriveLight(lightOffer, lightBounds);
            else SetLight(false, int.MinValue);
            TrimDepthPool(drawn, now);

            frameWatch.Stop();
            int failedMaskWrites = mask.TakeFailedWrites();
            if (ParsekLog.IsVerboseEnabled)
                LogFrame(offers.Count, drawn, outOfReach, isolated, other, haveLight, lightOffer.recIdx, reach,
                    stockOn, ready, maskedGhosts, failedMaskWrites, frameWatch.Elapsed.TotalMilliseconds);
            offers.Clear();
            maskGhosts.Clear();
        }

        // Once per drawing frame: the message is built only when the rate limit lets it out.
        private void LogFrame(int offerCount, int drawn, int outOfReach, int isolated, int other,
            bool haveLight, int lightRecIdx, float reach, bool stockOn, bool ready, int maskedGhosts,
            int failedMaskWrites, double cpuMs)
        {
            ParsekLog.VerboseRateLimited("ReentryFx", "aero-frame", () => string.Format(CultureInfo.InvariantCulture,
                "Ghost aero FX frame: offers={0} drawn={1} outOfReach={2} isolated={3} other={4} " +
                "light={5} reach={6:F0}m stockOn={7} ready={8} maskGhosts={9} maskRenderers={10} " +
                "lastMasked={11} maskWriteFailures={12} depthMaps={13} depthRes={14} cpuMs={15:F3} " +
                "fxCopyRenders={16} depthCopyRenders={17} framesCopiesOn={18} mode={19} submitted={20}",
                offerCount, drawn, outOfReach, isolated, other,
                haveLight ? "#" + lightRecIdx.ToString(CultureInfo.InvariantCulture) : "none",
                reach, stockOn, ready, maskedGhosts, mask.CandidateCount, mask.LastMaskedCount,
                failedMaskWrites, depthPool.Count, depthPoolResolution,
                cpuMs, fxCopyRenders, depthCopyRenders, framesCopiesOn,
                diagMode, lastSubmitted), 5.0);
        }

        /// <summary>
        /// Gathers the renderers to hide from stock's FX and velocity cameras this frame: every
        /// active renderer, on a layer either camera draws, of each ghost within stock's reach,
        /// only while stock's effect is on. Returns how many ghosts are masked.
        /// </summary>
        private int CollectMaskCandidates(bool stockOn, Vector3 cameraPos, float reach)
        {
            if (!stockOn || maskGhosts.Count == 0) { NoteMasking(0); return 0; }
            if (stockVelocityCamera == null && StockVelocityCamField != null && stockCamera != null)
                stockVelocityCamera = StockVelocityCamField.GetValue(stockCamera) as Camera;
            if (stockVelocityCamera == null && !velocityCamMissingLogged)
            {
                velocityCamMissingLogged = true;
                ParsekLog.Verbose("ReentryFx",
                    "Ghost aero FX: stock velocity camera not found yet; ghosts are hidden from stock's FX camera only");
            }
            int fxMask = stockCameraComponent.cullingMask;
            int velocityMask = stockVelocityCamera != null ? stockVelocityCamera.cullingMask : 0;
            int ghosts = 0;
            for (int g = 0; g < maskGhosts.Count; g++)
            {
                GameObject ghost = maskGhosts[g];
                if (ghost == null || !ghost.activeInHierarchy) continue;
                rendererScratch.Clear();
                ghost.GetComponentsInChildren(false, rendererScratch);
                bool any = false;
                Bounds bounds = default;
                for (int r = 0; r < rendererScratch.Count; r++)
                {
                    Renderer rend = rendererScratch[r];
                    if (rend == null || !rend.enabled) continue;
                    if (!GhostAeroFx.LayerSeenByStockFx(rend.gameObject.layer, fxMask, velocityMask)) continue;
                    if (!any) { bounds = rend.bounds; any = true; }
                    else bounds.Encapsulate(rend.bounds);
                }
                if (!any) continue;
                float distance = Vector3.Distance(cameraPos, bounds.center);
                if (!GhostAeroFx.ShouldMaskFromStockFx(true,
                        GhostAeroFx.WithinReach(distance, bounds.extents.magnitude, reach)))
                    continue;
                for (int r = 0; r < rendererScratch.Count; r++)
                {
                    Renderer rend = rendererScratch[r];
                    if (rend == null || !rend.enabled) continue;
                    if (!GhostAeroFx.LayerSeenByStockFx(rend.gameObject.layer, fxMask, velocityMask)) continue;
                    mask.AddCandidate(rend);
                }
                ghosts++;
            }
            rendererScratch.Clear();
            NoteMasking(ghosts);
            return ghosts;
        }

        private void NoteMasking(int ghosts)
        {
            bool masking = ghosts > 0;
            if (masking == maskingLogged) return;
            maskingLogged = masking;
            ParsekLog.Verbose("ReentryFx", masking
                ? string.Format(CultureInfo.InvariantCulture,
                    "Ghost aero FX: hiding {0} ghost(s) ({1} renderers) from stock's FX and velocity cameras " +
                    "while stock's fire is on", ghosts, mask.CandidateCount)
                : "Ghost aero FX: no ghost hidden from stock's FX cameras");
        }

        /// <summary>This drawing slot's own velocity depth map, at stock's occlusion size.</summary>
        private RenderTexture AcquireDepthMap(int slot, int resolution, float now)
        {
            if (depthPoolResolution != resolution)
            {
                if (depthPool.Count > 0)
                    ParsekLog.Verbose("ReentryFx", string.Format(CultureInfo.InvariantCulture,
                        "Ghost aero depth maps resized {0} -> {1}: released {2}",
                        depthPoolResolution, resolution, depthPool.Count));
                ReleaseDepthPool();
                depthPoolResolution = resolution;
            }
            while (depthPool.Count <= slot)
            {
                var rt = new RenderTexture(resolution, resolution, 32, RenderTextureFormat.Depth);
                rt.name = "Parsek ghost aero velocity depth " + depthPool.Count.ToString(CultureInfo.InvariantCulture);
                rt.Create();
                depthPool.Add(rt);
                depthPoolLastUsed.Add(now);
                ParsekLog.Verbose("ReentryFx", string.Format(CultureInfo.InvariantCulture,
                    "Ghost aero depth map {0} created at {1}x{1}", depthPool.Count - 1, resolution));
            }
            RenderTexture map = depthPool[slot];
            if (!map.IsCreated()) map.Create();
            depthPoolLastUsed[slot] = now;
            return map;
        }

        private void TrimDepthPool(int drawn, float now)
        {
            int released = 0;
            for (int i = depthPool.Count - 1; i >= 0; i--)
            {
                if (!GhostAeroFx.ShouldReleasePooledDepthMap(i, drawn, depthPoolLastUsed[i], now)) break;
                RenderTexture rt = depthPool[i];
                if (rt != null)
                {
                    rt.Release();
                    Destroy(rt);
                }
                depthPool.RemoveAt(i);
                depthPoolLastUsed.RemoveAt(i);
                released++;
            }
            if (released > 0)
                ParsekLog.Verbose("ReentryFx", string.Format(CultureInfo.InvariantCulture,
                    "Ghost aero depth maps released: {0} idle, {1} kept", released, depthPool.Count));
        }

        private static void NoteDecision(Offer o, GhostAeroSheathDecision decision, float distance, float reach)
        {
            if (o.info.lastSheathDecision.HasValue && o.info.lastSheathDecision.Value == decision) return;
            o.info.lastSheathDecision = decision;
            ParsekLog.Verbose("ReentryFx", string.Format(CultureInfo.InvariantCulture,
                "Ghost #{0} reentry sheath: {1} (fx={2:F3} state={3:F2} dist={4:F0}m reach={5:F0}m)",
                o.recIdx, decision, o.fx, o.aeroState, distance, reach));
        }

        /// <summary>
        /// World bounds of the ghost meshes the sheath draws this frame (enabled renderers on
        /// active objects, on a layer stock's FX camera sees); returns how many there are.
        /// </summary>
        private static int TryGhostBounds(ReentryFxInfo info, int fxMask, out Bounds bounds)
        {
            bounds = default;
            int visible = 0;
            List<FireShellMesh> meshes = info.fireShellMeshes;
            if (meshes == null) return 0;
            for (int m = 0; m < meshes.Count; m++)
            {
                FireShellMesh fsm = meshes[m];
                if (fsm.mesh == null || fsm.renderer == null) continue;
                GameObject go = fsm.renderer.gameObject;
                if (!GhostAeroFx.SheathMeshVisible(fsm.renderer.enabled, go.activeInHierarchy, go.layer, fxMask))
                    continue;
                if (visible == 0) bounds = fsm.renderer.bounds;
                else bounds.Encapsulate(fsm.renderer.bounds);
                visible++;
            }
            return visible;
        }

        private bool DrawSheath(Offer o, Bounds bounds, float cameraDistance, int slot, bool stockOn, float now)
        {
            AeroFxDrawValues v = GhostAeroFx.Blend(condensation, heat, o.fx, o.aeroState);
            int lod = GhostAeroFx.ShaderLod(stockCamera.ReplacementShaders.Length, stockFx.detailLevel);
            Vector3 effectDirection = -o.flowDirection;

            // The ghost's own occlusion map: stock's velocity camera, boxed and stood off as stock
            // does for the active vessel, on the camera's distance to this ghost.
            GhostAeroFx.VelocityCamBox(bounds.size.magnitude, cameraDistance, out float box, out float far);
            // Each drawing ghost renders its own depth map at stock's occlusion size; the map must
            // live until the FX copy renders, which is why it is not shared between ghosts.
            RenderTexture depthMap = AcquireDepthMap(slot,
                GhostAeroFx.OcclusionResolution(stockCamera.occlusionMapResolution), now);
            Transform vt = velocityCamera.transform;
            vt.position = bounds.center - effectDirection * box;
            vt.forward = effectDirection;
            velocityCamera.orthographicSize = box;
            velocityCamera.farClipPlane = far;
            velocityCamera.targetTexture = depthMap;
            velocityCamera.rect = new Rect(0f, 0f, 1f, 1f);
            velocityCamera.aspect = 1f;
            velocityCamera.Render();

            while (blocks.Count <= slot) blocks.Add(new MaterialPropertyBlock());
            MaterialPropertyBlock mpb = blocks[slot];
            mpb.Clear();
            Vector3 vp = vt.position;
            var fxColor = new Color(1f, 1f, 1f, GhostAeroFx.FxColorAlpha(v.Intensity, lod));
            Matrix4x4 world2Velocity = Matrix4x4.TRS(Vector3.zero,
                Quaternion.FromToRotation(Vector3.up, effectDirection), Vector3.one);
            Matrix4x4 depthCamMatrix = velocityCamera.worldToCameraMatrix;
            Matrix4x4 depthProjMatrix = velocityCamera.projectionMatrix;
            var cameraPos = new Vector4(vp.x, vp.y, vp.z, 0f);
            mpb.SetVector("_LightDirection0", effectDirection);
            mpb.SetColor("_FXColor", fxColor);
            mpb.SetColor("_MyLightColor0", v.Color);
            mpb.SetFloat("_FxLength", v.Length);
            mpb.SetFloat("_EdgeFade", v.EdgeFade);
            mpb.SetFloat("_Obliqueness", stockCamera.obliqueness);
            mpb.SetFloat("_FXTextureSpeed", stockCamera.textureSpeed);
            mpb.SetFloat("_FXTextureScale", stockCamera.textureScale);
            if (stockCamera.texture != null) mpb.SetTexture("_FXMainTex", stockCamera.texture);
            mpb.SetTexture("_FXDepthMap", depthMap);
            mpb.SetMatrix("_FXDepthCamMatrix", depthCamMatrix);
            mpb.SetMatrix("_FXDepthProjMatrix", depthProjMatrix);
            mpb.SetVector("_FXCameraPos", cameraPos);
            mpb.SetFloat("_FXProjectionNear", velocityCamera.nearClipPlane);
            mpb.SetFloat("_FXProjectionFar", velocityCamera.farClipPlane);
            mpb.SetFloat("_FXProjectionBias", stockCamera.projectionBias);
            mpb.SetMatrix("_FXWorld2Velocity", world2Velocity);
            mpb.SetFloat("_FXFalloff", v.Falloff1);
            mpb.SetFloat("_FXFalloff2", v.Falloff2);
            mpb.SetFloat("_FXFalloff3", v.Falloff3);
            mpb.SetFloat("_FXWobble", v.Wobble);
            if (slot == 0) slot0SetGlobals = !stockOn;
            if (slot == 0 && !stockOn)
            {
                // The same values as globals, as FXCamera.OnPreRender sets them, for whatever the
                // replacement shaders read outside the per-draw block, only while stock's camera
                // is off (it sets its own before it renders again). While it is on, the globals
                // are the active vessel's and every ghost value rides in its own block.
                Shader.SetGlobalVector("_LightDirection0", effectDirection);
                Shader.SetGlobalColor("_FXColor", fxColor);
                Shader.SetGlobalColor("_MyLightColor0", v.Color);
                Shader.SetGlobalFloat("_FxLength", v.Length);
                Shader.SetGlobalFloat("_EdgeFade", v.EdgeFade);
                Shader.SetGlobalFloat("_Obliqueness", stockCamera.obliqueness);
                Shader.SetGlobalFloat("_FXTextureSpeed", stockCamera.textureSpeed);
                Shader.SetGlobalFloat("_FXTextureScale", stockCamera.textureScale);
                if (stockCamera.texture != null) Shader.SetGlobalTexture("_FXMainTex", stockCamera.texture);
                Shader.SetGlobalTexture("_FXDepthMap", depthMap);
                Shader.SetGlobalMatrix("_FXDepthCamMatrix", depthCamMatrix);
                Shader.SetGlobalMatrix("_FXDepthProjMatrix", depthProjMatrix);
                Shader.SetGlobalVector("_FXCameraPos", cameraPos);
                Shader.SetGlobalFloat("_FXProjectionNear", velocityCamera.nearClipPlane);
                Shader.SetGlobalFloat("_FXProjectionFar", velocityCamera.farClipPlane);
                Shader.SetGlobalFloat("_FXProjectionBias", stockCamera.projectionBias);
                Shader.SetGlobalMatrix("_FXWorld2Velocity", world2Velocity);
                Shader.SetGlobalFloat("_FXFalloff", v.Falloff1);
                Shader.SetGlobalFloat("_FXFalloff2", v.Falloff2);
                Shader.SetGlobalFloat("_FXFalloff3", v.Falloff3);
                Shader.SetGlobalFloat("_FXWobble", v.Wobble);
            }

            LogSheathMaterialsOnce(o);
            prepared.Add(new PreparedSheath
            {
                offer = o, slot = slot, values = v, lod = lod, box = box, far = far,
                worldToVelocity = depthCamMatrix,
            });
            return true;
        }

        /// <summary>
        /// Submits every sheath prepared this frame to the FX copy. It runs last in the frame's
        /// drive, after the cameras are switched on and set (and after a rig capture), because a
        /// <c>Graphics.DrawMesh</c> for one camera lives in that camera's own list until it renders,
        /// and a submission made before the camera was switched on or re-sorted never reached the
        /// screen (the 2026-11-28 diagnostic flight). Returns the submeshes submitted.
        /// </summary>
        private int SubmitPrepared()
        {
            int total = 0;
            Material plain = GhostAeroFx.UsesReplacementShader(diagMode) ? null : SanityMaterial();
            for (int i = 0; i < prepared.Count; i++)
            {
                PreparedSheath p = prepared[i];
                int submitted = SubmitSheath(p.offer, blocks[p.slot], plain);
                total += submitted;
                if (ParsekLog.IsVerboseEnabled) LogSheathDrawn(p, submitted, diagMode);
            }
            prepared.Clear();
            return total;
        }

        // Per drawing ghost per frame: the message is built only when the rate limit lets it out.
        private static void LogSheathDrawn(PreparedSheath p, int submitted, GhostAeroFxDiagnosticMode mode)
        {
            AeroFxInputs inputs = p.offer.info.lastInputs;
            ParsekLog.VerboseRateLimited("ReentryFx", $"aero-sheath-{p.offer.recIdx}", () => string.Format(
                CultureInfo.InvariantCulture,
                "Ghost #{0} sheath drawn: fx={1:F3} state={2:F2} submeshes={3} lod={4} alpha={5:F3} " +
                "length={6:F2} color=({7:F2},{8:F2},{9:F2}) box={10:F1}m velFar={12:F1}m mode={11} {13}",
                p.offer.recIdx, p.offer.fx, p.offer.aeroState, submitted, p.lod,
                GhostAeroFx.FxColorAlpha(p.values.Intensity, p.lod), p.values.Length,
                p.values.Color.r, p.values.Color.g, p.values.Color.b,
                p.box, mode, p.far, GhostAeroFx.FormatInputs(inputs)), 5.0);
        }

        private void DriveCameras(Camera mainCamera, float reach)
        {
            int lod = GhostAeroFx.ShaderLod(stockCamera.ReplacementShaders.Length, stockFx.detailLevel);
            float fov = mainCamera.fieldOfView;

            // The copies render from their parent's pose (stock's FX camera parent, the main
            // camera); a scene change of that parent would leave them drawing from a stale pose.
            Transform expectedParent = stockCameraComponent.transform.parent != null
                ? stockCameraComponent.transform.parent : mainCamera.transform;
            KeepCopyParented(fxCamera, expectedParent);
            KeepCopyParented(depthCamera, expectedParent);

            // The copies took stock's settings once (BuildCopies' CopyFrom) and follow the main
            // camera as its children; per frame only the values stock's own LateUpdate changes are
            // set. Every write here happens before this frame's sheath submissions (SubmitPrepared),
            // so nothing done to the camera can drop them.
            depthCamera.fieldOfView = fov;
            depthCamera.farClipPlane = reach;
            depthCamera.SetReplacementShader(stockDepth.ReplacementShader, "RenderType");

            if (fxCamera.cullingMask != 1 << SheathLayer) fxCamera.cullingMask = 1 << SheathLayer;
            fxCamera.fieldOfView = fov;
            fxCamera.farClipPlane = reach;
            ApplyReplacementShader(lod);

            // Stock's two cameras sit at depth 2 / 3 (FXCamera.OnCameraChange); render strictly
            // after them so stock's pass and ours never interleave, depth copy first. A depth is
            // written only when it changes (a write re-sorts the camera).
            float after = GhostAeroFx.CopyCameraDepth(stockDepth.depthCamera.depth, stockCameraComponent.depth);
            if (GhostAeroFx.CameraDepthNeedsWrite(depthCamera.depth, after)) depthCamera.depth = after;
            float fxDepth = after + GhostAeroFx.CopyCameraDepthStep;
            if (GhostAeroFx.CameraDepthNeedsWrite(fxCamera.depth, fxDepth)) fxCamera.depth = fxDepth;

            // The copies' on / off state is ours (the depth pass stays off in the NoSceneDepth
            // diagnostic mode).
            bool depthOn = GhostAeroFx.SceneDepthPassOn(diagMode);
            if (depthCamera.enabled != depthOn) depthCamera.enabled = depthOn;
            if (!fxCamera.enabled)
            {
                fxCamera.enabled = true;
                if (!fxCopyReenabledLogged)
                {
                    fxCopyReenabledLogged = true;
                    ParsekLog.Info("ReentryFx", "Ghost aero FX camera copy was off while drawing; switched on again");
                }
            }
        }

        private static void KeepCopyParented(Camera copy, Transform expectedParent)
        {
            Transform t = copy.transform;
            if (t.parent == expectedParent) return;
            string was = t.parent != null ? t.parent.name : "none";
            t.SetParent(expectedParent, false);
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;
            ParsekLog.Info("ReentryFx", $"Ghost aero FX camera copy '{copy.name}' re-parented: {was} -> {expectedParent.name}");
        }

        private void ApplyReplacementShader(int lod)
        {
            if (GhostAeroFx.UsesReplacementShader(diagMode))
                fxCamera.SetReplacementShader(stockCamera.ReplacementShaders[lod], "RenderType");
            else
                fxCamera.ResetReplacementShader();
        }

        /// <summary>
        /// Submits a ghost's visible sheath meshes to the FX copy for this frame: its own
        /// materials with the ghost's property block, or <paramref name="plain"/> (the diagnostic
        /// sanity material) with none. Returns the submeshes submitted.
        /// </summary>
        private int SubmitSheath(Offer o, MaterialPropertyBlock mpb, Material plain, int onlyMesh = -1)
        {
            int submitted = 0;
            List<FireShellMesh> meshes = o.info.fireShellMeshes;
            if (meshes == null) return 0;
            for (int m = 0; m < meshes.Count; m++)
            {
                if (onlyMesh >= 0 && m != onlyMesh) continue;
                FireShellMesh fsm = meshes[m];
                if (fsm.mesh == null || fsm.transform == null || fsm.renderer == null) continue;
                GameObject go = fsm.renderer.gameObject;
                if (!GhostAeroFx.SheathMeshVisible(fsm.renderer.enabled, go.activeInHierarchy, go.layer, fxLayerMask))
                    continue;
                Material[] materials = fsm.renderer.sharedMaterials;
                if (materials == null) continue;
                Matrix4x4 matrix = fsm.transform.localToWorldMatrix;
                int subMeshes = Mathf.Min(fsm.mesh.subMeshCount, materials.Length);
                for (int s = 0; s < subMeshes; s++)
                {
                    Material material = plain != null ? plain : materials[s];
                    if (material == null) continue;
                    Graphics.DrawMesh(fsm.mesh, matrix, material, SheathLayer, fxCamera, s,
                        plain != null ? null : mpb, false, false);
                    submitted++;
                }
            }
            return submitted;
        }

        /// <summary>Once per ghost build: the shaders and RenderType tags the sheath submits.</summary>
        private void LogSheathMaterialsOnce(Offer o)
        {
            if (o.info.sheathMaterialsLogged) return;
            o.info.sheathMaterialsLogged = true;
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int hidden = 0;
            List<FireShellMesh> meshes = o.info.fireShellMeshes;
            for (int m = 0; meshes != null && m < meshes.Count; m++)
            {
                FireShellMesh fsm = meshes[m];
                if (fsm.renderer == null) continue;
                GameObject go = fsm.renderer.gameObject;
                if (!GhostAeroFx.SheathMeshVisible(fsm.renderer.enabled, go.activeInHierarchy, go.layer, fxLayerMask))
                {
                    hidden++;
                    continue;
                }
                Material[] materials = fsm.renderer.sharedMaterials;
                for (int s = 0; materials != null && s < materials.Length; s++)
                {
                    Material mat = materials[s];
                    string key = mat == null ? "null"
                        : (mat.shader != null ? mat.shader.name : "?") + "[" + mat.GetTag("RenderType", false, "none") +
                          "] L" + go.layer.ToString(CultureInfo.InvariantCulture);
                    counts.TryGetValue(key, out int c);
                    counts[key] = c + 1;
                }
            }
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, int> kv in counts)
                sb.Append(kv.Key).Append('x').Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append(' ');
            ParsekLog.Verbose("ReentryFx", string.Format(CultureInfo.InvariantCulture,
                "Ghost #{0} sheath materials: {1}(meshes={2} skippedHiddenOrOtherLayer={3} fxMask=0x{4:X})",
                o.recIdx, sb, meshes != null ? meshes.Count : 0, hidden, fxLayerMask));
        }

        /// <summary>The diagnostic sanity material: flat magenta (no depth test or culling where the shader allows).</summary>
        private Material SanityMaterial()
        {
            if (sanityMaterial != null) return sanityMaterial;
            // Unlit/Color reads no vertex colour; Internal-Colored (always in a build) multiplies
            // by it; KSP's emissive diffuse is the last resort, bright through its emission.
            Shader shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) shader = Shader.Find("KSP/Emissive/Diffuse");
            if (shader == null)
            {
                if (!sanityMaterialMissingLogged)
                {
                    sanityMaterialMissingLogged = true;
                    ParsekLog.Info("ReentryFx", "Ghost aero FX sanity material unavailable: no unlit shader found");
                }
                return null;
            }
            sanityMaterial = new Material(shader) { name = "Parsek ghost aero sanity" };
            var magenta = new Color(1f, 0f, 1f, 1f);
            sanityMaterial.SetColor("_Color", magenta);
            if (sanityMaterial.HasProperty("_EmissiveColor")) sanityMaterial.SetColor("_EmissiveColor", magenta);
            if (sanityMaterial.HasProperty("_ZTest")) sanityMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
            if (sanityMaterial.HasProperty("_Cull")) sanityMaterial.SetInt("_Cull", (int)CullMode.Off);
            if (sanityMaterial.HasProperty("_ZWrite")) sanityMaterial.SetInt("_ZWrite", 0);
            ParsekLog.Verbose("ReentryFx", $"Ghost aero FX sanity material built on '{shader.name}'");
            return sanityMaterial;
        }

        /// <summary>Runs, waits on or abandons a rig capture request this frame.</summary>
        private void DrivePendingCapture(Camera mainCamera, int drawn, int frame)
        {
            if (pendingCapture == null) return;
            if (drawn > 0 && haveCaptureOffer && mainCamera != null && fxCamera != null)
            {
                RunCapture(mainCamera);
                return;
            }
            if (GhostAeroFx.ShouldAbandonCapture(pendingCaptureFrame, frame))
                FinishCapture(null, string.Format(CultureInfo.InvariantCulture,
                    "abandoned: no drawing frame in {0} frames", GhostAeroFx.DiagnosticCaptureTimeoutFrames));
        }

        private void RunCapture(Camera mainCamera)
        {
            var capture = new GhostAeroFxDiagnosticCapture { Label = pendingCaptureLabel };
            var report = new StringBuilder();
            int lod = GhostAeroFx.ShaderLod(stockCamera.ReplacementShaders.Length, stockFx.detailLevel);
            MaterialPropertyBlock mpb = blocks.Count > 0 ? blocks[0] : null;
            var clear = new Color32(0, 0, 0, 0);
            // The capture ghost is slot 0; its own box, far and matrix (the shared velocity camera
            // has moved on to the last ghost that rendered a map this frame).
            bool haveSlot0 = false;
            PreparedSheath slot0 = default;
            for (int i = 0; i < prepared.Count; i++)
                if (prepared[i].slot == 0) { slot0 = prepared[i]; haveSlot0 = true; break; }
            report.Append("ghost=#").Append(captureOffer.recIdx.ToString(CultureInfo.InvariantCulture))
                .Append(" mode=").Append(diagMode)
                .Append(" fx=").Append(captureOffer.fx.ToString("F3", CultureInfo.InvariantCulture))
                .Append(" state=").Append(captureOffer.aeroState.ToString("F3", CultureInfo.InvariantCulture));
            if (haveSlot0)
                report.Append(" velocityCam(slot0) box=").Append(slot0.box.ToString("F1", CultureInfo.InvariantCulture))
                    .Append(" far=").Append(slot0.far.ToString("F1", CultureInfo.InvariantCulture));
            else report.Append(" velocityCam(slot0)=none");
            report.Append(" sheathsThisFrame=").Append(prepared.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" slot0SetGlobals=").Append(slot0SetGlobals ? "True" : "False")
                .Append(" reversedZ=").Append(SystemInfo.usesReversedZBuffer ? "True" : "False")
                .Append(' ').Append(GhostAeroFx.FormatInputs(captureOffer.info.lastInputs)).Append(' ');
            try
            {
                int w = Mathf.Max(16, mainCamera.pixelWidth);
                int h = Mathf.Max(16, mainCamera.pixelHeight);
                capture.Width = w;
                capture.Height = h;
                int rendersBefore = fxCopyRenders;
                capture.FxPixels = RenderCopyToPixels(w, h, lod, mpb, null);
                report.Append("| fx (stock replacement, ghost values, no scene depth): ")
                    .Append(GhostAeroFx.DescribePixels(capture.FxPixels, clear)).Append(' ');
                Material plain = SanityMaterial();
                if (plain != null)
                {
                    capture.PlainPixels = RenderCopyToPixels(w, h, lod, mpb, plain);
                    report.Append("| plain (no replacement, magenta): ")
                        .Append(GhostAeroFx.DescribePixels(capture.PlainPixels, clear)).Append(' ');
                }
                else report.Append("| plain: no sanity shader ");
                RenderTexture map = depthPool.Count > 0 ? depthPool[0] : null;
                if (map != null)
                {
                    capture.DepthPixels = ReadDepthMap(map);
                    capture.DepthSize = map.width;
                    report.Append("| depthMap0 ").Append(map.width.ToString(CultureInfo.InvariantCulture))
                        .Append(": ").Append(GhostAeroFx.DescribeDepthPixels(capture.DepthPixels)).Append(' ');
                    AppendEmptyMapProbe(report, w, h, lod, mpb, map, capture.FxPixels, clear);
                }
                else report.Append("| depthMap0: none ");
                AppendSheathMeshProbes(report, w, h, lod, mpb, haveSlot0, slot0, capture, clear);
                report.Append("| copyRendersDuringCapture=")
                    .Append((fxCopyRenders - rendersBefore).ToString(CultureInfo.InvariantCulture)).Append(' ');
            }
            catch (Exception e)
            {
                report.Append("| capture failed: ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append(' ');
            }
            finally
            {
                // The on-screen submissions come after this (SubmitPrepared), so the manual renders
                // cannot consume them; only the mode's replacement shader is put back.
                ApplyReplacementShader(lod);
            }
            report.Append("| cameras: ").Append(DescribeCameras(mainCamera));
            FinishCapture(capture, report.ToString());
        }

        /// <summary>
        /// The capture ghost again through the FX shader with a cleared velocity map (nothing
        /// occludes): how much of the normal render the ghost's own occlusion map changes. A
        /// near-zero difference means the occlusion test does not act on this ghost's map.
        /// </summary>
        private void AppendEmptyMapProbe(StringBuilder report, int w, int h, int lod,
            MaterialPropertyBlock mpb, RenderTexture map, Color32[] fxPixels, Color32 clear)
        {
            if (mpb == null) return;
            RenderTexture empty = RenderTexture.GetTemporary(map.width, map.height, 32, RenderTextureFormat.Depth);
            RenderTexture previousTarget = velocityCamera.targetTexture;
            int previousMask = velocityCamera.cullingMask;
            try
            {
                velocityCamera.cullingMask = 0;
                velocityCamera.targetTexture = empty;
                velocityCamera.Render();
                mpb.SetTexture("_FXDepthMap", empty);
                if (slot0SetGlobals) Shader.SetGlobalTexture("_FXDepthMap", empty);
                Color32[] unoccluded = RenderCopyToPixels(w, h, lod, mpb, null);
                GhostAeroFx.MeasurePixels(fxPixels, clear, out int normalCovered, out long normalSum);
                GhostAeroFx.MeasurePixels(unoccluded, clear, out int emptyCovered, out long emptySum);
                report.Append("| emptyMap (nothing occludes): covered=")
                    .Append(emptyCovered.ToString(CultureInfo.InvariantCulture))
                    .Append(" rgbSum=").Append(emptySum.ToString(CultureInfo.InvariantCulture))
                    .Append(" vs normal covered=").Append(normalCovered.ToString(CultureInfo.InvariantCulture))
                    .Append(" rgbSum=").Append(normalSum.ToString(CultureInfo.InvariantCulture))
                    .Append(" pixelsChangedByOcclusion=")
                    .Append(GhostAeroFx.CountDifferingPixels(fxPixels, unoccluded, 8).ToString(CultureInfo.InvariantCulture))
                    .Append(' ');
            }
            finally
            {
                mpb.SetTexture("_FXDepthMap", map);
                if (slot0SetGlobals) Shader.SetGlobalTexture("_FXDepthMap", map);
                velocityCamera.targetTexture = previousTarget;
                velocityCamera.cullingMask = previousMask;
                RenderTexture.ReleaseTemporary(empty);
            }
        }

        /// <summary>
        /// One line per visible sheath mesh of the capture ghost: what it alone draws through the
        /// FX shader, and where its bounds stand against slot 0's own velocity depth map.
        /// </summary>
        private void AppendSheathMeshProbes(StringBuilder report, int w, int h, int lod,
            MaterialPropertyBlock mpb, bool haveSlot0, PreparedSheath slot0,
            GhostAeroFxDiagnosticCapture capture, Color32 clear)
        {
            List<FireShellMesh> meshes = captureOffer.info.fireShellMeshes;
            if (meshes == null) return;
            bool reversedZ = SystemInfo.usesReversedZBuffer;
            int probed = 0;
            for (int m = 0; m < meshes.Count; m++)
            {
                FireShellMesh fsm = meshes[m];
                if (fsm.mesh == null || fsm.transform == null || fsm.renderer == null) continue;
                GameObject go = fsm.renderer.gameObject;
                if (!GhostAeroFx.SheathMeshVisible(fsm.renderer.enabled, go.activeInHierarchy, go.layer, fxLayerMask))
                    continue;
                Color32[] alone = RenderCopyToPixels(w, h, lod, mpb, null, m);
                GhostAeroFx.MeasurePixels(alone, clear, out int covered, out long rgbSum);

                float centerD = float.NaN, nearD = float.NaN, mapD = float.NaN;
                bool onMap = false;
                int tx = -1, ty = -1;
                byte raw = 0;
                if (haveSlot0)
                {
                    Bounds b = fsm.renderer.bounds;
                    Vector3 c = slot0.worldToVelocity.MultiplyPoint3x4(b.center);
                    centerD = -c.z;
                    nearD = float.MaxValue;
                    for (int k = 0; k < 8; k++)
                    {
                        Vector3 corner = b.center + Vector3.Scale(b.extents,
                            new Vector3((k & 1) == 0 ? -1f : 1f, (k & 2) == 0 ? -1f : 1f, (k & 4) == 0 ? -1f : 1f));
                        float d = -slot0.worldToVelocity.MultiplyPoint3x4(corner).z;
                        if (d < nearD) nearD = d;
                    }
                    if (capture.DepthPixels != null && capture.DepthSize > 0
                        && GhostAeroFx.TryVelocityMapTexel(c.x, c.y, slot0.box, capture.DepthSize, out tx, out ty))
                    {
                        onMap = true;
                        raw = capture.DepthPixels[ty * capture.DepthSize + tx].r;
                        mapD = GhostAeroFx.VelocityMapDistance(raw, slot0.far, reversedZ);
                    }
                }
                Material[] mats = fsm.renderer.sharedMaterials;
                var matText = new StringBuilder();
                for (int s = 0; mats != null && s < mats.Length; s++)
                {
                    Material mat = mats[s];
                    if (s > 0) matText.Append('+');
                    matText.Append(mat == null ? "null"
                        : (mat.shader != null ? mat.shader.name : "?") + "[" + mat.GetTag("RenderType", false, "none") + "]");
                }
                report.Append("| ").Append(GhostAeroFx.FormatSheathMeshProbe(m, go.name, fsm.mesh.name,
                    matText.ToString(), go.layer, fsm.mesh.vertexCount, covered, rgbSum,
                    centerD, nearD, onMap, tx, ty, raw, mapD)).Append(' ');
                probed++;
            }
            report.Append("| meshesProbed=").Append(probed.ToString(CultureInfo.InvariantCulture)).Append(' ');
        }

        private void FinishCapture(GhostAeroFxDiagnosticCapture capture, string report)
        {
            Action<GhostAeroFxDiagnosticCapture> done = pendingCapture;
            if (done == null) return;
            string label = pendingCaptureLabel;
            pendingCapture = null;
            pendingCaptureLabel = null;
            if (capture == null) capture = new GhostAeroFxDiagnosticCapture { Label = label };
            capture.Report = report;
            ParsekLog.Info("ReentryFx", $"Ghost aero FX capture '{label}': {report}");
            try
            {
                done(capture);
            }
            catch (Exception e)
            {
                ParsekLog.Warn("ReentryFx", $"Ghost aero FX capture '{label}' callback failed: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// Renders the FX copy alone into a cleared transparent texture (no scene depth) after
        /// submitting the capture ghost again, and reads it back.
        /// </summary>
        private Color32[] RenderCopyToPixels(int w, int h, int lod, MaterialPropertyBlock mpb, Material plain,
            int onlyMesh = -1)
        {
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
            RenderTexture previousTarget = fxCamera.targetTexture;
            CameraClearFlags previousClear = fxCamera.clearFlags;
            Color previousBackground = fxCamera.backgroundColor;
            try
            {
                if (plain != null) fxCamera.ResetReplacementShader();
                else fxCamera.SetReplacementShader(stockCamera.ReplacementShaders[lod], "RenderType");
                SubmitSheath(captureOffer, mpb, plain, onlyMesh);
                fxCamera.targetTexture = rt;
                fxCamera.clearFlags = CameraClearFlags.SolidColor;
                fxCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                fxCamera.Render();
                return ReadBack(rt, w, h);
            }
            finally
            {
                fxCamera.targetTexture = previousTarget;
                fxCamera.clearFlags = previousClear;
                fxCamera.backgroundColor = previousBackground;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>The depth map read back as colour (depth in the red channel).</summary>
        private static Color32[] ReadDepthMap(RenderTexture map)
        {
            int n = map.width;
            RenderTexture rt = RenderTexture.GetTemporary(n, n, 0, RenderTextureFormat.ARGB32);
            try
            {
                Graphics.Blit(map, rt);
                return ReadBack(rt, n, n);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static Color32[] ReadBack(RenderTexture rt, int w, int h)
        {
            RenderTexture previousActive = RenderTexture.active;
            var texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = rt;
                texture.ReadPixels(new Rect(0f, 0f, w, h), 0, 0, false);
                texture.Apply(false);
                return texture.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previousActive;
                Destroy(texture);
            }
        }

        /// <summary>
        /// Every camera that bears on the sheath: the three copies, stock's three, the main
        /// camera and each enabled camera of the scene, then the shaders in use.
        /// </summary>
        private string DescribeCameras(Camera mainCamera)
        {
            var sb = new StringBuilder();
            if (stockVelocityCamera == null && StockVelocityCamField != null && stockCamera != null)
                stockVelocityCamera = StockVelocityCamField.GetValue(stockCamera) as Camera;
            AppendCamera(sb, "parsek.fx", fxCamera);
            AppendCamera(sb, "parsek.depth", depthCamera);
            AppendCamera(sb, "parsek.velocity", velocityCamera);
            AppendCamera(sb, "stock.fx", stockCameraComponent);
            AppendCamera(sb, "stock.depth", stockDepth != null ? stockDepth.depthCamera : null);
            AppendCamera(sb, "stock.velocity", stockVelocityCamera);
            AppendCamera(sb, "main", mainCamera);
            Camera[] all = Camera.allCameras;
            sb.Append("| scene (enabled, ").Append(all.Length.ToString(CultureInfo.InvariantCulture)).Append("): ");
            for (int i = 0; i < all.Length; i++) AppendCamera(sb, all[i] != null ? all[i].name : "null", all[i]);
            sb.Append("| shaders: fx=[");
            Shader[] fxShaders = stockCamera != null ? stockCamera.ReplacementShaders : null;
            for (int i = 0; fxShaders != null && i < fxShaders.Length; i++)
            {
                Shader sh = fxShaders[i];
                sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append(':')
                    .Append(sh != null ? sh.name + (sh.isSupported ? "" : "(unsupported)") : "null").Append(' ');
            }
            Shader depthShader = stockDepth != null ? stockDepth.ReplacementShader : null;
            sb.Append("] depth=").Append(depthShader != null
                ? depthShader.name + (depthShader.isSupported ? "" : "(unsupported)") : "null");
            sb.Append(" lod=").Append(stockCamera != null && stockFx != null
                ? GhostAeroFx.ShaderLod(stockCamera.ReplacementShaders.Length, stockFx.detailLevel).ToString(CultureInfo.InvariantCulture)
                : "-");
            sb.Append(" fxTexture=").Append(stockCamera != null && stockCamera.texture != null ? stockCamera.texture.name : "null");
            return sb.ToString();
        }

        private static void AppendCamera(StringBuilder sb, string label, Camera cam)
        {
            if (cam == null)
            {
                sb.Append('[').Append(label).Append(": none] ");
                return;
            }
            RenderTexture tt = cam.targetTexture;
            string target = tt == null ? null : string.Format(CultureInfo.InvariantCulture,
                "{0}:{1}x{2}:{3}", tt.name, tt.width, tt.height, tt.format);
            Transform parent = cam.transform.parent;
            sb.Append('[').Append(label).Append(": ")
                .Append(GhostAeroFx.FormatCameraState(cam.name, cam.enabled, cam.gameObject.activeInHierarchy,
                    cam.depth, cam.clearFlags.ToString(), cam.cullingMask, target, cam.nearClipPlane,
                    cam.farClipPlane, cam.fieldOfView, cam.orthographic, cam.depthTextureMode.ToString()))
                .Append(" parent=").Append(parent != null ? parent.name : "none")
                .Append(" pos=").Append(cam.transform.position.ToString("F1"))
                .Append("] ");
        }

        private void SetCameras(bool on)
        {
            if (on == camerasOn) return;
            if (fxCamera == null || depthCamera == null) { camerasOn = false; return; }
            camerasOn = on;
            fxCamera.enabled = on;
            depthCamera.enabled = on;
            ParsekLog.Verbose("ReentryFx", $"Ghost aero FX cameras {(on ? "on" : "off")}");
        }

        private void DriveLight(Offer o, Bounds bounds)
        {
            if (ghostLight == null) return;
            AeroFxDrawValues v = GhostAeroFx.Blend(condensation, heat, o.fx, o.aeroState);
            float radius = Mathf.Max(1f, bounds.extents.magnitude);
            Transform lt = ghostLight.transform;
            // Stock's light shines back along the flow from the shock in front of the vessel.
            lt.position = bounds.center + o.flowDirection * (radius * 1.5f);
            lt.forward = -o.flowDirection;
            if (ghostLight.type == LightType.Point || ghostLight.type == LightType.Spot)
                ghostLight.range = Mathf.Max(stockLight != null && stockLight.type != LightType.Directional
                    ? stockLight.range : 0f, radius * 4f, 10f);
            ghostLight.color = v.Color;
            ghostLight.intensity = v.LightPower;
            SetLight(true, o.recIdx);
        }

        private void SetLight(bool on, int recIdx)
        {
            if (ghostLight == null) return;
            if (ghostLight.enabled != on) ghostLight.enabled = on;
            if (lightOwnerRecIdx == recIdx) return;
            lightOwnerRecIdx = recIdx;
            ParsekLog.Verbose("ReentryFx", on
                ? $"Ghost reentry light owned by ghost #{recIdx}"
                : "Ghost reentry light off");
        }
    }

    /// <summary>
    /// What the FX rig's diagnostic capture hands back: Parsek's FX camera rendered alone (stock's
    /// replacement shader with the ghost's values, then the plain sanity material), the ghost's
    /// velocity depth map read back as colour, and the one-line report also written to the log.
    /// </summary>
    internal sealed class GhostAeroFxDiagnosticCapture
    {
        public string Label;
        public int Width;
        public int Height;
        public Color32[] FxPixels;
        public Color32[] PlainPixels;
        public int DepthSize;
        public Color32[] DepthPixels;
        public string Report;
    }
}
