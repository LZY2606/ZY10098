using PairwiseGsb.Core.Storage;

namespace PairwiseGsb.Core;

public sealed partial class ComparisonService
{
    public async Task<SessionSnapshot> LockAsync(string sessionId, LockRequest request, CancellationToken cancellationToken = default)
    {
        return await store.Mutate(doc =>
        {
            var session = RequireSession(doc, sessionId);
            var decision = AddDecision(doc, session, DecisionKind.Lock, DecisionStatus.Active,
                request.LeftComponentId, request.RightComponentId);
            AppendEvent(doc, session.Id, "mapping-locked", new
            {
                decisionId = decision.Id,
                left = request.LeftComponentId,
                right = request.RightComponentId
            });
            RunSearch(doc, session, null);
            return Snapshot(doc, session.Id);
        });
    }

    public async Task<SessionSnapshot> VetoAsync(string sessionId, LockRequest request, CancellationToken cancellationToken = default)
    {
        return await store.Mutate(doc =>
        {
            var session = RequireSession(doc, sessionId);
            var decision = AddDecision(doc, session, DecisionKind.Veto, DecisionStatus.Active,
                request.LeftComponentId, request.RightComponentId);
            AppendEvent(doc, session.Id, "mapping-vetoed", new
            {
                decisionId = decision.Id,
                left = request.LeftComponentId,
                right = request.RightComponentId
            });
            RunSearch(doc, session, null);
            return Snapshot(doc, session.Id);
        });
    }

    public async Task<SessionSnapshot> ReviewSuggestionAsync(
        string sessionId,
        string decisionId,
        SuggestionDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        return await store.Mutate(doc =>
        {
            var session = RequireSession(doc, sessionId);
            if (!doc.Decisions.TryGetValue(decisionId, out var suggestion) ||
                suggestion.SessionId != sessionId ||
                suggestion.Kind != DecisionKind.ReviewSuggestion)
            {
                throw new KeyNotFoundException("找不到待复核映射建议。");
            }

            suggestion.Status = request.Accept ? DecisionStatus.Active : DecisionStatus.Rejected;
            suggestion.UpdatedAt = UtcNow();
            if (request.Accept)
            {
                var accepted = AddDecision(doc, session, DecisionKind.Lock, DecisionStatus.Active,
                    request.LeftComponentId ?? suggestion.LeftComponentId,
                    request.RightComponentId ?? suggestion.RightComponentId,
                    suggestion.SourceDecisionId);
                AppendEvent(doc, session.Id, "suggestion-accepted", new { decisionId = accepted.Id, sourceDecisionId = suggestion.SourceDecisionId });
            }
            else
            {
                AppendEvent(doc, session.Id, "suggestion-rejected", new { decisionId, sourceDecisionId = suggestion.SourceDecisionId });
            }

            RunSearch(doc, session, null);
            return Snapshot(doc, session.Id);
        });
    }

    public async Task<MappingCertificate?> GetCertificateAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return await store.Read(doc => RequireSession(doc, sessionId).Certificate);
    }

    public static bool VerifyCertificate(MappingCertificate certificate)
    {
        var digest = CanonicalJson.Fingerprint(new
        {
            leftFingerprint = certificate.LeftFingerprint,
            rightFingerprint = certificate.RightFingerprint,
            rulesFingerprint = certificate.RulesFingerprint,
            components = certificate.Components
                .OrderBy(mapping => mapping.LeftComponentId, StringComparer.Ordinal)
                .Select(mapping => new
                {
                    left = mapping.LeftComponentId,
                    right = mapping.RightComponentId,
                    pins = mapping.Pins.OrderBy(pin => pin.Key, StringComparer.Ordinal)
                }),
            nets = certificate.Nets.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        });
        return digest == certificate.MappingDigest;
    }
}
