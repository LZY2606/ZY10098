using System.Text.Json.Serialization;

namespace PairwiseGsb.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PortDirection
{
    Input,
    Output,
    InOut,
    Power,
    Ground
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosticSeverity
{
    Error,
    Warning
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionStatus
{
    Created,
    Ready,
    Searching,
    Equivalent,
    NonEquivalent,
    SearchTimeout,
    LockConflict,
    DiagnosticFailure
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Faulted
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BatchStatus
{
    Pending,
    Running,
    Published
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DecisionKind
{
    Lock,
    Veto,
    ReviewSuggestion
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DecisionStatus
{
    Active,
    Rejected,
    NeedsReview
}

public sealed record ParameterValue(string Key, double? NumericValue, string? Unit, string? RawValue);

public sealed record NetlistPort(
    string Id,
    string Name,
    PortDirection Direction = PortDirection.InOut,
    string? Net = null);

public sealed record PinConnection(string Pin, string Net);

public sealed record Device(
    string Id,
    string Name,
    string Type,
    IReadOnlyList<PinConnection> Pins,
    IReadOnlyList<ParameterValue>? Parameters = null);

public sealed record ModulePort(string Id, string Name, PortDirection Direction = PortDirection.InOut);

public sealed record ModuleInstance(
    string Id,
    string Name,
    string ModuleId,
    IReadOnlyDictionary<string, string> PortMap);

public sealed record ModuleDefinition(
    string Id,
    string Name,
    IReadOnlyList<ModulePort> Ports,
    IReadOnlyList<Device> Devices,
    IReadOnlyList<ModuleInstance> Instances);

public sealed record NetDefinition(string Id, string? Name = null, IReadOnlyList<string>? Aliases = null);

public sealed class NetlistDocument
{
    public string Name { get; set; } = "";
    public IReadOnlyList<NetlistPort> Ports { get; set; } = [];
    public IReadOnlyList<Device> Devices { get; set; } = [];
    public IReadOnlyList<ModuleDefinition> Modules { get; set; } = [];
    public IReadOnlyList<ModuleInstance> Instances { get; set; } = [];
    public IReadOnlyList<NetDefinition> Nets { get; set; } = [];
    public IReadOnlyList<string> PowerNetNames { get; set; } = [];
}

public sealed record AliasGroup(string Canonical, IReadOnlyList<string> Aliases);

public sealed record ResistorArrayRule(
    string DeviceType,
    string CommonPin,
    IReadOnlyList<string> ElementPins,
    string ResistanceParameter = "R");

public sealed record SwappablePinRule(string DeviceType, IReadOnlyList<string> PinGroup);

public sealed class RuleSet
{
    public string Version { get; set; } = "rules-2026.09.1";
    public double ParameterTolerance { get; set; } = 1e-9;
    public IReadOnlyList<AliasGroup> ComponentAliases { get; set; } = [];
    public IReadOnlyList<AliasGroup> PortAliases { get; set; } = [];
    public IReadOnlyList<AliasGroup> NetAliases { get; set; } = [];
    public IReadOnlyList<AliasGroup> ParameterAliases { get; set; } = [];
    public IReadOnlyList<ResistorArrayRule> ResistorArrays { get; set; } = [];
    public IReadOnlyList<SwappablePinRule> SwappablePins { get; set; } = [];
}

public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string? Side = null,
    string? Path = null);

public sealed record ExpandedComponent(
    string Id,
    string Name,
    string Type,
    IReadOnlyList<string> Pins,
    IReadOnlyList<ParameterValue> Parameters,
    string OriginDeviceId,
    bool IsExternalPort = false,
    PortDirection? Direction = null);

public sealed record ExpandedNet(string Id, string? Name, IReadOnlyList<string> Aliases, bool IsPower = false);

public sealed record ExpandedConnection(string ComponentId, string Pin, string NetId);

public sealed class ExpandedNetlist
{
    public required NetlistDocument Source { get; init; }
    public required RuleSet Rules { get; init; }
    public List<ExpandedComponent> Components { get; } = [];
    public List<ExpandedNet> Nets { get; } = [];
    public List<ExpandedConnection> Connections { get; } = [];
    public List<Diagnostic> Diagnostics { get; } = [];
}

public sealed record ComponentMapping(
    string LeftComponentId,
    string RightComponentId,
    IReadOnlyDictionary<string, string> Pins,
    string LeftName,
    string RightName);

public sealed record CandidateMapping(
    string LeftComponentId,
    string RightComponentId,
    string LeftName,
    string RightName,
    int Score,
    string Reason);

public sealed record DistinguishingSubgraph(
    string Reason,
    IReadOnlyList<ExpandedComponent> Components,
    IReadOnlyList<ExpandedNet> Nets,
    IReadOnlyList<ExpandedConnection> Connections,
    IReadOnlyList<string>? ResponsibleLockIds = null);

public sealed class MappingCertificate
{
    public string CertificateId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string LeftFingerprint { get; set; } = "";
    public string RightFingerprint { get; set; } = "";
    public string RuleVersion { get; set; } = "";
    public string RulesFingerprint { get; set; } = "";
    public IReadOnlyList<ComponentMapping> Components { get; set; } = [];
    public IReadOnlyDictionary<string, string> Nets { get; set; } = new Dictionary<string, string>();
    public string MappingDigest { get; set; } = "";
    public string IssuedAt { get; set; } = "";
    public IReadOnlyList<long> EvidenceEventSequence { get; set; } = [];
}

public sealed record SearchResult(
    SessionStatus Status,
    IReadOnlyList<ComponentMapping> Components,
    IReadOnlyDictionary<string, string> Nets,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<CandidateMapping> Ambiguities,
    DistinguishingSubgraph? DistinguishingSubgraph,
    IReadOnlyList<string>? ResponsibleLockIds,
    bool TimedOut);
