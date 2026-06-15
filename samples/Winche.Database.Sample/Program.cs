using Winche.Database.AspNetCore.DependencyInjection;
using Winche.Database.AspNetCore.Rest.DependencyInjection;
using Winche.Database.AspNetCore.WebSockets.DependencyInjection;
using Winche.Database.DependencyInjection;
using Winche.Database.Sample.Configurations;
using Winche.Rules;
using Winche.Rules.Expressions;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddWincheDatabase(opts =>
    {
        opts.ConnectionString =
            builder.Configuration.GetConnectionString("WincheDatabase") ??
            builder.Configuration.GetConnectionString("DefaultConnection") ??
            throw new InvalidOperationException("No connection string found for WincheDatabase.");

        opts.AddHook<DocumentUpdateHook>();
        opts.MapClaims(_ => new Dictionary<string, object?> { ["uid"] = "user-123" });
        opts.UseRules(r =>
            r.Match("userData/{userId}/{document=**}", owned =>
                owned.Allow(RuleOperations.All, Expr.Auth("uid").Eq(Expr.Param("userId")))));
    });
builder.Services.AddWincheDatabaseWsApi();

builder.Services.AddOpenApi();

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    app.UseHttpsRedirection();
}
await app.InitializeWincheDatabaseAsync();
app.UseWebSockets();
app.MapWincheDatabaseWsApi();
app.MapWincheDatabaseRestApi();

app.Start();
app.WaitForShutdown();
