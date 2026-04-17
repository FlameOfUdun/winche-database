using WincheDatabase.REST.DependencyInjection;
using WincheDatabase.Store.DependencyInjection;
using WincheDatabase.Store.Models;
using WincheDatabase.WS.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

var connString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new ArgumentNullException(nameof(args));

static void AddRules(List<AccessRule> rules)
{
    rules.Add(new AccessRule
    {
        Path = "patients",
        Operations = new HashSet<AccessOperation>() { AccessOperation.Read },
        Evaluate = async (context, ct) => true,
    });
}

builder.Services.AddWincheDatabaseDocumentStore(connString, builder.Configuration, AddRules);
builder.Services.AddWincheDatabaseWs();
builder.Services.AddOpenApi();

static async Task<Dictionary<string, object?>> MapClaims(HttpContext context)
{
    return new Dictionary<string, object?>
    {
        ["uid"] = "123"
    };
}

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
app.UseWincheDatabaseWsApi(mapClaims: MapClaims);
app.UseWincheDatabaseRestApi(mapClaims: MapClaims);
app.Run();
