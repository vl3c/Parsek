# Parsek Spike Prototype Plan

## Objective

Validate the core concept: **Record vessel trajectory → Play back as ghost vessel**

This spike proves feasibility before investing in full architecture.

---

## What We're Testing

1. **Recording** - Can we capture vessel position/rotation reliably?
2. **Playback** - Can we position a ghost object to follow the recorded path?
3. **Interpolation** - Does time-based interpolation work smoothly?
4. **Coordinate Systems** - Do geographic coords (lat/lon/alt) work across time?

---

## Key Insights from Reference Mods

### From PersistentTrails (Track.cs)

```
Store: lat, lon, alt, orientation, velocity, recordTime
NOT Unity world coordinates (they drift!)
```

- Use `vessel.latitude`, `vessel.longitude`, `vessel.altitude`
- Convert back via `body.GetWorldSurfacePosition(lat, lon, alt)`
- Linear interpolation (`Vector3.Lerp`, `Quaternion.Lerp`) is sufficient
- Protect against NaN in quaternions

### From PersistentTrails (OffRailsObject.cs)

- **Reference frame correction needed**: When celestial body moves, adjust ghost position
- Track `mainBody.position` delta each frame and subtract from ghost position

---

## Spike Scope

### In Scope
- [x] Record vessel lat/lon/alt/rotation every 0.5s
- [x] Store in memory (no persistence)
- [x] Spawn simple sphere ghost on playback
- [x] Interpolate position based on universal time
- [x] Basic keyboard controls (F9 record, F10 playback)
- [x] Reference frame correction for celestial body movement

### Out of Scope (deferred to full implementation)
- Persistence to save files
- Staging events
- Real vessel model (using sphere)
- GUI panels
- Multiple recordings
- SOI transitions

---

## Implementation

### File Structure
```
Source/
└── Parsek/
    └── ParsekSpike.cs    (single file, ~200 lines)
```

### Data Structure
```csharp
struct TrajectoryPoint
{
    public double ut;           // Universal time
    public double lat, lon, alt; // Geographic coords
    public Quaternion rotation;
    public string bodyName;
}
```

### Recording Logic
```csharp
void SamplePosition()
{
    var v = FlightGlobals.ActiveVessel;
    recording.Add(new TrajectoryPoint
    {
        ut = Planetarium.GetUniversalTime(),
        lat = v.latitude,
        lon = v.longitude,
        alt = v.altitude,
        rotation = v.transform.rotation,
        bodyName = v.mainBody.name
    });
}
```

### Playback Logic
```csharp
void UpdateGhost()
{
    double currentUT = Planetarium.GetUniversalTime();

    // Find surrounding waypoints and interpolate
    var (pos, rot) = InterpolateAtTime(currentUT);

    ghost.transform.position = pos;
    ghost.transform.rotation = rot;
}
```

### Reference Frame Correction
```csharp
// Track celestial body movement
Vector3 bodyDelta = mainBody.position - lastBodyPosition;
lastBodyPosition = mainBody.position;

// Correct ghost position
ghost.transform.position -= bodyDelta;
```

---

## Test Plan

1. **Launch any vessel** (simple rocket works)
2. **Press F9** to start recording
3. **Fly around for 30-60 seconds** (pitch, roll, climb)
4. **Press F9** again to stop recording
5. **Press F10** to start playback
6. **Observe**: Green sphere should follow your flight path
7. **Verify**: Position matches approximately, no jitter

### Success Criteria
- Ghost follows recorded path within ~10m accuracy
- No visible jitter or jumping
- Works during normal flight (not just on launchpad)
- Handles time warp gracefully (ghost catches up)

---

## Risk Mitigation

| Risk | Mitigation |
|------|------------|
| Coordinate drift | Use geographic coords, not Unity world coords |
| Reference frame shift | Track and correct for celestial body movement |
| Quaternion NaN | Sanitize quaternion values before use |
| Performance | Sample every 0.5s, not every frame |

---

## Next Steps After Spike

If spike succeeds:
1. Set up proper project structure with .csproj
2. Create formal `TrajectoryFrame` and `MissionRecording` classes
3. Add staging event detection
4. Implement persistence via `ScenarioModule`
5. Create basic GUI panel

If spike fails:
1. Identify specific failure point
2. Study FMRS/PersistentTrails more deeply
3. Adjust approach based on findings
