"""
collect-logs.py - Gather KSP/Parsek logs, save snapshots, recordings sidecars,
and test results for debugging.

Usage:  python scripts/collect-logs.py [label] [--save NAME] [--skip-validation]
                                      [--skip-recordings]
Output: logs/YYYY-MM-DD_HHMM[_label]/

KSP directory resolution (first match wins):
  1. --ksp-dir argument
  2. KSPDIR environment variable
  3. <repo>/Kerbal Space Program/
  4. <repo>/../Kerbal Space Program/
"""

import argparse
import os
import platform
import shutil
import subprocess
import sys
from datetime import datetime
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parent.parent
SKIP_SAVES = {"scenarios", "training"}


def find_ksp_dir(explicit=None):
    """Resolve KSP installation directory."""
    candidates = []

    if explicit:
        candidates.append(Path(explicit))

    env = os.environ.get("KSPDIR") or os.environ.get("KSPDir")
    if env:
        candidates.append(Path(env))

    candidates.append(REPO_ROOT / "Kerbal Space Program")
    candidates.append(REPO_ROOT.parent / "Kerbal Space Program")

    for c in candidates:
        marker = c / "KSP_x64_Data" / "Managed" / "Assembly-CSharp.dll"
        if marker.is_file():
            return c

    # Fallback to first existing directory
    for c in candidates:
        if c.is_dir():
            return c

    return None


def find_player_log():
    """Find Unity Player.log based on platform."""
    system = platform.system()
    if system == "Windows":
        appdata = os.environ.get("APPDATA", "")
        if appdata:
            return Path(appdata) / ".." / "LocalLow" / "Squad" / "Kerbal Space Program" / "Player.log"
    elif system == "Linux":
        home = Path.home()
        return home / ".config" / "unity3d" / "Squad" / "Kerbal Space Program" / "Player.log"
    elif system == "Darwin":
        home = Path.home()
        return home / "Library" / "Logs" / "Unity" / "Player.log"
    return None


def human_size(nbytes):
    for unit in ("B", "K", "M", "G"):
        if abs(nbytes) < 1024:
            return f"{nbytes:.0f}{unit}" if unit == "B" else f"{nbytes:.1f}{unit}"
        nbytes /= 1024
    return f"{nbytes:.1f}T"


def dir_size(path):
    total = 0
    for f in path.rglob("*"):
        if not f.is_file():
            continue
        try:
            total += f.stat().st_size
        except OSError:
            continue
    return total


def copy_file(src, dst_dir):
    """Copy a single file. Returns True if copied."""
    src = Path(src)
    if not src.is_file():
        return False
    dst_dir = Path(dst_dir)
    dst_dir.mkdir(parents=True, exist_ok=True)
    try:
        shutil.copy2(src, dst_dir / src.name)
        print(f"  {src.name}  ({human_size(src.stat().st_size)})")
        return True
    except (OSError, shutil.Error) as exc:
        print(f"  {src.name}  (copy failed: {exc})")
        return False


def copy_tree(src, dst):
    """Copy directory contents. Returns True if anything was copied."""
    src, dst = Path(src), Path(dst)
    if not src.is_dir():
        return False

    copied_any = False
    copied_files = 0
    copied_bytes = 0

    for root, _dirs, files in os.walk(src):
        root_path = Path(root)
        rel_root = root_path.relative_to(src)

        for name in files:
            src_file = root_path / name
            dst_file = dst / rel_root / name
            dst_file.parent.mkdir(parents=True, exist_ok=True)
            try:
                shutil.copy2(src_file, dst_file)
                copied_any = True
                copied_files += 1
                copied_bytes += src_file.stat().st_size
            except (OSError, shutil.Error) as exc:
                rel_path = src_file.relative_to(src)
                print(f"  {src.name}/  (copy failed: {rel_path} - {exc})")

    if not copied_any:
        return False

    print(f"  {src.name}/  ({human_size(copied_bytes)}, {copied_files} files)")
    return True


def detect_save(ksp_dir):
    """Find save with most recently modified persistent.sfs."""
    saves_dir = ksp_dir / "saves"
    if not saves_dir.is_dir():
        return None

    best_name, best_mtime = None, 0
    for entry in saves_dir.iterdir():
        if not entry.is_dir() or entry.name in SKIP_SAVES:
            continue
        sfs = entry / "persistent.sfs"
        if sfs.is_file():
            mtime = sfs.stat().st_mtime
            if mtime > best_mtime:
                best_mtime = mtime
                best_name = entry.name
    return best_name


def git_state():
    """Capture git branch, commit, status, and diff --stat."""
    lines = []
    try:
        branch = subprocess.run(
            ["git", "branch", "--show-current"],
            capture_output=True, text=True, cwd=REPO_ROOT,
        )
        commit = subprocess.run(
            ["git", "log", "--oneline", "-1"],
            capture_output=True, text=True, cwd=REPO_ROOT,
        )
        status = subprocess.run(
            ["git", "status", "--short"],
            capture_output=True, text=True, cwd=REPO_ROOT,
        )
        diff = subprocess.run(
            ["git", "diff", "--stat"],
            capture_output=True, text=True, cwd=REPO_ROOT,
        )
        lines.append(f"Branch: {branch.stdout.strip() or 'detached'}")
        lines.append(f"Commit: {commit.stdout.strip()}")
        lines.append("")
        lines.append("--- status ---")
        lines.append(status.stdout.strip())
        lines.append("")
        lines.append("--- diff --stat ---")
        lines.append(diff.stdout.strip())
    except FileNotFoundError:
        lines.append("(git not found)")
    return "\n".join(lines)


def run_log_validation(ksp_log_path, out_dir):
    """Run the KSP.log contract validator and save results."""
    test_csproj = REPO_ROOT / "Source" / "Parsek.Tests" / "Parsek.Tests.csproj"
    if not test_csproj.is_file():
        write_log_validation_failure(
            out_dir,
            ksp_log_path,
            "test project not found",
            f"Expected test project at {test_csproj}")
        print("  log-validation.txt  (FAILED)")
        return

    env = os.environ.copy()
    env["PARSEK_LIVE_VALIDATE_REQUIRED"] = "1"
    env["PARSEK_LIVE_KSP_LOG_PATH"] = str(ksp_log_path)

    result = subprocess.run(
        [
            "dotnet", "test", str(test_csproj),
            "--filter", "FullyQualifiedName~LiveKspLogValidationTests.ValidateLatestSession",
            "--no-build", "-v", "minimal",
        ],
        capture_output=True, text=True, env=env, cwd=str(test_csproj.parent),
        timeout=60,
    )

    output_lines = []
    output_lines.append(f"KSP.log validation: {'PASSED' if result.returncode == 0 else 'FAILED'}")
    output_lines.append(f"Log path: {ksp_log_path}")
    output_lines.append("")
    if result.stdout.strip():
        output_lines.append(result.stdout.strip())
    if result.stderr.strip():
        output_lines.append(result.stderr.strip())

    validation_file = out_dir / "log-validation.txt"
    validation_file.write_text("\n".join(output_lines), encoding="utf-8")

    status = "PASSED" if result.returncode == 0 else "FAILED"
    print(f"  log-validation.txt  ({status})")


def write_log_validation_failure(out_dir, ksp_log_path, reason, detail=""):
    """Write a failed validation artifact when validation could not complete."""
    output_lines = [
        "KSP.log validation: FAILED",
        f"Log path: {ksp_log_path}",
        "",
        f"Validation did not complete: {reason}",
    ]
    if detail:
        output_lines.append(str(detail))

    validation_file = out_dir / "log-validation.txt"
    validation_file.write_text("\n".join(output_lines), encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description="Collect KSP/Parsek debug logs.")
    parser.add_argument("label", nargs="?", default="", help="Short label for the folder name")
    parser.add_argument("--save", default=None, help="Save game name (auto-detects most recent if omitted)")
    parser.add_argument("--ksp-dir", default=None, help="Path to KSP installation (default: auto-detect)")
    parser.add_argument("--output-dir", default=None, help="Output base directory (default: <repo>/logs/)")
    parser.add_argument("--skip-validation", action="store_true", help="Skip KSP.log contract validation")
    parser.add_argument("--skip-recordings", action="store_true", help="Skip copying Parsek/Recordings sidecars")
    args = parser.parse_args()

    # Resolve KSP directory
    ksp_dir = find_ksp_dir(args.ksp_dir)
    if ksp_dir is None or not ksp_dir.is_dir():
        print("ERROR: KSP directory not found.", file=sys.stderr)
        print("Set KSPDIR env var or pass --ksp-dir.", file=sys.stderr)
        sys.exit(1)

    # Resolve output directory (default: ../logs/ relative to repo, i.e. sibling of repo)
    logs_dir = Path(args.output_dir) if args.output_dir else REPO_ROOT.parent / "logs"

    # Resolve save
    save_name = args.save or detect_save(ksp_dir)
    if not save_name:
        print("ERROR: No save games found.", file=sys.stderr)
        sys.exit(1)
    save_dir = ksp_dir / "saves" / save_name
    if not save_dir.is_dir():
        print(f"ERROR: Save directory not found: {save_dir}", file=sys.stderr)
        sys.exit(1)

    # Create output directory
    timestamp = datetime.now().strftime("%Y-%m-%d_%H%M")
    folder = f"{timestamp}_{args.label}" if args.label else timestamp
    out_dir = logs_dir / folder
    if out_dir.exists():
        print(f"ERROR: Output directory already exists: {out_dir}", file=sys.stderr)
        sys.exit(1)
    out_dir.mkdir(parents=True)

    print(f"Collecting logs into: {folder}/")
    print(f"KSP: {ksp_dir}")
    print(f"Save game: {save_name}")
    print()

    # KSP logs
    print("KSP logs:")
    copy_file(ksp_dir / "KSP.log", out_dir)
    player_log = find_player_log()
    if player_log:
        copy_file(player_log, out_dir)
    copy_file(ksp_dir / "parsek-test-results.txt", out_dir)
    copy_file(ksp_dir / "Logs" / "ModuleManager" / "ModuleManager.log", out_dir)
    copy_file(ksp_dir / "Logs" / "ModuleManager" / "MMPatch.log", out_dir)
    print()

    # Save games
    print(f"Save snapshot ({save_name}):")
    save_out = out_dir / "saves" / save_name
    copy_file(save_dir / "persistent.sfs", save_out)
    copy_file(save_dir / "quicksave.sfs", save_out)
    loadmetas = list(save_dir.glob("*.loadmeta"))
    if loadmetas:
        copied_loadmetas = 0
        for lm in loadmetas:
            if copy_file(lm, save_out):
                copied_loadmetas += 1
        print(f"  {copied_loadmetas}/{len(loadmetas)} .loadmeta files")
    if args.skip_recordings:
        print("  Parsek/Recordings/  (skipped)")
    else:
        copy_tree(save_dir / "Parsek" / "Recordings", save_out / "Parsek" / "Recordings")
    print()

    # Parsek data
    print("Parsek data:")
    parsek_out = out_dir / "parsek"
    if not args.skip_recordings:
        copy_tree(save_dir / "Parsek" / "Recordings", parsek_out / "Recordings")
    copy_tree(save_dir / "Parsek" / "GameState", parsek_out / "GameState")
    copy_tree(save_dir / "Parsek" / "Saves", parsek_out / "Saves")
    print()

    # Log validation
    ksp_log = ksp_dir / "KSP.log"
    if not args.skip_validation and ksp_log.is_file():
        print("Log validation:")
        try:
            run_log_validation(ksp_log, out_dir)
        except subprocess.TimeoutExpired as exc:
            write_log_validation_failure(
                out_dir,
                ksp_log,
                "timed out after 60s",
                str(exc))
            print("  (timed out after 60s)")
        except Exception as e:
            write_log_validation_failure(
                out_dir,
                ksp_log,
                "unexpected validation error",
                str(e))
            print(f"  (error: {e})")
        print()

    # Git state
    print("Git state:")
    git_file = out_dir / "git-state.txt"
    git_file.write_text(git_state(), encoding="utf-8")
    print("  git-state.txt")
    print()

    # Summary
    total = human_size(dir_size(out_dir))
    print(f"Done. Total size: {total}")
    print(f"  {out_dir}")


if __name__ == "__main__":
    main()
