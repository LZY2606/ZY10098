namespace PairwiseGsb.Core;

public sealed partial class NetlistExpander
{
    private HashSet<string> ResolvePowerNets(NetlistDocument document, IReadOnlyDictionary<string, NetDefinition> netById)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in document.PowerNetNames)
        {
            var match = document.Nets.FirstOrDefault(net =>
                net.Id == name ||
                string.Equals(net.Name, name, StringComparison.OrdinalIgnoreCase) ||
                net.Aliases?.Contains(name, StringComparer.OrdinalIgnoreCase) == true);
            if (match is not null)
            {
                result.Add(match.Id);
                continue;
            }

            result.Add(name);
        }

        foreach (var port in document.Ports.Where(port =>
                     port.Direction is PortDirection.Power or PortDirection.Ground))
        {
            result.Add(port.Net ?? port.Id);
        }

        foreach (var net in document.Nets)
        {
            var canonicalName = netAliases.Canonical(net.Name ?? net.Id);
            if (canonicalName is "VDD" or "GND" ||
                document.PowerNetNames.Contains(net.Id, StringComparer.OrdinalIgnoreCase) ||
                document.PowerNetNames.Contains(net.Name ?? "", StringComparer.OrdinalIgnoreCase))
            {
                result.Add(net.Id);
            }
        }

        return result;
    }

    private static void DetectHierarchyCycle(
        NetlistDocument document,
        IReadOnlyDictionary<string, ModuleDefinition> modules,
        List<Diagnostic> diagnostics)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var module in document.Modules)
        {
            Visit(module.Id, module.Id, new Stack<string>());
        }

        void Visit(string rootId, string moduleId, Stack<string> path)
        {
            if (state.TryGetValue(moduleId, out var status) && status == 1)
            {
                diagnostics.Add(new Diagnostic("hierarchy-cycle", DiagnosticSeverity.Error,
                    $"模块 {moduleId} 的层次引用形成循环：{string.Join(" -> ", path.Reverse())} -> {moduleId}。", null, string.Join("/", path)));
                return;
            }

            if (state.TryGetValue(moduleId, out status) && status == 2)
            {
                return;
            }

            state[moduleId] = 1;
            path.Push(moduleId);
            if (modules.TryGetValue(moduleId, out var module))
            {
                foreach (var instance in module.Instances)
                {
                    if (modules.ContainsKey(instance.ModuleId))
                    {
                        Visit(rootId, instance.ModuleId, path);
                    }
                }
            }

            path.Pop();
            state[moduleId] = 2;
        }
    }

    private IReadOnlyList<ParameterValue> NormalizeParameters(
        IReadOnlyList<ParameterValue>? parameters,
        List<Diagnostic> diagnostics,
        string path)
    {
        if (parameters is null)
        {
            return [];
        }

        return parameters.Select(parameter =>
        {
            var key = parameterAliases.Canonical(parameter.Key).ToUpperInvariant();
            var renamed = parameter with { Key = key };
            if (UnitConvert.TryNormalize(renamed, out var normalized, out var diagnostic) && diagnostic is not null)
            {
                diagnostics.Add(diagnostic with { Side = null, Path = path });
            }

            return normalized;
        }).ToList();
    }

    private static void DetectDanglingPorts(NetlistDocument document, ExpandedNetlist expanded)
    {
        foreach (var port in document.Ports)
        {
            var netId = port.Net ?? port.Id;
            var used = expanded.Connections.Any(connection =>
                connection.ComponentId == $"PORT:{port.Id}" &&
                expanded.Connections.Any(other => other.NetId == connection.NetId && other.ComponentId != connection.ComponentId));
            if (!used)
            {
                expanded.Diagnostics.Add(new Diagnostic("dangling-port", DiagnosticSeverity.Error,
                    $"外部端口 {port.Name} 悬空，没有连接任何器件。", null, $"PORT:{port.Id}"));
            }
        }
    }

    private void DetectPowerShorts(NetlistDocument document, ExpandedNetlist expanded, HashSet<string> powerNetIds)
    {
        DetectPowerShortsInternal(document, expanded, powerNetIds);
    }

    private void DetectPowerShortsInternal(
        NetlistDocument document,
        ExpandedNetlist expanded,
        HashSet<string> powerNetIds)
    {
        var powerCanonicalGroups = powerNetIds
            .Select(netAliases.Canonical)
            .Where(id => id is "VDD" or "GND")
            .GroupBy(id => id)
            .ToDictionary(group => group.Key, group => group.Count());

        foreach (var (canonical, count) in powerCanonicalGroups)
        {
            if (count > 1)
            {
                expanded.Diagnostics.Add(new Diagnostic("power-short", DiagnosticSeverity.Error,
                    $"{canonical} 电源网络在展开后被短接到一起。", null, canonical));
            }
        }

        foreach (var component in expanded.Components.Where(component => string.Equals(component.Type, "R", StringComparison.OrdinalIgnoreCase)))
        {
            var resistance = component.Parameters.FirstOrDefault(parameter => parameter.Key == "R");
            if (resistance?.NumericValue != 0)
            {
                continue;
            }

            var nets = expanded.Connections
                .Where(connection => connection.ComponentId == component.Id)
                .Select(connection => connection.NetId)
                .Distinct()
                .ToList();
            var powerEnds = nets
                .Where(netId =>
                {
                    var exists = powerNetIds.Contains(netId);
                    if (!exists && netId is "vdd" or "gnd")
                    {
                        return true;
                    }

                    return exists;
                })
                .Select(netId => netId switch
                {
                    "vdd" => "VDD",
                    "gnd" => "GND",
                    _ => document.Ports
                        .Where(port => (port.Net ?? port.Id) == netId && port.Direction is PortDirection.Power or PortDirection.Ground)
                        .Select(port => portAliases.Canonical(port.Name))
                        .FirstOrDefault() ?? netAliases.Canonical(document.Nets.FirstOrDefault(net => net.Id == netId)?.Name ?? netId)
                })
                .Where(id => id is "VDD" or "GND")
                .Distinct()
                .ToList();
            if (powerEnds.Contains("VDD") && powerEnds.Contains("GND"))
            {
                expanded.Diagnostics.Add(new Diagnostic("power-short", DiagnosticSeverity.Error,
                    $"零欧姆电阻 {component.Id} 将 VDD 与 GND 短接。", null, component.Id));
            }
        }
    }

    private static void DetectParameterUnitMismatches(ExpandedNetlist expanded)
    {
        foreach (var component in expanded.Components)
        {
            foreach (var parameter in component.Parameters)
            {
                var expected = parameter.Key switch
                {
                    "R" => "ohm",
                    "C" => "f",
                    "L" => "h",
                    _ => null
                };

                if (expected is not null && !string.IsNullOrEmpty(parameter.Unit) &&
                    !UnitConvert.SameDimension(parameter.Unit, expected))
                {
                    expanded.Diagnostics.Add(new Diagnostic("parameter-unit-mismatch", DiagnosticSeverity.Error,
                        $"器件 {component.Name} 的参数 {parameter.Key} 单位为 {parameter.Unit}，与 {expected} 不一致。",
                        null, component.Id));
                }
            }
        }
    }
}
