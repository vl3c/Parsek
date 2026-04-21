"""
validate-release-bundle.py - Validate a release-closeout evidence bundle.

Usage:
  python scripts/validate-release-bundle.py <bundle-dir> [--profile NAME]

The validator checks:
  - required artifacts exist in the bundle
  - log-validation.txt reports `KSP.log validation: PASSED`
  - required runtime rows for the named release profile exist in
    parsek-test-results.txt and every captured row for those tests is `PASSED`

It writes a `release-bundle-validation.txt` report into the bundle directory.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path


REQUIRED_ARTIFACTS = (
    "KSP.log",
    "Player.log",
    "parsek-test-results.txt",
    "log-validation.txt",
)

PROFILE_TESTS = {
    "release-auto-record": (
        "RuntimeTests.AutoRecordOnLaunch_StartsExactlyOnce",
        "RuntimeTests.AutoRecordOnEvaFromPad_StartsExactlyOnce",
    ),
    "release-core-playback": (
        "RuntimeTests.TreeMergeDialog_DiscardButton_ClearsPendingTree",
        "RuntimeTests.TreeMergeDialog_DeferredMergeButton_CommitsPendingTree",
        "RuntimeTests.KeepVessel_FastForwardIntoPlayback_SpawnsExactlyOnce",
    ),
    "release-scene-transitions": (
        "RuntimeTests.RevertToLaunch_SoftUnstashesPendingTree_WithoutMergeDialog",
        "RuntimeTests.ExitToSpaceCenter_DeferredMergeButton_CommitsPendingTree",
        "RuntimeTests.ExitToSpaceCenter_DeferredDiscardButton_ClearsPendingTree",
    ),
}


def infer_profile(bundle_dir: Path) -> str | None:
    bundle_name = bundle_dir.name
    for profile in PROFILE_TESTS:
        if bundle_name.endswith(profile):
            return profile
    return None


def read_text(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def validate_log_validation(bundle_dir: Path, messages: list[str]) -> bool:
    path = bundle_dir / "log-validation.txt"
    if not path.is_file():
        messages.append("Missing artifact: log-validation.txt")
        return False

    content = read_text(path)
    first_line = next((line.strip() for line in content.splitlines() if line.strip()), "")
    success_markers = (
        "KSP.log validation: PASSED",
        "KSP.log validation passed.",
    )

    if not any(marker in content for marker in success_markers):
        messages.append(
            "log-validation.txt does not report a recognized passing validator result "
            f"(first non-empty line: {first_line or '<empty>'})"
        )
        return False

    return True


def parse_results(results_text: str) -> dict[str, list[tuple[str, str]]]:
    rows: dict[str, list[tuple[str, str]]] = {}
    in_results = False
    current_test = None

    scene_row = re.compile(r"^(?P<scene>\S+)\s+(?P<status>PASSED|FAILED|SKIPPED)\b")

    for raw_line in results_text.splitlines():
        line = raw_line.rstrip("\n")
        if line == "ALL RESULTS (one row per scene, per test):":
            in_results = True
            continue

        if not in_results:
            continue

        if line.startswith("  [") and line.endswith("]"):
            continue

        if line.startswith("    ") and not line.startswith("      "):
            current_test = line.strip()
            rows.setdefault(current_test, [])
            continue

        if line.startswith("      ") and current_test is not None:
            entry = line.strip()
            if entry == "(never run)" or entry.endswith("(not run in this scene)"):
                continue
            match = scene_row.match(entry)
            if match is None:
                continue
            rows[current_test].append((match.group("scene"), match.group("status")))

    return rows


def validate_results(bundle_dir: Path, profile: str, messages: list[str]) -> bool:
    path = bundle_dir / "parsek-test-results.txt"
    if not path.is_file():
        messages.append("Missing artifact: parsek-test-results.txt")
        return False

    rows = parse_results(read_text(path))
    ok = True

    for test_name in PROFILE_TESTS[profile]:
        captured_rows = rows.get(test_name, [])
        if not captured_rows:
            messages.append(f"Missing required runtime row: {test_name}")
            ok = False
            continue

        non_passed = [(scene, status) for scene, status in captured_rows if status != "PASSED"]
        if non_passed:
            detail = ", ".join(f"{scene}={status}" for scene, status in non_passed)
            messages.append(f"Required runtime row is not fully PASSED: {test_name} ({detail})")
            ok = False

    return ok


def validate_bundle(bundle_dir: Path, profile: str) -> tuple[bool, list[str]]:
    messages: list[str] = []
    ok = True

    for artifact in REQUIRED_ARTIFACTS:
        if not (bundle_dir / artifact).is_file():
            messages.append(f"Missing artifact: {artifact}")
            ok = False

    if ok:
        ok = validate_log_validation(bundle_dir, messages) and ok
        ok = validate_results(bundle_dir, profile, messages) and ok

    if ok:
        messages.append(f"Bundle profile '{profile}' passed all release evidence checks.")

    return ok, messages


def write_report(bundle_dir: Path, profile: str, ok: bool, messages: list[str]) -> None:
    status = "PASSED" if ok else "FAILED"
    report_lines = [
        f"Release bundle validation: {status}",
        f"Bundle: {bundle_dir}",
        f"Profile: {profile}",
        "",
    ]
    report_lines.extend(messages)
    (bundle_dir / "release-bundle-validation.txt").write_text(
        "\n".join(report_lines) + "\n",
        encoding="utf-8",
    )


def main() -> int:
    parser = argparse.ArgumentParser(description="Validate a Parsek release evidence bundle.")
    parser.add_argument("bundle_dir", help="Path to the collected bundle directory")
    parser.add_argument(
        "--profile",
        choices=sorted(PROFILE_TESTS.keys()),
        help="Release profile to validate (default: infer from bundle folder name)",
    )
    args = parser.parse_args()

    bundle_dir = Path(args.bundle_dir).resolve()
    if not bundle_dir.is_dir():
        print(f"ERROR: Bundle directory not found: {bundle_dir}", file=sys.stderr)
        return 1

    profile = args.profile or infer_profile(bundle_dir)
    if profile is None:
        print(
            "ERROR: Could not infer release profile from bundle name. "
            "Pass --profile explicitly.",
            file=sys.stderr,
        )
        return 1

    ok, messages = validate_bundle(bundle_dir, profile)
    write_report(bundle_dir, profile, ok, messages)

    for message in messages:
        print(message)

    if not ok:
        print(
            f"Release bundle validation failed. See "
            f"{bundle_dir / 'release-bundle-validation.txt'}",
            file=sys.stderr,
        )
        return 1

    print(f"Release bundle validation passed. See {bundle_dir / 'release-bundle-validation.txt'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
