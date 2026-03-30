using System.Text.Json;
using WincheDb.Core.Ast;
using WincheDb.DocumentStore.DependencyInjection;
using WincheDb.DocumentStore.Models;
using WincheDb.DocumentStore.Services;
using WincheDb.Realtime.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

var connString = "Host=127.0.0.1;Port=5432;Username=postgres;Password=Ehsan1371;Database=WincheDatabase";
builder.Services.AddWincheDbStore(connString, options =>
{
    options.AccessRules =
    [
        new AccessRule
        {
            Path = "patients",
            Operations = new HashSet<AccessOperation>() {AccessOperation.Read},
            Evaluate = async (context, ct) => true,
        }
    ];
});
builder.Services.AddWincheDbRealtime(); 
builder.Services.AddControllers();
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
app.MapControllers();
app.UseWebSockets();
app.UseWincheDbStore();
app.Run();
