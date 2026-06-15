# STEP 0.8 — CI Invariant Lanes

**Goal:** wire the five invariants as build-blocking checks so they're enforced mechanically, not
by review. This is the last item before the Phase 1 fan-out gate.

**Principle:** prefer **compile-time** enforcement (the build fails) over tests where possible;
use **architecture tests** for dependency/shape rules. Every lane must be runnable from one
command and wired as a required PR status check.

> Write lane 5 to the **Step 1.0 reframed** invariant — *no live network in the demo runtime
> path; LLM from cache; real client only at seed-prep/smoke* — **not** the current "zero LLM
> calls anywhere." Otherwise it blocks Phase 1's `CachingLlmClient` and gets redone.

---

## Artifacts to create

1. **`tests/Kozmo.Architecture.Tests/`** — xUnit project, references `NetArchTest.Rules`, all
   subsystem assemblies, `Km.Store`, `Kozmo.Contracts`. All tests tagged `[Trait("Category","Invariant")]`.
2. **`Microsoft.CodeAnalysis.BannedApiAnalyzers`** package + a **`BannedSymbols.txt`** in the
   projects named below (compile-time enforcement for lanes 3 and 5b).
3. **`ci/check-invariants.sh`** — the single gating command (build + invariant tests).

csproj wiring for any project that gets a banned list:

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" Version="3.3.4" PrivateAssets="all" />
  <AdditionalFiles Include="BannedSymbols.txt" />
</ItemGroup>
```

---

## Lane 1 — Pipeline direction (no cross-module internal imports)

- **Checks:** the five module assemblies (`Ii.Observation`, `Ii.Rubric`, `Ii.Index`,
  `Ii.Posture`, `Ii.Decay`) do not depend on each other. They may depend only on
  `Ii.Contracts`, `Kozmo.Contracts`, `Kozmo.Platform`, `Kozmo.Llm` (interface). Only `Ii.Spine`
  may depend on all modules.
- **Mechanism:** NetArchTest, in `Kozmo.Architecture.Tests`.
- **Pass/fail:** fail if any module references another module's namespace.

```csharp
[Fact, Trait("Category","Invariant")]
public void Modules_do_not_reference_each_other() {
    var modules = new[]{ "Ii.Observation","Ii.Rubric","Ii.Index","Ii.Posture","Ii.Decay" };
    foreach (var m in modules) {
        var result = Types.InAssembly(Assembly.Load(m))
            .ShouldNot().HaveDependencyOnAny(modules.Where(x => x != m).ToArray())
            .GetResult();
        Assert.True(result.IsSuccessful,
            $"{m} → {string.Join(",", result.FailingTypeNames ?? new List<string>())}");
    }
}
```

---

## Lane 2 — Belief immutability (append-and-supersede only)

- **Checks:** (a) `Belief` (and `Observation`, `PostureAssignment`) have no non-init setters;
  (b) `IEntityStore` exposes no belief-mutation method (`Update/Edit/Modify/Delete/Remove` of a
  belief) — only append, supersede, and read.
- **Mechanism:** reflection test.
- **Pass/fail:** fail on any settable belief property or any belief-mutation method.

```csharp
[Fact, Trait("Category","Invariant")]
public void Belief_has_no_mutable_setters() {
    foreach (var p in typeof(Belief).GetProperties()) {
        var isInitOnly = p.SetMethod?.ReturnParameter
            .GetRequiredCustomModifiers().Any(t => t.Name == "IsExternalInit") ?? true;
        Assert.True(p.SetMethod is null || isInitOnly, $"Belief.{p.Name} is mutable");
    }
}

[Fact, Trait("Category","Invariant")]
public void EntityStore_has_no_belief_mutation() {
    var banned = new[]{ "Update","Edit","Modify","Delete","Remove" };
    foreach (var mi in typeof(IEntityStore).GetMethods())
        Assert.False(banned.Any(b => mi.Name.Contains(b)) && mi.Name.Contains("Belief"),
            $"IEntityStore.{mi.Name} looks like a belief mutation");
}
```

---

## Lane 3 — Determinism (no clock, no randomness in the deterministic modules)

- **Checks:** no `DateTime.UtcNow` / `DateTimeOffset.UtcNow` / `DateTime.Now` / `Random` /
  `Stopwatch` in `Ii.Observation`, `Ii.Rubric`, `Ii.Index`, `Ii.Posture`, `Ii.Decay`. Time is
  injected by `Ii.Spine` (the sole clock reader). The existing determinism spike already proves
  this at runtime; this lane prevents regressions at **compile time**.
- **Mechanism:** `BannedApiAnalyzers` + `BannedSymbols.txt` added to the five module projects
  (not `Ii.Spine`). Build fails on use.
- **Pass/fail:** build error on any banned symbol.

`BannedSymbols.txt` (the five module projects):

```
P:System.DateTime.UtcNow;Inject time via Spine — modules are clock-free (determinism invariant).
P:System.DateTimeOffset.UtcNow;Inject time via Spine.
P:System.DateTime.Now;Inject time via Spine.
T:System.Random;No randomness in deterministic modules.
T:System.Diagnostics.Stopwatch;No wall-clock in deterministic modules.
```

> Review note: if any deterministic module uses `Guid.NewGuid()` for IDs that feed the
> fingerprint, that is also non-deterministic — IDs in the deterministic path should be derived
> deterministically or assigned by Spine. Add `M:System.Guid.NewGuid` to the list if so.

---

## Lane 4 — Confidence discipline

- **Checks (static, over config):** for every source tier, `weight ≤ 1.0`; and the structural
  guarantee `REPORTED.weight (0.50) < CRITICAL confidence gate (0.60)`.
- **Checks (runtime, over the pipeline):** every produced belief has `confidence ≤ tier_weight`;
  every posture has `confidence ≤ 0.95`; the index `confidence_floor` equals the minimum of its
  contributing belief confidences.
- **Mechanism:** two tests in `Kozmo.Architecture.Tests` — one loads the catalogue and asserts
  the arithmetic; one runs the golden fixtures (and a few generated inputs) through `IIiFacade`
  and asserts the ceilings.
- **Pass/fail:** fail if config violates the ordering, or any belief/posture exceeds its ceiling.

```csharp
[Fact, Trait("Category","Invariant")]
public void Reported_tier_is_below_critical_gate() {
    var cat = Catalogue.Load(CataloguePath);
    Assert.True(cat.SourceTiers["REPORTED"].Weight < cat.Bands.CriticalConfidenceGate,
        "REPORTED tier must be structurally below the CRITICAL gate");
}
```

---

## Lane 5 — No live dependency in the demo runtime path (Step 1.0 reframed)

- **Checks (5a, reference boundary):** the demo-runtime assemblies — `Ii.Spine`, the five
  modules, `Km.Store` — must **not** reference `System.Net.Http` or the real network LLM client
  assembly. They may reference `Kozmo.Llm` (the `IKozmoLlm` interface) and the cache
  implementation only.
- **Checks (5b, banned API):** ban `System.Net.Http.HttpClient` and the `Anthropic` SDK
  namespace in those projects.
- **Structural requirement this imposes on Phase 1 (Dev B):** the real `LlmClient` must live in
  its **own assembly** (`Kozmo.Llm.Anthropic`), referenced only by the seed-prep and smoke
  entrypoints — never by the demo runtime. `CachingLlmClient` (no network) lives in `Kozmo.Llm`.
- **Mechanism:** NetArchTest (5a) + `BannedApiAnalyzers` (5b).
- **Pass/fail:** fail if a demo-runtime assembly references `Kozmo.Llm.Anthropic` /
  `System.Net.Http`, or uses `HttpClient` / `Anthropic.*`.
- **Note:** until Phase 1 adds the clients, this trivially passes; it starts enforcing the moment
  Dev B adds the real client. Write it forward now.
- **Phase 1 companion (Dev B, not 0.8):** a unit test that `CachingLlmClient` **throws on a cache
  miss in demo mode** (a miss must never fall through to the network).

```csharp
[Fact, Trait("Category","Invariant")]
public void Demo_runtime_has_no_network_or_real_llm_dependency() {
    var demo = new[]{ "Ii.Spine","Ii.Observation","Ii.Rubric","Ii.Index","Ii.Posture","Ii.Decay","Km.Store" };
    foreach (var asm in demo) {
        var result = Types.InAssembly(Assembly.Load(asm))
            .ShouldNot().HaveDependencyOnAny("Kozmo.Llm.Anthropic","System.Net.Http")
            .GetResult();
        Assert.True(result.IsSuccessful, $"{asm} → live dependency: " +
            string.Join(",", result.FailingTypeNames ?? new List<string>()));
    }
}
```

`BannedSymbols.txt` (demo-runtime projects):

```
T:System.Net.Http.HttpClient;No outbound HTTP in the demo runtime (no-live-dependency invariant).
N:Anthropic;Real model client lives in Kozmo.Llm.Anthropic, reachable from seed-prep/smoke only.
```

---

## Where it hooks in CI

`ci/check-invariants.sh` — one gating command:

```bash
#!/usr/bin/env bash
set -euo pipefail
# Lanes 3 + 5b: BannedApiAnalyzers report as errors → build fails.
dotnet build Kozmo.sln -c Release -warnaserror
# Lanes 1, 2, 4, 5a: architecture + invariant tests.
dotnet test tests/Kozmo.Architecture.Tests -c Release --filter "Category=Invariant"
```

Wire this as a **required status check** on every PR. Merge is blocked if it fails. (The existing
16 Phase 0 tests run in the normal test job; the determinism spike there is the runtime proof
that complements lane 3's compile-time guard.)

---

## Definition of done for Step 0.8

- [ ] `Kozmo.Architecture.Tests` project added; lanes 1, 2, 4, 5a implemented and green.
- [ ] `BannedApiAnalyzers` + `BannedSymbols.txt` added to the five module projects (lane 3) and the demo-runtime projects (lane 5b); build still clean.
- [ ] `ci/check-invariants.sh` runs build + invariant tests and is wired as a required PR check.
- [ ] All five lanes verified to **fail** on a deliberate violation (introduce a `DateTime.UtcNow` in `Ii.Rubric`, a cross-module import, an `HttpClient` in `Ii.Spine`) — then revert. A lane that never fails isn't enforcing anything.

When this is green and a deliberate violation is confirmed to block, the **fan-out gate is met** —
Dev A and Dev B begin Phase 1.
