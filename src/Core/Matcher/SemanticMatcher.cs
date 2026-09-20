using System.Diagnostics;

namespace PairwiseGsb.Core.Matcher;

public sealed partial class SemanticMatcher
{
    private readonly RuleSet rules;
    private readonly Stopwatch stopwatch = new();
    private SearchOptions options = SearchOptions.Default;
    private List<CandidateMapping> ambiguities = [];
    private bool timedOut;

    public SemanticMatcher(RuleSet rules)
    {
        this.rules = rules;
    }

    public SearchResult Search(ExpandedNetlist left, ExpandedNetlist right, SearchOptions? searchOptions = null)
    {
        options = searchOptions ?? SearchOptions.Default;
        ambiguities = [];
        timedOut = false;
        stopwatch.Restart();

        var diagnostics = left.Diagnostics.Concat(right.Diagnostics).ToList();
        if (diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return new SearchResult(SessionStatus.DiagnosticFailure, [], new Dictionary<string, string>(),
                diagnostics, [], DiagnosticWitness(left, right, diagnostics), null, false);
        }

        if (left.Components.Count != right.Components.Count || left.Connections.Count != right.Connections.Count)
        {
            return new SearchResult(SessionStatus.NonEquivalent, [], new Dictionary<string, string>(), diagnostics,
                [], CountWitness(left, right), null, false);
        }

        var state = new SearchState();
        var orderedLeft = OrderComponents(left, right);
        if (!SearchComponent(left, right, orderedLeft, 0, state))
        {
            if (!timedOut && (options.LockedPairs.Count > 0 || options.VetoedPairs.Count > 0))
            {
                var responsibleLocks = FindLockConflict(left, right);
                if (responsibleLocks is not null)
                {
                    return new SearchResult(SessionStatus.LockConflict, [], new Dictionary<string, string>(), diagnostics,
                        ambiguities, LockWitness(left, right, responsibleLocks), responsibleLocks, false);
                }
            }
            var witness = timedOut
                ? new DistinguishingSubgraph("搜索达到时间预算，不能据此判定不等价。", [], [], [], null)
                : CountWitness(left, right);
            return new SearchResult(timedOut ? SessionStatus.SearchTimeout : SessionStatus.NonEquivalent,
                [], new Dictionary<string, string>(), diagnostics, ambiguities, witness, null, timedOut);
        }

        var mappings = BuildMappings(left, right, state);
        CollectAmbiguities(left, right, mappings);
        if (timedOut)
        {
            return new SearchResult(SessionStatus.SearchTimeout, [], new Dictionary<string, string>(), diagnostics, ambiguities,
                new DistinguishingSubgraph("搜索达到时间预算，不能据此判定不等价。", [], [], [], null), null, true);
        }

        return new SearchResult(SessionStatus.Equivalent, mappings, state.NetByLeft.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value),
            diagnostics, ambiguities, null, null, timedOut);
    }

    private bool SearchComponent(ExpandedNetlist left, ExpandedNetlist right, List<ExpandedComponent> orderedLeft, int index, SearchState state)
    {
        if (options.Timeout.HasValue && stopwatch.Elapsed > options.Timeout.Value)
        {
            timedOut = true;
            return false;
        }

        if (timedOut)
        {
            return false;
        }

        if (index == orderedLeft.Count)
        {
            return state.NetByLeft.Count == right.Nets.Count;
        }

        var leftComponent = orderedLeft[index];
        var pair = options.LockedPairs.FirstOrDefault(candidate =>
            candidate.StartsWith(leftComponent.Id + "⇒", StringComparison.Ordinal));
        var candidates = BuildCandidates(leftComponent, left, right, state, pair);
        foreach (var rightComponent in candidates)
        {
            if (rightComponent is not null && !CanUsePair(leftComponent, rightComponent, options, state))
            {
                continue;
            }

            if (rightComponent is null)
            {
                continue;
            }

            foreach (var pinMapping in EnumeratePinMappings(leftComponent, rightComponent))
            {
                var next = state.Clone();
                next.ComponentByLeft[leftComponent.Id] = rightComponent.Id;
                next.ComponentByRight[rightComponent.Id] = leftComponent.Id;
                next.PinsByPair[PairKey.Create(leftComponent.Id, rightComponent.Id)] = pinMapping;
                next.UsedPairs.Add(PairKey.Create(leftComponent.Id, rightComponent.Id));

                if (TryAssignNets(leftComponent, rightComponent, pinMapping, left, right, next) &&
                    SearchComponent(left, right, orderedLeft, index + 1, next))
                {
                    RestoreState(state, next);
                    return true;
                }
            }
        }

        return false;
    }

    private void RestoreState(SearchState target, SearchState source)
    {
        target.ComponentByLeft.Clear();
        target.ComponentByRight.Clear();
        target.NetByLeft.Clear();
        target.NetByRight.Clear();
        target.PinsByPair.Clear();
        target.UsedPairs.Clear();
        foreach (var (key, value) in source.ComponentByLeft) target.ComponentByLeft[key] = value;
        foreach (var (key, value) in source.ComponentByRight) target.ComponentByRight[key] = value;
        foreach (var (key, value) in source.NetByLeft) target.NetByLeft[key] = value;
        foreach (var (key, value) in source.NetByRight) target.NetByRight[key] = value;
        foreach (var (key, value) in source.PinsByPair) target.PinsByPair[key] = new Dictionary<string, string>(value);
        target.UsedPairs.UnionWith(source.UsedPairs);
        target.VisitedNodes = source.VisitedNodes;
    }

    private List<ExpandedComponent> OrderComponents(ExpandedNetlist left, ExpandedNetlist right)
    {
        return left.Components
            .OrderBy(component => component.IsExternalPort ? 0 : 1)
            .ThenByDescending(component => left.Connections.Count(connection => connection.ComponentId == component.Id))
            .ThenBy(component => component.Id, StringComparer.Ordinal)
            .ToList();
    }

    private List<ExpandedComponent> BuildCandidates(
        ExpandedComponent leftComponent,
        ExpandedNetlist left,
        ExpandedNetlist right,
        SearchState state,
        string? lockedPair)
    {
        IEnumerable<ExpandedComponent> query = right.Components.Where(component =>
            !state.ComponentByRight.ContainsKey(component.Id) && CompatibleType(leftComponent, component));

        if (lockedPair is not null)
        {
            var rightId = lockedPair[(lockedPair.IndexOf('⇒') + 1)..];
            query = query.Where(component => component.Id == rightId);
        }
        else
        {
            query = query.OrderByDescending(component => HeuristicScore(leftComponent, component))
                .ThenBy(component => component.Id, StringComparer.Ordinal);
        }

        return query.ToList();
    }

    private static bool CanUsePair(ExpandedComponent left, ExpandedComponent right, SearchOptions options, SearchState state)
    {
        var pair = PairKey.Create(left.Id, right.Id);
        return !options.VetoedPairs.Contains(pair) && !state.UsedPairs.Contains(pair);
    }

    private static bool CompatibleType(ExpandedComponent left, ExpandedComponent right)
    {
        if (left.IsExternalPort != right.IsExternalPort)
        {
            return false;
        }

        return left.IsExternalPort ? left.Direction == right.Direction : string.Equals(left.Type, right.Type, StringComparison.Ordinal);
    }

    private static int HeuristicScore(ExpandedComponent left, ExpandedComponent right)
    {
        var score = left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase) ? 10 : 0;
        score += Math.Min(left.Parameters.Count, right.Parameters.Count);
        return score;
    }

    private static List<ComponentMapping> BuildMappings(ExpandedNetlist left, ExpandedNetlist right, SearchState state)
    {
        var rightById = right.Components.ToDictionary(component => component.Id, StringComparer.Ordinal);
        return state.ComponentByLeft
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var leftComponent = left.Components.First(component => component.Id == pair.Key);
                var rightComponent = rightById[pair.Value];
                var pins = state.PinsByPair[PairKey.Create(pair.Key, pair.Value)];
                return new ComponentMapping(pair.Key, pair.Value, pins, leftComponent.Name, rightComponent.Name);
            })
            .ToList();
    }
}
