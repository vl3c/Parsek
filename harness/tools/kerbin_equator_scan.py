#!/usr/bin/env python3
"""Survey Kerbin's equator for space-race site candidates (design spike S1).

docs/dev/design-competitive-multiplayer.md (section 7.3) places three copies of
the KSC on Kerbin's equator, a quarter and a half turn away from the stock one.
Whether those places are dry and flat cannot be read from files, so this tool
asks the running game: it samples the stock terrain height through kRPC and
ranks the spots near each target longitude.

It only READS (body.surface_height / body.biome_at). It flies nothing, changes
nothing in the save, and needs KSP to sit in any loaded scene (Space Center is
enough) with the kRPC server up, as the provisioned automation instance has it
(127.0.0.1:50000/50001).

What it writes, under ``<umbrella>/logs/terrain/<YYYY-MM-DD>/`` by default
(never the repo tree):

    coarse.csv           lat, lon, height (0 = ocean), one row per coarse sample
    biomes.csv           lon, biome along the KSC latitude
    ksc_fine.csv         fine grid around the stock KSC (the reference footprint)
    fine_<target>_<n>.csv  fine grid around each shortlisted candidate
    summary.json         everything below, machine readable
    summary.txt          the shortlist per target, human readable

Usage (from harness/, with the mission venv python)::

    missions/.venv/Scripts/python tools/kerbin_equator_scan.py
        [--host 127.0.0.1] [--rpc-port 50000] [--stream-port 50001]
        [--out DIR] [--coarse-step 0.1] [--fine-step 0.025]
        [--search-deg 30] [--shortlist 4] [--selftest]

``--selftest`` runs the pure analysis over a synthetic planet with no game and
no kRPC, and exits non-zero on a wrong answer.
"""

from __future__ import annotations

import argparse
import csv
import datetime
import json
import math
import os
import sys
import time
from typing import Callable, Dict, Iterable, List, Optional, Sequence, Tuple

KERBIN_RADIUS_M = 600000.0
METERS_PER_DEG = KERBIN_RADIUS_M * math.pi / 180.0  # 10471.98 m at the equator

# The stock KSC, from Source/Parsek/SpawnCollisionDetector.cs (pad and runway
# threshold) and their midpoint as the footprint centre.
KSC_PAD = (-0.0972, -74.5575)
KSC_RUNWAY = (-0.0502, -74.7300)
KSC_CENTER = (-0.0737, -74.6438)

# Site targets: a half turn and the two quarter turns from the KSC centre.
TARGETS = (("opposite", 180.0), ("quarter_east", 90.0), ("quarter_west", -90.0))

# Coarse rows: latitudes around the KSC's, so a footprint can be judged in both
# directions. The fine grids are centred on the KSC latitude itself.
COARSE_LAT_HALF_SPAN = 0.5
# Radii (m) over which a candidate's flatness is reported.
REPORT_RADII_M = (1500.0, 3000.0, 5000.0, 7500.0)
FINE_HALF_SPAN_M = 7500.0
# A coarse window must be dry everywhere within this radius to be shortlisted.
SHORTLIST_DRY_RADIUS_M = 3000.0
OCEAN_HEIGHT_M = 0.5  # surface_height reports 0 over ocean


def wrap_lon(lon: float) -> float:
    """Longitude into [-180, 180)."""
    return (lon + 180.0) % 360.0 - 180.0


def lon_delta(a: float, b: float) -> float:
    """Signed shortest difference a - b in degrees."""
    return wrap_lon(a - b)


def frange(start: float, stop: float, step: float) -> List[float]:
    n = int(round((stop - start) / step))
    return [round(start + i * step, 6) for i in range(n + 1)]


def surface_offset_m(lat: float, lon: float, lat0: float, lon0: float) -> Tuple[float, float]:
    """(east, north) metres of (lat, lon) from (lat0, lon0), small-angle."""
    east = lon_delta(lon, lon0) * METERS_PER_DEG * math.cos(math.radians(lat0))
    north = (lat - lat0) * METERS_PER_DEG
    return east, north


def window_stats(samples: Iterable[Tuple[float, float, float]], lat0: float, lon0: float,
                 radius_m: float) -> Optional[Dict[str, float]]:
    """Height statistics of the samples within radius_m of (lat0, lon0)."""
    hs: List[float] = []
    for lat, lon, h in samples:
        east, north = surface_offset_m(lat, lon, lat0, lon0)
        if east * east + north * north <= radius_m * radius_m:
            hs.append(h)
    if not hs:
        return None
    n = len(hs)
    mean = sum(hs) / n
    var = sum((h - mean) ** 2 for h in hs) / n
    ocean = sum(1 for h in hs if h <= OCEAN_HEIGHT_M)
    return {
        "samples": n,
        "min": min(hs),
        "max": max(hs),
        "mean": mean,
        "stddev": math.sqrt(var),
        "range": max(hs) - min(hs),
        "oceanFraction": ocean / float(n),
    }


def shortlist_candidates(coarse: Sequence[Tuple[float, float, float]], center_lat: float,
                         target_lon: float, search_deg: float, ksc_mean: float,
                         keep: int, min_separation_deg: float = 1.0) -> List[Dict[str, float]]:
    """Rank coarse longitudes near target_lon as site centres. Pure.

    A centre qualifies when every coarse sample within SHORTLIST_DRY_RADIUS_M is
    dry. The score (lower is better) is the height range in that window, plus
    how far the mean sits from the KSC's own ground, plus a small cost per
    degree away from the target so that the nearest good spot wins a tie.
    """
    lons = sorted({lon for lat, lon, _ in coarse
                   if abs(lon_delta(lon, target_lon)) <= search_deg})
    near = [s for s in coarse if abs(lon_delta(s[1], target_lon)) <= search_deg + 1.0]
    scored: List[Dict[str, float]] = []
    for lon in lons:
        st = window_stats(near, center_lat, lon, SHORTLIST_DRY_RADIUS_M)
        if st is None or st["oceanFraction"] > 0.0:
            continue
        off = abs(lon_delta(lon, target_lon))
        score = st["range"] + 0.25 * abs(st["mean"] - ksc_mean) + 2.0 * off
        scored.append({"lon": lon, "offsetDeg": off, "score": score,
                       "coarseRange": st["range"], "coarseMean": st["mean"]})
    scored.sort(key=lambda c: c["score"])
    picked: List[Dict[str, float]] = []
    for c in scored:
        if all(abs(lon_delta(c["lon"], p["lon"])) >= min_separation_deg for p in picked):
            picked.append(c)
        if len(picked) >= keep:
            break
    return picked


def max_step_slope_deg(grid: Dict[Tuple[float, float], float], step_deg: float,
                       lat0: float, lon0: float, radius_m: float) -> float:
    """Steepest slope (degrees) between neighbouring fine samples within radius_m."""
    worst = 0.0
    for (lat, lon), h in grid.items():
        east, north = surface_offset_m(lat, lon, lat0, lon0)
        if east * east + north * north > radius_m * radius_m:
            continue
        for dlat, dlon in ((step_deg, 0.0), (0.0, step_deg)):
            other = grid.get((round(lat + dlat, 6), round(wrap_lon(lon + dlon), 6)))
            if other is None:
                continue
            run = step_deg * METERS_PER_DEG
            worst = max(worst, math.degrees(math.atan2(abs(other - h), run)))
    return worst


def describe_fine(grid: Dict[Tuple[float, float], float], step_deg: float,
                  lat0: float, lon0: float) -> Dict[str, object]:
    samples = [(lat, lon, h) for (lat, lon), h in grid.items()]
    out: Dict[str, object] = {"lat": lat0, "lon": lon0, "radii": {}}
    for r in REPORT_RADII_M:
        st = window_stats(samples, lat0, lon0, r)
        if st is not None:
            st["maxSlopeDeg"] = max_step_slope_deg(grid, step_deg, lat0, lon0, r)
        out["radii"][str(int(r))] = st
    return out


# --- sampling (the only part that needs the game) ---------------------------

def sample_grid(height_fn: Callable[[float, float], float], lats: Sequence[float],
                lons: Sequence[float], label: str,
                progress: Callable[[str], None]) -> Dict[Tuple[float, float], float]:
    grid: Dict[Tuple[float, float], float] = {}
    total = len(lats) * len(lons)
    done = 0
    start = time.monotonic()
    last = start
    for lat in lats:
        for lon in lons:
            grid[(round(lat, 6), round(wrap_lon(lon), 6))] = float(height_fn(lat, wrap_lon(lon)))
            done += 1
            now = time.monotonic()
            if now - last >= 15.0:
                rate = done / max(now - start, 1e-6)
                progress("%s: %d/%d samples, %.0f/s, about %.0f s left"
                         % (label, done, total, rate, (total - done) / max(rate, 1e-6)))
                last = now
    progress("%s: %d samples in %.1f s" % (label, total, time.monotonic() - start))
    return grid


def fine_axes(lat0: float, lon0: float, step_deg: float) -> Tuple[List[float], List[float]]:
    half = FINE_HALF_SPAN_M / METERS_PER_DEG
    n = int(math.ceil(half / step_deg))
    lats = [round(lat0 + i * step_deg, 6) for i in range(-n, n + 1)]
    lons = [round(wrap_lon(lon0 + i * step_deg), 6) for i in range(-n, n + 1)]
    return lats, lons


def write_grid_csv(path: str, grid: Dict[Tuple[float, float], float]) -> None:
    with open(path, "w", newline="", encoding="ascii") as f:
        w = csv.writer(f)
        w.writerow(["lat", "lon", "height_m"])
        for (lat, lon) in sorted(grid):
            w.writerow(["%.6f" % lat, "%.6f" % lon, "%.2f" % grid[(lat, lon)]])


def run_survey(height_fn: Callable[[float, float], float],
               biome_fn: Optional[Callable[[float, float], str]],
               out_dir: Optional[str], coarse_step: float, fine_step: float,
               search_deg: float, keep: int,
               progress: Callable[[str], None]) -> Dict[str, object]:
    """The whole survey over a height function. Game-free apart from height_fn."""
    center_lat, center_lon = KSC_CENTER
    summary: Dict[str, object] = {
        "kscCenter": {"lat": center_lat, "lon": center_lon},
        "coarseStepDeg": coarse_step, "fineStepDeg": fine_step,
        "searchDeg": search_deg, "targets": {},
    }

    # 1. The reference: what the stock KSC footprint looks like.
    lats, lons = fine_axes(center_lat, center_lon, fine_step)
    ksc_grid = sample_grid(height_fn, lats, lons, "KSC reference", progress)
    ksc = describe_fine(ksc_grid, fine_step, center_lat, center_lon)
    ksc["padHeight"] = float(height_fn(*KSC_PAD))
    ksc["runwayHeight"] = float(height_fn(*KSC_RUNWAY))
    summary["ksc"] = ksc
    ksc_mean = ksc["radii"]["1500"]["mean"]  # type: ignore[index]

    # 2. Coarse rows around the KSC latitude, all the way round.
    coarse_lats = frange(center_lat - COARSE_LAT_HALF_SPAN, center_lat + COARSE_LAT_HALF_SPAN,
                         coarse_step)
    coarse_lons = [round(wrap_lon(-180.0 + i * coarse_step), 6)
                   for i in range(int(round(360.0 / coarse_step)))]
    coarse_grid = sample_grid(height_fn, coarse_lats, coarse_lons, "coarse scan", progress)
    coarse = [(lat, lon, h) for (lat, lon), h in coarse_grid.items()]

    biomes: List[Tuple[float, str]] = []
    if biome_fn is not None:
        for lon in coarse_lons:
            biomes.append((lon, str(biome_fn(center_lat, lon))))
        progress("biomes: %d samples" % len(biomes))

    # How much of the KSC latitude row is dry at all.
    row = [h for (lat, lon), h in coarse_grid.items()
           if abs(lat - min(coarse_lats, key=lambda v: abs(v - center_lat))) < 1e-6]
    summary["equatorDryFraction"] = (sum(1 for h in row if h > OCEAN_HEIGHT_M) / float(len(row))
                                     if row else None)

    # 3. Shortlist near each target and look closely.
    fine_grids: Dict[str, Dict[Tuple[float, float], float]] = {}
    for name, turn in TARGETS:
        target_lon = wrap_lon(center_lon + turn)
        picks = shortlist_candidates(coarse, center_lat, target_lon, search_deg, ksc_mean, keep)
        entries = []
        for i, c in enumerate(picks):
            lats, lons = fine_axes(center_lat, c["lon"], fine_step)
            grid = sample_grid(height_fn, lats, lons, "%s #%d" % (name, i + 1), progress)
            fine_grids["%s_%d" % (name, i + 1)] = grid
            d = describe_fine(grid, fine_step, center_lat, c["lon"])
            d.update(c)
            if biome_fn is not None:
                d["biome"] = str(biome_fn(center_lat, c["lon"]))
            entries.append(d)
        summary["targets"][name] = {  # type: ignore[index]
            "targetLon": target_lon,
            "targetHeight": float(height_fn(center_lat, target_lon)),
            "targetBiome": str(biome_fn(center_lat, target_lon)) if biome_fn else None,
            "candidates": entries,
        }

    if out_dir:
        os.makedirs(out_dir, exist_ok=True)
        write_grid_csv(os.path.join(out_dir, "coarse.csv"), coarse_grid)
        write_grid_csv(os.path.join(out_dir, "ksc_fine.csv"), ksc_grid)
        for key, grid in fine_grids.items():
            write_grid_csv(os.path.join(out_dir, "fine_%s.csv" % key), grid)
        if biomes:
            with open(os.path.join(out_dir, "biomes.csv"), "w", newline="", encoding="ascii") as f:
                w = csv.writer(f)
                w.writerow(["lon", "biome"])
                for lon, b in biomes:
                    w.writerow(["%.6f" % lon, b])
        with open(os.path.join(out_dir, "summary.json"), "w", encoding="ascii") as f:
            json.dump(summary, f, indent=2, sort_keys=True)
        with open(os.path.join(out_dir, "summary.txt"), "w", encoding="ascii") as f:
            f.write(format_summary(summary))
    return summary


def format_summary(summary: Dict[str, object]) -> str:
    lines: List[str] = []
    ksc = summary["ksc"]  # type: ignore[assignment]
    lines.append("Kerbin equator survey (heights in metres above sea level, 0 = ocean)")
    lines.append("")
    lines.append("Stock KSC reference at lat %.4f lon %.4f: pad %.1f, runway %.1f"
                 % (ksc["lat"], ksc["lon"], ksc["padHeight"], ksc["runwayHeight"]))  # type: ignore[index]
    lines.extend(_radius_lines(ksc["radii"]))  # type: ignore[index]
    dry = summary.get("equatorDryFraction")
    if dry is not None:
        lines.append("")
        lines.append("Dry fraction of the KSC latitude all the way round: %.0f%%" % (100.0 * dry))
    for name, t in summary["targets"].items():  # type: ignore[union-attr]
        lines.append("")
        lines.append("Target %s: lon %.3f (height there %.1f, biome %s)"
                     % (name, t["targetLon"], t["targetHeight"], t.get("targetBiome")))
        if not t["candidates"]:
            lines.append("  no dry spot within the search range")
        for i, c in enumerate(t["candidates"]):
            lines.append("  #%d lon %.3f (%.2f deg from target, biome %s, score %.1f)"
                         % (i + 1, c["lon"], c["offsetDeg"], c.get("biome"), c["score"]))
            lines.extend("  " + ln for ln in _radius_lines(c["radii"]))
    lines.append("")
    return "\n".join(lines)


def _radius_lines(radii: Dict[str, Optional[Dict[str, float]]]) -> List[str]:
    out = []
    for r in sorted(radii, key=lambda k: int(k)):
        st = radii[r]
        if st is None:
            continue
        out.append("  within %5s m: min %7.1f max %7.1f mean %7.1f range %6.1f "
                   "ocean %3.0f%% steepest %4.1f deg (%d samples)"
                   % (r, st["min"], st["max"], st["mean"], st["range"],
                      100.0 * st["oceanFraction"], st.get("maxSlopeDeg", 0.0), st["samples"]))
    return out


# --- self test ---------------------------------------------------------------

def _selftest() -> int:
    """A synthetic Kerbin: ocean everywhere except a flat plateau near the
    opposite target and a rough ridge nearer to it. The plateau must win."""
    center_lat, center_lon = KSC_CENTER
    opposite = wrap_lon(center_lon + 180.0)
    plateau = wrap_lon(opposite + 6.0)
    ridge = wrap_lon(opposite + 2.0)

    def height(lat: float, lon: float) -> float:
        if abs(lon_delta(lon, center_lon)) < 2.0:
            return 65.0
        if abs(lon_delta(lon, plateau)) < 1.5:
            return 70.0
        if abs(lon_delta(lon, ridge)) < 1.0:
            return 400.0 + 300.0 * math.sin(math.radians(lon * 400.0)) + 2000.0 * abs(lat - center_lat)
        return 0.0

    summary = run_survey(height, None, None, coarse_step=0.25, fine_step=0.1,
                         search_deg=20.0, keep=2, progress=lambda s: None)
    cands = summary["targets"]["opposite"]["candidates"]  # type: ignore[index]
    problems: List[str] = []
    if not cands:
        problems.append("no candidate found for the opposite target")
    else:
        best = cands[0]
        if abs(lon_delta(best["lon"], plateau)) > 1.5:
            problems.append("best candidate at lon %.2f, expected the plateau near %.2f"
                            % (best["lon"], plateau))
        if best["radii"]["3000"]["range"] > 1.0:
            problems.append("plateau reported as not flat")
    if summary["targets"]["quarter_east"]["candidates"]:  # type: ignore[index]
        problems.append("a candidate was found in open ocean")
    if abs(wrap_lon(190.0) - (-170.0)) > 1e-9 or abs(lon_delta(-179.0, 179.0) - 2.0) > 1e-9:
        problems.append("longitude wrap is wrong")
    for p in problems:
        print("SELFTEST FAIL: " + p)
    print("selftest %s" % ("FAILED" if problems else "ok"))
    return 1 if problems else 0


# --- shell -------------------------------------------------------------------

def _default_out_dir() -> str:
    here = os.path.dirname(os.path.abspath(__file__))
    umbrella = os.path.dirname(os.path.dirname(os.path.dirname(here)))
    return os.path.join(umbrella, "logs", "terrain", datetime.date.today().isoformat())


def main(argv: Optional[Sequence[str]] = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--rpc-port", type=int, default=50000)
    ap.add_argument("--stream-port", type=int, default=50001)
    ap.add_argument("--out", default=None, help="output directory (default: <umbrella>/logs/terrain/<date>)")
    ap.add_argument("--coarse-step", type=float, default=0.1)
    ap.add_argument("--fine-step", type=float, default=0.025)
    ap.add_argument("--search-deg", type=float, default=30.0,
                    help="how far from each target longitude to look")
    ap.add_argument("--shortlist", type=int, default=4, help="candidates looked at closely per target")
    ap.add_argument("--selftest", action="store_true")
    args = ap.parse_args(argv)

    if args.selftest:
        return _selftest()

    import krpc  # lazy: the self test and the import must work without the venv

    out_dir = args.out or _default_out_dir()
    print("connecting to kRPC at %s:%d" % (args.host, args.rpc_port))
    conn = krpc.connect(name="kerbin-equator-scan", address=args.host,
                        rpc_port=args.rpc_port, stream_port=args.stream_port)
    try:
        kerbin = conn.space_center.bodies["Kerbin"]
        probe = kerbin.surface_height(KSC_PAD[0], KSC_PAD[1])
        print("connected; pad height reads %.1f m" % probe)
        summary = run_survey(kerbin.surface_height, kerbin.biome_at, out_dir,
                             args.coarse_step, args.fine_step, args.search_deg,
                             args.shortlist, progress=lambda s: print(s, flush=True))
    finally:
        conn.close()
    print(format_summary(summary))
    print("written to %s" % out_dir)
    return 0


if __name__ == "__main__":
    sys.exit(main())
