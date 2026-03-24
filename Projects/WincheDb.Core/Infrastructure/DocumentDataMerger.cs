using System.Text.Json.Nodes;

namespace WincheDb.Core.Infrastructure;

/// <summary>
/// Provides deep merge functionality for JSON documents following RFC 7396 (JSON Merge Patch).
/// </summary>
public static class DocumentDataMerger
{
    /// <summary>
    /// Performs a deep merge of patch into target.
    /// - Objects: recursively merged
    /// - Arrays: replaced (not merged element-wise)
    /// - Primitives: replaced
    /// - null in patch: removes the key from target
    /// </summary>
    public static JsonObject DeepMerge(JsonObject target, JsonObject patch)
    {
        var result = MergeNodes(target, patch);
        return result as JsonObject ?? [];
    }

    private static JsonNode? MergeNodes(JsonNode? target, JsonNode? patch)
    {
        // If patch is null, the key should be removed (return null)
        if (patch is null)
            return null;

        // If both are objects, merge recursively
        if (target is JsonObject targetObj && patch is JsonObject patchObj)
        {
            return MergeObjects(targetObj, patchObj);
        }

        // Otherwise, patch replaces target (arrays, primitives, or type mismatch)
        return patch.DeepClone();
    }

    private static JsonObject MergeObjects(JsonObject target, JsonObject patch)
    {
        var result = new JsonObject();

        // Copy all properties from target
        foreach (var (key, value) in target)
        {
            if (patch.ContainsKey(key))
            {
                // Key exists in patch - merge or replace
                var patchValue = patch[key];
                if (patchValue is null)
                {
                    // null in patch means remove the key - skip adding to result
                    continue;
                }

                var merged = MergeNodes(value, patchValue);
                if (merged is not null)
                {
                    result[key] = merged;
                }
            }
            else
            {
                // Key only in target - preserve it
                result[key] = value?.DeepClone();
            }
        }

        // Add new keys from patch that don't exist in target
        foreach (var (key, value) in patch)
        {
            if (!target.ContainsKey(key) && value is not null)
            {
                result[key] = value.DeepClone();
            }
        }

        return result;
    }
}
