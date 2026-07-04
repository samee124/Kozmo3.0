using System.Security.Cryptography;
using System.Text;
using Ii.CandidateExtraction;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// E1 Part 7 Step 2 — proves BeliefExtractionPrompt.BuildSystem(catalogue) reproduces the original
/// hand-authored System prompt constant byte-for-byte, OFFLINE, before any cassette re-record is
/// even considered. If this test is green, the generated prompt hashes to the same cassette cache
/// key as before (CachingLlmClient.ComputeKey includes the full system prompt string), so the
/// existing fixtures/kyv/belief-extraction.cassette.json entries stay valid with zero live LLM
/// calls spent.
/// <para>
/// OriginalSystemPrompt below is a verbatim copy of the pre-Step-2 BeliefExtractionPrompt.System
/// constant (see git history prior to the E1 Part 7 Step 2 commit) — not a re-derivation. Any
/// future catalogue edit that changes a target key's prompt_fragment will legitimately break this
/// test; that is the intended signal that a cassette re-record is required.
/// </para>
/// </summary>
public sealed class BeliefExtractionPromptGenerationTests
{
    private const string OriginalSystemPrompt = """
        You are a strict commercial-intelligence fact extractor. Extract ONLY the five facts
        listed below, and ONLY when a value is EXPLICITLY stated in the document text. Do NOT
        infer, estimate, approximate, or use outside knowledge.

        FACTS (criterion key -> what to look for -> raw value encoding):
        - sla_uptime: an explicit committed or reported uptime/SLA percentage
          (e.g. "99.9% uptime"). value = the percentage number as given (e.g. 99.9),
          NOT divided by 100.
        - csat: an explicit CUSTOMER SATISFACTION score — from a customer survey, CSAT/NPS
          program, or similar sentiment measure of the customer's satisfaction with the VENDOR
          RELATIONSHIP — on a 1.0-5.0 rating scale ONLY (e.g. "Customer satisfaction survey:
          4.6 out of 5.0", "CSAT rating: 4.2/5"). value = the score exactly as given (e.g. 4.6).
          Do NOT extract a CSAT figure given as a percentage or on a 0-100 scale (e.g. "CSAT: 92%")
          — that is a different scale this fact does not cover; omit it. Do NOT extract a "study
          quality score", "product quality score", or any other operational/QA/technical quality
          metric that is not explicitly about the customer's sentiment toward the vendor
          relationship — a quality score on a study, deliverable, or product is NOT a customer
          satisfaction score, even when it shares the same 1.0-5.0 shape (e.g. "Study quality
          scores averaged 4.6 out of 5.0" is a QA metric, not CSAT — omit it).
        - payment_terms: the invoice payment due period ONLY (e.g. "due within 30 days of
          invoice"). value = integer days (e.g. 30). Do NOT use insurance, cancellation, or
          termination notice periods for this fact.
        - renewal_date: an explicit CALENDAR date the agreement renews or is next due for
          renewal (e.g. "renews on September 1, 2026"). value = that date formatted as the
          STRING "YYYY-MM-DD" (e.g. "2026-09-01") — a plain calendar date, NOT a Unix timestamp;
          do not attempt to compute a timestamp yourself, the conversion happens deterministically
          downstream. An auto-renewal clause with no specific date is a RULE, not a date — omit
          renewal_date unless a concrete calendar date is stated.
        - annual_value: an explicit contract price or subscription fee paid by the customer.
          value = the dollar amount as a plain number (e.g. 250000 for "$250,000/year").
          Insurance requirements, liability caps, and indemnification ceilings are NOT fees.

        ABSTENTION IS MANDATORY: if a fact is not explicitly present in the document, DO NOT
        include it in the output — omit it entirely. Never invent, guess, or infer a value that
        is not stated. If NONE of the five facts appear anywhere in the document, return an
        empty "facts" array. An empty array is a correct, expected answer for many documents
        (e.g. marketing brochures, meeting notes) — it is not a failure.

        Every fact you DO include must carry the exact quoted span of text it was drawn from, so
        the claim can be checked against the source.

        Return JSON with this exact shape — no markdown fences, just the JSON object:
        {
          "facts": [
            {
              "criterion": "<sla_uptime|csat|payment_terms|renewal_date|annual_value>",
              "value": <number, EXCEPT renewal_date which is the string "YYYY-MM-DD">,
              "evidence": "<exact quoted span from the document text>",
              "confidence": <float 0.0-1.0>
            }
          ],
          "confidence": <float 0.0-1.0>,
          "reasoning": "<one sentence>"
        }
        """;

    [Fact]
    public void BuildSystem_ReproducesOriginalPrompt_StringIdentical()
    {
        var profile   = TestHelpers.LoadProfile();
        var generated = BeliefExtractionPrompt.BuildSystem(
            profile.ClaimKeyCatalogue, BeliefExtractionPrompt.TargetCriteriaOrder);

        Assert.Equal(OriginalSystemPrompt, generated);
    }

    [Fact]
    public void BuildSystem_ReproducesOriginalPrompt_HashIdentical()
    {
        var profile   = TestHelpers.LoadProfile();
        var generated = BeliefExtractionPrompt.BuildSystem(
            profile.ClaimKeyCatalogue, BeliefExtractionPrompt.TargetCriteriaOrder);

        var originalHash  = Sha256Hex(OriginalSystemPrompt);
        var generatedHash = Sha256Hex(generated);

        Assert.Equal(originalHash, generatedHash);
    }

    private static string Sha256Hex(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
