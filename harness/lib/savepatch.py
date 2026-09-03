"""Pure FLIGHTSTATE patching for the per-spec `[[fixture.liveState]]` stage step.

WHAT THIS EXISTS FOR. A supply route REPLAYS a recorded run against the CURRENT
LIVE ENDPOINTS, so what a dispatch lane measures is decided almost entirely by
two numbers that live in FLIGHTSTATE and nowhere else: how much the source holds
at the moment of the crossing, and how much headroom the destination has. Every
interesting edge of the dispatch gate - origin empty, origin partial, cargo
missing, destination full, destination partial, destination empty - is the SAME
committed recording payload against a DIFFERENT live endpoint state.

Before this module the only way to author those lanes was one committed fixture
per variant: a full save tree (persistent.sfs plus ~39 sidecars, megabytes) whose
Parsek payload is byte-identical to its siblings and whose ONLY difference is a
single `amount =` line. That is a maintenance liability rather than a fixture -
every re-harvest has to be repeated N times, and `RECORDED_FIXTURES` grows N
shapes that can drift apart silently. `[[fixture.liveState]]` inverts it: ONE
committed fixture, and each spec declares the live endpoint state IT wants, in
the spec, next to the tokens that depend on it.

TWO SPEC SURFACES, kept separate on purpose. `[[fixture.liveState]]` is
FLIGHTSTATE-only (per-vessel resources, inventory, or whole-node REMOVAL);
`[fixture.career]` writes ONE key in ONE career SCENARIO node (`Funding.funds`).
They are not folded together because the FLIGHTSTATE boundary below is the safety
argument for the first, and smuggling a top-level SCENARIO write through a vessel
entry would erase it.

WHAT IS AND IS NOT TOUCHED, and the boundary is the whole safety argument:
  * FLIGHTSTATE ONLY, and only the vessels a spec names by `persistentId` (plus,
    under its own key, the career `Funding` pool - see above).
  * THE PARSEK PAYLOAD IS NEVER TOUCHED. No recording, no route window, no
    branch point, no origin proof, no ledger row. The route windows are this
    module's INPUT (the `restore-dock-endpoint` mode reads them), so editing one
    would make the patch unfalsifiable in exactly the way the builder's own
    start-of-cycle repair refuses to be.
  * The staged copy is patched, never the committed fixture: `run.py` copies the
    template into the automation instance FIRST and patches the copy.

THE PRECEDENT, and why the implementation is SHARED rather than parallel.
`harness/tools/build_rover_relay_c_recorded.py` step 3 already does exactly this
edit once, at BUILD time, to stage `rover-relay-c-recorded` at start-of-cycle: it
restores rover B and rover A to the state THEIR OWN route window recorded at ITS
dock. That code - lifting a `STOREDPART` block out of a window snapshot,
re-indenting it to FLIGHTSTATE depth, splicing it into a `ModuleInventoryPart`'s
`STOREDPARTS` body and rewriting the slot-ascending `inventory` CSV (absent, not
blank, when the container ends up empty) - is fiddly, was got wrong twice during
authoring, and is now the SAME functions on both sides: the builder imports them
from here. A second copy would be a second thing to drift, and the drift would be
invisible (both sides would produce a save that LOADS).

DETERMINISM. Text in, text out, stdlib only, no clock, no filesystem, no
randomness. Line endings are preserved: the file's own separator is detected and
restored, because `rover-relay-c-recorded` is LF (the harvest wrote it that way)
while every builder-authored fixture is CRLF, and a whole-file re-ending would
turn a one-line patch into a multi-megabyte diff.

FAIL CLOSED, ALWAYS. Every fault raises `LiveStatePatchError` naming the vessel,
the resource or the window, and `run.py` turns that into a pre-boot
`INVALID(staging)` with nothing launched. The failure mode this rules out is the
expensive one: a patch that silently did nothing, a lane that then measures the
UNPATCHED fixture, and a green run that proves the opposite of what its header
says. There is no fail-open path here and there must never be one.

ASCII only; no em dashes.
"""

from __future__ import annotations

import os as _os
import sys as _sys
from typing import Any, Dict, List, Optional, Sequence, Tuple

# ONE copy of the ConfigNode-text node helpers. `tools/build_career_pad_craft.py`
# is where every fixture builder already imports them from, and this module is
# the FIRST lib-side consumer, so it imports the same four functions rather than
# growing a second implementation on the other side of the lib/tools line.
# `test_savepatch.py` asserts the imported objects are IDENTICAL to the builder's
# (`is`, not equality), which is a drift guard no comment can provide. The
# helpers are pure and stdlib-only, so the direction of this import creates no
# cycle: `build_career_pad_craft` imports nothing from `lib/`.
#
# APPENDED, not inserted at 0, and the difference matters: `hlib` imports this
# module, so this path edit happens inside every harness process, and putting a
# directory of 30-odd scripts AHEAD of the stdlib would make any future
# `tools/<stdlib-name>.py` shadow the real module everywhere. Appending makes the
# import work without granting `tools/` precedence over anything.
# `test_savepatch.py` also asserts `tools/` shares no module name with `lib/`,
# `harness/` or the stdlib, so the collision cannot arise unnoticed either way.
_LIB_DIR = _os.path.dirname(_os.path.abspath(__file__))
_TOOLS_DIR = _os.path.join(_os.path.dirname(_LIB_DIR), "tools")
if _TOOLS_DIR not in _sys.path:
    _sys.path.append(_TOOLS_DIR)

import build_career_pad_craft as _nodes  # noqa: E402

find_node = _nodes.find_node
child_nodes = _nodes.child_nodes
get_value = _nodes.get_value
set_value = _nodes.set_value


class LiveStatePatchError(Exception):
    """A declared liveState entry cannot be applied to these bytes.

    Always fatal to the run, pre-boot. The message names the vessel / resource /
    window at fault so the harness log says WHICH declaration is wrong rather
    than that staging failed."""


# ---------------------------------------------------------------------------
# The spec grammar.
# ---------------------------------------------------------------------------

# `[[fixture.liveState]]` - an ARRAY of tables, one per vessel, so the ordinary
# author-facing shape reads as a list of endpoints:
#
#     [[fixture.liveState]]
#     pid       = 90564594                 # rover B, the pickup source
#     resources = { LiquidFuel = 0 }       # drain the tank
#     inventory = "clear"                  # and empty both containers
#
# Key names follow the surrounding `[fixture]` block's camelCase (`saveTemplate`,
# `injectedRecordings`), and `pid` is spelled the way every route spec and every
# log token in this family spells a KSP `persistentId`.
LIVE_STATE_KEY = "liveState"

ENTRY_KEYS = ("pid", "resources", "inventory", "remove")

# `remove = true` DELETES the vessel's whole FLIGHTSTATE `VESSEL` node, which is
# the only way to author "the endpoint this route names is no longer in the save"
# without a second harvest. It is exclusive with `resources` / `inventory` (both
# would patch a node that is about to be deleted) and it is REFUSED for any
# vessel at or before the save's `activeVessel` index: `activeVessel` is an INDEX
# into the FLIGHTSTATE vessel list in file order, so removing an earlier vessel
# silently re-points the focus at a different craft and every token the lane
# derives becomes a token about a different scene. Removing a LATER one leaves
# the index naming the same vessel it named before.
#
# WHAT IT DOES NOT DO, and the distinction matters when reading a lane header: it
# does not by itself produce an `EndpointLost` hold.
# `RouteEndpointResolver.TryResolveEndpoint` walks root-part -> pid -> SURFACE
# PROXIMITY, so on a surface endpoint the removal only opens the proximity step,
# and whether that step misses is a property of what else is parked near the
# recorded coordinates. On `rover-route-recorded` it does NOT miss (RVR-18).
REMOVE_KEY = "remove"

# The inventory modes. `keep` is the DEFAULT (an absent key changes nothing), so
# a spec that only wants a tank number writes only `resources`.
INVENTORY_KEEP = "keep"
INVENTORY_CLEAR = "clear"
# `restore-dock-endpoint:<windowIndex>` restores the vessel's containers from
# window N's own `DOCK_ENDPOINT_INVENTORY` snapshot - the fixture's OWN recorded
# bytes, which is what makes the restored state auditable rather than invented.
# This is the mode the BUILDER uses (through the same functions below).
INVENTORY_RESTORE_DOCK_PREFIX = "restore-dock-endpoint:"

# WHY THERE IS NO `restore-undock-endpoint:<N>` AND NO `fill` MODE, recorded here
# because the obvious next request is one of them and the answer is a property of
# the bytes rather than a scope call:
#
#   * `UNDOCK_ENDPOINT_INVENTORY` IS NOT A CENSUS OF THE RESULTING INVENTORY.
#     Measured on `rover-relay-c-recorded`: window 1's holder carries FOUR items
#     - `DeployedCentralStation` slot 1, `DeployedCentralStation` slot 1,
#     `evaChute` slot 0, `evaScienceKit` slot 2 - against a rover A that holds
#     SIX stored parts live. Two of those items are the SAME part name at the
#     SAME `slotIndex`, and the snapshot records no container index at all, so
#     there is no rule inside these bytes that assigns them to containers. The
#     builder needed rover A's LIVE `persistentId`s to tell the original station
#     from the delivered one; a restore mode has, by construction, no live
#     vessel to read.
#   * A `fill` MODE WOULD BE INVENTED BYTES. Filling the free slots means
#     authoring `STOREDPART` nodes no snapshot in this save recorded, which is
#     the one thing every fixture builder in this tree refuses to do.
#
# So the DESTINATION-SLOTS-FULL edge is not expressible today ON THAT FIXTURE by
# STAGING, and the roadmap records that it needs a FLIGHTSTATE fill mode (or a
# second harvest) rather than a new string here. It IS expressible on
# `rover-route-recorded` without any new mode, and the difference is worth
# stating because it is what stopped a `fill` mode being built: there the
# destination starts with 3 of 6 slots free and ONE cycle consumes two of them,
# so a lane that removes the RESOURCE constraint (a plain `resources` entry, the
# tank staged low) reaches the slot shortfall by PLAYING the fixture rather than
# by authoring bytes. RVR-16 is that lane.


# ---------------------------------------------------------------------------
# The career spec surface: `[fixture.career]`.
# ---------------------------------------------------------------------------

# `[fixture.career]` - a SINGLE table (not an array), because what it addresses is
# the save's one career SCENARIO set rather than a list of vessels:
#
#     [fixture.career]
#     funds = 7409                  # one dispatch cost short, so the gate refuses
#
# WHY IT IS NOT A `liveState` ENTRY. `liveState` is FLIGHTSTATE-only by contract,
# and that boundary is the whole safety argument of this module. The funds pool
# lives in `SCENARIO { name = Funding }`, a top-level sibling of FLIGHTSTATE, so
# it gets its own key, its own validator and its own applier rather than being
# smuggled through a vessel entry.
#
# WHY ONLY `funds`. Reputation and science clamp through the same
# `KspStatePatcher.ApplyDrawdownGuard` path but no lane's tokens are derived from
# either, and a key nothing reads is a key that rots. The dispatch gate's step 7
# (`env.IsCareer && route.IsKscOrigin` -> `funds >= cost`) is the ONE career
# quantity a route lane's arithmetic runs on.
#
# THE SEEDED POOL IS THE LIVE POOL, which is what makes a declared number
# predictable: `LedgerOrchestrator.EnsureInitialFundsSeed` seeds the ledger from
# `Funding.Instance.Funds`, and `PatchFunds`' guarded uplift REFUSES to raise the
# live pool to a ledger running balance above it (RVR-4 measured
# `GUARDED UPLIFT clamped ... running=29200 live=11000 clampedTo=11000` on this
# very fixture family). So the value written here is the value the funds gate
# reads, milestone rows in the committed ledger notwithstanding.
CAREER_KEY = "career"
CAREER_KEYS = ("funds",)

# The SCENARIO node that carries the pool, and the key inside it.
CAREER_FUNDING_SCENARIO = "Funding"
CAREER_FUNDS_KEY = "funds"


def _is_number(value: Any) -> bool:
    """A TOML number, excluding bool (which is an int subclass in Python)."""
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def parse_inventory_mode(mode: str) -> Tuple[str, Optional[int]]:
    """(kind, windowIndex) for a declared inventory mode string.

    Returns ``("restore-dock-endpoint", N)`` for the parametrised form and
    ``(mode, None)`` for `keep` / `clear`. Raises ValueError on anything else -
    callers that must not raise (the validator) catch it."""
    if mode in (INVENTORY_KEEP, INVENTORY_CLEAR):
        return mode, None
    if mode.startswith(INVENTORY_RESTORE_DOCK_PREFIX):
        tail = mode[len(INVENTORY_RESTORE_DOCK_PREFIX):]
        # Digits only, on purpose: `int()` would accept "+1", " 1" and "1_0".
        if tail.isdigit():
            return INVENTORY_RESTORE_DOCK_PREFIX[:-1], int(tail)
    raise ValueError(
        "inventory: %r is not one of %r / %r / %r<windowIndex>"
        % (mode, INVENTORY_KEEP, INVENTORY_CLEAR, INVENTORY_RESTORE_DOCK_PREFIX))


def validate_live_state(fixture: Any) -> List[str]:
    """Validate the `[[fixture.liveState]]` spec surface. Pure; pre-launch.

    Called from `hlib.validate_spec`, which is where every other fixture key is
    checked, so a malformed declaration is an INVALID-SPEC with KSP never
    launched rather than a staging abort after the instance was prepared.

    WHAT IS CHECKABLE HERE and what deliberately is not: this runs with no save
    in hand, so it checks SHAPE (pid is a positive int, resources are
    name -> non-negative number, inventory is one of the enumerated modes, no
    unknown keys, no duplicate pid). Whether the pid EXISTS in the fixture,
    whether the resource exists on that vessel, and whether the amount exceeds
    `maxAmount` are checked by the applier against the bytes - a spec-time check
    would have to parse the fixture and would then be a different, weaker copy
    of the applier's own assertions."""
    errs: List[str] = []
    if not isinstance(fixture, dict):
        return errs
    if LIVE_STATE_KEY not in fixture:
        return errs
    entries = fixture[LIVE_STATE_KEY]
    if not isinstance(entries, list):
        return ["fixture.%s: must be an array of tables ([[fixture.%s]])"
                % (LIVE_STATE_KEY, LIVE_STATE_KEY)]
    if not entries:
        # An empty array is inert and therefore misleading: it reads as "this
        # lane declares live state" while patching nothing.
        return ["fixture.%s: declared but empty; omit the key instead"
                % LIVE_STATE_KEY]
    seen_pids: Dict[int, int] = {}
    for i, entry in enumerate(entries):
        where = "fixture.%s[%d]" % (LIVE_STATE_KEY, i)
        if not isinstance(entry, dict):
            errs.append("%s: must be a table" % where)
            continue
        unknown = sorted(k for k in entry if k not in ENTRY_KEYS)
        if unknown:
            errs.append("%s: unknown key(s) %s (accepted: %s)"
                        % (where, unknown, list(ENTRY_KEYS)))
        pid = entry.get("pid")
        if not isinstance(pid, int) or isinstance(pid, bool) or pid <= 0:
            errs.append("%s.pid: %r must be a positive integer persistentId"
                        % (where, pid))
        else:
            if pid in seen_pids:
                errs.append(
                    "%s.pid: %d is already declared by %s[%d]; one entry per "
                    "vessel (two entries would apply in file order and the "
                    "second would silently win)"
                    % (where, pid, "fixture." + LIVE_STATE_KEY, seen_pids[pid]))
            seen_pids[pid] = i
        if "resources" in entry:
            res = entry["resources"]
            if not isinstance(res, dict):
                errs.append("%s.resources: must be a table of "
                            "{ <ResourceName> = <amount> }" % where)
            elif not res:
                errs.append("%s.resources: declared but empty; omit the key "
                            "instead" % where)
            else:
                for name, amount in res.items():
                    if not isinstance(name, str) or not name:
                        errs.append("%s.resources: %r is not a resource name"
                                    % (where, name))
                        continue
                    if not _is_number(amount):
                        errs.append("%s.resources.%s: %r must be a number"
                                    % (where, name, amount))
                    elif amount < 0:
                        errs.append("%s.resources.%s: %r must be >= 0"
                                    % (where, name, amount))
        if "inventory" in entry:
            mode = entry["inventory"]
            if not isinstance(mode, str):
                errs.append("%s.inventory: %r must be a string" % (where, mode))
            else:
                try:
                    parse_inventory_mode(mode)
                except ValueError as ex:
                    errs.append("%s.%s" % (where, ex))
        if REMOVE_KEY in entry:
            remove = entry[REMOVE_KEY]
            if not isinstance(remove, bool):
                errs.append("%s.%s: %r must be the boolean true"
                            % (where, REMOVE_KEY, remove))
            elif not remove:
                # Inert and therefore misleading, the same call the empty-array
                # and empty-table cases make: it reads as "this lane removes a
                # vessel" while removing none.
                errs.append("%s.%s: false patches nothing; omit the key instead"
                            % (where, REMOVE_KEY))
            elif ("resources" in entry) or ("inventory" in entry):
                errs.append(
                    "%s: `%s = true` cannot be combined with `resources` / "
                    "`inventory` - those would patch a VESSEL node this entry "
                    "then deletes, so the declaration reads as two different "
                    "intentions" % (where, REMOVE_KEY))
        if (("resources" not in entry) and ("inventory" not in entry)
                and (REMOVE_KEY not in entry)):
            errs.append(
                "%s: declares neither `resources`, `inventory` nor `%s`, so it "
                "patches nothing" % (where, REMOVE_KEY))
    return errs


def validate_career_state(fixture: Any) -> List[str]:
    """Validate the `[fixture.career]` spec surface. Pure; pre-launch.

    Called from `hlib.validate_spec` for the same reason `validate_live_state`
    is: a malformed declaration must be an INVALID-SPEC with KSP never launched.
    Shape only - whether the save actually carries a `Funding` SCENARIO is the
    applier's assertion against the bytes, and a career key declared on a SANDBOX
    fixture is exactly the mistake that must abort rather than no-op."""
    errs: List[str] = []
    if not isinstance(fixture, dict):
        return errs
    if CAREER_KEY not in fixture:
        return errs
    entry = fixture[CAREER_KEY]
    where = "fixture.%s" % CAREER_KEY
    if not isinstance(entry, dict):
        return ["%s: must be a table ([%s])" % (where, where)]
    if not entry:
        return ["%s: declared but empty; omit the key instead" % where]
    unknown = sorted(k for k in entry if k not in CAREER_KEYS)
    if unknown:
        errs.append("%s: unknown key(s) %s (accepted: %s)"
                    % (where, unknown, list(CAREER_KEYS)))
    if CAREER_FUNDS_KEY in entry:
        funds = entry[CAREER_FUNDS_KEY]
        if not _is_number(funds):
            errs.append("%s.%s: %r must be a number" % (where, CAREER_FUNDS_KEY, funds))
        elif funds < 0:
            # A negative pool is a save KSP never writes, and every token a lane
            # derives from a negative seed would be a token about a state the
            # product has no contract for.
            errs.append("%s.%s: %r must be >= 0" % (where, CAREER_FUNDS_KEY, funds))
    return errs


def declared_career_state(fixture: Any) -> Optional[Dict]:
    """The declared career table, or None when the key is absent. Shape-tolerant:
    called AFTER validation, so it never re-reports."""
    if not isinstance(fixture, dict):
        return None
    entry = fixture.get(CAREER_KEY)
    if not isinstance(entry, dict) or not entry:
        return None
    return entry


def declared_live_state(fixture: Any) -> List[Dict]:
    """The declared entries, or [] when the key is absent. Shape-tolerant: this
    is called AFTER validation, so it never re-reports."""
    if not isinstance(fixture, dict):
        return []
    entries = fixture.get(LIVE_STATE_KEY)
    if not isinstance(entries, list):
        return []
    return [e for e in entries if isinstance(e, dict)]


# ---------------------------------------------------------------------------
# The per-fixture inventory layout.
# ---------------------------------------------------------------------------

# THE CRAFT-AUTHORED INVENTORY LAYOUT, per fixture save name:
# (container index in FILE order, slotIndex, partName).
#
# It exists because a `DOCK_ENDPOINT_INVENTORY` snapshot records a `slotIndex`
# but NOT which `ModuleInventoryPart` the slot belongs to, so restoring one needs
# an external statement of where each part kind lives on that craft. The
# `rover-relay-c-recorded` row was DERIVED FROM THE BYTES OF TWO VESSELS
# INDEPENDENTLY (and corroborated on a third) rather than guessed - the full
# derivation, including why the obvious slot-index-only rule picks the WRONG
# station, is in `harness/tools/build_rover_relay_c_recorded.py`'s
# `CRAFT_AUTHORED_INVENTORY_LAYOUT` comment. THE BUILDER READS THIS TABLE; the
# constant there is an alias, so the derivation comment and the values it
# describes cannot drift apart.
#
# Keyed on the fixture's SAVE NAME (the `saveTemplate` leaf, which `run.py`
# already resolves as `runSaveName`), because that is the only identity both the
# build step and the stage step have in common.
INVENTORY_LAYOUTS: Dict[str, Tuple[Tuple[int, str, str], ...]] = {
    "rover-relay-c-recorded": (
        (0, "0", "evaChute"),
        (1, "0", "evaScienceKit"),
        (1, "1", "DeployedCentralStation"),
    ),
}

# The window snapshot nests STOREDPART three levels deeper than FLIGHTSTATE does
# (WINDOW / DOCK_ENDPOINT_INVENTORY / ITEM / STOREDPART_SNAPSHOT vs
# PART / MODULE / STOREDPARTS), so the lift strips exactly three tabs.
SNAPSHOT_INDENT_STRIP = "\t\t\t"
# The FLIGHTSTATE depth a `ModuleInventoryPart`'s own keys sit at.
MODULE_KEY_INDENT = "\t\t\t\t\t"


# ---------------------------------------------------------------------------
# Save-structure readers (shared with the builder).
# ---------------------------------------------------------------------------


def flightstate_node(lines: List[str]) -> Optional[Tuple[int, int]]:
    return find_node(lines, "FLIGHTSTATE")


def scenario_node_named(lines: List[str], name: str) -> List[Tuple[int, int]]:
    """Every top-level `SCENARIO { name = <name> }` span, in file order.

    A LIST rather than the first hit, because the career applier refuses a save
    carrying two `Funding` nodes instead of picking one - a duplicate is a save
    nobody should be patching blind."""
    out: List[Tuple[int, int]] = []
    i = 0
    while True:
        node = find_node(lines, "SCENARIO", i)
        if node is None:
            return out
        if get_value(lines, node, "name") == name:
            out.append(node)
        i = node[1]


def parsek_scenario_node(lines: List[str]) -> Optional[Tuple[int, int]]:
    i = 0
    while True:
        node = find_node(lines, "SCENARIO", i)
        if node is None:
            return None
        if get_value(lines, node, "name") == "ParsekScenario":
            return node
        i = node[1]


def flightstate_vessels(lines: List[str]) -> List[Tuple[str, str, Tuple[int, int]]]:
    """(name, persistentId, span) per FLIGHTSTATE DIRECT-child VESSEL, in file
    order - which is `activeVessel` index order."""
    fs = flightstate_node(lines)
    if fs is None:
        return []
    out = []
    for node in child_nodes(lines, fs, "VESSEL"):
        out.append((get_value(lines, node, "name"),
                    get_value(lines, node, "persistentId"),
                    node))
    return out


def route_windows(lines: List[str]) -> List[Tuple[int, int]]:
    """Every route-connection WINDOW node in the save, in file order.

    The walk is ParsekScenario -> RECORDING_TREE -> RECORDING ->
    ROUTE_CONNECTION_WINDOWS -> WINDOW, i.e. exactly the traversal
    `build_rover_relay_c_recorded._window_records` performs, so `windowIndex` in
    a spec means the same thing it means in the builder's `REPAIR_TARGETS`.
    `test_savepatch.py` asserts the two agree span-for-span on the committed
    fixture rather than trusting this comment."""
    scn = parsek_scenario_node(lines)
    if scn is None:
        return []
    out: List[Tuple[int, int]] = []
    for tree in child_nodes(lines, scn, "RECORDING_TREE"):
        for rec in child_nodes(lines, tree, "RECORDING"):
            for holder in child_nodes(lines, rec, "ROUTE_CONNECTION_WINDOWS"):
                out.extend(child_nodes(lines, holder, "WINDOW"))
    return out


def inventory_modules(lines: List[str],
                      vessel: Tuple[int, int]) -> List[Tuple[int, int]]:
    """Every `MODULE { name = ModuleInventoryPart }` inside ``vessel``, in FILE
    order. The order IS the container index the layout table keys on."""
    out = []
    for part in child_nodes(lines, vessel, "PART"):
        for module in child_nodes(lines, part, "MODULE"):
            if get_value(lines, module, "name") == "ModuleInventoryPart":
                out.append(module)
    return out


def dock_endpoint_stored_parts(
        lines: List[str], window: Tuple[int, int]) -> List[Tuple[str, str, List[str]]]:
    """(partName, slotIndex, STOREDPART lines) per `DOCK_ENDPOINT_INVENTORY` item.

    The lines are lifted VERBATIM out of the window's own
    `ITEM/STOREDPART_SNAPSHOT/STOREDPART` node and re-indented from the
    snapshot's depth to the FLIGHTSTATE depth (three tabs shallower). Nothing
    else is rewritten: the restored bytes are the RECORDED ones, inner
    `persistentId` included, which is what makes a restore auditable against the
    window it came from."""
    out: List[Tuple[str, str, List[str]]] = []
    for holder in child_nodes(lines, window, "DOCK_ENDPOINT_INVENTORY"):
        for item in child_nodes(lines, holder, "ITEM"):
            for snapshot in child_nodes(lines, item, "STOREDPART_SNAPSHOT"):
                for stored in child_nodes(lines, snapshot, "STOREDPART"):
                    block = []
                    for line in lines[stored[0]:stored[1]]:
                        if line.startswith(SNAPSHOT_INDENT_STRIP):
                            block.append(line[len(SNAPSHOT_INDENT_STRIP):])
                        elif line.strip() == "":
                            block.append(line)
                        else:
                            raise LiveStatePatchError(
                                "a STOREDPART_SNAPSHOT line is shallower than the "
                                "expected %d tabs and cannot be re-indented: %r"
                                % (len(SNAPSHOT_INDENT_STRIP), line))
                    out.append((get_value(lines, stored, "partName"),
                                get_value(lines, stored, "slotIndex"),
                                block))
    return out


def plan_container_entries(
        stored: Sequence[Tuple[str, str, List[str]]],
        layout: Sequence[Tuple[int, str, str]],
        where: str) -> Dict[int, List[Tuple[int, str, List[str]]]]:
    """containerIndex -> [(slotIndex, partName, STOREDPART lines)], slot-ascending.

    ``where`` names the source in every error (a window index for a restore).
    Both all-or-nothing checks below are deliberate: a snapshot the layout cannot
    address unambiguously must FAIL rather than be placed somewhere plausible."""
    by_part: Dict[str, Tuple[str, List[str]]] = {}
    for part_name, slot, block in stored:
        if part_name in by_part:
            raise LiveStatePatchError(
                "%s carries two %s items; the authored-layout table addresses "
                "one slot per part name, so this snapshot cannot be placed"
                % (where, part_name))
        by_part[part_name] = (slot, block)

    placement: Dict[int, List[Tuple[int, str, List[str]]]] = {}
    for container_index, slot, part_name in layout:
        if part_name not in by_part:
            raise LiveStatePatchError(
                "%s has no %s, which the authored layout places at container %d "
                "slot %s" % (where, part_name, container_index, slot))
        want_slot, block = by_part[part_name]
        if want_slot != slot:
            raise LiveStatePatchError(
                "%s's recorded %s sits at slotIndex %s, but the authored layout "
                "places it at %s - re-derive the layout"
                % (where, part_name, want_slot, slot))
        placement.setdefault(container_index, []).append(
            (int(slot), part_name, block))
    for entries in placement.values():
        entries.sort()
    return placement


def rewrite_container(lines: List[str], module: Tuple[int, int],
                      entries: Sequence[Tuple[int, str, List[str]]]) -> List[str]:
    """Replace one `ModuleInventoryPart`'s STOREDPARTS body and `inventory` CSV.

    ``entries`` is (slotIndex, partName, STOREDPART lines) in slot order. An
    EMPTY list produces the shape KSP itself writes for an empty container: an
    `inventory` key that is ABSENT rather than blank, and `STOREDPARTS { }`."""
    out = list(lines)

    holders = child_nodes(out, module, "STOREDPARTS")
    if len(holders) != 1:
        raise LiveStatePatchError(
            "a ModuleInventoryPart carries %d STOREDPARTS node(s), expected 1"
            % len(holders))
    body: List[str] = []
    for _slot, _part, block in entries:
        body.extend(block)
    start, end = holders[0]
    out[start + 2:end - 1] = body

    # The CSV is slot-ascending part names; KSP omits the key entirely when the
    # container is empty (measured on `rover-relay-c-recorded`'s own empty
    # container).
    csv_line = None
    if entries:
        csv_line = "%sinventory = %s" % (
            MODULE_KEY_INDENT, ",".join(part for _slot, part, _b in entries))

    # Re-resolve the module span after the body splice, then rewrite the key.
    module_start = module[0]
    module_end = module[1] + (len(body) - ((end - 1) - (start + 2)))

    # DEPTH-EXACT, and the `not ... + "\t"` half is load-bearing: a lifted
    # STOREDPART carries a whole nested PART whose own MODULEs each write
    # `stagingEnabled = True` at a DEEPER indent, and every one of those lines
    # also startswith the five-tab module indent. A prefix-only test therefore
    # anchors on the LAST of those and splices the `inventory` CSV into the
    # middle of a stored part. Measured: it did exactly that on rover B's empty
    # first container, the one case that takes the insert branch.
    def _at_module_depth(index: int) -> bool:
        return (out[index].startswith(MODULE_KEY_INDENT)
                and not out[index].startswith(MODULE_KEY_INDENT + "\t"))

    existing = None
    anchor = None
    for i in range(module_start, module_end):
        text = out[i].strip()
        if text.startswith("inventory = ") and _at_module_depth(i):
            existing = i
        if text.startswith("stagingEnabled = ") and _at_module_depth(i):
            anchor = i
    if anchor is None:
        raise LiveStatePatchError(
            "a ModuleInventoryPart has no stagingEnabled key to anchor the "
            "inventory CSV against")
    if existing is not None:
        if csv_line is None:
            del out[existing]
        else:
            out[existing] = csv_line
    elif csv_line is not None:
        out.insert(anchor + 1, csv_line)
    return out


# ---------------------------------------------------------------------------
# The applier.
# ---------------------------------------------------------------------------


def format_amount(value: float) -> str:
    """Render a declared TOML amount the way the save writes one.

    An integral value prints WITHOUT a decimal point (`400`, not `400.0`),
    because that is the form KSP writes and the form every `amount =` line in
    the committed fixtures already carries; a fractional one prints with
    `repr`, which is Python's shortest round-tripping form and the closest
    analogue of the C# `"R"` the writer uses."""
    if isinstance(value, int) and not isinstance(value, bool):
        return str(value)
    if float(value).is_integer():
        return str(int(value))
    return repr(float(value))


def _resource_nodes(lines: List[str], vessel: Tuple[int, int],
                    resource: str) -> List[Tuple[int, int]]:
    out = []
    for part in child_nodes(lines, vessel, "PART"):
        for res in child_nodes(lines, part, "RESOURCE"):
            if get_value(lines, res, "name") == resource:
                out.append(res)
    return out


def _set_resource(lines: List[str], vessel: Tuple[int, int], vessel_name: str,
                  resource: str, amount: float) -> Tuple[List[str], str]:
    """Set ``resource`` on ``vessel`` to ``amount``. Returns (lines, note).

    EXACTLY ONE matching RESOURCE node is required, and that is a decision
    rather than a limitation. On a multi-tank vessel "LiquidFuel = 100" has two
    defensible readings - 100 per tank, or 100 across the vessel - and a gate
    that reads the SUMMED stored amount would behave differently under each. A
    lane whose declared number can be read two ways is a lane whose tokens are
    not derivable, so the applier refuses and names the count; a fixture that
    needs the multi-tank form gets an explicit distribution rule at that point,
    authored against the gate that will read it."""
    nodes = _resource_nodes(lines, vessel, resource)
    if len(nodes) != 1:
        raise LiveStatePatchError(
            "liveState: vessel %r carries %d %s RESOURCE node(s), expected "
            "exactly 1 - the patch would have to decide how to split %s across "
            "them" % (vessel_name, len(nodes), resource, format_amount(amount)))
    node = nodes[0]
    raw_max = get_value(lines, node, "maxAmount")
    if raw_max is None:
        raise LiveStatePatchError(
            "liveState: vessel %r's %s RESOURCE has no maxAmount to clamp "
            "against" % (vessel_name, resource))
    try:
        max_amount = float(raw_max)
    except (TypeError, ValueError):
        raise LiveStatePatchError(
            "liveState: vessel %r's %s RESOURCE has an unparseable maxAmount %r"
            % (vessel_name, resource, raw_max))
    # Mirror of the validator's `>= 0` shape check, kept HERE as well because
    # the applier is also reachable without `validate_spec` in front of it (the
    # builder, a direct caller) and a negative `amount =` is a save KSP never
    # writes; both ends of the range are refused by the same function.
    if float(amount) < 0:
        raise LiveStatePatchError(
            "liveState: vessel %r's %s = %s is negative - a spec error, not a "
            "clamp" % (vessel_name, resource, format_amount(amount)))
    if float(amount) > max_amount:
        raise LiveStatePatchError(
            "liveState: vessel %r's %s = %s exceeds maxAmount %s - a spec error, "
            "not a clamp: silently capping it would make every token the lane "
            "derives from that number wrong while the run stayed green"
            % (vessel_name, resource, format_amount(amount), raw_max))
    before = get_value(lines, node, "amount")
    text = format_amount(amount)
    if not set_value(lines, node, "amount", text):
        raise LiveStatePatchError(
            "liveState: vessel %r's %s RESOURCE has no amount to rewrite"
            % (vessel_name, resource))
    return lines, "%s %s->%s" % (resource, before, text)


def _active_vessel_index(lines: List[str]) -> int:
    """The save's `activeVessel` index, or a raise when it cannot be read.

    Not optional: it is the ONLY thing that makes a removal safe to reason
    about, so a save that does not carry it is a save this module refuses to
    remove from rather than one it removes from hopefully."""
    fs = flightstate_node(lines)
    if fs is None:
        raise LiveStatePatchError(
            "liveState: the save carries no FLIGHTSTATE node, so no vessel can "
            "be removed from it")
    raw = get_value(lines, fs, "activeVessel")
    if raw is None:
        raise LiveStatePatchError(
            "liveState: FLIGHTSTATE carries no activeVessel index, so a removal "
            "cannot be proven not to re-point the focus")
    try:
        return int(raw.strip())
    except (AttributeError, ValueError):
        raise LiveStatePatchError(
            "liveState: FLIGHTSTATE's activeVessel is %r, which is not an index"
            % (raw,))


def _remove_vessel(lines: List[str], pid: str) -> Tuple[List[str], str]:
    """Delete the whole `VESSEL` node carrying ``pid``. Returns (lines, note).

    THE ONE REFUSAL, and it is the reason this mode is safe to ship: `activeVessel`
    is a positional index into the FLIGHTSTATE vessel list, so deleting a vessel at
    or before it re-points the focus at a different craft (or at nothing). Every
    token a lane derives is a statement about the scene that boots, so the patch
    refuses and names both indices rather than shipping a save whose focus moved."""
    vessels = flightstate_vessels(lines)
    matches = [(i, name, span) for i, (name, vpid, span) in enumerate(vessels)
               if vpid == pid]
    if len(matches) != 1:
        raise LiveStatePatchError(
            "liveState: expected exactly one FLIGHTSTATE vessel with persistentId "
            "%s to remove, found %d" % (pid, len(matches)))
    index, vessel_name, span = matches[0]
    active = _active_vessel_index(lines)
    if index <= active:
        raise LiveStatePatchError(
            "liveState: refusing to remove vessel %r at FLIGHTSTATE index %d - "
            "activeVessel is %d, and removing a vessel at or before it re-points "
            "the focus at a different craft; a lane needing that must move the "
            "focus explicitly" % (vessel_name, index, active))
    out = list(lines)
    del out[span[0]:span[1]]
    return out, vessel_name


def apply_career_state(text: str, entry: Optional[Dict]) -> Tuple[str, List[str]]:
    """Apply the declared `[fixture.career]` table to a save's text. Pure.

    Returns (patchedText, notes). A None / empty entry returns the text UNCHANGED
    and no notes. Fails closed on a save with no (or more than one) `Funding`
    SCENARIO, which is exactly what a career declaration on a SANDBOX fixture
    would hit: the alternative is a lane whose seed silently stayed the
    template's while its header claims otherwise."""
    if not entry:
        return text, []

    crlf = "\r\n" in text
    lines = text.replace("\r\n", "\n").split("\n")
    notes: List[str] = []

    if CAREER_FUNDS_KEY in entry:
        nodes = scenario_node_named(lines, CAREER_FUNDING_SCENARIO)
        if len(nodes) != 1:
            raise LiveStatePatchError(
                "career: the save carries %d SCENARIO { name = %s } node(s), "
                "expected exactly 1 - a career declaration on a SANDBOX fixture "
                "reads as 0 here and must abort rather than stage a save whose "
                "funds are still the template's"
                % (len(nodes), CAREER_FUNDING_SCENARIO))
        before = get_value(lines, nodes[0], CAREER_FUNDS_KEY)
        if before is None:
            raise LiveStatePatchError(
                "career: SCENARIO { name = %s } carries no `%s` key to rewrite"
                % (CAREER_FUNDING_SCENARIO, CAREER_FUNDS_KEY))
        amount = entry[CAREER_FUNDS_KEY]
        if float(amount) < 0:
            raise LiveStatePatchError(
                "career: funds = %s is negative - a spec error, not a clamp"
                % format_amount(amount))
        rendered = format_amount(amount)
        if not set_value(lines, nodes[0], CAREER_FUNDS_KEY, rendered):
            raise LiveStatePatchError(
                "career: SCENARIO { name = %s }'s `%s` key could not be rewritten"
                % (CAREER_FUNDING_SCENARIO, CAREER_FUNDS_KEY))
        notes.append("funds %s->%s" % (before, rendered))

    return ("\r\n" if crlf else "\n").join(lines), notes


def _apply_inventory(lines: List[str], vessel: Tuple[int, int], vessel_name: str,
                     mode: str, layout: Optional[Sequence[Tuple[int, str, str]]],
                     save_name: str) -> Tuple[List[str], str]:
    kind, window_index = parse_inventory_mode(mode)
    if kind == INVENTORY_KEEP:
        return lines, INVENTORY_KEEP

    modules = inventory_modules(lines, vessel)
    if not modules:
        raise LiveStatePatchError(
            "liveState: vessel %r carries no ModuleInventoryPart, so "
            "inventory=%r cannot be applied" % (vessel_name, mode))

    if kind == INVENTORY_CLEAR:
        # Bottom-up so an earlier container's splice cannot invalidate a later
        # container's span.
        for module in reversed(modules):
            lines = rewrite_container(lines, module, [])
        return lines, "clear (%d container(s))" % len(modules)

    # restore-dock-endpoint:<N>
    if layout is None:
        raise LiveStatePatchError(
            "liveState: inventory=%r needs a craft-authored inventory layout and "
            "save %r has no row in savepatch.INVENTORY_LAYOUTS (known: %s)"
            % (mode, save_name, sorted(INVENTORY_LAYOUTS)))
    windows = route_windows(lines)
    if window_index >= len(windows):
        raise LiveStatePatchError(
            "liveState: inventory=%r names window %d but the save carries %d "
            "route-connection window(s)" % (mode, window_index, len(windows)))
    stored = dock_endpoint_stored_parts(lines, windows[window_index])
    placement = plan_container_entries(
        stored, layout, "window %d's DOCK_ENDPOINT_INVENTORY" % window_index)
    if len(placement) > len(modules):
        raise LiveStatePatchError(
            "liveState: vessel %r has %d inventory container(s) but the layout "
            "addresses %d" % (vessel_name, len(modules), len(placement)))
    for container_index, module in reversed(list(enumerate(modules))):
        lines = rewrite_container(lines, module, placement.get(container_index, []))
    return lines, "restore-dock-endpoint:%d (%d stored part(s))" % (
        window_index, len(stored))


def apply_live_state(text: str, entries: Sequence[Dict],
                     save_name: str = "") -> Tuple[str, List[str]]:
    """Apply every declared liveState entry to a save's text. Pure.

    Returns (patchedText, notes) where each note is the one-line
    `pid=... resources=... inventory=...` summary `run.py` logs. An empty
    ``entries`` returns the text UNCHANGED and no notes, so a spec that declares
    nothing is byte-identical to one authored before this mechanism existed.

    Entries are applied one at a time and each RE-RESOLVES the FLIGHTSTATE span
    from scratch, so a container splice under one vessel cannot corrupt the next
    vessel's addressing regardless of file order."""
    if not entries:
        return text, []

    crlf = "\r\n" in text
    lines = text.replace("\r\n", "\n").split("\n")
    layout = INVENTORY_LAYOUTS.get(save_name)
    notes: List[str] = []

    for entry in entries:
        pid = str(entry.get("pid"))
        matches = [(name, span) for name, vpid, span in flightstate_vessels(lines)
                   if vpid == pid]
        if len(matches) != 1:
            raise LiveStatePatchError(
                "liveState: expected exactly one FLIGHTSTATE vessel with "
                "persistentId %s, found %d" % (pid, len(matches)))
        vessel_name, span = matches[0]

        if entry.get(REMOVE_KEY):
            # Whole-node deletion, so nothing below applies: the validator
            # already refuses `remove` alongside `resources` / `inventory`, and
            # the next entry re-resolves its own span from the shortened text.
            lines, removed_name = _remove_vessel(lines, pid)
            notes.append("pid=%s name=%s removed=1" % (pid, removed_name))
            continue

        resource_notes: List[str] = []
        # Sorted so the note (and therefore the harness log) is deterministic
        # regardless of TOML table order.
        for resource in sorted(entry.get("resources") or {}):
            amount = entry["resources"][resource]
            # Re-resolve the vessel span before each write: a resource rewrite
            # is in-place (no line count change), but re-resolving costs nothing
            # and removes the invariant from the reader's head.
            span = [s for n, vpid, s in flightstate_vessels(lines) if vpid == pid][0]
            lines, note = _set_resource(lines, span, vessel_name, resource, amount)
            resource_notes.append(note)

        mode = entry.get("inventory", INVENTORY_KEEP)
        span = [s for n, vpid, s in flightstate_vessels(lines) if vpid == pid][0]
        lines, inv_note = _apply_inventory(lines, span, vessel_name, mode,
                                           layout, save_name)

        notes.append("pid=%s name=%s resources=[%s] inventory=%s"
                     % (pid, vessel_name,
                        ",".join(resource_notes) if resource_notes else "-",
                        inv_note))

    return ("\r\n" if crlf else "\n").join(lines), notes
