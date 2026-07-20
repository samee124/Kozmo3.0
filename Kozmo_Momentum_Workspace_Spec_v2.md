# Kozmo — Momentum Workspace (Vendor Readiness)
## Revised Specification — Real-Wired, Interactive, One Impact Vector

**Status:** Build spec for the new primary UI, wired to the existing (non-configurable) backend.
**Supersedes:** the earlier "Path A standalone mock" spec. This is **Path B** — real data, real endpoints, honest states.
**Scope for today:** ONE Impact Vector — **Vendor Readiness** — with its four real dimensions (Operational, Experiential, Financial, Strategic) and their real weights, wired to the existing Kozmo backend. Fully interactive: answer a check-in → belief writes → gap fills → Super Index + stance recompute → visible on screen.
**Tomorrow (out of scope):** make Impact Vectors configurable (per-vector dimensions + weights) so other vectors work. That is the E2 generalization.

---

## 1. The reframe this UI expresses

Everything already built = **one Impact Vector**. Nothing about the engine changes; only the framing and the front end.

| Existing backend concept | Presented in the Momentum Workspace as |
|---|---|
| The `saas` vendor doctrine (rubric + catalogue + dimensions) | The **Vendor Readiness** Impact Vector |
| The four dimensions (Operational, Experiential, Financial, Strategic) | Vendor Readiness's **four dimensions**, with their real weights |
| The composite index (`IndexViewDto.Composite` + `Band`) | The **Super Index** (the vector's headline score) |
| Posture / stance (`PostureAssignment.Stance`) | The vector's **recommended stance** |
| Per-dimension score + confidence + beliefCount | Each dimension's **index + confidence + assessed/not-assessed state** |
| Open check-ins (`GET /checkins`) | The workspace's **open check-ins** (the interactive gap-fillers) |
| The check-in→belief→recompute→persist loop (built this session) | The **live "answer → watch it change"** interaction |
| Index history (checkpoints) | **Momentum** (the trend of the Super Index) |

**The engine (beliefs, Q&A, evidence, scoring, index roll-up) is unchanged.** This is a presentation layer over endpoints that already exist and already work.

---

## 2. What is REAL today vs. framed vs. honestly-absent

Critical for honesty (the discipline held all session). The Momentum Workspace must show the truth, never fabricate.

**REAL and wired (shown as live data):**
- The four dimensions and their real weights (from the profile config).
- Each dimension's real score, confidence, and **assessed / not-assessed** state (`beliefCount == 0` → "Not assessed").
- The Super Index (composite), band, and stance — real, from `GET /vendors/{id}`.
- Open check-ins for the vendor (`GET /checkins`).
- The **interactive loop**: answering a check-in writes a real belief, recomputes + persists the index, and the workspace re-renders the changed Super Index / dimension / stance. **This whole loop is already built and proven** (commits `5e0d76c`, `8037b83`).

**Framed (real data, new vocabulary):**
- "Vendor Readiness Impact Vector," "Super Index," "Momentum Workspace" are new *names* over real backend values — not new data.

**Honestly absent (shown as "not assessed" / disabled, NEVER faked):**
- Dimensions with no scoring beliefs (for a real vendor like IIVS: Operational may be real; Experiential/Financial/Strategic likely "not assessed"). Shown truthfully as "Not assessed," never as 0.5 or a mock number.
- Momentum history: only real if the vendor has multiple persisted index checkpoints. If only one exists, show "Baseline — first review" honestly, not a fabricated trend line.
- Other Impact Vectors: **not shown as live**. May appear ONLY as clearly-disabled "Coming soon — configurable" placeholders, never as if they work.

**The honest demo picture:** for a real vendor, the workspace shows the Vendor Readiness Super Index with **the dimensions that have data lit and the rest honestly "not assessed."** The live moment: answer the bound uptime check-in → the Operational dimension fills (gap closes) → Super Index + stance recompute → visible on screen. Convincing *because* it's real.

**Scope note on which dimensions can flip live:** only dimensions with **bound answerable check-ins** flip on answer. Today that's Operational (via `sla_uptime`) and Experiential (via `csat`). Financial/Strategic don't yet have bound scoring check-ins (that's later E2 binding work). So the live loop demonstrates on Operational (and Experiential if a csat check-in is open), honestly.

---

## 3. Endpoints to wire (all exist today)

- `GET /programs/{id}/vendors` — the vendor list (already dedup-tiebroken to surface the assessed duplicate).
- `GET /vendors/{id}` — the real IndexViewDto: `Composite`, `Band`, `ConfidenceFloor`, `BandDrivenBy`, `WorstDimension`, `Dimensions[]` (each: `Dimension`, `Score`, `Confidence`, `Weight`, `Contribution`, `BeliefCount`), plus posture/stance. Returns 404 / not-assessed gracefully.
- `GET /vendors/{id}/trail` — per-belief drill-down for a dimension (`BeliefViewDto`: `Value`, `Confidence`, `SourceTier`, `Derivation`, `ClassificationMethod`), handles not-assessed (`Assessed:false`).
- `GET /vendors/{id}/metadata` — extracted contract clauses (real evidence).
- `GET /vendors/{id}/vendor-file` — real completeness ratio / filled / gap keys.
- `GET /checkins` — open check-ins (filter client-side by vendorId).
- `POST /checkins/{id}/answer` — the fixed path: writes banded/dimensioned/Confirmed belief, recomputes, **persists** the index. **After calling this, re-fetch `GET /vendors/{id}` and re-render.**

No new backend endpoints are required for today's scope.

---

## 4. Screens

The Momentum Workspace is the shell. For today, it centers on ONE vector (Vendor Readiness) for a selected vendor.

### Screen A — Momentum Workspace (the main screen)
The primary UI. For the selected Commercial Party (vendor):

1. **Party header** — vendor name, type tags, program. A "Design/preview" honesty note is NOT needed here since it's real data — but if other (non-working) vectors are shown as placeholders, they must be clearly labeled "coming soon."

2. **Impact Vector header — the signature instrument.** For Vendor Readiness:
   - Super Index number (large), band label (semantic color), stance, momentum arrow.
   - A momentum sparkline IF ≥2 real checkpoints exist; else "Baseline" honestly.
   - The **dimension-contribution bar** — a single stacked bar showing each dimension's weighted contribution, segments colored by each dimension's band; "not assessed" dimensions render as a neutral hatched/empty segment (honest — visibly not-yet-scored, not a fake value).

3. **Dimensions grid** — one card per dimension (Operational, Experiential, Financial, Strategic):
   - Dimension name, weight, real score + score bar, confidence tag, trend.
   - If `beliefCount == 0`: render "Not assessed" (neutral text, empty bar) — never 0.5, never a mock number.
   - Tappable → Screen B (dimension detail).

4. **Open check-ins panel — the interactive core.** Lists the vendor's open check-ins (`GET /checkins`). Each: the question, a typed input (or appropriate control for its response shape), and an **Answer** button.
   - On answer: `POST /checkins/{id}/answer` → show a brief "Updating…" state → re-fetch `GET /vendors/{id}` → **re-render** the Super Index, the affected dimension (gap fills), band, and stance. The change is visible on screen.
   - Answered check-in leaves the list; the dimension it fed visibly updates.

5. **Recommended stance + evidence** — the stance (from posture) and the key real evidence (from `/metadata` and the contributing beliefs), shown as the vector's output.

6. **(Optional) Other Impact Vectors strip** — Contract Risk / Spend Optimization / etc. shown ONLY as disabled "Coming soon — configurable" cards. Never as working. Omit entirely if it risks looking live.

### Screen B — Dimension detail
Drill into one dimension:
- Dimension name + real score + bar.
- **What we know** — the contributing beliefs (from `/trail`): value, source tier, derivation, confidence — the real grounding.
- If not assessed: honest "No scoring evidence yet for this dimension" + which check-ins would fill it (if any are bound).
- **Recommended actions** — derived from the stance / gaps.
- Prev/next dimension.

### Screen C — Momentum & history (only if real history exists)
- Super Index over its real checkpoint series (line). If only one checkpoint: "Baseline — first review," no fake trend.
- Timeline of past reviews (checkpoints) with date + index.
- Honest: if there's no history, this screen says so plainly rather than inventing a trend.

---

## 5. Visual design — "Commercial instrument"

Deliberately NOT the consumer cream/terracotta look of the shared mock (that reads as a lifestyle brand and is a known AI-default tell). Kozmo's audience is enterprise procurement/finance/legal + investors → serious, precise, institutional.

### 5.1 Palette
```
--canvas      #F4F5F7   /* cool institutional off-white */
--surface     #FFFFFF
--ink         #14171F   /* near-black text / structure */
--muted       #5B6270
--line        #E3E6EB   /* hairlines */
--accent      #1F5FA6   /* deep serious blue — Super Index, active, focus, links */

/* Semantic status — meaning only, never decoration */
--critical    #B23A48   --critical-bg #F7E9EB
--atrisk      #C77D24   --atrisk-bg   #F7EFDF
--healthy     #2E7D5B   --healthy-bg  #E4F0EA
--notassessed #9AA1AC   /* neutral grey for honest not-assessed states */
```
Semantic colors carry band severity (a score's color = its band) and are always paired with a text label / glyph so meaning survives grayscale + color-blindness.

### 5.2 Typography
```
Display:  "Fraunces" (high-contrast serif) — Super Index number + section headings. 400/600. Restraint.
Body/UI:  "Inter" — dense data + labels. 400/500/600.
Numerals: Inter with tabular-nums (font-feature-settings:'tnum') for all scores/weights/deltas.
```
Scale: 64 (Super Index) / 32 (screen title) / 22 (section) / 16 (body) / 14 (card title) / 13 (secondary) / 11 (eyebrow, uppercase, .08em tracking).

### 5.3 Signature element
The **Super Index instrument header**: big Fraunces score + semantic band + momentum sparkline (real, or "Baseline") + the stacked **dimension-contribution bar** (each dimension's weighted contribution, colored by its band; not-assessed = hatched/empty). One element encodes dimensions → weighted roll-up → Super Index → momentum. Everything else stays quiet.

```
┌──────────────────────────────────────────────────────────────────────┐
│ VENDOR READINESS · REVIEW                              [ Re-review ]   │
│                                                                        │
│   40        CRITICAL   ↓            (Baseline — 1 review)               │
│  /100       ── Super Index ──   Stance: Escalate                       │
│                                                                        │
│  Contribution  ▓▓▓▓░░░░ ▨▨▨▨ ▨▨▨▨ ▨▨▨▨                                  │
│                Operational  Exp   Fin  Strat                           │
│                (real 0.10)  (not assessed — hatched)                   │
└──────────────────────────────────────────────────────────────────────┘
```

### 5.4 Layout, motion, quality floor
- Max width ~1080px, 56px section rhythm. Cards: surface on canvas, 1px line, 12px radius, 20–24px padding; hover border → accent.
- Dimensions: 2-col desktop / 1-col mobile. Check-ins: stacked list with inline inputs.
- Motion: 0.4s fade-up on screen change; score/contribution bars animate width from 0 on paint; **on check-in answer, the updated Super Index number counts to its new value and the filled dimension bar animates** (this transition IS the payoff — it earns motion). Respect `prefers-reduced-motion` (skip animations, jump to final values).
- Quality floor: responsive to mobile, 2px accent keyboard focus ring, reduced-motion respected, status never color-only.

---

## 6. Honesty requirements (non-negotiable)

1. Not-assessed dimensions render as "Not assessed" (neutral), never 0.5, never a fabricated score.
2. No fake momentum: a single checkpoint shows "Baseline," not an invented trend.
3. Other Impact Vectors, if shown at all, are clearly disabled "Coming soon — configurable," never made to look live.
4. Everything in the Vendor Readiness vector binds to real endpoints. If an endpoint 404s / returns not-assessed, show the honest empty state, not an error and not mock data.
5. The interactive loop shows the REAL recomputed result (whatever it is — a good answer improves the score, a bad one worsens it; the score reflects reality, not optimism).

---

## 7. Relationship to E2 (why this is safe to build now)

- The Momentum Workspace for the ONE existing vector binds to `GET /vendors/{id}`, which is stable at the current backend state (E2.1 + E2.2a committed). E2.2b/c/3/4/5 do NOT change what that endpoint returns for the existing vector — they generalize to *other* vectors/dimensions.
- Therefore this UI needs **no** E2 completion and will **not** require rework when E2 is later finished. E2 is what lets the Workspace show *additional* vectors, configurably — tomorrow's work.
- Build order rationale: ship the visible interactive Workspace now (one real vector); resume E2 later to generalize it. The dashboard motivates and directs the engine work.
