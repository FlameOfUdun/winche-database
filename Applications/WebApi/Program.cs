using WincheDatabase.REST.DependencyInjection;
using WincheDatabase.REST.Services;
using WincheDatabase.Store.DependencyInjection;
using WincheDatabase.Store.Models;
using WincheDatabase.WS.DependencyInjection;
using WincheDatabase.WS.Services;
using WincheSentinel.Core.Models;

var builder = WebApplication.CreateBuilder(args);

var connString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new ArgumentNullException(nameof(args));

builder.Services.AddWincheDatabaseDocumentStore(connString, builder.Configuration, (config) =>
{
    //config.AddDocumentAccessRule(new DocumentAccessRule(
    //    path: "patients",
    //    operations: [AccessOperation.Read],
    //    evaluate: async (context, ct) => true
    //));
});
builder.Services.AddWincheDatabaseRestApi((config) =>
{
    config.AddClaimsMapper(new RestClaimsMapper((context) => 
    {
        return new Dictionary<string, object?>
        {
            ["uid"] = "123"
        };
    }));
});
builder.Services.AddWincheDatabaseWsApi((config) =>
{
    config.AddClaimsMapper(new WsClaimsMapper((context) => 
    {
        return new Dictionary<string, object?>
        {
            ["uid"] = "123"
        };
    }));
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
