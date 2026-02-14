---
name: codex
description: Delegate tasks to OpenAI Codex CLI for code analysis, refactoring, editing, and code reviews. Use when the user wants to leverage Codex or explicitly mentions Codex.
argument-hint: [prompt]
---

# Codex CLI Integration

Invoke the Codex CLI in headless (non-interactive) mode to delegate tasks.

## General Tasks

**Windows (default — sandbox works natively):**
```bash
codex exec --full-auto "$ARGUMENTS"
```

**Linux / Raspberry Pi (Landlock sandbox not available — must bypass):**
```bash
codex exec --dangerously-bypass-approvals-and-sandbox "$ARGUMENTS"
```

## Code Review

By default, `codex exec review` reviews the latest commits (not uncommitted changes).

**Review latest commits (Windows):**
```bash
codex exec review --full-auto
```

**Review latest commits (Linux / Raspberry Pi):**
```bash
codex exec review --dangerously-bypass-approvals-and-sandbox
```

**Review against a base branch:**
```bash
codex exec review --full-auto --base main
```

**Review a specific commit:**
```bash
codex exec review --full-auto --commit <SHA>
```

**Custom review instructions:**
```bash
codex exec review --full-auto "focus on security vulnerabilities"
```

### Review Options

| Flag | Description |
|------|-------------|
| `--base <BRANCH>` | Review changes against the given base branch |
| `--commit <SHA>` | Review changes introduced by a specific commit |
| `--uncommitted` | Review staged, unstaged, and untracked changes (can be flaky) |
| `--title <TITLE>` | Optional title to display in review summary |

## Core Options

| Flag | Description |
|------|-------------|
| `-m, --model <MODEL>` | Override model (omit to use default) |
| `-C, --cd <DIR>` | Working directory for the agent |
| `-i, --image <FILE>` | Attach image(s) to the prompt |
| `-o, --output-last-message <FILE>` | Write final message to file |
| `--json` | Output events as JSONL |

## Platform Notes

- **Windows:** Use `--full-auto` (workspace-scoped sandbox works natively)
- **Linux with Landlock:** Use `--full-auto` if `cat /sys/kernel/security/landlock/abi_version` succeeds
- **Linux without Landlock (Raspberry Pi, older kernels):** Must use `--dangerously-bypass-approvals-and-sandbox` since the default sandbox requires Landlock

## Session Management

```bash
codex exec resume --last          # Resume most recent session
codex exec resume <session-id>    # Resume specific session
```

## Notes

- Uses the default model from `~/.codex/config.toml` unless `-m` is specified
- Do NOT pipe stderr to `/dev/null` — it can suppress all output
- `codex review` (top-level) also works but lacks `--full-auto` / sandbox flags
- Codex must be installed with valid credentials (`codex --version` to verify)
