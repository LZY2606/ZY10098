using System.Diagnostics;

namespace NetCompare.Core;

public sealed class MatchingEngine
{
    private readonly FlatNetlist left;
    private readonly FlatNetlist right;
    private readonly RuleSet rules;
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private readonly DateTimeOffset now;
    private readonly Dictionary<string, FlatComponent> lc;
    private readonly Dictionary<string, FlatComponent> rc;
    private readonly CancellationTokenSource cts;
    private readonly HashSet<(string, string)> rejected = new();
    private readonly Dictionary<string, string> lockedLeft = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> lockedRight = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), List<PinPair>> lockedPins = new();
    private readonly string ruleFingerprint;
    private (string Left, string Right)? forbiddenPair;

    public MatchingEngine(FlatNetlist left, FlatNetlist right, RuleSet rules, IReadOnlyList<MappingDecision> decisions, int timeoutMs, DateTimeOffset? now = null)
    {
        this.left = left;
        this.right = right;
        this.rules = rules;
        ruleFingerprint = Hashing.Fingerprint(rules);
        this.now = now ?? DateTimeOffset.UtcNow;
        lc = left.Components.ToDictionary(c => c.Id, c => c, StringComparer.Ordinal);
        rc = right.Components.ToDictionary(c => c.Id, c => c, StringComparer.Ordinal);
        cts = new CancellationTokenSource(Math.Max(10, timeoutMs));

        foreach (var decision in decisions.Where(d => d.ReviewState == ReviewState.Accepted))
        {
            var lId = FindBySource(left, decision.LeftSourceId);
            var rId = FindBySource(right, decision.RightSourceId);
            if (lId is null || rId is null) continue;
            if (decision.Kind == DecisionKind.Rejected)
            {
                rejected.Add((lId, rId));
            }
            else
            {
                lockedLeft[lId] = rId;
                lockedRight[rId] = lId;
                lockedPins[(lId, rId)] = decision.Pins;
            }
        }
    }

    public AnalysisResult Analyze()
    {
        var diagnostics = SideDiagnostics("left", left.Diagnostics).Concat(SideDiagnostics("right", right.Diagnostics)).ToList();
        if (diagnostics.Any(d => d.Level == DiagnosticLevel.Error))
            return Blocked(diagnostics);

        var conflict = FindLockConflict(diagnostics);
        if (conflict is not null) return conflict;

        var maps = new List<Dictionary<string, ComponentMapping>>();
        try
        {
            Search(new Dictionary<string, ComponentMapping>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal), maps, collect: false);
            if (maps.Count == 1 && !cts.IsCancellationRequested)
                FindAlternative(maps);
        }
        catch (OperationCanceledException)
        {
        }
        if (maps.Count == 0)
        {
            if (cts.IsCancellationRequested)
                return Timeout(diagnostics);
            return NonEquivalent(diagnostics);
        }

        if (cts.IsCancellationRequested)
            return Timeout(diagnostics);

        if (maps.Count == 1)
            return Complete(maps[0], diagnostics);

        var first = maps[0];
        var second = maps[1];
        var ambiguousKey = first.Keys.First(left =>
            !string.Equals(first[left].RightId, second[left].RightId, StringComparison.Ordinal));
        var alternativeIds = new List<string> { second[ambiguousKey].RightId };
        return new AnalysisResult
        {
            Status = AnalysisStatus.Ambiguous,
            Mappings = ToMappings(first).OrderBy(m => m.LeftId, StringComparer.Ordinal).ToList(),
            AmbiguousCandidates =
            [
                new CandidateMapping
                {
                    LeftId = ambiguousKey,
                    RightId = first[ambiguousKey].RightId,
                    AlternativeRightIds = alternativeIds
                }
            ],
            UnmatchedLeft = [],
            UnmatchedRight = [],
            Diagnostics = diagnostics,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            TimedOut = false
        };
    }

    private static IEnumerable<Diagnostic> SideDiagnostics(string side, IEnumerable<Diagnostic> source)
    {
        foreach (var diagnostic in source)
            yield return new Diagnostic
            {
                Kind = diagnostic.Kind,
                Level = diagnostic.Level,
                Side = side,
                Message = diagnostic.Message,
                Path = diagnostic.Path
            };
    }

    private AnalysisResult Blocked(List<Diagnostic> diagnostics) => new()
    {
        Status = AnalysisStatus.DiagnosticsBlocked,
        Diagnostics = diagnostics,
        ElapsedMs = stopwatch.ElapsedMilliseconds
    };

    private AnalysisResult Timeout(List<Diagnostic> diagnostics) => new()
    {
        Status = AnalysisStatus.SearchTimeout,
        Diagnostics = diagnostics,
        ElapsedMs = stopwatch.ElapsedMilliseconds,
        TimedOut = true
    };

    private void FindAlternative(List<Dictionary<string, ComponentMapping>> maps)
    {
        var first = maps[0];
        foreach (var leftId in first.Keys.OrderBy(id => id, StringComparer.Ordinal))
        {
            forbiddenPair = (leftId, first[leftId].RightId);
            var probe = new List<Dictionary<string, ComponentMapping>>();
            try
            {
                Search(new Dictionary<string, ComponentMapping>(StringComparer.Ordinal),
                    new Dictionary<string, string>(StringComparer.Ordinal), probe, collect: false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (probe.Count == 1)
            {
                maps.Add(probe[0]);
                break;
            }
        }
        forbiddenPair = null;
    }

    private AnalysisResult? FindLockConflict(List<Diagnostic> diagnostics)
    {
        var locks = lockedLeft.Select(kv => (Left: kv.Key, Right: kv.Value)).ToList();
        foreach (var pair in locks)
        {
            if (rejected.Contains((pair.Left, pair.Right)) ||
                !StaticCompatible(lc[pair.Left], rc[pair.Right]))
            {
                return Conflict([pair.Left], diagnostics, stopwatch.ElapsedMilliseconds);
            }
        }

        if (!CanSatisfy(locks, cts.Token) && !cts.IsCancellationRequested)
        {
            for (var size = 1; size <= locks.Count; size++)
            {
                foreach (var subset in Combinations(locks, size))
                {
                    using var probe = new CancellationTokenSource(rules.SearchTimeoutMs);
                    if (!CanSatisfy(subset, probe.Token))
                        return Conflict(subset.Select(p => p.Left).ToList(), diagnostics, stopwatch.ElapsedMilliseconds);
                }
            }
        }

        if (cts.IsCancellationRequested)
            return Timeout(diagnostics);
        return null;
    }

    private static AnalysisResult Conflict(List<string> lockIds, List<Diagnostic> diagnostics, long elapsed) => new()
    {
        Status = AnalysisStatus.MappingConflict,
        Diagnostics = diagnostics,
        ConflictingLocks = lockIds,
        ElapsedMs = elapsed
    };

    private bool CanSatisfy(IReadOnlyList<(string Left, string Right)> locks, CancellationToken token)
    {
        var mappings = new Dictionary<string, ComponentMapping>(StringComparer.Ordinal);
        foreach (var pair in locks)
        {
            var mapping = BuildMapping(lc[pair.Left], rc[pair.Right], lockedPins.TryGetValue(pair, out var pins) ? pins : []);
            if (mapping is null || !Compatible(mapping, mappings, out _)) return false;
            mappings[pair.Left] = mapping;
        }
        try
        {
            return Search(mappings, new Dictionary<string, string>(StringComparer.Ordinal), [], collect: false, token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private bool Search(
        Dictionary<string, ComponentMapping> mappings,
        Dictionary<string, string> inverse,
        List<Dictionary<string, ComponentMapping>>? found,
        bool collect,
        CancellationToken? suppliedToken = null)
    {
        var token = suppliedToken ?? cts.Token;
        if (token.IsCancellationRequested || (collect && found is { Count: >= 2 })) return false;
        if (mappings.Count == left.Components.Count)
        {
            if (inverse.Count == right.Components.Count && ValidateNets(mappings))
            {
                found?.Add(new Dictionary<string, ComponentMapping>(mappings, StringComparer.Ordinal));
                return true;
            }
            return false;
        }

        var next = left.Components
            .Where(c => !mappings.ContainsKey(c.Id))
            .OrderBy(c => Candidates(c, inverse).Count)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .First();

        foreach (var candidate in Candidates(next, inverse))
        {
            token.ThrowIfCancellationRequested();
            var preferredPins = lockedPins.TryGetValue((next.Id, candidate.Id), out var pins) ? pins : [];
            var mapping = BuildMapping(next, candidate, preferredPins);
            if (mapping is null || !Compatible(mapping, mappings, out _)) continue;
            mappings[next.Id] = mapping;
            inverse[candidate.Id] = next.Id;
            var result = Search(mappings, inverse, found, found is not null, token);
            inverse.Remove(candidate.Id);
            mappings.Remove(next.Id);
            if (result || token.IsCancellationRequested || (found is not null && found.Count >= 2)) return result;
        }

        return false;
    }

    private List<FlatComponent> Candidates(FlatComponent component, Dictionary<string, string> inverse)
    {
        if (lockedLeft.TryGetValue(component.Id, out var lockedId))
            return inverse.ContainsKey(lockedId) ? [] : [rc[lockedId]];

        return right.Components
            .Where(c => !inverse.ContainsKey(c.Id))
            .Where(c => !rejected.Contains((component.Id, c.Id)))
            .Where(c => forbiddenPair is not (var left, var right) ||
                !(string.Equals(left, component.Id, StringComparison.Ordinal) &&
                  string.Equals(right, c.Id, StringComparison.Ordinal)))
            .Where(c => StaticCompatible(component, c))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static bool StaticCompatible(FlatComponent a, FlatComponent b)
    {
        if (!string.Equals(a.Type, b.Type, StringComparison.OrdinalIgnoreCase) ||
            a.IsPort != b.IsPort ||
            a.Pins.Count != b.Pins.Count ||
            a.Parameters.Count != b.Parameters.Count)
            return false;

        foreach (var parameter in a.Parameters)
        {
            if (!b.Parameters.TryGetValue(parameter.Key, out var other) ||
                !SameScalar(parameter.Value, other))
                return false;
        }
        return true;
    }

    private static bool SameScalar(string a, string b) =>
        string.Equals(a, b, StringComparison.Ordinal) ||
        (double.TryParse(a, out var da) && double.TryParse(b, out var db) && Math.Abs(da - db) < 0.0000001);

    private ComponentMapping? BuildMapping(FlatComponent a, FlatComponent b, IReadOnlyList<PinPair> preferred)
    {
        var leftPins = a.Pins.Keys.OrderBy(p => p, StringComparer.Ordinal).ToList();
        var rightPins = b.Pins.Keys.OrderBy(p => p, StringComparer.Ordinal).ToList();

        var graph = leftPins.ToDictionary(
            leftPin => leftPin,
            leftPin => rightPins.Where(rightPin => PinAllowed(a, leftPin, rightPin)).ToList(),
            StringComparer.Ordinal);

        foreach (var preference in preferred)
        {
            if (!graph.TryGetValue(preference.LeftPin, out var options)) return null;
            var forced = options.FirstOrDefault(p =>
                string.Equals(p, preference.RightPin, StringComparison.OrdinalIgnoreCase));
            if (forced is null) return null;
            graph[preference.LeftPin] = [forced];
        }

        var assignment = BipartiteMatch(graph);
        if (assignment is null) return null;
        return new ComponentMapping
        {
            LeftId = a.Id,
            RightId = b.Id,
            Pins = leftPins
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(leftPin => new PinPair { LeftPin = leftPin, RightPin = assignment[leftPin] })
                .ToList()
        };
    }

    private bool PinAllowed(FlatComponent component, string leftPin, string rightPin)
    {
        if (component.IsPort)
            return string.Equals(leftPin, rightPin, StringComparison.OrdinalIgnoreCase);
        if (string.Equals(leftPin, rightPin, StringComparison.OrdinalIgnoreCase)) return true;
        var rule = rules.SwappablePins.FirstOrDefault(r =>
            string.Equals(r.DeviceType, component.Type, StringComparison.OrdinalIgnoreCase));
        return rule is not null &&
               rule.Pins.Any(p => string.Equals(p, leftPin, StringComparison.OrdinalIgnoreCase)) &&
               rule.Pins.Any(p => string.Equals(p, rightPin, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string>? BipartiteMatch(Dictionary<string, List<string>> graph)
    {
        var chosenByRight = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var left in graph.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!Augment(left, graph, chosenByRight, new HashSet<string>(StringComparer.Ordinal)))
                return null;
        }

        foreach (var (right, matchedLeft) in chosenByRight)
            result[matchedLeft] = right;
        return result;
    }

    private static bool Augment(
        string left,
        Dictionary<string, List<string>> graph,
        Dictionary<string, string> chosenByRight,
        HashSet<string> visited)
    {
        foreach (var right in graph[left].OrderBy(r => r, StringComparer.Ordinal))
        {
            if (!visited.Add(right)) continue;
            if (!chosenByRight.ContainsKey(right) ||
                Augment(chosenByRight[right], graph, chosenByRight, visited))
            {
                chosenByRight[right] = left;
                return true;
            }
        }
        return false;
    }

    private bool Compatible(
        ComponentMapping mapping,
        IReadOnlyDictionary<string, ComponentMapping> mappings,
        out Dictionary<string, string> netMap)
    {
        netMap = BuildNetMap(mappings);
        return PinNets(mapping, netMap);
    }

    private bool PinNets(ComponentMapping mapping, Dictionary<string, string> netMap)
    {
        foreach (var pin in mapping.Pins)
        {
            var leftNet = NetFor(mapping.LeftId, pin.LeftPin, left);
            var rightNet = NetFor(mapping.RightId, pin.RightPin, right);
            if (leftNet is null || rightNet is null) return false;
            if (netMap.TryGetValue(leftNet, out var existing))
            {
                if (!string.Equals(existing, rightNet, StringComparison.Ordinal)) return false;
            }
            else if (netMap.Values.Any(v => string.Equals(v, rightNet, StringComparison.Ordinal)))
            {
                return false;
            }
            else
            {
                netMap[leftNet] = rightNet;
            }
        }
        return true;
    }

    private static string? NetFor(string componentId, string pin, FlatNetlist netlist)
    {
        foreach (var (net, endpoints) in netlist.Nets)
        {
            if (endpoints.Any(endpoint =>
                string.Equals(endpoint.ComponentId, componentId, StringComparison.Ordinal) &&
                string.Equals(endpoint.Pin, pin, StringComparison.Ordinal)))
                return net;
        }
        return null;
    }

    private Dictionary<string, string> BuildNetMap(IEnumerable<KeyValuePair<string, ComponentMapping>> mappings)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mapping in mappings.Select(kv => kv.Value))
            if (!PinNets(mapping, map)) return map;
        return map;
    }

    private bool ValidateNets(IReadOnlyDictionary<string, ComponentMapping> mappings)
    {
        var map = BuildNetMap(mappings);
        var leftConnected = left.Nets.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var rightConnected = right.Nets.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        return map.Count == leftConnected.Count &&
               map.Values.ToHashSet(StringComparer.Ordinal).Count == rightConnected.Count &&
               map.Keys.All(leftConnected.Contains) &&
               map.Values.All(rightConnected.Contains);
    }

    private static string? FindBySource(FlatNetlist netlist, string sourceId) =>
        netlist.Components
            .Where(c => string.Equals(c.SourceId, sourceId, StringComparison.Ordinal))
            .Select(c => c.Id)
            .FirstOrDefault();

    private static List<IReadOnlyList<T>> Combinations<T>(IReadOnlyList<T> source, int size)
    {
        var result = new List<IReadOnlyList<T>>();
        Combine(source, size, 0, new List<T>(), result);
        return result;
    }

    private static void Combine<T>(
        IReadOnlyList<T> source,
        int size,
        int start,
        List<T> current,
        List<IReadOnlyList<T>> result)
    {
        if (current.Count == size)
        {
            result.Add(current.ToList());
            return;
        }

        for (var i = start; i < source.Count; i++)
        {
            current.Add(source[i]);
            Combine(source, size, i + 1, current, result);
            current.RemoveAt(current.Count - 1);
        }
    }

    private AnalysisResult Complete(Dictionary<string, ComponentMapping> map, List<Diagnostic> diagnostics)
    {
        var componentMappings = ToMappings(map);
        var netMap = BuildNetMap(map)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var certificate = new MappingCertificate
        {
            LeftFingerprint = left.Fingerprint,
            RightFingerprint = right.Fingerprint,
            RuleVersion = rules.Version,
            RuleFingerprint = ruleFingerprint,
            Components = componentMappings,
            Nets = netMap,
            IssuedAt = now
        };
        certificate.CertificateSha256 = CertificateHasher.Hash(certificate);

        return new AnalysisResult
        {
            Status = AnalysisStatus.Equivalent,
            Mappings = componentMappings,
            Diagnostics = diagnostics,
            Certificate = certificate,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        };
    }

    private static List<ComponentMapping> ToMappings(Dictionary<string, ComponentMapping> map) =>
        map.Values
            .OrderBy(m => m.LeftId, StringComparer.Ordinal)
            .Select(m => new ComponentMapping
            {
                LeftId = m.LeftId,
                RightId = m.RightId,
                Pins = m.Pins
                    .OrderBy(p => p.LeftPin, StringComparer.Ordinal)
                    .Select(p => new PinPair { LeftPin = p.LeftPin, RightPin = p.RightPin })
                    .ToList()
            })
            .ToList();

    private AnalysisResult NonEquivalent(List<Diagnostic> diagnostics)
    {
        var witness = BuildWitness();
        return new AnalysisResult
        {
            Status = AnalysisStatus.NotEquivalent,
            UnmatchedLeft = left.Components.Select(c => c.Id).Except(MappedIds(left), StringComparer.Ordinal).Order().ToList(),
            UnmatchedRight = right.Components.Select(c => c.Id).Except(MappedIds(right), StringComparer.Ordinal).Order().ToList(),
            Diagnostics = diagnostics,
            Witness = witness,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        };
    }

    private IEnumerable<string> MappedIds(FlatNetlist netlist)
    {
        var best = new Dictionary<string, ComponentMapping>(StringComparer.Ordinal);
        try
        {
            Search(best, new Dictionary<string, string>(StringComparer.Ordinal), [], collect: false);
        }
        catch (OperationCanceledException)
        {
        }
        return netlist == left ? best.Keys : best.Values.Select(v => v.RightId);
    }

    private DistinguishingSubgraph BuildWitness()
    {
        var chooseLeft = left.Components.Count >= right.Components.Count;
        var source = chooseLeft ? left : right;
        var target = chooseLeft ? right : left;
        var side = chooseLeft ? "left" : "right";
        var sourceComponents = source.Components.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();

        foreach (var component in sourceComponents)
        {
            if (!target.Components.Any(c => StaticCompatible(component, c)))
                return Witness(side, source, [component.Id], "component has no compatible candidate");
        }

        var subset = sourceComponents.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (CanEmbed(source, target, subset))
            return Witness(side, source, subset.OrderBy(x => x, StringComparer.Ordinal).ToList(), "full graph unexpectedly embeds; counts differ");

        for (var radius = 1; radius <= sourceComponents.Count; radius++)
        {
            foreach (var seed in sourceComponents)
            {
                var candidate = Neighborhood(source, seed.Id, radius);
                if (!CanEmbed(source, target, candidate))
                {
                    var reducedFurther = true;
                    while (reducedFurther && candidate.Count > 1)
                    {
                        reducedFurther = false;
                        foreach (var removable in candidate.OrderBy(x => x, StringComparer.Ordinal).ToList())
                        {
                            if (string.Equals(removable, seed.Id, StringComparison.Ordinal)) continue;
                            var reduced = candidate
                                .Where(x => !string.Equals(x, removable, StringComparison.Ordinal))
                                .ToHashSet(StringComparer.Ordinal);
                            if (!CanEmbed(source, target, reduced))
                            {
                                candidate = reduced;
                                reducedFurther = true;
                                break;
                            }
                        }
                    }
                    return Witness(side, source, candidate.OrderBy(x => x, StringComparer.Ordinal).ToList(), "minimal non-embeddable connected neighborhood");
                }
            }
        }

        return Witness(side, source, subset.Order().ToList(), "graph counts differ");
    }

    private bool CanEmbed(FlatNetlist source, FlatNetlist target, HashSet<string> subset)
    {
        var components = subset.Select(id => source.Components.First(c => c.Id == id)).ToList();
        if (components.Count > target.Components.Count) return false;
        var map = new Dictionary<string, ComponentMapping>(StringComparer.Ordinal);
        var inverse = new Dictionary<string, string>(StringComparer.Ordinal);
        return EmbedSearch(source, target, components, map, inverse);
    }

    private bool EmbedSearch(
        FlatNetlist source,
        FlatNetlist target,
        List<FlatComponent> remaining,
        Dictionary<string, ComponentMapping> map,
        Dictionary<string, string> inverse)
    {
        if (remaining.Count == 0) return true;
        var next = remaining
            .OrderBy(c => target.Components.Count(x => !inverse.ContainsKey(x.Id) && StaticCompatible(c, x)))
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .First();
        var tail = remaining.Where(c => c.Id != next.Id).ToList();

        foreach (var candidate in target.Components.Where(c => !inverse.ContainsKey(c.Id) && StaticCompatible(next, c)))
        {
            var mapping = BuildMapping(next, candidate, []);
            if (mapping is null || !Compatible(mapping, map, out _)) continue;
            map[next.Id] = mapping;
            inverse[candidate.Id] = next.Id;
            if (EmbedSearch(source, target, tail, map, inverse)) return true;
            inverse.Remove(candidate.Id);
            map.Remove(next.Id);
        }
        return false;
    }

    private static HashSet<string> Neighborhood(FlatNetlist netlist, string seed, int radius)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { seed };
        var frontier = new HashSet<string>(StringComparer.Ordinal) { seed };
        for (var i = 0; i < radius; i++)
        {
            var nextFrontier = new HashSet<string>(StringComparer.Ordinal);
            foreach (var componentId in frontier)
            {
                var nets = netlist.Nets
                    .Where(kv => kv.Value.Any(e => e.ComponentId == componentId))
                    .Select(kv => kv.Key);
                foreach (var net in nets)
                {
                    foreach (var neighbor in netlist.Nets[net].Select(e => e.ComponentId))
                    {
                        if (result.Add(neighbor)) nextFrontier.Add(neighbor);
                    }
                }
            }
            frontier = nextFrontier;
        }
        return result;
    }

    private static DistinguishingSubgraph Witness(string side, FlatNetlist netlist, List<string> componentIds, string reason)
    {
        var ids = componentIds.ToHashSet(StringComparer.Ordinal);
        var netIds = netlist.Nets
            .Where(kv => kv.Value.Any(e => ids.Contains(e.ComponentId)))
            .Select(kv => kv.Key)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        return new DistinguishingSubgraph
        {
            Side = side,
            Reason = reason,
            ComponentIds = componentIds,
            NetIds = netIds,
            Components = netlist.Components.Where(c => ids.Contains(c.Id)).ToList()
        };
    }
}

public static class CertificateHasher
{
    public static string Hash(MappingCertificate certificate)
    {
        var payload = new
        {
            algorithm = certificate.Algorithm,
            leftFingerprint = certificate.LeftFingerprint,
            rightFingerprint = certificate.RightFingerprint,
            ruleVersion = certificate.RuleVersion,
            ruleFingerprint = certificate.RuleFingerprint,
            components = certificate.Components
                .OrderBy(c => c.LeftId, StringComparer.Ordinal)
                .Select(c => new
                {
                    left = c.LeftId,
                    right = c.RightId,
                    pins = c.Pins
                        .OrderBy(p => p.LeftPin, StringComparer.Ordinal)
                        .Select(p => new { left = p.LeftPin, right = p.RightPin })
                }),
            nets = certificate.Nets
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new { left = kv.Key, right = kv.Value })
        };
        return Hashing.Sha256(System.Text.Json.JsonSerializer.Serialize(payload, Hashing.JsonOptions));
    }

    public static bool Verify(MappingCertificate certificate) =>
        string.Equals(certificate.CertificateSha256, Hash(certificate), StringComparison.Ordinal);
}
