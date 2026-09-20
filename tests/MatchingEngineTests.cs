using NetCompare.Core;

namespace NetCompare.Tests;

public sealed class MatchingEngineTests
{
    [Fact]
    public void ExpandsArrayAndFindsEquivalentCertificate()
    {
        var rules = DemoRules();
        var left = Expand("left", ArrayNetlist(), rules);
        var right = Expand("right", DiscreteNetlist(), rules);

        var result = new MatchingEngine(left, right, rules, [], 1000).Analyze();

        Assert.Equal(AnalysisStatus.Equivalent, result.Status);
        Assert.NotNull(result.Certificate);
        Assert.Equal(2, result.Mappings.Count);
        Assert.True(CertificateHasher.Verify(result.Certificate!));
        var first = result.Certificate!.CertificateSha256;
        result.Certificate!.Components.Reverse();
        result.Certificate!.Nets = result.Certificate.Nets.Reverse().ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(first, CertificateHasher.Hash(result.Certificate!));
    }

    [Fact]
    public void MissingResistorReturnsNotEquivalentWithWitness()
    {
        var rules = DemoRules();
        var left = Expand("left", ArrayNetlist(), rules);
        var right = Expand("right", SingleResistorNetlist(), rules);

        var result = new MatchingEngine(left, right, rules, [], 1000).Analyze();

        Assert.Equal(AnalysisStatus.NotEquivalent, result.Status);
        Assert.NotNull(result.Witness);
        Assert.NotEmpty(result.Witness!.ComponentIds);
        Assert.Null(result.Certificate);
    }

    [Fact]
    public void PowerShortAndDanglingPortsBecomeDiagnostics()
    {
        var rules = DemoRules();
        rules.NetAliases = [["VDD", "GND"]];
        var document = new NetlistDocument
        {
            Id = "bad",
            RootModuleId = "top",
            Modules =
            [
                new ModuleDefinition
                {
                    Id = "top",
                    Ports = [new PortDefinition { Id = "X" }],
                    Devices = [new DeviceDefinition { Id = "R", Type = "resistor", Pins = new() { ["A"] = "VDD", ["B"] = "GND" }, Parameters = new() { ["resistance"] = "100 ohm" } }]
                }
            ]
        };
        var flat = Expand("left", document, rules);
        var other = Expand("right", DiscreteNetlist(), rules);

        var result = new MatchingEngine(flat, other, rules, [], 1000).Analyze();

        Assert.Equal(AnalysisStatus.DiagnosticsBlocked, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Kind == DiagnosticKind.PowerShort);
        Assert.Contains(result.Diagnostics, d => d.Kind == DiagnosticKind.DanglingPort);
    }

    [Fact]
    public void HierarchyCycleIsDiagnostic()
    {
        var rules = DemoRules();
        var document = new NetlistDocument
        {
            Id = "cycle",
            RootModuleId = "top",
            Modules =
            [
                new ModuleDefinition { Id = "top", Instances = [new InstanceDefinition { Id = "child", ModuleId = "child", Ports = new() { ["P"] = "N" } }] },
                new ModuleDefinition { Id = "child", Ports = [new PortDefinition { Id = "P" }], Instances = [new InstanceDefinition { Id = "self", ModuleId = "top" }] }
            ]
        };

        var flat = Expand("left", document, rules);

        Assert.Contains(flat.Diagnostics, d => d.Kind == DiagnosticKind.HierarchyCycle);
    }

    [Fact]
    public void UnitMismatchIsDiagnosticBeforeComparison()
    {
        var rules = DemoRules();
        var netlist = new NetlistDocument
        {
            Id = "units",
            RootModuleId = "top",
            Modules = [new ModuleDefinition
            {
                Id = "top",
                Devices = [new DeviceDefinition
                {
                    Id = "R",
                    Type = "resistor",
                    Pins = new() { ["A"] = "A", ["B"] = "B" },
                    Parameters = new() { ["resistance"] = "1 fizzohm" }
                }]
            }]
        };

        var flat = Expand("left", netlist, rules);

        Assert.Contains(flat.Diagnostics, d => d.Kind == DiagnosticKind.ParameterUnitMismatch);
    }

    [Fact]
    public void IncompatibleLockedPairReportsMinimalConflictSet()
    {
        var rules = DemoRules();
        var left = Expand("left", ArrayNetlist(), rules);
        var right = Expand("right", DiscreteNetlist(), rules);
        var r2 = right.Components.First(c => c.Id == "$root/R2");
        var r1 = right.Components.First(c => c.Id == "$root/R1");
        var decisions = new List<MappingDecision>
        {
            Decision("$root/RA1.R1", r2.SourceId),
            Decision("$root/RA1.R2", r2.SourceId)
        };

        var result = new MatchingEngine(left, right, rules, decisions, 1000).Analyze();

        Assert.Equal(AnalysisStatus.MappingConflict, result.Status);
        Assert.Single(result.ConflictingLocks);
    }

    [Fact]
    public void SearchTimeoutIsDistinctFromNoMapping()
    {
        var rules = DemoRules();
        rules.SearchTimeoutMs = 1;
        var left = Expand("left", LargeNetlist(42), rules);
        var right = Expand("right", LargeNetlist(42), rules);

        var result = new MatchingEngine(left, right, rules, [], 1).Analyze();

        Assert.Equal(AnalysisStatus.SearchTimeout, result.Status);
        Assert.True(result.TimedOut);
        Assert.Null(result.Witness);
    }

    private static RuleSet DemoRules() => new()
    {
        Id = "rules",
        Version = "test-1",
        SearchTimeoutMs = 1000,
        PortAliases = [new PortAliasRule { Aliases = new() { ["IN"] = "A", ["OUT"] = "B" } }],
        SwappablePins = [new SwappablePinRule { DeviceType = "resistor", Pins = ["A", "B"] }]
    };

    private static FlatNetlist Expand(string side, NetlistDocument document, RuleSet rules)
    {
        var revision = new NetlistRevision
        {
            Id = side + "-rev",
            NetlistId = side,
            Fingerprint = Hashing.Fingerprint(document),
            Document = document
        };
        return NetlistExpander.Expand(revision, rules);
    }

    private static NetlistDocument ArrayNetlist() => new()
    {
        Id = "array",
        RootModuleId = "top",
        Modules = [new ModuleDefinition
        {
            Id = "top",
            Ports = [new PortDefinition { Id = "A" }, new PortDefinition { Id = "B" }],
            Devices = [new DeviceDefinition
            {
                Id = "RA1",
                Type = "resistor_array",
                Pins = new() { ["A1"] = "A", ["B1"] = "N", ["A2"] = "N", ["B2"] = "B" },
                Parameters = new() { ["count"] = "2", ["resistance"] = "1 kohm" }
            }]
        }]
    };

    private static NetlistDocument DiscreteNetlist() => new()
    {
        Id = "discrete",
        RootModuleId = "top",
        Modules = [new ModuleDefinition
        {
            Id = "top",
            Ports = [new PortDefinition { Id = "IN" }, new PortDefinition { Id = "OUT" }],
            Devices =
            [
                new DeviceDefinition { Id = "R1", Type = "resistor", Pins = new() { ["A"] = "IN", ["B"] = "N" }, Parameters = new() { ["resistance"] = "1000 ohm" } },
                new DeviceDefinition { Id = "R2", Type = "resistor", Pins = new() { ["A"] = "N", ["B"] = "OUT" }, Parameters = new() { ["resistance"] = "1000 ohm" } }
            ]
        }]
    };

    private static NetlistDocument SingleResistorNetlist() => new()
    {
        Id = "single",
        RootModuleId = "top",
        Modules = [new ModuleDefinition
        {
            Id = "top",
            Devices = [new DeviceDefinition { Id = "R1", Type = "resistor", Pins = new() { ["A"] = "A", ["B"] = "B" }, Parameters = new() { ["resistance"] = "1000 ohm" } }]
        }]
    };

    private static NetlistDocument LargeNetlist(int count) => new()
    {
        Id = "large",
        RootModuleId = "top",
        Modules = [new ModuleDefinition
        {
            Id = "top",
            Devices = Enumerable.Range(1, count).Select(i => new DeviceDefinition
            {
                Id = "R" + i,
                Type = "resistor",
                Pins = new() { ["A"] = "N" + i, ["B"] = "N" + (i + 1) },
                Parameters = new() { ["resistance"] = "1000 ohm" }
            }).ToList()
        }]
    };

    private static MappingDecision Decision(string left, string right) => new()
    {
        Kind = DecisionKind.Locked,
        ReviewState = ReviewState.Accepted,
        LeftSourceId = left,
        RightSourceId = right
    };
}
