using Ig.Contracts;

namespace Ig.Resolution;

/// <summary>
/// Stage B — Entity-type classification. Deterministic rules first; LLM only for
/// genuinely ambiguous candidates that survive all rule checks as UNKNOWN.
/// PERSON / INTERNAL / PRODUCT / NON_VENDOR are dropped with a recorded reason.
/// COMPANY and UNKNOWN proceed to Stage C.
/// </summary>
public sealed class EntityTypeClassificationStage
{
    private readonly IEntityTypeClassifier _llm;

    // Words whose presence in a name strongly indicates an organised company/firm
    private static readonly HashSet<string> _companyIndicators = new(StringComparer.OrdinalIgnoreCase)
    {
        "consulting", "solutions", "technologies", "technology", "systems", "group",
        "holdings", "international", "services", "enterprises", "industries",
        "associates", "partners", "ventures", "labs", "laboratory", "laboratory",
        "studio", "studios", "works", "digital", "global", "cloud", "software",
        "hardware", "media", "health", "capital", "resources", "network", "networks",
        "logistics", "management", "agency", "firm", "advisors", "advisory",
    };

    // Words that denote an internal organisational unit (standalone or in combination)
    private static readonly HashSet<string> _internalKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "procurement", "it", "finance", "hr", "legal", "accounting", "operations",
        "payroll", "facilities", "administration", "management", "compliance",
        "security", "marketing", "sales", "engineering", "helpdesk", "help desk",
        "treasury", "audit", "tax", "purchasing",
    };

    // Legal suffix words — presence rules out PERSON classification
    private static readonly HashSet<string> _legalSuffixWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "inc", "llc", "ltd", "limited", "gmbh", "corp", "co", "sa", "bv", "ag", "plc",
    };

    // Common Western given names used to gate the PERSON rule.
    // Intentionally conservative: false-negative (miss a person) is better than
    // false-positive (drop a company named "Blue Salt" as if it were a person).
    private static readonly HashSet<string> _commonGivenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "james","john","robert","michael","william","david","richard","joseph","thomas",
        "charles","christopher","daniel","matthew","anthony","mark","donald","steven",
        "paul","andrew","kenneth","joshua","kevin","brian","george","timothy","ronald",
        "edward","jason","jeffrey","ryan","jacob","gary","nicholas","eric","jonathan",
        "stephen","larry","justin","scott","brandon","benjamin","samuel","raymond",
        "frank","gregory","raymond","patrick","alexander","jack","dennis","jerry",
        "mary","patricia","jennifer","linda","barbara","elizabeth","susan","jessica",
        "sarah","karen","lisa","nancy","betty","margaret","sandra","ashley","dorothy",
        "kimberly","emily","donna","michelle","carol","amanda","melissa","deborah",
        "stephanie","sharon","laura","cynthia","kathleen","amy","angela","shirley",
        "anna","brenda","pamela","emma","nicole","helen","samantha","katherine",
        "christine","debra","rachel","carolyn","janet","catherine","virginia","maria",
        "heather","diane","julie","joyce","victoria","kelly","christina","lauren",
        "joan","evelyn","olivia","judith","megan","cheryl","andrea","hannah","martha",
        "jacqueline","ann","gloria","jean","kathryn","alice","teresa","sophie","jane",
        "lucy","amy","grace","alice",
    };

    public EntityTypeClassificationStage(IEntityTypeClassifier llm) => _llm = llm;

    public async Task<ClassifiedCandidate> ClassifyAsync(
        NormalizedCandidate normalized,
        CancellationToken   ct = default)
    {
        var entityType = DetermineByRules(normalized.EffectiveName);

        if (entityType == EntityType.Unknown)
        {
            // LLM fallback: only for names rules cannot classify
            entityType = await _llm.ClassifyAsync(
                normalized.EffectiveName, normalized.ComparisonKey, ct);
        }

        var (dropped, reason) = DropDecision(entityType, normalized.EffectiveName);
        return new ClassifiedCandidate(normalized, entityType, dropped, reason);
    }

    // ── Deterministic rule engine ──────────────────────────────────────────────

    private static EntityType DetermineByRules(string effectiveName)
    {
        if (string.IsNullOrWhiteSpace(effectiveName))
            return EntityType.NonVendor;

        var words = effectiveName.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // INTERNAL: all words are internal-department keywords
        if (words.Length > 0 && words.All(w => _internalKeywords.Contains(w)))
            return EntityType.Internal;

        // PERSON: 2–3 title-case, all-letter words; no legal suffixes or company indicators
        if (IsPerson(effectiveName, words))
            return EntityType.Person;

        // COMPANY: any legal-entity designator confirms a registered company.
        // This catches multi-word names like "Cloud Wave Inc." where no _companyIndicators
        // word is present but the legal suffix alone is sufficient evidence.
        if (words.Any(w => _legalSuffixWords.Contains(w.TrimEnd('.'))))
            return EntityType.Company;

        // COMPANY: any company-indicator word present
        if (words.Any(w => _companyIndicators.Contains(w)))
            return EntityType.Company;

        // Single-word names with no indicator → COMPANY by conservative default.
        // Multi-word names with no matching indicator → UNKNOWN (→ LLM fallback).
        return words.Length == 1 ? EntityType.Company : EntityType.Unknown;
    }

    private static bool IsPerson(string effectiveName, string[] words)
    {
        if (words.Length < 2 || words.Length > 3) return false;

        // Disqualify if any word is a legal suffix or company/internal keyword
        foreach (var w in words)
        {
            var bare = w.TrimEnd('.');
            if (_legalSuffixWords.Contains(bare))   return false;
            if (_companyIndicators.Contains(bare))  return false;
            if (_internalKeywords.Contains(bare))   return false;
        }

        // Every word must start with an uppercase letter and contain only letters (+ optional trailing ".")
        foreach (var w in words)
        {
            var bare = w.TrimEnd('.');
            if (bare.Length == 0)              return false;
            if (!char.IsUpper(bare[0]))        return false;
            if (!bare.All(char.IsLetter))      return false;
        }

        // Require the first word to be a recognised Western given name.
        // This prevents two-word company names like "Blue Salt" or "Red Hawk" from
        // being misclassified as persons: the title-case pattern alone is too broad.
        return _commonGivenNames.Contains(words[0].TrimEnd('.'));
    }

    private static (bool dropped, string? reason) DropDecision(EntityType type, string name) =>
        type switch
        {
            EntityType.Person    => (true, $"Natural person (non-vendor): '{name}'"),
            EntityType.Internal  => (true, $"Internal department (non-vendor): '{name}'"),
            EntityType.Product   => (true, $"Product entity (not a vendor): '{name}'"),
            EntityType.NonVendor => (true, $"Non-vendor entity (document artifact): '{name}'"),
            _                    => (false, null),
        };
}
