using Microsoft.Extensions.Options;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using WincheDatabase.Core.Infrastructure;
using WincheDatabase.Core.Models;
using WincheDatabase.Store;
using WincheDatabase.Store.Models;

namespace WincheDatabase.Store.Services;

public class AccessRuleEvaluator(IOptions<StoreOptions> options)
{
    private readonly StoreOptions _options = options.Value;

    public async Task EvaluateAsync(AccessOperation operation, string path, JsonObject? incomingData = null, Func<string, Task<Document?>>? getExisting = null, CancellationToken ct = default)
    {
        var context = BuildContext(operation, path, incomingData, getExisting);

        if (_options.AccessRules is { Count: > 0 } rules)
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

            context = context with { Params = pathParams };

            if (await rule.Evaluate(context, ct))
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