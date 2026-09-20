using PairwiseGsb.Core.Storage;

namespace PairwiseGsb.Core;

public sealed record ImportNetlistRequest(string? Name, string RawText);

public sealed record ImportRulesRequest(string? Version, string? RawText);

public sealed record CreateSessionRequest(string? Name, string LeftNetlistId, string RightNetlistId, string? RulesId, int? TimeoutMs = null);

public sealed record BatchSubmitRequest(string? Name, string? IdempotencyKey, IReadOnlyList<BatchPairInput> Pairs);

public sealed record BatchPairInput(string? Name, string LeftNetlistId, string RightNetlistId, string? RulesId);

public sealed record LockRequest(string LeftComponentId, string RightComponentId);

public sealed record SuggestionDecisionRequest(bool Accept, string? LeftComponentId = null, string? RightComponentId = null);

public sealed record VerifyCertificateRequest(string MappingDigest, IReadOnlyList<ComponentMapping> Components, IReadOnlyDictionary<string, string> Nets);

public sealed record SessionSnapshot(
    ComparisonSession Session,
    IReadOnlyList<DecisionRecord> Decisions,
    IReadOnlyList<SessionEvent> Events,
    NetlistRecord LeftNetlist,
    NetlistRecord RightNetlist,
    RuleRecord Rules,
    ExpandedNetlist LeftExpanded,
    ExpandedNetlist RightExpanded);
