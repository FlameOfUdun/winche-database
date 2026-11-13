using Microsoft.Extensions.Options;
using Winche.Database.Authorization;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Values;
using Winche.Rules;
using Winche.Rules.Text;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// Rules written in the text syntax (Winche.Rules 3.1.0), as the console editor saves them, enforced on real
/// writes — field allow-lists, "only these fields may change", type checks — through resource.data.
/// </summary>
[Collection("postgres")]
public class TextRuleTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private static readonly RuleSet Rules = RuleSetBuilder.Build(r =>
    {
        r.Match("profiles/{uid}", b =>
        {
            b.Allow([RuleOperation.Get], RuleText.Parse("request.auth.uid == uid"));
            b.Allow([RuleOperation.Create], RuleText.Parse("""
                request.auth.uid == uid
                  && request.resource.data.keys().hasOnly(['name', 'email', 'data'])
                  && request.resource.data.name is string
                  && request.resource.data.name.size() <= 20
                  && request.resource.data.get('email', 'x@y.z').matches('[^@]+@[^@]+')
                """));
            b.Allow([RuleOperation.Update], RuleText.Parse("""
                request.auth.uid == uid
                  && request.resource.data.diff(resource.data).affectedKeys().hasOnly(['name', 'email'])
                """));
        });
    });

    private DocumentDatabase Core() => new(Fx.DataSource, Options.Create(new WincheDatabaseOptions()));

    private DocumentDatabase As(string uid) =>
        new(Fx.DataSource, Options.Create(new WincheDatabaseOptions()),
            writeAuthorizer: new RulesWriteAuthorizer(
                new RuleEngine(new StaticRuleSetRepository(Rules)),
                new StaticClaimsAccessor(new Dictionary<string, object?> { ["uid"] = uid })));

    private static SetWrite Set(string path, params (string K, Value V)[] fields) =>
        new() { Path = path, Fields = fields.ToDictionary(f => f.K, f => f.V) };

    private static StringValue S(string s) => new(s);

    [Fact]
    public async Task Create_allows_only_listed_fields_of_the_right_type()
    {
        var uid = $"u{Guid.NewGuid():N}";
        var p = $"profiles/{uid}";

        await Assert.ThrowsAsync<AccessDeniedException>(() => As(uid).WriteAsync([Set(p, ("name", S("Ann")), ("role", S("admin")))]));   // extra field
        await Assert.ThrowsAsync<AccessDeniedException>(() => As(uid).WriteAsync([Set(p, ("name", new IntegerValue(1)))]));             // wrong type
        await Assert.ThrowsAsync<AccessDeniedException>(() => As(uid).WriteAsync([Set(p, ("name", S(new string('x', 21))))]));         // too long
        await Assert.ThrowsAsync<AccessDeniedException>(() => As(uid).WriteAsync([Set(p, ("name", S("Ann")), ("email", S("nope")))])); // bad email
        await Assert.ThrowsAsync<AccessDeniedException>(() => As("someone-else").WriteAsync([Set(p, ("name", S("Ann")))]));            // not theirs

        await As(uid).WriteAsync([Set(p, ("name", S("Ann")), ("email", S("ann@x.io")))]);
        Assert.NotNull(await Core().GetAsync(p));
    }

    [Fact]
    public async Task Update_may_change_only_the_listed_fields()
    {
        var uid = $"u{Guid.NewGuid():N}";
        var p = $"profiles/{uid}";
        await Core().WriteAsync([Set(p, ("name", S("Ann")), ("role", S("user")))]);   // role set by the server

        await As(uid).WriteAsync([Set(p, ("name", S("Anna")), ("role", S("user")))]);                              // only name changed
        await Assert.ThrowsAsync<AccessDeniedException>(() => As(uid).WriteAsync([Set(p, ("name", S("Anna")), ("role", S("admin")))]));   // role changed
        await Assert.ThrowsAsync<AccessDeniedException>(() => As(uid).WriteAsync([Set(p, ("name", S("Anna")))]));                        // role removed
        Assert.Equal(S("user"), (await Core().GetAsync(p))!.Fields["role"]);
    }

    [Fact]
    public async Task A_field_named_data_still_wins_over_the_data_map()
    {
        var uid = $"u{Guid.NewGuid():N}";
        var p = $"profiles/{uid}";
        // The document has its own `data` field: request.resource.data is that field, so keys() is its keys.
        await Assert.ThrowsAsync<AccessDeniedException>(() => As(uid).WriteAsync([
            Set(p, ("name", S("Ann")), ("data", new MapValue(new Dictionary<string, Value> { ["x"] = new IntegerValue(1) })))]));

        var resource = DocumentToResource.Convert(new Documents.Document
        {
            Path = "c/d", Id = "d", Collection = "c",
            Fields = new Dictionary<string, Value> { ["a"] = new IntegerValue(1) },
            CreateTime = DateTimeOffset.UtcNow, UpdateTime = DateTimeOffset.UtcNow, Version = 3,
        });
        Assert.Equal(["a"], resource.AsMap["data"].AsMap.Keys);           // fields only
        Assert.Equal(3, resource.AsMap["version"].AsInt);                  // columns still at the top level
        Assert.Equal(1, resource.AsMap["a"].AsInt);
    }
}
