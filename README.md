# Kozmo Commercial Intelligence Platform

Kozmo is a deterministic commercial-intelligence platform that turns vendor signals (uptime drops, payment delays, CSAT alerts) into auditable posture stances. A rule-based pipeline classifies each signal into one of four dimensions (Operational / Experiential / Financial / Strategic), scores beliefs with a confidence-weighted rubric, aggregates them into an EntityIndex, and assigns a stance (Maintain / Monitor / Renegotiate / Escalate / Remediate). The whole pipeline is a **glass box**: the same evidence always produces a byte-identical fingerprint.

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 8.0.x | `dotnet --version` |
| bash | any | for `ci/check-invariants.sh` (Git Bash on Windows) |
| `ANTHROPIC_API_KEY` | — | **Phase 1 only** — not needed to build or test Phase 0 |

## Build, test, and gate

```bash
# Build
dotnet build Kozmo.sln -c Release

# Test (23 tests — determinism spike + golden path + invariant lanes)
dotnet test Kozmo.sln -c Release

# PR gate (build -warnaserror + invariant filter — required to pass before merge)
bash ci/check-invariants.sh
```

## Repo map

```
libs/                          core libraries (Contracts, Platform, Llm, Bus, Identity)
subsystems/
  interpretation-inference/    I&I pipeline: Observation → Rubric → Index → Posture → Decay
  knowledge-memory/            Km.Store: SQLite belief store + Catalogue loader
tests/
  Kozmo.Architecture.Tests/   five CI invariant lanes (NetArchTest + BannedApiAnalyzers)
catalogue/profiles/saas/       nine *.saas.v1.json config files — the cognition parameters
fixtures/                      10 frozen signals + 3 vendor records (golden expectations)
ci/
  check-invariants.sh          the single gating command — wire as required PR status check
schema/                        JSON schemas → C# records in Kozmo.Contracts (codegen source)
PHASE_0.md                     architecture spec (read first)
DECISIONS.md                   durable architectural choices (read before proposing changes)
STEP_1_0_contract_amendment.md Step 1.0 contract changes (read before touching types)
STEP_0_8_ci_invariant_lanes.md CI lane details (read before touching BannedSymbols or arch tests)
CLAUDE.md                      guidance for Claude Code agents working in this repo
```
