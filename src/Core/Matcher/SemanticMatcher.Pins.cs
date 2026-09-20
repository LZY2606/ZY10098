namespace PairwiseGsb.Core.Matcher;

public sealed partial class SemanticMatcher
{
    private IEnumerable<Dictionary<string, string>> EnumeratePinMappings(ExpandedComponent left, ExpandedComponent right)
    {
        if (left.Pins.Count != right.Pins.Count)
        {
            yield break;
        }

        var swappable = rules.SwappablePins
            .FirstOrDefault(rule => string.Equals(rule.DeviceType, left.Type, StringComparison.Ordinal))
            ?.PinGroup.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        var groups = left.Pins
            .GroupBy(pin => swappable.Contains(pin) ? "*" : pin)
            .OrderBy(group => group.Key)
            .Select(group => group.OrderBy(pin => pin, StringComparer.Ordinal).ToList())
            .ToList();

        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedRight = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in EnumerateGroup(groups, 0, right.Pins.OrderBy(pin => pin, StringComparer.Ordinal).ToList(), current, usedRight))
        {
            yield return result;
        }
    }

    private static IEnumerable<Dictionary<string, string>> EnumerateGroup(
        List<List<string>> groups,
        int groupIndex,
        List<string> rightPins,
        Dictionary<string, string> current,
        HashSet<string> usedRight)
    {
        if (groupIndex == groups.Count)
        {
            yield return new Dictionary<string, string>(current, StringComparer.Ordinal);
            yield break;
        }

        var group = groups[groupIndex];
        var swappable = group.Count > 1;
        var permutations = new List<List<string>>();
        if (!swappable)
        {
            var pin = group[0];
            if (rightPins.Contains(pin) && !usedRight.Contains(pin))
            {
                permutations.Add([pin]);
            }
        }
        else
        {
            permutations = Permute(group).ToList();
        }

        foreach (var permutation in permutations)
        {
            var available = rightPins.Where(pin => !usedRight.Contains(pin)).ToList();
            if (swappable)
            {
                if (available.Count < permutation.Count)
                {
                    continue;
                }

                var swappableAvailable = available.Where(pin => group.Contains(pin)).ToList();
                if (swappableAvailable.Count < permutation.Count)
                {
                    continue;
                }

                var chosen = swappableAvailable.Take(permutation.Count).ToList();
                for (var index = 0; index < permutation.Count; index++)
                {
                    current[permutation[index]] = chosen[index];
                    usedRight.Add(chosen[index]);
                }

                foreach (var result in EnumerateGroup(groups, groupIndex + 1, rightPins, current, usedRight))
                {
                    yield return result;
                }

                foreach (var pin in chosen)
                {
                    usedRight.Remove(pin);
                }
            }
            else
            {
                var chosen = permutation[0];
                current[group[0]] = chosen;
                usedRight.Add(chosen);
                foreach (var result in EnumerateGroup(groups, groupIndex + 1, rightPins, current, usedRight))
                {
                    yield return result;
                }

                usedRight.Remove(chosen);
            }
        }
    }

    private static IEnumerable<List<string>> Permute(List<string> values)
    {
        if (values.Count <= 1)
        {
            yield return values;
            yield break;
        }

        var array = values.ToArray();
        Array.Sort(array, StringComparer.Ordinal);
        yield return array.ToList();

        while (NextPermutation(array))
        {
            yield return array.ToList();
        }
    }

    private static bool NextPermutation(string[] values)
    {
        var index = values.Length - 2;
        while (index >= 0 && string.Compare(values[index], values[index + 1], StringComparison.Ordinal) >= 0)
        {
            index--;
        }

        if (index < 0)
        {
            return false;
        }

        var swap = values.Length - 1;
        while (string.Compare(values[swap], values[index], StringComparison.Ordinal) <= 0)
        {
            swap--;
        }

        (values[index], values[swap]) = (values[swap], values[index]);
        Array.Reverse(values, index + 1, values.Length - index - 1);
        return true;
    }
}
