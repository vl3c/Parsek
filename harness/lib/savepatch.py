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
import re as _re
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

ENTRY_KEYS = ("pid", "resources", "inventory", "remove", "fill")

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
# recorded coordinates. On `rover-route-recorded` ONE removal does NOT miss: the
# fallback finds `A` 1.03 m from the recorded dock point and (since the
# 2026-09-04 operator ruling) TRANSFERS the route's persisted stop onto it -
# RVR-18.
#
# MORE THAN ONE ENTRY MAY REMOVE, and that is how the miss is authored. RVR-19
# declares TWO `remove = true` entries to delete every surface vessel except the
# route's own TRANSPORT, which the ruling forbids transferring onto, so the
# proximity step finally refuses and the cycle holds `EndpointLost`. Nothing
# special is needed for the second entry: entries apply in file order and each
# re-resolves its own span from the already-shortened text, and the
# `activeVessel` refusal below is re-checked per entry against the index the save
# still carries (removals strictly AFTER it never move it, in any order).
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

# WHY THERE IS STILL NO `restore-undock-endpoint:<N>`, and it is a property of
# the bytes rather than a scope call: `UNDOCK_ENDPOINT_INVENTORY` IS NOT A CENSUS
# OF THE RESULTING INVENTORY. Measured on `rover-relay-c-recorded`, window 1's
# holder carries FOUR items - `DeployedCentralStation` slot 1,
# `DeployedCentralStation` slot 1, `evaChute` slot 0, `evaScienceKit` slot 2 -
# against a rover A that held SIX stored parts live at harvest. Two of those
# items are the SAME part name at the SAME `slotIndex`, and the snapshot records
# no container index at all, so there is no rule inside these bytes that assigns
# them to containers. The builder needed rover A's LIVE `persistentId`s to tell
# the original station from the delivered one; a restore mode has, by
# construction, no live vessel to read.


# ---------------------------------------------------------------------------
# The `fill` key: author a FULL destination inventory from recorded bytes.
# ---------------------------------------------------------------------------

# `fill` - a SEPARATE key from `inventory`, composable with it:
#
#     [[fixture.liveState]]
#     pid  = 4280917262
#     fill = { part = "evaChute", slots = 3 }
#
# WHAT IT DOES, and the placement rule is the whole of it: CLONE a `STOREDPART`
# node ALREADY PRESENT IN THIS SAVE for that part name into EVERY FREE SLOT of
# EVERY `ModuleInventoryPart` on the vessel - containers in FILE order, slots
# ASCENDING inside each container. Nothing about the cloned node is authored
# except its ADDRESS: `slotIndex` becomes the target slot and the nested `PART`'s
# `persistentId` is re-stamped to a value no line in the save already carries.
# Every other byte - `quantity`, `stackCapacity`, `variantName`, `cid`, the whole
# nested PART with its MODULE / ACTIONS bodies - is the recorded one.
#
# WHY THIS SHAPE AND NOT THE TWO ALTERNATIVES, because the module used to say a
# fill mode could not exist and that claim has to be answered rather than
# deleted:
#
#   * `fill-from-dock-endpoint:<N>` (restore window N's items into the first free
#     slots) WOULD BE A SILENT NO-OP HERE, which is the worst outcome this module
#     has. The dock-endpoint restore already places by the craft-authored layout
#     table, and that table names EXACTLY the slots the endpoint already occupies
#     (`rover-relay-c-recorded`: chute c0s0, kit c1s0, station c1s1). Placing the
#     same three items "in the first free slots" instead would be the very
#     container-assignment guess the paragraph above refuses, and it still could
#     not fill: three recorded items cannot fill three free slots AND stay at the
#     addresses the layout derives them at.
#   * A BARE `fill = "<partName>"` STRING CANNOT EXPRESS "EVERY FREE SLOT",
#     because the per-container slot COUNT is not in the save. Measured: the
#     string `InventorySlots` appears ZERO times in `persistent.sfs`; it is a
#     PART-CONFIG property of `ConformalStorageUnit` and the fixture ships no
#     readable craft file either (its `.craft` sidecars are compressed `PSN0`
#     blobs). Inferring the capacity from the highest observed `slotIndex` would
#     read a FULL container as capacity-1 and fill nothing at all - a declaration
#     that reads as "fill it" and patches nothing, the exact failure mode the
#     fail-closed rule above exists for. So the capacity is DECLARED, in the
#     spec, beside the tokens that depend on it, and the applier CROSS-CHECKS it
#     against the bytes: any existing `slotIndex >= slots` is refused as a wrong
#     declaration rather than worked around.
#
# THE BYTES ARE RECORDED; ONLY THE PLACEMENT IS AUTHORED. That is the whole
# difference from the "a fill mode would be invented bytes" position this comment
# replaces. A clone is a byte-for-byte copy of a `STOREDPART` this save already
# carries, found in a stated order (see `find_stored_part_template`): the
# vessel's OWN containers first, then any other FLIGHTSTATE vessel's, then any
# route window's `DOCK_ENDPOINT_INVENTORY` snapshot. No template anywhere in the
# save is a REFUSAL, pre-boot, naming the part and the three places searched.
#
# WHY `persistentId` IS THE ONE FIELD RE-STAMPED, measured rather than assumed:
# inside FLIGHTSTATE all 72 `persistentId` values are DISTINCT (63 vessel parts +
# 9 stored parts), so a verbatim duplicate would break the one uniqueness the key
# exists for. `cid` is deliberately NOT re-stamped - it is shared BY CONSTRUCTION
# across every instance of a part kind (`evaChute` reads `cid = 4294400076` in
# all three rovers), so renumbering it would author a difference the save does
# not have. `uid`, `mid` and `launchID` are `0` on every stored part and are
# likewise left alone. As a guard against a stored part shape this reasoning does
# not cover, the applier REFUSES a template carrying more than one
# `persistentId` line at any depth rather than re-stamping the outermost and
# leaving a nested one to collide.
#
# WHAT IT IS FOR. The DESTINATION-SLOTS-FULL edge on `rover-relay-c-recorded`:
# rover A has two `ConformalStorageUnit` containers of three slots and ships
# three stored parts, so three slots are free and one delivery cycle consumes at
# most two - the shortfall is unreachable by PLAYING this fixture. (It IS
# reachable by playing `rover-route-recorded`, where the destination starts with
# 3 of 6 free and ONE cycle consumes ALL THREE; RVR-16 is that lane, and it is
# why this mode was not built earlier.) RVR-20 is the lane this key exists for.
FILL_KEY = "fill"
FILL_KEYS = ("part", "slots")

# The first candidate `persistentId` for a clone. Allocation walks UP from here
# skipping every value the save already carries and every value this pass has
# already handed out, so it is deterministic (same save + same declaration =
# same ids) without being random. The base is deliberately outside the range
# KSP's own generator produces in practice and inside `uint32`, so a clone's id
# is recognisable as authored when reading a staged save by hand.
FILL_CLONE_PERSISTENT_ID_BASE = 900000001


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
        if FILL_KEY in entry:
            errs.extend(_validate_fill(entry[FILL_KEY], where))
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
            elif (("resources" in entry) or ("inventory" in entry)
                    or (FILL_KEY in entry)):
                errs.append(
                    "%s: `%s = true` cannot be combined with `resources` / "
                    "`inventory` / `%s` - those would patch a VESSEL node this "
                    "entry then deletes, so the declaration reads as two "
                    "different intentions" % (where, REMOVE_KEY, FILL_KEY))
        if (("resources" not in entry) and ("inventory" not in entry)
                and (FILL_KEY not in entry) and (REMOVE_KEY not in entry)):
            errs.append(
                "%s: declares none of `resources`, `inventory`, `%s` or `%s`, so "
                "it patches nothing" % (where, FILL_KEY, REMOVE_KEY))
    return errs


def _validate_fill(fill: Any, where: str) -> List[str]:
    """Shape checks for one `fill = { part = ..., slots = N }` table.

    BOTH keys are required and neither has a default. `part` has none because a
    fill with no part name is not a partial declaration but a meaningless one;
    `slots` has none because the per-container capacity is NOT readable from the
    save (see the `fill` comment above), so any default would be this module
    guessing the one number it cannot check cheaply. Whether the part exists,
    whether the vessel has containers and whether the declared capacity agrees
    with the bytes are the APPLIER's assertions - the same split every other
    mode here uses, and both halves run pre-boot."""
    errs: List[str] = []
    if not isinstance(fill, dict):
        return ["%s.%s: %r must be a table ({ part = \"<partName>\", slots = N })"
                % (where, FILL_KEY, fill)]
    unknown = sorted(k for k in fill if k not in FILL_KEYS)
    if unknown:
        errs.append("%s.%s: unknown key(s) %s (accepted: %s)"
                    % (where, FILL_KEY, unknown, list(FILL_KEYS)))
    part = fill.get("part")
    if not isinstance(part, str) or not part.strip():
        errs.append("%s.%s.part: %r must be a non-empty STOREDPART partName"
                    % (where, FILL_KEY, part))
    slots = fill.get("slots")
    if not isinstance(slots, int) or isinstance(slots, bool) or slots <= 0:
        errs.append(
            "%s.%s.slots: %r must be a positive integer - the per-container slot "
            "capacity of the vessel's ModuleInventoryPart(s), which the save does "
            "not carry (`InventorySlots` is a part-config property)"
            % (where, FILL_KEY, slots))
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
# The `fill` applier's readers and the clone stamp.
# ---------------------------------------------------------------------------

# A `persistentId = <digits>` line at any depth. Used BOTH to census the values
# a save already carries and to count the ids inside a candidate template.
_PERSISTENT_ID_LINE = _re.compile(r"^\t*persistentId = ([0-9]+)\s*$")


def container_entries(lines: List[str],
                      module: Tuple[int, int]) -> List[Tuple[int, str, List[str]]]:
    """(slotIndex, partName, STOREDPART lines) per stored part in one container.

    IN FILE ORDER, which on a harvested save is NOT slot order - `C`'s first
    container in `rover-relay-c-recorded` is written slot 2 before slot 0 while
    its `inventory` CSV is slot-ascending. Callers that hand the result back to
    `rewrite_container` sort it themselves; the read does not reorder, so a
    caller can tell the two apart."""
    holders = child_nodes(lines, module, "STOREDPARTS")
    if len(holders) != 1:
        raise LiveStatePatchError(
            "a ModuleInventoryPart carries %d STOREDPARTS node(s), expected 1"
            % len(holders))
    out: List[Tuple[int, str, List[str]]] = []
    for stored in child_nodes(lines, holders[0], "STOREDPART"):
        raw = get_value(lines, stored, "slotIndex")
        try:
            slot = int(str(raw).strip())
        except (TypeError, ValueError):
            raise LiveStatePatchError(
                "a STOREDPART carries slotIndex %r, which is not an index" % (raw,))
        out.append((slot, get_value(lines, stored, "partName"),
                    list(lines[stored[0]:stored[1]])))
    return out


def find_stored_part_template(
        lines: List[str], vessel: Tuple[int, int],
        part_name: str) -> Optional[Tuple[str, List[str]]]:
    """(sourceDescription, STOREDPART lines) for the first template found, or None.

    THE ORDER IS THE CONTRACT and it goes from most to least local, so a fill
    clones the vessel's OWN copy of a part whenever it has one:

      1. this vessel's `ModuleInventoryPart` containers, in file order;
      2. any other FLIGHTSTATE vessel's, in file order;
      3. any route window's `DOCK_ENDPOINT_INVENTORY` snapshot, in window order
         (re-indented to FLIGHTSTATE depth by the same lift a restore uses).

    Tier 1 exists because a same-vessel clone is the one whose bytes are already
    known to load on THAT craft; tier 3 exists because a lane may want to fill
    with a part no live vessel still holds, and the recorded snapshots are the
    only other place in the save real `STOREDPART` bytes live."""
    for module in inventory_modules(lines, vessel):
        for slot, name, block in container_entries(lines, module):
            if name == part_name:
                return ("this vessel's own container (slot %d)" % slot, block)
    for vname, _vpid, span in flightstate_vessels(lines):
        if span == vessel:
            continue
        for module in inventory_modules(lines, span):
            for slot, name, block in container_entries(lines, module):
                if name == part_name:
                    return ("FLIGHTSTATE vessel %r (slot %d)" % (vname, slot),
                            block)
    for index, window in enumerate(route_windows(lines)):
        for name, slot, block in dock_endpoint_stored_parts(lines, window):
            if name == part_name:
                return ("window %d's DOCK_ENDPOINT_INVENTORY (slotIndex %s)"
                        % (index, slot), block)
    return None


def save_persistent_ids(lines: Sequence[str]) -> set:
    """Every `persistentId` value the save carries, as strings.

    WHOLE-FILE, not FLIGHTSTATE-only, and deliberately over-broad: the window
    snapshots repeat live ids by design, so a clone that dodged only the live
    ones could still be confused with a snapshot copy by a reader. Dodging every
    value in the file costs nothing and makes a clone's id unambiguous."""
    out = set()
    for line in lines:
        match = _PERSISTENT_ID_LINE.match(line)
        if match:
            out.add(match.group(1))
    return out


def stamp_clone(block: Sequence[str], slot: int,
                persistent_id: Optional[str]) -> List[str]:
    """A copy of ``block`` re-addressed to ``slot``, with a fresh nested pid.

    THE ONLY TWO AUTHORED BYTES. `slotIndex` is the placement; the nested PART's
    `persistentId` is the uniqueness the key exists for. `cid` is left alone on
    purpose - it is shared across every instance of a part kind in this save, so
    renumbering it would author a difference the bytes do not have."""
    out = list(block)
    node = find_node(out, "STOREDPART")
    if node is None or node != (0, len(out)):
        raise LiveStatePatchError(
            "a STOREDPART template does not span its own block, so it cannot be "
            "cloned (found %r for %d line(s))" % (node, len(out)))
    if not set_value(out, node, "slotIndex", str(slot)):
        raise LiveStatePatchError(
            "a STOREDPART template carries no slotIndex to re-address")
    if persistent_id is None:
        return out
    parts = child_nodes(out, node, "PART")
    if len(parts) != 1:
        raise LiveStatePatchError(
            "a STOREDPART template carries %d nested PART node(s), expected 1"
            % len(parts))
    if not set_value(out, parts[0], "persistentId", persistent_id):
        raise LiveStatePatchError(
            "a STOREDPART template's nested PART carries no persistentId to "
            "re-stamp")
    return out


def _template_persistent_id_count(block: Sequence[str]) -> int:
    return sum(1 for line in block if _PERSISTENT_ID_LINE.match(line))


def _next_persistent_id(taken: set, cursor: int) -> Tuple[str, int]:
    while str(cursor) in taken:
        cursor += 1
    return str(cursor), cursor + 1


def require_fill_template(lines: List[str], vessel: Tuple[int, int],
                          vessel_name: str,
                          part_name: str) -> Tuple[str, List[str]]:
    """`find_stored_part_template`, but a miss is a REFUSAL naming where it looked.

    RESOLVED BEFORE THE ENTRY'S `inventory` MODE RUNS, and that ordering is a
    decision rather than an accident: `inventory = "clear"` followed by
    `fill = { part = "evaChute" }` is a coherent declaration - empty the
    containers, then fill them with the part this fixture holds - and the clear
    has just deleted the only `evaChute` in the vessel. So the TEMPLATE is
    resolved against the state the entry INHERITED, while the FREE SLOTS are
    computed against the state the inventory mode LEFT, because that is what a
    fill is a statement about."""
    found = find_stored_part_template(lines, vessel, part_name)
    if found is None:
        raise LiveStatePatchError(
            "liveState: fill part=%r has no template in this save - no STOREDPART "
            "with that partName exists on vessel %r, on any other FLIGHTSTATE "
            "vessel, or in any route window's DOCK_ENDPOINT_INVENTORY. A fill "
            "clones RECORDED bytes and never authors a node."
            % (part_name, vessel_name))
    source, template = found
    ids_in_template = _template_persistent_id_count(template)
    if ids_in_template > 1:
        raise LiveStatePatchError(
            "liveState: fill part=%r's template (%s) carries %d persistentId "
            "lines; only the nested PART's is re-stamped, so a deeper one would "
            "be cloned verbatim and collide" % (part_name, source, ids_in_template))
    return source, template


def plan_fill(lines: List[str], vessel: Tuple[int, int], vessel_name: str,
              part_name: str, slots: int,
              template: Sequence[str]) -> Tuple[List[List[Tuple[int, str, List[str]]]],
                                                List[str]]:
    """(perContainerEntries, addresses) for a fill. Pure.

    Raises rather than returning an empty plan: a `fill` that would place NOTHING
    is a spec error, never a quiet pass. Containers are walked in FILE order and
    free slots ascending inside each, which is the placement rule stated in one
    sentence."""
    modules = inventory_modules(lines, vessel)
    if not modules:
        raise LiveStatePatchError(
            "liveState: vessel %r carries no ModuleInventoryPart, so fill "
            "part=%r cannot be applied" % (vessel_name, part_name))

    ids_in_template = _template_persistent_id_count(template)
    taken = save_persistent_ids(lines)
    cursor = FILL_CLONE_PERSISTENT_ID_BASE
    per_container: List[List[Tuple[int, str, List[str]]]] = []
    addresses: List[str] = []
    for container_index, module in enumerate(modules):
        entries = container_entries(lines, module)
        occupied = set()
        for slot, name, _block in entries:
            if slot < 0 or slot >= slots:
                raise LiveStatePatchError(
                    "liveState: vessel %r container %d holds %s at slotIndex %d, "
                    "but the declaration says slots=%d - the declared capacity is "
                    "wrong, and filling against it would leave real slots free"
                    % (vessel_name, container_index, name, slot, slots))
            if slot in occupied:
                raise LiveStatePatchError(
                    "liveState: vessel %r container %d holds two stored parts at "
                    "slotIndex %d; this save cannot be filled against"
                    % (vessel_name, container_index, slot))
            if not name:
                # A fill is the only mode that re-emits the container's EXISTING
                # entries through `rewrite_container`, so it is the only one whose
                # `inventory` CSV depends on their part names. A nameless stored
                # part would silently render as the string `None` in that CSV.
                raise LiveStatePatchError(
                    "liveState: vessel %r container %d holds a stored part at "
                    "slotIndex %d with no partName, so the rewritten inventory "
                    "CSV cannot name it" % (vessel_name, container_index, slot))
            occupied.add(slot)
        merged = list(entries)
        for slot in range(slots):
            if slot in occupied:
                continue
            new_id = None
            if ids_in_template:
                new_id, cursor = _next_persistent_id(taken, cursor)
                taken.add(new_id)
            merged.append((slot, part_name, stamp_clone(template, slot, new_id)))
            addresses.append("c%ds%d" % (container_index, slot))
        per_container.append(sorted(merged, key=lambda e: e[0]))

    if not addresses:
        raise LiveStatePatchError(
            "liveState: vessel %r has no free slot across %d container(s) of "
            "slots=%d, so fill part=%r would place nothing - a no-op declaration "
            "is a spec error, not a pass"
            % (vessel_name, len(modules), slots, part_name))
    return per_container, addresses


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


def _apply_fill(lines: List[str], vessel: Tuple[int, int], vessel_name: str,
                fill: Dict, template: Sequence[str],
                source: str) -> Tuple[List[str], str]:
    """Clone one recorded STOREDPART into every free slot. Returns (lines, note).

    RUNS AFTER the entry's `inventory` mode by construction (see
    `apply_live_state`), so `inventory = "clear"` + `fill` fills EVERY slot and
    `fill` alone fills only what the fixture left free. The order is fixed rather
    than arbitrary: `clear` and `restore-dock-endpoint` decide what is occupied,
    and a fill is a statement about what is left. The TEMPLATE, by contrast, was
    resolved BEFORE that mode ran - see `require_fill_template`."""
    part_name = str(fill.get("part"))
    slots = int(fill.get("slots"))
    per_container, addresses = plan_fill(
        lines, vessel, vessel_name, part_name, slots, template)
    modules = inventory_modules(lines, vessel)
    # Bottom-up so an earlier container's splice cannot invalidate a later
    # container's span, exactly as `clear` and `restore-dock-endpoint` do.
    for container_index, module in reversed(list(enumerate(modules))):
        lines = rewrite_container(lines, module, per_container[container_index])
    return lines, "%s x%d slots=%d [%s] from %s" % (
        part_name, len(addresses), slots, ",".join(addresses), source)


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

        # The fill's TEMPLATE is resolved HERE, before the inventory mode - a
        # `clear` would otherwise delete the very stored part the fill names.
        # See `require_fill_template`.
        fill = entry.get(FILL_KEY)
        fill_template = fill_source = None
        if fill:
            span = [s for n, vpid, s in flightstate_vessels(lines)
                    if vpid == pid][0]
            fill_source, fill_template = require_fill_template(
                lines, span, vessel_name, str(fill.get("part")))

        mode = entry.get("inventory", INVENTORY_KEEP)
        span = [s for n, vpid, s in flightstate_vessels(lines) if vpid == pid][0]
        lines, inv_note = _apply_inventory(lines, span, vessel_name, mode,
                                           layout, save_name)

        note = ("pid=%s name=%s resources=[%s] inventory=%s"
                % (pid, vessel_name,
                   ",".join(resource_notes) if resource_notes else "-",
                   inv_note))

        # `fill` LAST, and the segment is APPENDED only when the key is declared:
        # every note a spec authored before this key existed must read exactly as
        # it did, so an absent `fill` adds no field rather than a `fill=-` one.
        if fill:
            span = [s for n, vpid, s in flightstate_vessels(lines)
                    if vpid == pid][0]
            lines, fill_note = _apply_fill(lines, span, vessel_name, fill,
                                           fill_template, fill_source)
            note = "%s fill=%s" % (note, fill_note)

        notes.append(note)

    return ("\r\n" if crlf else "\n").join(lines), notes
