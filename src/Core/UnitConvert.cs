using System.Globalization;

namespace PairwiseGsb.Core;

public static class UnitConvert
{
    private static readonly Dictionary<string, double> PrefixFactors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["f"] = 1e-15,
        ["p"] = 1e-12,
        ["n"] = 1e-9,
        ["u"] = 1e-6,
        ["μ"] = 1e-6,
        ["m"] = 1e-3,
        ["k"] = 1e3,
        ["K"] = 1e3,
        ["meg"] = 1e6,
        ["M"] = 1e6,
        ["g"] = 1e9,
        ["t"] = 1e12
    };

    private static readonly HashSet<string> KnownUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "ohm", "Ω", "f", "h", "v", "a", "w", "s"
    };

    public static bool TryNormalize(ParameterValue parameter, out ParameterValue normalized, out Diagnostic? diagnostic)
    {
        normalized = parameter;
        diagnostic = null;
        if (!parameter.NumericValue.HasValue || string.IsNullOrWhiteSpace(parameter.Unit))
        {
            return true;
        }

        var unit = parameter.Unit.Trim();
        var multiplier = 1.0;
        var dimension = unit;
        foreach (var (prefix, factor) in PrefixFactors.OrderByDescending(pair => pair.Key.Length))
        {
            var matches = unit.StartsWith(prefix, StringComparison.Ordinal)
                || (prefix.Length == 1 && unit.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (matches)
            {
                var suffix = unit[prefix.Length..];
                if (KnownUnits.Contains(suffix))
                {
                    multiplier = factor;
                    dimension = suffix;
                    break;
                }
            }
        }

        if (!KnownUnits.Contains(dimension) && !KnownUnits.Contains(unit))
        {
            diagnostic = new Diagnostic(
                "unknown-unit",
                DiagnosticSeverity.Warning,
                $"参数 {parameter.Key} 使用未知单位 {parameter.Unit}。",
                null,
                parameter.Key);
            return true;
        }

        if (KnownUnits.Contains(unit))
        {
            dimension = unit;
            multiplier = 1;
        }

        normalized = parameter with { NumericValue = parameter.NumericValue * multiplier, Unit = NormalizeDimension(dimension) };
        return true;
    }

    public static bool SameDimension(string? left, string? right)
    {
        TryNormalize(new ParameterValue("", 1, left, null), out var leftNormalized, out _);
        TryNormalize(new ParameterValue("", 1, right, null), out var rightNormalized, out _);
        return string.Equals(NormalizeDimension(leftNormalized.Unit), NormalizeDimension(rightNormalized.Unit), StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryParseNumber(object? value, out double? number)
    {
        number = null;
        if (value is null)
        {
            return true;
        }

        switch (value)
        {
            case double d:
                number = d;
                return true;
            case int i:
                number = i;
                return true;
            case long l:
                number = l;
                return true;
            case string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                number = parsed;
                return true;
            default:
                return false;
        }
    }

    private static string NormalizeDimension(string? unit)
    {
        return (unit ?? "").Trim().ToLowerInvariant() switch
        {
            "ω" => "ohm",
            "" => "",
            var value => value
        };
    }
}
