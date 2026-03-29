using System.Collections.Immutable;
using System.Text.Json.Nodes;
using WincheDb.Core.Infrastructure;
using WincheDb.Core.Models;
using WincheDb.DocumentStore.Models;

namespace WincheDb.DocumentStore.Infrastructure;

internal static class AccessRuleEvaluator
{
    public static Task EvaluateAsync(StoreOptions options, AccessOperation operation, string path, JsonObject? incomingData = null, Func<string, Task<Document?>>? getExisting = null, CancellationToken ct = default)
    {
        return EvaluateCoreAsync(options, operation, path, incomingData, getExisting, ct);
    }

    private static async Task EvaluateCoreAsync(StoreOptions options, AccessOperation operation, string path, JsonObject? incomingData, Func<string, Task<Document?>>? getExisting, CancellationToken ct)
    {
        var context = BuildContext(operation, path, incomingData, getExisting);
        if (options.AccessRules is { Count: > 0 } rules)
            await EvaluateRulesAsync(rules, context, operation, path, ct);
    }

    private static async Task EvaluateRulesAsync(List<AccessRule> rules, AccessContext context, AccessOperation operation, string path, CancellationToken ct)
    {
        foreach (var rule in rules)
        {
            if (rule.Operations is not null && !rule.Operations.Contains(operation))
                continue;

            IReadOnlyDictionary<string, string> pathParams;

            if (rule.Path is null)
            {
                pathParams = ImmutableDictionary<string, string>.Empty;
            }
            else
            {
                var result = PathPatternMatcher.Match(rule.Path, path);
                if (!result.IsMatch)
                    continue;

                pathParams = result.Params;
            }

            var ruleContext = new RuleAccessContext
            {
                Context = context,
                PathParams = pathParams,
            };

            if (await rule.Evaluate(ruleContext, ct))
                return;

            throw new AccessDeniedException(operation, path);
        }
    }

    private static AccessContext BuildContext(AccessOperation operation, string path, JsonObject? incomingData, Func<string, Task<Document?>>? getExisting)
    {
        return new AccessContext
        {
            Operation = operation,
            Claims = CallerContext.Claims ?? ImmutableDictionary<string, object?>.Empty,
            Path = path,
            IncomingData = incomingData,
            GetExistingDocument = getExisting is not null && path is not null ? _ => getExisting(path) : null,
        };
    }
}