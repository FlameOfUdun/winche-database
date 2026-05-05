using WincheDatabase.REST.DependencyInjection;
using WincheDatabase.Sample.Configurations;
using WincheDatabase.Store.DependencyInjection;
using WincheDatabase.WS.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

var connString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new ArgumentNullException(nameof(args));

builder.Services.AddWincheDatabaseDocumentStore(connString, builder.Configuration, (config) =>
{
    config.AddDocumentAccessRule<AllowPublicAccess>();
    config.AddDocumentStoreHook<DocumentUpdateHook>();
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
app.UseWincheDatabaseDocumentStore();
app.UseWincheDatabaseWsApi();
app.UseWincheDatabaseRestApi();
app.Run();
