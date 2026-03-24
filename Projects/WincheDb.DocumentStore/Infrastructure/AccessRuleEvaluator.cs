using System.Collections.Immutable;
using System.Text.Json.Nodes;
using WincheDb.Core.Ast;
using WincheDb.Core.Infrastructure;
using WincheDb.Core.Models;
using WincheDb.DocumentStore.Models;

namespace WincheDb.DocumentStore.Infrastructure;

internal static class AccessRuleEvaluator
{
    public static Task EvaluateAsync(
        StoreOptions options,
        AccessOperation operation,
        string? path = null,
        Query? query = null,
        JsonObject? incomingData = null,
        Func<string, Task<Document?>>? getExisting = null,
        CancellationToken ct = default)
        => EvaluateCoreAsync(options, operation, path, query, incomingData, getExisting, resolvedCollection: query?.Collection, ct);

    private static async Task EvaluateCoreAsync(
        StoreOptions options,
        AccessOperation operation,
        string? path,
        Query? query,
        JsonObject? incomingData,
        Func<string, Task<Document?>>? getExisting,
        string? resolvedCollection,
        CancellationToken ct)
    {
        var effectiveCollection = resolvedCollection ?? query?.Collection;
        var context = BuildContext(operation, path, query, incomingData, getExisting, effectiveCollection);

        if (options.AccessRules is { Count: > 0 } rules)
            await EvaluateRulesAsync(rules, context, operation, path, effectiveCollection, ct);

        if (query?.Include is { Count: > 0 } includes && effectiveCollection is not null)
        {
            foreach (var include in includes)
            {
                var subCollection = ResolveSubCollection(effectiveCollection, include.Collection);

                await EvaluateCoreAsync(
                    options,
                    AccessOperation.Query,
                    path: null,
                    query: include,
                    incomingData: null,
                    getExisting: null,
                    resolvedCollection: subCollection,
                    ct: ct);
            }
        }
    }

    private static async Task EvaluateRulesAsync(
        List<AccessRule> rules,
        AccessContext context,
        AccessOperation operation,
        string? path,
        string? effectiveCollection,
        CancellationToken ct)
    {
        var matchPath = operation == AccessOperation.Query ? effectiveCollection : path;

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
                return;

            throw new AccessDeniedException(operation, matchPath);
        }

        throw new AccessDeniedException(operation, matchPath ?? path);
    }

    private static AccessContext BuildContext(
        AccessOperation operation,
        string? path,
        Query? query,
        JsonObject? incomingData,
        Func<string, Task<Document?>>? getExisting,
        string? effectiveCollection)
    {
        return new AccessContext
        {
            Operation = operation,
            Claims = CallerContext.Claims ?? ImmutableDictionary<string, object?>.Empty,
            Path = path,
            Query = query is not null
                ? query with { Collection = effectiveCollection ?? query.Collection }
                : null,
            IncomingData = incomingData,
            GetExistingDocument = getExisting is not null && path is not null
                ? _ => getExisting(path)
                : null,
        };
    }

    private static string ResolveSubCollection(string parentCollection, string relativeCollection)
        => $"{parentCollection.TrimEnd('/')}/*/{relativeCollection.TrimStart('/')}";
}