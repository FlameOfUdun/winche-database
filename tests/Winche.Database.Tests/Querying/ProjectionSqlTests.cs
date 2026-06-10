using Winche.Database.Documents;
using Winche.Database.Querying.Sql;

namespace Winche.Database.Tests.Querying;

/// <summary>
/// Pure unit tests for <see cref="ProjectionSql"/> — no database required.
/// Validates the prefix-tree build logic and the SQL it emits.
/// </summary>
public class ProjectionSqlTests
{
    private static FieldPath F(string p) => FieldPath.Parse(p);

    // Helper: build a fresh ParameterBag and return (sql, bag).
    private static (string Sql, ParameterBag Bag) Build(params string[] paths)
    {
        var bag = new ParameterBag();
        var fieldPaths = paths.Select(F).ToList();
        var sql = ProjectionSql.Build(fieldPaths, "d.data", bag);
        return (sql, bag);
    }

    // ── Prefix-tree / ancestor-wins semantics ─────────────────────────────────

    [Fact]
    public void SingleTopLevelField_EmitsLeafExpression()
    {
        var (sql, _) = Build("name");

        // Should be jsonb_strip_nulls(jsonb_build_object($1, d.data->$2))
        // Both the key and the accessor share the same parameter placeholder.
        Assert.StartsWith("jsonb_strip_nulls(jsonb_build_object(", sql);
        Assert.Contains("d.data->", sql);
    }

    [Fact]
    public void TwoTopLevelFields_BothAppearInOutput()
    {
        var (sql, bag) = Build("name", "score");
        var parameters = bag.ToArray();

        // Two field names → two parameters ("name" and "score").
        Assert.Equal(2, parameters.Length);
        Assert.Equal("name",  parameters[0].Value);
        Assert.Equal("score", parameters[1].Value);
    }

    [Fact]
    public void NestedPath_EmitsMapValueFieldsEnvelope()
    {
        var (sql, _) = Build("address.city");

        // Interior node: must contain the mapValue/fields envelope pattern.
        Assert.Contains("'mapValue'", sql);
        Assert.Contains("'fields'",   sql);
        Assert.Contains("CASE WHEN",  sql);
        Assert.Contains("IS NULL",    sql);
    }

    [Fact]
    public void AncestorWins_SelectBothAddressAndAddressCity_OnlyLeafExpressionEmitted()
    {
        // "address" is a whole-field selector; "address.city" must be ignored.
        var (sql, bag) = Build("address", "address.city");
        var parameters = bag.ToArray();

        // Only one parameter: "address". The "city" sub-path must not appear.
        Assert.Single(parameters);
        Assert.Equal("address", parameters[0].Value);

        // No mapValue envelope (because "address" is a leaf, not an interior node).
        Assert.DoesNotContain("'mapValue'", sql);
    }

    [Fact]
    public void AncestorWins_DescendantBeforeAncestor_AncestorStillWins()
    {
        // Order in the input list must not matter; "address" should still win.
        var (sql, bag) = Build("address.city", "address");
        var parameters = bag.ToArray();

        Assert.Single(parameters);
        Assert.Equal("address", parameters[0].Value);
        Assert.DoesNotContain("'mapValue'", sql);
    }

    [Fact]
    public void ThreeLevelPath_CorrectlyNested()
    {
        var (sql, bag) = Build("a.b.c");
        var parameters = bag.ToArray();

        // Three segments → three parameters: "a", "b", "c".
        Assert.Equal(3, parameters.Length);
        Assert.Equal("a", parameters[0].Value);
        Assert.Equal("b", parameters[1].Value);
        Assert.Equal("c", parameters[2].Value);

        // Two levels of CASE WHEN nesting: one for "a" (interior) and one for "b" (interior).
        Assert.Equal(2, CountOccurrences(sql, "CASE WHEN"));
        // The 'mapValue'/'fields' envelopes must appear (at least 2 of each for 2 interior levels).
        Assert.True(CountOccurrences(sql, "'mapValue'") >= 2);
        Assert.True(CountOccurrences(sql, "'fields'") >= 2);
    }

    [Fact]
    public void TwoSiblingNestedPaths_SameParent_ShareOneInteriorNode()
    {
        var (sql, bag) = Build("address.city", "address.country");
        var parameters = bag.ToArray();

        // Three distinct field names: "address", "city", "country".
        var names = parameters.Select(p => (string)p.Value!).ToList();
        Assert.Contains("address", names);
        Assert.Contains("city",    names);
        Assert.Contains("country", names);

        // Only one CASE WHEN (one interior node for "address").
        Assert.Equal(1, CountOccurrences(sql, "CASE WHEN"));
    }

    [Fact]
    public void FieldNameIsParameterized_NoLiteralNameInSql()
    {
        var (sql, bag) = Build("secretField");
        var parameters = bag.ToArray();

        // The field name must be a parameter, never interpolated into the SQL.
        Assert.Single(parameters);
        Assert.Equal("secretField", parameters[0].Value);
        Assert.DoesNotContain("secretField", sql); // name must NOT appear literally
    }

    // ── SQL shape ─────────────────────────────────────────────────────────────

    [Fact]
    public void TopLevelLeaf_SqlShape()
    {
        var (sql, _) = Build("displayName");

        // Expected: jsonb_strip_nulls(jsonb_build_object($1, d.data->$1))
        Assert.Equal("jsonb_strip_nulls(jsonb_build_object($1, d.data->$1))", sql);
    }

    [Fact]
    public void TwoTopLevelLeaves_SqlShape()
    {
        var (sql, _) = Build("displayName", "address");

        // Both fields emit leaf expressions: each field uses a single parameter for both the
        // key and the ->> accessor. Order of parameters is not guaranteed; check structure.
        Assert.StartsWith("jsonb_strip_nulls(jsonb_build_object(", sql);
        // Each field: $N, d.data->$N  (two times total)
        Assert.Equal(2, CountOccurrences(sql, "d.data->$"));
        Assert.DoesNotContain("'mapValue'", sql);
    }

    [Fact]
    public void NestedLeaf_SqlShape_AddressCity()
    {
        // "address.city" → address is interior, city is leaf
        // Parameters: $1=address, $2=city
        // Expected SQL shape for the interior node:
        //   jsonb_strip_nulls(jsonb_build_object(
        //     $1,
        //     CASE WHEN d.data->$1->'mapValue'->'fields' IS NULL
        //          THEN NULL
        //          ELSE jsonb_build_object('mapValue', jsonb_build_object('fields',
        //               jsonb_strip_nulls(jsonb_build_object($2, d.data->$1->'mapValue'->'fields'->$2))))
        //     END))
        var (sql, bag) = Build("address.city");
        var parameters = bag.ToArray();

        Assert.Equal(2, parameters.Length);
        Assert.Equal("address", parameters[0].Value);
        Assert.Equal("city",    parameters[1].Value);

        Assert.Contains("CASE WHEN d.data->$1->'mapValue'->'fields' IS NULL", sql);
        Assert.Contains("d.data->$1->'mapValue'->'fields'->$2",               sql);
        Assert.Contains("jsonb_build_object('mapValue', jsonb_build_object('fields'", sql);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int CountOccurrences(string source, string substring)
    {
        var count = 0;
        var idx = 0;
        while ((idx = source.IndexOf(substring, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += substring.Length;
        }
        return count;
    }
}
