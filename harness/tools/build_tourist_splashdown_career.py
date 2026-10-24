#!/usr/bin/env python3
"""Build the committed `tourist-splashdown-career` fixture BY CONSTRUCTION (the TOUR-1 host).

WHY THIS FIXTURE EXISTS. GC12-TOURISTS-REBORN-AS-HIRED-CREW (fixed 2026-10-24, unit tests
only): a spawn whose snapshot names a kerbal the roster no longer holds re-created him with
`GetNewKerbal(KerbalType.Crew)`, so a tourist stock had removed came back as a free hired
crewmember; and the trait lookup read only `roster.Crew`, so a tourist's KerbalAssignment
row said "Pilot". A live proof needs a tourist ABOARD a recorded vessel that ends away from
the KSC (a flight ending within 50 m of the pad or the runway threshold is retired, never
spawned), with an Active tourism contract stock can end so that stock itself removes him
(`TourismContract`'s clear on cancel). No committed fixture carries a tourist or a
TourismContract instance (the corpus has the contract type only as a WEIGHTS entry), and no
seam verb seats a kerbal in a vessel outside the editor, so the tourist is authored here.

THE SOURCE IS `kerbin-splashdown-recorded`: B4's committed Kerbal X flight left the mk1-3pod
capsule (three seats, Jebediah Kerman in seat 0) LANDED about 1,690 km east of the pad
(lat -0.12, lon 86.70), the host EVA-6 / EVA-7 rewind and spawn on. It is SANDBOX, and a
contract needs CAREER, so the first step is `build_route_career.py`'s conversion recipe
verbatim (the H73 rule: only the game mode moves; that module's docstring is the authority
for every edit it makes), run here because that builder's SOURCES tuple is its own five
route hosts. Its KSC-debris edit removes this save's THREE launch clamps landed at the
LaunchPad (stock would recover them at the first career load and pay their value with no
ledger row); `activeVessel` (the capsule, index 1) is unaffected.

THE TOURIST EDITS (all three surfaces copied from a stock-written career, never invented):
  1. ROSTER gains `Masel Kerman`, the KERBAL node stock wrote for the first offered
     TourismContract of `Source/Parsek.Tests/Fixtures/C1Career/persistent.sfs`, byte for
     byte except `type = Unowned` -> `type = Tourist` (what stock's accept does to an
     offered contract's tourists; the GC-12 fix creates a missing tourist the same way) and
     `idx = -1` -> `idx = 1` (her seat). `state = Assigned` and `trait = Tourist` are
     stock's own.
  2. The capsule's mk1-3pod PART gains `crew = Masel Kerman` after Jebediah's line (seat 1;
     the pod has three).
  3. ContractSystem's CONTRACTS gains that same C1Career contract (guid
     36cb3f0c-a786-41f3-ac4b-8d223b3a3683, `Kerbal Motion LLC`, a Minmus flyby tour),
     ACTIVE: `state = Active`; the second tourist (`Macgan Kerman`) and his
     KerbalTourParameter dropped, so the contract names only the seated tourist;
     `values` re-based on this save's clock in the shape the committed
     `stock-screen-census` Active contract carries (dateAccepted = ACCEPT_UT, dateDeadline
     = ACCEPT_UT + TimeDeadline) with the two FAILURE penalties zeroed (values[4] funds,
     values[7] reputation; the parameters' own failure values are already 0), so the
     lane's cancel moves no pool on a career whose reputation is 0.
  4. The ledger gains the matching `type = 5` ContractAccept row (KSC, no recordingId,
     `seq = 1`: the only KSC row in the file), field for field the
     `build_career_contract_pad.py` shape. LOAD-BEARING: `KspStatePatcher.PatchContracts`
     removes an Active stock contract whose guid the ledger does not hold as active, so
     without this row Parsek would drop the contract at the first recalc. The row sits at
     UT 1270, below the save clock 1276.62 (`Ledger.Reconcile` prunes a contract row past
     the clock on cold load), with `advanceFunds = 0` (FundsModule credits an advance
     unconditionally) and both penalties 0, matching the stock values above.
  5. `persistent.loadmeta` `ongoingContracts = 1` (the conversion copies fresh-career's 0).

WHAT IS NOT TOUCHED: every recording sidecar, baseline, milestone and event file, the
ParsekScenario node (the committed Kerbal X tree), ProgressTracking, and every vessel but
the capsule's one crew line. The Kerbal X recordings carry Jebediah only; the tourist is in
the live save alone, so she rides only the flight the lane records.

GUESSED (the first flight confirms; pre-registered in the TOUR-1 header): that stock keeps
the authored Active TourismContract through the load (its MeetRequirements and parameter
loads), and that stock's cancel removes an off-vessel tourist from the roster.

`--check` re-derives every file and compares it with the committed bytes;
`harness/lib/test_tourist_splashdown_career.py` runs it in-process.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import os
import shutil
import sys
from typing import Dict, List, Tuple

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import build_career_pad_craft as _recipe  # noqa: E402
import build_route_career as _career  # noqa: E402

HARNESS = os.path.dirname(_HERE)
REPO = os.path.dirname(HARNESS)
SAVES = os.path.join(HARNESS, "fixtures", "saves")

SOURCE = "kerbin-splashdown-recorded"
TARGET = "tourist-splashdown-career"
OUT_DIR = os.path.join(SAVES, TARGET)
C1_SFS = os.path.join(REPO, "Source", "Parsek.Tests", "Fixtures", "C1Career", "persistent.sfs")

TITLE = "%s (CAREER)" % TARGET
DESCRIPTION = ("Parsek autotest fixture (file-constructed: %s converted to CAREER with a "
               "seated tourist and her Active tourism contract by "
               "harness/tools/build_tourist_splashdown_career.py)." % SOURCE)
EXPECTED_KSC_DEBRIS = 3

TOURIST = "Masel Kerman"
DROPPED_TOURIST = "Macgan Kerman"
TOURIST_SEAT_IDX = "1"
PILOT = "Jebediah Kerman"
CAPSULE_NAME = "#autoLOC_501232"
CAPSULE_PART = "mk1-3pod"

CONTRACT_GUID = "36cb3f0c-a786-41f3-ac4b-8d223b3a3683"
CONTRACT_TYPE = "TourismContract"
# Cosmetic: nothing matches on a title.
CONTRACT_TITLE = "Tourism contract (fixture accept row)"
ACCEPT_UT = 1270
ACCEPT_ACTION_ID = "act_7c3e9a1f04b24d6e8f15a2b7c9d0e613"
SAVE_CLOCK_UT = 1276.6235614006234

SFS = "persistent.sfs"
LOADMETA = "persistent.loadmeta"
LEDGER = _career.LEDGER


def _read(path: str) -> Tuple[List[str], str]:
    return _career.read_text(path)


# ---------------------------------------------------------------------------
# Step 0: the career conversion (build_route_career.build_sfs, with this save's debris count)
# ---------------------------------------------------------------------------


def convert_to_career(src: List[str], base: List[str]) -> List[str]:
    out = list(src)
    if _career._top_value(out, "Mode") != _career.EXPECT_SOURCE_MODE:
        raise SystemExit("%s: GAME Mode is not SANDBOX" % SOURCE)
    _recipe.set_top_value(out, "Mode", _career.EXPECT_MODE)
    if not _recipe.set_top_value(out, "Title", TITLE):
        raise SystemExit("%s: no Title line" % SOURCE)
    if not _recipe.set_top_value(out, "Description", DESCRIPTION):
        raise SystemExit("%s: no Description line" % SOURCE)

    src_params = _career._top_node(out, "PARAMETERS")
    base_params = _career._top_node(base, "PARAMETERS")
    if src_params is None or base_params is None:
        raise SystemExit("a PARAMETERS node is missing")
    params = base[base_params[0]:base_params[1]]
    settings = _recipe.child_nodes(out, src_params, "ParsekSettings")
    if len(settings) > 1:
        raise SystemExit("%s: more than one ParsekSettings node" % SOURCE)
    if settings:
        params[-1:-1] = out[settings[0][0]:settings[0][1]]
    out[src_params[0]:src_params[1]] = params

    for name, _anchor, _where in _career.CAREER_SCENARIOS:
        if _recipe._scenario_node(out, name) is not None:
            raise SystemExit("%s already carries a %s SCENARIO" % (SOURCE, name))
    for name, anchor, where in _career.CAREER_SCENARIOS:
        node = _recipe._scenario_node(base, name)
        if node is None:
            raise SystemExit("fresh-career has no %s SCENARIO" % name)
        block = base[node[0]:node[1]]
        if name == "ResearchAndDevelopment":
            block = _career._grant_tech(block)
        elif name == "ScenarioUpgradeableFacilities":
            block = _career._top_facilities(block)
        _career._insert_scenario(out, block, anchor, where)

    removed = _career.strip_ksc_debris(out)
    if removed != EXPECTED_KSC_DEBRIS:
        raise SystemExit("removed %d KSC debris vessel(s), expected %d"
                         % (removed, EXPECTED_KSC_DEBRIS))
    return out


# ---------------------------------------------------------------------------
# Steps 1-3: the tourist, her seat, her contract
# ---------------------------------------------------------------------------


def _c1_roster_kerbal(c1: List[str], name: str) -> List[str]:
    roster = _career._top_node(c1, "ROSTER")
    if roster is None:
        raise SystemExit("C1Career has no ROSTER")
    hits = [k for k in _recipe.child_nodes(c1, roster, "KERBAL")
            if _recipe.get_value(c1, k, "name") == name]
    if len(hits) != 1:
        raise SystemExit("C1Career ROSTER has %d KERBAL named %s" % (len(hits), name))
    return c1[hits[0][0]:hits[0][1]]


def _replace_exact(block: List[str], old: str, new: str) -> None:
    idx = [i for i, line in enumerate(block) if line == old]
    if len(idx) != 1:
        raise SystemExit("expected exactly one %r line, found %d" % (old, len(idx)))
    block[idx[0]] = new


def tourist_kerbal(c1: List[str]) -> List[str]:
    block = list(_c1_roster_kerbal(c1, TOURIST))
    _replace_exact(block, "\t\t\ttype = Unowned", "\t\t\ttype = Tourist")
    _replace_exact(block, "\t\t\tidx = -1", "\t\t\tidx = " + TOURIST_SEAT_IDX)
    for want in ("\t\t\ttrait = Tourist", "\t\t\tstate = Assigned", "\t\t\ttour = False"):
        if want not in block:
            raise SystemExit("C1Career %s lacks %r" % (TOURIST, want))
    return block


def _c1_contract(c1: List[str]) -> List[str]:
    scn = _recipe._scenario_node(c1, "ContractSystem")
    contracts = _recipe.find_node(c1, "CONTRACTS", scn[0], scn[1]) if scn else None
    if contracts is None:
        raise SystemExit("C1Career has no ContractSystem CONTRACTS")
    hits = [c for c in _recipe.child_nodes(c1, contracts, "CONTRACT")
            if _recipe.get_value(c1, c, "guid") == CONTRACT_GUID]
    if len(hits) != 1:
        raise SystemExit("C1Career has %d CONTRACT %s" % (len(hits), CONTRACT_GUID))
    return c1[hits[0][0]:hits[0][1]]


def _fmt_ut(v: float) -> str:
    s = repr(float(v))
    return s[:-2] if s.endswith(".0") else s


def active_contract(c1: List[str]) -> List[str]:
    block = list(_c1_contract(c1))
    node = (0, len(block))
    if _recipe.get_value(block, node, "type") != CONTRACT_TYPE:
        raise SystemExit("C1Career contract %s is not a %s" % (CONTRACT_GUID, CONTRACT_TYPE))
    if _recipe.get_value(block, node, "state") != "Offered":
        raise SystemExit("C1Career contract %s is not Offered" % CONTRACT_GUID)
    _recipe.set_value(block, node, "state", "Active")

    vals = _recipe.get_value(block, node, "values").split(",")
    if len(vals) != 12:
        raise SystemExit("contract values has %d fields, expected 12" % len(vals))
    # values = TimeExpiry, TimeDeadline, FundsAdvance, FundsCompletion, FundsFailure,
    #          ScienceCompletion, ReputationCompletion, ReputationFailure,
    #          dateExpire, dateAccepted, dateDeadline, dateFinished
    if vals[2] != "0":
        raise SystemExit("C1Career contract carries an advance (%s)" % vals[2])
    vals[4] = "0"
    vals[7] = "0"
    vals[9] = _fmt_ut(ACCEPT_UT)
    vals[10] = _fmt_ut(ACCEPT_UT + float(vals[1]))
    _recipe.set_value(block, node, "values", ",".join(vals))

    if _recipe.get_value(block, node, "tourists") != "%s|%s" % (TOURIST, DROPPED_TOURIST):
        raise SystemExit("C1Career contract tourists line changed")
    _recipe.set_value(block, node, "tourists", TOURIST)

    params = _recipe.child_nodes(block, node, "PARAM")
    dropped = [p for p in params if _recipe.get_value(block, p, "kerbalName") == DROPPED_TOURIST]
    kept = [p for p in params if _recipe.get_value(block, p, "kerbalName") == TOURIST]
    if len(dropped) != 1 or len(kept) != 1 or len(params) != 2:
        raise SystemExit("C1Career contract PARAM layout changed")
    del block[dropped[0][0]:dropped[0][1]]
    return block


def deadline_abs_ut(c1: List[str]) -> str:
    vals = _recipe.get_value(_c1_contract(c1), (0, len(_c1_contract(c1))), "values").split(",")
    return _fmt_ut(ACCEPT_UT + float(vals[1]))


def _capsule_pod(lines: List[str]) -> Tuple[int, int]:
    flight = _career._top_node(lines, "FLIGHTSTATE")
    caps = [v for v in _recipe.child_nodes(lines, flight, "VESSEL")
            if _recipe.get_value(lines, v, "name") == CAPSULE_NAME
            and _recipe.get_value(lines, v, "sit") == "LANDED"]
    if len(caps) != 1:
        raise SystemExit("expected one LANDED %s vessel, found %d" % (CAPSULE_NAME, len(caps)))
    pods = [p for p in _recipe.child_nodes(lines, caps[0], "PART")
            if _recipe.get_value(lines, p, "name") == CAPSULE_PART]
    if len(pods) != 1:
        raise SystemExit("expected one %s PART in the capsule" % CAPSULE_PART)
    return pods[0]


def seat_tourist(lines: List[str]) -> None:
    pod = _capsule_pod(lines)
    crew = [i for i in range(pod[0], pod[1]) if lines[i] == "\t\t\t\tcrew = " + PILOT]
    if len(crew) != 1 or any(lines[i].startswith("\t\t\t\tcrew = ")
                             for i in range(pod[0], pod[1]) if i != crew[0]):
        raise SystemExit("the capsule pod does not hold exactly Jebediah")
    lines.insert(crew[0] + 1, "\t\t\t\tcrew = " + TOURIST)


def add_roster_kerbal(lines: List[str], block: List[str]) -> None:
    roster = _career._top_node(lines, "ROSTER")
    if roster is None:
        raise SystemExit("no ROSTER")
    if any(_recipe.get_value(lines, k, "name") == TOURIST
           for k in _recipe.child_nodes(lines, roster, "KERBAL")):
        raise SystemExit("%s already in the ROSTER" % TOURIST)
    lines[roster[1] - 1:roster[1] - 1] = block


def add_contract(lines: List[str], block: List[str]) -> None:
    scn = _recipe._scenario_node(lines, "ContractSystem")
    contracts = _recipe.find_node(lines, "CONTRACTS", scn[0], scn[1])
    if contracts is None:
        raise SystemExit("no ContractSystem CONTRACTS")
    if _recipe.child_nodes(lines, contracts, "CONTRACT"):
        raise SystemExit("the career ContractSystem already carries a contract")
    lines[contracts[1] - 1:contracts[1] - 1] = block


def build_sfs(src: List[str], base: List[str], c1: List[str]) -> List[str]:
    out = convert_to_career(src, base)
    seat_tourist(out)
    add_roster_kerbal(out, tourist_kerbal(c1))
    add_contract(out, active_contract(c1))
    return out


# ---------------------------------------------------------------------------
# Steps 4-5: the ledger accept row, loadmeta
# ---------------------------------------------------------------------------


def accept_row(c1: List[str]) -> List[str]:
    return [
        "GAME_ACTION",
        "{",
        "\tut = %s" % _fmt_ut(ACCEPT_UT),
        "\ttype = 5",
        "\tactionId = %s" % ACCEPT_ACTION_ID,
        "\tseq = 1",
        "\tcontractId = %s" % CONTRACT_GUID,
        "\tcontractType = %s" % CONTRACT_TYPE,
        "\tcontractTitle = %s" % CONTRACT_TITLE,
        "\tadvanceFunds = 0",
        "\tdeadlineAbsUT = %s" % deadline_abs_ut(c1),
        "\tfundsPenalty = 0",
        "\trepPenalty = 0",
        "}",
    ]


def build_ledger(src: List[str], c1: List[str]) -> List[str]:
    out, _counts = _career.build_ledger(src)
    if any(_career._row_value(out, r, "seq") is not None
           and _career._row_value(out, r, "recordingId") is None
           for r in _career._ledger_rows(out)):
        raise SystemExit("the converted ledger already carries a KSC row; seq 1 is not free")
    if out and out[-1] == "":
        out[-1:-1] = accept_row(c1)
    else:
        out += accept_row(c1)
    return out


def build_loadmeta(src: List[str], base: List[str]) -> List[str]:
    out = _career.build_loadmeta(src, base)
    for i, line in enumerate(out):
        if line.startswith("ongoingContracts = "):
            out[i] = "ongoingContracts = 1"
            return out
    raise SystemExit("loadmeta lacks ongoingContracts")


# ---------------------------------------------------------------------------
# The whole fixture
# ---------------------------------------------------------------------------


def build_files() -> Dict[str, bytes]:
    base_sfs, _ = _read(os.path.join(SAVES, _career.BASE_NAME, SFS))
    base_meta, _ = _read(os.path.join(SAVES, _career.BASE_NAME, LOADMETA))
    c1, _ = _read(C1_SFS)
    root = os.path.join(SAVES, SOURCE)
    files: Dict[str, bytes] = {}
    for dirpath, _dirs, names in os.walk(root):
        for name in names:
            path = os.path.join(dirpath, name)
            rel = os.path.relpath(path, root)
            if rel == SFS:
                lines, nl = _read(path)
                files[rel] = _career.render_text(build_sfs(lines, base_sfs, c1), nl)
            elif rel == LOADMETA:
                lines, nl = _read(path)
                files[rel] = _career.render_text(build_loadmeta(lines, base_meta), nl)
            elif rel == LEDGER:
                lines, nl = _read(path)
                files[rel] = _career.render_text(build_ledger(lines, c1), nl)
            else:
                with open(path, "rb") as fh:
                    files[rel] = fh.read()
    if SFS not in files or LOADMETA not in files or LEDGER not in files:
        raise SystemExit("%s lacks persistent.sfs, persistent.loadmeta or the ledger" % SOURCE)
    return dict(sorted(files.items()))


def _lines(data: bytes) -> List[str]:
    return data.decode("utf-8").replace("\r\n", "\n").split("\n")


def verify(files: Dict[str, bytes]) -> List[str]:
    """Post-conditions the TOUR-1 lane depends on."""
    problems = list(_career.verify_sfs(_lines(files[SFS])))
    sfs = _lines(files[SFS])
    pod = _capsule_pod(sfs)
    crew = [sfs[i].strip() for i in range(pod[0], pod[1]) if sfs[i].startswith("\t\t\t\tcrew = ")]
    if crew != ["crew = " + PILOT, "crew = " + TOURIST]:
        problems.append("capsule pod crew is %r" % crew)
    roster = _career._top_node(sfs, "ROSTER")
    kerbals = [k for k in _recipe.child_nodes(sfs, roster, "KERBAL")
               if _recipe.get_value(sfs, k, "name") == TOURIST]
    if len(kerbals) != 1:
        problems.append("ROSTER holds %d %s" % (len(kerbals), TOURIST))
    else:
        for key, want in (("type", "Tourist"), ("trait", "Tourist"), ("state", "Assigned"),
                          ("idx", TOURIST_SEAT_IDX)):
            got = _recipe.get_value(sfs, kerbals[0], key)
            if got != want:
                problems.append("%s %s is %r, expected %r" % (TOURIST, key, got, want))
    scn = _recipe._scenario_node(sfs, "ContractSystem")
    contracts = _recipe.find_node(sfs, "CONTRACTS", scn[0], scn[1])
    cs = _recipe.child_nodes(sfs, contracts, "CONTRACT")
    if len(cs) != 1:
        problems.append("ContractSystem holds %d contracts" % len(cs))
    else:
        for key, want in (("guid", CONTRACT_GUID), ("type", CONTRACT_TYPE),
                          ("state", "Active"), ("tourists", TOURIST)):
            got = _recipe.get_value(sfs, cs[0], key)
            if got != want:
                problems.append("contract %s is %r, expected %r" % (key, got, want))
        vals = (_recipe.get_value(sfs, cs[0], "values") or "").split(",")
        if len(vals) != 12 or vals[2] != "0" or vals[4] != "0" or vals[7] != "0":
            problems.append("contract values carry an advance or a penalty: %r" % vals)
        elif not float(vals[9]) < SAVE_CLOCK_UT < float(vals[10]):
            problems.append("contract dates do not bracket the save clock: %r" % vals)
    ledger = _lines(files[LEDGER])
    accepts = [r for r in _career._ledger_rows(ledger)
               if _career._row_value(ledger, r, "type") == "5"]
    if len(accepts) != 1 or _career._row_value(ledger, accepts[0], "contractId") != CONTRACT_GUID:
        problems.append("ledger does not hold exactly the one accept row for the contract")
    elif not float(_career._row_value(ledger, accepts[0], "ut")) < SAVE_CLOCK_UT:
        problems.append("accept row is past the save clock (Ledger.Reconcile would prune it)")
    return problems


def check() -> List[str]:
    files = build_files()
    problems = verify(files)
    for rel, data in files.items():
        path = os.path.join(OUT_DIR, rel)
        if not os.path.isfile(path):
            problems.append("missing committed file: " + rel.replace(os.sep, "/"))
            continue
        with open(path, "rb") as fh:
            if fh.read() != data:
                problems.append("committed file drifted from the builder: " + rel.replace(os.sep, "/"))
    committed = set()
    for root, _dirs, names in os.walk(OUT_DIR):
        for n in names:
            committed.add(os.path.relpath(os.path.join(root, n), OUT_DIR))
    extra = sorted(committed - set(files))
    if extra:
        problems.append("unexpected committed files: %r" % extra)
    return problems


def _write(files: Dict[str, bytes]) -> None:
    if os.path.isdir(OUT_DIR):
        shutil.rmtree(OUT_DIR)
    for rel, data in files.items():
        path = os.path.join(OUT_DIR, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as fh:
            fh.write(data)


def main(argv: List[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    g = ap.add_mutually_exclusive_group(required=True)
    g.add_argument("--check", action="store_true")
    g.add_argument("--write", action="store_true")
    args = ap.parse_args(argv)
    if args.write:
        files = build_files()
        problems = verify(files)
        if problems:
            for p in problems:
                print("PROBLEM: " + p)
            return 1
        _write(files)
        print("wrote %d file(s) under %s" % (len(files), OUT_DIR))
        return 0
    problems = check()
    for p in problems:
        print("PROBLEM: " + p)
    print("OK" if not problems else "FAIL")
    return 0 if not problems else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
