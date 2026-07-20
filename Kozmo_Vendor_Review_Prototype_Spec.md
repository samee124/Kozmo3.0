# Kozmo — Vendor Review UI / Momentum Workspace
## Design Prototype Specification (Path A — vision artifact, mock data)

**Status:** Specification for a standalone, self-contained design prototype.
**Purpose:** Vision artifact for investors and team alignment — shows where Kozmo is going, not what the live system renders today.
**Honesty stance:** Explicitly a *design prototype*. All data is curated mock data for one worked example (Cyberdyne Systems Inc.). It is not wired to the live pipeline and must carry a visible "Design prototype — illustrative data" marker so it is never mistaken for the running product.
**Stack:** Single standalone HTML file (self-contained: inline CSS + vanilla JS, no build step, no external JS deps except Google Fonts). This matches the nature of the shared `Image_Review_Prototype` mock and makes it trivial to open, share, and screen-record.

---

## 0. Why standalone mock, not wired-to-backend

The brief said Path A ("mock data, the full vision… honest because it's explicitly a prototype"). It also floated "it can take beliefs/evidence/documents of that vendor." Those pull in opposite directions, and for a **vision artifact** the standalone mock wins decisively:

- The full model (multiple Impact Vectors, per-vector dimensions like renewal-timing/utilization/spend, Super Index, Momentum, Matters) **does not exist in the backend yet** — it is what the E2 sequence is building toward. Wiring to real beliefs would surface the true current state (one composite index, 4 fixed dimensions, "only 2 of 16 claim keys fully wired," most dimensions "not assessed") — exactly the reality Path A exists to transcend for a pitch.
- A vision artifact must look finished and tell a clean story. A live-wired version today would show *less* than the vision, honestly, and would be fragile.
- Standalone = no dependency on the running app, no duplicate-vendor issues, no pipeline gaps. Open the file, present it.

If a real-wired version is wanted later, that is **Path B** — a separate, E2-gated effort where each section binds to real data or an honest "not assessed" placeholder. This spec is Path A only.

---

## 1. The model this prototype renders (decided)

This is the conceptual spine the UI visualizes. It is faithful to `Kozmo_02_Impact_Vectors`, `Kozmo_01_Reviews_and_Matters`, and the Momentum-Workspace framing — but expressed as a designed surface.

```
Commercial Party  (Cyberdyne Systems Inc.)
  └── Momentum Workspace            ← the primary UI object
        └── Impact Vectors (multiple, selectable)     ← the "Review" the user launches
              ├── Renewal Optimization      ← worked example #1 (fully built out)
              ├── Contract Risk             ← worked example #2 (built out)
              ├── Supplier Readiness        ← shown as a card (summary only)
              ├── Contract Compliance       ← shown as a card (summary only)
              └── Spend Optimization        ← shown as a card (summary only)
                    └── Dimensions (per-vector)        ← e.g. Renewal Opt → renewal timing, utilization, spend…
                          └── Vichara review → Dimension Index (score + confidence + trend)
                    └── Super Index = weighted roll-up of dimension indexes (per this vector's weightage config)
                    └── Momentum = trend of the Super Index over checkpoint history
                    └── Matters (triggered by weak/urgent dimensions) → Interventions → Next actions
```

**Key vocabulary shown to the user** (enterprise-facing verbs, per the brief):
- **"Review"** — the user-facing action ("Launch a Review of Cyberdyne for Renewal Optimization"). Internally unchanged (Q&A/evidence/beliefs), but the word the user sees.
- **Impact Vector** — the lens/config that sets the dimensions + evidence the Review looks for.
- **Dimension** — a measurable aspect of the vector, each independently reviewed and indexed.
- **Super Index** — the weighted roll-up for one Impact Vector (the headline score).
- **Momentum** — the direction the Super Index is moving (a derivative, not a level).
- **Matter** — a consequential situation the Review surfaced that needs deliberate action.

---

## 2. The worked example — Cyberdyne Systems Inc.

One Commercial Party, shown across multiple Impact Vectors. The brief's "Vendor Renewal Review" maps to the **Renewal Optimization** Impact Vector, built out in full. A second vector (**Contract Risk**) is built out to prove the model generalizes. Three more appear as summary cards to show breadth.

### 2.1 Impact Vector: Renewal Optimization (primary worked example)

**Super Index: 58 / 100 · AT RISK · Momentum: ↓ Declining**
(headline for this vector — weighted roll-up of the dimensions below)

**Dimensions** (this maps the brief's dimension list into the Renewal Optimization vector; each has score /100, weight, confidence, trend, one-line finding):

| Dimension | Weight | Score | Confidence | Trend | One-line finding (mock) |
|---|---|---|---|---|---|
| Cost Increase Risk | 22% | 41 | High | ↓ | Proposed uplift is 18% YoY, well above the 6% benchmark for this category. |
| Operational Dependency | 18% | 72 | High | → | Deeply embedded in two production workflows; switching cost is high. |
| SLA Performance | 15% | 55 | Medium | ↓ | Priority-response SLA missed 4× in Q3; avg resolution trending up. |
| Compliance Exposure | 15% | 63 | Medium | → | SOC 2 current, but security questionnaire has two open items. |
| Negotiation Leverage | 15% | 38 | Medium | ↓ | Late in the window, no benchmark pack prepared, few alternatives scoped. |
| Renewal Urgency | 15% | 30 | High | ↓ | Notice window closes in 47 days; auto-renewal clause active. |

**Weightage config (this vector):** the six weights above sum to 100%. Super Index = Σ(weight × score), with a confidence floor and an urgency override note (Renewal Urgency being critical can cap the band regardless of composite — mirrors the doc's "notice deadline overrides everything" rule). Displayed value 58 is the illustrative roll-up.

**Evidence basis** (the brief's evidence list — shown as the Review's grounding, each as an evidence chip with a source-type tag):
- Contract and amendments *(document)*
- Spend from ERP *(system)*
- Tickets from Jira / Zendesk *(system)*
- SLA history *(system)*
- Security questionnaire *(document)*
- Business-owner feedback check-in *(human)*
- Previous renewal decision *(record)*
- Open obligations *(record)*
- Market benchmark *(external)*

**Review output** (the brief's output list — the Review's synthesized result):
- **Executive summary** — 2–3 sentence plain-language verdict.
- **Renewal risk score** — the Super Index (58 / AT RISK).
- **Key evidence** — the 3–4 most decisive evidence items driving the score.
- **Recommended position** — the stance ("Renegotiate before notice window closes").
- **Counteroffer strategy** — the specific commercial ask (target uplift ≤ benchmark, seek SLA credits, reduce minimum commitment).
- **Required approvals** — who must sign off (Procurement lead, Finance, Legal for clause changes).
- **Next actions** — the concrete steps (prepare benchmark pack, issue conditional non-renewal notice, book business-owner review).

**Matters surfaced by this Review** (triggered by the weak/urgent dimensions):
- **Repricing Matter** — trigger: Cost Increase Risk 41 + Negotiation Leverage 38. Objective: bring uplift to benchmark before signature. Interventions: request benchmark, prepare negotiation brief. Owner: Procurement. Status: Open.
- **Renewal Recovery Matter** — trigger: Renewal Urgency 30 (notice closes in 47 days). Objective: protect options before auto-renewal. Interventions: issue conditional non-renewal notice. Owner: Procurement + Legal. Status: Urgent.

### 2.2 Impact Vector: Contract Risk (second worked example — proves generalization)

**Super Index: 47 / 100 · CRITICAL · Momentum: → Stuck**

Dimensions (a *different* set — this is the point: each vector defines its own):

| Dimension | Weight | Score | Confidence | Trend |
|---|---|---|---|---|
| Termination Risk | 20% | 44 | High | → |
| Liability Exposure | 20% | 39 | Medium | ↓ |
| Obligation Coverage | 20% | 52 | Medium | → |
| Auto-Renewal Exposure | 15% | 35 | High | ↓ |
| Deviation from Standard | 15% | 58 | Low | → |
| Amendment Currency | 10% | 61 | Medium | ↑ |

Matter surfaced: **Contract Evidence Matter** (liability deviation unresolved, one obligation owner unassigned).

### 2.3 Impact Vectors shown as summary cards (breadth, not built out)

Each renders as a compact card with a Super Index, band, and momentum arrow only:
- **Supplier Readiness** — 66 / HEALTHY / ↑ Improving
- **Contract Compliance** — 54 / AT RISK / → Stuck
- **Spend Optimization** — 71 / HEALTHY / ↑ Improving

---

## 3. Screen inventory

Five screens, adapted from the shared prototype's flow but re-fitted to the Kozmo model. The party (Cyberdyne) is the constant; the Impact Vector is what the user selects.

### Screen 1 — Launch Review (adapts prototype's "Upload")
The entry point. Presents the Commercial Party and lets the user pick which Review (Impact Vector) to run.
- Party header: Cyberdyne Systems Inc., type tags (Software / SaaS, Strategic tier, VND-… id).
- **"What would you like to review?"** — a grid of Impact Vector choices (Renewal Optimization, Contract Risk, Supplier Readiness, Contract Compliance, Spend Optimization), each a selectable card with a one-line description of what that lens evaluates.
- Evidence-on-file panel — shows the evidence Kozmo already holds for this party (the 9-item evidence list as chips), so the user sees what the Review will draw on.
- Primary button: **"Launch Review."**

### Screen 2 — Review in progress (adapts "Processing")
Vichara-style enquiry made visible. A spinner + a per-dimension checklist that ticks through as each dimension is "reviewed."
- Status line cycles: "Reviewing renewal timing… / utilization… / spend…"
- Checklist = the selected vector's dimensions, each flipping from ○ to ✓.
- Sub-label: "Running Vichara enquiry across 6 dimensions · grounding in 9 evidence sources."

### Screen 3 — The Review / Momentum Workspace (the main screen; adapts "Look Profile")
The core deliverable. For the selected Impact Vector:
1. **Header band** — Super Index (58/100), band (AT RISK), Momentum (↓ Declining), the Impact Vector name, party name, and a "Re-review" action. Includes a delta chip (▲/▼ vs last checkpoint) to make Momentum tangible.
2. **Executive summary** — the plain-language verdict (Review output #1).
3. **Review by dimension** — the signature grid: one card per dimension, each with score, weight, a score bar, confidence tag, trend arrow, and one-line finding. Weakest dimensions flagged "Focus." Cards are tappable → Screen 4.
4. **Recommended position + counteroffer strategy** — a two-panel current→target treatment (recommended stance and the specific commercial ask).
5. **Matters** — the consequential situations this Review surfaced, each a card: title, trigger, objective, interventions, owner, status chip (Open / Urgent).
6. **Required approvals + Next actions** — a phased action list (who signs off; what happens next, in sequence).
7. **Impact Vector switcher** — a strip letting the viewer flip to the other vectors (Contract Risk built out; the three summary cards) — this is what makes it a *Momentum Workspace*, not a single report.

### Screen 4 — Dimension detail (adapts "Dimension detail")
Drill into one dimension (e.g. Cost Increase Risk).
- Dimension name + score + score bar.
- **What we found** — the finding (the interpreted state).
- **Why it matters** — the commercial rationale.
- **Evidence** — the specific evidence items this dimension's index rests on (evidence chips with source tags), making the "grounded, not generated" story visible.
- **Confidence & freshness** — a small readout (e.g. "Medium confidence · benchmark 40 days old").
- **Recommended actions** — the per-dimension next steps.
- Prev/next dimension navigation.

### Screen 5 — Momentum & history (adapts "History")
Shows Momentum as a first-class idea over time.
- Super Index over time — a line chart (e.g. 68 → 61 → 58 across three checkpoints), making "Momentum is a derivative" literal.
- **Review timeline** — past Reviews (checkpoints) with date, index, and a one-line note.
- **Compare Reviews** — dimension-by-dimension deltas between two checkpoints (which dimensions moved, up/down), plus a short narrative.
- **Matters status over time** — which matters opened/closed and outcomes.

---

## 4. Visual design

### 4.1 Design direction — deliberately NOT the cream/terracotta default

The shared `Image_Review_Prototype` used the warm-cream + brown look (`#F5F3EE` / `#8A5A2B`). That is a beautiful *consumer* aesthetic — right for a personal-image product, wrong for Kozmo. Kozmo is **enterprise commercial intelligence for procurement/finance/legal** — the audience is investors and enterprise buyers. It should read as *serious, precise, institutional* — closer to a Bloomberg terminal or a McKinsey exhibit than a lifestyle brand. And per the design guidance, the cream/terracotta look is a known AI-default "tell" we should avoid on a fresh brief.

**Direction: "Commercial instrument."** A cool, near-neutral canvas with ink-dark structure, a single confident accent for signal, and semantic status colors used sparingly and meaningfully (a score's color *means* something — band severity — never decoration). Precision over warmth. Data-forward, calm, dense but legible.

### 4.2 Palette (6 named values + semantic status set)

```
--canvas      #F4F5F7   /* cool off-white page background — institutional, not cream */
--surface     #FFFFFF   /* card surface */
--ink         #14171F   /* near-black primary text / structural elements */
--muted       #5B6270   /* secondary text, labels */
--line        #E3E6EB   /* hairline borders, dividers */
--accent      #1F5FA6   /* the single confident accent — a deep, serious blue (trust/finance),
                            used for the Super Index, active states, links, focus rings */

/* Semantic status — used ONLY to carry meaning (band severity, trend), never for decoration */
--critical    #B23A48   /* CRITICAL band, negative trend */
--atrisk      #C77D24   /* AT RISK band (amber-ochre) */
--healthy     #2E7D5B   /* HEALTHY band, positive trend */
--strategic   #1F5FA6   /* STRATEGIC (reuses accent blue) */
--critical-bg #F7E9EB   --atrisk-bg #F7EFDF   --healthy-bg #E4F0EA   /* tint fills for chips */
```

Rationale: deep blue accent = the "serious finance instrument" signal, and it is *not* terracotta. Amber for AT_RISK and a restrained brick-red for CRITICAL are semantic, so a viewer reads band severity by color instantly — that is meaning, not decoration. The cool canvas separates it firmly from the consumer prototype.

### 4.3 Typography — deliberate pairing, not defaults

```
Display / headline:  "Fraunces" (a characterful high-contrast serif — used with restraint for
                      the Super Index number and section headings; gives gravitas without the
                      generic Newsreader look). Weights 400 / 600.
Body / UI:           "Inter" (clean, neutral, excellent for dense data and labels). 400 / 500 / 600.
Data / numerals:     Inter with tabular-nums (font-feature-settings:'tnum') for all scores, weights,
                      and deltas so columns align and numbers feel instrument-grade.
```

Type scale (px): 64 (Super Index number) / 32 (screen title) / 22 (section heading) / 16 (body) / 14 (card title) / 13 (secondary) / 11 (eyebrow labels, uppercase, letter-spacing .08em).

Rationale: Fraunces gives an editorial-but-institutional display voice distinct from the prototype's Newsreader; Inter + tabular numerals makes the data read as precise instrumentation. The display face is spent only on the headline number and section heads — restraint per the guidance.

### 4.4 The signature element

**The Super Index "instrument" header.** The one memorable device: for each Impact Vector, a header that combines (a) the large Fraunces score, (b) a semantic band label, (c) a compact **Momentum sparkline** (the last 3–4 checkpoint values as a tiny inline line), and (d) a **dimension-contribution bar** — a single horizontal stacked bar showing how each weighted dimension contributes to (and drags on) the Super Index, segments colored by the dimension's own band. This one element encodes the entire model — dimensions → weighted roll-up → Super Index → momentum — in a single glance. Everything else on the page stays quiet so this instrument is the thing people remember.

ASCII sketch of the signature header:

```
┌──────────────────────────────────────────────────────────────────────┐
│ RENEWAL OPTIMIZATION · REVIEW                          [ Re-review ]   │
│                                                                        │
│   58        AT RISK   ↓ Declining   ▁▃▂▁  (68→61→58)                    │
│  /100       ── Super Index ──                                          │
│                                                                        │
│  Contribution  ▓▓▓▓▓░░░▓▓▓▓░░░░▓▓░░░  ← stacked, per-dimension,         │
│                cost·op·sla·comp·lev·urg   colored by each band         │
└──────────────────────────────────────────────────────────────────────┘
```

### 4.5 Layout

- Max content width ~1080px, centered, generous vertical rhythm (56px between major sections).
- Cards: `--surface` on `--canvas`, 1px `--line` border, 12px radius, 20–24px padding. Hover: border shifts to `--accent`.
- Dimension grid: 2 columns desktop, 1 column mobile.
- Matter cards: full-width stacked, with a colored left-border by status (Urgent = `--critical`, Open = `--accent`).
- A persistent **"Design prototype — illustrative data"** ribbon/badge (top-right, subtle but always visible) so it is never mistaken for live product.
- Top bar: "KOZMO" wordmark (Inter, letter-spacing .14em, uppercase), party name, and Review/Momentum nav pills.

### 4.6 Motion (restrained)

- Screen transitions: 0.4s fade-up (`translateY(8px)`→0), matching the prototype's calm feel.
- Screen 2 checklist: dimensions tick in sequence (~500ms each) — this animation *is* the "Vichara enquiry" made visible, so it earns its place.
- Score bars and the contribution bar: animate width from 0 on first paint of Screen 3 (0.6s ease-out).
- Respect `prefers-reduced-motion`: disable the fills' animation and the fade-ups.
- No other motion — per the guidance, extra animation reads as AI-generated.

### 4.7 Quality floor

Responsive to mobile (single-column, stacked header), visible keyboard focus (2px `--accent` ring), reduced-motion respected, semantic status never conveyed by color alone (always paired with a text label or arrow glyph so it survives color-blindness and grayscale printing — important for an exhibit that may be printed).

---

## 5. Data structure (mock, in-file)

A single JS object drives everything, so a viewer/editor can tweak the story without touching markup. Shape:

```js
const PARTY = {
  name: "Cyberdyne Systems Inc.",
  tags: ["Software / SaaS", "Strategic tier", "VND-004431"],
  evidence: [
    { label: "Contract and amendments", type: "document" },
    { label: "Spend from ERP", type: "system" },
    { label: "Tickets from Jira / Zendesk", type: "system" },
    { label: "SLA history", type: "system" },
    { label: "Security questionnaire", type: "document" },
    { label: "Business-owner feedback check-in", type: "human" },
    { label: "Previous renewal decision", type: "record" },
    { label: "Open obligations", type: "record" },
    { label: "Market benchmark", type: "external" }
  ]
};

const VECTORS = [
  {
    id: "renewal", name: "Renewal Optimization",
    description: "Is this renewal heading toward a good commercial outcome?",
    superIndex: 58, band: "AT_RISK", momentum: "declining",
    history: [68, 61, 58],
    executiveSummary: "…",
    recommendedPosition: "Renegotiate before the notice window closes.",
    counteroffer: "…", approvals: ["Procurement lead", "Finance", "Legal (clause changes)"],
    nextActions: [ {phase:"This week", items:[…]}, … ],
    dimensions: [
      { name:"Cost Increase Risk", weight:22, score:41, confidence:"High", trend:"down",
        finding:"…", why:"…", evidence:["Market benchmark","Spend from ERP","Contract and amendments"],
        actions:[…], freshness:"benchmark 40 days old" },
      … (6 total)
    ],
    matters: [
      { title:"Repricing Matter", trigger:"Cost Increase Risk 41 + Negotiation Leverage 38",
        objective:"…", interventions:[…], owner:"Procurement", status:"Open" },
      { title:"Renewal Recovery Matter", …, status:"Urgent" }
    ]
  },
  { id:"contractRisk", name:"Contract Risk", …fully built… },
  { id:"supplierReadiness", name:"Supplier Readiness", summaryOnly:true, superIndex:66, band:"HEALTHY", momentum:"improving" },
  { id:"contractCompliance", summaryOnly:true, … },
  { id:"spendOptimization", summaryOnly:true, … }
];
```

Bands: `CRITICAL | AT_RISK | HEALTHY | STRATEGIC`. Trends/momentum: `up | down | flat` → arrows ↑ ↓ →. Confidence: `High | Medium | Low`.

---

## 6. Build plan (phased, so it's reviewable)

1. **Scaffold + tokens** — single HTML file, the palette/type CSS variables, top bar, the "Design prototype" badge, the screen-switching JS shell (5 screens, fade-up transitions).
2. **Screen 3 first (the payoff)** — the Review / Momentum Workspace for Renewal Optimization: the signature Super-Index instrument header (score + band + sparkline + contribution bar), executive summary, the dimension grid, recommended-position/counteroffer, matters, approvals/next-actions, and the vector switcher. This is the screen that sells the vision; build and screenshot it first.
3. **Screen 4** — dimension detail (found / why / evidence / confidence / actions), wired to the same mock object, prev/next.
4. **Screen 1 + 2** — Launch Review (party + vector picker + evidence-on-file) and the Vichara processing animation.
5. **Screen 5** — Momentum & history (index line chart, review timeline, compare deltas).
6. **Second vector** — flesh out Contract Risk so the switcher proves the model generalizes; wire the three summary cards.
7. **Polish pass** — responsive/mobile, focus states, reduced-motion, grayscale-safe status, screenshot critique, remove one accessory.

Deliverable at the end of step 2 is already demo-worthy (the core screen); steps 3–7 complete the flow.

---

## 7. What this prototype deliberately does NOT do

- Not wired to the live pipeline; no real beliefs, no real index, no real check-ins. (That's Path B, E2-gated.)
- Does not claim the backend has an Impact Vector registry, per-vector dimensions, per-vector weight configs, Momentum checkpoints, or Matters — these are the vision E2 is building toward. The prototype *shows* them; it does not assert they are live.
- Does not reuse the consumer cream/terracotta aesthetic — deliberately re-themed for an enterprise/investor audience.
- The "Design prototype — illustrative data" marker stays visible throughout so the artifact is honest about being a vision piece.
