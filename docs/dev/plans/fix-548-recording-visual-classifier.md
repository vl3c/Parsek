# Fix #548: Recording Visual Classifier

## Goal

Make structurally valid but visually empty recordings read as intentional in the Recordings window:

- **Static placeholder**: a landed background continuation with a surface position and explicit time range, but no playable trail or orbit payload.
- **Stationary tail**: a terminal leaf whose visible sections are stationary/coasting and whose events do not add visible activity.

This is a presentation fix only. It must not change trajectory data, spawn timing, map presence, Watch eligibility, Re-Fly routing, or recording schema.

## Decisions

- Classify at read/render time from existing recording fields.
- Do not add serialized flags or bump `RecordingFormatVersion`.
- Keep both rows visible and actionable for rename, regroup, archive, delete, Watch, and Re-Fly controls.
- Keep map-presence work out of scope. Surface pins or ProtoVessels for stationary bases are a separate feature.
- Do not trim or synthesize trajectory data for cosmetic reasons.

## Implementation

1. Add `Source/Parsek/RecordingVisualClassifier.cs`.
   - `StaticPlaceholder`: `SurfacePos.HasValue`, fewer than two flat points, no orbit payload, and no animated track-section payload.
   - `StationaryTail`: leaf recording, at least two flat points, no orbit payload, all track sections boring, at least one surface-stationary section, and no non-inert part/segment/flag events.
2. Update `Source/Parsek/UI/RecordingsTableUI.cs`.
   - Classify each row during draw.
   - Replace generic status text with `static` or `stationary`; preserve terminal outcomes with compact suffixes such as `Landed still`.
   - Add distinct tooltip copy explaining why the row is valid and why it remains visible.
3. Add focused xUnit tests in `Source/Parsek.Tests/RecordingVisualClassifierTests.cs`.
   - Static placeholder detection and non-detection for orbit/animated payloads.
   - Stationary tail detection, non-boring-section rejection, event rejection, inert-event allowance, and mid-chain rejection.
4. Update `CHANGELOG.md` and `docs/dev/todo-and-known-bugs.md`.

## Non-Goals

- No optimizer changes.
- No minimal-window trim.
- No hidden-by-default setting.
- No persisted visual payload kind.
- No ghost map-presence behavior change.
