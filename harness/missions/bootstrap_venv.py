"""Mission venv bootstrap (design M-B1 "Dependency manifest" + "The dependency
decision").

Creates the module-scoped, gitignored virtual environment
``harness/missions/.venv`` from ``harness/missions/requirements.txt``, installs
``krpc==0.5.4`` through pip's OWN resolver (NO hand-pinned protobuf), VERIFIES the
install (``import krpc`` + a generated-code smoke that exercises the compiled
protobuf bindings), then writes ``harness/missions/.venv/.venv-stamp.json`` with
the pinned krpc version, the RESOLVED protobuf version (frozen from
``pip freeze``), a pip-freeze hash, and the resolved source (PyPI vs krpc-python
asset). Only on a clean smoke is the stamp written; the first verified bootstrap
then PROMOTES the resolved protobuf version into ``requirements.txt`` as the
committed pin (``--promote``, default on).

PURE / SHELL SPLIT: all decisions -- requirements parse, resolved-version
extraction, freeze hashing, stamp construction, the requirements-promotion edit,
and the stamp-satisfies-requirements self-check -- are the side-effect-free
functions in the first half, unit-tested on the base interpreter with NO venv, no
network, no pip. The second half is the I/O shell (venv create, pip install, the
smoke subprocess, the file writes); it is NOT unit-tested here (network + a real
python), it is the PENDING-OPERATOR live step. ``--dry-run`` prints the plan and
runs every pure decision it can WITHOUT creating a venv or touching the network.

The stamp shape matches what ``hlib.venv_admission`` consumes at pre-launch ADMIT:
requirements are ``dist -> version``, and the stamp's frozen resolved pins live
under ``stamp["pins"]`` (same ``dist -> version`` shape). Only the COMMITTED
requirements are enforced there, so an extra stamp pin (the RESOLVED protobuf
before it is promoted) is tolerated.

stdlib ONLY (this runs on the BASE interpreter to CREATE the venv; it never
imports krpc -- the smoke import runs INSIDE the created venv via a subprocess).
ASCII only; LF line endings.
"""

from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
import subprocess
import sys
from typing import Dict, List, Optional, Tuple

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))            # harness/missions
REQUIREMENTS_PATH = os.path.join(SCRIPT_DIR, "requirements.txt")
VENV_DIR = os.path.join(SCRIPT_DIR, ".venv")
STAMP_PATH = os.path.join(VENV_DIR, ".venv-stamp.json")

VENV_STAMP_SCHEMA = 1
SOURCE_PYPI = "pypi"
SOURCE_KRPC_PYTHON_ASSET = "krpc-python-asset"

# The pinned client (matches the provisioned kRPC 0.5.4 server; pins.toml
# [krpc].tag). protobuf is deliberately NOT pinned here -- pip's resolver picks it
# and the bootstrap freezes the resolved version.
KRPC_PIN = "0.5.4"


# ===========================================================================
# Pure decisions (unit-tested; no venv, no pip, no network, no filesystem).
# ===========================================================================


def parse_requirements(text: str) -> Dict[str, str]:
    """Parse ``requirements.txt`` into a ``dist -> version`` map of the COMMITTED
    pins only. Comment lines (``#``) and blank lines are skipped, so the
    PROVISIONAL protobuf line (a comment until the first verified bootstrap
    promotes it) is NOT returned -- exactly the behavior hlib.venv_admission needs
    (it enforces only committed pins). Only strict ``name==version`` pins are
    recognized; a non-pinned requirement is ignored (there are none in v1)."""
    reqs: Dict[str, str] = {}
    for raw in (text or "").splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        # Strip an inline comment.
        if "#" in line:
            line = line.split("#", 1)[0].strip()
        if "==" not in line:
            continue
        name, _, version = line.partition("==")
        name = name.strip()
        version = version.strip()
        if name and version:
            reqs[_canonical_dist(name)] = version
    return reqs


def _canonical_dist(name: str) -> str:
    """Canonical distribution key (lowercase, ``_``/`.` -> ``-``) so a freeze line
    and a requirement line for the same dist compare equal."""
    return name.strip().lower().replace("_", "-").replace(".", "-")


def resolved_version_from_freeze(freeze_text: str, dist: str) -> Optional[str]:
    """Extract a distribution's resolved version from ``pip freeze`` output
    (``name==version`` lines), or None if the dist is absent. Canonical-name
    matched so ``protobuf`` / ``Protobuf`` / ``proto-buf`` line up."""
    want = _canonical_dist(dist)
    for raw in (freeze_text or "").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "==" not in line:
            continue
        name, _, version = line.partition("==")
        if _canonical_dist(name) == want and version.strip():
            return version.strip()
    return None


def freeze_hash(freeze_text: str) -> str:
    """A stable sha256 hex of the ``pip freeze`` output (order-normalized), so the
    stamp records a fingerprint of the exact resolved dependency set. Lines are
    sorted + lowercased so a cosmetic reorder does not churn the hash."""
    lines = sorted(
        l.strip().lower() for l in (freeze_text or "").splitlines()
        if l.strip() and not l.strip().startswith("#"))
    payload = "\n".join(lines).encode("ascii", "replace")
    return hashlib.sha256(payload).hexdigest()


def build_stamp(
    krpc_version: str, protobuf_version: str, freeze_text: str,
    source: str = SOURCE_PYPI, created_utc: Optional[str] = None,
) -> Dict:
    """Construct the ``.venv-stamp.json`` content (pure). ``pins`` is the
    ``dist -> version`` map hlib.venv_admission reads; it carries the pinned krpc
    AND the resolved protobuf. ``created_utc`` is injectable so a test gets a
    deterministic stamp; production passes None -> now."""
    if created_utc is None:
        created_utc = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    return {
        "schema": VENV_STAMP_SCHEMA,
        "pins": {
            "krpc": str(krpc_version),
            "protobuf": str(protobuf_version),
        },
        "source": source,
        "freezeHash": freeze_hash(freeze_text),
        "createdUtc": created_utc,
    }


def serialize_stamp(stamp: Dict) -> str:
    """Serialize a stamp deterministically (stable key order, ASCII, LF), so a
    re-bootstrap with identical inputs writes a byte-identical stamp (modulo the
    createdUtc timestamp)."""
    return json.dumps(stamp, sort_keys=True, indent=2, ensure_ascii=True).replace("\r\n", "\n") + "\n"


def stamp_satisfies_requirements(stamp: Optional[Dict], requirements: Optional[Dict]) -> bool:
    """Bootstrap-side self-check that a freshly built (or on-disk) stamp COVERS the
    committed requirements: every committed ``dist -> version`` is present in
    ``stamp["pins"]`` with an equal version. This MIRRORS hlib.venv_admission's
    admit condition (the harness is the load-bearing gate at run time); the
    bootstrap uses it to confirm what it just produced before promoting. Extra
    stamp pins not yet in requirements (the RESOLVED protobuf pre-promotion) are
    tolerated, so a pre-promotion stamp still self-checks OK against the (still
    protobuf-less) committed requirements."""
    if not stamp:
        return False
    pins = stamp.get("pins", {}) or {}
    for dist, want in (requirements or {}).items():
        if str(pins.get(dist)) != str(want):
            return False
    return True


def promote_requirements_text(current_text: str, protobuf_version: str) -> str:
    """Promote the RESOLVED protobuf version into ``requirements.txt`` (design:
    "the FIRST verified bootstrap PROMOTES the resolved protobuf version into
    requirements.txt as the committed pin"). Pure text edit:

      - if a real ``protobuf==X`` pin already exists, its version is UPDATED to
        the resolved one (idempotent re-bootstrap);
      - otherwise the PROVISIONAL commented protobuf line is REPLACED with a real
        ``protobuf==<resolved>`` pin (and a short provenance comment);
      - if neither is present, the pin is APPENDED.

    Returns the new file text (LF endings); the caller writes it. Given identical
    inputs the output is stable (idempotent)."""
    pin_line = "protobuf==%s" % protobuf_version
    out_lines: List[str] = []
    replaced = False
    for raw in current_text.splitlines():
        stripped = raw.strip()
        low = stripped.lower()
        is_real_pin = low.startswith("protobuf==") or low.startswith("protobuf ==")
        is_provisional = stripped.startswith("#") and "protobuf==" in low and "provisional" in low
        if is_real_pin and not replaced:
            out_lines.append(pin_line)
            replaced = True
            continue
        if is_provisional and not replaced:
            out_lines.append(
                "# protobuf pin promoted from the first verified .venv-stamp (RESOLVED by pip):")
            out_lines.append(pin_line)
            replaced = True
            continue
        out_lines.append(raw)
    if not replaced:
        if out_lines and out_lines[-1].strip():
            out_lines.append("")
        out_lines.append(pin_line)
    return "\n".join(out_lines).rstrip("\n") + "\n"


def generated_code_smoke_source() -> str:
    """The python the bootstrap runs INSIDE the created venv to prove the resolved
    protobuf is ABI-compatible with the kRPC client's generated code (design: a
    "generated-code smoke ... that exercises the compiled .proto bindings"). It
    imports krpc, reads the client version, and constructs a krpc protobuf message
    type from the compiled bindings; a clean exit (code 0) is the pass signal. Kept
    as a pure string so the shell can hand it to ``python -c`` in the venv and the
    tests can assert on its content without running it."""
    return (
        "import sys\n"
        "import krpc\n"
        "print('krpc', getattr(krpc, '__version__', '?'))\n"
        "# Exercise the compiled protobuf bindings: a krpc ProcedureCall message\n"
        "# round-trips through the generated code iff protobuf is ABI-compatible.\n"
        "from krpc.schema import KRPC_pb2\n"
        "msg = KRPC_pb2.ProcedureCall()\n"
        "msg.service = 'KRPC'\n"
        "msg.procedure = 'GetStatus'\n"
        "data = msg.SerializeToString()\n"
        "rt = KRPC_pb2.ProcedureCall()\n"
        "rt.ParseFromString(data)\n"
        "assert rt.service == 'KRPC' and rt.procedure == 'GetStatus', 'protobuf round-trip mismatch'\n"
        "print('smoke-ok')\n"
        "sys.exit(0)\n"
    )


def plan_bootstrap(venv_dir: str, requirements_path: str, source: str = SOURCE_PYPI) -> List[str]:
    """A human-readable ordered plan of the bootstrap I/O steps (for ``--dry-run``
    and the log). Pure; performs nothing."""
    return [
        "create venv at %s (base interpreter %s)" % (venv_dir, sys.executable),
        "pip install -r %s (krpc==%s; protobuf RESOLVED by pip, NOT hand-pinned)"
        % (requirements_path, KRPC_PIN),
        "smoke: run 'import krpc' + generated-code round-trip inside the venv",
        "pip freeze the venv; extract the RESOLVED protobuf version",
        "write %s (pins krpc + resolved protobuf, freeze hash, source=%s)"
        % (os.path.join(venv_dir, ".venv-stamp.json"), source),
        "promote the resolved protobuf pin into %s (first verified bootstrap)" % (requirements_path,),
    ]


def venv_python_path(venv_dir: str) -> str:
    """The venv's python executable path (Windows Scripts/ vs POSIX bin/)."""
    if os.name == "nt":
        return os.path.join(venv_dir, "Scripts", "python.exe")
    return os.path.join(venv_dir, "bin", "python")


# ===========================================================================
# I/O shell (venv create + pip + smoke + writes). NOT unit-tested here; this is
# the PENDING-OPERATOR live step (network + a real python). Every heavy phase is
# guarded so a --dry-run performs NO side effects.
# ===========================================================================


def _log(level: str, message: str) -> None:
    # Mirror the harness [Provision]/[Harness] tag family; the operator captures
    # stdout. Bootstrap runs before any KSP boot, so there is no result JSON here.
    print("[Bootstrap][%s] %s" % (level, message))


def run_bootstrap(
    venv_dir: str = VENV_DIR,
    requirements_path: str = REQUIREMENTS_PATH,
    dry_run: bool = True,
    promote: bool = True,
    runner=subprocess.run,
) -> int:
    """Create + verify + stamp the mission venv (I/O). Returns a process exit code.

    LIVE (``dry_run=False``): create the venv, ``pip install -r requirements``,
    run the generated-code smoke IN the venv, ``pip freeze``, extract the resolved
    protobuf, write the stamp, and (``promote``) update requirements.txt. Any step
    failing aborts NONzero WITHOUT writing a stamp -- a failed smoke must never
    leave a venv that would falsely admit a flight.

    DRY-RUN (``dry_run=True``): print the plan + run every pure decision it can
    without a venv or network, and exit 0. This is what the tests + the operator use
    to sanity-check the wiring before the one live bootstrap.

    NOTE on defaults: this FUNCTION defaults to ``dry_run=True`` (safe by default
    when called programmatically / from a test), but the CLI runs LIVE by default --
    ``main`` passes ``dry_run=args.dry_run`` and ``--dry-run`` is a store_true flag,
    so a bare ``python bootstrap_venv.py`` provisions for real; pass ``--dry-run`` to
    only print the plan."""
    with open(requirements_path, "r", encoding="ascii") as fh:
        req_text = fh.read()
    requirements = parse_requirements(req_text)
    _log("Info", "requirements committed pins: %s" % (requirements,))
    for step in plan_bootstrap(venv_dir, requirements_path):
        _log("Plan", step)

    if dry_run:
        _log("Info", "dry-run: no venv created, no network, no writes")
        return 0

    # --- LIVE from here (network + real python). Deliberately minimal + loud. ---
    _log("Info", "creating venv at %s" % (venv_dir,))
    runner([sys.executable, "-m", "venv", venv_dir], check=True)
    vpy = venv_python_path(venv_dir)
    _log("Info", "pip install -r %s" % (requirements_path,))
    runner([vpy, "-m", "pip", "install", "--upgrade", "pip"], check=True)
    runner([vpy, "-m", "pip", "install", "-r", requirements_path], check=True)

    _log("Info", "generated-code smoke inside the venv")
    smoke = runner([vpy, "-c", generated_code_smoke_source()],
                   check=False, capture_output=True, text=True)
    if smoke.returncode != 0:
        _log("Error", "smoke FAILED (no stamp written): %s" % (smoke.stderr.strip(),))
        return 2
    _log("Info", "smoke ok: %s" % (smoke.stdout.strip().replace("\n", " | "),))

    freeze = runner([vpy, "-m", "pip", "freeze"], check=True, capture_output=True, text=True)
    freeze_text = freeze.stdout
    protobuf_version = resolved_version_from_freeze(freeze_text, "protobuf")
    if not protobuf_version:
        _log("Error", "protobuf not found in pip freeze (no stamp written)")
        return 3
    krpc_version = resolved_version_from_freeze(freeze_text, "krpc") or KRPC_PIN
    _log("Info", "resolved krpc=%s protobuf=%s" % (krpc_version, protobuf_version))

    stamp = build_stamp(krpc_version, protobuf_version, freeze_text, source=SOURCE_PYPI)
    with open(STAMP_PATH, "w", encoding="ascii", newline="\n") as fh:
        fh.write(serialize_stamp(stamp))
    _log("Info", "wrote stamp %s" % (STAMP_PATH,))

    # Self-check: the stamp must cover the committed requirements (mirrors
    # hlib.venv_admission), else the venv would be refused at ADMIT anyway.
    if not stamp_satisfies_requirements(stamp, requirements):
        _log("Error", "stamp does not satisfy committed requirements; venv would be refused at ADMIT")
        return 4

    if promote:
        new_req = promote_requirements_text(req_text, protobuf_version)
        if new_req != req_text:
            with open(requirements_path, "w", encoding="ascii", newline="\n") as fh:
                fh.write(new_req)
            _log("Info", "promoted protobuf==%s into %s" % (protobuf_version, requirements_path))
        else:
            _log("Info", "requirements already carries protobuf==%s (idempotent)" % (protobuf_version,))
    return 0


def main(argv: Optional[List[str]] = None) -> int:
    p = argparse.ArgumentParser(
        prog="bootstrap_venv",
        description="Bootstrap the M-B1 mission venv (harness/missions/.venv).")
    p.add_argument("--dry-run", action="store_true",
                   help="print the plan + run pure decisions; create NO venv, no network, no writes")
    p.add_argument("--no-promote", action="store_true",
                   help="do NOT promote the resolved protobuf pin into requirements.txt")
    args = p.parse_args(argv)
    return run_bootstrap(dry_run=args.dry_run, promote=not args.no_promote)


if __name__ == "__main__":
    sys.exit(main())
