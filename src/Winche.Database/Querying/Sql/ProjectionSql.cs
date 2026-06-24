using System.Text;
using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.Querying.Sql;

/// <summary>
/// Builds a JSONB projection expression that reconstructs only the selected <see cref="FieldPath"/>s
/// directly in SQL, so the full document never enters app memory.
///
/// <para>Semantics mirror <c>FieldProjector</c> exactly:</para>
/// <list type="bullet">
///   <item>Paths absent in the document are silently omitted (CASE/strip_nulls).</item>
///   <item>Ancestor wins: if both <c>address</c> and <c>address.city</c> are selected,
///         the whole <c>address</c> field is returned and deeper paths are ignored.</item>
///   <item>Path-through-non-map is silently omitted (the CASE IS NULL check).</item>
/// </list>
/// </summary>
internal static class ProjectionSql
{
    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Returns a SQL expression that evaluates to a JSONB object containing only the
    /// selected fields, suitable for use as <c>&lt;expr&gt; AS data</c> in a SELECT list.
    /// </summary>
    /// <param name="paths">The selected <see cref="FieldPath"/>s (non-empty).</param>
    /// <param name="baseAccessor">SQL expression for the root data column, e.g. <c>d.data</c>.</param>
    /// <param name="bag">Parameter bag; field names are bound as parameters (injection-safe).</param>
    public static string Build(IReadOnlyList<FieldPath> paths, string baseAccessor, ParameterBag bag)
    {
        // Build a prefix tree from the selected paths.
        var root = BuildTree(paths);

        // Emit the outermost jsonb_strip_nulls(jsonb_build_object(…)) expression.
        var entries = EmitEntries(root.Children, baseAccessor, bag);
        return $"jsonb_strip_nulls(jsonb_build_object({entries}))";
    }

    // ── Prefix tree ───────────────────────────────────────────────────────────

    private sealed class TrieNode
    {
        /// <summary>True if a selected path terminates at this node (the node is a whole-field leaf).</summary>
        public bool IsLeaf { get; set; }

        public Dictionary<string, TrieNode> Children { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Fold all field paths into a prefix tree.
    /// Ancestor-wins rule: if a node is already a leaf (a whole-field selector) we do not
    /// record any deeper children under it — matching <c>FieldProjector</c>'s behaviour.
    /// </summary>
    private static TrieNode BuildTree(IReadOnlyList<FieldPath> paths)
    {
        var root = new TrieNode();
        foreach (var path in paths)
        {
            var node = root;
            foreach (var segment in path.Segments)
            {
                // If a shallower path already made this an ancestor leaf, stop descending.
                if (node.IsLeaf) break;

                if (!node.Children.TryGetValue(segment, out var child))
                    node.Children[segment] = child = new TrieNode();
                node = child;
            }
            // Mark as a leaf (whole-field selector at this depth).
            node.IsLeaf = true;
            // Drop any previously accumulated children — the ancestor wins.
            node.Children.Clear();
        }
        return root;
    }

    // ── SQL emitters ──────────────────────────────────────────────────────────

    /// <summary>
    /// Emits the comma-separated key/value pairs for a <c>jsonb_build_object(…)</c> call,
    /// one pair per child of <paramref name="children"/>.
    /// </summary>
    private static string EmitEntries(
        Dictionary<string, TrieNode> children,
        string accessor,
        ParameterBag bag)
    {
        var parts = new List<string>(children.Count * 2);
        foreach (var (name, node) in children)
        {
            // Field name is a bound parameter — no string interpolation of user input.
            var nameParam = bag.Add(name);
            var valueExpr = node.IsLeaf
                ? LeafExpr(accessor, nameParam)
                : InteriorExpr(name, node, accessor, nameParam, bag);

            parts.Add(nameParam);
            parts.Add(valueExpr);
        }
        return string.Join(", ", parts);
    }

    /// <summary>
    /// A leaf node: emit the tagged value at <c>accessor -&gt; nameParam</c>.
    /// NULL (absent key) propagates up; <c>jsonb_strip_nulls</c> at the root removes it.
    /// </summary>
    private static string LeafExpr(string accessor, string nameParam) =>
        $"{accessor}->{nameParam}";

    /// <summary>
    /// An interior node: we need to descend through the map envelope
    /// (<c>mapValue → fields</c>) and recurse.  We emit a CASE expression so that
    /// when the key is absent OR the value is not a map the whole entry becomes NULL
    /// and is stripped by <c>jsonb_strip_nulls</c> at the root.
    /// </summary>
    private static string InteriorExpr(
        string name,
        TrieNode node,
        string accessor,
        string nameParam,
        ParameterBag bag)
    {
        // Path into the child's mapValue.fields envelope (tags are fixed SQL literals).
        var childAccessor = $"{accessor}->{nameParam}->'{WireTags.MapValue}'->'{WireTags.Fields}'";

        // Recurse for the children of this interior node.
        var childEntries = EmitEntries(node.Children, childAccessor, bag);

        // Re-use nameParam for the CASE guard (it was already added to the bag above in EmitEntries).
        var sb = new StringBuilder();
        sb.Append($"CASE WHEN {accessor}->{nameParam}->'{WireTags.MapValue}'->'{WireTags.Fields}' IS NULL");
        sb.Append(" THEN NULL");
        sb.Append($" ELSE jsonb_build_object('{WireTags.MapValue}', jsonb_build_object('{WireTags.Fields}',");
        sb.Append($" jsonb_strip_nulls(jsonb_build_object({childEntries})))) END");
        return sb.ToString();
    }
}
