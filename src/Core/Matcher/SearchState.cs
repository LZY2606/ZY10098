namespace PairwiseGsb.Core.Matcher;

internal sealed class SearchState
{
    public Dictionary<string, string> ComponentByLeft { get; internal set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ComponentByRight { get; internal set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> NetByLeft { get; internal set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> NetByRight { get; internal set; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, string>> PinsByPair { get; } = new(StringComparer.Ordinal);
    public HashSet<string> UsedPairs { get; } = new(StringComparer.Ordinal);
    public long VisitedNodes { get; set; }

    public SearchState Clone()
    {
        var clone = new SearchState
        {
            ComponentByLeft = new Dictionary<string, string>(ComponentByLeft, StringComparer.Ordinal),
            ComponentByRight = new Dictionary<string, string>(ComponentByRight, StringComparer.Ordinal),
            NetByLeft = new Dictionary<string, string>(NetByLeft, StringComparer.Ordinal),
            NetByRight = new Dictionary<string, string>(NetByRight, StringComparer.Ordinal),
            VisitedNodes = VisitedNodes
        };
        foreach (var (pair, pins) in PinsByPair)
        {
            clone.PinsByPair[pair] = new Dictionary<string, string>(pins, StringComparer.Ordinal);
        }

        clone.UsedPairs.UnionWith(UsedPairs);
        return clone;
    }
}
