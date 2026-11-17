#!/usr/bin/env python3
"""Cfg reader shared by the S6 tools (spike branch only, task S6).

The Python twin of Source/Parsek/InGameTests/Spikes/S6/SpikeS6Cfg.cs. Both parse the
text KSP writes (ConfigNode.Save) and build the same canonical form of a node, so a
hash the in-game save watch logs equals the hash these tools compute for the same node.

Rules (KSP's writer): "key = value" splits at the first '='; a bare word names the next
node and "{" opens it ("NAME {" on one line too); "}" closes; "//" starts a comment. An
unbalanced "}" is ignored; nodes left open at the end are closed.

Stdlib only; ASCII only.
"""

import hashlib
import os

MARK_NAME = "ParsekRaceSpikeMark"
PROBE_NAME = "SpikeS6ProbeScenario"
HERE = os.path.dirname(os.path.abspath(__file__))
MARK_FILE = os.path.join(HERE, "template", "mark.cfg")
# Pinned twice on purpose: here and in the C# SpikeS6CfgTests. A change to the mark or to
# the canonical form must move both.
EXPECTED_MARK_HASH = "33f1d41c85676b3f"


class Node(object):
    __slots__ = ("name", "values", "nodes")

    def __init__(self, name):
        self.name = name or ""
        self.values = []  # list of (key, value)
        self.nodes = []

    def get(self, key):
        for k, v in self.values:
            if k == key:
                return v
        return None

    def get_all(self, key):
        return [v for k, v in self.values if k == key]

    def children(self, name):
        return [n for n in self.nodes if n.name == name]


def _strip_comment(line):
    c = line.find("//")
    return line if c < 0 else line[:c]


def parse(text):
    """Parse cfg text into a synthetic root whose children are the top-level nodes."""
    root = Node("<root>")
    stack = [root]
    pending = None
    if text is None:
        return root
    for raw in text.split("\n"):
        line = _strip_comment(raw).strip()
        if not line:
            continue
        i = 0
        while i < len(line):
            positions = [p for p in (line.find("{", i), line.find("}", i)) if p >= 0]
            brace = min(positions) if positions else -1
            segment = (line[i:] if brace < 0 else line[i:brace]).strip()
            if segment:
                eq = segment.find("=")
                if eq >= 0:
                    if pending is not None:
                        stack[-1].values.append((pending, ""))
                        pending = None
                    stack[-1].values.append((segment[:eq].strip(), segment[eq + 1:].strip()))
                else:
                    pending = segment
            if brace < 0:
                break
            if line[brace] == "{":
                node = Node(pending or "")
                pending = None
                stack[-1].nodes.append(node)
                stack.append(node)
            elif len(stack) > 1:
                stack.pop()
            i = brace + 1
    return root


def parse_file(path):
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        return parse(fh.read())


def find_scenarios(root, name):
    """SCENARIO nodes named `name` under the GAME node(s); a root without GAME is searched itself."""
    games = root.children("GAME") or [root]
    found = []
    for g in games:
        for n in g.nodes:
            if n.name == "SCENARIO" and n.get("name") == name:
                found.append(n)
    return found


def scenario_names(root):
    names = []
    for g in root.children("GAME"):
        for s in g.nodes:
            if s.name == "SCENARIO":
                names.append(s.get("name") or "")
    return names


def normalize_value(value):
    """Trimmed, inner runs of spaces and tabs collapsed, no space next to a comma."""
    if not value:
        return ""
    out = []
    space = False
    for c in value.strip():
        if c in (" ", "\t"):
            space = True
            continue
        if space and c != "," and out and out[-1] != ",":
            out.append(" ")
        space = False
        out.append(c)
    return "".join(out)


def canonical(node):
    lines = []

    def walk(n):
        lines.append(n.name + " {")
        for k, v in n.values:
            lines.append(k.strip() + " = " + normalize_value(v))
        for c in n.nodes:
            walk(c)
        lines.append("}")

    walk(node)
    return "\n".join(lines)


def short_hash(text):
    return hashlib.sha256((text or "").encode("utf-8")).hexdigest()[:16]


def mark_facts(text, mark=MARK_NAME, probe=PROBE_NAME):
    """(is_game, mark_count, canonical_or_None, hash_or_'-', probe_count, scenario_count)."""
    root = parse(text)
    marks = find_scenarios(root, mark)
    probes = find_scenarios(root, probe) if probe else []
    canon = canonical(marks[0]) if marks else None
    return (bool(root.children("GAME")), len(marks), canon,
            short_hash(canon) if canon is not None else "-", len(probes), len(scenario_names(root)))


def expected_mark(path=MARK_FILE):
    """(canonical, hash) of the template's mark text."""
    root = parse_file(path)
    marks = find_scenarios(root, MARK_NAME)
    if len(marks) != 1:
        raise ValueError("%s holds %d marks, expected 1" % (path, len(marks)))
    canon = canonical(marks[0])
    return canon, short_hash(canon)


def normalize_number(value):
    if value is None:
        return ""
    t = value.strip()
    try:
        d = float(t)
    except ValueError:
        return t
    # C# double.ToString("R"): integers print without a decimal point.
    if d == int(d) and abs(d) < 1e15:
        return str(int(d))
    return repr(d)


def tech_line(tid, cost, any_to_unlock, parents):
    ps = sorted(p.strip() for p in (parents or []) if p)
    return "%s|%s|%s|%s" % ((tid or "").strip(), normalize_number(cost),
                            "True" if any_to_unlock else "False", ",".join(ps))


def tech_lines_from_cfg(root):
    trees = root.children("TechTree") or [root]
    lines = []
    for tree in trees:
        for rd in tree.children("RDNode"):
            parents = [p.get("parentID") for p in rd.children("Parent")]
            anyu = (rd.get("anyToUnlock") or "").lower() == "true"
            lines.append(tech_line(rd.get("id"), rd.get("cost"), anyu, parents))
    return sorted(lines)
