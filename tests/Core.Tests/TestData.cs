using System.Text.Json;
using PairwiseGsb.Core;

namespace Core.Tests;

internal static class TestData
{
    public static string Json(NetlistDocument document)
    {
        return JsonSerializer.Serialize(document, CanonicalJson.Options);
    }

    public static NetlistDocument Simple(string name = "simple", bool swapped = false)
    {
        var pins = false
            ? new List<PinConnection> { new("2", "n1"), new("1", "n2") }
            : new List<PinConnection> { new("1", "n1"), new("2", "n2") };
        return new NetlistDocument
        {
            Name = name,
            Ports =
            [
                new("in", "IN", PortDirection.Input, "n1"),
                new("out", "OUT", PortDirection.Output, "n2")
            ],
            Nets = [new("n1"), new("n2")],
            Devices =
            [
                new("r1", "R1", "resistor", pins,
                    [new ParameterValue("RESISTANCE", 1, "kohm", null)])
            ]
        };
    }

    public static NetlistDocument ResistorArray(string name = "array")
    {
        return new NetlistDocument
        {
            Name = name,
            Ports =
            [
                new("com", "COM", PortDirection.InOut, "com"),
                new("a", "A", PortDirection.InOut, "a"),
                new("b", "B", PortDirection.InOut, "b")
            ],
            Nets = [new("com"), new("a"), new("b")],
            Devices =
            [
                new("rn1", "RN1", "RN",
                    [new("COM", "com"), new("R1", "a"), new("R2", "b")],
                    [new("R", 1000, "ohm", null)])
            ]
        };
    }

    public static NetlistDocument SplitResistors(string name = "split")
    {
        return new NetlistDocument
        {
            Name = name,
            Ports =
            [
                new("com", "COM", PortDirection.InOut, "com"),
                new("a", "A", PortDirection.InOut, "a"),
                new("b", "B", PortDirection.InOut, "b")
            ],
            Nets = [new("com"), new("a"), new("b")],
            Devices =
            [
                new("r1", "RN1_R1", "R", [new("1", "com"), new("2", "a")], [new("R", 1000, "ohm", null)]),
                new("r2", "RN1_R2", "R", [new("1", "com"), new("2", "b")], [new("R", 1000, "ohm", null)])
            ]
        };
    }

    public static NetlistDocument PowerShort(string name = "short")
    {
        return new NetlistDocument
        {
            Name = name,
            Ports =
            [
                new("p", "VDD", PortDirection.Power, "vdd"),
                new("g", "GND", PortDirection.Ground, "gnd")
            ],
            Nets = [new("vdd"), new("gnd")],
            PowerNetNames = ["vdd", "gnd"],
            Devices =
            [
                new("r0", "SHORT", "R", [new("1", "vdd"), new("2", "gnd")], [new("R", 0, "ohm", null)])
            ]
        };
    }

    public static NetlistDocument Dangling(string name = "dangling")
    {
        return new NetlistDocument
        {
            Name = name,
            Ports = [new("in", "IN", PortDirection.Input, "n1")],
            Nets = [new("n1")],
            Devices = []
        };
    }

    public static NetlistDocument ManySymmetricResistors(string name = "many")
    {
        const int count = 24;
        return new NetlistDocument
        {
            Name = name,
            Ports = Enumerable.Range(0, count).Select(index => new NetlistPort(
                $"p{index}", $"P{index}", PortDirection.InOut, $"n{index}")).ToList(),
            Nets = Enumerable.Range(0, count).Select(index => new NetDefinition($"n{index}")).ToList(),
            Devices = Enumerable.Range(0, count).Select(index => new Device(
                $"r{index}",
                $"R{index}",
                "R",
                new List<PinConnection> { new("1", $"n{index}"), new("2", $"n{(index + 1) % count}") },
                new List<ParameterValue> { new("R", 1000, "ohm", null) })).ToList()
        };
    }

    public static NetlistDocument WithParameter(NetlistDocument document, ParameterValue parameter)
    {
        return new NetlistDocument
        {
            Name = document.Name,
            Ports = document.Ports,
            Nets = document.Nets,
            Devices = document.Devices
                .Select(device => device.Id == "r1"
                    ? device with { Parameters = new List<ParameterValue> { parameter } }
                    : device)
                .ToList(),
            Modules = document.Modules,
            Instances = document.Instances,
            PowerNetNames = document.PowerNetNames
        };
    }
}
