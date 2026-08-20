#!/usr/bin/env python3
"""Build the `strategy-career` fixture BY CONSTRUCTION (no KSP launch).

WHY THIS EXISTS. `L3-strategy-currency-conversion` drives the whole
`StrategyLifecycle` in-game category, and exactly one of its seven cells could
never run:

    OperationStrategy_RewardMultiplier_IsNotCaptured   SKIP
    "'LeadershipInitiative' cannot be activated on this save:
     Cannot afford Setup Cost: Not enough Reputation"

That cell was the category's NEGATIVE CONTROL - the one declaration that failed
if `StrategyConversionCapture.EvaluateLegs`'s scoping rule was deleted and the
query-family door started capturing everything - so the skip was the matrix's
only real coverage hole. (It is now
`OperationStrategy_RewardMultiplier_IsCapturedOnNominalReason`: the 2026-08-21
funds-debit wave found its own reason, Progression, on the NOMINAL-channel side
of the qualification, so the uplift it drives IS captured and the remaining
suppression is pinned headlessly. The activation requirement below - the only
thing this fixture exists to satisfy - is unchanged.)

THE REQUIREMENT, READ FROM STOCK CONFIG AND STOCK CODE (not guessed):

  GameData/Squad/Strategies/Strategies.cfg, STRATEGY LeadershipInitiative
      initialCostReputationMin = 10.0
      initialCostReputationMax = 100.0
      hasFactorSlider          = True
      factorSliderDefault      = 0.05

  Assembly-CSharp `Strategies.Strategy`
      InitialCostReputation => FactorLerp(InitialCostReputationMin,
                                          InitialCostReputationMax)
      FactorLerp(a, b)      => Mathf.Lerp(a, b, Factor)
      Create(...)           => factor = FactorSliderDefault when Factor == 0
      CanBeActivated(reason):
          ... if (Reputation.Instance.reputation < InitialCostReputation)
                  reason = "Cannot afford Setup Cost: Not enough Reputation"

  => the gate is a CURRENT-POOL comparison taken at ACTIVATION time, against
     Mathf.Lerp(10, 100, 0.05) = 14.5 reputation.

`fresh-career` seeds `SCENARIO { name = Reputation; rep = 0 }`, so the pool is
below the gate and the cell self-skips. `Activate()` then charges the cost with
`Reputation.Instance.AddReputation(-InitialCostReputation,
TransactionReasons.StrategySetup)`, which is a ledger-modelled stock reason, and
the cell restores the pool with `SetReputation` (absolute, no curve) in its
`finally`. So a SEED clears the gate outright: the fixture's reputation lands in
the ledger as a `ReputationInitial` SEED row, which
`ReputationModule.ProcessReputationInitial` assigns directly with NO curve
applied. That is the whole reason a seed works where an in-batch top-up does
not: there is no positive no-recurve action type, and a curve-approximate
top-up would have to survive the reputation guard's 0.01 epsilon.

WHY A SIBLING RATHER THAN AN EDIT TO `fresh-career`. This is the
`career-science-pad` precedent applied to the other base fixture, and here the
collateral is measured rather than feared. SEVEN committed specs stage
`fresh-career` directly (B10, the four L1 career scripts, this one, R7c) and SIX
more stage `career-pad-craft`, which `build_career_pad_craft.py` DERIVES from
`fresh-career` (CL-1, CL-2, CL-3, H26, L2, R7a) - plus `career-science-pad`
below that. Mutating the base would re-open all of them, and two of them pin
values that are FUNCTIONS OF THE REPUTATION POOL, because KSP's granular
reputation curve is state-dependent:

    CL-2-pod-impact-ledger pins KSP's OWN post-curve digits as an EXACT-DIGIT
    logContract regex - the crew-loss hit
        "Added -9.999828 (-10) reputation: 'VesselLoss'"
    and the two `+1` Progression awards
        "Added 0.9999995 (1) reputation: 'Progression'"

    `oracle.apply_rep_curve` (Parsek's replica of the same curve, as of PR
    #1508's residual-step port - which reproduces that Progression pin to all
    seven printed digits at rep 0, so it is a calibrated instrument and not an
    analogy) puts a -10 nominal at -9.9996061 from rep 0 against -10.0001546
    from rep 25. That 5.5e-4 shift is ~500x the last printed digit, so the
    exact-digit regex would stop matching and would have to be re-flown and
    re-measured.

    NOTE WHICH SURFACE BINDS: the oracle's own reputation FACET would NOT red at
    that size (its tolerance is 0.1), so the cost of seeding the base is a
    re-flown logContract pin rather than a moved manifest amount. Stated
    precisely because the first draft of this file claimed a ~0.3 shift against
    the 0.1 facet tolerance, which was measured against the PRE-#1508 replica
    and is wrong.

So `fresh-career` is NOT touched. This builds a sibling beside it, and `--check`
re-verifies both the base's career post-conditions and this fixture's own.

WHAT IT SPLICES. Exactly two edits against the `fresh-career` save, plus one
derived `persistent.loadmeta` field:

  1. GAME `Title` -> "strategy-career (CAREER)", matching every other committed
     fixture's own leaf (the leaf IS the runSaveName run.py stages into).
  2. `SCENARIO { name = Reputation }`'s `rep` -> the seed below.
  3. loadmeta `reputationPercent` -> `(int)(rep / 10f)`, which is verbatim what
     `LoadGameDialog`'s save-info reader computes from the same field. It is
     Load-menu preview only - nothing in the harness reads it - but a fixture
     whose two halves disagree is a fixture that teaches the next reader wrong.

HOW THE SEED VALUE WAS DERIVED. Not chosen for roundness; solved for, from the
same stock config the requirement came from. Two bounds:

  FLOOR   14.5, the activation gate above.
  CEILING 35.0, `UnpaidResearchProgramCfg`'s own lerped reputation setup cost -
          the next stock strategy that a rising pool would newly unblock. (The
          one after that is `AgressiveNegotiations` at `requiredReputationMin`
          38.0.) Staying under it MATTERS because two cells in this category
          take whatever `ProbeActivatableStockStrategy` returns, which is the
          FIRST activatable entry in `StrategySystem.Instance.Strategies`; the
          smaller the set change, the less the seed can move a subject out from
          under a cell that did not ask for it.

  Any admissible seed necessarily unblocks `FundraisingCampaignCfg` (cost 7.3)
  and `LeadershipInitiative` (14.5), because 14.5 > 7.3. Those two are the
  MINIMUM disturbance the requirement forces. `AppreciationCampaignCfg` (funds
  70750, no reputation cost) is already activatable at rep 0 and already sits
  ahead of both in config order, so the probe's pick does not move.

  Centre of [14.5, 35.0) is 24.75; `REPUTATION_SEED` is 25 - 1.72x the cost it
  must clear, 10 short of the next threshold, and the nearest whole number to
  the midpoint of the admissible band.

WHAT IS NOT TOUCHED. Everything else comes from `fresh-career` unchanged:
`Mode = CAREER`, `Funding` 500000, `ResearchAndDevelopment` 100 (the science
seed the two science-costing cells top up ledger-visibly), ZERO `VESSEL` nodes -
which is the load-bearing one, because all seven `StrategyLifecycle`
declarations are `Scene = GameScenes.SPACECENTER` and it is the empty
`FLIGHTSTATE` that makes `TestCommandLoadGame.DecideLoadRoute` take the
`NoVesselSpaceCenter` route where they are all eligible - the empty
`SCENARIO { name = StrategySystem }` `STRATEGIES` node (so every strategy is
built fresh from config at `factorSliderDefault`, which is what pins the 14.5),
and the deliberate ABSENCE of a `SCENARIO { name = ParsekScenario }` node, which
`fresh-career`'s KSC-route consumers rely on and which `PreParsekBackup` reads.

Usage:
    python harness/tools/build_strategy_career.py            # write it
    python harness/tools/build_strategy_career.py --check    # verify only

`--check` re-runs every post-condition against the ALREADY COMMITTED fixture. It
is WIRED, not decorative: `StrategyCareerFixtureDriftTests` in
`harness/lib/test_strategy_career_fixture.py` runs the same `verify` in-process
AND re-runs `build` over the CURRENT `fresh-career` bytes, asserting
byte-identity with the committed save - so a change to the base fixture reds in
the suite instead of drifting silently into a live flight.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import argparse
import os
import shutil
import sys
from typing import List, Optional

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS_ROOT = os.path.dirname(_HERE)
_SAVES = os.path.join(_HARNESS_ROOT, "fixtures", "saves")

if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

# ONE copy of the ConfigNode-text helpers and ONE copy of the career
# post-conditions worth reusing. Importing rather than re-implementing is what
# keeps "the base's shape still holds" a fact this file cannot drift away from.
import build_career_pad_craft as base_builder  # noqa: E402

read_lines = base_builder.read_lines
find_node = base_builder.find_node
child_nodes = base_builder.child_nodes
get_value = base_builder.get_value
set_value = base_builder.set_value
set_top_value = base_builder.set_top_value

BASE_NAME = "fresh-career"
TARGET_NAME = "strategy-career"

# THE ONE NUMBER THIS FIXTURE EXISTS FOR. See "HOW THE SEED VALUE WAS DERIVED".
REPUTATION_SEED = 25

# Stock `LeadershipInitiative` at `factorSliderDefault = 0.05`:
# Mathf.Lerp(10.0, 100.0, 0.05). Restated as code so `verify` gates on the
# REQUIREMENT rather than on the seed agreeing with itself.
LEADERSHIP_INITIATIVE_SETUP_REP_COST = 10.0 + (100.0 - 10.0) * 0.05  # 14.5

# The next stock reputation threshold above the seed - the ceiling of the
# admissible band. `UnpaidResearchProgramCfg` lerps its own
# initialCostReputation 30.0..130.0 at the same 0.05.
NEXT_STOCK_REP_THRESHOLD = 30.0 + (130.0 - 30.0) * 0.05  # 35.0

# Inherited from the base and asserted, not assumed: a consumer reasoning from
# `fresh-career`'s seed must still be right about this fixture.
EXPECT_MODE = "CAREER"
EXPECT_FUNDS = "500000"
EXPECT_SCIENCE = "100"


def newline_of(path: str) -> str:
    """The line terminator the file on disk actually uses.

    `harness/fixtures/**` is `-text` in `.gitattributes`, so what is committed is
    what lands in the working tree verbatim, and every committed save under
    `fixtures/saves/` is LF. `build_career_pad_craft.write_lines` hardcodes CRLF,
    which is harmless for ITS drift cell (that one compares normalized LINE
    LISTS, not bytes) but would land this sibling on disk in a convention none of
    its siblings use, and 2.3 KB larger than the base it is a two-line edit of.
    Reading the terminator off the base is the version that cannot be wrong
    whichever way the base is stored."""
    with open(path, "rb") as fh:
        data = fh.read()
    return "\r\n" if b"\r\n" in data else "\n"


def write_lines(path: str, lines: List[str], newline: str = "\n") -> None:
    """Write lines back with an explicit terminator (see `newline_of`).

    `read_lines` normalizes CRLF to LF before splitting, so a file that ended
    with a terminator yields a trailing empty element and this join reproduces
    it - the round trip is exact in both directions."""
    with open(path, "w", encoding="utf-8", newline="") as fh:
        fh.write(newline.join(lines))


# ---------------------------------------------------------------------------
# The splice.
# ---------------------------------------------------------------------------


def _scenario_node(lines: List[str], name: str):
    """(header, end) of the GAME-level `SCENARIO` whose `name = <name>`."""
    i = 0
    while True:
        node = find_node(lines, "SCENARIO", i)
        if node is None:
            return None
        if get_value(lines, node, "name") == name:
            return node
        i = node[1]


def build(base_lines: List[str], title: str,
          reputation_seed: int = REPUTATION_SEED) -> List[str]:
    """Return the seeded save. Pure over the input line list."""
    out = list(base_lines)

    if not set_top_value(out, "Title", title):
        raise SystemExit("base has no GAME-level Title line")

    rep_node = _scenario_node(out, "Reputation")
    if rep_node is None:
        raise SystemExit("base has no SCENARIO { name = Reputation } node")
    if not set_value(out, rep_node, "rep", str(reputation_seed)):
        raise SystemExit("the Reputation SCENARIO carries no `rep` value")

    return out


def build_loadmeta(base_meta: List[str], reputation_seed: int = REPUTATION_SEED) -> List[str]:
    """Restamp `reputationPercent` from the seed.

    `LoadGameDialog`'s save-info reader computes it as
    `(int)(float.Parse(rep) / 10f)`, so this is that expression and not an
    approximation of it. Every other field is the base's: the seed moves no
    vessel, no UT and no other pool."""
    out = list(base_meta)
    percent = int(float(reputation_seed) / 10.0)
    for i, line in enumerate(out):
        if line.startswith("reputationPercent = "):
            out[i] = "reputationPercent = %d" % percent
    return out


# ---------------------------------------------------------------------------
# Post-conditions. Run on both the freshly built save and on --check.
# ---------------------------------------------------------------------------


def verify(lines: List[str], base_lines: List[str],
           reputation_seed: int = REPUTATION_SEED) -> List[str]:
    """Return a list of failure strings (empty = every post-condition holds)."""
    problems: List[str] = []

    mode = None
    for line in lines:
        if line.startswith("\tMode = "):
            mode = line.split("=", 1)[1].strip()
            break
    if mode != EXPECT_MODE:
        problems.append("GAME Mode is %r, expected %r" % (mode, EXPECT_MODE))

    # THE SUBJECT. Both halves asserted: the value is the seed, and the seed
    # actually clears the stock gate this fixture exists to clear.
    rep_node = _scenario_node(lines, "Reputation")
    if rep_node is None:
        problems.append("no SCENARIO { name = Reputation } node")
    else:
        rep = get_value(lines, rep_node, "rep")
        if rep != str(reputation_seed):
            problems.append("Reputation SCENARIO rep is %r, expected %r"
                            % (rep, str(reputation_seed)))
        else:
            value = float(rep)
            if value < LEADERSHIP_INITIATIVE_SETUP_REP_COST:
                problems.append(
                    "rep %g is below LeadershipInitiative's %g setup cost - the "
                    "cell this fixture exists for would still self-skip"
                    % (value, LEADERSHIP_INITIATIVE_SETUP_REP_COST))
            if value >= NEXT_STOCK_REP_THRESHOLD:
                problems.append(
                    "rep %g reaches the next stock reputation threshold (%g) - the "
                    "seed must stay under it so the activatable-strategy set, and "
                    "with it the probe-driven cells' subject, moves as little as "
                    "the requirement allows"
                    % (value, NEXT_STOCK_REP_THRESHOLD))

    # THE SCENE ROUTE. Zero VESSEL nodes is what puts the batch at the space
    # centre where all seven SPACECENTER-scened declarations are eligible; one
    # vessel would route to FLIGHT and scene-skip every one of them.
    fs = find_node(lines, "FLIGHTSTATE")
    if fs is None:
        problems.append("no FLIGHTSTATE node")
    else:
        vessels = child_nodes(lines, fs, "VESSEL")
        if vessels:
            problems.append("expected 0 VESSEL nodes, found %d - a vessel routes "
                            "LoadGame to FLIGHT and scene-skips the whole category"
                            % len(vessels))

    # The other two pools, inherited unchanged. The science seed in particular
    # is what the two science-costing cells top up from.
    funding = _scenario_node(lines, "Funding")
    if funding is None:
        problems.append("no SCENARIO { name = Funding } node")
    elif get_value(lines, funding, "funds") != EXPECT_FUNDS:
        problems.append("Funding funds is %r, expected %r"
                        % (get_value(lines, funding, "funds"), EXPECT_FUNDS))
    rnd = _scenario_node(lines, "ResearchAndDevelopment")
    if rnd is None:
        problems.append("no SCENARIO { name = ResearchAndDevelopment } node")
    elif get_value(lines, rnd, "sci") != EXPECT_SCIENCE:
        problems.append("ResearchAndDevelopment sci is %r, expected %r"
                        % (get_value(lines, rnd, "sci"), EXPECT_SCIENCE))

    # An EMPTY STRATEGIES node is what makes `Strategy.Create` build every
    # strategy fresh at `factorSliderDefault`, which is what pins the 14.5 the
    # seed is sized against. A persisted strategy could carry any `factor`.
    strategy_system = _scenario_node(lines, "StrategySystem")
    if strategy_system is None:
        problems.append("no SCENARIO { name = StrategySystem } node")
    else:
        strategies = find_node(lines, "STRATEGIES", strategy_system[0],
                               strategy_system[1])
        if strategies is None:
            problems.append("the StrategySystem SCENARIO carries no STRATEGIES node")
        elif strategies[1] - strategies[0] != 3:
            problems.append(
                "the STRATEGIES node is not empty (%d lines) - a persisted "
                "strategy can carry any `factor`, and the seed is sized against "
                "factorSliderDefault" % (strategies[1] - strategies[0]))

    # `fresh-career`'s deliberate absence of a Parsek footprint is inherited.
    if _scenario_node(lines, "ParsekScenario") is not None:
        problems.append("a ParsekScenario SCENARIO node is present - the base "
                        "deliberately carries none, and its presence would "
                        "suppress PreParsekBackup on a fixture never meant to")

    if title_of(lines) != "%s (CAREER)" % TARGET_NAME:
        problems.append("GAME Title is %r, expected %r"
                        % (title_of(lines), "%s (CAREER)" % TARGET_NAME))

    # THE DERIVATION ITSELF. Every line except Title and rep must be the base's,
    # so a base change cannot arrive here as anything but a rebuild.
    expected = build(base_lines, "%s (CAREER)" % TARGET_NAME, reputation_seed)
    if expected != lines:
        problems.append("the save is not what `build` produces from the current "
                        "%s bytes - re-run this builder" % BASE_NAME)

    return problems


def title_of(lines: List[str]) -> Optional[str]:
    for line in lines:
        if line.startswith("\tTitle = "):
            return line.split("=", 1)[1].strip()
    return None


# ---------------------------------------------------------------------------


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--check", action="store_true",
                        help="verify the committed fixture instead of writing it")
    parser.add_argument("--target-name", default=TARGET_NAME)
    parser.add_argument("--reputation", type=int, default=REPUTATION_SEED)
    args = parser.parse_args(argv)

    base_dir = os.path.join(_SAVES, BASE_NAME)
    base_sfs = os.path.join(base_dir, "persistent.sfs")
    target_dir = os.path.join(_SAVES, args.target_name)
    target_sfs = os.path.join(target_dir, "persistent.sfs")

    if not os.path.isfile(base_sfs):
        print("FAIL: missing input fixture %s" % base_sfs)
        return 1
    base_lines = read_lines(base_sfs)

    if args.check:
        if not os.path.isfile(target_sfs):
            print("FAIL: %s does not exist" % target_sfs)
            return 1
        problems = verify(read_lines(target_sfs), base_lines, args.reputation)
        for problem in problems:
            print("FAIL: %s" % problem)
        if problems:
            return 1
        print("OK: %s satisfies every post-condition" % args.target_name)
        return 0

    built = build(base_lines, "%s (CAREER)" % args.target_name, args.reputation)
    problems = verify(built, base_lines, args.reputation)
    if problems:
        for problem in problems:
            print("FAIL: %s" % problem)
        return 1

    os.makedirs(target_dir, exist_ok=True)
    base_meta = os.path.join(base_dir, "persistent.loadmeta")
    write_lines(target_sfs, built, newline_of(base_sfs))
    write_lines(os.path.join(target_dir, "persistent.loadmeta"),
                build_loadmeta(read_lines(base_meta), args.reputation),
                newline_of(base_meta))
    addons_src = os.path.join(base_dir, "AddOns")
    addons_dst = os.path.join(target_dir, "AddOns")
    if os.path.isdir(addons_src):
        shutil.copytree(addons_src, addons_dst, dirs_exist_ok=True)
    print("OK: wrote %s (base=%s rep=%d, LeadershipInitiative setup cost %g)"
          % (target_dir, BASE_NAME, args.reputation,
             LEADERSHIP_INITIATIVE_SETUP_REP_COST))
    return 0


if __name__ == "__main__":
    sys.exit(main())
