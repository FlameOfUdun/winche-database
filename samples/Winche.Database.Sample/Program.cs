using Winche.Database.AspNetCore.DependencyInjection;
using Winche.Database.AspNetCore.Rest.DependencyInjection;
using Winche.Database.AspNetCore.WebSockets.DependencyInjection;
using Winche.Database.DependencyInjection;
using Winche.Database.Sample.Configurations;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddWincheDatabase(opts =>
    {
        opts.ConnectionString =
            builder.Configuration.GetConnectionString("WincheDatabase") ??
            builder.Configuration.GetConnectionString("DefaultConnection") ??
            throw new InvalidOperationException("No connection string found for WincheDatabase.");
            
        opts.AddDocumentAccessRule<AllowPublicAccessRule>();
        opts.AddDocumentAccessRule<SmokeUsersCollectionReadRule>();
        opts.AddDocumentAccessRule<OwnerReadRule>();
        opts.AddDocumentStoreHook<DocumentUpdateHook>();
        opts.AddIndexDefinition<WildcardIndexDefinition>();
        opts.SetCallerClaimsAccessor<CallerClaimsAccessor>();
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
app.MapWincheDatabaseWsApi();
app.MapWincheDatabaseRestApi();

await CascadeDeleteSmokeTest.RunAsync(app.Services);
await AccessRuleSmokeTest.RunAsync(app.Services);

app.Run();
