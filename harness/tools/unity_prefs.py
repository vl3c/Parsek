"""Snapshot / restore the Unity screen prefs every KSP on the PC shares (shell).

The decisions (which values, the snapshot format, the restore plan, what a
start does with a marker left by a dead run) live in harness/lib/unityprefs.py;
this file is the winreg + file I/O around them. Only the holder of the
machine lock calls it: a serial run.py, or the parallel runner around its
whole run. ``run.py --slot`` children never do.

API (also used by tools/parallel_runner.py):

    begin(umbrella_root, log_fn, backend=None) -> outcome
        restore a snapshot a dead run left (crash marker), then snapshot the
        current values into slots.unity_prefs_snapshot_path(umbrella_root).
    end(umbrella_root, log_fn, backend=None) -> outcome
        restore from the snapshot and delete it (call after the last KSP exited).

``log_fn(level, message)`` with level Info / Warn / Verbose. Outcomes are short
strings (``snapshot``, ``restored``, ``no-snapshot``, ``unsupported``,
``failed``...). Neither call ever raises: prefs hygiene must never cost a run.
On a non-Windows host with no backend both return ``unsupported``.

CLI: python harness/tools/unity_prefs.py {begin|end} [--umbrella-root DIR]

ASCII only; stdlib only.
"""

from __future__ import annotations

import argparse
import os
import sys
from datetime import datetime, timezone
from typing import Callable, List, Optional

HERE = os.path.dirname(os.path.abspath(__file__))
LIB_DIR = os.path.join(os.path.dirname(HERE), "lib")
if LIB_DIR not in sys.path:
    sys.path.insert(0, LIB_DIR)

import slots  # noqa: E402
import unityprefs  # noqa: E402

LogFn = Callable[[str, str], None]

OUT_SNAPSHOT = "snapshot"
OUT_RECOVERED = "recovered-then-snapshot"
OUT_RESTORED = "restored"
OUT_NO_SNAPSHOT = "no-snapshot"
OUT_UNSUPPORTED = "unsupported"
OUT_FAILED = "failed"


class WinregBackend:
    """The registry key, through winreg (imported lazily: Windows only)."""

    def __init__(self, key_path: str = unityprefs.PREFS_KEY):
        import winreg  # noqa: F401  (raises ImportError off Windows)
        self._winreg = winreg
        self.key_path = key_path

    def read_values(self) -> List[unityprefs.RegValue]:
        winreg = self._winreg
        out: List[unityprefs.RegValue] = []
        try:
            key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, self.key_path, 0,
                                 winreg.KEY_READ)
        except FileNotFoundError:
            return out
        with key:
            i = 0
            while True:
                try:
                    name, data, reg_type = winreg.EnumValue(key, i)
                except OSError:
                    break
                out.append((name, data, reg_type))
                i += 1
        return out

    def write(self, name: str, data: object, reg_type: int) -> None:
        winreg = self._winreg
        with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, self.key_path, 0,
                                winreg.KEY_SET_VALUE) as key:
            winreg.SetValueEx(key, name, 0, reg_type, data)

    def delete(self, name: str) -> None:
        winreg = self._winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, self.key_path, 0,
                            winreg.KEY_SET_VALUE) as key:
            winreg.DeleteValue(key, name)


def _default_backend(log_fn: LogFn):
    if os.name != "nt":
        return None
    try:
        return WinregBackend()
    except Exception as exc:  # noqa: BLE001 - hygiene never costs a run
        log_fn("Warn", "unity prefs: registry unavailable (%s: %s)" % (type(exc).__name__, exc))
        return None


def _read_snapshot(path: str) -> Optional[List[unityprefs.RegValue]]:
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return unityprefs.decode_snapshot(fh.read())
    except (OSError, unityprefs.SnapshotError):
        return None


def _apply(backend, snapshot: List[unityprefs.RegValue], log_fn: LogFn) -> bool:
    writes, deletes = unityprefs.plan_restore(snapshot, backend.read_values())
    for name, data, reg_type in writes:
        backend.write(name, data, reg_type)
    for name in deletes:
        backend.delete(name)
    log_fn("Info", "unity prefs restored: wrote %d value(s), deleted %d (%s)"
           % (len(writes), len(deletes), unityprefs.describe(snapshot)))
    return True


def _write_text_atomic(path: str, text: str) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = "%s.tmp-%d" % (path, os.getpid())
    with open(tmp, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    os.replace(tmp, path)


def begin(umbrella_root: str, log_fn: LogFn, backend=None) -> str:
    path = slots.unity_prefs_snapshot_path(umbrella_root)
    if backend is None:
        backend = _default_backend(log_fn)
    if backend is None:
        log_fn("Verbose", "unity prefs: snapshot skipped (no Windows registry on this host)")
        return OUT_UNSUPPORTED
    try:
        exists = os.path.exists(path)
        decoded = _read_snapshot(path) if exists else None
        action = unityprefs.start_action(exists, decoded)
        outcome = OUT_SNAPSHOT
        if action == unityprefs.START_RECOVER:
            log_fn("Warn", "unity prefs: a snapshot from a run that did not finish is at %s; "
                           "restoring it before this run's snapshot" % path)
            _apply(backend, decoded or [], log_fn)
            outcome = OUT_RECOVERED
        elif action == unityprefs.START_DISCARD:
            aside = "%s.unreadable-%d" % (path, os.getpid())
            os.replace(path, aside)
            log_fn("Warn", "unity prefs: snapshot %s does not decode; moved aside to %s and "
                           "not applied" % (path, aside))
        current = backend.read_values()
        text = unityprefs.encode_snapshot(
            current, datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"), os.getpid())
        _write_text_atomic(path, text)
        log_fn("Info", "unity prefs snapshot -> %s (%s)"
               % (path, unityprefs.describe(unityprefs.select_tracked(current))))
        return outcome
    except Exception as exc:  # noqa: BLE001 - hygiene never costs a run
        log_fn("Warn", "unity prefs snapshot FAILED (%s: %s)" % (type(exc).__name__, exc))
        return OUT_FAILED


def end(umbrella_root: str, log_fn: LogFn, backend=None) -> str:
    path = slots.unity_prefs_snapshot_path(umbrella_root)
    if not os.path.exists(path):
        log_fn("Verbose", "unity prefs: no snapshot at %s, nothing to restore" % path)
        return OUT_NO_SNAPSHOT
    if backend is None:
        backend = _default_backend(log_fn)
    if backend is None:
        log_fn("Verbose", "unity prefs: restore skipped (no Windows registry on this host)")
        return OUT_UNSUPPORTED
    snapshot = _read_snapshot(path)
    if snapshot is None:
        log_fn("Warn", "unity prefs: snapshot %s does not decode; left in place, not applied"
               % path)
        return OUT_FAILED
    try:
        _apply(backend, snapshot, log_fn)
        os.remove(path)
        return OUT_RESTORED
    except Exception as exc:  # noqa: BLE001 - the marker stays; the next start retries
        log_fn("Warn", "unity prefs restore FAILED (%s: %s); the snapshot stays at %s and the "
                       "next start restores it" % (type(exc).__name__, exc, path))
        return OUT_FAILED


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="Unity screen prefs snapshot / restore")
    parser.add_argument("action", choices=("begin", "end"))
    parser.add_argument("--umbrella-root",
                        default=os.path.abspath(os.path.join(HERE, "..", "..", "..")))
    args = parser.parse_args(argv)

    def log(level: str, message: str) -> None:
        print("[%s][UnityPrefs] %s" % (level, message))

    fn = begin if args.action == "begin" else end
    outcome = fn(os.path.abspath(args.umbrella_root), log)
    print("unity-prefs %s outcome=%s" % (args.action, outcome))
    return 1 if outcome == OUT_FAILED else 0


if __name__ == "__main__":
    sys.exit(main())
