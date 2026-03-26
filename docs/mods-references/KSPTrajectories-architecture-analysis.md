# KSPTrajectories Architecture Analysis
**For Parsek Project - Map View Rendering, Custom Overlays, and NavBall Integration Reference**

Based on thorough exploration of the KSPTrajectories mod (by neuoy/fat-lobyte/PiezPiedPy, GPL-3.0), this document provides detailed analysis of custom rendering in map view and flight view, coordinate transforms between world/scaled/screen space, procedural mesh generation, GL-based drawing, camera integration, NavBall marker cloning, and target management patterns -- all directly applicable to Parsek's ghost vessel map presence and custom orbit line rendering.

---

## 1. OVERALL PROJECT STRUCTURE AND ORGANIZATION

### File Organization
The project consists of 28 C# source files organized into four functional groups:

**Display (Rendering & UI):**
- `MapOverlay.cs` - Map view trajectory line rendering via procedural ribbon meshes
- `FlightOverlay.cs` - In-flight trajectory line rendering via LineRenderer
- `GfxUtil.cs` - Reusable MonoBehaviour components (TrajectoryLine, TargetingCross)
- `NavBallOverlay.cs` - Custom NavBall markers by cloning `progradeVector`
- `MainGUI.cs` - PopupDialog-based settings window
- `AppLauncherButton.cs` - Stock/Blizzy toolbar integration

**Prediction Engine:**
- `Trajectory.cs` - Core trajectory computation (patched conics + atmospheric RK4 integration)
- `DescentProfile.cs` - Angle-of-attack profile management
- `TargetProfile.cs` - Landing target position management
- `StockAeroUtil.cs` - Stock aerodynamic model interface

**Aerodynamic Models:**
- `AeroDynamicModel.cs` - Abstract aero model base
- `AeroDynamicModelFactory.cs` - Factory for stock/FAR model selection
- `AeroForceCache.cs` - Cached aerodynamic force lookup
- `FARModel.cs` / `StockModel.cs` - Concrete aero model implementations

**Utilities:**
- `GLUtils.cs` - Immediate-mode GL drawing (from MechJeb, adapted)
- `Util.cs` - Logging, math, reflection, game state helpers
- `DebugLines.cs` - Debug visualization via LineRenderer pool
- `Profiler.cs` - Performance profiling utilities

**Other:**
- `Trajectories.cs` - Main ScenarioModule, lifecycle orchestrator
- `TrajectoriesVesselSettings.cs` - Per-vessel PartModule for persistent settings
- `API.cs` - Public API for other mods
- `Settings.cs` - Global config file management
- `ToolbarWrapper.cs` - Blizzy toolbar abstraction (3rd party)

### Architectural Pattern
The mod uses a **ScenarioModule + static class** architecture. The `Trajectories` class is a `[KSPScenario]` that acts as the lifecycle orchestrator:

```csharp
[KSPScenario(ScenarioCreationOptions.AddToAllGames, new[] { GameScenes.FLIGHT })]
internal sealed class Trajectories : ScenarioModule
```

All major subsystems (`Trajectory`, `MapOverlay`, `FlightOverlay`, `NavBallOverlay`, `MainGUI`) are **static classes** with `Start()`, `Update()`, and `Destroy()` methods called from the ScenarioModule's lifecycle. This avoids MonoBehaviour proliferation while still participating in Unity's update loop.

The exception is the inner `MapTrajectoryRenderer` class, which must be a MonoBehaviour to receive `OnPreRender` callbacks from the camera it's attached to.

---

## 2. MAP VIEW RENDERING (MapOverlay.cs)

This is the most directly relevant subsystem for Parsek's ghost orbit line rendering.

### Architecture: Camera-Attached MonoBehaviour with Procedural Meshes

The core rendering pipeline works as follows:

1. A `MapTrajectoryRenderer` MonoBehaviour is attached to the `PlanetariumCamera`
2. Its `OnPreRender()` fires before each camera render
3. Inside `OnPreRender()`, meshes are rebuilt from current trajectory data
4. The meshes use the stock orbit line material for visual consistency

#### Initialization (Start)

```csharp
internal static void Start()
{
    material ??= MapView.fetch.orbitLinesMaterial;
    map_renderer = PlanetariumCamera.Camera.gameObject.AddComponent<MapTrajectoryRenderer>();
    map_renderer.Visible = false;
}
```

**Key pattern:** The material is obtained from `MapView.fetch.orbitLinesMaterial` -- this is the same material KSP uses for stock orbit lines, ensuring visual consistency. For Parsek, this could be used as a base material, potentially with color modification for ghost orbits.

#### Layer Constants

```csharp
private const int layer2D = 31;
private const int layer3D = 24;
```

KSP map view has two modes controlled by `MapView.Draw3DLines`:
- **Layer 24** = 3D mode (when zoomed in, lines have perspective)
- **Layer 31** = 2D mode (when zoomed out, lines are flat overlays)

Every mesh must switch layers based on this flag:

```csharp
mesh_found.layer = MapView.Draw3DLines ? layer3D : layer2D;
```

**For Parsek:** Any custom map view rendering must handle both layer modes or the lines will disappear at certain zoom levels.

### The Ribbon Mesh Approach (MakeRibbonEdge)

Rather than using Unity's `LineRenderer`, map view lines are built as **procedural triangle-strip ribbon meshes** -- two vertices per sample point, forming a screen-space-width ribbon that always faces the camera.

```csharp
private static void MakeRibbonEdge(Vector3d prevPos, Vector3d edgeCenter, float width,
    Vector3[] vertices, int startIndex)
{
    Camera camera = PlanetariumCamera.Camera;

    // Convert world positions to SCREEN space
    Vector3 start = camera.WorldToScreenPoint(ScaledSpace.LocalToScaledSpace(prevPos));
    Vector3 end = camera.WorldToScreenPoint(ScaledSpace.LocalToScaledSpace(edgeCenter));

    // Compute perpendicular direction in screen space for ribbon width
    Vector3 segment = new Vector3(end.y - start.y, start.x - end.x, 0).normalized * (width * 0.5f);

    // 2D mode: set Z to fixed depth (in front or behind camera plane)
    if (!MapView.Draw3DLines)
    {
        float dist = Screen.height / 2 + 0.01f;
        start.z = start.z >= 0.15f ? dist : -dist;
        end.z = end.z >= 0.15f ? dist : -dist;
    }

    Vector3 p0 = (end + segment);
    Vector3 p1 = (end - segment);

    // 3D mode: convert back from screen to world space
    if (MapView.Draw3DLines)
    {
        p0 = camera.ScreenToWorldPoint(p0);
        p1 = camera.ScreenToWorldPoint(p1);
    }

    vertices[startIndex + 0] = p0;
    vertices[startIndex + 1] = p1;

    // Degenerate triangles for segments crossing the camera plane (2D mode)
    if (!MapView.Draw3DLines && (start.z > 0) != (end.z > 0))
    {
        vertices[startIndex + 0] = vertices[startIndex + 1];
        if (startIndex >= 2)
            vertices[startIndex - 2] = vertices[startIndex - 1];
    }
}
```

**Critical coordinate transform pipeline:**
1. World position -> `ScaledSpace.LocalToScaledSpace()` -> Scaled space position
2. Scaled space -> `camera.WorldToScreenPoint()` -> Screen space
3. Compute perpendicular offset in screen space (constant pixel width)
4. Screen space -> `camera.ScreenToWorldPoint()` -> Back to scaled world space (3D mode)

**The `ScaledSpace.LocalToScaledSpace()` call is essential.** KSP's map view camera operates in "scaled space" where 1 unit = 6000 meters. All world positions must be scaled before being projected.

**2D mode handling:** When `MapView.Draw3DLines` is false, the Z coordinate is set to a fixed depth (`Screen.height / 2 + 0.01f`) so the ribbon appears as a flat overlay. Segments that cross the camera plane are collapsed to degenerate triangles (zero area) to prevent visual artifacts.

This technique (noted as "Code taken from RemoteTech mod") is the standard approach used across many KSP mods for map view line rendering.

### Mesh Construction from Orbit (InitMeshFromOrbit)

The orbit is sampled at adaptive intervals based on true anomaly:

```csharp
private static void InitMeshFromOrbit(Vector3 bodyPosition, Mesh mesh, Orbit orbit,
    double startTime, double duration, Color color)
{
    int steps = 128;
    double maxDT = Math.Max(1.0, duration / steps);
    double maxDTA = 2.0 * Math.PI / steps;  // Max true anomaly change per step

    // Adaptive stepping: binary search to keep dTA under maxDTA
    while (true)
    {
        double time = prevTime + maxDT;
        for (int count = 0; count < 100; ++count)
        {
            double ta = orbit.TrueAnomalyAtUT(time);
            while (ta < prevTA)
                ta += 2.0 * Math.PI;
            if (ta - prevTA <= maxDTA)
            {
                prevTA = ta;
                break;
            }
            time = (prevTime + time) * 0.5;  // Bisect
        }
        // ...store time step...
    }
```

Then positions are obtained from the KSP `Orbit` object and converted:

```csharp
Vector3d curMeshPos = Util.SwapYZ(orbit.getRelativePositionAtUT(time));
// SwapYZ is needed because KSP's orbital math uses a different coordinate convention
if (Settings.BodyFixedMode)
    curMeshPos = Trajectory.CalculateRotatedPosition(orbit.referenceBody, curMeshPos, time);
curMeshPos += bodyPosition;  // Body-relative -> world position
```

**The `SwapYZ` call** swaps Y and Z axes. KSP's `Orbit.getRelativePositionAtUT()` returns positions in the orbital math frame (Y-up for orbital plane), but the world frame has Z and Y swapped. This is a common gotcha.

The mesh is then assembled with the standard triangle strip pattern:

```csharp
mesh.Clear();
mesh.vertices = vertices;
mesh.uv = uvs;
mesh.colors = colors;
mesh.triangles = triangles;
mesh.RecalculateBounds();
mesh.MarkDynamic();  // Hint to Unity: this mesh changes frequently
```

**UV coordinates** are set to `(0.8f, 0)` / `(0.8f, 1)` for all vertices. The `0.8` U value samples a specific region of the orbit line material texture (likely the solid-line region of a texture atlas). Different U values could produce different line styles (dashed, dotted) if the material texture supports it.

### Mesh Reuse Pool

Rather than creating and destroying GameObjects each frame, the renderer maintains a pool:

```csharp
internal List<GameObject> meshes = new List<GameObject>();
```

At the start of each render pass, all meshes are deactivated:

```csharp
foreach (GameObject mesh in map_renderer.meshes)
    mesh.SetActive(false);
```

Then `GetMesh()` either reactivates an existing inactive mesh or creates a new one:

```csharp
private static GameObject GetMesh()
{
    // Find inactive mesh
    foreach (GameObject mesh in map_renderer.meshes)
    {
        if (!mesh.activeSelf)
        {
            mesh.SetActive(true);
            return mesh;
        }
    }
    // Create new mesh if none available
    GameObject newMesh = new GameObject();
    newMesh.AddComponent<MeshFilter>();
    MeshRenderer renderer = newMesh.AddComponent<MeshRenderer>();
    renderer.receiveShadows = false;
    newMesh.layer = MapView.Draw3DLines ? layer3D : layer2D;
    map_renderer.meshes.Add(newMesh);
    return newMesh;
}
```

**For Parsek:** This pool pattern is efficient for per-frame mesh rebuilding. Ghost orbit lines could use the same approach -- a pool of mesh GameObjects, rebuilt each frame in `OnPreRender`.

### Crosshair Marker Rendering (InitMeshCrosshair)

Impact and target positions are rendered as crosshair markers built from 8 vertices (two perpendicular quads):

```csharp
private static void InitMeshCrosshair(CelestialBody body, Mesh mesh, Vector3 position, Color color)
{
    // Scale crosshair based on camera distance
    float crossThickness = Mathf.Min(line_width * 0.001f
        * Vector3.Distance(camPos, position), 6000.0f);
    float crossSize = crossThickness * 10.0f;

    // Two perpendicular rectangles forming a + shape
    Vector3 crossV1 = Vector3.Cross(position, Vector3.right).normalized;
    Vector3 crossV2 = Vector3.Cross(position, crossV1).normalized;

    // ...vertex positions computed relative to position...

    // Convert to scaled space for map view
    for (int i = 0; i < vertices.Length; ++i)
    {
        vertices[i] = MapView.Draw3DLines ?
            (Vector3)ScaledSpace.LocalToScaledSpace(vertices[i] + body.position)
            : new Vector3(0, 0, 0);  // Hidden in 2D mode
    }
}
```

**Key observation:** The crosshair is only displayed in 3D mode. In 2D mode, vertices are zeroed out. The altitude is hacked up by 1200m to prevent it from being hidden under terrain in map view.

---

## 3. FLIGHT VIEW RENDERING (FlightOverlay.cs + GfxUtil.cs)

### LineRenderer Approach for Flight View

Unlike map view (which uses procedural meshes), flight view uses Unity's `LineRenderer` via the `GfxUtil.TrajectoryLine` component:

```csharp
internal static void Start()
{
    line = FlightCamera.fetch.mainCamera.gameObject
        .AddComponent<GfxUtil.TrajectoryLine>();
    line.Scene = GameScenes.FLIGHT;
    impact_cross = FlightCamera.fetch.mainCamera.gameObject
        .AddComponent<GfxUtil.TargetingCross>();
    target_cross = FlightCamera.fetch.mainCamera.gameObject
        .AddComponent<GfxUtil.TargetingCross>();
}
```

**Key pattern:** Components are attached to `FlightCamera.fetch.mainCamera.gameObject`. This ensures the components receive camera lifecycle callbacks (`OnPreRender`, `OnPostRender`).

### TrajectoryLine Component (GfxUtil.cs)

```csharp
internal sealed class TrajectoryLine : MonoBehaviour
{
    private const float MIN_WIDTH = 0.025f;
    private const float MAX_WIDTH = 250f;
    private const float DIST_DIV = 1e3f;

    internal void Awake()
    {
        game_object = new GameObject("Trajectories_LineRenderer");
        game_object.transform.parent = gameObject.transform;

        line_renderer = game_object.AddComponent<LineRenderer>();

        material = new Material(Shader.Find("KSP/Orbit Line"));
        material ??= new Material(Shader.Find("KSP/Particles/Additive")); // fallback

        line_renderer.material = material;
        line_renderer.startColor = XKCDColors.BlueBlue;
        line_renderer.endColor = XKCDColors.BlueBlue;
        line_renderer.numCapVertices = 5;
        line_renderer.numCornerVertices = 7;
        line_renderer.startWidth = MIN_WIDTH;
        line_renderer.endWidth = MIN_WIDTH;
        line_renderer.shadowCastingMode = ShadowCastingMode.Off;
    }
```

**Shader selection:** `"KSP/Orbit Line"` is the primary shader, with `"KSP/Particles/Additive"` as fallback. This is used consistently throughout the codebase (MapOverlay, FlightOverlay, DebugLines, GLUtils).

**Distance-adaptive width:** Line width is adjusted in `OnPreRender` based on camera distance:

```csharp
internal void OnPreRender()
{
    ref_camera = CameraManager.GetCurrentCamera();
    cam_pos = ref_camera.transform.position;
    line_renderer.startWidth = Mathf.Clamp(
        Vector3.Distance(cam_pos, line_renderer.GetPosition(0)) / DIST_DIV,
        MIN_WIDTH, MAX_WIDTH);
    line_renderer.endWidth = Mathf.Clamp(
        Vector3.Distance(cam_pos,
            line_renderer.GetPosition(line_renderer.positionCount - 1)) / DIST_DIV,
        MIN_WIDTH, MAX_WIDTH);
}
```

**For Parsek:** In flight view, LineRenderer is simpler and adequate. The width scaling pattern (distance / 1000, clamped) provides good visual behavior at all zoom levels.

### TargetingCross Component (GfxUtil.cs)

The targeting cross uses GL immediate-mode rendering in `OnPostRender`:

```csharp
internal sealed class TargetingCross : MonoBehaviour
{
    internal void OnPostRender()
    {
        // Get lat/lon for the position
        Body.GetLatLonAlt(Position.Value, out latitude, out longitude, out altitude);

        // Viewport visibility check
        screen_point = FlightCamera.fetch.mainCamera.WorldToViewportPoint(Position.Value);
        if (!(screen_point.z >= 0f && screen_point.x >= 0f && screen_point.x <= 1f
              && screen_point.y >= 0f && screen_point.y <= 1f))
            return;

        // Distance-based sizing
        size = Mathf.Clamp(Vector3.Distance(
            FlightCamera.fetch.mainCamera.transform.position, Position.Value) / DIST_DIV,
            MIN_SIZE, MAX_SIZE);

        // Draw using GL
        GLUtils.DrawGroundMarker(Body, latitude, longitude, Color, false, 0d, size);
    }
}
```

---

## 4. GL DRAWING UTILITIES (GLUtils.cs)

This file (originally from MechJeb) provides immediate-mode GL drawing functions useful for markers and paths.

### Material Setup

```csharp
static Material material
{
    get
    {
        _material ??= new Material(Shader.Find("KSP/Orbit Line"));
        _material ??= new Material(Shader.Find("KSP/Particles/Additive"));
        return _material;
    }
}
```

### Ground Marker Drawing

```csharp
public static void DrawGroundMarker(CelestialBody body, double latitude, double longitude,
    Color c, bool map, double rotation = 0, double radius = 0)
{
    Vector3d up = body.GetSurfaceNVector(latitude, longitude);
    var height = body.pqsController.GetSurfaceHeight(...);
    if (height < body.Radius) height = body.Radius;
    Vector3d center = body.position + height * up;

    // Determine camera position (different for map vs flight)
    Vector3d camPos = map
        ? ScaledSpace.ScaledToLocalSpace(PlanetariumCamera.Camera.transform.position)
        : (Vector3d)FlightCamera.fetch.mainCamera.transform.position;

    // Occlusion check (skip if behind planet)
    if (IsOccluded(center, body, camPos)) return;

    // Draw three triangles forming a marker
    GLTriangle(center, center + radius * (...), center + radius * (...), c, map);
    GLTriangle(...);  // 120 degrees offset
    GLTriangle(...);  // 240 degrees offset
}
```

### GL Vertex Projection

The dual-mode projection helper is worth studying:

```csharp
public static void GLVertex(Vector3d worldPosition, bool map = false)
{
    Vector3 screenPoint = map
        ? PlanetariumCamera.Camera.WorldToViewportPoint(
              ScaledSpace.LocalToScaledSpace(worldPosition))
        : FlightCamera.fetch.mainCamera.WorldToViewportPoint(worldPosition);
    GL.Vertex3(screenPoint.x, screenPoint.y, 0);
}
```

**Key distinction:** Map view requires `ScaledSpace.LocalToScaledSpace()` before projection, flight view does not.

### Horizon Occlusion Test

```csharp
public static bool IsOccluded(Vector3d worldPosition, CelestialBody byBody, Vector3d camPos)
{
    Vector3d VC = (byBody.position - camPos) / (byBody.Radius - 100);
    Vector3d VT = (worldPosition - camPos) / (byBody.Radius - 100);
    double VT_VC = Vector3d.Dot(VT, VC);

    if (VT_VC < VC.sqrMagnitude - 1) return false;  // In front of horizon
    return VT_VC * VT_VC / VT.sqrMagnitude > VC.sqrMagnitude - 1;
}
```

This implements the cesium horizon-culling algorithm. The 100m subtraction from body radius provides a small margin so markers don't pop in/out at the exact horizon.

**For Parsek:** This occlusion test is useful for deciding whether to draw ghost markers in flight view (e.g., a ghost on the far side of a body should be hidden).

### Dashed Path Drawing

```csharp
public static void DrawPath(CelestialBody mainBody, List<Vector3d> points, Color c,
    bool map, bool dashed = false)
{
    GL.PushMatrix();
    material?.SetPass(0);
    GL.LoadPixelMatrix();
    GL.Begin(GL.LINES);
    GL.Color(c);

    int step = (dashed ? 2 : 1);  // Skip every other segment for dashes
    for (int i = 0; i < points.Count - 1; i += step)
    {
        if (!IsOccluded(points[i], mainBody, camPos)
            && !IsOccluded(points[i + 1], mainBody, camPos))
            GLPixelLine(points[i], points[i + 1], map);
    }

    GL.End();
    GL.PopMatrix();
}
```

**For Parsek:** The dashed line technique (stepping by 2 instead of 1 through the point list) is extremely simple but effective. This could be used to visually distinguish ghost orbit lines from stock ones without any custom shader work. However, the GL immediate-mode approach is less performant than the mesh-based approach used in `MapOverlay` -- for per-frame redrawing of potentially many ghost orbits, the mesh approach is preferred.

---

## 5. NAVBALL INTEGRATION (NavBallOverlay.cs)

This subsystem demonstrates how to add custom markers to the NavBall by cloning existing stock markers.

### Cloning the Prograde Vector

```csharp
internal static void Start()
{
    navball = UnityEngine.Object.FindObjectOfType<NavBall>();

    if (navball != null)
    {
        // Clone the prograde vector marker
        guide_transform = (Transform)GameObject.Instantiate(
            navball.progradeVector, navball.progradeVector.parent);

        // Scale it down
        guide_transform.gameObject.transform.localScale =
            guide_transform.gameObject.transform.localScale * SCALE;  // SCALE = 0.5f

        // Replace texture
        guide_renderer = guide_transform.GetComponent<Renderer>();
        guide_renderer.material.SetTexture("_MainTexture", guide_texture);
        guide_renderer.material.SetTextureOffset("_MainTexture", Vector2.zero);
        guide_renderer.material.SetTextureScale("_MainTexture", Vector2.one);
    }
}
```

**Key pattern:** `navball.progradeVector` is cloned and reparented under the same parent. The texture is swapped via `material.SetTexture("_MainTexture", ...)`. The material property name is `_MainTexture` (not `_MainTex`).

### Positioning the Marker

```csharp
internal static void Update()
{
    // Calculate the direction vector for the marker
    reference = CalcReference();

    // Position on NavBall using gymbal rotation and unit scale
    guide_transform.gameObject.transform.localPosition =
        navball.attitudeGymbal * (CorrectedDirection.Value * navball.VectorUnitScale);

    // Hide if behind the NavBall sphere
    guide_transform.gameObject.SetActive(
        guide_transform.gameObject.transform.localPosition.z >= navball.VectorUnitCutoff);
}
```

**Key NavBall properties:**
- `navball.attitudeGymbal` - Quaternion that rotates world directions into NavBall space
- `navball.VectorUnitScale` - Scale factor that maps unit vectors to NavBall surface positions
- `navball.VectorUnitCutoff` - Z threshold below which markers are behind the sphere

**For Parsek:** If ghost vessels need NavBall indicators (e.g., showing the ghost's relative position), this cloning technique avoids having to reverse-engineer the NavBall rendering system.

### Custom Textures

Textures are loaded from PNG files at runtime:

```csharp
string TrajTexturePath = KSPUtil.ApplicationRootPath + "GameData/Trajectories/Textures/";
guide_texture.LoadImage(File.ReadAllBytes(TrajTexturePath + "GuideNavMarker.png"));
```

This uses `Texture2D.LoadImage()` with raw bytes rather than the GameDatabase texture loader.

---

## 6. COORDINATE TRANSFORM REFERENCE

The mod demonstrates the complete set of KSP coordinate transforms needed for rendering across different views.

### World Space <-> Scaled Space

```csharp
// World -> Scaled (for map view rendering)
Vector3 scaledPos = ScaledSpace.LocalToScaledSpace(worldPosition);

// Scaled -> World (for getting camera position in world coords)
Vector3d worldCamPos = ScaledSpace.ScaledToLocalSpace(
    PlanetariumCamera.Camera.transform.position);
```

The scale factor is 1:6000 (1 scaled unit = 6000 world meters).

### Body-Relative Positions

```csharp
// Body-relative orbital position -> World position
Vector3d curMeshPos = Util.SwapYZ(orbit.getRelativePositionAtUT(time));
curMeshPos += bodyPosition;  // body.position is the world-space body center
```

The `SwapYZ` is required because `Orbit.getRelativePositionAtUT()` returns positions in KSP's orbital math frame which has Y and Z swapped compared to the world frame.

### Body-Fixed Rotation

```csharp
internal static Vector3d CalculateRotatedPosition(CelestialBody body,
    Vector3d relativePosition, double time)
{
    float angle = (float)(-(time - Planetarium.GetUniversalTime())
        * body.angularVelocity.magnitude / Math.PI * 180.0);
    Quaternion bodyRotation = Quaternion.AngleAxis(angle, body.angularVelocity.normalized);
    return bodyRotation * relativePosition;
}
```

This rotates a body-relative position to account for body rotation at a future/past time relative to the current time. The angular velocity is applied as a time-delta rotation.

**Note for Parsek:** This is different from Parsek's surface-relative rotation approach, which uses `body.bodyTransform.rotation`. Trajectories' approach works for prediction (future positions relative to current body orientation), while Parsek's approach works for playback (absolute surface-relative positions stored at record time).

### Screen Space Transforms

```csharp
// Map view: World -> Scaled -> Screen
Vector3 screen = PlanetariumCamera.Camera.WorldToScreenPoint(
    ScaledSpace.LocalToScaledSpace(worldPos));

// Map view: World -> Scaled -> Viewport [0,1]
Vector3 viewport = PlanetariumCamera.Camera.WorldToViewportPoint(
    ScaledSpace.LocalToScaledSpace(worldPos));

// Flight view: World -> Screen (no scaling needed)
Vector3 screen = FlightCamera.fetch.mainCamera.WorldToScreenPoint(worldPos);

// Flight view: World -> Viewport [0,1]
Vector3 viewport = FlightCamera.fetch.mainCamera.WorldToViewportPoint(worldPos);
```

### Camera References

| Context | Camera | Access |
|---------|--------|--------|
| Map view | PlanetariumCamera | `PlanetariumCamera.Camera` |
| Flight view | FlightCamera | `FlightCamera.fetch.mainCamera` |
| Current camera (any scene) | CameraManager | `CameraManager.GetCurrentCamera()` |

---

## 7. PERFORMANCE PATTERNS

### Per-Frame Mesh Rebuilding Strategy

The mod rebuilds all map view meshes every frame in `OnPreRender`. This is workable because:
1. The mesh pool avoids allocation/deallocation churn
2. Vertex counts are bounded (128 steps per orbit patch, max 4 patches)
3. `mesh.MarkDynamic()` tells Unity to optimize for frequent updates
4. Screen-space ribbon calculation is computationally cheap

### Incremental Trajectory Computation

The trajectory prediction engine uses C# `IEnumerable<bool>` coroutine-style incremental computation:

```csharp
private static IEnumerable<bool> ComputeTrajectoryIncrement()
{
    for (int patchIdx = 0; patchIdx < Settings.MaxPatchCount; ++patchIdx)
    {
        if (Util.ElapsedMilliseconds(increment_time) > MAX_INCREMENT_TIME)
            yield return false;  // Pause until next frame
        // ...compute one patch...
    }
}
```

This spreads computation across multiple frames with a 2ms per-frame budget. The results are double-buffered (`patchesBackBuffer_` -> `Patches`) so rendering always uses complete data.

**For Parsek:** Ghost orbit computation is much simpler (no atmospheric prediction, just orbital elements), but the double-buffering pattern is good practice if orbit line mesh generation becomes expensive with many ghosts.

### Visibility Gating

All overlays check visibility before doing work:

```csharp
// Map overlay
if (!Util.IsMap || !Settings.DisplayTrajectories) { visible = false; return; }

// Flight overlay
if (!Settings.DisplayTrajectories || Util.IsMap
    || !Settings.DisplayTrajectoriesInFlight || Trajectory.Patches.Count == 0)
    return;
```

---

## 8. MATERIAL AND SHADER REFERENCE

### Shaders Used

| Shader | Where Used | Purpose |
|--------|-----------|---------|
| `"KSP/Orbit Line"` | MapOverlay, GfxUtil, GLUtils, DebugLines | Primary orbit/line rendering |
| `"KSP/Particles/Additive"` | GfxUtil, GLUtils, DebugLines | Fallback if Orbit Line unavailable |
| `MapView.fetch.orbitLinesMaterial` | MapOverlay | Stock orbit line material instance |

### Colors Used

| Color | Usage |
|-------|-------|
| `Color.white` | Space orbit line (map view) |
| `Color.red` | Atmospheric trajectory line, impact crosshair (map view) |
| `Color.green` | Target crosshair (map view) |
| `XKCDColors.BlueBlue` | Flight view trajectory line |
| `XKCDColors.FireEngineRed` | Impact cross (flight view), debug lines |
| `XKCDColors.AcidGreen` | Target cross (flight view) |

### UV Coordinate Convention

All mesh vertices use UV `(0.8f, v)` where v alternates between 0 and 1 for the two ribbon edges. The U=0.8 value samples a specific column of the orbit line material texture. Different U values could potentially produce different visual styles.

---

## 9. TARGET PROFILE MANAGEMENT (TargetProfile.cs)

The `TargetProfile` stores target positions in body-local coordinates:

```csharp
internal static Vector3d? WorldPosition
{
    get => LocalPosition.HasValue
        ? Body?.transform?.TransformDirection(LocalPosition.Value) : null;
    set => LocalPosition = value.HasValue
        ? Body?.transform?.InverseTransformDirection(value.Value) : null;
}
```

Positions are stored as `Body.transform.InverseTransformDirection()` results (body-local), and converted back to world space on access. This survives body rotation but not SOI changes.

Target persistence is via a `TrajectoriesVesselSettings` PartModule with `[KSPField(isPersistant = true)]` fields. This is a different approach from Parsek's ScenarioModule pattern.

---

## 10. LIFECYCLE AND SCENE MANAGEMENT

The `Trajectories` ScenarioModule orchestrates all subsystem lifecycles:

```csharp
public override void OnLoad(ConfigNode node)
{
    // Subscribe to GameEvents
    GameEvents.onTimeWarpRateChanged.Add(WarpChanged);
    GameEvents.onVesselLoaded.Add(VesselLoaded);

    // Initialize all subsystems
    DescentProfile.Start();
    Trajectory.Start();
    MapOverlay.Start();
    FlightOverlay.Start();
    NavBallOverlay.Start();
    MainGUI.Start();
    AppLauncherButton.Start();
}

internal void Update()
{
    // Delegate to all subsystems
    Trajectory.Update();
    MapOverlay.Update();
    FlightOverlay.Update();
    NavBallOverlay.Update();
    MainGUI.Update();
}

internal void OnDestroy()
{
    // Clean up in reverse order
    AppLauncherButton.DestroyToolbarButton();
    MainGUI.DeSpawn();
    NavBallOverlay.DestroyTransforms();
    FlightOverlay.Destroy();
    MapOverlay.DestroyRenderer();
    Trajectory.Destroy();
    DescentProfile.Clear();
}
```

**Key observation:** `MapOverlay.Start()` is called from `OnLoad()`, not from a KSPAddon. This means the map trajectory renderer is created at scene load and persists for the duration of the flight scene. It simply shows/hides based on `MapView.MapIsEnabled`.

---

## 11. APPLICABILITY TO PARSEK

### Directly Reusable Patterns

**1. Map View Orbit Line Rendering (HIGH VALUE)**

The ribbon mesh technique from `MapOverlay` is the recommended approach for ghost orbit lines. Key elements to adopt:
- `MapTrajectoryRenderer` MonoBehaviour on `PlanetariumCamera.Camera`
- `OnPreRender` callback for mesh rebuilding
- `MakeRibbonEdge` for constant-width screen-space ribbons
- Layer switching (`layer2D = 31` / `layer3D = 24`) based on `MapView.Draw3DLines`
- Mesh pool with activate/deactivate pattern
- `MapView.fetch.orbitLinesMaterial` for visual consistency with stock lines

However, since Parsek is using the ProtoVessel approach, stock KSP will draw orbit lines for ghost vessels automatically. The ribbon mesh technique becomes useful only if Parsek wants **visually differentiated** ghost orbit lines (different color, dashed, etc.) drawn in addition to or instead of the stock lines.

**2. Coordinate Transform Pipeline (HIGH VALUE)**

The complete chain is demonstrated and Parsek needs all of it:
```
World position
  -> body.position + SwapYZ(orbit.getRelativePositionAtUT(time))
  -> ScaledSpace.LocalToScaledSpace(worldPos)
  -> PlanetariumCamera.Camera.WorldToScreenPoint(scaledPos)
```

**3. Horizon Occlusion Test (MEDIUM VALUE)**

`GLUtils.IsOccluded` is directly reusable for hiding ghost markers when behind a body.

**4. NavBall Marker Cloning (LOW-MEDIUM VALUE)**

If Parsek ever needs NavBall indicators for ghost vessels (e.g., showing relative position for rendezvous), the clone-`progradeVector` technique with custom textures is proven and clean.

**5. Dashed Line Drawing via GL (MEDIUM VALUE)**

The `DrawPath` with `dashed = true` technique (skip every other segment) is the simplest way to make ghost orbit lines visually distinct. Could be used for a quick prototype before implementing the full mesh-based approach.

### What NOT to Copy

**1. The Atmospheric Prediction Engine**

Parsek does not need trajectory prediction. Ghost trajectories are recorded data, not predictions. The entire `Trajectory.cs` prediction engine, aerodynamic models, and RK4 integration are irrelevant.

**2. Static Class Architecture**

The Trajectories mod uses static classes for everything, which makes testing difficult and creates tight coupling. Parsek already has a better architecture with instance-based classes and dependency injection patterns.

**3. Per-Vessel PartModule for Settings**

`TrajectoriesVesselSettings` uses PartModule fields for persistence. Parsek's ScenarioModule approach is better for ghost data that is not tied to a specific vessel's parts.

**4. GL Immediate Mode for Frequent Drawing**

`GLUtils.DrawPath`/`GLTriangle` use GL immediate mode (`GL.Begin`/`GL.End`), which is fine for occasional markers but suboptimal for per-frame orbit line rendering. Prefer the mesh-based approach from `MapOverlay` for performance with many ghost orbits.

### Recommended Integration Approach for Ghost Map Presence

**Phase 1: ProtoVessel + Stock Rendering**
Since Parsek is creating lightweight Vessel objects via ProtoVessel, KSP will automatically render stock orbit lines for ghost vessels. This gives map presence for free.

**Phase 2: Custom Visual Differentiation (if needed)**
If ghost orbit lines need to look different from real vessels:
1. Create a `GhostMapOverlay` MonoBehaviour attached to `PlanetariumCamera.Camera`
2. In `OnPreRender`, iterate ghost recordings with orbital data
3. For each ghost, build a ribbon mesh using the `MakeRibbonEdge` approach
4. Use a tinted version of `MapView.fetch.orbitLinesMaterial` or a custom material
5. Use the dashed technique (alternate segment skipping) or custom UV values for visual distinction
6. Handle layer switching for 2D/3D mode

**Phase 3: Ghost Map Markers (if beyond stock icons)**
If ghost vessels need custom map icons beyond what stock provides:
1. Use GL immediate mode (`GLUtils` pattern) for simple markers
2. Or create small mesh billboards using the crosshair mesh approach
3. Apply occlusion testing via `IsOccluded` pattern

### Key Constants and References Summary

```csharp
// Cameras
PlanetariumCamera.Camera           // Map view camera
FlightCamera.fetch.mainCamera      // Flight view camera
CameraManager.GetCurrentCamera()   // Whatever is active

// Map view layers
int layer2D = 31;                  // Flat overlay mode
int layer3D = 24;                  // Perspective mode
MapView.Draw3DLines                // Which mode is active
MapView.MapIsEnabled               // Whether map view is showing

// Coordinate transforms
ScaledSpace.LocalToScaledSpace(worldPos)    // World -> Scaled (1:6000)
ScaledSpace.ScaledToLocalSpace(scaledPos)   // Scaled -> World

// Materials
MapView.fetch.orbitLinesMaterial   // Stock orbit line material
Shader.Find("KSP/Orbit Line")     // Primary line shader
Shader.Find("KSP/Particles/Additive")  // Fallback shader

// NavBall
navball.progradeVector             // Transform to clone for custom markers
navball.attitudeGymbal             // Rotation from world to navball space
navball.VectorUnitScale            // Unit vector -> navball surface scale
navball.VectorUnitCutoff           // Z cutoff for behind-sphere hiding

// Orbit data
orbit.getRelativePositionAtUT(time)     // Body-relative position (needs SwapYZ)
orbit.getOrbitalVelocityAtUT(time)      // Body-relative velocity (needs SwapYZ)
orbit.TrueAnomalyAtUT(time)             // For adaptive sampling
body.position                           // World-space body center
body.GetSurfaceNVector(lat, lon)        // Surface normal at lat/lon
body.pqsController.GetSurfaceHeight()   // Terrain height query
```
