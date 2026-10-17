"""Pure resource reads over a Parsek save: the route-connection window manifests and the
recording snapshot sidecars (todo HARNESS-RESOURCE-ORACLE-AND-INVARIANT-RULES, roadmap TC-2).

Two questions, both answered from what is on disk, never from Parsek's log lines:

* What did a vessel hold? ``snapshot_totals`` decodes a ``<id>_vessel.craft`` /
  ``<id>_ghost.craft`` sidecar (text, or ``PSN0`` + 25-byte header + raw deflate, the
  ``SnapshotSidecarCodec`` layout) and sums every PART > RESOURCE.
* Was a transfer conserved? ``window_conservation`` takes one ``ROUTE_CONNECTION_WINDOWS``
  window (the dock-time and undock-time manifests of both sides) and checks, per resource,
  that what one side lost is what the other gained.

The live half is the seam verb ``ReadVesselResources`` (what is in the tanks NOW); a lane pins
the numbers this module derives from its fixture, and ``test_resourceq`` keeps the pins and
the fixture in step. Stdlib only.
"""

import struct
import zlib
from typing import Dict, List, Optional, Sequence, Tuple

# resource -> (amount, maxAmount)
Manifest = Dict[str, Tuple[float, float]]

SNAPSHOT_MAGIC = b"PSN0"
# Magic (4) + format version (int) + schema generation (int) + codec (byte) +
# uncompressed length (int) + compressed length (int) + checksum (uint).
SNAPSHOT_HEADER_BYTES = 4 + 4 + 4 + 1 + 4 + 4 + 4

# The four manifest blocks of one window, as the C# RouteConnectionWindow codec writes them.
WINDOW_BLOCKS: Tuple[str, ...] = (
    "DOCK_TRANSPORT_RESOURCES", "DOCK_ENDPOINT_RESOURCES",
    "UNDOCK_TRANSPORT_RESOURCES", "UNDOCK_ENDPOINT_RESOURCES",
)

# Resources that change with time rather than with a transfer (VesselSnapshotOps'
# ExtractResourceManifest excludes the same two).
NOISE_RESOURCES: Tuple[str, ...] = ("ElectricCharge", "IntakeAir")


def decode_snapshot_sidecar(data: bytes) -> str:
    """The ConfigNode text of one snapshot sidecar: a ``PSN0`` binary payload is
    raw-inflated after its fixed header; anything else is read as UTF-8 text."""
    if data[:4] == SNAPSHOT_MAGIC:
        if len(data) < SNAPSHOT_HEADER_BYTES:
            raise ValueError("snapshot sidecar shorter than its header")
        codec = data[12]
        if codec != 1:
            raise ValueError("unknown snapshot codec %d" % codec)
        (length,) = struct.unpack_from("<i", data, 13)
        text = zlib.decompress(data[SNAPSHOT_HEADER_BYTES:], -15)
        if len(text) != length:
            raise ValueError("snapshot payload length %d, header says %d" % (len(text), length))
        return text.decode("utf-8")
    return data.decode("utf-8-sig")


def _add(into: Manifest, name: str, amount: float, max_amount: float) -> None:
    a, m = into.get(name, (0.0, 0.0))
    into[name] = (a + amount, m + max_amount)


def snapshot_totals(text: str) -> Tuple[int, Manifest]:
    """(part count, per-resource totals) of one vessel snapshot's ConfigNode text: every
    RESOURCE node directly under a PART node."""
    parts = 0
    totals: Manifest = {}
    stack: List[str] = []
    prev = ""
    res: Optional[Dict[str, str]] = None
    for raw in text.splitlines():
        line = raw.strip()
        if line == "{":
            stack.append(prev)
            if prev == "PART":
                parts += 1
            if prev == "RESOURCE" and len(stack) >= 2 and stack[-2] == "PART":
                res = {}
        elif line == "}":
            closed = stack.pop() if stack else ""
            if closed == "RESOURCE" and res is not None:
                _add(totals, res.get("name", ""), float(res.get("amount", 0)),
                     float(res.get("maxAmount", 0)))
                res = None
        elif res is not None and " = " in line and stack and stack[-1] == "RESOURCE":
            key, value = line.split(" = ", 1)
            res[key] = value
        prev = line
    return parts, totals


def read_snapshot_file(path: str) -> Tuple[int, Manifest]:
    """``snapshot_totals`` of one sidecar on disk."""
    with open(path, "rb") as fh:
        return snapshot_totals(decode_snapshot_sidecar(fh.read()))


def read_route_windows(sfs_text: str) -> List[Dict]:
    """Every WINDOW under ``ROUTE_CONNECTION_WINDOWS`` as ``{"windowId": .., "dockUT": ..,
    "undockUT": .., "transferTargetPid": .., <block>: Manifest}``, in file order. A pure
    text scan of the ConfigNode layout (node name on its own line, then braces)."""
    lines = [ln.strip() for ln in sfs_text.splitlines()]
    windows: List[Dict] = []
    try:
        start = lines.index("ROUTE_CONNECTION_WINDOWS")
    except ValueError:
        return windows
    stack: List[str] = []
    prev = ""
    window: Optional[Dict] = None
    block: Optional[str] = None
    res: Optional[Dict[str, str]] = None
    for line in lines[start + 1:]:
        if line == "{":
            stack.append(prev)
            if prev == "WINDOW" and len(stack) == 2:
                window = {}
                windows.append(window)
            elif window is not None and prev in WINDOW_BLOCKS:
                block = prev
                window[block] = {}
            elif block is not None and prev == "RESOURCE":
                res = {}
        elif line == "}":
            closed = stack.pop() if stack else ""
            if closed == "RESOURCE" and res is not None and block is not None:
                _add(window[block], res.get("name", ""), float(res.get("amount", 0)),
                     float(res.get("maxAmount", 0)))
                res = None
            elif closed in WINDOW_BLOCKS:
                block = None
            elif closed == "WINDOW":
                window = None
            if not stack:
                break
        elif " = " in line:
            key, value = line.split(" = ", 1)
            if res is not None:
                res[key] = value
            elif window is not None and block is None and len(stack) == 2:
                window.setdefault(key, value)
        prev = line
    return windows


def manifest_sum(*manifests: Manifest) -> Manifest:
    """The per-resource sum of several manifests."""
    total: Manifest = {}
    for m in manifests:
        for name, (amount, max_amount) in (m or {}).items():
            _add(total, name, amount, max_amount)
    return total


def window_conservation(window: Dict, tolerance: float = 0.01,
                        skip: Sequence[str] = NOISE_RESOURCES) -> List[Dict]:
    """Per resource of one window (sorted), what each side gained between dock and undock
    and whether the transfer conserved it: ``transportDelta + endpointDelta`` within
    ``tolerance``. A transfer only moves fuel between the two sides, so a non-zero sum is
    fuel that appeared or vanished (RCS burned while docked shows as a small loss, which
    the tolerance absorbs)."""
    rows = []
    blocks = [window.get(b) or {} for b in WINDOW_BLOCKS]
    names = sorted(set().union(*[set(b) for b in blocks]) - set(skip))
    dt, de, ut_, ue = blocks
    for name in names:
        transport = ut_.get(name, (0.0, 0.0))[0] - dt.get(name, (0.0, 0.0))[0]
        endpoint = ue.get(name, (0.0, 0.0))[0] - de.get(name, (0.0, 0.0))[0]
        imbalance = transport + endpoint
        rows.append({
            "resource": name,
            "transportDelta": transport,
            "endpointDelta": endpoint,
            "imbalance": imbalance,
            "conserved": abs(imbalance) <= tolerance,
        })
    return rows


def format_expect(totals: Manifest, resources: Sequence[str]) -> str:
    """The ``expect=`` value ``ReadVesselResources`` takes for ``resources`` of ``totals``,
    each amount at the verb's own F4 precision."""
    return ";".join("%s:%.4f" % (name, totals.get(name, (0.0, 0.0))[0]) for name in resources)
