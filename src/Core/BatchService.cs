using PairwiseGsb.Core.Storage;

namespace PairwiseGsb.Core;

public sealed class BatchService
{
    private readonly JsonDocumentStore store;

    public BatchService(JsonDocumentStore store)
    {
        this.store = store;
    }

    public async Task<BatchRecord> SubmitAsync(BatchSubmitRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Pairs.Count == 0)
        {
            throw new ArgumentException("批量比较至少需要一对网表。");
        }

        var idempotencyKey = request.IdempotencyKey
            ?? CanonicalJson.Sha256(CanonicalJson.Serialize(request))[..24];
        return await store.Mutate(doc =>
        {
            var existing = doc.Batches.Values.FirstOrDefault(batch => batch.IdempotencyKey == idempotencyKey);
            if (existing is not null)
            {
                return existing;
            }

            var prepared = new List<(BatchPairRequest Pair, ComparisonSession Session)>();
            foreach (var pair in request.Pairs)
            {
                var create = new CreateSessionRequest(pair.Name, pair.LeftNetlistId, pair.RightNetlistId, pair.RulesId);
                var left = Require(doc, pair.LeftNetlistId, doc.Netlists);
                var right = Require(doc, pair.RightNetlistId, doc.Netlists);
                var rules = string.IsNullOrWhiteSpace(pair.RulesId)
                    ? EnsureDefaultRules(doc)
                    : Require(doc, pair.RulesId, doc.Rules);
                var sessionId = ComparisonService.StableId("sess",
                    string.Join('|', left.Fingerprint, right.Fingerprint, rules.Fingerprint, pair.Name ?? ""));
                var session = doc.Sessions.TryGetValue(sessionId, out var oldSession)
                    ? oldSession
                    : new ComparisonSession
                    {
                        Id = sessionId,
                        Name = pair.Name ?? $"{left.Name} ↔ {right.Name}",
                        LeftNetlistId = left.Id,
                        RightNetlistId = right.Id,
                        RulesId = rules.Id,
                        LeftFingerprint = left.Fingerprint,
                        RightFingerprint = right.Fingerprint,
                        RulesFingerprint = rules.Fingerprint,
                        RuleVersion = rules.Version,
                        Status = SessionStatus.Created,
                        CreatedAt = ComparisonService.UtcNow(),
                        UpdatedAt = ComparisonService.UtcNow()
                    };
                prepared.Add((new BatchPairRequest
                {
                    Name = pair.Name ?? session.Name,
                    LeftNetlistId = left.Id,
                    RightNetlistId = right.Id,
                    RulesId = rules.Id
                }, session));
            }

            var batchId = ComparisonService.StableId("batch", idempotencyKey);
            var batch = new BatchRecord
            {
                Id = batchId,
                IdempotencyKey = idempotencyKey,
                Status = BatchStatus.Pending,
                Pairs = prepared.Select(item => item.Pair).ToList(),
                CreatedAt = ComparisonService.UtcNow()
            };

            for (var index = 0; index < prepared.Count; index++)
            {
                var item = prepared[index];
                if (!doc.Sessions.ContainsKey(item.Session.Id))
                {
                    doc.Sessions[item.Session.Id] = item.Session;
                }

                var jobId = ComparisonService.StableId("job", $"{batchId}:{index}:{item.Session.Id}");
                if (!doc.Jobs.ContainsKey(jobId))
                {
                    doc.Jobs[jobId] = new JobRecord
                    {
                        Id = jobId,
                        IdempotencyKey = $"{idempotencyKey}:{index}",
                        BatchId = batchId,
                        SessionId = item.Session.Id,
                        PairIndex = index,
                        Status = JobStatus.Pending,
                        CreatedAt = ComparisonService.UtcNow(),
                        UpdatedAt = ComparisonService.UtcNow()
                    };
                }

                batch.SessionIds.Add(item.Session.Id);
                batch.JobIds.Add(jobId);
            }

            doc.Batches[batchId] = batch;
            return batch;
        });
    }

    public async Task<BatchRecord> GetAsync(string batchId, CancellationToken cancellationToken = default)
    {
        return await store.Read(doc => doc.Batches.TryGetValue(batchId, out var batch)
            ? batch
            : throw new KeyNotFoundException("找不到批量比较。"));
    }

    private static T Require<T>(StoreDocument doc, string id, IReadOnlyDictionary<string, T> records)
    {
        if (!records.TryGetValue(id, out var record))
        {
            throw new KeyNotFoundException($"找不到记录 {id}。");
        }

        return record;
    }

    private RuleRecord EnsureDefaultRules(StoreDocument doc)
    {
        var rules = DefaultRules.Create();
        var fingerprint = CanonicalJson.Fingerprint(CanonicalJson.Serialize(rules));
        var id = ComparisonService.StableId("rules", fingerprint);
        if (doc.Rules.TryGetValue(id, out var existing))
        {
            return existing;
        }

        var record = new RuleRecord
        {
            Id = id,
            Version = rules.Version,
            RawText = CanonicalJson.Serialize(rules),
            Fingerprint = fingerprint,
            CreatedAt = ComparisonService.UtcNow()
        };
        doc.Rules[id] = record;
        return record;
    }
}
