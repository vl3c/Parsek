// Production resolver defensive tests are exercised under the live KSP
// runtime via the in-game test runner (Pipeline_Anchor_RelativeBoundary,
// Pipeline_Anchor_OrbitalCheckpoint, Pipeline_Anchor_SOI,
// Pipeline_Anchor_Loop in Source/Parsek/InGameTests/RuntimeTests.cs).
//
// xUnit can't drive ProductionAnchorWorldFrameResolver directly because
// the resolver's method bodies reference Vessel.GetWorldPos3D /
// CelestialBody.GetWorldSurfacePosition / Orbit.getPositionAtUT — types
// whose JIT verification eagerly resolves ECall metadata against KSP's
// Assembly-CSharp. Under xUnit (no Unity runtime) the JIT throws
// SecurityException ("ECall methods must be packaged into a system
// module") even on early-out code paths that never actually execute the
// live API call.
//
// The propagator's resolver-dispatch contract is xUnit-tested via
// AnchorWorldFrameResolverTests (interface-level stubs); the production
// implementation's defensive gates are tested under live KSP via the
// in-game tests. This file is intentionally empty as a marker.
