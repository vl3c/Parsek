using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// globals, computed from the ghost's own conditions. While stock's FX camera is on, no ghost
    /// draws: stock already paints every vessel in its reach, ghosts included.
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

        private readonly List<Offer> offers = new List<Offer>();
        private int offersFrame = -1;
        // One depth atlas for the scene, one tile per sheath slot: no render texture per ghost.
        private RenderTexture velocityAtlas;
        private readonly MaterialPropertyBlock[] blocks = new MaterialPropertyBlock[GhostAeroFx.MaxSheathsPerFrame];

        private AerodynamicsFX stockFx;
        private FXCamera stockCamera;
        private Camera stockCameraComponent;
        private FXDepthCamera stockDepth;
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

        private static GhostAeroFxRenderer EnsureInstance()
        {
            if (instance != null) return instance;
            if (!HighLogic.LoadedSceneIsFlight || FlightCamera.fetch == null) return null;
            var go = new GameObject("Parsek GhostAeroFx");
            instance = go.AddComponent<GhostAeroFxRenderer>();
            ParsekLog.Verbose("ReentryFx", "Ghost aero FX renderer created for the flight scene");
            return instance;
        }

        private void OnDestroy()
        {
            if (velocityCamera != null) velocityCamera.targetTexture = null;
            if (velocityAtlas != null)
            {
                velocityAtlas.Release();
                Destroy(velocityAtlas);
                velocityAtlas = null;
            }
            // The two copies live beside stock's cameras, not under this object.
            if (fxCamera != null) Destroy(fxCamera.gameObject);
            if (depthCamera != null) Destroy(depthCamera.gameObject);
            if (instance == this) instance = null;
            ParsekLog.Verbose("ReentryFx", "Ghost aero FX renderer destroyed");
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
            try
            {
                DriveFrame();
            }
            catch (Exception e)
            {
                // A failure here must not repeat as an exception every frame: drop the frame's
                // sheaths, switch the copies off and say why at most every 30 s.
                offers.Clear();
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

            if (offers.Count == 0)
            {
                SetCameras(false);
                SetLight(false, int.MinValue);
                return;
            }

            bool ready = ResolveStock();
            bool mapView = MapView.MapIsEnabled;
            bool thermalDebug = PhysicsGlobals.ThermalColorsDebug;
            int detailLevel = stockFx != null ? stockFx.detailLevel : 0;
            bool stockOn = ready && stockCameraComponent != null && stockCameraComponent.enabled;
            bool stockLightAvailable = ready && stockLight != null;
            bool stockLightOn = stockLightAvailable && stockLight.enabled;
            FlightCamera flightCamera = FlightCamera.fetch;
            Camera mainCamera = flightCamera != null ? flightCamera.mainCamera : null;
            if (mainCamera == null)
            {
                offers.Clear();
                SetCameras(false);
                SetLight(false, int.MinValue);
                return;
            }
            frameWatch.Reset();
            frameWatch.Start();
            float reach = GhostAeroFx.StockReach(flightCamera.Distance);
            Vector3 cameraPos = mainCamera.transform.position;

            int drawn = 0, stockDraws = 0, outOfReach = 0, capped = 0, isolated = 0, other = 0;
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

                GhostAeroSheathDecision decision = GhostAeroFx.DecideSheath(o.fx, mapView, thermalDebug,
                    detailLevel, ready, stockOn, inReach, haveBounds ? meshCount : 0, o.isolated, drawn);
                if (decision == GhostAeroSheathDecision.Draw)
                {
                    if (DrawSheath(o, bounds, drawn))
                        drawn++;
                    else
                        decision = GhostAeroSheathDecision.NoMeshes;
                }
                switch (decision)
                {
                    case GhostAeroSheathDecision.Draw: break;
                    case GhostAeroSheathDecision.StockDraws: stockDraws++; break;
                    case GhostAeroSheathDecision.OutOfReach: outOfReach++; break;
                    case GhostAeroSheathDecision.FrameCap: capped++; break;
                    case GhostAeroSheathDecision.Isolated: isolated++; break;
                    default: other++; break;
                }
                NoteDecision(o, decision, distance, reach);

                GhostAeroLightDecision light = GhostAeroFx.DecideLight(o.fx, mapView,
                    stockLightAvailable, stockLightOn, inReach);
                if (light == GhostAeroLightDecision.Offer && !o.isolated
                    && GhostAeroFx.LightOfferWins(haveLight, lightDistance, distance))
                {
                    haveLight = true;
                    lightDistance = distance;
                    lightOffer = o;
                    lightBounds = haveBounds ? bounds : new Bounds(center, Vector3.one * (radius * 2f));
                }
            }

            SetCameras(drawn > 0);
            if (drawn > 0) DriveCameras(flightCamera, mainCamera, reach);
            if (haveLight) DriveLight(lightOffer, lightBounds);
            else SetLight(false, int.MinValue);

            frameWatch.Stop();
            ParsekLog.VerboseRateLimited("ReentryFx", "aero-frame", string.Format(CultureInfo.InvariantCulture,
                "Ghost aero FX frame: offers={0} drawn={1} stockDraws={2} outOfReach={3} capped={4} " +
                "isolated={5} other={6} light={7} reach={8:F0}m stockOn={9} ready={10} cpuMs={11:F3}",
                offers.Count, drawn, stockDraws, outOfReach, capped, isolated, other,
                haveLight ? "#" + lightOffer.recIdx.ToString(CultureInfo.InvariantCulture) : "none",
                reach, stockOn, ready, frameWatch.Elapsed.TotalMilliseconds), 5.0);
            offers.Clear();
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

        private bool DrawSheath(Offer o, Bounds bounds, int slot)
        {
            AeroFxDrawValues v = GhostAeroFx.Blend(condensation, heat, o.fx, o.aeroState);
            int lod = GhostAeroFx.ShaderLod(stockCamera.ReplacementShaders.Length, stockFx.detailLevel);
            Vector3 effectDirection = -o.flowDirection;

            // The ghost's own occlusion map: stock's velocity camera, boxed on the ghost.
            GhostAeroFx.VelocityCamBox(bounds.size.magnitude, out float box, out float far);
            // Each drawing ghost renders into its own tile of the one shared atlas; the tile's
            // offset is folded into the projection the shader reads, so its lookups land there.
            if (velocityAtlas == null)
            {
                velocityAtlas = new RenderTexture(
                    GhostAeroFx.VelocityDepthResolution * GhostAeroFx.MaxSheathsPerFrame,
                    GhostAeroFx.VelocityDepthResolution, 32, RenderTextureFormat.Depth);
                velocityAtlas.name = "Parsek ghost aero velocity depth atlas";
                velocityAtlas.Create();
            }
            GhostAeroFx.VelocityAtlasTile(slot, GhostAeroFx.MaxSheathsPerFrame,
                out float rectX, out float rectWidth, out float ndcScaleX, out float ndcOffsetX);
            RenderTexture depthMap = velocityAtlas;
            Transform vt = velocityCamera.transform;
            vt.position = bounds.center - effectDirection * box;
            vt.forward = effectDirection;
            velocityCamera.orthographicSize = box;
            velocityCamera.farClipPlane = far;
            velocityCamera.targetTexture = velocityAtlas;
            velocityCamera.rect = new Rect(rectX, 0f, rectWidth, 1f);
            velocityCamera.aspect = 1f;
            velocityCamera.Render();

            MaterialPropertyBlock mpb = blocks[slot] ?? (blocks[slot] = new MaterialPropertyBlock());
            mpb.Clear();
            Vector3 vp = vt.position;
            var fxColor = new Color(1f, 1f, 1f, GhostAeroFx.FxColorAlpha(v.Intensity, lod));
            Matrix4x4 world2Velocity = Matrix4x4.TRS(Vector3.zero,
                Quaternion.FromToRotation(Vector3.up, effectDirection), Vector3.one);
            Matrix4x4 depthCamMatrix = velocityCamera.worldToCameraMatrix;
            Matrix4x4 tile = Matrix4x4.identity;
            tile.m00 = ndcScaleX;
            tile.m03 = ndcOffsetX;
            Matrix4x4 depthProjMatrix = tile * velocityCamera.projectionMatrix;
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
            if (slot == 0)
            {
                // The same values as globals, as FXCamera.OnPreRender sets them, for whatever the
                // replacement shaders read outside the per-draw block. Stock's camera is off on
                // every frame a ghost draws and sets its own before it renders again.
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

        private void DriveCameras(FlightCamera flightCamera, Camera mainCamera, float reach)
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
