using PairwiseGsb.Core;
using PairwiseGsb.Core.Matcher;

namespace Core.Tests;

public sealed class MatcherTests
{
    private readonly RuleSet rules = DefaultRules.Create();

    [Fact]
    public void Equivalent_when_renamed_and_pins_swapped()
    {
        var left = Expand(TestData.Simple("a"));
        var right = Expand(TestData.Simple("b", true));

        var result = Match(left, right);
        Assert.True(result.Status == SessionStatus.Equivalent,
            $"{result.Status}:{result.DistinguishingSubgraph?.Reason} diag={string.Join(";", result.Diagnostics.Select(d => d.Code))}");
        Assert.Equal(SessionStatus.Equivalent, result.Status);
        Assert.Single(result.Components, mapping => !mapping.LeftComponentId.StartsWith("PORT:"));
        Assert.False(result.TimedOut);
    }

    [Fact]
    public void Equivalent_after_resistor_array_expansion()
    {
        var left = Expand(TestData.ResistorArray());
        var right = Expand(TestData.SplitResistors());

        var result = Match(left, right);

        Assert.Equal(SessionStatus.Equivalent, result.Status);
        Assert.Equal(5, result.Components.Count);
    }

    [Fact]
    public void Timeout_is_distinct_from_non_equivalence()
    {
        var left = Expand(TestData.ManySymmetricResistors());
        var right = Expand(TestData.ManySymmetricResistors());
        var options = SearchOptions.Default with { Timeout = TimeSpan.FromTicks(1) };

        var result = new SemanticMatcher(rules).Search(left, right, options);

        Assert.Equal(SessionStatus.SearchTimeout, result.Status);
        Assert.True(result.TimedOut);
        Assert.Null(result.DistinguishingSubgraph?.ResponsibleLockIds);
    }

    [Fact]
    public void Non_equivalence_has_minimal_witness()
    {
        var left = Expand(TestData.Simple());
        var different = TestData.WithParameter(TestData.Simple(), new ParameterValue("R", 2000, "ohm", null));
        var right = Expand(different);

        var result = Match(left, right);

        Assert.Equal(SessionStatus.NonEquivalent, result.Status);
        Assert.NotNull(result.DistinguishingSubgraph);
        Assert.Contains(result.DistinguishingSubgraph!.Components, component => component.OriginDeviceId == "r1");
    }

    [Fact]
    public void Conflicting_lock_returns_minimal_responsible_locks()
    {
        var left = Expand(TestData.Simple());
        var right = Expand(TestData.Simple());
        var options = SearchOptions.Default with
        {
            LockedPairs = new HashSet<string>(StringComparer.Ordinal)
            {
                PairKey.Create("PORT:in", "PORT:out"),
                PairKey.Create("PORT:out", "PORT:in")
            }
        };

        var result = new SemanticMatcher(rules).Search(left, right, options);

        Assert.Equal(SessionStatus.LockConflict, result.Status);
        Assert.NotNull(result.ResponsibleLockIds);
        Assert.Single(result.ResponsibleLockIds!);
    }

    [Fact]
    public void Certificate_digest_is_stable_under_certificate_node_traversal_order()
    {
        var left = Expand(TestData.Simple());
        var right = Expand(TestData.Simple("b", true));
        var result = Match(left, right);
        var digest = CertificateDigest(result);

        var reorderedComponents = result.Components.Reverse().ToList();
        var reordered = result with { Components = (IReadOnlyList<ComponentMapping>)reorderedComponents };
        var reorderedDigest = CertificateDigest(reordered);

        Assert.Equal(digest, reorderedDigest);
    }

    private ExpandedNetlist Expand(NetlistDocument document)
    {
        return new NetlistExpander(rules).Expand(document);
    }

    private SearchResult Match(ExpandedNetlist left, ExpandedNetlist right)
    {
        return new SemanticMatcher(rules).Search(left, right, SearchOptions.Default);
    }

    private static string CertificateDigest(SearchResult result)
    {
        return CanonicalJson.Fingerprint(new
        {
            components = result.Components.OrderBy(component => component.LeftComponentId, StringComparer.Ordinal),
            nets = result.Nets.OrderBy(net => net.Key, StringComparer.Ordinal)
        });
    }
}
