using Microsoft.Extensions.Hosting;
using PairwiseGsb.Core.Matcher;
using PairwiseGsb.Core.Storage;

namespace PairwiseGsb.Core;

public sealed class BackgroundBatchRunner : BackgroundService
{
    private readonly JsonDocumentStore store;
    private readonly TimeSpan interval;

    public BackgroundBatchRunner(JsonDocumentStore store, TimeSpan? interval = null)
    {
        this.store = store;
        this.interval = interval ?? TimeSpan.FromMilliseconds(200);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(stoppingToken);
            }
            catch
            {
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    public async Task ProcessOnceAsync(CancellationToken cancellationToken = default)
    {
        var claim = await store.Mutate(doc =>
        {
            var job = doc.Jobs.Values
                .Where(item => item.Status == JobStatus.Pending || item.Status == JobStatus.Faulted)
                .OrderBy(item => item.PairIndex)
                .FirstOrDefault();
            if (job is null)
            {
                return null;
            }

            job.Status = JobStatus.Running;
            job.Attempts++;
            job.UpdatedAt = ComparisonService.UtcNow();
            return job;
        });

        if (claim is null)
        {
            return;
        }

        try
        {
            await store.Mutate(doc =>
            {
                var session = doc.Sessions[claim.SessionId];
                RunExisting(doc, session);
                var sequence = doc.NextSequence++;
                doc.Events.Add(new SessionEvent
                {
                    Sequence = sequence,
                    SessionId = session.Id,
                    Type = "batch-pair-completed",
                    At = ComparisonService.UtcNow(),
                    Payload = CanonicalJson.Serialize(new { batchId = claim.BatchId, jobId = claim.Id, status = session.Status.ToString() })
                });
                doc.Jobs[claim.Id].Status = JobStatus.Completed;
                doc.Jobs[claim.Id].LastError = null;
                doc.Jobs[claim.Id].UpdatedAt = ComparisonService.UtcNow();

                var batch = doc.Batches[claim.BatchId];
                var jobs = batch.JobIds.Select(id => doc.Jobs[id]).ToList();
                if (jobs.All(job => job.Status == JobStatus.Completed) && batch.Status != BatchStatus.Published)
                {
                    batch.Status = BatchStatus.Published;
                    batch.PublishedAt = ComparisonService.UtcNow();
                }
                else if (jobs.Any(job => job.Status != JobStatus.Completed))
                {
                    batch.Status = BatchStatus.Running;
                }
            });
        }
        catch (Exception exception)
        {
            await store.Mutate(doc =>
            {
                var job = doc.Jobs[claim.Id];
                job.Status = JobStatus.Faulted;
                job.LastError = exception.Message;
                job.UpdatedAt = ComparisonService.UtcNow();
            });
        }
    }

    private static void RunExisting(StoreDocument doc, ComparisonSession session)
    {
        var left = doc.Netlists[session.LeftNetlistId];
        var right = doc.Netlists[session.RightNetlistId];
        var rulesRecord = doc.Rules[session.RulesId];
        var rules = System.Text.Json.JsonSerializer.Deserialize<RuleSet>(rulesRecord.RawText, CanonicalJson.Options)
            ?? DefaultRules.Create();
        var leftNetlist = System.Text.Json.JsonSerializer.Deserialize<NetlistDocument>(left.RawText, CanonicalJson.Options)!;
        var rightNetlist = System.Text.Json.JsonSerializer.Deserialize<NetlistDocument>(right.RawText, CanonicalJson.Options)!;
        var expandedLeft = new NetlistExpander(rules).Expand(leftNetlist);
        var expandedRight = new NetlistExpander(rules).Expand(rightNetlist);
        var active = doc.Decisions.Values.Where(decision => decision.SessionId == session.Id && decision.Status == DecisionStatus.Active).ToList();
        var options = new SearchOptions(
            active.Where(decision => decision.Kind == DecisionKind.Lock)
                .Select(decision => PairKey.Create(decision.LeftComponentId, decision.RightComponentId))
                .ToHashSet(StringComparer.Ordinal),
            active.Where(decision => decision.Kind == DecisionKind.Veto)
                .Select(decision => PairKey.Create(decision.LeftComponentId, decision.RightComponentId))
                .ToHashSet(StringComparer.Ordinal),
            TimeSpan.FromMilliseconds(500));
        var result = new SemanticMatcher(rules).Search(expandedLeft, expandedRight, options);
        session.Result = result;
        session.Status = result.Status;
        session.Certificate = result.Status == SessionStatus.Equivalent
            ? CreateCertificatePublic(session, result)
            : null;
        session.UpdatedAt = ComparisonService.UtcNow();
    }

    private static MappingCertificate CreateCertificatePublic(ComparisonSession session, SearchResult result)
    {
        var digest = CanonicalJson.Fingerprint(new
        {
            leftFingerprint = session.LeftFingerprint,
            rightFingerprint = session.RightFingerprint,
            rulesFingerprint = session.RulesFingerprint,
            components = result.Components.OrderBy(mapping => mapping.LeftComponentId, StringComparer.Ordinal)
                .Select(mapping => new { left = mapping.LeftComponentId, right = mapping.RightComponentId, pins = mapping.Pins.OrderBy(pin => pin.Key, StringComparer.Ordinal) }),
            nets = result.Nets.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        });
        return new MappingCertificate
        {
            CertificateId = ComparisonService.StableId("cert", digest),
            SessionId = session.Id,
            LeftFingerprint = session.LeftFingerprint,
            RightFingerprint = session.RightFingerprint,
            RuleVersion = session.RuleVersion,
            RulesFingerprint = session.RulesFingerprint,
            Components = result.Components,
            Nets = result.Nets,
            MappingDigest = digest,
            IssuedAt = ComparisonService.UtcNow()
        };
    }
}
