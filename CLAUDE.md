# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What Kozmo is

A deterministic commercial-intelligence platform: a signal (uptime drop, payment delay, CSAT alert) enters the pipeline; rule-based classification, weighted scoring across four dimensions (Operational / Experiential / Financial / Strategic), and a confidence-gated posture engine produce a vendor stance (Maintain / Monitor / Renegotiate / Escalate / Remediate). The whole pipeline is a **glass box** — same evidence in produces a byte-identical fingerprint every run.

## Current state — Dev B starts here

Dev A work (A1–A4) is **complete**. 71 tests green, 5 CI invariant lanes passing.

- 16 .NET 8 projects, frozen contracts, 9 catalogue configs, 3-vendor / 10-signal seed, deterministic pipeline
- Full REST API (`host/dotnet/Kozmo.Api`) with `/vendors`, `/vendors/{id}`, `/vendors/{id}/trail`, `/vendors/{id}/trajectory`
- Dev B owns: `CachingLlmClient`, real `LlmClient` (in `Kozmo.Llm.Anthropic` only), contradiction + gap detection, data seed-prep tooling, React UI
- `PHASE_1.md` must exist before starting Phase 1 work — if absent, stop and ask

## Spec — read before touching anything

| Before you… | Read |
|---|---|
| Start any work | `PHASE_0.md` (architecture, golden expectations), `DECISIONS.md` (settled choices + A1–A4 errata) |
| Touch contracts or generated types | `STEP_1_0_contract_amendment.md` |
| Touch CI lanes, BannedSymbols, arch tests | `STEP_0_8_ci_invariant_lanes.md` |
| Start Phase 1 | `PHASE_1.md` |
| Touch catalogue or scoring | the nine `*.saas.v1.json` in `catalogue/profiles/saas/` |

Business rules (tier weights, band thresholds, half-lives) live **in the configs, not in your head**. Read them; never invent a number.

## Build / test / gate

```bash
dotnet build Kozmo.sln -c Release                                               # must be 0 errors, 0 warnings
dotnet test Kozmo.sln -c Release                                                # 71 tests green
bash ci/check-invariants.sh                                                     # PR gate — run before every commit
dotnet test Kozmo.sln --filter "DisplayName~<TestName>"                         # single test
dotnet test tests/Kozmo.Architecture.Tests --filter "Category=Invariant"        # invariant lanes only
dotnet test Kozmo.sln --filter "Golden"                                         # golden pin tests only
```

## Solution layout

```
libs/
  Kozmo.Contracts/          shared records + enums — generated from schema/; never hand-edit
  Kozmo.Platform/           FingerprintComputer (SHA-256, deterministic)
  Kozmo.Llm/                IKozmoLlm + LlmResult (Dev B implements CachingLlmClient here)
  Kozmo.Bus/                IKozmoBus (reserved)
  Kozmo.Identity/           ICustomerContext (reserved)
subsystems/interpretation-inference/dotnet/
  Ii.Contracts/             module interfaces + IIiFacade
  Ii.Observation/           rule-based classification + entity/alias resolution
  Ii.Rubric/                per-dimension weighted scoring (deterministic)
  Ii.Index/                 composite score + confidence floor + fingerprint + banding
  Ii.Posture/               band + trend + renewal date → stance (deterministic)
  Ii.Decay/                 freshness half-life
  Ii.Spine/                 façade orchestrator; sole clock reader
  Ii.Fakes/                 FakeBus, FakeCustomerContext
  Ii.Tests/                 unit + integration tests (determinism spike + golden path + BandDrivenBy)
subsystems/knowledge-memory/dotnet/
  Km.Store/                 SqliteEntityStore + Catalogue (append-supersede, validated)
host/dotnet/
  Kozmo.Api/                ASP.NET 8 REST host — Program.cs, DtoMapper, endpoint handlers
  Kozmo.Api.Tests/          integration tests via WebApplicationFactory (Classes K–N)
tests/
  Kozmo.Architecture.Tests/ 7 invariant-lane tests (all 5 CI lanes)
catalogue/profiles/saas/    9 *.saas.v1.json configs
ci/check-invariants.sh      PR gate command
```

## Reference rules (enforced by CI)

- Modules reference `Ii.Contracts` only — never each other, never `Ii.Spine`
- `Km.Store` references `Kozmo.Contracts` only — never `Ii.*`
- Only `Ii.Spine` reads the clock — all other modules receive time as a parameter
- Generated types in `Kozmo.Contracts` are never hand-edited

## The five CI invariants

1. **Pipeline direction** — module assemblies do not reference each other; NetArchTest in `Kozmo.Architecture.Tests`
2. **Belief immutability** — `Belief` is all-init-only; `IEntityStore` exposes no belief-edit path; reflection tests
3. **Determinism** — `DateTime.UtcNow`, `DateTimeOffset.UtcNow`, `Random`, `Stopwatch` banned in 5 module projects via `BannedApiAnalyzers`; violation = RS0030 build error
4. **Confidence discipline** — `REPORTED` weight (0.50) < CRITICAL gate (0.60); all tier weights ≤ 1.0; catalogue config tests
5. **No live dependency** — `HttpClient` + `Anthropic` namespace banned in all demo-runtime projects; `Kozmo.Llm.Anthropic` (real client) must never be imported by demo runtime

## Dev A / Dev B split (Phase 1)

| Dev A — complete | Dev B — starts here |
|---|---|
| `IIiFacade` pipeline (A1–A4) | `CachingLlmClient` — no network; cache miss must throw |
| API host + trail/trajectory endpoints | `LlmClient` — real Anthropic; lives in `Kozmo.Llm.Anthropic` only |
| CI invariant lanes (all 5 green) | Contradiction + gap detection logic |
| BandDrivenBy mechanism (A4) | Data seed-prep tooling; React UI |

**Dev B hard rule:** `LlmClient` lives in `Kozmo.Llm.Anthropic` — never imported by demo runtime (Lane 5 rejects it at build). A `CachingLlmClient` cache miss must fail the run, never fall through to the network.

## Prime directives (non-negotiable)

1. **Reproducible fingerprint.** Inputs: sorted beliefs `(Dimension, Criterion, Value, Confidence)` + dimension scores + weights + config_version. **Never** include annotation fields (`ClassificationMethod`, `ClassificationConfidence`, `ReasoningSummary`, `Cautions`, `EvidenceGaps`). Run the determinism spike after every contract change.
2. **Beliefs are append-and-supersede.** No edit path. A correction is a new version pointing back via `superseded_by`.
3. **No live network in the demo runtime path.** LLM outputs are served from frozen cache. Real client reachable only from seed-prep / smoke entrypoints.
4. **Earn every structure.** 4 dimensions, 5 stances, 9 configs. Adding one requires explicit sign-off.
5. **Ask, don't invent.** Ambiguous business rule → stop and ask; do not guess.

## Golden expectations (must never drift)

- **Cloudwave Systems Inc.:** op=0.45 / exp=0.40 / fin=0.55 / strat=0.50 → AtRisk / Renegotiate
- **Corvus Infrastructure Ltd.:** all four dimensions 0.20–0.35 (uniformly Critical composite) → Critical / Escalate
- **Meridian IT Services Ltd.:** → Healthy / Maintain

`dotnet test Kozmo.sln --filter "Golden"` must stay green after every change.
