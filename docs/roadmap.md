# Parsek Roadmap

## Recording File Size Optimization

Recording sidecar files (`.prec` trajectory, `_vessel.craft`, `_ghost.craft`) can grow large with many trajectory points, orbit segments, and part events. Investigate and implement size reduction strategies:

- **Shorter key names in trajectory serialization**: Current keys like `startUT`, `longitude`, `argumentOfPeriapsis` are verbose. Consider abbreviated keys (e.g. `sut`, `lon`, `ape`) for the `.prec` format, with a version bump for backward compat.
- **Delta encoding for trajectory points**: Successive points often have similar lat/lon/alt/rotation values. Store first point as absolute, subsequent as deltas — smaller numeric values compress better.
- **Compact numeric encoding**: `ToString("R")` produces full round-trip precision (17 significant digits for double). Many values (altitude, rotation components) don't need full precision. Consider fixed decimal places where appropriate (e.g. 6 decimal places for lat/lon, 4 for quaternion components).
- **Binary format for trajectory data**: ConfigNode text format has significant overhead (key names, whitespace, newlines per value). A binary format with a header + packed floats/doubles would be substantially smaller.
- **Part event compression**: Events with the same `partName` repeated across many events waste space. Consider a part name table (index-based lookup) at the top of the file.
- **Snapshot deduplication**: `_vessel.craft` and `_ghost.craft` often contain near-identical data. Consider storing only the delta or sharing common parts.
- **Optional gzip compression**: Wrap sidecar files in gzip. ConfigNode text compresses very well (typically 5-10x). Would require `System.IO.Compression` and a format version bump.

Priority: medium. Current file sizes are manageable for typical gameplay sessions but could become problematic with many long recordings or heavily staged vessels.
