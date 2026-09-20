namespace PairwiseGsb.Core.Matcher;

public sealed partial class SemanticMatcher
{
    private List<string>? FindLockConflict(ExpandedNetlist left, ExpandedNetlist right)
    {
        if (options.LockedPairs.Count == 0)
        {
            return null;
        }

        var parsed = new List<(string Key, string LeftId, string RightId)>();
        foreach (var key in options.LockedPairs)
        {
            var separator = key.IndexOf('⇒');
            if (separator <= 0 ||
                !left.Components.Any(component => component.Id == key[..separator]) ||
                !right.Components.Any(component => component.Id == key[(separator + 1)..]))
            {
                return [key];
            }

            var leftId = key[..separator];
            var rightId = key[(separator + 1)..];
            var leftComponent = left.Components.First(component => component.Id == leftId);
            var rightComponent = right.Components.First(component => component.Id == rightId);
            if (!CompatibleType(leftComponent, rightComponent) || options.VetoedPairs.Contains(key))
            {
                return [key];
            }

            parsed.Add((key, leftId, rightId));
        }

        if (parsed.GroupBy(item => item.LeftId, StringComparer.Ordinal).Any(group => group.Select(item => item.RightId).Distinct(StringComparer.Ordinal).Count() > 1) ||
            parsed.GroupBy(item => item.RightId, StringComparer.Ordinal).Any(group => group.Select(item => item.LeftId).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            var duplicate = parsed.GroupBy(item => item.LeftId).First(group => group.Count() > 1).Select(item => item.Key).ToList();
            return duplicate;
        }

        if (options.LockedPairs.Count <= 1)
        {
            return null;
        }

        var unconstrained = new SemanticMatcher(rules).Search(left, right, options with
        {
            LockedPairs = new HashSet<string>(StringComparer.Ordinal),
            Timeout = TimeSpan.FromSeconds(5)
        });
        if (unconstrained.Status != SessionStatus.Equivalent)
        {
            return null;
        }

        for (var size = 1; size < parsed.Count; size++)
        {
            foreach (var subset in Combinations(parsed, size))
            {
                var remainingKeys = parsed.Except(subset).Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
                var remainingOptions = options with
                {
                    LockedPairs = remainingKeys,
                    Timeout = TimeSpan.FromSeconds(5)
                };
                if (new SemanticMatcher(rules).Search(left, right, remainingOptions).Status == SessionStatus.Equivalent)
                {
                    return subset.Select(item => item.Key).ToList();
                }
            }
        }

        return parsed.Count == 1 ? parsed.Select(item => item.Key).ToList() : parsed.Select(item => item.Key).ToList();
    }

    private void CollectAmbiguities(
        ExpandedNetlist left,
        ExpandedNetlist right,
        IReadOnlyList<ComponentMapping> mappings)
    {
        ambiguities = [];
        var mappingByLeft = mappings.ToDictionary(mapping => mapping.LeftComponentId, StringComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            var leftComponent = left.Components.First(component => component.Id == mapping.LeftComponentId);
            foreach (var alternative in right.Components.Where(component =>
                         component.Id != mapping.RightComponentId &&
                         CompatibleType(leftComponent, component) &&
                         ParametersCompatible(leftComponent, component)))
            {
                var probe = new SearchState();
                var probePins = EnumeratePinMappings(leftComponent, alternative).FirstOrDefault();
                if (probePins is null)
                {
                    continue;
                }

                probe.ComponentByLeft[leftComponent.Id] = alternative.Id;
                probe.ComponentByRight[alternative.Id] = leftComponent.Id;
                probe.PinsByPair[PairKey.Create(leftComponent.Id, alternative.Id)] = probePins;
                if (TryAssignNets(leftComponent, alternative, probePins, left, right, probe))
                {
                    ambiguities.Add(new CandidateMapping(
                        mapping.LeftComponentId,
                        alternative.Id,
                        mapping.LeftName,
                        alternative.Name,
                        1,
                        "该候选满足局部类型、参数与连接约束；可锁定或否决后继续搜索。"));
                    break;
                }
            }
        }
    }

    private static DistinguishingSubgraph CountWitness(ExpandedNetlist left, ExpandedNetlist right)
    {
        var reason = left.Components.Count != right.Components.Count
            ? $"展开后器件/端口数量不同：{left.Components.Count} vs {right.Components.Count}。"
            : $"连接端子数量不同：{left.Connections.Count} vs {right.Connections.Count}。";
        var componentIds = left.Components.Take(10).Select(component => component.Id)
            .Concat(right.Components.Take(10).Select(component => component.Id)).ToHashSet(StringComparer.Ordinal);
        return Subgraph(reason, componentIds, left, right);
    }

    private static DistinguishingSubgraph DiagnosticWitness(
        ExpandedNetlist left,
        ExpandedNetlist right,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        var paths = diagnostics.Select(diagnostic => diagnostic.Path).Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.Ordinal);
        var componentIds = left.Components.Concat(right.Components)
            .Where(component => paths.Contains(component.Id) || paths.Contains(component.OriginDeviceId))
            .Select(component => component.Id).ToHashSet(StringComparer.Ordinal);
        return Subgraph("存在必须先处理的诊断。", componentIds, left, right);
    }

    private static DistinguishingSubgraph LockWitness(
        ExpandedNetlist left,
        ExpandedNetlist right,
        IReadOnlyList<string> lockKeys)
    {
        var componentIds = lockKeys.SelectMany(key =>
        {
            var parts = key.Split('⇒');
            return parts;
        }).ToHashSet(StringComparer.Ordinal);
        return Subgraph("锁定约束之间或锁定与网表约束发生冲突。", componentIds, left, right, lockKeys);
    }

    private static DistinguishingSubgraph Subgraph(
        string reason,
        HashSet<string> componentIds,
        ExpandedNetlist left,
        ExpandedNetlist right,
        IReadOnlyList<string>? lockKeys = null)
    {
        var immediateConnections = left.Connections.Concat(right.Connections)
            .Where(connection => componentIds.Contains(connection.ComponentId)).ToList();
        var netIds = immediateConnections.Select(connection => connection.NetId).ToHashSet(StringComparer.Ordinal);
        var neighboringComponents = left.Connections.Concat(right.Connections)
            .Where(connection => netIds.Contains(connection.NetId))
            .Select(connection => connection.ComponentId)
            .ToHashSet(StringComparer.Ordinal);
        var allComponents = left.Components.Concat(right.Components)
            .Where(component => componentIds.Contains(component.Id) || neighboringComponents.Contains(component.Id))
            .ToList();
        var allConnections = left.Connections.Concat(right.Connections)
            .Where(connection => allComponents.Any(component => component.Id == connection.ComponentId))
            .ToList();
        var allNets = left.Nets.Concat(right.Nets)
            .Where(net => netIds.Contains(net.Id))
            .ToList();
        return new DistinguishingSubgraph(reason, allComponents, allNets, allConnections, lockKeys);
    }

    private static IEnumerable<List<T>> Combinations<T>(IReadOnlyList<T> values, int size)
    {
        var result = new List<T>();
        foreach (var item in Enumerate(values, size, 0, result))
        {
            yield return item;
        }
    }

    private static IEnumerable<List<T>> Enumerate<T>(IReadOnlyList<T> values, int size, int start, List<T> current)
    {
        if (current.Count == size)
        {
            yield return current.ToList();
            yield break;
        }

        for (var index = start; index < values.Count; index++)
        {
            current.Add(values[index]);
            foreach (var result in Enumerate(values, size, index + 1, current))
            {
                yield return result;
            }

            current.RemoveAt(current.Count - 1);
        }
    }
}
