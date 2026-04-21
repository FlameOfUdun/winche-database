using WincheDatabase.AST.Models;
using WincheDatabase.REST.DependencyInjection;
using WincheDatabase.Store.DependencyInjection;
using WincheDatabase.WS.DependencyInjection;
using WincheSentinel.Core.Models;

var builder = WebApplication.CreateBuilder(args);

var connString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new ArgumentNullException(nameof(args));

builder.Services.AddWincheDatabaseDocumentStore(connString, builder.Configuration, (config) =>
{
    config.AddDocumentAccessRule(new(
        path: "patients",
        operations: [AccessOperation.Read],
        evaluate: async (context, ct) => true
    ));
    config.AddDocumentAccessRule(new(
        path: "products/**",
        operations: [AccessOperation.Read],
        evaluate: async (context, ct) => true
    ));
    config.AddIndexDefinition(new(
        collection: "products", 
        fields: [new("price", SortDirection.Desc, FieldType.Numeric)]
    ));
});
builder.Services.AddWincheDatabaseRestApi((config) =>
{
    config.AddClaimsMapper(new ((context) => 
    {
        return new Dictionary<string, object?>
        {
            ["uid"] = "123"
        };
    }));
});
builder.Services.AddWincheDatabaseWsApi((config) =>
{
    config.AddClaimsMapper(new ((context) => 
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
