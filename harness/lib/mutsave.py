"""Pure core of the save-perturbation mutation checker (MUTATION-CHECK-PHASE-2, PR 1).

Phase 1 (``mutlib``) proved an armed save-parse window at the FACET level: it
moved the measured number by one and to zero and asked the evaluator's own
window rule. That proves a window is not too wide; it cannot prove the number
is READ. A window whose parser path is dead (a renamed node, a key the writer
stopped emitting, a selector that never matches) measures a constant, and a
constant that sits inside the window passes forever. The ``max = 0`` tripwires
(``ghostChainNodes``, ``tombstones``) are the extreme case: a dead path reads
zero, which is exactly what the window wants.

This module perturbs the SAVE ITSELF. For every window of every ARMED block it
builds edits that should push the measured value across each declared bound
(drop ``RECORDING`` nodes, clone a supersede ``ENTRY``, clear a ``terminalState``,
rename a route's status, rewrite ``pointCount`` values ...), writes the edited
tree back to ConfigNode text, and re-runs the REAL
``saveparse.parse_parsek_scenario`` + ``saveparse.evaluate_save_structure``.
The edit is planned from the save's structure; the verdict comes only from the
real parser and evaluator over the edited bytes, so a selector here that picks
the wrong nodes shows up as a survivor, never as a false kill.

Per window the verdict is:

- ``PROVEN``: at least one bound-crossing edit turned the window's own label red.
- ``VACUOUS``: an edit was constructed for a declared bound and the window did
  not red - either the re-measured value never moved (a dead parser path) or it
  moved without crossing (the edit did not reach what the facet counts).
- ``UNCHECKED``: no bound could be tested (the crossing needs more than
  ``MAX_CROSSING_EDITS`` added nodes, or no edit is constructible); a checker
  limit, not a spec finding.

Each armed block also gets FAULT edits: a torn save and a save without the
ParsekScenario node must red every armed block, and the points / routes blocks'
defined mismatches (``unparsed``, ``codecRejects``) must fire when their trigger
is planted.

Pure: no file I/O, no clock. ``harness/tools/mutation_check.py`` is the shell.
"""

from __future__ import annotations

import copy
from dataclasses import dataclass, field
from typing import Callable, Dict, List, Optional, Sequence, Tuple

import saveparse
from saveparse import SfsNode

VERIFIER = "saveParse"
KIND_DOWN = "save-down"
KIND_UP = "save-up"
KIND_SET = "save-set"
KIND_FAULT = "save-fault"

PROVEN = "PROVEN"
VACUOUS = "VACUOUS"
UNCHECKED = "UNCHECKED"

# An up-crossing clones (max - measured + 1) nodes. Past this a bound is reported
# UNCHECKED (decorative or deliberately loose), never VACUOUS.
MAX_CROSSING_EDITS = 256

FRESH_PREFIX = "mutcheck"
FRESH_BODY = "MutationCheckBody"
FRESH_PID_BASE = 990000000

SCENARIO_NODE = "SCENARIO"
TREE_NODE = "RECORDING_TREE"
REC_NODE = "RECORDING"
BP_NODE = "BRANCH_POINT"


# ---------------------------------------------------------------------------
# ConfigNode text writer + Parsek-surface reduction.
# ---------------------------------------------------------------------------


def write_sfs(root: SfsNode) -> str:
    """Serialize an ``SfsNode`` tree in the shape KSP's ConfigNode writer uses
    (name line, ``{`` line, tab-indented ``key = value`` lines, ``}`` line).
    ``saveparse.parse_sfs`` reads it back to the same tree (values before child
    nodes within one node; the parser does not depend on their interleave)."""
    out: List[str] = []

    def emit(node: SfsNode, depth: int) -> None:
        ind = "\t" * depth
        for k, v in node.values:
            out.append("%s%s = %s" % (ind, k, v))
        for c in node.nodes:
            out.append(ind + c.name)
            out.append(ind + "{")
            emit(c, depth + 1)
            out.append(ind + "}")

    emit(root, 0)
    return "\n".join(out) + "\n"


def find_scenario(root: SfsNode) -> Tuple[Optional[SfsNode], Optional[SfsNode]]:
    """(parent, node) of the first ParsekScenario SCENARIO node, depth-first."""
    stack: List[Tuple[Optional[SfsNode], SfsNode]] = [(None, root)]
    while stack:
        parent, node = stack.pop()
        if node.name == SCENARIO_NODE and node.value("name") == saveparse.PARSEK_SCENARIO_NAME:
            return parent, node
        stack.extend((node, c) for c in reversed(node.nodes))
    return None, None


def reduce_save(root: SfsNode) -> SfsNode:
    """The save cut down to the surface the save-parse facets read: ``GAME`` with
    its values, every ParsekScenario SCENARIO node (whole), and ``FLIGHTSTATE``
    with each ``VESSEL``'s values (parts and modules dropped). A reduced tree is
    used only after ``check_save_lane`` has shown it measures the same facets as
    the full save; otherwise every edit runs over the full tree."""
    out = SfsNode(name="", values=list(root.values))
    for top in root.nodes:
        if top.name != "GAME":
            continue
        game = SfsNode(name="GAME", values=list(top.values))
        for child in top.nodes:
            if (child.name == SCENARIO_NODE
                    and child.value("name") == saveparse.PARSEK_SCENARIO_NAME):
                game.nodes.append(copy.deepcopy(child))
            elif child.name == "FLIGHTSTATE":
                fs = SfsNode(name="FLIGHTSTATE", values=list(child.values))
                for v in child.nodes_named("VESSEL"):
                    fs.nodes.append(SfsNode(name="VESSEL", values=list(v.values)))
                game.nodes.append(fs)
        out.nodes.append(game)
    return out


# ---------------------------------------------------------------------------
# Small tree-edit helpers.
# ---------------------------------------------------------------------------


class _Ctx:
    """Fresh, deterministic identifiers for synthesized / cloned nodes."""

    def __init__(self) -> None:
        self.n = 0

    def fresh(self, what: str = "") -> str:
        self.n += 1
        return "%s%s%04d" % (FRESH_PREFIX, what, self.n)

    def fresh_pid(self) -> str:
        self.n += 1
        return str(FRESH_PID_BASE + self.n)


def _set(node: SfsNode, key: str, val: str) -> None:
    """Set the first ``key`` (the one ConfigNode.GetValue reads) and drop any
    later duplicates, so the edit cannot be shadowed."""
    for i, (k, _v) in enumerate(node.values):
        if k == key:
            node.values[:] = (node.values[:i] + [(key, val)]
                              + [kv for kv in node.values[i + 1:] if kv[0] != key])
            return
    node.values.append((key, val))


def _del(node: SfsNode, key: str) -> None:
    node.values[:] = [kv for kv in node.values if kv[0] != key]


def _int(node: SfsNode, key: str) -> Optional[int]:
    raw = node.value(key)
    if raw is None:
        return None
    try:
        return int(raw.strip())
    except ValueError:
        return None


def _true(node: SfsNode, key: str) -> bool:
    raw = node.value(key)
    return raw is not None and raw.strip().lower() == "true"


def _remove(parent: SfsNode, node: SfsNode) -> None:
    for i, c in enumerate(parent.nodes):
        if c is node:
            del parent.nodes[i]
            return


def _insert_after(parent: SfsNode, anchor: SfsNode, new: SfsNode) -> None:
    for i, c in enumerate(parent.nodes):
        if c is anchor:
            parent.nodes.insert(i + 1, new)
            return
    parent.nodes.append(new)


def _child(parent: SfsNode, name: str) -> SfsNode:
    got = parent.first(name)
    if got is None:
        got = SfsNode(name=name)
        parent.nodes.append(got)
    return got


def _scenario(root: SfsNode) -> Optional[SfsNode]:
    return find_scenario(root)[1]


def _game(root: SfsNode) -> SfsNode:
    return _child(root, "GAME")


def _trees(sc: SfsNode) -> List[SfsNode]:
    return sc.nodes_named(TREE_NODE)


def _is_committed_tree(tree: SfsNode) -> bool:
    return not _true(tree, "isActive") and not _true(tree, "isPending")


def _recordings(sc: SfsNode) -> List[Tuple[SfsNode, SfsNode]]:
    return [(t, r) for t in _trees(sc) for r in t.nodes_named(REC_NODE)]


def _ensure_tree(sc: SfsNode, ctx: _Ctx) -> SfsNode:
    trees = _trees(sc)
    if trees:
        return trees[0]
    tree = SfsNode(name=TREE_NODE, values=[("id", ctx.fresh("tree")), ("rootRecordingId", "")])
    sc.nodes.append(tree)
    return tree


def _synth_recording(sc: SfsNode, ctx: _Ctx) -> Tuple[SfsNode, SfsNode]:
    tree = _ensure_tree(sc, ctx)
    rec = SfsNode(name=REC_NODE, values=[("recordingId", ctx.fresh("rec")),
                                         ("vesselName", "MutationCheck"), ("pointCount", "2")])
    tree.nodes.append(rec)
    return tree, rec


def _clone_recording(sc: SfsNode, ctx: _Ctx) -> Tuple[SfsNode, SfsNode]:
    """A copy of the first RECORDING under a FRESH id (so the id-deduped points
    facets count it), or a synthesized one when the save has none."""
    recs = _recordings(sc)
    if not recs:
        return _synth_recording(sc, ctx)
    tree, rec = recs[0]
    new = copy.deepcopy(rec)
    _set(new, "recordingId", ctx.fresh("rec"))
    _insert_after(tree, rec, new)
    return tree, new


def _flight_vessels(root: SfsNode) -> Tuple[SfsNode, List[SfsNode]]:
    fs = _child(_game(root), "FLIGHTSTATE")
    return fs, fs.nodes_named("VESSEL")


# ---------------------------------------------------------------------------
# Operators: each moves one facet toward a TARGET value by editing the tree.
#
# Signature: op(root, sc, measured, target, ctx) -> detail string, or None when
# the edit cannot be constructed. ``measured`` is the baseline facet value (a
# planning input only); the outcome is re-measured by the real parser.
# ---------------------------------------------------------------------------

Op = Callable[[SfsNode, SfsNode, int, int, _Ctx], Optional[str]]
Selector = Callable[[SfsNode, SfsNode], List[Tuple[SfsNode, SfsNode]]]
Synth = Callable[[SfsNode, SfsNode, _Ctx], Optional[Tuple[SfsNode, SfsNode]]]


def node_count_op(what: str, select: Selector, synth: Synth,
                  on_clone: Optional[Callable[[SfsNode, _Ctx], None]] = None) -> Op:
    """A facet that counts nodes: remove (measured - target) selected nodes, or
    add (target - measured) clones of the first one (synthesized when none)."""

    def op(root: SfsNode, sc: SfsNode, measured: int, target: int, ctx: _Ctx) -> Optional[str]:
        items = select(root, sc)
        if target < measured:
            k = measured - target
            if len(items) < k:
                return None
            for parent, node in items[len(items) - k:]:
                _remove(parent, node)
            return "removed %d %s" % (k, what)
        k = target - measured
        if items:
            parent, proto = items[0]
            added = 0
        else:
            made = synth(root, sc, ctx)
            if made is None:
                return None
            parent, proto = made
            added = 1
        for _ in range(k - added):
            new = copy.deepcopy(proto)
            if on_clone is not None:
                on_clone(new, ctx)
            _insert_after(parent, proto, new)
        return "added %d %s%s" % (k, what, " (synthesized)" if added else "")

    return op


def _staging_op(parent_name: str, child_name: str, synth_values: Callable[[_Ctx], List[Tuple[str, str]]]) -> Op:
    def select(_root: SfsNode, sc: SfsNode) -> List[Tuple[SfsNode, SfsNode]]:
        par = sc.first(parent_name)
        return [(par, c) for c in par.nodes_named(child_name)] if par is not None else []

    def synth(_root: SfsNode, sc: SfsNode, ctx: _Ctx) -> Tuple[SfsNode, SfsNode]:
        par = _child(sc, parent_name)
        node = SfsNode(name=child_name, values=synth_values(ctx))
        par.nodes.append(node)
        return par, node

    return node_count_op("%s/%s" % (parent_name, child_name), select, synth)


def _trees_op(committed_only: bool) -> Op:
    def select(_root: SfsNode, sc: SfsNode) -> List[Tuple[SfsNode, SfsNode]]:
        return [(sc, t) for t in _trees(sc) if not committed_only or _is_committed_tree(t)]

    def synth(_root: SfsNode, sc: SfsNode, ctx: _Ctx) -> Tuple[SfsNode, SfsNode]:
        tree = SfsNode(name=TREE_NODE, values=[("id", ctx.fresh("tree")), ("rootRecordingId", "")])
        sc.nodes.append(tree)
        return sc, tree

    def fresh_tree(node: SfsNode, ctx: _Ctx) -> None:
        _set(node, "id", ctx.fresh("tree"))

    return node_count_op("%sRECORDING_TREE" % ("committed " if committed_only else ""),
                         select, synth, fresh_tree)


def _recordings_op() -> Op:
    def select(_root: SfsNode, sc: SfsNode) -> List[Tuple[SfsNode, SfsNode]]:
        return _recordings(sc)

    def synth(_root: SfsNode, sc: SfsNode, ctx: _Ctx) -> Tuple[SfsNode, SfsNode]:
        return _synth_recording(sc, ctx)

    def fresh_rec(node: SfsNode, ctx: _Ctx) -> None:
        _set(node, "recordingId", ctx.fresh("rec"))

    return node_count_op("RECORDING", select, synth, fresh_rec)


def _ghost_chain_op() -> Op:
    def select(_root: SfsNode, sc: SfsNode) -> List[Tuple[SfsNode, SfsNode]]:
        out: List[Tuple[SfsNode, SfsNode]] = []
        stack = [sc]
        while stack:
            par = stack.pop()
            for c in par.nodes:
                if c.name in saveparse.GHOST_CHAIN_NODE_NAMES:
                    out.append((par, c))
                stack.append(c)
        return out

    def synth(_root: SfsNode, sc: SfsNode, _ctx: _Ctx) -> Tuple[SfsNode, SfsNode]:
        node = SfsNode(name=saveparse.GHOST_CHAIN_NODE_NAMES[0])
        sc.nodes.append(node)
        return sc, node

    return node_count_op("ghost-chain node", select, synth)


def _committed_spawned_pids(sc: SfsNode) -> List[str]:
    out: List[str] = []
    for t in _trees(sc):
        if not _is_committed_tree(t):
            continue
        for r in t.nodes_named(REC_NODE):
            pid = _int(r, "spawnedPid")
            if pid:
                out.append(str(pid))
    return out


def _spawned_vessels_op() -> Op:
    def select(root: SfsNode, sc: SfsNode) -> List[Tuple[SfsNode, SfsNode]]:
        pids = set(_committed_spawned_pids(sc))
        fs, vessels = _flight_vessels(root)
        return [(fs, v) for v in vessels if v.value("persistentId") is not None
                and str(_int(v, "persistentId")) in pids]

    def synth(root: SfsNode, sc: SfsNode, ctx: _Ctx) -> Optional[Tuple[SfsNode, SfsNode]]:
        pids = _committed_spawned_pids(sc)
        if pids:
            pid = pids[0]
        else:
            committed = [r for t in _trees(sc) if _is_committed_tree(t)
                         for r in t.nodes_named(REC_NODE)]
            if committed:
                rec = committed[0]
            else:
                tree = SfsNode(name=TREE_NODE, values=[("id", ctx.fresh("tree")),
                                                       ("rootRecordingId", "")])
                rec = SfsNode(name=REC_NODE, values=[("recordingId", ctx.fresh("rec")),
                                                     ("pointCount", "2")])
                tree.nodes.append(rec)
                sc.nodes.append(tree)
            pid = ctx.fresh_pid()
            _set(rec, "spawnedPid", pid)
        fs, _v = _flight_vessels(root)
        node = SfsNode(name="VESSEL", values=[("name", "MutationCheck"), ("type", "Ship"),
                                              ("persistentId", pid)])
        fs.nodes.append(node)
        return fs, node

    return node_count_op("spawned VESSEL", select, synth)


def _vessel_name_op(name: str) -> Op:
    excluded = saveparse.VESSEL_NAME_CENSUS_EXCLUDED_TYPES

    def select(root: SfsNode, _sc: SfsNode) -> List[Tuple[SfsNode, SfsNode]]:
        fs, vessels = _flight_vessels(root)
        return [(fs, v) for v in vessels
                if (v.value("name") or "") == name and (v.value("type") or "") not in excluded]

    def synth(root: SfsNode, _sc: SfsNode, ctx: _Ctx) -> Tuple[SfsNode, SfsNode]:
        fs, _v = _flight_vessels(root)
        node = SfsNode(name="VESSEL", values=[("name", name), ("type", "Ship"),
                                              ("persistentId", ctx.fresh_pid())])
        fs.nodes.append(node)
        return fs, node

    def fresh_pid(node: SfsNode, ctx: _Ctx) -> None:
        _set(node, "persistentId", ctx.fresh_pid())

    return node_count_op("VESSEL named %r" % name, select, synth, fresh_pid)


def _enum_index(names: Sequence[str], name: str) -> Optional[int]:
    if name in names:
        return list(names).index(name)
    try:
        return int(name)
    except ValueError:
        return None


def _terminal_state_op(name: str) -> Optional[Op]:
    idx = _enum_index(saveparse.TERMINAL_STATE_NAMES, name)
    if idx is None:
        return None

    def op(_root: SfsNode, sc: SfsNode, measured: int, target: int, ctx: _Ctx) -> Optional[str]:
        recs = [r for _t, r in _recordings(sc)]
        mine = [r for r in recs if _int(r, "terminalState") == idx]
        if target < measured:
            k = measured - target
            if len(mine) < k:
                return None
            for r in mine[len(mine) - k:]:
                _del(r, "terminalState")
            return "cleared terminalState=%d on %d RECORDING(s)" % (idx, k)
        k = target - measured
        free = [r for r in recs if r.value("terminalState") is None]
        for r in free[:k]:
            _set(r, "terminalState", str(idx))
        cloned = 0
        while len(free[:k]) + cloned < k:
            _t, new = _clone_recording(sc, ctx)
            _set(new, "terminalState", str(idx))
            cloned += 1
        return "set terminalState=%d on %d RECORDING(s)%s" % (
            idx, k, " (%d cloned)" % cloned if cloned else "")

    return op


def _branch_point_op(name: str) -> Optional[Op]:
    unparsed = name == "unparsed"
    idx = None if unparsed else _enum_index(saveparse.BRANCH_TYPE_NAMES, name)
    if idx is None and not unparsed:
        return None

    def matches(bp: SfsNode) -> bool:
        return _int(bp, "type") is None if unparsed else _int(bp, "type") == idx

    def select(_root: SfsNode, sc: SfsNode) -> List[Tuple[SfsNode, SfsNode]]:
        return [(t, b) for t in _trees(sc) for b in t.nodes_named(BP_NODE) if matches(b)]

    def synth(_root: SfsNode, sc: SfsNode, ctx: _Ctx) -> Tuple[SfsNode, SfsNode]:
        tree = _ensure_tree(sc, ctx)
        bp = SfsNode(name=BP_NODE, values=[("id", ctx.fresh("bp")),
                                           ("type", "garbled" if unparsed else str(idx))])
        tree.nodes.append(bp)
        return tree, bp

    def fresh_bp(node: SfsNode, ctx: _Ctx) -> None:
        _set(node, "id", ctx.fresh("bp"))

    return node_count_op("BRANCH_POINT type=%s" % name, select, synth, fresh_bp)


# --- points (values, deduped by recording id the way the facet dedupes) ------


def _point_groups(sc: SfsNode) -> List[List[SfsNode]]:
    """RECORDING nodes grouped by recordingId, first-seen order (an empty id is
    its own group). The FIRST node's pointCount is the one the facet reads."""
    groups: Dict[str, List[SfsNode]] = {}
    order: List[List[SfsNode]] = []
    for _t, r in _recordings(sc):
        rid = r.value("recordingId") or ""
        if rid and rid in groups:
            groups[rid].append(r)
            continue
        g = [r]
        if rid:
            groups[rid] = g
        order.append(g)
    return order


def _gval(g: List[SfsNode]) -> Optional[int]:
    v = _int(g[0], "pointCount")
    return v if v is not None and v >= 0 else None


def _gset(g: List[SfsNode], v: int) -> None:
    for r in g:
        _set(r, "pointCount", str(v))


def _new_point_group(sc: SfsNode, ctx: _Ctx, v: int) -> None:
    _t, r = _clone_recording(sc, ctx)
    _set(r, "pointCount", str(v))


def _points_op(key: str) -> Op:
    trivial = saveparse.TRIVIAL_POINT_COUNT

    def op(_root: SfsNode, sc: SfsNode, measured: int, target: int, ctx: _Ctx) -> Optional[str]:
        groups = [g for g in _point_groups(sc) if _gval(g) is not None]
        if key == "total":
            if target < measured:
                need = measured - target
                for g in sorted(groups, key=lambda g: -_gval(g)):
                    take = min(need, _gval(g))
                    _gset(g, _gval(g) - take)
                    need -= take
                    if need == 0:
                        break
                return None if need else "lowered pointCount by %d in total" % (measured - target)
            if groups:
                _gset(groups[0], _gval(groups[0]) + target - measured)
            else:
                _new_point_group(sc, ctx, target - measured)
            return "raised pointCount by %d in total" % (target - measured)
        if key == "largest":
            if target < measured:
                hit = [g for g in groups if _gval(g) > target]
                for g in hit:
                    _gset(g, target)
                return "capped %d recording(s) at pointCount=%d" % (len(hit), target) if hit else None
            if groups:
                _gset(groups[0], target)
            else:
                _new_point_group(sc, ctx, target)
            return "set one recording to pointCount=%d" % target
        if key == "smallest":
            if target < measured:
                if not groups:
                    return None
                _gset(groups[0], target)
                return "set one recording to pointCount=%d" % target
            hit = [g for g in groups if _gval(g) < target]
            for g in hit:
                _gset(g, target)
            if not hit:
                if groups:
                    return None
                _new_point_group(sc, ctx, target)
                return "added one recording at pointCount=%d" % target
            return "raised %d recording(s) to pointCount=%d" % (len(hit), target)
        if key == "trivialRecordings":
            triv = [g for g in groups if _gval(g) <= trivial]
            if target < measured:
                k = measured - target
                if len(triv) < k:
                    return None
                for g in triv[:k]:
                    _gset(g, trivial + 1)
                return "raised %d trivial recording(s) to pointCount=%d" % (k, trivial + 1)
            k = target - measured
            rich = [g for g in groups if _gval(g) > trivial]
            for g in rich[:k]:
                _gset(g, trivial)
            cloned = 0
            while len(rich[:k]) + cloned < k:
                _new_point_group(sc, ctx, trivial)
                cloned += 1
            return "made %d recording(s) trivial (pointCount=%d)%s" % (
                k, trivial, " (%d cloned)" % cloned if cloned else "")
        return None

    return op


# --- routes ------------------------------------------------------------------


def _routes(sc: SfsNode, dormant: bool = False) -> List[SfsNode]:
    par = sc.first("DORMANT_ROUTES" if dormant else "ROUTES")
    return par.nodes_named("ROUTE") if par is not None else []


def _synth_route(sc: SfsNode, ctx: _Ctx, dormant: bool = False,
                 with_stop: bool = True) -> Tuple[SfsNode, SfsNode]:
    par = _child(sc, "DORMANT_ROUTES" if dormant else "ROUTES")
    route = SfsNode(name="ROUTE", values=[
        ("id", ctx.fresh("route")), ("name", "MutationCheck"), ("status", "Active"),
        ("completedCycles", "0"), ("skippedCycles", "0")])
    if with_stop:
        route.nodes.append(SfsNode(name="STOP", values=[("connectionKind", "None")],
                                   nodes=[SfsNode(name="ENDPOINT")]))
    par.nodes.append(route)
    return par, route


def _clone_route(sc: SfsNode, ctx: _Ctx) -> SfsNode:
    routes = _routes(sc)
    if not routes:
        return _synth_route(sc, ctx)[1]
    new = copy.deepcopy(routes[0])
    _set(new, "id", ctx.fresh("route"))
    _insert_after(sc.first("ROUTES"), routes[0], new)
    return new


def _route_list_op(dormant: bool) -> Op:
    def select(_root: SfsNode, sc: SfsNode) -> List[Tuple[SfsNode, SfsNode]]:
        par = sc.first("DORMANT_ROUTES" if dormant else "ROUTES")
        return [(par, r) for r in par.nodes_named("ROUTE")] if par is not None else []

    def synth(_root: SfsNode, sc: SfsNode, ctx: _Ctx) -> Tuple[SfsNode, SfsNode]:
        return _synth_route(sc, ctx, dormant)

    return node_count_op("%sROUTE" % ("dormant " if dormant else ""), select, synth)


def _route_child_op(what: str, path: Tuple[str, ...]) -> Op:
    """STOP (path ("STOP",)) or SOURCE_REFS/SOURCE rows under committed routes."""
    def select(_root: SfsNode, sc: SfsNode) -> List[Tuple[SfsNode, SfsNode]]:
        out: List[Tuple[SfsNode, SfsNode]] = []
        for r in _routes(sc):
            par = r
            for name in path[:-1]:
                par = par.first(name)
                if par is None:
                    break
            if par is not None:
                out.extend((par, c) for c in par.nodes_named(path[-1]))
        return out

    def synth(_root: SfsNode, sc: SfsNode, ctx: _Ctx) -> Tuple[SfsNode, SfsNode]:
        routes = _routes(sc)
        route = routes[0] if routes else _synth_route(sc, ctx, with_stop=False)[1]
        par = route
        for name in path[:-1]:
            par = _child(par, name)
        if path[-1] == "STOP":
            node = SfsNode(name="STOP", values=[("connectionKind", "None")],
                           nodes=[SfsNode(name="ENDPOINT")])
        else:
            node = SfsNode(name=path[-1], values=[("recordingId", ctx.fresh("rec")),
                                                  ("treeId", ctx.fresh("tree"))])
        par.nodes.append(node)
        return par, node

    return node_count_op(what, select, synth)


def _route_counter_op(key: str) -> Op:
    def op(_root: SfsNode, sc: SfsNode, measured: int, target: int, ctx: _Ctx) -> Optional[str]:
        routes = [r for r in _routes(sc) if _int(r, key) is not None]
        if target < measured:
            need = measured - target
            for r in sorted(routes, key=lambda r: -_int(r, key)):
                take = min(need, _int(r, key))
                _set(r, key, str(_int(r, key) - take))
                need -= take
                if need == 0:
                    break
            return None if need else "lowered %s by %d" % (key, measured - target)
        if routes:
            _set(routes[0], key, str(_int(routes[0], key) + target - measured))
        else:
            route = _synth_route(sc, ctx)[1]
            _set(route, key, str(target - measured))
        return "raised %s by %d" % (key, target - measured)

    return op


def _relabel_op(what: str, items: Callable[[SfsNode], List[SfsNode]],
                read: Callable[[SfsNode], str], write: Callable[[SfsNode, str], None],
                other: str, name: str, make_new: Callable[[SfsNode, _Ctx], Optional[SfsNode]]) -> Op:
    """A bucketed facet (route status, connection kind, body name): move nodes
    out of bucket ``name`` by relabelling them ``other``, or into it by
    relabelling nodes from other buckets, cloning a new carrier when short."""

    def op(_root: SfsNode, sc: SfsNode, measured: int, target: int, ctx: _Ctx) -> Optional[str]:
        nodes = items(sc)
        mine = [n for n in nodes if read(n) == name]
        if target < measured:
            k = measured - target
            if len(mine) < k:
                return None
            for n in mine[len(mine) - k:]:
                write(n, other)
            return "relabelled %d %s %r -> %r" % (k, what, name, other)
        k = target - measured
        rest = [n for n in nodes if read(n) != name]
        for n in rest[:k]:
            write(n, name)
        made = 0
        while len(rest[:k]) + made < k:
            new = make_new(sc, ctx)
            if new is None:
                return None
            write(new, name)
            made += 1
        return "relabelled %d %s -> %r%s" % (k, what, name, " (%d cloned)" % made if made else "")

    return op


def _stops(sc: SfsNode) -> List[SfsNode]:
    return [s for r in _routes(sc) for s in r.nodes_named("STOP")]


def _new_stop(sc: SfsNode, ctx: _Ctx) -> SfsNode:
    routes = _routes(sc)
    route = routes[0] if routes else _synth_route(sc, ctx, with_stop=False)[1]
    stop = SfsNode(name="STOP", values=[("connectionKind", "None")],
                   nodes=[SfsNode(name="ENDPOINT")])
    route.nodes.append(stop)
    return stop


def _route_status_op(name: str) -> Op:
    names = saveparse.ROUTE_STATUS_NAMES

    def read(r: SfsNode) -> str:
        raw = r.value("status")
        return raw if raw in names else saveparse.ROUTE_STATUS_DEFAULT

    other = "Paused" if name != "Paused" else "Active"
    return _relabel_op("ROUTE status", _routes, read, lambda r, v: _set(r, "status", v),
                       other, name, _clone_route)


def _connection_kind_op(name: str) -> Op:
    other = "Grapple" if name != "Grapple" else "DockingPort"
    return _relabel_op("STOP connectionKind", _stops,
                       lambda s: saveparse.route_connection_kind_name(s.value("connectionKind")),
                       lambda s, v: _set(s, "connectionKind", v), other, name, _new_stop)


def _origin_body_op(name: str) -> Op:
    def read(r: SfsNode) -> str:
        o = r.first("ORIGIN")
        return (o.value("bodyName") or "") if o is not None else ""

    def write(r: SfsNode, v: str) -> None:
        _set(_child(r, "ORIGIN"), "bodyName", v)

    return _relabel_op("ROUTE ORIGIN bodyName", _routes, read, write,
                       FRESH_BODY, name, _clone_route)


def _destination_body_op(name: str) -> Op:
    def read(s: SfsNode) -> str:
        e = s.first("ENDPOINT")
        return (e.value("bodyName") or "") if e is not None else ""

    def write(s: SfsNode, v: str) -> None:
        _set(_child(s, "ENDPOINT"), "bodyName", v)

    return _relabel_op("STOP ENDPOINT bodyName", _stops, read, write,
                       FRESH_BODY, name, _new_stop)


# ---------------------------------------------------------------------------
# Window -> operator.
# ---------------------------------------------------------------------------

_REWIND_OPS: Dict[str, Op] = {
    "supersedeRows": _staging_op("RECORDING_SUPERSEDES", "ENTRY", lambda c: [
        ("relationId", c.fresh("rel")), ("oldRecordingId", c.fresh("rec")),
        ("newRecordingId", c.fresh("rec")), ("ut", "0")]),
    "tombstones": _staging_op("LEDGER_TOMBSTONES", "ENTRY", lambda c: [
        ("tombstoneId", c.fresh("tomb")), ("actionId", c.fresh("act")),
        ("retiringRecordingId", c.fresh("rec")), ("ut", "0")]),
    "rewindPoints": _staging_op("REWIND_POINTS", "POINT", lambda c: [
        ("rewindPointId", c.fresh("rp")), ("branchPointId", c.fresh("bp")),
        ("sessionProvisional", "False"), ("quicksaveFilename", c.fresh("rp") + ".sfs")]),
}


def operator_for(label: str) -> Optional[Op]:
    """The operator for one evaluator window label, or None (not modelled)."""
    parts = label.split(".")
    if parts[0] == "rewind" and len(parts) == 2:
        return _REWIND_OPS.get(parts[1])
    if parts[:2] == ["recordings", "structure"]:
        rest = parts[2:]
        if len(rest) == 1:
            return {
                "trees": _trees_op(False),
                "committedTrees": _trees_op(True),
                "recordings": _recordings_op(),
                "ghostChainNodes": _ghost_chain_op(),
                "spawnedVessels": _spawned_vessels_op(),
            }.get(rest[0])
        if len(rest) >= 2:
            name = ".".join(rest[1:])
            if rest[0] == "terminalStates":
                return _terminal_state_op(name)
            if rest[0] == "branchPoints":
                return _branch_point_op(name)
            if rest[0] == "vesselNames":
                return _vessel_name_op(name)
        return None
    if parts[:2] == ["recordings", "points"] and len(parts) == 3:
        return _points_op(parts[2])
    if parts[0] == "routes":
        rest = parts[1:]
        if len(rest) == 1:
            return {
                "count": _route_list_op(False),
                "dormant": _route_list_op(True),
                "stops": _route_child_op("STOP", ("STOP",)),
                "sourceRefs": _route_child_op("SOURCE", ("SOURCE_REFS", "SOURCE")),
                "completedCycles": _route_counter_op("completedCycles"),
                "skippedCycles": _route_counter_op("skippedCycles"),
            }.get(rest[0])
        if len(rest) >= 2:
            name = ".".join(rest[1:])
            return {
                "statuses": _route_status_op,
                "connectionKinds": _connection_kind_op,
                "originBodies": _origin_body_op,
                "destinationBodies": _destination_body_op,
            }.get(rest[0], lambda _n: None)(name)
    return None


def window_bounds(window) -> Tuple[Optional[int], Optional[int]]:
    """(lo, hi) of a count window as the evaluator reads it; a bare int pins both."""
    if isinstance(window, bool):
        return None, None
    if isinstance(window, int):
        return window, window
    if isinstance(window, dict):
        lo, hi = window.get("min"), window.get("max")
        lo = lo if isinstance(lo, int) and not isinstance(lo, bool) else None
        hi = hi if isinstance(hi, int) and not isinstance(hi, bool) else None
        return lo, hi
    return None, None


def crossing_targets(window, measured: int) -> List[Tuple[str, Optional[int], str]]:
    """(direction, target, why) for each declared bound: just below a lower bound,
    just above an upper one. A target of None is a bound the checker cannot test
    (``min = 0``, or more than ``MAX_CROSSING_EDITS`` additions away)."""
    lo, hi = window_bounds(window)
    out: List[Tuple[str, Optional[int], str]] = []
    if lo is not None:
        out.append(("down", lo - 1 if lo >= 1 else None,
                    "min %d" % lo if lo >= 1 else "min 0 cannot be undercut"))
    if hi is not None:
        far = hi + 1 - measured > MAX_CROSSING_EDITS
        out.append(("up", None if far else hi + 1,
                    "max %d" % hi if not far else
                    "max %d is more than %d additions above %d" % (hi, MAX_CROSSING_EDITS, measured)))
    return out


# ---------------------------------------------------------------------------
# Evaluation of one edited tree.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class EditResult:
    status: str
    armed_mismatches: Tuple[str, ...]
    measured: Dict[str, int]
    sets: Dict[str, Tuple[str, ...]]


def _measure(expectations: Dict, snapshot) -> Tuple[Dict[str, int], Dict[str, Tuple[str, ...]]]:
    import mutlib  # local: mutlib imports this module inside check_lane
    counts = {label: measured for label, _w, measured, _a
              in mutlib.save_windows(expectations, snapshot)}
    sets: Dict[str, Tuple[str, ...]] = {}
    if snapshot is not None and snapshot.parsed and snapshot.scenario_found:
        facets = saveparse.observed_routes_facets(snapshot)
        for key in saveparse.ROUTES_SET_KEYS:
            sets["routes.%s" % key] = tuple(sorted(set(facets.get(key, []))))
    return counts, sets


def evaluate_text(expectations: Dict, text: str) -> EditResult:
    snap = saveparse.parse_parsek_scenario(text)
    res = saveparse.evaluate_save_structure(expectations, snap)
    counts, sets = _measure(expectations, snap)
    return EditResult(res.status, tuple(res.armed_mismatches), counts, sets)


def _label_red(res: EditResult, label: str) -> bool:
    return any(m.startswith(label + " ") for m in res.armed_mismatches)


# ---------------------------------------------------------------------------
# Lane records.
# ---------------------------------------------------------------------------


@dataclass
class GateVerdict:
    """One armed window (or set assertion, or block fault) and what proved it."""
    label: str           # evaluator label, e.g. recordings.structure.trees
    block: str           # rewind | recordings.structure | recordings.points | routes
    window: str          # the declared window as text
    measured: str
    verdict: str         # PROVEN | VACUOUS | UNCHECKED
    reason: str = ""


@dataclass
class SaveCheck:
    mutations: list = field(default_factory=list)       # mutlib.Mutation
    gates: List[GateVerdict] = field(default_factory=list)
    notes: List[str] = field(default_factory=list)


def _block_of(label: str) -> str:
    if label.startswith("recordings.structure"):
        return "recordings.structure"
    if label.startswith("recordings.points"):
        return "recordings.points"
    return label.split(".", 1)[0]


FAULT_PHRASES = {
    "tear": "save unreadable",
    "drop-scenario": "no ParsekScenario node",
    "drop-pointCount": "carry no readable pointCount",
    "strip-stops": "DROPPED by RouteCodec",
    "drop-completedCycles": "completedCycles/skippedCycles",
}


def _fault_edits(armed: Sequence[str]) -> List[Tuple[str, str, Callable[[SfsNode, _Ctx], Optional[str]]]]:
    """(block, fault, edit) for every armed block. ``tear`` is applied to TEXT by
    the caller, so its edit is a marker that returns None-as-text."""
    out: List[Tuple[str, str, Callable[[SfsNode, _Ctx], Optional[str]]]] = []

    def drop_scenario(root: SfsNode, _ctx: _Ctx) -> Optional[str]:
        parent, node = find_scenario(root)
        if parent is None:
            return None
        _remove(parent, node)
        return "removed the ParsekScenario SCENARIO node"

    def drop_point_count(root: SfsNode, ctx: _Ctx) -> Optional[str]:
        sc = _scenario(root)
        groups = _point_groups(sc)
        if not groups:
            _t, r = _synth_recording(sc, ctx)
            groups = [[r]]
        for r in groups[0]:
            _del(r, "pointCount")
        return "removed pointCount from one recording"

    def strip_stops(root: SfsNode, ctx: _Ctx) -> Optional[str]:
        sc = _scenario(root)
        routes = _routes(sc)
        route = routes[0] if routes else _synth_route(sc, ctx, with_stop=False)[1]
        route.nodes[:] = [c for c in route.nodes if c.name != "STOP"]
        return "removed every STOP of one committed route"

    def drop_cycles(root: SfsNode, ctx: _Ctx) -> Optional[str]:
        sc = _scenario(root)
        routes = _routes(sc)
        route = routes[0] if routes else _synth_route(sc, ctx)[1]
        _del(route, "completedCycles")
        return "removed completedCycles from one committed route"

    for block in armed:
        out.append((block, "tear", lambda _r, _c: "truncated the save text to half"))
        out.append((block, "drop-scenario", drop_scenario))
        if block == "recordings.points":
            out.append((block, "drop-pointCount", drop_point_count))
        if block == saveparse.ROUTES_BLOCK:
            out.append((block, "strip-stops", strip_stops))
            out.append((block, "drop-completedCycles", drop_cycles))
    return out


def _set_edits(expectations: Dict) -> List[Tuple[str, Callable[[SfsNode, _Ctx], Optional[str]]]]:
    routes = expectations.get(saveparse.ROUTES_BLOCK)
    if not isinstance(routes, dict) or routes.get(saveparse.GATING_KEY) is not True:
        return []
    out: List[Tuple[str, Callable[[SfsNode, _Ctx], Optional[str]]]] = []
    if isinstance(routes.get("ids"), list):
        def rename_id(root: SfsNode, ctx: _Ctx) -> Optional[str]:
            sc = _scenario(root)
            rs = _routes(sc)
            if rs:
                _set(rs[0], "id", ctx.fresh("route"))
                return "renamed one committed route id"
            _synth_route(sc, ctx)
            return "added a committed route under a fresh id"
        out.append(("routes.ids", rename_id))
    if isinstance(routes.get("destinationVesselPids"), list):
        def rename_pid(root: SfsNode, ctx: _Ctx) -> Optional[str]:
            sc = _scenario(root)
            stops = _stops(sc)
            stop = stops[0] if stops else _new_stop(sc, ctx)
            _set(_child(stop, "ENDPOINT"), "vesselPersistentId", ctx.fresh_pid())
            return "re-pointed one STOP endpoint vesselPersistentId"
        out.append(("routes.destinationVesselPids", rename_pid))
    return out


def mutate_save_structure(expectations: Dict, root: SfsNode) -> SaveCheck:
    """Every save-level edit for the ARMED blocks of ``expectations`` over the
    baseline tree ``root`` (whose evaluation must already be PASS; the caller
    checks). ``root`` is never modified."""
    import mutlib  # local import, see _measure
    check = SaveCheck()
    base_text = write_sfs(root)
    base = evaluate_text(expectations, base_text)
    if base.status != saveparse.STATUS_PASS:
        check.notes.append("the written-back baseline save is not PASS (%s); no edits run"
                           % "; ".join(base.armed_mismatches[:2]))
        return check
    armed = set(saveparse.armed_structure_blocks(expectations))

    def run(edit: Callable[[SfsNode, _Ctx], Optional[str]]) -> Tuple[Optional[str], Optional[EditResult]]:
        tree = copy.deepcopy(root)
        ctx = _Ctx()
        detail = edit(tree, ctx)
        if detail is None:
            return None, None
        return detail, evaluate_text(expectations, write_sfs(tree))

    def record(label: str, window_text: str, kind: str, detail: str, killed: bool,
               triage: str, note: str) -> None:
        check.mutations.append(mutlib.Mutation(
            VERIFIER, "%s %s" % (label, window_text), kind, detail,
            mutlib.KILLED if killed else mutlib.SURVIVED,
            "" if killed else triage, "" if killed else note))

    # 1. Bound-crossing edits, per armed count window.
    for label, window, measured, is_armed in mutlib.save_windows(
            expectations, saveparse.parse_parsek_scenario(base_text)):
        if not is_armed:
            continue
        wtext = mutlib._window_text(window)
        block = _block_of(label)
        op = operator_for(label)
        targets = crossing_targets(window, measured)
        if op is None:
            check.gates.append(GateVerdict(label, block, wtext, str(measured), UNCHECKED,
                                           "no save edit models this facet"))
            continue
        killed_any = False
        tried = 0
        reasons: List[str] = []
        for direction, target, why in targets:
            if target is None:
                reasons.append(why)
                continue
            detail, res = run(lambda t, c, target=target: op(t, _scenario(t), measured, target, c))
            kind = KIND_DOWN if direction == "down" else KIND_UP
            if res is None:
                reasons.append("%s: no edit constructible" % why)
                continue
            tried += 1
            after = res.measured.get(label)
            killed = _label_red(res, label)
            killed_any = killed_any or killed
            if after == measured:
                triage, note = mutlib.TRIAGE, ("dead path: the edit did not move the measured "
                                               "value (%d)" % measured)
            elif after != target:
                triage, note = mutlib.TRIAGE, ("the edit moved the value to %s, not to %d"
                                               % (after, target))
            else:
                triage, note = mutlib.TRIAGE, "the value crossed %s and the evaluator stayed green" % why
            record(label, wtext, kind, "%d -> %d (%s): %s; re-measured %s" % (
                measured, target, why, detail, after), killed, triage, note)
            if not killed:
                reasons.append("%s: %s" % (why, note))
        if killed_any:
            check.gates.append(GateVerdict(label, block, wtext, str(measured), PROVEN))
        elif tried:
            check.gates.append(GateVerdict(label, block, wtext, str(measured), VACUOUS,
                                           "; ".join(reasons)))
        else:
            check.gates.append(GateVerdict(label, block, wtext, str(measured), UNCHECKED,
                                           "; ".join(reasons) or "no bound declared"))

    # 2. Set assertions (routes.ids / routes.destinationVesselPids).
    for label, edit in _set_edits(expectations):
        detail, res = run(edit)
        if res is None:
            check.gates.append(GateVerdict(label, saveparse.ROUTES_BLOCK, "set", "", UNCHECKED,
                                           "no edit constructible"))
            continue
        killed = _label_red(res, label)
        moved = res.sets.get(label) != base.sets.get(label)
        note = ("the set changed and the evaluator stayed green" if moved
                else "dead path: the edit did not change the measured set")
        record(label, "set", KIND_SET, detail, killed, mutlib.TRIAGE, note)
        check.gates.append(GateVerdict(label, saveparse.ROUTES_BLOCK, "set",
                                       ",".join(base.sets.get(label, ())),
                                       PROVEN if killed else VACUOUS, "" if killed else note))

    # 3. Block faults.
    for block, fault, edit in _fault_edits(sorted(armed)):
        if fault == "tear":
            text = base_text[:len(base_text) // 2]
            res = evaluate_text(expectations, text)
            detail = "truncated the save text to half"
        else:
            detail, res = run(edit)
            if res is None:
                continue
        phrase = FAULT_PHRASES[fault]
        killed = any(phrase in m for m in res.armed_mismatches)
        label = "%s:%s" % (block, fault)
        record(label, "fault", KIND_FAULT, detail, killed, mutlib.TRIAGE,
               "the planted fault (%s) did not red the armed block" % phrase)
        check.gates.append(GateVerdict(label, block, "fault", "", PROVEN if killed else VACUOUS,
                                       "" if killed else "expected a '%s' mismatch" % phrase))
    return check


# ---------------------------------------------------------------------------
# Baseline + reduction for one save.
# ---------------------------------------------------------------------------


@dataclass
class SaveBaseline:
    root: Optional[SfsNode]
    reasons: List[str]
    reduced: bool
    note: str = ""


def prepare_baseline(expectations: Dict, save_text: Optional[str]) -> SaveBaseline:
    """Parse the archived save, check the ARMED save-parse blocks pass on it, and
    pick the tree edits run over: the reduced Parsek surface when it measures
    exactly what the full save measures, else the full tree."""
    if save_text is None:
        return SaveBaseline(None, ["no archived save"], False)
    snap = saveparse.parse_parsek_scenario(save_text)
    res = saveparse.evaluate_save_structure(expectations, snap)
    if res.status != saveparse.STATUS_PASS:
        return SaveBaseline(None, ["saveParse: %s" % m for m in res.armed_mismatches]
                            or ["saveParse status %s" % res.status], False)
    parsed = saveparse.parse_sfs(save_text)
    full = parsed.root
    reduced = reduce_save(full)
    rsnap = saveparse.parse_parsek_scenario(write_sfs(reduced))
    if (saveparse.observed_structure_facets(rsnap) == saveparse.observed_structure_facets(snap)
            and saveparse.evaluate_save_structure(expectations, rsnap).status == res.status):
        return SaveBaseline(reduced, [], True)
    return SaveBaseline(full, [], False,
                        "the reduced Parsek surface measured differently; edits ran over the full save")

