using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Winche.Database.Runtime.ChangeFeed;

namespace Winche.Database.AspNetCore.HealthChecks;

/// <summary>
/// Reports Degraded when a durable consumer (hook type) is more than <c>maxLag</c> behind, is failing, or has
/// dead letters — Healthy otherwise. It never reports Unhealthy: restarting the node doesn't fix a failing
/// hook, so this should alert, not make a load balancer or orchestrator replace the task.
/// </summary>
public sealed class ChangeFeedHealthCheck(ChangeFeedConsumers consumers, TimeSpan maxLag) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        IReadOnlyList<DurableConsumerStatus> statuses;
        try
        {
            statuses = await consumers.GetStatusAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Degraded("Could not read the change-feed consumers' status", ex);
        }

        var problems = new List<string>();
        var data = new Dictionary<string, object>();
        foreach (var s in statuses)
        {
            data[s.Name] = new { lagSeconds = (long)s.Lag.TotalSeconds, failingSince = s.FailingSince, s.FailingAttempts, s.DeadLetters };
            if (s.Lag > maxLag) problems.Add($"{s.Name} is {s.Lag:g} behind");
            if (s.FailingSince is { } since) problems.Add($"{s.Name} failing since {since:u} ({s.FailingAttempts} attempts)");
            if (s.DeadLetters > 0) problems.Add($"{s.Name} has {s.DeadLetters} dead letter(s)");
        }

        return problems.Count == 0
            ? HealthCheckResult.Healthy(data: data)
            : HealthCheckResult.Degraded(string.Join("; ", problems), data: data);
    }
}

public static class ChangeFeedHealthCheckExtensions
{
    /// <summary>
    /// Adds <see cref="ChangeFeedHealthCheck"/>: Degraded when a hook type is more than <paramref name="maxLag"/>
    /// (default 5 minutes) behind, is failing, or has dead letters.
    /// </summary>
    public static IHealthChecksBuilder AddWincheChangeFeed(
        this IHealthChecksBuilder builder, string name = "winche-change-feed", TimeSpan? maxLag = null, IEnumerable<string>? tags = null) =>
        builder.Add(new HealthCheckRegistration(
            name,
            sp => new ChangeFeedHealthCheck(sp.GetRequiredService<ChangeFeedConsumers>(), maxLag ?? TimeSpan.FromMinutes(5)),
            HealthStatus.Degraded,
            tags));
}
