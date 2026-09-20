namespace PairwiseGsb.Core;

public static class DefaultRules
{
    public const string Version = "rules-2026.09.1";

    public static RuleSet Create()
    {
        return new RuleSet
        {
            Version = Version,
            ParameterTolerance = 1e-9,
            ComponentAliases =
            [
                new("R", ["resistor", "RES"]),
                new("C", ["capacitor", "CAP"]),
                new("L", ["inductor", "IND"]),
                new("Q", ["transistor", "BJT"]),
                new("M", ["mosfet", "MOS"])
            ],
            PortAliases =
            [
                new("VDD", ["VCC", "AVDD", "DVDD"]),
                new("GND", ["VSS", "AGND", "DGND"]),
                new("IN", ["INPUT", "I"]),
                new("OUT", ["OUTPUT", "O"])
            ],
            NetAliases =
            [
                new("VDD", ["VCC", "AVDD", "DVDD"]),
                new("GND", ["VSS", "AGND", "DGND"])
            ],
            ParameterAliases =
            [
                new("R", ["RES", "RESISTANCE"]),
                new("C", ["CAP", "CAPACITANCE"]),
                new("L", ["IND", "INDUCTANCE"])
            ],
            ResistorArrays =
            [
                new("RN", "COM", ["R1", "R2", "R3", "R4", "R5", "R6", "R7", "R8"], "R")
            ],
            SwappablePins =
            [
                new("R", ["1", "2"]),
                new("C", ["1", "2"]),
                new("L", ["1", "2"])
            ]
        };
    }
}
