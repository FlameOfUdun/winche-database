using Winche.Database.Values;
using Winche.Rules;
using Winche.Rules.Evaluation;

namespace Winche.Database.Authorization;

/// <summary>
/// <see cref="IRuleValueComparer"/> whose semantics are the database's canonical
/// <see cref="ValueComparer"/> — so the rules prover proves "safe" using the exact equality/ordering
/// the query engine executes. Equality mirrors typed equality (same rank + compare == 0); ordering is
/// defined only within the same rank, with NaN excluded (mirrors <c>FilterEvaluator</c>).
/// </summary>
public sealed class WincheRuleValueComparer : IRuleValueComparer
{
    /// <summary>Shared instance.</summary>
    public static readonly WincheRuleValueComparer Instance = new();

    /// <inheritdoc/>
    public bool AreEqual(RuleValue a, RuleValue b)
    {
        var x = RuleValueToValue.Convert(a);
        var y = RuleValueToValue.Convert(b);
        return x.Rank == y.Rank && ValueComparer.Instance.Compare(x, y) == 0;
    }

    /// <inheritdoc/>
    public bool TryCompare(RuleValue a, RuleValue b, out int result)
    {
        result = 0;
        var x = RuleValueToValue.Convert(a);
        var y = RuleValueToValue.Convert(b);
        if (x.Rank != y.Rank) return false;
        if (IsNaN(x) || IsNaN(y)) return false;
        result = ValueComparer.Instance.Compare(x, y);
        return true;
    }

    private static bool IsNaN(Value v) => v is DoubleValue d && double.IsNaN(d.Value);
}
