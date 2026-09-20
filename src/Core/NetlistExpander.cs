namespace PairwiseGsb.Core;

public sealed partial class NetlistExpander
{
    private readonly AliasIndex componentAliases;
    private readonly AliasIndex portAliases;
    private readonly AliasIndex netAliases;
    private readonly AliasIndex parameterAliases;
    private readonly Dictionary<string, ResistorArrayRule> resistorArrays;

    public NetlistExpander(RuleSet rules)
    {
        Rules = rules;
        componentAliases = AliasIndex.From(rules.ComponentAliases);
        portAliases = AliasIndex.From(rules.PortAliases);
        netAliases = AliasIndex.From(rules.NetAliases);
        parameterAliases = AliasIndex.From(rules.ParameterAliases);
        resistorArrays = rules.ResistorArrays.ToDictionary(rule => rule.DeviceType, StringComparer.Ordinal);
    }

    public RuleSet Rules { get; }

    public ExpandedNetlist Expand(NetlistDocument document)
    {
        var expanded = new ExpandedNetlist { Source = document, Rules = Rules };
        var netById = document.Nets.ToDictionary(net => net.Id, StringComparer.Ordinal);
        var moduleById = document.Modules.ToDictionary(module => module.Id, StringComparer.Ordinal);
        var powerNetIds = ResolvePowerNets(document, netById);
        var netIds = new HashSet<string>(StringComparer.Ordinal) { "" };
        foreach (var net in document.Nets.OrderBy(net => net.Id, StringComparer.Ordinal))
        {
            netIds.Add(net.Id);
            var canonicalId = CanonicalNet(net.Id);
            var aliases = net.Aliases?.Select(netAliases.Canonical).Distinct().ToList() ?? [];
            expanded.Nets.Add(new ExpandedNet(canonicalId, net.Name, aliases, powerNetIds.Contains(net.Id)));
        }

        DetectHierarchyCycle(document, moduleById, expanded.Diagnostics);

        foreach (var port in document.Ports.OrderBy(port => port.Id, StringComparer.Ordinal))
        {
            var netId = port.Net ?? port.Id;
            netIds.Add(netId);
            expanded.Components.Add(new ExpandedComponent(
                $"PORT:{port.Id}",
                portAliases.Canonical(port.Name),
                "__PORT__",
                ["PAD"],
                [],
                $"PORT:{port.Id}",
                true,
                port.Direction));
            expanded.Connections.Add(new ExpandedConnection($"PORT:{port.Id}", "PAD", CanonicalNet(netId)));
        }

        foreach (var device in document.Devices.OrderBy(device => device.Id, StringComparer.Ordinal))
        {
            AddDevice(expanded, device, device.Id, new HashSet<string>(StringComparer.Ordinal), netById, powerNetIds, netIds);
        }

        var topInstances = document.Instances.OrderBy(instance => instance.Id, StringComparer.Ordinal).ToList();
        ExpandInstances(expanded, topInstances, moduleById, netById, powerNetIds, netIds, "", new Stack<string>());

        DetectDanglingPorts(document, expanded);
        DetectPowerShorts(document, expanded, powerNetIds);
        DetectParameterUnitMismatches(expanded);
        return expanded;
    }

    private void ExpandInstances(
        ExpandedNetlist expanded,
        IEnumerable<ModuleInstance> instances,
        IReadOnlyDictionary<string, ModuleDefinition> modules,
        IReadOnlyDictionary<string, NetDefinition> netById,
        HashSet<string> powerNetIds,
        HashSet<string> netIds,
        string prefix,
        Stack<string> path)
    {
        foreach (var instance in instances)
        {
            var instancePath = $"{prefix}{instance.Id}";
            if (path.Contains(instance.ModuleId))
            {
                expanded.Diagnostics.Add(new Diagnostic("hierarchy-cycle", DiagnosticSeverity.Error,
                    $"模块 {instance.ModuleId} 的层次引用形成循环。", null, instancePath));
                continue;
            }

            if (!modules.TryGetValue(instance.ModuleId, out var module))
            {
                expanded.Diagnostics.Add(new Diagnostic("missing-module", DiagnosticSeverity.Error,
                    $"实例 {instance.Id} 引用了不存在的模块 {instance.ModuleId}。", null, instancePath));
                continue;
            }

            path.Push(instance.ModuleId);
            foreach (var device in module.Devices)
            {
                AddDevice(expanded, device, $"{instancePath}/{device.Id}", new HashSet<string>(StringComparer.Ordinal), netById, powerNetIds, netIds, instance.PortMap);
            }

            var childInstances = module.Instances.Select(child => MapChildInstance(child, instancePath, instance.PortMap, module)).ToList();
            ExpandInstances(expanded, childInstances, modules, netById, powerNetIds, netIds, $"{instancePath}/", path);
            path.Pop();
        }
    }

    private ModuleInstance MapChildInstance(ModuleInstance child, string parentPath, IReadOnlyDictionary<string, string> parentMap, ModuleDefinition parentModule)
    {
        var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (childModulePort, childLocalNet) in child.PortMap)
        {
            mapped[childModulePort] = parentMap.TryGetValue(childLocalNet, out var parentNet) ? parentNet : childLocalNet;
        }

        return new ModuleInstance($"{parentPath}/{child.Id}", child.Name, child.ModuleId, mapped);
    }

    private void AddDevice(
        ExpandedNetlist expanded,
        Device device,
        string expandedId,
        HashSet<string> duplicatePins,
        IReadOnlyDictionary<string, NetDefinition> netById,
        HashSet<string> powerNetIds,
        HashSet<string> netIds,
        IReadOnlyDictionary<string, string>? portMap = null)
    {
        var type = CanonicalComponentType(device.Type);
        var parameters = NormalizeParameters(device.Parameters, expanded.Diagnostics, expandedId);
        if (resistorArrays.TryGetValue(device.Type, out var arrayRule) || resistorArrays.TryGetValue(type, out arrayRule!))
        {
            foreach (var elementPin in arrayRule.ElementPins)
            {
                var pin = device.Pins.FirstOrDefault(connection => connection.Pin == elementPin);
                var common = device.Pins.FirstOrDefault(connection => connection.Pin == arrayRule.CommonPin);
                if (pin == null || common == null)
                {
                    continue;
                }

                var id = $"{expandedId}:{elementPin}";
                var elementParameters = AssignElementParameters(parameters, arrayRule.ResistanceParameter, elementPin, elementPin, expanded.Diagnostics, expandedId);
                expanded.Components.Add(new ExpandedComponent(id, $"{device.Name}_{elementPin}", "R",
                    ["1", "2"], elementParameters, device.Id));
                AddConnection(expanded, id, "1", common.Net, netById, portMap, powerNetIds, netIds);
                AddConnection(expanded, id, "2", pin.Net, netById, portMap, powerNetIds, netIds);
            }

            return;
        }

        var pins = new List<string>();
        foreach (var pinConnection in device.Pins)
        {
            if (!duplicatePins.Add(pinConnection.Pin))
            {
                expanded.Diagnostics.Add(new Diagnostic("duplicate-pin", DiagnosticSeverity.Error,
                    $"器件 {device.Id} 的引脚 {pinConnection.Pin} 重复。", null, expandedId));
                continue;
            }

            pins.Add(pinConnection.Pin);
            AddConnection(expanded, expandedId, pinConnection.Pin, pinConnection.Net, netById, portMap, powerNetIds, netIds);
        }

        expanded.Components.Add(new ExpandedComponent(expandedId, device.Name, type, pins, parameters, device.Id));
    }

    private static IReadOnlyList<ParameterValue> AssignElementParameters(
        IReadOnlyList<ParameterValue> source,
        string resistanceKey,
        string elementPin,
        string elementName,
        List<Diagnostic> diagnostics,
        string path)
    {
        var list = source.ToList();
        var index = list.FindIndex(parameter => parameter.Key == resistanceKey);
        if (index >= 0)
        {
            list[index] = list[index] with { Key = "R" };
            return list;
        }

        var elementSpecific = source.FirstOrDefault(parameter =>
            parameter.Key.Equals($"{resistanceKey}_{elementName}", StringComparison.OrdinalIgnoreCase) ||
            parameter.Key.Equals($"{resistanceKey}{elementName}", StringComparison.OrdinalIgnoreCase));
        if (elementSpecific is not null)
        {
            list.Remove(elementSpecific);
            list.Add(elementSpecific with { Key = "R" });
            return list;
        }

        diagnostics.Add(new Diagnostic("missing-array-element-value", DiagnosticSeverity.Error,
            $"电阻阵列元素 {elementPin} 缺少电阻参数。", null, path));
        return list;
    }

    private void AddConnection(ExpandedNetlist expanded, string componentId, string pin, string netReference,
        IReadOnlyDictionary<string, NetDefinition> netById, IReadOnlyDictionary<string, string>? portMap,
        HashSet<string> powerNetIds, HashSet<string> netIds)
    {
        var localNet = portMap is not null && portMap.TryGetValue(netReference, out var mapped) ? mapped : netReference;
        if (!netIds.Contains(localNet) && !netById.ContainsKey(localNet) && localNet != "")
        {
            expanded.Diagnostics.Add(new Diagnostic("undefined-net", DiagnosticSeverity.Error,
                $"连接引用了未声明网络 {localNet}。", null, componentId));
        }

        var netId = CanonicalNet(localNet);
        if (!expanded.Nets.Any(net => net.Id == netId))
        {
            expanded.Nets.Add(new ExpandedNet(netId, localNet, [], powerNetIds.Contains(localNet)));
        }
        netIds.Add(localNet);

        expanded.Connections.Add(new ExpandedConnection(componentId, pin, netId));
    }

    private string CanonicalNet(string netId)
    {
        return netAliases.Canonical(netId);
    }

    private string CanonicalComponentType(string type)
    {
        var canonical = componentAliases.Canonical(type);
        return canonical.ToUpperInvariant() switch
        {
            "R" => "R",
            "C" => "C",
            "L" => "L",
            "Q" => "Q",
            "M" => "M",
            _ => canonical
        };
    }
}
