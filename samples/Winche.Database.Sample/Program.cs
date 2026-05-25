using Winche.Database.AspNetCore.Rest.DependencyInjection;
using Winche.Database.AspNetCore.WebSockets.DependencyInjection;
using Winche.Database.DependencyInjection;
using Winche.Database.Sample.Configurations;

var builder = WebApplication.CreateBuilder(args);

var connString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new ArgumentNullException(nameof(args));

builder.Services.AddWincheDatabase(connString, builder.Configuration, (config) =>
{
    config.AddDocumentAccessRule<AllowPublicAccess>();
    config.AddDocumentStoreHook<DocumentUpdateHook>();
    config.AddIndexDefinition<WildcardIndexDefinition>();
});
builder.Services.AddWincheDatabaseRestApi((config) =>
{
    config.AddClaimsMapper<RESTClaimsMapper>();
});
builder.Services.AddWincheDatabaseWsApi((config) =>
{
    config.AddClaimsMapper<WSClaimsMapper>();
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
app.UseWincheDatabase();
app.UseWincheDatabaseWsApi();
app.UseWincheDatabaseRestApi();

// await CascadeDeleteSmokeTest.RunAsync(app.Services);

app.Run();
