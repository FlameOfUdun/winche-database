using System.Text.Json.Nodes;

namespace WincheDatabase.Core.Infrastructure;

public static class DocumentDataMerger
{
    public static JsonObject DeepMerge(JsonObject target, JsonObject patch)
    {
        var result = MergeNodes(target, patch);
        return result as JsonObject ?? [];
    }

    private static JsonNode? MergeNodes(JsonNode? target, JsonNode? patch)
    {
        if (patch is null)
            return null;

        if (target is JsonObject targetObj && patch is JsonObject patchObj)
            return MergeObjects(targetObj, patchObj);

        return patch.DeepClone();
    }

    private static JsonObject MergeObjects(JsonObject target, JsonObject patch)
    {
        var result = new JsonObject();

        foreach (var (key, value) in target)
        {
            if (patch.ContainsKey(key))
            {
                var patchValue = patch[key];
                if (patchValue is null)
                    continue;

                var merged = MergeNodes(value, patchValue);
                if (merged is not null)
                    result[key] = merged;
            }
            else
            {
                result[key] = value?.DeepClone();
            }
        }

        foreach (var (key, value) in patch)
        {
            if (!target.ContainsKey(key) && value is not null)
                result[key] = value.DeepClone();
        }

        return result;
    }
}
