#!/usr/bin/env python3
"""Offline simulation of the re-aim algorithms against REAL recorded data.

Parses the ORBIT_SEGMENT nodes from one or more .prec.txt recording sidecars and
runs faithful Python ports of:
  - ReaimLoiterCompressor.OrbitalPeriod / ComputeCuts
  - GhostPlaybackLogic.CompressSpanUT / DecompressSpanUT / TotalCutLength
  - ReaimClassifier.Classify (transfer-run-ending-at-SOI)
  - ReaimWindowPlanner.Plan (synodic windows + phase anchor)
  - MissionLoopUnitBuilder phase-anchor compression adjustment

so we can confirm (or refute) the in-game numbers WITHOUT a game round trip.

Usage: python reaim_sim.py <member1.prec.txt> [member2.prec.txt ...]
"""
import sys, math, re

# --- KSP stock constants (GM in m^3/s^2; heliocentric sma in m) -------------
MU = {
    "Sun":    1.1723328e18,
    "Kerbin": 3.5316000e12,
    "Mun":    6.5138398e10,
    "Minmus": 1.7658000e9,
    "Duna":   3.0136321e11,
    "Ike":    1.8568369e10,
}
HELIO_SMA = {"Kerbin": 13599840256.0, "Duna": 20726155264.0,
             "Moho": 5263138304.0, "Eve": 9832684544.0,
             "Dres": 40839348203.0, "Jool": 68773560320.0, "Eeloo": 90118820000.0}
PARENT = {"Kerbin": "Sun", "Duna": "Sun", "Mun": "Kerbin", "Minmus": "Kerbin",
          "Ike": "Duna", "Sun": None, "Moho": "Sun", "Eve": "Sun", "Dres": "Sun",
          "Jool": "Sun", "Eeloo": "Sun"}

def orbit_period(sma, mu):
    if sma is None or mu is None or math.isnan(sma) or math.isnan(mu) or sma <= 0.0 or mu <= 0.0:
        return float('nan')
    return 2.0 * math.pi * math.sqrt(sma*sma*sma / mu)

def helio_period(body):
    return orbit_period(HELIO_SMA.get(body, float('nan')), MU["Sun"])

def ancestor_chain(body):
    chain, b = [], body
    while b is not None:
        chain.append(b); b = PARENT.get(b)
    return chain

# --- parse ORBIT_SEGMENT nodes ---------------------------------------------
class Seg: pass

def parse_segments(path, indented=False):
    segs = []
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        lines = f.read().splitlines()
    i = 0
    while i < len(lines):
        # FLAT rec.OrbitSegments: top-level ORBIT_SEGMENT at column 0. indented=True instead reads the
        # TrackSection checkpoint ORBIT_SEGMENTs (nested, leading whitespace) to compare what the
        # rebuild-from-tracksections path would feed the algorithms.
        is_flat = (lines[i] == "ORBIT_SEGMENT")
        is_chk = (lines[i] != "ORBIT_SEGMENT" and lines[i].strip() == "ORBIT_SEGMENT")
        if (indented and is_chk) or (not indented and is_flat):
            # expect '{' next
            j = i + 1
            while j < len(lines) and lines[j].strip() != "{":
                j += 1
            kv = {}
            j += 1
            while j < len(lines) and lines[j].strip() != "}":
                m = re.match(r"\s*([A-Za-z]+)\s*=\s*(.*)$", lines[j])
                if m:
                    kv[m.group(1)] = m.group(2).strip()
                j += 1
            s = Seg()
            s.startUT = float(kv.get("startUT", "nan"))
            s.endUT = float(kv.get("endUT", "nan"))
            s.sma = float(kv.get("sma", "nan"))
            s.ecc = float(kv.get("ecc", "nan"))
            s.body = kv.get("body", "")
            s.predicted = kv.get("isPredicted", "False").lower() == "true"
            segs.append(s)
            i = j + 1
        else:
            i += 1
    return segs

# --- ReaimLoiterCompressor.ComputeCuts -------------------------------------
A_STEP = 0.05
CONTIG_EPS = 1.0
KEEP_REVS = 1

def compute_cuts(segs):
    cuts = []  # (startUT, length)
    i = 0
    n = len(segs)
    while i < n:
        seg = segs[i]
        period = orbit_period(seg.sma, MU.get(seg.body))
        if seg.predicted or not seg.body or math.isnan(period) or period <= 0.0:
            i += 1
            continue
        body = seg.body
        firstA = seg.sma
        run_start = seg.startUT
        run_end = seg.endUT
        prev_end = seg.endUT
        j = i
        while j + 1 < n:
            nxt = segs[j+1]
            if nxt.predicted or nxt.body != body:
                break
            np_ = orbit_period(nxt.sma, MU.get(nxt.body))
            if math.isnan(np_) or np_ <= 0.0:
                break
            if nxt.startUT - prev_end > CONTIG_EPS:
                break
            a_rel = abs(nxt.sma - firstA) / max(1.0, abs(firstA))
            if a_rel > A_STEP:
                break
            j += 1
            run_end = nxt.endUT
            prev_end = nxt.endUT
        t_rep = orbit_period(firstA, MU.get(body))
        dur = run_end - run_start
        if t_rep > 0.0 and not math.isnan(t_rep):
            whole_revs = math.floor(dur / t_rep + 1e-6)
            if whole_revs > KEEP_REVS:
                cut_len = (whole_revs - KEEP_REVS) * t_rep
                cuts.append((run_start, cut_len, i, j, body, firstA, t_rep, dur))
        i = j + 1
    return cuts

def total_cut(cuts):
    return sum(c[1] for c in cuts)

def compress_ut(t, cuts):
    removed = 0.0
    for c in cuts:
        st, ln = c[0], c[1]
        if t <= st:
            continue
        end = st + ln
        oe = t if t < end else end
        removed += oe - st
    return t - removed

def decompress_ut(c, cuts):
    t = c
    for cut in cuts:
        if cut[0] <= t:
            t += cut[1]
    return t

# --- ReaimClassifier.Classify ----------------------------------------------
def classify(orbit_segments):
    segs = [s for s in orbit_segments if (not s.predicted and s.body)]
    if not segs:
        return {"supported": False, "reason": "no usable orbit segments"}
    segs.sort(key=lambda s: s.startUT)
    launch = segs[0].body
    strict_anc = set(ancestor_chain(launch)); strict_anc.discard(launch)
    helio = next((i for i,s in enumerate(segs) if s.body in strict_anc), -1)
    if helio < 0:
        return {"supported": False, "reason": "no heliocentric leg"}
    ancestor = segs[helio].body
    parking = next((i for i in range(helio-1, -1, -1) if segs[i].body == launch), -1)
    arrival = next((i for i in range(helio+1, len(segs))
                    if segs[i].body != ancestor and segs[i].body != launch), -1)
    if arrival < 0:
        return {"supported": False, "reason": "no arrival leg"}
    target = segs[arrival].body
    if PARENT.get(target) != ancestor:
        return {"supported": False, "reason": f"target {target} not direct child of {ancestor}"}
    # multi-hop guard
    for i in range(arrival+1, len(segs)):
        if segs[i].body in strict_anc:
            return {"supported": False, "reason": "more than one heliocentric leg"}
    # transfer run: last coast contiguous with arrival SOI entry, walk back
    arr = segs[arrival]
    SOI_EPS = 1.0
    BURN_CHAIN = 3600.0
    last_coast = -1
    for i in range(len(segs)):
        if segs[i].body == ancestor and abs(segs[i].endUT - arr.startUT) <= SOI_EPS:
            last_coast = i
    if last_coast < 0:
        return {"supported": False, "reason": "no transfer coast contiguous with SOI entry"}
    firstA = segs[last_coast].sma
    tstart = last_coast
    run_start = segs[last_coast].startUT
    for i in range(last_coast-1, -1, -1):
        s = segs[i]
        if s.body != ancestor:
            break
        a_rel = abs(s.sma - firstA) / max(1.0, abs(firstA))
        if a_rel > A_STEP:
            break
        if run_start - s.endUT > BURN_CHAIN:
            break
        tstart = i
        run_start = s.startUT
    transfer = segs[tstart]
    last = segs[last_coast]
    t_rep = orbit_period(firstA, MU.get(ancestor))
    if not math.isnan(t_rep) and t_rep > 0.0 and (last.endUT - transfer.startUT) > 1.5 * t_rep:
        return {"supported": False, "reason": "transfer run spans >1 revolution"}
    if tstart-1 >= 0 and segs[tstart-1].body == ancestor and transfer.startUT - segs[tstart-1].endUT <= BURN_CHAIN:
        return {"supported": False, "reason": "departs from heliocentric parking orbit"}
    return {"supported": True, "launch": launch, "target": target, "ancestor": ancestor,
            "departUT": transfer.startUT, "arrivalUT": arr.startUT,
            "tof": arr.startUT - transfer.startUT, "transferIdx": tstart, "lastCoastIdx": last_coast,
            "arrivalIdx": arrival, "helioIdx": helio, "firstA": firstA, "t_rep_helio": t_rep}

def synodic(p_o, p_t):
    return abs(1.0 / (1.0/p_t - 1.0/p_o))

def plan(p_o, p_t, dep, tof, span_start, span_end, ref):
    syn = synodic(p_o, p_t)
    d0 = dep
    if ref > dep:
        k = math.ceil((ref - dep) / syn)
        d0 = dep + max(k, 0.0) * syn
    if d0 < ref:
        d0 += syn
    cadence = max(syn, span_end - span_start)
    phase = d0 - (dep - span_start)
    return {"d0": d0, "synodic": syn, "tof": tof, "phase": phase, "cadence": cadence}

# --- driver -----------------------------------------------------------------
def main():
    args = sys.argv[1:]
    indented = "--checkpoints" in args
    paths = [a for a in args if not a.startswith("--")]
    if not paths:
        print("usage: reaim_sim.py [--checkpoints] <file.prec.txt> ..."); return
    allsegs = []
    for p in paths:
        ss = parse_segments(p, indented=indented)
        print(f"# {p}: {len(ss)} ORBIT_SEGMENTs ({'checkpoints' if indented else 'flat'})")
        allsegs.extend(ss)
    allsegs.sort(key=lambda s: s.startUT)
    span_start = min(s.startUT for s in allsegs)
    span_end = max(s.endUT for s in allsegs)
    print(f"\n=== {len(allsegs)} gathered segments, span=[{span_start:.1f},{span_end:.1f}] dur={span_end-span_start:.0f} ===")
    print(f"{'idx':>3} {'body':7} {'startUT':>14} {'endUT':>14} {'dur':>12} {'sma':>16} {'ecc':>7} {'period':>12} {'revs':>9} pred")
    for i, s in enumerate(allsegs):
        per = orbit_period(s.sma, MU.get(s.body))
        dur = s.endUT - s.startUT
        revs = (dur/per) if (not math.isnan(per) and per > 0) else float('nan')
        print(f"{i:>3} {s.body:7} {s.startUT:>14.1f} {s.endUT:>14.1f} {dur:>12.0f} {s.sma:>16.3e} {s.ecc:>7.3f} "
              f"{('nan' if math.isnan(per) else f'{per:.0f}'):>12} {('nan' if math.isnan(revs) else f'{revs:.2f}'):>9} {s.predicted}")

    print("\n=== ComputeCuts ===")
    cuts = compute_cuts(allsegs)
    print(f"loiterCuts={len(cuts)} totalCut={total_cut(cuts):.0f} recordedSpan={span_end-span_start:.0f} "
          f"compressedSpan={(span_end-span_start)-total_cut(cuts):.0f}")
    for ci, c in enumerate(cuts):
        st, ln, i0, j0, body, fa, trep, dur = c
        print(f"  cut#{ci} seg#{i0}..{j0} {body} start={st:.1f} len={ln:.0f} end={st+ln:.1f} "
              f"runDur={dur:.0f} T_rep={trep:.0f} revs={dur/trep:.2f}")

    print("\n=== Classify ===")
    plan_c = classify(allsegs)
    for k, v in plan_c.items():
        print(f"  {k} = {v}")

    if plan_c.get("supported"):
        p_o = helio_period(plan_c["launch"]); p_t = helio_period(plan_c["target"])
        print(f"\n=== Plan (helio periods: {plan_c['launch']}={p_o:.0f} {plan_c['target']}={p_t:.0f}) ===")
        sched = plan(p_o, p_t, plan_c["departUT"], plan_c["tof"], span_start, span_end, span_end)
        for k, v in sched.items():
            print(f"  {k} = {v:.3f}" if isinstance(v, float) else f"  {k} = {v}")
        cbd = plan_c["departUT"] - compress_ut(plan_c["departUT"], cuts)
        comp_phase = sched["phase"] + cbd
        print(f"  cutBeforeDeparture = {cbd:.0f}")
        print(f"  compressedPhaseAnchor = {comp_phase:.3f}")
        print(f"  compressedDepartureOffset = {compress_ut(plan_c['departUT'], cuts)-span_start:.0f}")

if __name__ == "__main__":
    main()
