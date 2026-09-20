namespace PairwiseGsb.Core;

internal sealed class AliasIndex
{
    private readonly Dictionary<string, string> map;

    private AliasIndex(Dictionary<string, string> map)
    {
        this.map = map;
    }

    public string Canonical(string value)
    {
        return map.TryGetValue(Normalize(value), out var canonical) ? canonical : Normalize(value);
    }

    public static AliasIndex From(IEnumerable<AliasGroup> groups)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var canonical = Normalize(group.Canonical);
            map[canonical] = canonical;
            foreach (var alias in group.Aliases)
            {
                map[Normalize(alias)] = canonical;
            }
        }

        return new AliasIndex(map);
    }

    internal static string Normalize(string value)
    {
        return value.Trim().Replace("_", "").Replace("-", "").ToLowerInvariant();
    }
}
