using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

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
                SetCameras(false);
                SetLight(false, int.MinValue);
                ParsekLog.WarnRateLimited("ReentryFx", "aero-frame-failed",
                    $"Ghost aero FX frame failed, no ghost sheath this frame: {e.GetType().Name}: {e.Message}", 30.0);
            }
        }

        private void DriveFrame()
        {
            int frame = Time.frameCount;
            if (offersFrame != frame) offers.Clear();
            if (maskGhostsFrame != frame) maskGhosts.Clear();
            mask.ClearCandidates();

            if (offers.Count == 0 && maskGhosts.Count == 0)
            {
                SetCameras(false);
                SetLight(false, int.MinValue);
                TrimDepthPool(0, Time.unscaledTime);
                return;
            }

            bool ready = ResolveStock();
            bool mapView = MapView.MapIsEnabled;
            bool thermalDebug = PhysicsGlobals.ThermalColorsDebug;
            int detailLevel = stockFx != null ? stockFx.detailLevel : 0;
            bool stockOn = ready && stockCameraComponent != null && stockCameraComponent.enabled;
            bool stockLightAvailable = ready && stockLight != null;
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
                bool haveBounds = TryGhostBounds(o.info, out Bounds bounds);
                Vector3 center = haveBounds ? bounds.center : o.root.position;
                float radius = haveBounds ? bounds.extents.magnitude : Mathf.Max(1f, o.info.vesselLength * 0.5f);
                float distance = Vector3.Distance(cameraPos, center);
                bool inReach = GhostAeroFx.WithinReach(distance, radius, reach);
                int meshCount = o.info.fireShellMeshes != null ? o.info.fireShellMeshes.Count : 0;
                int visibleMeshes = haveBounds ? meshCount : 0;

                GhostAeroSheathDecision decision = GhostAeroFx.DecideSheath(o.fx, mapView, thermalDebug,
                    detailLevel, ready, inReach, visibleMeshes, o.isolated);
                if (decision == GhostAeroSheathDecision.Draw)
                {
                    if (DrawSheath(o, bounds, drawn, stockOn, now))
                        drawn++;
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
            if (drawn > 0) DriveCameras(mainCamera, reach);
            if (haveLight) DriveLight(lightOffer, lightBounds);
            else SetLight(false, int.MinValue);
            TrimDepthPool(drawn, now);

            frameWatch.Stop();
            int failedMaskWrites = mask.TakeFailedWrites();
            ParsekLog.VerboseRateLimited("ReentryFx", "aero-frame", string.Format(CultureInfo.InvariantCulture,
                "Ghost aero FX frame: offers={0} drawn={1} outOfReach={2} isolated={3} other={4} " +
                "light={5} reach={6:F0}m stockOn={7} ready={8} maskGhosts={9} maskRenderers={10} " +
                "lastMasked={11} maskWriteFailures={12} depthMaps={13} depthRes={14} cpuMs={15:F3}",
                offers.Count, drawn, outOfReach, isolated, other,
                haveLight ? "#" + lightOffer.recIdx.ToString(CultureInfo.InvariantCulture) : "none",
                reach, stockOn, ready, maskedGhosts, mask.CandidateCount, mask.LastMaskedCount,
                failedMaskWrites, depthPool.Count, depthPoolResolution,
                frameWatch.Elapsed.TotalMilliseconds), 5.0);
            offers.Clear();
            maskGhosts.Clear();
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

        /// <summary>World bounds of the ghost meshes the sheath draws (active, enabled renderers).</summary>
        private static bool TryGhostBounds(ReentryFxInfo info, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            List<FireShellMesh> meshes = info.fireShellMeshes;
            if (meshes == null) return false;
            for (int m = 0; m < meshes.Count; m++)
            {
                FireShellMesh fsm = meshes[m];
                if (fsm.mesh == null || fsm.renderer == null || !fsm.renderer.enabled) continue;
                if (!fsm.renderer.gameObject.activeInHierarchy) continue;
                if (!any) { bounds = fsm.renderer.bounds; any = true; }
                else bounds.Encapsulate(fsm.renderer.bounds);
            }
            return any;
        }

        private bool DrawSheath(Offer o, Bounds bounds, int slot, bool stockOn, float now)
        {
            AeroFxDrawValues v = GhostAeroFx.Blend(condensation, heat, o.fx, o.aeroState);
            int lod = GhostAeroFx.ShaderLod(stockCamera.ReplacementShaders.Length, stockFx.detailLevel);
            Vector3 effectDirection = -o.flowDirection;

            // The ghost's own occlusion map: stock's velocity camera, boxed on the ghost.
            GhostAeroFx.VelocityCamBox(bounds.size.magnitude, out float box, out float far);
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

            int submitted = 0;
            List<FireShellMesh> meshes = o.info.fireShellMeshes;
            for (int m = 0; m < meshes.Count; m++)
            {
                FireShellMesh fsm = meshes[m];
                if (fsm.mesh == null || fsm.transform == null || fsm.renderer == null) continue;
                if (!fsm.renderer.enabled || !fsm.transform.gameObject.activeInHierarchy) continue;
                Material[] materials = fsm.renderer.sharedMaterials;
                if (materials == null) continue;
                Matrix4x4 matrix = fsm.transform.localToWorldMatrix;
                int subMeshes = Mathf.Min(fsm.mesh.subMeshCount, materials.Length);
                for (int s = 0; s < subMeshes; s++)
                {
                    if (materials[s] == null) continue;
                    Graphics.DrawMesh(fsm.mesh, matrix, materials[s], SheathLayer, fxCamera, s, mpb,
                        false, false);
                    submitted++;
                }
            }

            ParsekLog.VerboseRateLimited("ReentryFx", $"aero-sheath-{o.recIdx}", string.Format(
                CultureInfo.InvariantCulture,
                "Ghost #{0} sheath drawn: fx={1:F3} state={2:F2} submeshes={3} lod={4} alpha={5:F3} " +
                "length={6:F2} color=({7:F2},{8:F2},{9:F2}) box={10:F1}m",
                o.recIdx, o.fx, o.aeroState, submitted, lod,
                GhostAeroFx.FxColorAlpha(v.Intensity, lod), v.Length, v.Color.r, v.Color.g, v.Color.b, box),
                5.0);
            return submitted > 0;
        }

        private void DriveCameras(Camera mainCamera, float reach)
        {
            int lod = GhostAeroFx.ShaderLod(stockCamera.ReplacementShaders.Length, stockFx.detailLevel);
            float fov = mainCamera.fieldOfView;

            depthCamera.CopyFrom(stockDepth.depthCamera);
            depthCamera.fieldOfView = fov;
            depthCamera.farClipPlane = reach;
            depthCamera.SetReplacementShader(stockDepth.ReplacementShader, "RenderType");

            fxCamera.CopyFrom(stockCameraComponent);
            fxCamera.cullingMask = 1 << SheathLayer;
            fxCamera.fieldOfView = fov;
            fxCamera.farClipPlane = reach;
            fxCamera.SetReplacementShader(stockCamera.ReplacementShaders[lod], "RenderType");

            // Stock's two cameras share depth 2 or 3 (FXCamera.OnCameraChange) with the copies
            // CopyFrom just made; render strictly after them so stock's pass and ours never
            // interleave, depth copy first.
            float after = GhostAeroFx.CopyCameraDepth(stockDepth.depthCamera.depth, stockCameraComponent.depth);
            depthCamera.depth = after;
            fxCamera.depth = after + GhostAeroFx.CopyCameraDepthStep;
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
}
