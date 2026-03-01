## Git Commits
- Do NOT add `Co-Authored-By` or any signature line to commit messages

## Canonical Reference
See the root `CLAUDE.md` for build commands, project layout, debug, and post-change checklist.

## Worktree Workflow

For manual worktrees (when not using `isolation=worktree`), create as sibling folders:
```bash
cd Parsek
git worktree add ../Parsek-<branch-name> -b <branch-name> HEAD
```

Layout:
```
Code/Parsek/                     # Root
├── Kerbal Space Program/        # Shared KSP instance (one level up)
├── Parsek/                      # Main repo (do NOT edit directly)
├── Parsek-<branch-name>/        # Your worktree (work here)
```

`Parsek.csproj` probes up to 5 parent levels for `Kerbal Space Program/`, so builds work from worktrees at this location.

Merge: `cd Parsek && git merge <branch-name>`
