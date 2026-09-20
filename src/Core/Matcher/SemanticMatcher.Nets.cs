namespace PairwiseGsb.Core.Matcher;

public sealed partial class SemanticMatcher
{
    private bool TryAssignNets(
        ExpandedComponent leftComponent,
        ExpandedComponent rightComponent,
        IReadOnlyDictionary<string, string> pinMapping,
        ExpandedNetlist left,
        ExpandedNetlist right,
        SearchState state)
    {
        var pending = new List<(string LeftNet, string RightNet)>();
        var leftConnections = left.Connections
            .Where(connection => connection.ComponentId == leftComponent.Id)
            .OrderBy(connection => connection.Pin, StringComparer.Ordinal)
            .ToList();

        foreach (var leftConnection in leftConnections)
        {
            if (!pinMapping.TryGetValue(leftConnection.Pin, out var rightPin))
            {
                return false;
            }

            var rightConnection = right.Connections.FirstOrDefault(connection =>
                connection.ComponentId == rightComponent.Id && connection.Pin == rightPin);
            if (rightConnection is null)
            {
                return false;
            }

            pending.Add((leftConnection.NetId, rightConnection.NetId));
        }

        foreach (var (leftNet, rightNet) in pending)
        {
            if (state.NetByLeft.TryGetValue(leftNet, out var existingRight) && existingRight != rightNet)
            {
                return false;
            }

            if (state.NetByRight.TryGetValue(rightNet, out var existingLeft) && existingLeft != leftNet)
            {
                return false;
            }
        }

        foreach (var (leftNet, rightNet) in pending)
        {
            state.NetByLeft[leftNet] = rightNet;
            state.NetByRight[rightNet] = leftNet;
        }

        return ParametersCompatible(leftComponent, rightComponent);
    }

    private bool ParametersCompatible(ExpandedComponent left, ExpandedComponent right)
    {
        if (left.Parameters.Count != right.Parameters.Count)
        {
            return false;
        }

        foreach (var leftParameter in left.Parameters.OrderBy(parameter => parameter.Key))
        {
            var rightParameter = right.Parameters.FirstOrDefault(parameter => parameter.Key == leftParameter.Key);
            if (rightParameter is null)
            {
                return false;
            }

            if (!UnitConvert.SameDimension(leftParameter.Unit, rightParameter.Unit))
            {
                return false;
            }

            if (leftParameter.NumericValue.HasValue && rightParameter.NumericValue.HasValue)
            {
                if (Math.Abs(leftParameter.NumericValue.Value - rightParameter.NumericValue.Value) > rules.ParameterTolerance)
                {
                    return false;
                }
            }
            else if (!string.Equals(leftParameter.RawValue, rightParameter.RawValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
