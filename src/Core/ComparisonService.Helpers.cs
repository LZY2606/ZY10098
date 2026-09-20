using PairwiseGsb.Core.Storage;

namespace PairwiseGsb.Core;

public sealed partial class ComparisonService
{
    internal static string StableId(string prefix, string input)
    {
        return $"{prefix}_{CanonicalJson.Sha256(input)[..16]}";
    }

    internal static string UtcNow()
    {
        return DateTimeOffset.UtcNow.ToString("O");
    }

    private static NetlistRecord RequireNetlist(StoreDocument doc, string id)
    {
        if (!doc.Netlists.TryGetValue(id, out var record))
        {
            throw new KeyNotFoundException($"找不到网表 {id}。");
        }

        return record;
    }

    private static RuleRecord RequireRules(StoreDocument doc, string id)
    {
        if (!doc.Rules.TryGetValue(id, out var record))
        {
            throw new KeyNotFoundException($"找不到规则 {id}。");
        }

        return record;
    }

    private static ComparisonSession RequireSession(StoreDocument doc, string id)
    {
        if (!doc.Sessions.TryGetValue(id, out var session))
        {
            throw new KeyNotFoundException($"找不到比较会话 {id}。");
        }

        return session;
    }

    private void AppendEvent(StoreDocument doc, string sessionId, string type, object payload)
    {
        var sequence = doc.NextSequence++;
        doc.Events.Add(new SessionEvent
        {
            Sequence = sequence,
            SessionId = sessionId,
            Type = type,
            At = UtcNow(),
            Payload = CanonicalJson.Serialize(payload)
        });
    }

    private DecisionRecord AddDecision(
        StoreDocument doc,
        ComparisonSession session,
        DecisionKind kind,
        DecisionStatus status,
        string leftComponentId,
        string rightComponentId,
        string? sourceDecisionId = null)
    {
        var snapshot = Snapshot(doc, session.Id);
        var left = snapshot.LeftExpanded.Components.FirstOrDefault(component => component.Id == leftComponentId);
        var right = snapshot.RightExpanded.Components.FirstOrDefault(component => component.Id == rightComponentId);
        var id = StableId("dec", $"{session.Id}|{kind}|{leftComponentId}|{rightComponentId}");
        var decision = new DecisionRecord
        {
            Id = id,
            SessionId = session.Id,
            Kind = kind,
            Status = status,
            LeftComponentId = leftComponentId,
            RightComponentId = rightComponentId,
            LeftName = left?.Name ?? leftComponentId,
            RightName = right?.Name ?? rightComponentId,
            SourceDecisionId = sourceDecisionId,
            CreatedAt = UtcNow(),
            UpdatedAt = UtcNow()
        };
        doc.Decisions[id] = decision;
        return decision;
    }
}
