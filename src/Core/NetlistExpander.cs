using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetCompare.Core;

public sealed partial class NetlistExpander
{
    private readonly RuleSet rules;
    private Dictionary<string, ModuleDefinition> modules = new(StringComparer.Ordinal);
    private readonly List<Diagnostic> diagnostics = [];
    private readonly Dictionary<string, FlatComponent> components = new();
    private readonly Dictionary<string, List<NetEndpoint>> nets = new(StringComparer.Ordinal);
    private readonly HashSet<string> addedEndpoints = new();

    private NetlistExpander(RuleSet rules) => this.rules = rules;

    public static FlatNetlist Expand(NetlistRevision revision, RuleSet rules)
    {
        var expander = new NetlistExpander(rules);
        expander.Build(revision);
        return new FlatNetlist
        {
            NetlistId = revision.NetlistId,
            RevisionId = revision.Id,
            Fingerprint = revision.Fingerprint,
            Components = expander.components.Values
                .OrderBy(c => c.Id, StringComparer.Ordinal)
                .ToList(),
            Nets = expander.nets
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value
                    .OrderBy(e => e.ComponentId, StringComparer.Ordinal)
                    .ThenBy(e => e.Pin, StringComparer.Ordinal)
                    .ToList(), StringComparer.Ordinal),
            Diagnostics = expander.diagnostics
                .OrderBy(d => d.Side, StringComparer.Ordinal)
                .ThenBy(d => d.Kind)
                .ThenBy(d => d.Path ?? "")
                .ThenBy(d => d.Message, StringComparer.Ordinal)
                .ToList()
        };
    }

    public static NetlistDocument ParseDocument(string rawText, string fallbackId, string fallbackName)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<NetlistDocument>(rawText, Hashing.JsonOptions)
                ?? throw new InvalidOperationException("empty document");
            if (string.IsNullOrWhiteSpace(doc.Id)) doc.Id = fallbackId;
            if (string.IsNullOrWhiteSpace(doc.Name)) doc.Name = fallbackName;
            doc.RawText = rawText;
            foreach (var module in doc.Modules)
            {
                if (string.IsNullOrWhiteSpace(module.Id)) module.Id = module.Name;
                module.Name = string.IsNullOrWhiteSpace(module.Name) ? module.Id : module.Name;
            }
            if (string.IsNullOrWhiteSpace(doc.RootModuleId))
                doc.RootModuleId = doc.Modules.FirstOrDefault()?.Id ?? "";
            return doc;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new NetlistFormatException(ex.Message, ex);
        }
    }

    private void Build(NetlistRevision revision)
    {
        var doc = revision.Document;
        modules = doc.Modules
            .GroupBy(m => m.Id)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        if (!modules.TryGetValue(doc.RootModuleId, out var root))
        {
            Add(DiagnosticKind.InvalidNetlist, "root module was not found", "$root");
            return;
        }

        var rootPorts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var port in root.Ports)
        {
            var canonical = CanonicalLocal(root, port.Id);
            rootPorts[canonical] = canonical;
            EnsureNet(canonical);
        }
        ExpandModule(root, "$root", rootPorts, new HashSet<string>(StringComparer.Ordinal));
        ApplyGlobalNetAliases();
        CheckGlobalPowerShort();
        ReportRootDanglingPorts();
    }

    private void ExpandModule(
        ModuleDefinition module,
        string path,
        Dictionary<string, string> externalNets,
        HashSet<string> activeModules)
    {
        if (!activeModules.Add(module.Id))
        {
            Add(DiagnosticKind.HierarchyCycle, $"module '{module.Id}' references itself through hierarchy", path);
            return;
        }

        foreach (var device in module.Devices)
            ExpandDevice(module, device, path, externalNets);

        foreach (var instance in module.Instances)
        {
            ModuleDefinition? child = null;
            if (modules.TryGetValue(instance.ModuleId, out var byId))
                child = byId;
            else
                child = modules.Values.FirstOrDefault(m =>
                    string.Equals(m.Name, instance.ModuleId, StringComparison.OrdinalIgnoreCase));
            if (child is null)
            {
                Add(DiagnosticKind.UnknownModule, $"instance '{instance.Id}' refers to missing module '{instance.ModuleId}'", path + "/" + instance.Id);
                continue;
            }

            var childExternal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var port in child.Ports)
            {
                var canonicalPort = CanonicalLocal(child, port.Id);
                if (!instance.Ports.TryGetValue(port.Id, out var supplied) &&
                    !instance.Ports.TryGetValue(canonicalPort, out supplied))
                {
                    Add(DiagnosticKind.DanglingPort, $"instance port '{child.Id}.{port.Id}' is unconnected", path + "/" + instance.Id);
                    continue;
                }
                childExternal[canonicalPort] = ResolveLocalNet(module, path, supplied, externalNets);
            }

            foreach (var connected in instance.Ports.Keys.Except(child.Ports.Select(p => CanonicalLocal(child, p.Id)), StringComparer.OrdinalIgnoreCase))
                Add(DiagnosticKind.DanglingPort, $"instance '{instance.Id}' connects unknown port '{connected}'", path + "/" + instance.Id);

            ExpandModule(child, path + "/" + instance.Id, childExternal, activeModules);
        }

        activeModules.Remove(module.Id);
    }

    private void ExpandDevice(
        ModuleDefinition module,
        DeviceDefinition device,
        string path,
        Dictionary<string, string> externalNets)
    {
        var arrayRule = rules.ResistorArrays.FirstOrDefault(r =>
            string.Equals(r.DeviceType, device.Type, StringComparison.OrdinalIgnoreCase));
        if (arrayRule is not null)
        {
            ExpandResistorArray(module, device, path, externalNets, arrayRule);
            return;
        }

        var component = new FlatComponent
        {
            Id = path + "/" + device.Id,
            SourceId = path + "/" + device.Id,
            Type = device.Type
        };
        foreach (var pin in device.Pins)
            component.Pins[pin.Key] = ResolveLocalNet(module, path, pin.Value, externalNets);
        foreach (var parameter in device.Parameters)
            component.Parameters[parameter.Key] = NormalizeParameter(device, parameter.Key, parameter.Value, path);
        AddComponent(component);
    }

    private void ExpandResistorArray(
        ModuleDefinition module,
        DeviceDefinition device,
        string path,
        Dictionary<string, string> externalNets,
        ResistorArrayRule rule)
    {
        if (!device.Parameters.TryGetValue(rule.CountParameter, out var countText) ||
            !int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ||
            count <= 0)
        {
            Add(DiagnosticKind.InvalidExpansion, $"resistor array '{device.Id}' has invalid {rule.CountParameter}", path + "/" + device.Id);
            return;
        }

        for (var index = 1; index <= count; index++)
        {
            var pinA = string.Format(CultureInfo.InvariantCulture, rule.PinAFormat, index);
            var pinB = string.Format(CultureInfo.InvariantCulture, rule.PinBFormat, index);
            if (!device.Pins.TryGetValue(pinA, out var netA) || !device.Pins.TryGetValue(pinB, out var netB))
            {
                Add(DiagnosticKind.InvalidExpansion, $"resistor array '{device.Id}' misses pin {pinA} or {pinB}", path + "/" + device.Id);
                continue;
            }

            var componentId = path + "/" + string.Format(CultureInfo.InvariantCulture, rule.NameFormat, device.Id, index);
            var component = new FlatComponent
            {
                Id = componentId,
                SourceId = componentId,
                Type = rule.ElementType
            };
            component.Pins["A"] = ResolveLocalNet(module, path, netA, externalNets);
            component.Pins["B"] = ResolveLocalNet(module, path, netB, externalNets);
            foreach (var parameter in device.Parameters)
            {
                if (string.Equals(parameter.Key, rule.CountParameter, StringComparison.OrdinalIgnoreCase)) continue;
                var key = string.Equals(parameter.Key, rule.ResistanceParameter, StringComparison.OrdinalIgnoreCase)
                    ? rule.ResistanceParameter
                    : parameter.Key;
                component.Parameters[key] = NormalizeParameter(device, key, parameter.Value, componentId);
            }
            AddComponent(component);
        }
    }

    private string NormalizeParameter(DeviceDefinition device, string name, string value, string path)
    {
        var rule = rules.ParameterUnits.FirstOrDefault(r =>
            string.Equals(r.ParameterName, name, StringComparison.OrdinalIgnoreCase));
        if (rule is null) return value;

        var match = ParameterValueRegex().Match(value.Trim());
        if (!match.Success ||
            !double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return value;

        var unit = match.Groups["unit"].Success ? match.Groups["unit"].Value : rule.CanonicalUnit;
        if (!rule.Units.TryGetValue(unit, out var multiplier))
        {
            Add(DiagnosticKind.ParameterUnitMismatch, $"device '{device.Id}' uses unknown unit '{unit}' for {name}", path);
            return value;
        }

        return (number * multiplier).ToString("0.##########", CultureInfo.InvariantCulture);
    }

    private void AddComponent(FlatComponent component)
    {
        components[component.Id] = component;
        foreach (var pin in component.Pins)
        {
            EnsureNet(pin.Value);
            var endpoint = new NetEndpoint(component.Id, pin.Key);
            var key = component.Id + "\u001f" + pin.Key;
            if (addedEndpoints.Add(key))
                nets[pin.Value].Add(endpoint);
        }
    }

    private void EnsureNet(string netId)
    {
        nets.TryAdd(netId, []);
    }

    private string ResolveLocalNet(
        ModuleDefinition module,
        string path,
        string localNet,
        Dictionary<string, string>? externalNets)
    {
        var canonical = CanonicalLocal(module, localNet);
        if (externalNets is not null && externalNets.TryGetValue(canonical, out var external))
            return external;
        return path + "/" + canonical;
    }

    private string CanonicalLocal(ModuleDefinition module, string name)
    {
        var current = name.Trim();
        var aliasRule = rules.PortAliases.FirstOrDefault(r =>
            r.ModuleId == "*" ||
            string.Equals(r.ModuleId, module.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.ModuleId, module.Name, StringComparison.OrdinalIgnoreCase));
        if (aliasRule?.Aliases.TryGetValue(current, out var alias) == true)
            current = alias;

        foreach (var net in module.Nets)
        {
            var group = new[] { net.Id }.Concat(net.Aliases).ToList();
            var match = group.FirstOrDefault(g => string.Equals(g, current, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return net.Id;
        }
        return current;
    }

    private void ApplyGlobalNetAliases()
    {
        var canonicalByNet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in rules.NetAliases)
        {
            var canonical = group.OrderBy(n => n, StringComparer.Ordinal).First();
            foreach (var net in group)
                canonicalByNet[net] = canonical;
        }

        var normalized = new Dictionary<string, List<NetEndpoint>>(StringComparer.Ordinal);
        foreach (var (net, endpoints) in nets)
        {
            var target = canonicalByNet.TryGetValue(net, out var alias) ? alias : net;
            if (!normalized.TryGetValue(target, out var list))
            {
                list = [];
                normalized[target] = list;
            }
            list.AddRange(endpoints);
        }

        nets.Clear();
        foreach (var (net, endpoints) in normalized)
            nets[net] = endpoints
                .DistinctBy(e => e.ComponentId + "\u001f" + e.Pin, StringComparer.Ordinal)
                .ToList();

        foreach (var component in components.Values)
        {
            var updated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (pin, net) in component.Pins)
                updated[pin] = canonicalByNet.TryGetValue(net, out var target) ? target : net;
            component.Pins = updated;
        }
    }

    private void CheckGlobalPowerShort()
    {
        foreach (var group in rules.NetAliases)
        {
            var power = group
                .Where(IsPowerName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var ground = group
                .Where(IsGroundName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (power.Count > 1 || ground.Count > 1 || (power.Count > 0 && ground.Count > 0))
                Add(DiagnosticKind.PowerShort, "power/ground net aliases short distinct supply nets", string.Join(",", group));
        }
    }

    private bool IsPowerName(string name) =>
        rules.PowerNames.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));

    private bool IsGroundName(string name) =>
        rules.GroundNames.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));

    private void Add(DiagnosticKind kind, string message, string? path) =>
        diagnostics.Add(new Diagnostic
        {
            Kind = kind,
            Level = DiagnosticLevel.Error,
            Side = "",
            Message = message,
            Path = path
        });

    private void ReportDanglingPorts()
    {
        foreach (var component in components.Values.Where(c => c.IsPort))
        {
            var connected = component.Pins.Values.Any(net =>
                nets.TryGetValue(net, out var endpoints) &&
                endpoints.Any(endpoint =>
                    !string.Equals(endpoint.ComponentId, component.Id, StringComparison.Ordinal)));
            if (!connected)
                Add(DiagnosticKind.DanglingPort, $"root port '{component.Id["$port/".Length..]}' is dangling", component.Id);
        }
    }

    private void ReportRootDanglingPorts()
    {
        foreach (var rootNet in nets.Keys.Where(net => net.IndexOf('/') < 0).ToList())
        {
            var connected = nets.TryGetValue(rootNet, out var endpoints) && endpoints.Count > 0;
            if (!connected)
                Add(DiagnosticKind.DanglingPort, "root port is dangling", "$port/" + rootNet);
        }
    }

    [GeneratedRegex(@"^\s*(?<value>[-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)\s*(?<unit>[^\s]+)?\s*$")]
    private static partial Regex ParameterValueRegex();
}

public sealed class NetlistFormatException(string message, Exception? inner = null) : Exception(message, inner);
