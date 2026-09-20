using Microsoft.Extensions.DependencyInjection;

namespace NetCompare.Core;

public static class AnalysisRunner
{
    public static async Task RunOnce(IServiceProvider services, string sessionId, CancellationToken cancellationToken = default)
    {
        var store = services.GetRequiredService<IStateStore>();
        await store.Mutate(async state =>
        {
            if (!state.Sessions.TryGetValue(sessionId, out var session) ||
                session.State is not (SessionState.Queued or SessionState.Analyzing))
                return;

            if (!state.Netlists.TryGetValue(session.LeftRevisionId, out var leftRevision) ||
                !state.Netlists.TryGetValue(session.RightRevisionId, out var rightRevision) ||
                !state.Rules.TryGetValue(session.RuleRevisionId, out var ruleRevision))
            {
                session.State = SessionState.Failed;
                AddEvent(session, "Failed", "bound revision is missing");
                return;
            }

            session.State = SessionState.Analyzing;
            AddEvent(session, "AnalysisStarted", "background analysis started");
            var left = NetlistExpander.Expand(leftRevision, ruleRevision.Rules);
            var right = NetlistExpander.Expand(rightRevision, ruleRevision.Rules);
            var activeDecisions = session.Decisions
                .Where(d => d.ReviewState == ReviewState.Accepted && d.Kind != DecisionKind.Suggestion)
                .ToList();
            var result = await Task.Run(() =>
                new MatchingEngine(left, right, ruleRevision.Rules, activeDecisions, ruleRevision.Rules.SearchTimeoutMs)
                    .Analyze(), cancellationToken);

            session.Result = result;
            session.CurrentJobId = null;
            session.State = result.Status == AnalysisStatus.Equivalent ? SessionState.Completed : SessionState.Review;
            AddEvent(session, "AnalysisCompleted", result.Status + (session.CurrentJobId is null ? "" : "; job:" + session.CurrentJobId));
            FinalizeBatches(state, session, result.Status);
            session.UpdatedAt = DateTimeOffset.UtcNow;
        }, cancellationToken);
    }

    private static void FinalizeBatches(PersistedState state, ComparisonSession session, AnalysisStatus status)
    {
        foreach (var batch in state.Batches.Values.Where(b => b.State == BatchState.Running))
        {
            foreach (var pair in batch.Pairs.Where(p => p.SessionId == session.Id))
                pair.Status = status;
            if (batch.Pairs.All(p => p.Status.HasValue))
            {
                batch.State = BatchState.Completed;
                batch.CompletedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    public static void AddEvent(ComparisonSession session, string type, string detail)
    {
        session.Events.Add(new SessionEvent
        {
            Sequence = session.Events.Count + 1,
            Type = type,
            At = DateTimeOffset.UtcNow,
            Detail = detail
        });
        session.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
