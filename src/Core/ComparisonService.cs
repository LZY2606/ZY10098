using System.Text.Json;

namespace NetCompare.Core;

public sealed record SaveNetlistInput(string? Id, string? Name, string RawText);
public sealed record SaveRulesInput(string? Id, string? Name, RuleSet Rules);
public sealed record CreateSessionInput(string LeftNetlistId, string RightNetlistId, string RulesId);
public sealed record DecisionInput(string LeftSourceId, string RightSourceId, DecisionKind Kind, List<PinPair>? Pins);
public sealed record BatchPairInput(string IdempotencyKey, string LeftNetlistId, string RightNetlistId);
public sealed record CreateBatchInput(string IdempotencyKey, string RulesId, List<BatchPairInput> Pairs);

public sealed class ComparisonService(IStateStore store, AnalysisQueue queue)
{
    public Task<PersistedState> Snapshot(CancellationToken cancellationToken = default) =>
        store.Read(state => CloneState(state), cancellationToken);

    public Task<NetlistRevision> SaveNetlist(SaveNetlistInput input, CancellationToken cancellationToken = default) =>
        store.Mutate(state =>
        {
            var netlistId = string.IsNullOrWhiteSpace(input.Id) ? Hashing.StableId("net", input.RawText) : input.Id.Trim();
            var document = NetlistExpander.ParseDocument(input.RawText, netlistId, input.Name ?? netlistId);
            var fingerprint = Hashing.Fingerprint(document);
            var existing = state.Netlists.Values
                .Where(r => r.NetlistId == netlistId)
                .OrderByDescending(r => r.Revision)
                .FirstOrDefault();
            if (existing?.Fingerprint == fingerprint) return Task.FromResult(existing);

            var revisionNumber = (existing?.Revision ?? 0) + 1;
            var revision = new NetlistRevision
            {
                Id = Hashing.StableId("rev", netlistId, revisionNumber.ToString()),
                NetlistId = netlistId,
                Revision = revisionNumber,
                Fingerprint = fingerprint,
                CreatedAt = DateTimeOffset.UtcNow,
                Document = document
            };
            state.Netlists[revision.Id] = revision;
            state.NetlistSequences[netlistId] = revisionNumber;
            return Task.FromResult(revision);
        }, cancellationToken);
    public Task<RuleRevision> SaveRules(SaveRulesInput input, CancellationToken cancellationToken = default) =>
        store.Mutate(state =>
        {
            var rulesId = string.IsNullOrWhiteSpace(input.Id) ? Hashing.StableId("rules", input.Rules.Version) : input.Id.Trim();
            input.Rules.Id = rulesId;
            input.Rules.Name = string.IsNullOrWhiteSpace(input.Name) ? rulesId : input.Name;
            var fingerprint = Hashing.Fingerprint(input.Rules);
            var existing = LatestRules(state, rulesId);
            if (existing?.Fingerprint == fingerprint) return Task.FromResult(existing);

            var revisionNumber = (existing?.Revision ?? 0) + 1;
            var revision = new RuleRevision
            {
                Id = Hashing.StableId("rulerev", rulesId, revisionNumber.ToString()),
                RulesId = rulesId,
                Revision = revisionNumber,
                Version = input.Rules.Version,
                Fingerprint = fingerprint,
                CreatedAt = DateTimeOffset.UtcNow,
                Rules = input.Rules
            };
            state.Rules[revision.Id] = revision;
            state.RuleSequences[rulesId] = revisionNumber;
            return Task.FromResult(revision);
        }, cancellationToken);

    public static PersistedState CloneState(PersistedState state) =>
        JsonSerializer.Deserialize<PersistedState>(JsonSerializer.Serialize(state, Hashing.JsonOptions), Hashing.JsonOptions)
        ?? new PersistedState();

    private static RuleRevision? LatestRules(PersistedState state, string rulesId) =>
        state.Rules.Values
            .Where(r => r.RulesId == rulesId)
            .OrderByDescending(r => r.Revision)
            .FirstOrDefault();

    public Task<ComparisonSession> CreateSession(CreateSessionInput input, CancellationToken cancellationToken = default) =>
        store.Mutate(state =>
        {
            var left = LatestNetlist(state, input.LeftNetlistId) ?? throw new NotFoundException("left netlist was not found");
            var right = LatestNetlist(state, input.RightNetlistId) ?? throw new NotFoundException("right netlist was not found");
            var rules = LatestRules(state, input.RulesId) ?? throw new NotFoundException("rule set was not found");
            var existing = state.Sessions.Values.FirstOrDefault(s =>
                s.LeftRevisionId == left.Id &&
                s.RightRevisionId == right.Id &&
                s.RuleRevisionId == rules.Id);
            if (existing is not null) return Task.FromResult(existing);

            var now = DateTimeOffset.UtcNow;
            var session = new ComparisonSession
            {
                Id = Hashing.StableId("session", left.Id, right.Id, rules.Id),
                LeftRevisionId = left.Id,
                RightRevisionId = right.Id,
                RuleRevisionId = rules.Id,
                LeftFingerprint = left.Fingerprint,
                RightFingerprint = right.Fingerprint,
                RuleVersion = rules.Version,
                RuleFingerprint = rules.Fingerprint,
                State = SessionState.Queued,
                CurrentJobId = Hashing.StableId("job", Hashing.StableId("session", left.Id, right.Id, rules.Id), "initial"),
                CreatedAt = now,
                UpdatedAt = now
            };
            CopyPriorSuggestions(state, session, left.NetlistId, right.NetlistId);
            AnalysisRunner.AddEvent(session, "Created", "session bound to two netlist and rule fingerprints");
            state.Sessions[session.Id] = session;
            queue.EnqueueSession(session.Id);
            return Task.FromResult(session);
        }, cancellationToken);

    public Task<ComparisonSession> AddDecision(string sessionId, DecisionInput input, CancellationToken cancellationToken = default) =>
        store.Mutate(state =>
        {
            var session = GetSession(state, sessionId);
            var decisionId = Hashing.StableId("decision", session.Id, input.LeftSourceId, input.RightSourceId, input.Kind.ToString());
            session.Decisions.RemoveAll(d => string.Equals(d.Id, decisionId, StringComparison.Ordinal));
            session.Decisions.Add(new MappingDecision
            {
                Id = decisionId,
                Kind = input.Kind,
                ReviewState = ReviewState.Accepted,
                LeftSourceId = input.LeftSourceId,
                RightSourceId = input.RightSourceId,
                Pins = input.Pins ?? [],
                CreatedAt = DateTimeOffset.UtcNow
            });
            session.Result = null;
            session.State = SessionState.Queued;
            session.CurrentJobId = StableJobId(session);
            AnalysisRunner.AddEvent(session, input.Kind == DecisionKind.Rejected ? "MappingRejected" : "MappingLocked", $"{input.LeftSourceId}->{input.RightSourceId}");
            queue.EnqueueSession(session.Id);
            return Task.FromResult(session);
        }, cancellationToken);

    public Task<ComparisonSession> RequeueSession(string sessionId, CancellationToken cancellationToken = default) =>
        store.Mutate(state =>
        {
            var session = GetSession(state, sessionId);
            session.State = SessionState.Queued;
            session.CurrentJobId = StableJobId(session);
            AnalysisRunner.AddEvent(session, "SearchRetry", "retry with a stable session and current constraints");
            queue.EnqueueSession(session.Id);
            return Task.FromResult(session);
        }, cancellationToken);

    public Task<ComparisonSession> ReviewSuggestion(string sessionId, string decisionId, bool accept, CancellationToken cancellationToken = default) =>
        store.Mutate(state =>
        {
            var session = GetSession(state, sessionId);
            var decision = session.Decisions.FirstOrDefault(d => d.Id == decisionId)
                ?? throw new NotFoundException("suggestion was not found");
            if (decision.Kind != DecisionKind.Suggestion)
                throw new ConflictException("decision is not a review suggestion");

            decision.ReviewState = accept ? ReviewState.Accepted : ReviewState.Rejected;
            if (accept) decision.Kind = DecisionKind.Locked;
            session.Result = null;
            session.State = SessionState.Queued;
            session.CurrentJobId = StableJobId(session);
            AnalysisRunner.AddEvent(session, accept ? "SuggestionAccepted" : "SuggestionRejected", $"{decision.LeftSourceId}->{decision.RightSourceId}");
            queue.EnqueueSession(session.Id);
            return Task.FromResult(session);
        }, cancellationToken);

    public Task<ComparisonSession> DeleteDecision(string sessionId, string decisionId, CancellationToken cancellationToken = default) =>
        store.Mutate(state =>
        {
            var session = GetSession(state, sessionId);
            var removed = session.Decisions.RemoveAll(d =>
                string.Equals(d.Id, decisionId, StringComparison.Ordinal) &&
                d.Kind != DecisionKind.Suggestion);
            if (removed == 0) throw new NotFoundException("active decision was not found");
            session.Result = null;
            session.State = SessionState.Queued;
            session.CurrentJobId = StableJobId(session);
            AnalysisRunner.AddEvent(session, "DecisionCleared", decisionId);
            queue.EnqueueSession(session.Id);
            return Task.FromResult(session);
        }, cancellationToken);

    public async Task<ComparisonBatch> CreateBatch(CreateBatchInput input, CancellationToken cancellationToken = default)
    {
        var queuedSessions = new List<string>();
        var batch = await store.Mutate(async state =>
        {
            var existing = state.Batches.Values.FirstOrDefault(b =>
                string.Equals(b.IdempotencyKey, input.IdempotencyKey, StringComparison.Ordinal));
            if (existing is not null) return existing;

            var rules = LatestRules(state, input.RulesId) ?? throw new NotFoundException("rule set was not found");
            foreach (var key in input.Pairs.Select(p => p.IdempotencyKey).GroupBy(k => k).Where(g => g.Count() > 1))
                throw new ConflictException($"duplicate pair idempotency key {key.Key}");

            var batch = new ComparisonBatch
            {
                Id = Hashing.StableId("batch", input.IdempotencyKey),
                IdempotencyKey = input.IdempotencyKey,
                RulesId = rules.RulesId,
                CreatedAt = DateTimeOffset.UtcNow
            };
            foreach (var pairInput in input.Pairs)
            {
                var left = LatestNetlist(state, pairInput.LeftNetlistId) ?? throw new NotFoundException("left netlist was not found");
                var right = LatestNetlist(state, pairInput.RightNetlistId) ?? throw new NotFoundException("right netlist was not found");
                var sessionId = Hashing.StableId("session", left.Id, right.Id, rules.Id);
                if (!state.Sessions.TryGetValue(sessionId, out var session))
                {
                    session = new ComparisonSession
                    {
                        Id = sessionId,
                        LeftRevisionId = left.Id,
                        RightRevisionId = right.Id,
                        RuleRevisionId = rules.Id,
                        LeftFingerprint = left.Fingerprint,
                        RightFingerprint = right.Fingerprint,
                        RuleVersion = rules.Version,
                        RuleFingerprint = rules.Fingerprint,
                        State = SessionState.Queued,
                        CurrentJobId = Hashing.StableId("job", sessionId, "initial"),
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    CopyPriorSuggestions(state, session, left.NetlistId, right.NetlistId);
                    AnalysisRunner.AddEvent(session, "Created", "session created by batch");
                    state.Sessions[sessionId] = session;
                    queuedSessions.Add(sessionId);
                }
                else if (session.State is SessionState.Completed or SessionState.Review && session.Result is not null)
                {
                }
                else
                {
                    if (session.State is SessionState.Created or SessionState.Failed)
                        queuedSessions.Add(sessionId);
                }

                batch.Pairs.Add(new BatchPair
                {
                    IdempotencyKey = pairInput.IdempotencyKey,
                    LeftNetlistId = left.NetlistId,
                    RightNetlistId = right.NetlistId,
                    SessionId = sessionId,
                    Status = session.Result?.Status
                });
            }

            if (batch.Pairs.All(p => p.Status.HasValue))
            {
                batch.State = BatchState.Completed;
                batch.CompletedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                batch.State = BatchState.Running;
            }
            state.Batches[batch.Id] = batch;
            await Task.CompletedTask;
            return batch;
        }, cancellationToken);
        foreach (var sessionId in queuedSessions)
            queue.EnqueueSession(sessionId);
        return batch;
    }

    private static NetlistRevision? LatestNetlist(PersistedState state, string id) =>
        state.Netlists.Values
            .Where(r => r.NetlistId == id)
            .OrderByDescending(r => r.Revision)
            .FirstOrDefault();

    private static ComparisonSession GetSession(PersistedState state, string id) =>
        state.Sessions.TryGetValue(id, out var session)
            ? session
            : throw new NotFoundException("session was not found");

    private static void CopyPriorSuggestions(PersistedState state, ComparisonSession target, string leftId, string rightId)
    {
        var leftRevisionIds = state.Netlists.Values
            .Where(r => r.NetlistId == leftId)
            .Select(r => r.Id)
            .ToHashSet(StringComparer.Ordinal);
        var rightRevisionIds = state.Netlists.Values
            .Where(r => r.NetlistId == rightId)
            .Select(r => r.Id)
            .ToHashSet(StringComparer.Ordinal);
        var previous = state.Sessions.Values
            .Where(s => s.Id != target.Id)
            .Where(s => leftRevisionIds.Contains(s.LeftRevisionId) && rightRevisionIds.Contains(s.RightRevisionId))
            .SelectMany(s => s.Decisions.Select(d => (Session: s, Decision: d)))
            .Where(x =>
                x.Session.LeftRevisionId != target.LeftRevisionId ||
                x.Session.RightRevisionId != target.RightRevisionId ||
                x.Session.RuleRevisionId != target.RuleRevisionId)
            .GroupBy(x => (x.Decision.LeftSourceId, x.Decision.RightSourceId))
            .Select(g => g.OrderByDescending(x => x.Session.CreatedAt).First())
            .Take(50);

        foreach (var item in previous)
        {
            if (item.Decision.Kind == DecisionKind.Rejected) continue;
            target.Decisions.Add(new MappingDecision
            {
                Id = Hashing.StableId("suggestion", target.Id, item.Decision.LeftSourceId, item.Decision.RightSourceId),
                Kind = DecisionKind.Suggestion,
                ReviewState = ReviewState.NeedsReview,
                LeftSourceId = item.Decision.LeftSourceId,
                RightSourceId = item.Decision.RightSourceId,
                Pins = item.Decision.Pins,
                OriginSessionId = item.Session.Id,
                CreatedAt = DateTimeOffset.UtcNow
            });
            AnalysisRunner.AddEvent(target, "PriorDecisionSuggested", $"{item.Decision.LeftSourceId}->{item.Decision.RightSourceId}");
        }
    }

    private static string StableJobId(ComparisonSession session) =>
        Hashing.StableId(
            "job",
            session.Id,
            string.Join(",", session.Decisions
                .Where(d => d.ReviewState == ReviewState.Accepted)
                .OrderBy(d => d.Id, StringComparer.Ordinal)
                .Select(d => d.Id)));
}
