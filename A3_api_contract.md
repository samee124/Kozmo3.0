# A3 — Read API & SSE Contract

**Purpose.** This is the seam Dev B's UI binds to. A3 builds a thin HTTP layer over the
existing `IIiFacade` — it does **not** add reasoning logic. It reads state, exposes the
glass-box drill-down, serves the 30-day trajectory, and offers reset/replay for the live
demo moments.

**Principles**
- **DTOs, not internal records.** The API exposes explicit view-model DTOs (below), not
  `EntityIndex`/`PostureAssignment` directly. Dev A can change internal records without
  breaking the UI as long as the DTO mapping holds. This is the split-at-contracts rule
  applied to the UI seam.
- **Read-only except reset/replay.** The only state-changing endpoints are `/demo/reset`
  and `/demo/replay`; everything else is a pure read.
- **Deterministic.** `GET` results are a function of current state; `reset` recomputes
  from the seed and returns identical fingerprints (the "run it again" proof).
- **JSON shapes are the contract** (expressed below language-neutrally, since the backend
  is C# and the UI is TypeScript).

---

## Endpoints

| Method | Path | Returns | Purpose |
|---|---|---|---|
| GET | `/vendors` | `VendorSummary[]` | List view — the three vendors with band/stance/fingerprint |
| GET | `/vendors/{id}` | `VendorDetail` | Detail — index + posture for one vendor |
| GET | `/vendors/{id}/trail` | `ReasoningTrail` | The glass-box drill-down (Posture ← Band ← Index ← Beliefs ← Signal) |
| GET | `/vendors/{id}/trajectory` | `TrajectoryPoint[]` | The 30-day posture trajectory (for the chart) |
| POST | `/demo/reset` | `{ vendors: VendorSummary[] }` | Recompute from seed; returns identical fingerprints (determinism proof) |
| POST | `/demo/replay` | `202 Accepted` | Begin a stepped replay; emits SSE events; ends with `replay-complete` |
| GET | `/events` | SSE stream | Server-sent events for live replay (`text/event-stream`) |

---

## Read DTOs

```jsonc
// VendorSummary — list row
{
  "entityId": "cloudwave",
  "name": "Cloudwave Systems Inc.",
  "band": "AtRisk",            // Healthy | AtRisk | Critical | ...
  "stance": "Renegotiate",     // Maintain | Renegotiate | Escalate | Remediate | ...
  "confidence": 0.78,          // posture confidence (already clamped 0..0.95)
  "fingerprint": "e5d0e9b9…",
  "asOf": "2025-..."           // the demo as-of date
}

// VendorDetail — detail page header
{
  "entityId": "cloudwave",
  "name": "Cloudwave Systems Inc.",
  "asOf": "2025-...",
  "index":   { /* IndexView */ },
  "posture": { /* PostureView */ }
}

// IndexView
{
  "composite": 0.47,
  "confidenceFloor": 0.80,
  "band": "AtRisk",
  "fingerprint": "e5d0e9b9…",
  "configVersion": "v1",
  "bandDrivenBy": "composite",      // "composite" | "worst-dimension-floor"
  "worstDimension": { "dimension": "Financial", "score": 0.31 },
  "dimensions": [ /* DimensionScoreView[] */ ]
}

// DimensionScoreView
{
  "dimension": "Operational",
  "score": 0.52,
  "confidence": 0.85,
  "weight": 0.30,
  "contribution": 0.156,            // score * weight, for the UI breakdown
  "beliefCount": 3
}

// PostureView
{
  "stance": "Renegotiate",
  "confidence": 0.78,
  "rationale": "AT_RISK with renewal in 18 days → commercial leverage window.",
  "cautions": [],                   // from MetaCognitionResult.Contradictions (A2)
  "evidenceGaps": [],               // from MetaCognitionResult.Gaps (A2)
  "renewal": { "renewalDate": "2025-...", "windowActive": true, "daysToRenewal": 18 }
}
```

### The drill-down — `ReasoningTrail` (the glass box)

One nested call returns the full chain so the UI can expand each layer without extra
round-trips.

```jsonc
{
  "posture": { /* PostureView */ },
  "band":    { "band": "AtRisk", "thresholds": { "Critical": 0.30, "AtRisk": 0.50, "Healthy": 0.70 }, "drivenBy": "composite" },
  "index":   { /* IndexView */ },
  "dimensions": [
    {
      "dimension": "Financial",
      "score": 0.31, "confidence": 0.80, "weight": 0.25,
      "beliefs": [ /* BeliefView[] */ ]
    }
    // ... one per dimension
  ]
}

// BeliefView — one rung above the raw signal
{
  "beliefId": "...",
  "dimension": "Financial",
  "criterion": "invoice_growth",
  "value": 0.30,
  "confidence": 0.80,
  "sourceTier": "Verified",                  // Verified | Inferred | Reported | Unverified
  "classificationMethod": "rule",            // "rule" | "llm" (annotation; not in fingerprint)
  "reasoningSummary": null,                   // LLM rationale later (annotation; not in fingerprint)
  "freshness": 0.91,                          // decay weight at as-of date
  "signal": { /* SignalRef */ }
}

// SignalRef — the raw evidence
{
  "signalId": "...",
  "type": "invoice",
  "timestamp": "2025-...",
  "source": "billing-system",
  "summary": "Invoice posted, 40% above prior month"   // + raw text for free-text signals
}
```

### Trajectory

```jsonc
// TrajectoryPoint — one per processed signal, ordered by timestamp
{
  "timestamp": "2025-...",
  "signalId": "...",
  "composite": 0.61,
  "band": "Healthy",
  "stance": "Maintain",
  "fingerprint": "…"        // fingerprint after this signal — lets the UI show evolution
}
```

The UI can animate the trajectory two ways: poll `/trajectory` and animate client-side
(simpler, no streaming), **or** subscribe to SSE for a server-driven replay (below).
`/trajectory` is always available; SSE is the optional richer path.

---

## Reset & replay

- **`POST /demo/reset`** — clears computed state, recomputes all three vendors from the
  seed, returns the final `VendorSummary[]`. Calling it twice must return **identical
  fingerprints** — this is the live determinism proof.
- **`POST /demo/replay`** — begins a stepped replay: signals are processed one at a time
  (with a small delay), and each step is pushed over SSE. Returns `202 Accepted`
  immediately; the client watches `/events`. Ends by emitting `replay-complete`.

---

## SSE event shape (`GET /events`, `text/event-stream`)

Each message uses a typed envelope:

```jsonc
{
  "type": "replay-step",     // replay-step | vendor-updated | replay-complete | reset-complete
  "ts": "2025-...",
  "data": { /* depends on type */ }
}
```

| `type` | `data` |
|---|---|
| `replay-step` | `{ entityId, signalId, timestamp, index: IndexView, stance, fingerprint }` |
| `vendor-updated` | `VendorSummary` |
| `replay-complete` | `{ vendors: VendorSummary[] }` |
| `reset-complete` | `{ vendors: VendorSummary[] }` |

---

## Façade dependencies — confirm before building

A3 is a wrapper over `IIiFacade`. These map to existing façade reads:
- `GET /vendors`, `/vendors/{id}` → `GetIndex` + `GetPosture`
- `GET /vendors/{id}/trail` → `GetReasoningTrail`
- `POST /demo/reset` → `Reset`

These likely need **small, deliberate façade additions** (treat like the 1.0a patch — if
the façade doesn't already expose them, surface it as a contract decision, don't add
silently):
- **`GetTrajectory(entityId)`** (or `GetHistory`) — the index/posture history over the
  window. The append-and-supersede store already retains history; this exposes it.
- **A replay orchestration** — either a `Replay()` method on the façade, or the API
  drives it by re-ingesting seed signals one at a time and emitting after each. Prefer the
  API-drives-it approach so the façade stays read-shaped and the replay concern lives in
  the host.

---

## Non-goals
- No write/ingest endpoint beyond reset/replay (the demo runs off the seed).
- No auth (faked customer context).
- No new reasoning logic — A3 only reads and serves.
- Don't serialize internal records directly — map to the DTOs above.
- Don't let the API surface change the fingerprint or any decision.
