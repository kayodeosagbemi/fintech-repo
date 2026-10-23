# AI Transcript

AI was used throughout this exercise, as the README expects. This file explains what exists and where to find it.

## What is recorded where

| Artifact | What it is |
|---|---|
| [`PLAN_1.md`](PLAN_1.md) | The implementation plan that came out of the AI-assisted planning sessions (domain/persistence, API/auth/idempotency and messaging planning passes, then a "Round 2" revision for cost, error paths, service-per-project layout, console client, CSV import and a pluggable client). It is the written-down result of those conversations and the document the build followed. |
| [`Claude_Session.md`](Claude_Session.md) | The log of the implementation session: for every step, what was built, the review agent's findings, the fix agent's changes, and the orchestrator's decisions, in order. This is the record of agent-to-agent communication. |
| `git log` | One commit per plan step, plus a separate commit for each round of review fixes, with the AI co-author trailer on every commit. |

## How the implementation session worked

An AI coding assistant (Claude Code) implemented `PLAN_1.md` step by step on the `develop` branch. For each step:

1. The assistant implemented the step and ran the unit tests.
2. It committed to `develop`.
3. A separate **review agent** (read-only) reviewed the commit against the plan and reported concerns by severity.
4. A separate **fix agent** addressed the concerns and committed the fixes, with the tests re-run.
5. When review concerns were closed, the work was pushed to `origin/develop`.

Review rounds found real defects that were fixed before being pushed, for example: cascade deletes on financial foreign keys; a race in approval and processing transitions; a consumer loop that would die permanently on a single exception; a rail timeout that failed a payment that might have succeeded; a raw exception message leaking through a catch-all 409 mapping; an idempotency claim that could be stranded forever by a crashed worker. See `Claude_Session.md` for each.

## Note on the raw conversation

The human-facing chat with the assistant (the interactive prompts and replies) is held in the assistant tool's own session history rather than in this repository, so it is not reproduced verbatim here. `PLAN_1.md` and `Claude_Session.md` are the committed records of what the AI was asked to do and what it did. If the full raw chat export is required, it can be exported from the tool and appended below.
