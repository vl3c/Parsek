#!/usr/bin/env python3
"""Append a metrics row (scripts/arch/metrics.py) to the metrics history.

The history is `metrics.jsonl` on the `metrics` branch: one JSON object per
line, in arrival order, at most one row per commit sha. Two commands:

    # the file step alone, in any directory that holds (or will hold) metrics.jsonl
    python3 scripts/arch/metrics_append.py append --row row.json --file metrics.jsonl

    # the CI job: check the branch out into a worktree of --repo, append, commit, push
    python3 scripts/arch/metrics_append.py publish --row row.json --repo . \\
        --remote origin --branch metrics --workdir ../metrics-branch

`publish` creates the branch as an orphan holding README.md when the remote
does not have it. A push the remote rejects (another job pushed first) is
retried after a re-fetch, a reset to the remote tip and a re-append, so the
row lands after whatever won the race. A row whose sha is already in the file
changes nothing and exits 0, so re-running a job is safe.

The worktree lives inside --repo's git directory, so it pushes with whatever
credentials that repository carries (in CI, the token actions/checkout
persists in the workspace checkout).

The file logic (`append_text`, `row_sha`, `read_shas`) is pure; the git calls
are the shell around it.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
import uuid
from pathlib import Path

KNOWN_SCHEMAS = ("parsek-metrics/1",)
HISTORY_FILE = "metrics.jsonl"
BOT_NAME = "github-actions[bot]"
BOT_EMAIL = "41898282+github-actions[bot]@users.noreply.github.com"
README = """# Parsek metrics history

This branch holds `metrics.jsonl`: one JSON row per commit on `main`, appended by
the `metrics` job of `.github/workflows/tests.yml` (`scripts/arch/metrics_append.py`).
Each row is built by `scripts/arch/metrics.py`; its docstring and
`docs/dev/arch/README.md` ("Metrics history") document the keys. The rows gate nothing.

Do not edit by hand; the job only appends.
"""


class RowError(ValueError):
    """A row or a history line that cannot be appended or read."""


# ---------------------------------------------------------------------------
# pure file logic
# ---------------------------------------------------------------------------


def row_sha(row):
    """The row's commit sha, after checking its schema id."""
    if not isinstance(row, dict):
        raise RowError("a row is a JSON object, not %s" % type(row).__name__)
    schema = row.get("schema")
    if schema not in KNOWN_SCHEMAS:
        raise RowError("unknown row schema %r (known: %s)" % (schema, ", ".join(KNOWN_SCHEMAS)))
    sha = (row.get("commit") or {}).get("sha")
    if not isinstance(sha, str) or not sha:
        raise RowError("the row has no commit.sha")
    return sha


def read_shas(text):
    """The sha of every row in a history text, in file order. A malformed line raises."""
    shas = []
    for number, line in enumerate(text.splitlines(), 1):
        if not line.strip():
            continue
        try:
            row = json.loads(line)
        except json.JSONDecodeError as exc:
            raise RowError("%s line %d is not JSON: %s" % (HISTORY_FILE, number, exc)) from exc
        try:
            shas.append(row_sha(row))
        except RowError as exc:
            raise RowError("%s line %d: %s" % (HISTORY_FILE, number, exc)) from exc
    return shas


def row_line(row):
    """The row as the one line the history stores (sorted keys, no spaces)."""
    return json.dumps(row, sort_keys=True, separators=(",", ":"), ensure_ascii=True)


def append_text(existing, row):
    """(new history text, appended) for `row` added to `existing`.

    Existing lines are kept byte for byte; a missing final newline is added
    before the new line. A row whose sha is already present leaves the text
    unchanged and returns appended=False.
    """
    sha = row_sha(row)
    if sha in read_shas(existing):
        return existing, False
    if existing and not existing.endswith("\n"):
        existing += "\n"
    return existing + row_line(row) + "\n", True


def append_file(path, row):
    """Append `row` to the history file at `path` (created when missing). True when it changed."""
    path = Path(path)
    existing = path.read_text(encoding="utf-8") if path.exists() else ""
    text, appended = append_text(existing, row)
    if appended:
        path.write_text(text, encoding="utf-8", newline="\n")
    return appended


def load_row(path):
    try:
        row = json.loads(Path(path).read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise RowError("%s is not JSON: %s" % (path, exc)) from exc
    row_sha(row)
    return row


# ---------------------------------------------------------------------------
# git shell
# ---------------------------------------------------------------------------


def git(args, cwd, check=True):
    result = subprocess.run(["git", *args], cwd=str(cwd), capture_output=True, text=True, check=False)
    if check and result.returncode != 0:
        raise RuntimeError("git %s failed (%d): %s" % (" ".join(args), result.returncode,
                                                       (result.stderr or result.stdout).strip()))
    return result


def remote_has_branch(repo, remote, branch):
    result = git(["ls-remote", "--exit-code", "--heads", remote, branch], repo, check=False)
    if result.returncode == 2:
        return False
    if result.returncode != 0:
        raise RuntimeError("git ls-remote %s %s failed: %s" % (remote, branch, result.stderr.strip()))
    return True


def tracking_ref(remote, branch):
    return "refs/remotes/%s/%s" % (remote, branch)


def checkout_branch(repo, remote, branch, workdir):
    """Point the worktree at `workdir` to the remote tip of `branch`, or to a
    fresh orphan history holding README.md when the remote has none. Returns
    True when the branch already existed on the remote.

    The worktree never checks out a local branch named `branch`: git refuses
    a branch another worktree of the repository already holds, so the tip is
    checked out detached and publish() pushes HEAD to the remote branch. The
    orphan case needs a branch name to be born on, so it gets a throwaway one.
    """
    workdir = Path(workdir)
    exists = remote_has_branch(repo, remote, branch)
    if exists:
        git(["fetch", "--quiet", "--no-tags", "--depth", "1", remote,
             "+refs/heads/%s:%s" % (branch, tracking_ref(remote, branch))], repo)
    if not workdir.exists():
        git(["worktree", "add", "--quiet", "--detach", str(workdir),
             tracking_ref(remote, branch) if exists else "HEAD"], repo)
    if exists:
        git(["checkout", "--quiet", "--force", "--detach", tracking_ref(remote, branch)], workdir)
        git(["clean", "-fdxq"], workdir)
        return True
    git(["checkout", "--quiet", "--force", "--detach"], workdir)
    git(["checkout", "--quiet", "--orphan", "%s-new-%s" % (branch, uuid.uuid4().hex[:12])], workdir)
    git(["rm", "-rfq", "--ignore-unmatch", "."], workdir)
    git(["clean", "-fdxq"], workdir)
    (workdir / "README.md").write_text(README, encoding="utf-8", newline="\n")
    git(["add", "README.md"], workdir)
    return False


def commit_row(workdir, row, name=BOT_NAME, email=BOT_EMAIL):
    """Append and commit; True when a commit was made (False: the sha was already there)."""
    workdir = Path(workdir)
    if not append_file(workdir / HISTORY_FILE, row):
        return False
    git(["add", HISTORY_FILE], workdir)
    git(["-c", "user.name=%s" % name, "-c", "user.email=%s" % email, "-c", "commit.gpgsign=false",
         "commit", "--quiet", "-m", "metrics: %s" % row_sha(row)[:7]], workdir)
    return True


def publish(repo, remote, branch, workdir, row, attempts=4, delay=2.0, before_push=None, log=print):
    """Append `row` to the branch's history and push it. Returns "pushed" or "present".

    A rejected push is retried up to `attempts` times in all, each after a
    re-fetch, a reset to the remote tip and a re-append. `before_push(attempt)`
    is a test seam called just before each push.
    """
    sha = row_sha(row)
    for attempt in range(1, attempts + 1):
        existed = checkout_branch(repo, remote, branch, workdir)
        if not commit_row(workdir, row):
            log("metrics: %s is already in %s on %s; nothing to push" % (sha[:12], HISTORY_FILE, branch))
            return "present"
        if before_push is not None:
            before_push(attempt)
        result = git(["push", "--quiet", remote, "HEAD:refs/heads/%s" % branch], workdir, check=False)
        if result.returncode == 0:
            log("metrics: pushed %s to %s (%s, attempt %d)" % (
                sha[:12], branch, "appended" if existed else "branch created", attempt))
            return "pushed"
        reason = (result.stderr or result.stdout).strip().splitlines()
        log("metrics: push attempt %d of %d rejected: %s" % (attempt, attempts, reason[-1] if reason else "no output"))
        if attempt < attempts and delay:
            time.sleep(delay * attempt)
    raise RuntimeError("metrics: could not push %s to %s after %d attempts" % (sha[:12], branch, attempts))


def main(argv=None):
    parser = argparse.ArgumentParser(description="Append a metrics row to the metrics history.")
    sub = parser.add_subparsers(dest="command", required=True)
    append = sub.add_parser("append", help="append to a metrics.jsonl file (no git)")
    append.add_argument("--row", required=True, help="the row file metrics.py wrote")
    append.add_argument("--file", default=HISTORY_FILE, help="the history file")
    pub = sub.add_parser("publish", help="append on the metrics branch, commit and push")
    pub.add_argument("--row", required=True, help="the row file metrics.py wrote")
    pub.add_argument("--repo", default=".", help="a checkout whose remote holds the branch")
    pub.add_argument("--remote", default="origin")
    pub.add_argument("--branch", default="metrics")
    pub.add_argument("--workdir", required=True, help="where to check the branch out (a worktree of --repo)")
    pub.add_argument("--attempts", type=int, default=4)
    pub.add_argument("--delay", type=float, default=2.0, help="seconds before retry n is n times this")
    args = parser.parse_args(argv)
    try:
        row = load_row(args.row)
        if args.command == "append":
            appended = append_file(args.file, row)
            print("metrics: %s %s" % (row_sha(row)[:12], "appended" if appended else "already present"))
        else:
            publish(args.repo, args.remote, args.branch, args.workdir, row, args.attempts, args.delay)
    except (RowError, RuntimeError, OSError) as exc:
        print("metrics_append: %s" % exc, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
