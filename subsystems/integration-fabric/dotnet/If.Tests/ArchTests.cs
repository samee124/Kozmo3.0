using System.Reflection;
using If.Contracts;
using NetArchTest.Rules;
using Xunit;

namespace If.Tests;

/// <summary>
/// Architecture invariant: If.Contracts must never reference Microsoft.Graph or any Microsoft SDK.
/// </summary>
public sealed class ArchTests
{
    private static readonly Assembly ContractsAsm = typeof(ICalendarSource).Assembly;

    [Fact]
    public void IfContracts_has_no_MicrosoftGraph_dependency()
    {
        var result = Types.InAssembly(ContractsAsm)
            .ShouldNot().HaveDependencyOnAny("Microsoft.Graph")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "If.Contracts must not reference Microsoft.Graph — it is integration-neutral. " +
            "Failing types: " + string.Join(", ", result.FailingTypeNames ?? new List<string>()));
    }

    [Fact]
    public void IfContracts_referenced_assemblies_contain_no_MicrosoftGraph()
    {
        var violations = ContractsAsm
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .Where(n => n.StartsWith("Microsoft.Graph", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(violations.Count == 0,
            "If.Contracts assembly references Microsoft.Graph — it must remain SDK-free. " +
            "Offending references: " + string.Join(", ", violations));
    }
}
