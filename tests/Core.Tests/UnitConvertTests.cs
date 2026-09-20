using PairwiseGsb.Core;

namespace Core.Tests;

public sealed class UnitConvertTests
{
    [Theory]
    [InlineData("kohm", 1, 1000.0, "ohm")]
    [InlineData("MΩ", 2, 2_000_000.0, "ohm")]
    [InlineData("megohm", 3, 3_000_000.0, "ohm")]
    [InlineData("uF", 5, 0.000005, "f")]
    public void Normalizes_si_prefixes(string unit, double input, double expected, string expectedUnit)
    {
        UnitConvert.TryNormalize(new ParameterValue("R", input, unit, null), out var normalized, out _);

        Assert.NotNull(normalized.NumericValue);
        Assert.True(Math.Abs(normalized.NumericValue!.Value - expected) < 0.000001);
        Assert.Equal(expectedUnit, normalized.Unit);
    }

    [Fact]
    public void Different_dimensions_are_not_equal()
    {
        Assert.False(UnitConvert.SameDimension("ohm", "F"));
        Assert.True(UnitConvert.SameDimension("kohm", "megohm"));
    }
}
