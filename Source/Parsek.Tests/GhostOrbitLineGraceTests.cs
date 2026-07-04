// Phase 5a (map/TS render overhaul): this file's tests pinned the FIX-#26 orbit-line grace
// machinery (ShouldDeferOrbitLineHide / OrbitLineGraceFrames / the OffReason* consts / the
// per-pid grace map in GhostMapPresence), which was DELETED with the legacy line-visibility
// decision cascade in GhostOrbitLinePatch. Every test here exercised a deleted symbol, so there
// was nothing to convert. The deletion is locked by GhostOrbitLineCascadeDeleteGateTests.
//
// This tombstone exists only because the working session could not `git rm`; delete this file
// outright when committing.
