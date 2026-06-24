using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime.Hosting;
using Winche.Database.Runtime.Ttl;

namespace Winche.Database.Tests.Ttl;

public class UseTtlTests
{
    private static List<TtlPolicy> RegisteredPolicies(IServiceCollection services) =>
        services.Where(d => d.ServiceType == typeof(TtlPolicy))
                .Select(d => (TtlPolicy)d.ImplementationInstance!)
                .ToList();

    [Fact]
    public void UseTtl_Builder_RegistersAccumulatingPolicies()
    {
        var services = new ServiceCollection();
        var opts = new WincheDatabaseOptions(services);

        opts.UseTtl(t => t.Add("things", "expireAt").Add(TtlPolicy.For("logs", "ttl")));
        opts.UseTtl(TtlPolicy.For("sessions", "exp"));   // params overload accumulates

        var policies = RegisteredPolicies(services);
        Assert.Equal(3, policies.Count);
        Assert.Contains(policies, p => p.CollectionId == "things" && p.Field.Segments is ["expireAt"]);
        Assert.Contains(policies, p => p.CollectionId == "logs" && p.Field.Segments is ["ttl"]);
        Assert.Contains(policies, p => p.CollectionId == "sessions" && p.Field.Segments is ["exp"]);
    }

    [Fact]
    public void TtlConfig_Defaults()
    {
        var cfg = new TtlConfig();
        Assert.Equal(TimeSpan.FromMinutes(5), cfg.SweepInterval);
        Assert.Equal(500, cfg.BatchSize);
    }

    [Fact]
    public void AddWincheDatabase_WithTtl_RegistersSweeperAndPolicy()
    {
        var services = new ServiceCollection();
        services.AddWincheDatabase(o =>
        {
            o.ConnectionString = "Host=localhost;Database=db";   // not connected — registration only
            o.UseTtl(t => t.Add("c", "ttl"));
        });

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(TtlSweeper));
        Assert.Contains(services, d => d.ServiceType == typeof(TtlPolicy));
    }
}
