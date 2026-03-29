using System.Text.Json;
using System.Text.Json.Serialization;
using WincheDb.Core.Ast;
using WincheDb.DocumentStore.DependencyInjection;
using WincheDb.DocumentStore.Models;
using WincheDb.DocumentStore.Services;
using WincheDb.JsonSerialization.Converters;
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
            Path = "patients",
            Operations = new HashSet<AccessOperation>() {AccessOperation.Read},
            Evaluate = async (context, ct) => true,
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
    options.JsonSerializerOptions.Converters.Add(new SortNodeListConverter());
    options.JsonSerializerOptions.Converters.Add(new WhereNodeConverter());
    options.JsonSerializerOptions.Converters.Add(new CursorValueListConverter());
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

await using var scope = app.Services.CreateAsyncScope();
var manager = scope.ServiceProvider.GetRequiredService<DocumentManager>();

var result = await manager.AggregateAsync(
[
    new MatchStage(
        "patients",
        new FieldFilter("status", ConditionalOperator.Eq, "active")
    ),
    new LookupStage(
        Collection: "encounters",
        LocalField: "id",
        ForeignField: "patientId",
        As: "encounters",
        Filter: new FieldFilter("type", ConditionalOperator.Eq, "emergency"),
        Limit: 10
    ),
    new GroupStage(
        Keys:
        [
            new GroupKey("ward", "ward")
        ],
        Accumulators:
        [
            new AccumulatorField("totalPatients", AggFunction.Count, Type: FieldType.Numeric),
            new AccumulatorField("avgAge", AggFunction.Avg, "age", Type: FieldType.Numeric),
            new AccumulatorField("maxSeverity", AggFunction.Max, "severity", Type: FieldType.Numeric)
        ],
        Having: new FieldFilter("totalPatients", ConditionalOperator.Gte, 2)
    ),
    new ProjectStage(
        Fields:
        [
            new ProjectField("ward", new FieldRefExpr("ward")),
            new ProjectField("totalPatients", new FieldRefExpr("totalPatients")),
            new ProjectField("avgAge", new FieldRefExpr("avgAge")),
            new ProjectField("maxSeverity", new FieldRefExpr("maxSeverity"))
        ]
    ),
    new SortStage(
        [
            new SortNode("maxSeverity", SortDirection.Desc, FieldType.Numeric)
        ]
    )
]);
Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

app.Run();
