using Winche.Database.AspNetCore.DependencyInjection;
using Winche.Database.AspNetCore.Rest.DependencyInjection;
using Winche.Database.AspNetCore.WebSockets.DependencyInjection;
using Winche.Database.DependencyInjection;
using Winche.Database.Sample.Configurations;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddWincheDatabase(builder.Configuration, (config) =>
    {
        config.AddDocumentAccessRule<AllowPublicAccessRule>();
        config.AddDocumentAccessRule<SmokeUsersCollectionReadRule>();
        config.AddDocumentAccessRule<OwnerReadRule>();
        config.AddDocumentStoreHook<DocumentUpdateHook>();
        config.AddIndexDefinition<WildcardIndexDefinition>();
        config.SetCallerClaimsAccessor<CallerClaimsAccessor>();
    })
    .AddWincheDatabaseWsApi();

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
app.UseWincheDatabase();
app.UseWincheDatabaseWsApi();
app.UseWincheDatabaseRestApi();

await CascadeDeleteSmokeTest.RunAsync(app.Services);
await AccessRuleSmokeTest.RunAsync(app.Services);

app.Run();
