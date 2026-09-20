using System.Text.Json;
using PairwiseGsb.Core.Matcher;
using PairwiseGsb.Core.Storage;

namespace PairwiseGsb.Core;

public sealed partial class ComparisonService
{
    private readonly JsonDocumentStore store;

    public ComparisonService(JsonDocumentStore store)
    {
        this.store = store;
    }

    public async Task<NetlistRecord> ImportNetlistAsync(ImportNetlistRequest request, CancellationToken cancellationToken = default)
    {
        var document = ParseNetlist(request.RawText);
        var fingerprint = CanonicalJson.Fingerprint(document);
        return await store.Mutate(doc =>
        {
            var existing = doc.Netlists.Values.FirstOrDefault(record => record.Fingerprint == fingerprint);
            if (existing is not null)
            {
                return existing;
            }

            var record = new NetlistRecord
            {
                Id = StableId("nl", fingerprint),
                Name = request.Name ?? document.Name,
                RawText = request.RawText,
                Fingerprint = fingerprint,
                CreatedAt = UtcNow()
            };
            doc.Netlists[record.Id] = record;
            return record;
        });
    }

    public async Task<RuleRecord> ImportRulesAsync(ImportRulesRequest request, CancellationToken cancellationToken = default)
    {
        var rules = ParseRules(request.RawText);
        if (!string.IsNullOrWhiteSpace(request.Version))
        {
            rules.Version = request.Version!;
        }

        var normalized = CanonicalJson.Serialize(rules);
        var fingerprint = CanonicalJson.Fingerprint(normalized);
        return await store.Mutate(doc =>
        {
            var existing = doc.Rules.Values.FirstOrDefault(record => record.Fingerprint == fingerprint);
            if (existing is not null)
            {
                return existing;
            }

            var record = new RuleRecord
            {
                Id = StableId("rules", fingerprint),
                Version = rules.Version,
                RawText = request.RawText ?? string.Empty,
                Fingerprint = fingerprint,
                CreatedAt = UtcNow()
            };
            doc.Rules[record.Id] = record;
            return record;
        });
    }

    public async Task<SessionSnapshot> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
    {
        return await store.Mutate(doc =>
        {
            var left = RequireNetlist(doc, request.LeftNetlistId);
            var right = RequireNetlist(doc, request.RightNetlistId);
            var rulesRecord = string.IsNullOrWhiteSpace(request.RulesId)
                ? EnsureDefaultRules(doc)
                : RequireRules(doc, request.RulesId!);
            var session = new ComparisonSession
            {
                Id = StableId("sess", string.Join('|', left.Fingerprint, right.Fingerprint, rulesRecord.Fingerprint, request.Name ?? "")),
                Name = request.Name ?? $"{left.Name} ↔ {right.Name}",
                LeftNetlistId = left.Id,
                RightNetlistId = right.Id,
                RulesId = rulesRecord.Id,
                LeftFingerprint = left.Fingerprint,
                RightFingerprint = right.Fingerprint,
                RulesFingerprint = rulesRecord.Fingerprint,
                RuleVersion = rulesRecord.Version,
                Status = SessionStatus.Created,
                CreatedAt = UtcNow(),
                UpdatedAt = UtcNow()
            };

            if (doc.Sessions.TryGetValue(session.Id, out var oldSession))
            {
                return Snapshot(doc, oldSession.Id);
            }

            doc.Sessions[session.Id] = session;
            AppendEvent(doc, session.Id, "session-created", new
            {
                leftFingerprint = left.Fingerprint,
                rightFingerprint = right.Fingerprint,
                rulesFingerprint = rulesRecord.Fingerprint,
                rulesVersion = rulesRecord.Version
            });
            CopyReviewSuggestions(doc, session);
            RunSearch(doc, session, request.TimeoutMs);
            return Snapshot(doc, session.Id);
        });
    }

    public async Task<SessionSnapshot> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return await store.Read(doc => Snapshot(doc, sessionId));
    }

    public async Task<IReadOnlyList<ComparisonSession>> ListSessionsAsync()
    {
        return await store.Read(doc => doc.Sessions.Values.OrderBy(session => session.CreatedAt).ToList());
    }

    public async Task<(IReadOnlyList<NetlistRecord> Netlists, IReadOnlyList<RuleRecord> Rules)> ListInputsAsync()
    {
        return await store.Read(doc =>
            ((IReadOnlyList<NetlistRecord>)doc.Netlists.Values.OrderBy(record => record.CreatedAt).ToList(),
             (IReadOnlyList<RuleRecord>)doc.Rules.Values.OrderBy(record => record.CreatedAt).ToList()));
    }

    public async Task<SessionSnapshot> SearchAsync(string sessionId, int? timeoutMs = null, CancellationToken cancellationToken = default)
    {
        return await store.Mutate(doc =>
        {
            var session = RequireSession(doc, sessionId);
            RunSearch(doc, session, timeoutMs);
            return Snapshot(doc, session.Id);
        });
    }

    private static NetlistDocument ParseNetlist(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<NetlistDocument>(raw, CanonicalJson.Options)
                ?? throw new ArgumentException("网表 JSON 为空。");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"网表 JSON 无法解析：{exception.Message}");
        }
    }

    private static RuleSet ParseRules(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultRules.Create();
        }

        try
        {
            return JsonSerializer.Deserialize<RuleSet>(raw, CanonicalJson.Options) ?? DefaultRules.Create();
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"规则 JSON 无法解析：{exception.Message}");
        }
    }

    private RuleRecord EnsureDefaultRules(StoreDocument doc)
    {
        var rules = DefaultRules.Create();
        var fingerprint = CanonicalJson.Fingerprint(CanonicalJson.Serialize(rules));
        if (doc.Rules.TryGetValue(StableId("rules", fingerprint), out var existing))
        {
            return existing;
        }

        var record = new RuleRecord
        {
            Id = StableId("rules", fingerprint),
            Version = rules.Version,
            RawText = CanonicalJson.Serialize(rules),
            Fingerprint = fingerprint,
            CreatedAt = UtcNow()
        };
        doc.Rules[record.Id] = record;
        return record;
    }

    private void RunSearch(StoreDocument doc, ComparisonSession session, int? timeoutMs)
    {
        var leftRecord = RequireNetlist(doc, session.LeftNetlistId);
        var rightRecord = RequireNetlist(doc, session.RightNetlistId);
        var rulesRecord = RequireRules(doc, session.RulesId);
        var rules = ParseRules(rulesRecord.RawText);
        var leftExpanded = new NetlistExpander(rules).Expand(ParseNetlist(leftRecord.RawText));
        var rightExpanded = new NetlistExpander(rules).Expand(ParseNetlist(rightRecord.RawText));
        var decisions = doc.Decisions.Values
            .Where(decision => decision.SessionId == session.Id && decision.Status == DecisionStatus.Active)
            .ToList();
        var locked = decisions.Where(decision => decision.Kind == DecisionKind.Lock)
            .Select(decision => PairKey.Create(decision.LeftComponentId, decision.RightComponentId))
            .ToHashSet(StringComparer.Ordinal);
        var vetoed = decisions.Where(decision => decision.Kind == DecisionKind.Veto)
            .Select(decision => PairKey.Create(decision.LeftComponentId, decision.RightComponentId))
            .ToHashSet(StringComparer.Ordinal);
        var options = new SearchOptions(
            locked,
            vetoed,
            TimeSpan.FromMilliseconds(timeoutMs ?? 500));
        var result = new SemanticMatcher(rules).Search(leftExpanded, rightExpanded, options);
        session.Result = result;
        session.Status = result.Status;
        session.Certificate = result.Status == SessionStatus.Equivalent
            ? CreateCertificate(session, result)
            : null;
        session.UpdatedAt = UtcNow();
        AppendEvent(doc, session.Id, "search-completed", new
        {
            status = result.Status.ToString(),
            timedOut = result.TimedOut,
            componentCount = result.Components.Count,
            diagnosticCount = result.Diagnostics.Count
        });
    }

    private static MappingCertificate CreateCertificate(ComparisonSession session, SearchResult result)
    {
        var orderedComponents = result.Components
            .OrderBy(mapping => mapping.LeftComponentId, StringComparer.Ordinal)
            .Select(mapping => new
            {
                left = mapping.LeftComponentId,
                right = mapping.RightComponentId,
                pins = mapping.Pins.OrderBy(pin => pin.Key, StringComparer.Ordinal)
            })
            .ToList();
        var orderedNets = result.Nets.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
        var digestPayload = new
        {
            leftFingerprint = session.LeftFingerprint,
            rightFingerprint = session.RightFingerprint,
            rulesFingerprint = session.RulesFingerprint,
            components = orderedComponents,
            nets = orderedNets
        };
        var digest = CanonicalJson.Fingerprint(digestPayload);
        return new MappingCertificate
        {
            CertificateId = StableId("cert", digest),
            SessionId = session.Id,
            LeftFingerprint = session.LeftFingerprint,
            RightFingerprint = session.RightFingerprint,
            RuleVersion = session.RuleVersion,
            RulesFingerprint = session.RulesFingerprint,
            Components = result.Components,
            Nets = result.Nets,
            MappingDigest = digest,
            IssuedAt = UtcNow()
        };
    }

    private static SessionSnapshot Snapshot(StoreDocument doc, string sessionId)
    {
        var session = RequireSession(doc, sessionId);
        var rulesRecord = RequireRules(doc, session.RulesId);
        var leftRecord = RequireNetlist(doc, session.LeftNetlistId);
        var rightRecord = RequireNetlist(doc, session.RightNetlistId);
        var rules = ParseRules(rulesRecord.RawText);
        return new SessionSnapshot(
            session,
            doc.Decisions.Values.Where(decision => decision.SessionId == sessionId).OrderBy(decision => decision.CreatedAt).ToList(),
            doc.Events.Where(ev => ev.SessionId == sessionId).OrderBy(ev => ev.Sequence).ToList(),
            leftRecord,
            rightRecord,
            rulesRecord,
            new NetlistExpander(rules).Expand(ParseNetlist(leftRecord.RawText)),
            new NetlistExpander(rules).Expand(ParseNetlist(rightRecord.RawText)));
    }

    private void CopyReviewSuggestions(StoreDocument doc, ComparisonSession session)
    {
        var previous = doc.Sessions.Values
            .Where(old => old.Id != session.Id)
            .Where(old => old.LeftNetlistId == session.LeftNetlistId ||
                old.RightNetlistId == session.RightNetlistId ||
                old.RulesId == session.RulesId)
            .Where(old => old.LeftNetlistId != session.LeftNetlistId ||
                old.RightNetlistId != session.RightNetlistId ||
                old.RulesId != session.RulesId)
            .OrderByDescending(old => old.CreatedAt)
            .FirstOrDefault();
        if (previous is null)
        {
            return;
        }

        foreach (var decision in doc.Decisions.Values
            .ToList()
            .Where(decision => decision.SessionId == previous.Id && decision.Kind is DecisionKind.Lock or DecisionKind.Veto))
        {
            var copy = new DecisionRecord
            {
                Id = StableId("dec", $"{session.Id}|{decision.LeftComponentId}|{decision.RightComponentId}|{decision.Kind}"),
                SessionId = session.Id,
                Kind = DecisionKind.ReviewSuggestion,
                Status = DecisionStatus.NeedsReview,
                LeftComponentId = decision.LeftComponentId,
                RightComponentId = decision.RightComponentId,
                LeftName = decision.LeftName,
                RightName = decision.RightName,
                SourceDecisionId = decision.Id,
                CreatedAt = UtcNow(),
                UpdatedAt = UtcNow()
            };
            doc.Decisions[copy.Id] = copy;
        }
    }
}
