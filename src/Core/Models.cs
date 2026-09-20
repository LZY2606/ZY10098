using System.Text.Json.Serialization;

namespace NetCompare.Core;

public enum DiagnosticLevel { Warning, Error }
public enum DiagnosticKind
{
    PowerShort, DanglingPort, HierarchyCycle, ParameterUnitMismatch,
    UnknownModule, InvalidExpansion, InvalidNetlist
}

public enum AnalysisStatus
{
    Equivalent, NotEquivalent, SearchTimeout, DiagnosticsBlocked, MappingConflict, Ambiguous
}

public enum SessionState { Created, Queued, Analyzing, Review, Completed, Failed }
public enum DecisionKind { Suggestion, Locked, Rejected }
public enum ReviewState { NeedsReview, Accepted, Rejected, Obsolete }
public enum BatchState { Pending, Running, Completed }

public sealed class NetlistDocument
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string RawText { get; set; } = "";
    public string RootModuleId { get; set; } = "";
    public List<ModuleDefinition> Modules { get; set; } = [];
}

public sealed class NetlistRevision
{
    public string Id { get; set; } = "";
    public string NetlistId { get; set; } = "";
    public int Revision { get; set; }
    public string Fingerprint { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public NetlistDocument Document { get; set; } = new();
}

public sealed class ModuleDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<PortDefinition> Ports { get; set; } = [];
    public List<DeviceDefinition> Devices { get; set; } = [];
    public List<InstanceDefinition> Instances { get; set; } = [];
    public List<NetDeclaration> Nets { get; set; } = [];
}

public sealed class PortDefinition
{
    public string Id { get; set; } = "";
    public string? Direction { get; set; }
}

public sealed class DeviceDefinition
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public Dictionary<string, string> Pins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class InstanceDefinition
{
    public string Id { get; set; } = "";
    public string ModuleId { get; set; } = "";
    public Dictionary<string, string> Ports { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class NetDeclaration
{
    public string Id { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
}

public sealed class ParameterUnitRule
{
    public string ParameterName { get; set; } = "";
    public string CanonicalUnit { get; set; } = "";
    public Dictionary<string, double> Units { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PortAliasRule
{
    public string ModuleId { get; set; } = "*";
    public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SwappablePinRule
{
    public string DeviceType { get; set; } = "";
    public List<string> Pins { get; set; } = [];
}

public sealed class ResistorArrayRule
{
    public string DeviceType { get; set; } = "resistor_array";
    public string ElementType { get; set; } = "resistor";
    public string CountParameter { get; set; } = "count";
    public string ResistanceParameter { get; set; } = "resistance";
    public string PinAFormat { get; set; } = "A{0}";
    public string PinBFormat { get; set; } = "B{0}";
    public string NameFormat { get; set; } = "{0}.R{1}";
}

public sealed class RuleSet
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public int SearchTimeoutMs { get; set; } = 250;
    public List<string> PowerNames { get; set; } = ["VDD", "VCC", "VPP"];
    public List<string> GroundNames { get; set; } = ["GND", "VSS"];
    public List<List<string>> NetAliases { get; set; } = [];
    public List<PortAliasRule> PortAliases { get; set; } = [];
    public List<SwappablePinRule> SwappablePins { get; set; } = [];
    public List<ResistorArrayRule> ResistorArrays { get; set; } = [new()];
    public List<ParameterUnitRule> ParameterUnits { get; set; } =
    [
        new()
        {
            ParameterName = "resistance",
            CanonicalUnit = "ohm",
            Units = new() { ["ohm"] = 1, ["Ω"] = 1, ["kohm"] = 1000, ["Mohm"] = 1_000_000 }
        }
    ];
}

public sealed class RuleRevision
{
    public string Id { get; set; } = "";
    public string RulesId { get; set; } = "";
    public int Revision { get; set; }
    public string Version { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public RuleSet Rules { get; set; } = new();
}

public sealed class Diagnostic
{
    public DiagnosticKind Kind { get; set; }
    public DiagnosticLevel Level { get; set; }
    public string Side { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Path { get; set; }
}

public sealed class FlatComponent
{
    public string Id { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string Type { get; set; } = "";
    public bool IsPort { get; set; }
    public Dictionary<string, string> Pins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class FlatNetlist
{
    public string NetlistId { get; set; } = "";
    public string RevisionId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public List<FlatComponent> Components { get; set; } = [];
    public Dictionary<string, List<NetEndpoint>> Nets { get; set; } = new(StringComparer.Ordinal);
    public List<Diagnostic> Diagnostics { get; set; } = [];
}

public sealed record NetEndpoint(string ComponentId, string Pin);

public sealed class PinPair
{
    public string LeftPin { get; set; } = "";
    public string RightPin { get; set; } = "";
}

public sealed class ComponentMapping
{
    public string LeftId { get; set; } = "";
    public string RightId { get; set; } = "";
    public List<PinPair> Pins { get; set; } = [];
}

public sealed class CandidateMapping
{
    public string LeftId { get; set; } = "";
    public string RightId { get; set; } = "";
    public List<string> AlternativeRightIds { get; set; } = [];
}

public sealed class DistinguishingSubgraph
{
    public string Side { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<string> ComponentIds { get; set; } = [];
    public List<string> NetIds { get; set; } = [];
    public List<FlatComponent> Components { get; set; } = [];
}

public sealed class MappingCertificate
{
    public string CertificateSha256 { get; set; } = "";
    public string Algorithm { get; set; } = "canonical-bijective-v1";
    public string LeftFingerprint { get; set; } = "";
    public string RightFingerprint { get; set; } = "";
    public string RuleVersion { get; set; } = "";
    public string RuleFingerprint { get; set; } = "";
    public List<ComponentMapping> Components { get; set; } = [];
    public Dictionary<string, string> Nets { get; set; } = new();
    public DateTimeOffset IssuedAt { get; set; }
}

public sealed class AnalysisResult
{
    public AnalysisStatus Status { get; set; }
    public List<ComponentMapping> Mappings { get; set; } = [];
    public List<string> UnmatchedLeft { get; set; } = [];
    public List<string> UnmatchedRight { get; set; } = [];
    public List<CandidateMapping> AmbiguousCandidates { get; set; } = [];
    public List<Diagnostic> Diagnostics { get; set; } = [];
    public DistinguishingSubgraph? Witness { get; set; }
    public MappingCertificate? Certificate { get; set; }
    public List<string> ConflictingLocks { get; set; } = [];
    public long ElapsedMs { get; set; }
    public bool TimedOut { get; set; }
}

public sealed class MappingDecision
{
    public string Id { get; set; } = "";
    public DecisionKind Kind { get; set; }
    public ReviewState ReviewState { get; set; } = ReviewState.Accepted;
    public string LeftSourceId { get; set; } = "";
    public string RightSourceId { get; set; } = "";
    public List<PinPair> Pins { get; set; } = [];
    public string OriginSessionId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SessionEvent
{
    public int Sequence { get; set; }
    public string Type { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class ComparisonSession
{
    public string Id { get; set; } = "";
    public string LeftRevisionId { get; set; } = "";
    public string RightRevisionId { get; set; } = "";
    public string RuleRevisionId { get; set; } = "";
    public string LeftFingerprint { get; set; } = "";
    public string RightFingerprint { get; set; } = "";
    public string RuleVersion { get; set; } = "";
    public string RuleFingerprint { get; set; } = "";
    public SessionState State { get; set; }
    public string? CurrentJobId { get; set; }
    public AnalysisResult? Result { get; set; }
    public List<MappingDecision> Decisions { get; set; } = [];
    public List<SessionEvent> Events { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class BatchPair
{
    public string IdempotencyKey { get; set; } = "";
    public string LeftNetlistId { get; set; } = "";
    public string RightNetlistId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public AnalysisStatus? Status { get; set; }
}

public sealed class ComparisonBatch
{
    public string Id { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string RulesId { get; set; } = "";
    public BatchState State { get; set; } = BatchState.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<BatchPair> Pairs { get; set; } = [];
}

public sealed class PersistedState
{
    public int FormatVersion { get; set; } = 1;
    public Dictionary<string, NetlistRevision> Netlists { get; set; } = new();
    public Dictionary<string, RuleRevision> Rules { get; set; } = new();
    public Dictionary<string, ComparisonSession> Sessions { get; set; } = new();
    public Dictionary<string, ComparisonBatch> Batches { get; set; } = new();
    public Dictionary<string, int> NetlistSequences { get; set; } = new();
    public Dictionary<string, int> RuleSequences { get; set; } = new();
}
