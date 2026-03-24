using System.Collections.Immutable;
using System.Text.Json.Nodes;
using WincheDb.Core.Ast;
using WincheDb.Core.Infrastructure;
using WincheDb.Core.Models;
using WincheDb.DocumentStore.Abstraction;

namespace WincheDb.DocumentStore.Services;

internal static class AccessRuleEvaluator
{
    public static async Task EvaluateAsync(
        StoreOptions options,
        AccessOperation operation,
        string? path = null,
        Query? query = null,
        JsonObject? incomingData = null,
        Func<string, Task<Document?>>? getExisting = null,
        CancellationToken ct = default)
    {
        var context = BuildContext(operation, path, query, incomingData, getExisting);

        // Legacy single callback
        if (options.AccessRule is not null)
        {
            if (!await options.AccessRule(context, ct))
                throw new AccessDeniedException(operation, path);
            return;
        }

        // Pattern-based rules
        if (options.AccessRules is { Count: > 0 } rules)
        {
            await EvaluateRulesAsync(rules, context, operation, path, query, ct);
            return;
        }

        // No rules configured — allow all
    }

    private static async Task EvaluateRulesAsync(
        List<Abstraction.AccessRule> rules,
        AccessContext context,
        AccessOperation operation,
        string? path,
        Query? query,
        CancellationToken ct)
    {
        var matchPath = operation == AccessOperation.Query ? query?.Collection : path;

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
                if (matchPath is null)
                    continue;

                var result = PathPatternMatcher.Match(rule.Path, matchPath);
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
                return; // allowed

            throw new AccessDeniedException(operation, path);
        }

        // No rule matched — deny by default
        throw new AccessDeniedException(operation, path);
    }
    
    private static AccessContext BuildContext(
        AccessOperation operation,
        string? path,
        Query? query,
        JsonObject? incomingData,
        Func<string, Task<Document?>>? getExisting)
    {
        return new AccessContext
        {
            Operation = operation,
            Claims = CallerContext.Claims ?? ImmutableDictionary<string, object?>.Empty,
            Path = path,
            Query = query,
            IncomingData = incomingData,
            GetExistingDocument = getExisting is not null && path is not null
                ? _ => getExisting(path)
                : null,
        };
    }
}
