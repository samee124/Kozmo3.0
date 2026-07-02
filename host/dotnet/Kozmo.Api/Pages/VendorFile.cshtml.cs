using Ii.Contracts;
using Ii.Spine;
using Km.Store;
using Kozmo.Contracts;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Kozmo.Api.Pages;

public sealed class VendorFileModel : PageModel
{
    private readonly IIiFacade         _facade;
    private readonly SqliteEntityStore _store;
    private readonly EntityRegistry    _registry;

    public string          VendorName  { get; private set; } = "";
    public DateTimeOffset  AsOf        { get; private set; } = DemoClock.AsOf;
    public VendorJudgement? Judgement  { get; private set; }
    public IReadOnlyList<Belief>   Beliefs  { get; private set; } = [];
    public IReadOnlyList<Evidence> Evidence { get; private set; } = [];

    public VendorFileModel(
        IIiFacade          facade,
        SqliteEntityStore  store,
        EntityRegistry     registry)
    {
        _facade   = facade;
        _store    = store;
        _registry = registry;
    }

    public async Task OnGetAsync(string id)
    {
        if (!Guid.TryParse(id, out var vendorId)) return;

        VendorName = _registry.GetEntity(vendorId)?.CanonicalName ?? id;
        AsOf       = DemoClock.AsOf;

        Evidence = await _store.GetEvidenceForVendorAsync(vendorId);
        if (Evidence.Count == 0) return;

        var raw = await _store.GetCurrentBeliefsAsync(vendorId);
        Beliefs = raw.Where(b => !string.IsNullOrEmpty(b.ClaimKey)).ToList();
        if (Beliefs.Count == 0) return;

        Judgement = await _facade.RecomputeVendorAsync(vendorId);
    }
}
