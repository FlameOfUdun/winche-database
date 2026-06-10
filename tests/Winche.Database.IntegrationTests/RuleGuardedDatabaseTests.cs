using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Winche.Database.Authorization;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Listening;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;
using Winche.Rules;
using Winche.Rules.Expressions;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// Integration tests for <see cref="RuleGuardedDocumentDatabase"/> (Phase 1).
/// Constructs the guard directly: core <see cref="DocumentDatabase"/> + a <see cref="Ruleset"/>
/// from <see cref="RulesetBuilder"/> + a <c>Func&lt;&gt;</c> returning fixed claims.
/// Seed data is always written via the CORE (unguarded) db; assertions through the guard.
/// </summary>
[Collection("postgres")]
public class RuleGuardedDatabaseTests(PostgresFixture fx) : QueryTestBase(fx)
{
    // ── Setup helpers ─────────────────────────────────────────────────────────

    private DocumentDatabase Core() =>
        new(Fx.DataSource, Options.Create(new WincheDatabaseOptions()));

    private static RuleGuardedDocumentDatabase Guard(
        DocumentDatabase core,
        Ruleset ruleset,
        IReadOnlyDictionary<string, object?>? claims) =>
        new(core, ruleset, () => claims);

    private static Dictionary<string, Value> Fields(params (string K, Value V)[] entries) =>
        entries.ToDictionary(e => e.K, e => e.V);

    // ── 1. Owner-only get ─────────────────────────────────────────────────────

    /// <summary>
    /// Rule: allow get if resource.data.ownerId == request.auth.uid
    /// Owner → allowed; non-owner → denied; missing doc → denied.
    /// </summary>
    [Fact]
    public async Task Get_OwnerRule_OwnerAllowed_OtherDenied_MissingDenied()
    {
        var core = Core();
        var ruleset = RulesetBuilder.Build(r =>
            r.Match("docs/{docId}", b =>
                b.Allow(
                    RuleOperations.Of(RuleOperation.Get),
                    Expr.Resource("data", "ownerId").Eq(Expr.Auth("uid")))));

        // Seed via core
        await core.WriteAsync([new SetWrite
        {
            Path = "docs/d1",
            Fields = Fields(("ownerId", new StringValue("alice"))),
        }]);

        // Owner read → allowed
        var aliceGuard = Guard(core, ruleset, new Dictionary<string, object?> { ["uid"] = "alice" });
        var doc = await aliceGuard.GetAsync("docs/d1");
        Assert.NotNull(doc);
        Assert.Equal("alice", ((StringValue)doc.Fields["ownerId"]).Value);

        // Non-owner read → denied
        var bobGuard = Guard(core, ruleset, new Dictionary<string, object?> { ["uid"] = "bob" });
        await Assert.ThrowsAsync<AccessDeniedException>(() => bobGuard.GetAsync("docs/d1"));

        // Missing doc → resource is Null → owner rule condition evaluates false (Null != uid) → denied
        var missingGuard = Guard(core, ruleset, new Dictionary<string, object?> { ["uid"] = "alice" });
        await Assert.ThrowsAsync<AccessDeniedException>(() => missingGuard.GetAsync("docs/nonexistent"));
    }

    // ── 2. Create requires owner claim in request.resource ────────────────────

    /// <summary>
    /// Rule: allow create if request.resource.data.ownerId == request.auth.uid
    /// Matching create (ownerId == uid) → allowed; mismatched → denied.
    /// </summary>
    [Fact]
    public async Task Write_CreateRule_OwnerMatchAllowed_MismatchDenied()
    {
        var core = Core();
        var ruleset = RulesetBuilder.Build(r =>
            r.Match("items/{itemId}", b =>
                b.Allow(
                    RuleOperations.Of(RuleOperation.Create),
                    Expr.RequestResource("data", "ownerId").Eq(Expr.Auth("uid")))));

        var aliceGuard = Guard(core, ruleset, new Dictionary<string, object?> { ["uid"] = "alice" });

        // Matching create: ownerId == uid → allowed
        await aliceGuard.WriteAsync([new SetWrite
        {
            Path = "items/i1",
            Fields = Fields(("ownerId", new StringValue("alice")), ("name", new StringValue("Widget"))),
        }]);
        Assert.NotNull(await core.GetAsync("items/i1"));

        // Mismatched create: ownerId != uid → denied
        await Assert.ThrowsAsync<AccessDeniedException>(() => aliceGuard.WriteAsync([new SetWrite
        {
            Path = "items/i2",
            Fields = Fields(("ownerId", new StringValue("charlie"))),
        }]));
        Assert.Null(await core.GetAsync("items/i2"));     // not written
    }

    // ── 3. Owner-immutable update ─────────────────────────────────────────────

    /// <summary>
    /// Rule: allow update if request.resource.data.ownerId == resource.data.ownerId
    /// Allowed when ownerId unchanged; denied when changed.
    /// </summary>
    [Fact]
    public async Task Write_UpdateRule_OwnerImmutable_AllowedWhenUnchanged_DeniedWhenChanged()
    {
        var core = Core();
        var ruleset = RulesetBuilder.Build(r =>
            r.Match("notes/{noteId}", b =>
                b.Allow(
                    RuleOperations.Of(RuleOperation.Update),
                    Expr.RequestResource("data", "ownerId").Eq(Expr.Resource("data", "ownerId")))));

        // Seed via core
        await core.WriteAsync([new SetWrite
        {
            Path = "notes/n1",
            Fields = Fields(("ownerId", new StringValue("alice")), ("text", new StringValue("hello"))),
        }]);

        var guard = Guard(core, ruleset, new Dictionary<string, object?> { ["uid"] = "alice" });

        // Update with ownerId unchanged → allowed
        await guard.WriteAsync([new SetWrite
        {
            Path = "notes/n1",
            Fields = Fields(("ownerId", new StringValue("alice")), ("text", new StringValue("updated"))),
        }]);
        var updated = await core.GetAsync("notes/n1");
        Assert.Equal("updated", ((StringValue)updated!.Fields["text"]).Value);

        // Update that changes ownerId → denied
        await Assert.ThrowsAsync<AccessDeniedException>(() => guard.WriteAsync([new SetWrite
        {
            Path = "notes/n1",
            Fields = Fields(("ownerId", new StringValue("bob")), ("text", new StringValue("hijacked"))),
        }]));
        // ownerId still alice after denied write
        var stable = await core.GetAsync("notes/n1");
        Assert.Equal("alice", ((StringValue)stable!.Fields["ownerId"]).Value);
    }

    // ── 4. Delete by owner ────────────────────────────────────────────────────

    /// <summary>
    /// Rule: allow delete if resource.data.ownerId == request.auth.uid
    /// </summary>
    [Fact]
    public async Task Write_DeleteRule_OwnerAllowed_OtherDenied()
    {
        var core = Core();
        var ruleset = RulesetBuilder.Build(r =>
            r.Match("posts/{postId}", b =>
                b.Allow(
                    RuleOperations.Of(RuleOperation.Delete),
                    Expr.Resource("data", "ownerId").Eq(Expr.Auth("uid")))));

        await core.WriteAsync([new SetWrite
        {
            Path = "posts/p1",
            Fields = Fields(("ownerId", new StringValue("alice"))),
        }]);

        // Non-owner delete → denied
        var bobGuard = Guard(core, ruleset, new Dictionary<string, object?> { ["uid"] = "bob" });
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            bobGuard.WriteAsync([new DeleteWrite { Path = "posts/p1" }]));
        Assert.NotNull(await core.GetAsync("posts/p1"));   // still exists

        // Owner delete → allowed
        var aliceGuard = Guard(core, ruleset, new Dictionary<string, object?> { ["uid"] = "alice" });
        await aliceGuard.WriteAsync([new DeleteWrite { Path = "posts/p1" }]);
        Assert.Null(await core.GetAsync("posts/p1"));      // deleted
    }

    // ── 5. Default-deny: no matching rule → denied ────────────────────────────

    [Fact]
    public async Task Get_DefaultDeny_NoMatchingRule_Denied()
    {
        var core = Core();
        // Ruleset with no rules → default-deny for everything
        var emptyRuleset = RulesetBuilder.Build(_ => { });

        await core.WriteAsync([new SetWrite
        {
            Path = "private/secret",
            Fields = Fields(("value", new IntegerValue(42))),
        }]);

        var guard = Guard(core, emptyRuleset, new Dictionary<string, object?> { ["uid"] = "anyone" });
        await Assert.ThrowsAsync<AccessDeniedException>(() => guard.GetAsync("private/secret"));
    }

    [Fact]
    public async Task Write_DefaultDeny_NoMatchingRule_Denied()
    {
        var core = Core();
        var emptyRuleset = RulesetBuilder.Build(_ => { });

        var guard = Guard(core, emptyRuleset, new Dictionary<string, object?> { ["uid"] = "anyone" });
        await Assert.ThrowsAsync<AccessDeniedException>(() => guard.WriteAsync([new SetWrite
        {
            Path = "private/doc",
            Fields = Fields(("x", new IntegerValue(1))),
        }]));
        Assert.Null(await core.GetAsync("private/doc"));
    }

    // ── 6. Batch rejection: one denied → nothing written ─────────────────────

    /// <summary>
    /// A 2-write batch where one is denied → throws and NEITHER doc is written.
    /// </summary>
    [Fact]
    public async Task Write_BatchRejection_OneDenied_NeitherWritten()
    {
        var core = Core();
        // Rule: only allow create on "allowed" collection
        var ruleset = RulesetBuilder.Build(r =>
            r.Match("allowed/{docId}", b =>
                b.Allow(RuleOperations.Of(RuleOperation.Create), Expr.Const(true))));
        // "blocked" collection has no rule → default-deny

        var guard = Guard(core, ruleset, null);   // unauthenticated

        // Batch: first write is to "allowed" (would pass), second to "blocked" (denied)
        await Assert.ThrowsAsync<AccessDeniedException>(() => guard.WriteAsync(
        [
            new SetWrite { Path = "allowed/doc1", Fields = Fields(("x", new IntegerValue(1))) },
            new SetWrite { Path = "blocked/doc2", Fields = Fields(("x", new IntegerValue(2))) },
        ]));

        // Neither document was written
        Assert.Null(await core.GetAsync("allowed/doc1"));
        Assert.Null(await core.GetAsync("blocked/doc2"));
    }

    // ── 7. Create vs update determination ────────────────────────────────────

    /// <summary>
    /// Writing to a non-existent path uses RuleOperation.Create;
    /// writing to an existing path uses RuleOperation.Update.
    /// A rule that only allows Create will permit the first write and deny the second.
    /// </summary>
    [Fact]
    public async Task Write_CreateVsUpdate_CorrectlyDetermined()
    {
        var core = Core();
        // Only allow Create, not Update
        var ruleset = RulesetBuilder.Build(r =>
            r.Match("once/{docId}", b =>
                b.Allow(RuleOperations.Of(RuleOperation.Create), Expr.Const(true))));

        var guard = Guard(core, ruleset, null);

        // First write to non-existent path → Create → allowed
        await guard.WriteAsync([new SetWrite
        {
            Path = "once/x",
            Fields = Fields(("v", new IntegerValue(1))),
        }]);
        Assert.NotNull(await core.GetAsync("once/x"));

        // Second write to same (now existing) path → Update → denied (no update rule)
        await Assert.ThrowsAsync<AccessDeniedException>(() => guard.WriteAsync([new SetWrite
        {
            Path = "once/x",
            Fields = Fields(("v", new IntegerValue(2))),
        }]));
        // Value unchanged
        var doc = await core.GetAsync("once/x");
        Assert.Equal(new IntegerValue(1), doc!.Fields["v"]);
    }

    // ── 8. ServerTimestamp transform: request.resource == request.time ────────

    /// <summary>
    /// A SetWrite with a ServerTimestamp transform on field "ts".
    /// Rule: allow create if request.resource.data.ts == request.time
    /// Because both are resolved to the same captured 'now', the condition must hold → allowed.
    /// </summary>
    [Fact]
    public async Task Write_ServerTimestampTransform_RequestResourceEqualsRequestTime()
    {
        var core = Core();
        // Rule checks the transform-resolved field equals request.time
        var ruleset = RulesetBuilder.Build(r =>
            r.Match("ts/{docId}", b =>
                b.Allow(
                    RuleOperations.Of(RuleOperation.Create),
                    Expr.RequestResource("data", "ts").Eq(Expr.Time()))));

        var guard = Guard(core, ruleset, new Dictionary<string, object?> { ["uid"] = "u1" });

        // A SetWrite with a ServerTimestamp transform on "ts"
        await guard.WriteAsync([new SetWrite
        {
            Path = "ts/doc1",
            Fields = Fields(("name", new StringValue("test"))),
            Transforms =
            [
                new FieldTransform(FieldPath.Parse("ts"), TransformKind.ServerTimestamp),
            ],
        }]);

        // The write went through — guard allowed it
        var written = await core.GetAsync("ts/doc1");
        Assert.NotNull(written);
        // The ts field is a TimestampValue (resolved by the real engine)
        Assert.True(written.Fields.ContainsKey("ts"));
        Assert.IsType<TimestampValue>(written.Fields["ts"]);
    }

    /// <summary>
    /// The same rule, but now without the ServerTimestamp transform — the field "ts" is absent in
    /// request.resource so the condition (absent != request.time) evaluates false → denied.
    /// This proves the transform resolution in the guard is the discriminating factor.
    /// </summary>
    [Fact]
    public async Task Write_NoTransform_ServerTimestampRuleDenied()
    {
        var core = Core();
        var ruleset = RulesetBuilder.Build(r =>
            r.Match("ts2/{docId}", b =>
                b.Allow(
                    RuleOperations.Of(RuleOperation.Create),
                    Expr.RequestResource("data", "ts").Eq(Expr.Time()))));

        var guard = Guard(core, ruleset, new Dictionary<string, object?> { ["uid"] = "u1" });

        // SetWrite without a transform: "ts" is absent → rule evaluates false → denied
        await Assert.ThrowsAsync<AccessDeniedException>(() => guard.WriteAsync([new SetWrite
        {
            Path = "ts2/doc1",
            Fields = Fields(("name", new StringValue("test"))),
        }]));
        Assert.Null(await core.GetAsync("ts2/doc1"));
    }

    // ── 9. Increment transform reflected in request.resource ─────────────────

    /// <summary>
    /// Seeds a document with count=10, then issues an UpdateWrite with Increment(5).
    /// Rule: allow update if request.resource.data.count == 15  → allowed.
    /// Then verify a wrong expectation (count == 10) → denied.
    /// </summary>
    [Fact]
    public async Task Write_IncrementTransform_RequestResourceReflectsNewValue()
    {
        var core = Core();

        // Seed via core: count = 10
        await core.WriteAsync([new SetWrite
        {
            Path = "counters/c1",
            Fields = Fields(("count", new IntegerValue(10L))),
        }]);

        // Rule: allow update only when incremented result == 15
        var rulesetAllow = RulesetBuilder.Build(r =>
            r.Match("counters/{docId}", b =>
                b.Allow(
                    RuleOperations.Of(RuleOperation.Update),
                    Expr.RequestResource("data", "count").Eq(15L))));

        var guard = Guard(core, rulesetAllow, null);

        // Increment by 5 → count becomes 15 → rule passes → allowed
        await guard.WriteAsync([new UpdateWrite
        {
            Path = "counters/c1",
            Fields = new Dictionary<FieldPath, Value>(),
            Transforms =
            [
                new FieldTransform(
                    FieldPath.Parse("count"),
                    TransformKind.Increment,
                    new IntegerValue(5L)),
            ],
        }]);
        var after = await core.GetAsync("counters/c1");
        Assert.Equal(new IntegerValue(15L), after!.Fields["count"]);

        // Rule that requires count == 10 (the pre-write value): should be denied
        var rulesetDeny = RulesetBuilder.Build(r =>
            r.Match("counters/{docId}", b =>
                b.Allow(
                    RuleOperations.Of(RuleOperation.Update),
                    Expr.RequestResource("data", "count").Eq(10L))));

        var guardDeny = Guard(core, rulesetDeny, null);

        // Increment by 5 again → count would become 20, but rule checks for 10 → denied
        await Assert.ThrowsAsync<AccessDeniedException>(() => guardDeny.WriteAsync([new UpdateWrite
        {
            Path = "counters/c1",
            Fields = new Dictionary<FieldPath, Value>(),
            Transforms =
            [
                new FieldTransform(
                    FieldPath.Parse("count"),
                    TransformKind.Increment,
                    new IntegerValue(5L)),
            ],
        }]));
        // count is still 15 (denied write did not go through)
        var stable = await core.GetAsync("counters/c1");
        Assert.Equal(new IntegerValue(15L), stable!.Fields["count"]);
    }

    // ── 10. ArrayUnion transform reflected in request.resource ───────────────

    /// <summary>
    /// Seeds a document with tags=["a"], then issues an UpdateWrite with ArrayUnion(["b"]).
    /// Rule: allow update if "b" in request.resource.data.tags → allowed.
    /// Then verify ArrayRemove is reflected: after removing "b", rule "b" in tags → denied.
    /// </summary>
    [Fact]
    public async Task Write_ArrayUnionTransform_RequestResourceReflectsUnionedArray()
    {
        var core = Core();

        // Seed: tags = ["a"]
        await core.WriteAsync([new SetWrite
        {
            Path = "arrays/a1",
            Fields = Fields(("tags", new ArrayValue([new StringValue("a")]))),
        }]);

        // Rule: allow update if "b" is in the post-write tags
        var ruleset = RulesetBuilder.Build(r =>
            r.Match("arrays/{docId}", b =>
                b.Allow(
                    RuleOperations.Of(RuleOperation.Update),
                    Expr.Const("b").In(Expr.RequestResource("data", "tags")))));

        var guard = Guard(core, ruleset, null);

        // ArrayUnion with "b" → tags becomes ["a","b"] → "b" in tags → allowed
        await guard.WriteAsync([new UpdateWrite
        {
            Path = "arrays/a1",
            Fields = new Dictionary<FieldPath, Value>(),
            Transforms =
            [
                new FieldTransform(
                    FieldPath.Parse("tags"),
                    TransformKind.ArrayUnion,
                    new ArrayValue([new StringValue("b")])),
            ],
        }]);
        var after = await core.GetAsync("arrays/a1");
        var tags = (ArrayValue)after!.Fields["tags"];
        Assert.Contains(new StringValue("b"), tags.Values);

        // Now ArrayRemove "b": post-write tags = ["a"]; rule still checks "b" in tags → denied
        await Assert.ThrowsAsync<AccessDeniedException>(() => guard.WriteAsync([new UpdateWrite
        {
            Path = "arrays/a1",
            Fields = new Dictionary<FieldPath, Value>(),
            Transforms =
            [
                new FieldTransform(
                    FieldPath.Parse("tags"),
                    TransformKind.ArrayRemove,
                    new ArrayValue([new StringValue("b")])),
            ],
        }]));
        // tags still ["a","b"] — denied write did not go through
        var stable = await core.GetAsync("arrays/a1");
        var stableTags = (ArrayValue)stable!.Fields["tags"];
        Assert.Contains(new StringValue("b"), stableTags.Values);
    }

    // ── Phase 2: list / query / count authorization ───────────────────────────

    /// <summary>
    /// Rule: allow read if resource.data.ownerId == request.auth.uid
    ///   - Query with WHERE ownerId == uid → allowed (provably safe), returns owned docs only.
    ///   - Query WITHOUT that constraint → rejected (not provably safe), throws AccessDeniedException.
    /// Confirms "rules are not filters": the unconstrained query is rejected, NOT silently filtered.
    /// </summary>
    [Fact]
    public async Task Query_OwnerRule_ConstrainedAllowed_UnconstrainedRejected()
    {
        var core = Core();
        var ruleset = RulesetBuilder.Build(r =>
            r.Match("owners/{docId}", b =>
                b.Allow(
                    RuleOperations.Read,
                    Expr.Resource("data", "ownerId").Eq(Expr.Auth("uid")))));

        // Seed via core: two documents, different owners
        await core.WriteAsync([new SetWrite
        {
            Path = "owners/a1",
            Fields = Fields(("ownerId", new StringValue("alice")), ("val", new IntegerValue(1))),
        }]);
        await core.WriteAsync([new SetWrite
        {
            Path = "owners/b1",
            Fields = Fields(("ownerId", new StringValue("bob")), ("val", new IntegerValue(2))),
        }]);

        var aliceGuard = Guard(core, ruleset, new Dictionary<string, object?> { ["uid"] = "alice" });

        // Constrained query WHERE ownerId == "alice" → allowed, returns alice's doc
        var constrained = new Query(
            "owners",
            Where: new FieldFilter(FieldPath.Parse("ownerId"), FilterOperator.Eq, new StringValue("alice")));
        var result = await aliceGuard.QueryAsync(constrained);
        Assert.All(result.Documents, d => Assert.Equal("alice", ((StringValue)d.Fields["ownerId"]).Value));
        Assert.Single(result.Documents);

        // Unconstrained query → rejected (rules are not filters)
        var unconstrained = new Query("owners");
        await Assert.ThrowsAsync<AccessDeniedException>(() => aliceGuard.QueryAsync(unconstrained));
    }

    /// <summary>
    /// Rule: allow read if resource.data.isPublic == true
    ///   - Query WHERE isPublic == true → allowed.
    ///   - Query without constraint → rejected.
    /// </summary>
    [Fact]
    public async Task Query_PublicFlagRule_ConstrainedAllowed_UnconstrainedRejected()
    {
        var core = Core();
        var ruleset = RulesetBuilder.Build(r =>
            r.Match("articles/{docId}", b =>
                b.Allow(
                    RuleOperations.Read,
                    Expr.Resource("data", "isPublic").Eq(true))));

        await core.WriteAsync([new SetWrite
        {
            Path = "articles/pub1",
            Fields = Fields(("isPublic", new BooleanValue(true)), ("title", new StringValue("Public Article"))),
        }]);
        await core.WriteAsync([new SetWrite
        {
            Path = "articles/priv1",
            Fields = Fields(("isPublic", new BooleanValue(false)), ("title", new StringValue("Private Article"))),
        }]);

        var guard = Guard(core, ruleset, new Dictionary<string, object?> { ["uid"] = "anyone" });

        // Query WHERE isPublic == true → allowed
        var publicQuery = new Query(
            "articles",
            Where: new FieldFilter(FieldPath.Parse("isPublic"), FilterOperator.Eq, new BooleanValue(true)));
        var result = await guard.QueryAsync(publicQuery);
        Assert.All(result.Documents, d => Assert.True(((BooleanValue)d.Fields["isPublic"]).Value));

        // Unconstrained query → rejected (does not silently filter to public docs)
        var allQuery = new Query("articles");
        await Assert.ThrowsAsync<AccessDeniedException>(() => guard.QueryAsync(allQuery));
    }

    /// <summary>
    /// Count mirrors query authorization:
    ///   - COUNT with provably-safe constraint → allowed, returns correct count.
    ///   - COUNT without constraint → rejected.
    /// </summary>
    [Fact]
    public async Task Count_OwnerRule_ConstrainedAllowed_UnconstrainedRejected()
    {
        var core = Core();
        var ruleset = RulesetBuilder.Build(r =>
            r.Match("items/{docId}", b =>
                b.Allow(
                    RuleOperations.Read,
                    Expr.Resource("data", "ownerId").Eq(Expr.Auth("uid")))));

        await core.WriteAsync([new SetWrite
        {
            Path = "items/i1",
            Fields = Fields(("ownerId", new StringValue("alice"))),
        }]);
        await core.WriteAsync([new SetWrite
        {
            Path = "items/i2",
            Fields = Fields(("ownerId", new StringValue("alice"))),
        }]);
        await core.WriteAsync([new SetWrite
        {
            Path = "items/i3",
            Fields = Fields(("ownerId", new StringValue("bob"))),
        }]);

        var aliceGuard = Guard(core, ruleset, new Dictionary<string, object?> { ["uid"] = "alice" });

        // Constrained COUNT → allowed, returns 2 (alice's docs)
        var constrainedCount = new Query(
            "items",
            Where: new FieldFilter(FieldPath.Parse("ownerId"), FilterOperator.Eq, new StringValue("alice")));
        var count = await aliceGuard.CountAsync(constrainedCount);
        Assert.Equal(2L, count);

        // Unconstrained COUNT → rejected
        var unconstrainedCount = new Query("items");
        await Assert.ThrowsAsync<AccessDeniedException>(() => aliceGuard.CountAsync(unconstrainedCount));
    }

    /// <summary>
    /// Default-deny: query on a collection with no rule → AccessDeniedException.
    /// </summary>
    [Fact]
    public async Task Query_DefaultDeny_NoMatchingRule_Rejected()
    {
        var core = Core();
        var emptyRuleset = RulesetBuilder.Build(_ => { });

        await core.WriteAsync([new SetWrite
        {
            Path = "secret/doc1",
            Fields = Fields(("val", new IntegerValue(1))),
        }]);

        var guard = Guard(core, emptyRuleset, new Dictionary<string, object?> { ["uid"] = "anyone" });

        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            guard.QueryAsync(new Query("secret")));
    }

    // ── Phase 3: Listen authorization ────────────────────────────────────────

    // Wires a DocumentDatabase + ListenerRegistry + ChangeFeedPump into one
    // disposable rig, identical to the pattern used in ListenerTests.
    private sealed class ListenRig : IAsyncDisposable
    {
        public required DocumentDatabase Db { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task Run { get; init; }
        public async ValueTask DisposeAsync() { Cts.Cancel(); await Run; }
    }

    private ListenRig StartListenRig()
    {
        var opts = Options.Create(new WincheDatabaseOptions());
        var registry = new ListenerRegistry(Fx.DataSource);
        var db = new DocumentDatabase(Fx.DataSource, opts, registry);
        var pump = new ChangeFeedPump(
            Fx.DataSource,
            [registry],
            new ChangeFeedConfig { PollInterval = TimeSpan.FromMilliseconds(200) },
            NullLogger<ChangeFeedPump>.Instance);
        var cts = new CancellationTokenSource();
        return new ListenRig { Db = db, Cts = cts, Run = Task.Run(() => pump.RunAsync(cts.Token)) };
    }

    private static async Task<QuerySnapshot> NextSnapshotAsync(IAsyncEnumerator<QuerySnapshot> e, int timeoutMs = 10_000)
    {
        var move = e.MoveNextAsync().AsTask();
        Assert.True(await Task.WhenAny(move, Task.Delay(timeoutMs)) == move, "timed out waiting for snapshot");
        Assert.True(await move, "snapshot stream ended unexpectedly");
        return e.Current;
    }

    /// <summary>
    /// Phase 3 — reject at subscribe time.
    /// Rule: allow read if resource.data.ownerId == request.auth.uid.
    /// An unconstrained query is not provably safe → Listen throws AccessDeniedException
    /// immediately, before any snapshot is produced.
    /// </summary>
    [Fact]
    public async Task Listen_UnconstrainedQuery_ThrowsAccessDenied_AtSubscribeTime()
    {
        await using var rig = StartListenRig();
        var ruleset = RulesetBuilder.Build(r =>
            r.Match("listeners/{docId}", b =>
                b.Allow(
                    RuleOperations.Read,
                    Expr.Resource("data", "ownerId").Eq(Expr.Auth("uid")))));

        var guard = Guard(rig.Db, ruleset, new Dictionary<string, object?> { ["uid"] = "alice" });

        // Unconstrained query on the collection — not provably safe → must throw immediately
        Assert.Throws<AccessDeniedException>(() =>
            guard.Listen(new Query("listeners")));
    }

    /// <summary>
    /// Phase 3 — allow + deliver live snapshot.
    /// Same rule, but the query is constrained WHERE ownerId == uid → provably safe.
    /// Subscribe does not throw. After seeding an owned doc via core and letting the
    /// pump deliver it, the listener snapshot contains that document.
    /// </summary>
    [Fact]
    public async Task Listen_ConstrainedQuery_AllowedAndDeliversOwnedDoc()
    {
        await using var rig = StartListenRig();
        var ruleset = RulesetBuilder.Build(r =>
            r.Match("listeners2/{docId}", b =>
                b.Allow(
                    RuleOperations.Read,
                    Expr.Resource("data", "ownerId").Eq(Expr.Auth("uid")))));

        var guard = Guard(rig.Db, ruleset, new Dictionary<string, object?> { ["uid"] = "alice" });

        // Constrained query — provably safe (ownerId constraint matches the rule)
        var constrainedQuery = new Query(
            "listeners2",
            Where: new FieldFilter(FieldPath.Parse("ownerId"), FilterOperator.Eq, new StringValue("alice")));

        // Must not throw at subscribe time
        await using var listener = guard.Listen(constrainedQuery);
        Assert.NotNull(listener);

        await using var e = listener.Snapshots().GetAsyncEnumerator();

        // Consume the (empty) initial snapshot
        var initial = await NextSnapshotAsync(e);
        Assert.Empty(initial.Documents);

        // Seed an owned document via the core (bypasses the guard)
        await rig.Db.WriteAsync([new SetWrite
        {
            Path = "listeners2/owned1",
            Fields = Fields(("ownerId", new StringValue("alice")), ("note", new StringValue("mine"))),
        }]);

        // Pump delivers a snapshot containing the owned doc
        var snap = await NextSnapshotAsync(e);
        Assert.Equal("owned1", Assert.Single(snap.Documents).Id);
    }

    /// <summary>
    /// Phase 3 — default-deny.
    /// Empty ruleset → no rule matches any collection → Listen throws for any query.
    /// </summary>
    [Fact]
    public void Listen_DefaultDeny_EmptyRuleset_Throws()
    {
        var core = Core();
        var emptyRuleset = RulesetBuilder.Build(_ => { });
        var guard = Guard(core, emptyRuleset, new Dictionary<string, object?> { ["uid"] = "anyone" });

        Assert.Throws<AccessDeniedException>(() =>
            guard.Listen(new Query("anything")));
    }

    /// <summary>
    /// GetAll authorizes each path individually as a get.
    ///   - All paths allowed → returns all docs.
    ///   - One path denied → throws AccessDeniedException before returning any results.
    /// </summary>
    [Fact]
    public async Task GetAll_OwnerRule_AllAllowed_OnePartiallyDeniedThrows()
    {
        var core = Core();
        var ruleset = RulesetBuilder.Build(r =>
            r.Match("docs/{docId}", b =>
                b.Allow(
                    RuleOperations.Read,
                    Expr.Resource("data", "ownerId").Eq(Expr.Auth("uid")))));

        await core.WriteAsync([new SetWrite
        {
            Path = "docs/d1",
            Fields = Fields(("ownerId", new StringValue("alice"))),
        }]);
        await core.WriteAsync([new SetWrite
        {
            Path = "docs/d2",
            Fields = Fields(("ownerId", new StringValue("alice"))),
        }]);
        await core.WriteAsync([new SetWrite
        {
            Path = "docs/d3",
            Fields = Fields(("ownerId", new StringValue("bob"))),
        }]);

        var aliceGuard = Guard(core, ruleset, new Dictionary<string, object?> { ["uid"] = "alice" });

        // All alice's paths → allowed
        var allowed = await aliceGuard.GetAllAsync(["docs/d1", "docs/d2"]);
        Assert.Equal(2, allowed.Count);
        Assert.All(allowed, d => Assert.NotNull(d));

        // Batch that includes bob's doc → denied (single denial blocks the whole batch)
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            aliceGuard.GetAllAsync(["docs/d1", "docs/d3"]));
    }
}
