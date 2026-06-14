using Winche.Database.Authorization;
using Winche.Database.Values;
using Winche.Rules;
using Xunit;

namespace Winche.Database.IntegrationTests;

public class RuleValueToValueTests
{
    [Fact]
    public void RoundTrips_ScalarKinds()
    {
        Assert.IsType<IntegerValue>(RuleValueToValue.Convert(RuleValue.Int(5)));
        Assert.IsType<DoubleValue>(RuleValueToValue.Convert(RuleValue.Double(5.0)));
        Assert.IsType<StringValue>(RuleValueToValue.Convert(RuleValue.String("x")));
        Assert.IsType<ReferenceValue>(RuleValueToValue.Convert(RuleValue.Path("a/b")));
        Assert.IsType<NullValue>(RuleValueToValue.Convert(RuleValue.Null));
    }
}
