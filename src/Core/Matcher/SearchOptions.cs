namespace PairwiseGsb.Core.Matcher;

public sealed record SearchOptions(
    IReadOnlySet<string> LockedPairs = null!,
    IReadOnlySet<string> VetoedPairs = null!,
    TimeSpan? Timeout = null,
    long AmbiguityNodeBudget = 2000)
{
    public static SearchOptions Default { get; } = new(
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        TimeSpan.FromMilliseconds(500),
        2000);
}

public static class PairKey
{
    public static string Create(string leftId, string rightId)
    {
        return $"{leftId}⇒{rightId}";
    }
}
