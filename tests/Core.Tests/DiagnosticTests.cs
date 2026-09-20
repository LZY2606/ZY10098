using PairwiseGsb.Core;
using PairwiseGsb.Core.Matcher;

namespace Core.Tests;

public sealed class DiagnosticTests
{
    private readonly RuleSet rules = DefaultRules.Create();

    [Fact]
    public void Power_short_is_precomparison_diagnostic()
    {
        var expanded = new NetlistExpander(rules).Expand(TestData.PowerShort());
        Assert.Contains(expanded.Diagnostics, d => d.Code == "power-short");
        var result = new SemanticMatcher(rules).Search(expanded, new NetlistExpander(rules).Expand(TestData.PowerShort()));

        Assert.Equal(SessionStatus.DiagnosticFailure, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "power-short");
    }

    [Fact]
    public void Dangling_port_is_precomparison_diagnostic()
    {
        var expanded = new NetlistExpander(rules).Expand(TestData.Dangling());

        Assert.Contains(expanded.Diagnostics, diagnostic => diagnostic.Code == "dangling-port");
    }

    [Fact]
    public void Hierarchy_reference_cycle_is_diagnostic()
    {
        var document = new NetlistDocument
        {
            Name = "cycle",
            Modules =
            [
                new("a", "A", [], [], [new("b", "B", "a", new Dictionary<string, string>())]),
                new("b", "B", [], [], [new("a", "A", "b", new Dictionary<string, string>())])
            ],
            Instances = [new("root", "ROOT", "a", new Dictionary<string, string>())]
        };

        var expanded = new NetlistExpander(rules).Expand(document);

        Assert.Contains(expanded.Diagnostics, diagnostic => diagnostic.Code == "hierarchy-cycle");
    }

    [Fact]
    public void Incompatible_parameter_unit_is_diagnostic()
    {
        var document = TestData.WithParameter(TestData.Simple(), new ParameterValue("R", 1000, "F", null));

        var expanded = new NetlistExpander(rules).Expand(document);

        Assert.Contains(expanded.Diagnostics, diagnostic => diagnostic.Code == "parameter-unit-mismatch");
    }
}
