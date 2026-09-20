namespace PairwiseGsb.Core.Storage;

public sealed class NetlistRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string RawText { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}

public sealed class RuleRecord
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string RawText { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}

public sealed class DecisionRecord
{
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public DecisionKind Kind { get; set; }
    public DecisionStatus Status { get; set; }
    public string LeftComponentId { get; set; } = "";
    public string RightComponentId { get; set; } = "";
    public string LeftName { get; set; } = "";
    public string RightName { get; set; } = "";
    public string? SourceDecisionId { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public sealed class SessionEvent
{
    public long Sequence { get; set; }
    public string SessionId { get; set; } = "";
    public string Type { get; set; } = "";
    public string At { get; set; } = "";
    public string Payload { get; set; } = "{}";
}

public sealed class BatchPairRequest
{
    public string Name { get; set; } = "";
    public string LeftNetlistId { get; set; } = "";
    public string RightNetlistId { get; set; } = "";
    public string RulesId { get; set; } = "";
}

public sealed class BatchRecord
{
    public string Id { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public BatchStatus Status { get; set; }
    public List<BatchPairRequest> Pairs { get; set; } = [];
    public List<string> SessionIds { get; set; } = [];
    public List<string> JobIds { get; set; } = [];
    public string CreatedAt { get; set; } = "";
    public string? PublishedAt { get; set; }
}

public sealed class JobRecord
{
    public string Id { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public int PairIndex { get; set; }
    public JobStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public sealed class ComparisonSession
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string LeftNetlistId { get; set; } = "";
    public string RightNetlistId { get; set; } = "";
    public string RulesId { get; set; } = "";
    public string LeftFingerprint { get; set; } = "";
    public string RightFingerprint { get; set; } = "";
    public string RulesFingerprint { get; set; } = "";
    public string RuleVersion { get; set; } = "";
    public SessionStatus Status { get; set; }
    public SearchResult? Result { get; set; }
    public MappingCertificate? Certificate { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public sealed class StoreDocument
{
    public int FormatVersion { get; set; } = 1;
    public long NextSequence { get; set; } = 1;
    public Dictionary<string, NetlistRecord> Netlists { get; set; } = new();
    public Dictionary<string, RuleRecord> Rules { get; set; } = new();
    public Dictionary<string, ComparisonSession> Sessions { get; set; } = new();
    public Dictionary<string, DecisionRecord> Decisions { get; set; } = new();
    public List<SessionEvent> Events { get; set; } = [];
    public Dictionary<string, BatchRecord> Batches { get; set; } = new();
    public Dictionary<string, JobRecord> Jobs { get; set; } = new();
}
