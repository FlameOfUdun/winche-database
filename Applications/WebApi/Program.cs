using WincheDb.DocumentStore.Abstraction;
using WincheDb.DocumentStore.DependencyInjection;
using WincheDb.JsonSerialization.Converters;
using System.Text.Json;
using System.Text.Json.Serialization;
using WincheDb.Realtime.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

var connString = "Host=127.0.0.1;Port=5432;Username=postgres;Password=Ehsan1371;Database=WincheDatabase";
builder.Services.AddNpgsqlDataSource(connString);
builder.Services.AddWincheDbStore(options =>
{
    options.AccessRules =
    [
        new AccessRule
        {
            Path = "users/{userId}",
            Operations = new HashSet<AccessOperation> 
            { 
                AccessOperation.Get,
                AccessOperation.Set,
                AccessOperation.Update,
                AccessOperation.Query,
            },
            Evaluate = (ctx, ct) =>
            {
                var allowed = ctx.Claims.TryGetValue("uid", out var uid)
                    && ctx.PathParams.TryGetValue("userId", out var userId)
                    && uid?.ToString() == userId;
                return Task.FromResult(allowed);
            }
        },
        new AccessRule
        {
            Path = "public/**",
            Operations = new HashSet<AccessOperation> 
            { 
                AccessOperation.Get, 
                AccessOperation.Query 
            },
            Evaluate = (_, _) => Task.FromResult(true)
        }
    ];
    return options;
});
builder.Services.AddWincheDbRealtime();

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.JsonSerializerOptions.WriteIndented = false;
    options.JsonSerializerOptions.ReadCommentHandling = JsonCommentHandling.Skip;
    options.JsonSerializerOptions.AllowTrailingCommas = true;
    options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    options.JsonSerializerOptions.Converters.Add(new OrderByListConverter());
    options.JsonSerializerOptions.Converters.Add(new  WhereNodeConverter());
    options.JsonSerializerOptions.Converters.Add(new CursorListConverter());
});

builder.Services.AddOpenApi();

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.UseWebSockets();
await app.UseWincheDbStore();
app.Run();
