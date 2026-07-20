namespace Rm.Contracts;

/// <summary>
/// Channel-agnostic output from the Reality-Model Query Service.
/// Text is the prose answer (LLM-phrased or deterministic template fallback).
/// Grounding is the structured data the text was composed from — callers and tests use
/// it to verify no fact in Text drifted beyond what was retrieved.
/// Grounding is null only when the vendor could not be identified (no context to retrieve).
/// </summary>
public sealed record VendorQueryAnswer(string Text, RetrievedContext? Grounding);
