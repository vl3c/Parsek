// The prototype end-of-frame render-state probe that lived here
// (GhostRenderStateProbe, opt-in via a hardcoded Enabled bool) has been folded
// into MapRenderProbe.cs and gated on the mapRenderTracing setting via
// MapRenderTrace.IsEnabled. Its sampling became the Tier-B change-based proto
// truth and its jump detector became the Tier-C icon-jump anomaly (now
// orbit-derived with the prototype's fixed threshold as a floor), alongside a
// new line-blink anomaly. See Source/Parsek/MapRenderProbe.cs,
// Source/Parsek/MapRenderTrace.cs, and docs/dev/design-map-ts-render-tracer.md.
//
// This file is intentionally left type-free; it is retained only because file
// deletion was unavailable in the authoring environment. It compiles to nothing
// and may be removed.
