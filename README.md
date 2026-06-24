# Winche.Database

[![NuGet version](https://img.shields.io/nuget/v/Winche.Database.svg)](https://www.nuget.org/packages/Winche.Database)

A JSON document database layer built on top of PostgreSQL. Store, query, and subscribe to JSON documents — with PostgreSQL as the storage backend via JSONB.

Supports real-time live queries with indexed delta listeners, optimistic ACID transactions, durable document lifecycle hooks, filtered secondary indexes, automatic document expiry (TTL), and a built-in rule-based access-control engine (Winche.Rules).

**Protocol docs:**

- [PROTOCOL](docs/PROTOCOL.md) — wire format reference (values, writes, queries, REST API, WebSocket protocol)

## Packages

| Package | Description |
| --- | --- |
| `Winche.Database` | Core document store: typed values, CRUD, queries, transactions, live listeners, hooks, filtered indexes, and rule-based access control (Winche.Rules) |
| `Winche.Database.AspNetCore` | Shared ASP.NET Core abstractions: `DocumentClaimsAccessor` and the `MapClaims` registration extension |
| `Winche.Database.AspNetCore.Rest` | ASP.NET Core minimal API REST endpoints |
| `Winche.Database.AspNetCore.WebSockets` | ASP.NET Core WebSocket transport, real-time delta listeners, and connection management |

## Install

```cmd
dotnet add package Winche.Database
dotnet add package Winche.Database.AspNetCore
dotnet add package Winche.Database.AspNetCore.Rest
dotnet add package Winche.Database.AspNetCore.WebSockets
```

Add only the transport packages you need (`Rest`, `WebSockets`, or both).

## Quick Start

### 1. Configure `appsettings.json`

```json
{
  "ConnectionStrings": {
    "WincheDatabase": "Host=localhost;Database=mydb;Username=postgres;Password=secret"
  }
}
```

The library does not read `IConfiguration` itself — you pass the connection string (and any store options) explicitly in `AddWincheDatabase` (step 2). Where you keep them is your choice.

All database objects (table, changes feed, cursors, `winche_*` functions, indexes) are created unqualified and resolve through the connection's schema search path — to host a store in a non-`public` schema, set it in the connection string (`Search Path=myschema`) rather than in code.

### 2. Register services

```csharp
using Winche.Database.DependencyInjection;
using Winche.Database.AspNetCore.DependencyInjection;
using Winche.Database.AspNetCore.Rest.DependencyInjection;
using Winche.Database.AspNetCore.WebSockets.DependencyInjection;
using Winche.Rules;
using Winche.Rules.Expressions;

builder.Services
    .AddWincheDatabase(config =>
    {
        // Connection (required); use "Search Path=myschema" for non-public schemas
        config.ConnectionString = builder.Configuration.GetConnectionString("WincheDatabase")!;

        // Store behavior (optional; these are the defaults)
        config.TransactionConfig = new() { IdleTimeoutSpan = TimeSpan.FromMinutes(1) };

        // Access rules (evaluated in memory on every operation)
        config.UseRules(r =>
            r.Match("users/{userId}", u =>
            {
                u.Allow(RuleOperations.Read,  Expr.Auth("uid").Eq(Expr.Param("userId")));
                u.Allow(RuleOperations.Write, Expr.Auth("uid").Eq(Expr.Param("userId")));
            }));

        // Document lifecycle hooks (at-least-once, feed-driven, must be idempotent)
        config.UseHooks(h => h.Add<MyHook>("{document=**}"));

        // Secondary indexes (declared by collection ID — the last path segment)
        config.UseIndexes(i => i.Add("sessionHistory", new IndexField("startedAt", SortDirection.Desc)));

        // TTL policies — auto-delete documents once a timestamp field is in the past
        config.UseTtl(t => t.Add("sessions", "expiresAt"));

        // Claims — maps the request principal to caller claims (DI available via http.RequestServices)
        config.MapClaims(http => new Dictionary<string, object?> { ["uid"] = http.User.FindFirst("sub")?.Value });
    })
    .AddWincheDatabaseWsApi();   // WebSocket transport
```

`MapClaims` is provided by `Winche.Database.AspNetCore.DependencyInjection` and registers the accessor for both the REST and WebSocket transports in one call.

### 3. Initialize schema and map routes

```csharp
await app.InitializeWincheDatabaseAsync();   // Creates winche_* tables/functions and syncs indexes
app.UseWincheWsQueryToken();                 // surfaces ?access_token= to your auth scheme (before UseAuthentication)
app.UseAuthentication();
app.UseWebSockets();                         // required before mapping the WS endpoint
app.MapWincheDatabaseWsApi().RequireAuthorization();   // WebSocket endpoint: /documents/ws
app.MapWincheDatabaseRestApi();                        // REST routes under /documents (configurable)
```

Both `Map*` methods return an `IEndpointConventionBuilder` covering **all** of their endpoints — for REST that includes the CRUD routes *and* every colon-verb (`:commit`, `:runQuery`, …); for WS the upgrade route. Apply cross-cutting policy on it once and it lands everywhere:

```csharp
app.MapWincheDatabaseRestApi().RequireAuthorization();  // CRUD AND :commit/:runQuery/…
app.MapWincheDatabaseWsApi().RequireRateLimiting("ws"); // throttle socket upgrades
```

The built-in claims/exception filters are always applied internally and run outermost, so caller conventions compose on top of them.

> **WebSocket authentication is at the HTTP upgrade.** Browsers cannot set `Authorization` headers on WebSocket upgrades, so the library provides `app.UseWincheWsQueryToken()` — middleware that promotes a `?access_token=<jwt>` query parameter to an `Authorization: Bearer …` header so your existing auth scheme (JWT bearer, etc.) can validate it at the upgrade. Place it **before** `app.UseAuthentication()`. The library does **not** validate the token; that is the responsibility of your registered authentication handler. Once the upgrade succeeds, `HttpContext.User` is populated for the lifetime of the connection and the server sends a `welcome` frame immediately — there is no in-band `hello` handshake or `auth.refresh` message. Gate with `.RequireAuthorization()` to reject unauthenticated upgrades before the socket is accepted. Connect at `/documents/ws?access_token=<jwt>`.
>
> **Security notes:** always require TLS in production (tokens in query strings appear in server logs and proxies); use short-lived tokens and reconnect on expiry.

## Features

- **typed engine** — 11 tagged value types (`null`, `bool`, `int64`, `double`, `timestamp`, `string`, `bytes`, `reference`, `geopoint`, `array`, `map`); cross-type total order; same-type-class inequality semantics; `__name__` tiebreaker; int/double numeric equality
- **Document storage** — Store documents as typed field maps; each document carries `path`, `id`, `collection`, `createTime`, `updateTime`, and `version` metadata
- **Querying** — 15 filter operators (including Winche extensions: `arrayContainsAll`, `contains`, `startsWith`, `endsWith`, `regex`, field-compare); `and`/`or`/`not` composites; `orderBy` with `__name__` tiebreaker; cursor-based pagination (`startAt`/`startAfter`/`endAt`/`endBefore`); field projection via `select`; `offset` (skip first N results) and `limitToLast` (last N results in ascending order)
- **Live queries with delta listeners** — Subscribe to a query and receive an initial full snapshot, then indexed `added`/`modified`/`removed` deltas with `count` checksum; `listen.snapshot` REPLACES client state, `listen.delta` MUTATES it
- **Optimistic transactions** — Optimistic read-version ledger; `ABORTED` on conflict with safe retry; `RunTransactionAsync` with automatic retry; reads-before-writes enforced; idle (60 s) and absolute (5 min) timeouts
- **Batch writes** — Atomic commit of up to 500 `set`/`update`/`delete` operations with field transforms, preconditions, and a single commit timestamp; use `new WriteBatch(db)` for a fluent builder; `set` supports explicit merge masks via `MergeFields` (subset-merge alternative to `merge: true`)
- **Field transforms** — `serverTimestamp`, `increment` (saturating int, promotes to double on mixed), `maximum`, `minimum`, `arrayUnion`, `arrayRemove`; use the `FieldValue` factory for a fluent C# API (`FieldValue.ServerTimestamp`, `Increment`, `Maximum`, `Minimum`, `ArrayUnion`, `ArrayRemove`, `Delete`)
- **Durable hooks** — Feed-driven, true at-least-once end-to-end; hooks execute inline+sequentially per batch; failed batches retried with capped backoff; hooks fire for writes from any node; cursor persisted across restarts; idempotency required
- **Secondary indexes** — composite expression indexes declared by **collection ID** (the last path segment), covering every collection with that id under any parent; `IndexDefinition.Where` for filtered/partial indexes (agreement tested against engine) (see below)
- **Access control** — in-memory rules engine (Winche.Rules); OR semantics with default-deny; for `list`/query operations the query must provably satisfy a read rule or it is rejected with PERMISSION_DENIED (rules are not post-filters)
- **TTL (auto-expiry)** — Documents are automatically deleted once a registered timestamp field is in the past; configurable sweep interval (default 5 min) and batch size (default 500); collection-group scoped; TTL deletes emit change-feed events; bypass access rules; cascade to subcollections by default (opt out with `CascadeDelete = false`)
- **PostgreSQL backend** — All data stored as typed JSONB; queries compiled to native PostgreSQL SQL backed by a family of `winche_*` ordering helper functions (`winche_rank`, `winche_num`, `winche_text`, `winche_key`, …); `IMMUTABLE` functions back expression indexes

## Secondary Indexes

An `IndexDefinition` declares a composite expression index over the `winche_*` ordering family,
scoped by **collection ID** — the last segment of a collection path. One definition covers every
collection sharing that id, regardless of parent:

```csharp
// one definition backs every user's sessionHistory subcollection, under any parent
config.UseIndexes(i =>
    i.Add("sessionHistory", new IndexField("startedAt", SortDirection.Desc)));
```

Per-collection queries (`collection = "userData/alice/sessionHistory"`) use the matching
collection-ID index automatically — **no change to query code**. Physically, the index is a partial
index keyed on `collection_id` with `collection_path` as the leading key, so a single concrete
collection stays seekable within the shared index.

Collection ID grammar (violations throw `InvalidPathPatternException` at schema-sync):

- A collection ID is a **single segment** matching `[A-Za-z0-9_-]+` (no `/`, no `*`).

Because indexes are keyed by collection id, an index on `sessionHistory` covers **all** collections
named `sessionHistory` (e.g. one per user). Cross-collection ("collection group") queries — querying
a field across *all* members at once — are not yet supported.

## Access Rules

Access rules determine whether a caller can perform an operation. Rules are defined with
`UseRules(r => r.Match(path, m => m.Allow(operations, condition)))` and evaluated in-memory by
Winche.Rules on every operation.

```csharp
using Winche.Rules;
using Winche.Rules.Expressions;

config.UseRules(r =>
    r.Match("users/{userId}", u =>
    {
        u.Allow(RuleOperations.Read,  Expr.Auth("uid").Eq(Expr.Param("userId")));
        u.Allow(RuleOperations.Write, Expr.Auth("uid").Eq(Expr.Param("userId")));
    }));
```

### Path patterns

- `{id}` — binds a single document-id segment (e.g. `{userId}` in `users/{userId}`)
- `{doc=**}` — binds any remaining path depth (e.g. `userData/{userId}/{rest=**}` matches everything under a user's subtree)

### Operations

| Constant | Expands to |
| --- | --- |
| `RuleOperations.Read` | `get` + `list` |
| `RuleOperations.Write` | `create` + `update` + `delete` |
| `RuleOperations.All` | all five operations |
| `RuleOperations.Of(...)` | explicit set |

### Conditions

Conditions are `RuleExpr` values built with the `Expr` factory:

| Expression | Maps to |
| --- | --- |
| `Expr.Auth("uid")` | `request.auth.uid` |
| `Expr.Auth("token", "role")` | `request.auth.token.role` |
| `Expr.Param("userId")` | path-capture variable `userId` |
| `Expr.Resource("ownerId")` | `resource.ownerId` (a field of the existing document) |
| `Expr.RequestResource("status")` | `request.resource.status` (the post-write document, with field transforms resolved) |
| `Expr.Time()` | `request.time` |
| `Expr.Exists(path)` | `exists(path)` — cross-document existence check |
| `Expr.Get(path)` | `get(path)` — cross-document read |
| `Expr.Any(a, b)` | `a \|\| b` (OR) |
| `Expr.All(a, b)` | `a && b` (AND) |
| `.Eq(...)` / `.Ne(...)` / `.Lt(...)` etc. | comparison operators |

A document's own fields are exposed at the top level of `resource` (so `resource.ownerId`, never a
`data` wrapper). The storage columns are added as reserved siblings, so rules can condition on document
metadata: `resource.id`, `resource.path`, `resource.collection`, `resource.createdAt`,
`resource.updatedAt`, and `resource.version`. A reserved column wins over a field of the same name.

### Semantics

Rules **grant** access; there is no explicit deny. A request is allowed if **any**
rule whose path pattern and operations set match returns `true` (OR). A matching rule that returns
`false` does not veto — it simply doesn't grant. If no rule grants, access is denied (default-deny).
Multiple `UseRules` calls accumulate — each registered ruleset's blocks are OR-combined with all
others. Registration order does not affect the decision.

> **Per-package isolation (8.2.0+):** each Winche package that uses Winche.Rules builds its own rules
> engine, registered under a package-specific DI key. Rulesets from different Winche libraries sharing
> one container (e.g. `Winche.Database` and `Winche.Storage`) therefore never merge into each other.
> This is automatic — no consumer action is required.

Because a grant cannot be revoked by another rule, **grant narrowly**: don't write a broad `**`
read grant and expect a more specific rule to restrict it — instead grant read only where it should
be allowed and let default-deny cover the rest.

### Rules are not filters

**For `list`/query operations, rules are not applied as post-execution row filters.** Instead, the
query is analyzed before execution: if its constraints provably satisfy a read rule (e.g. it
constrains `ownerId == request.auth.uid` and there is a matching `allow read` rule), the query is
permitted. If no read rule is provably satisfied by the query's constraints, the query is
**rejected** with PERMISSION_DENIED — results are **never** silently post-filtered. This means the
query itself must carry the constraining filter; the rule engine validates the query's intent, not
its results.

For `get` operations, the rule is evaluated in-memory against the loaded document.

### Claims Accessor

To supply per-request caller claims, register a delegate with `MapClaims`:

```csharp
config.MapClaims(http => new Dictionary<string, object?> { ["uid"] = http.User.FindFirst("sub")?.Value });
```

`MapClaims` is an extension method on `WincheDatabaseOptions` from
`Winche.Database.AspNetCore.DependencyInjection`. It registers the accessor for both the REST and
WebSocket transports. DI services are available inside the delegate via `http.RequestServices`.

## Document Store Hooks

Hooks let you react to document mutations. Implement `DocumentStoreHook` (behavior only) and register it against a path with `UseHooks(h => h.Add<MyHook>(path))` — mirroring how `UseIndexes` binds a collection id. Hooks execute **inline and sequentially** inside `HookFeedConsumer`, which is driven by a durable feed runner. Delivery is **true at-least-once end-to-end** — the cursor advances only after all hooks in a batch succeed, and failed batches are retried with capped backoff (1 s → 30 s). Implementations **must be idempotent** (use the document `version` as an idempotency key).

The path supplied at registration is a **trigger pattern** (the same grammar as the rules engine): literal segments, `{id}` single-segment captures, and a trailing `{document=**}` recursive wildcard. Use `"{document=**}"` to match every document. Bare `*`/`**` are not valid. Because the path lives in the registration (not the class), the same hook type can be bound to multiple paths.

```csharp
using Winche.Database.Abstraction;
using Winche.Database.Documents;

// Behavior only — no Path; the pattern is supplied when you register the hook.
public class AuditHook : DocumentStoreHook
{
    public override Task OnDocumentSetAsync(string path, Document document, CancellationToken ct)
    {
        // called after a document is created or replaced
        return Task.CompletedTask;
    }

    public override Task OnDocumentUpdatedAsync(string path, Document document, CancellationToken ct)
    {
        // called after a document is patched
        return Task.CompletedTask;
    }

    public override Task OnDocumentDeletedAsync(string path, CancellationToken ct)
    {
        // called after a document (or subtree) is deleted
        return Task.CompletedTask;
    }
}
```

Register it against one or more paths (hook types are constructed via DI, so constructor injection works):

```csharp
config.UseHooks(h => h
    .Add<AuditHook>("orders/{document=**}")     // every document under any orders collection
    .Add<AuditHook>("invoices/{invoiceId}"));   // same hook type, bound to a second path
```

## TTL Policies (Auto-Expiry)

TTL policies automatically delete documents once a registered timestamp field is in the past. Register policies via `UseTtl` inside `AddWincheDatabase`:

```csharp
config.UseTtl(t =>
{
    t.Add("sessions", "expiresAt");    // collectionId, field name
    t.Add("tokens",   "validUntil");
});
```

The fluent builder (`TtlBuilder`) also accepts a fully-constructed `TtlPolicy`:

```csharp
config.UseTtl(t => t.Add(TtlPolicy.For("sessions", "expiresAt")));
```

The `params` overload registers policies directly without a builder:

```csharp
config.UseTtl(TtlPolicy.For("sessions", "expiresAt"), TtlPolicy.For("tokens", "validUntil"));
```

Multiple `UseTtl` calls accumulate — all registered policies are collected for the background sweeper.

### Semantics

- A document is deleted once its registered field holds a `timestampValue` in the past. A missing field, or a field that is not a timestamp, **never** expires the document.
- Policies apply per **collection group**: the policy's collection id matches documents in ALL collections sharing that id (e.g. a policy on `sessions` covers `users/u1/sessions/*` and `teams/t2/sessions/*`).
- TTL deletes go through the normal write path, so they **emit change-feed events** — listeners and hooks observe the removals as ordinary `removed` events.
- TTL is **system-initiated** and therefore **bypasses security rules**.
- Deletes **cascade to subcollections by default**. Set `CascadeDelete = false` to delete only the matched document and leave its subcollection documents in place.

> **Expiry is not instantaneous:** a document is removed on the next sweep after its field elapses, so the lag is bounded by `SweepInterval` (default 5 minutes).

### Tuning

Sweep behavior is controlled by `WincheDatabaseOptions.Ttl` (`TtlConfig`):

| Field | Default | Description |
| --- | --- | --- |
| `SweepInterval` | 5 minutes | How often the TTL sweeper runs. Must be `> TimeSpan.Zero`. |
| `BatchSize` | 500 | Maximum documents deleted per collection per sweep pass. Clamped to at most 500 (the write-batch cap). |
| `CascadeDelete` | `true` | When `true`, a TTL delete also removes the document's subcollections. Set `false` to delete only the matched document. |

```csharp
config.Ttl = new TtlConfig
{
    SweepInterval = TimeSpan.FromMinutes(10),
    BatchSize     = 200,
};
```

The sweeper loops within each collection until it fetches a short batch (fewer than `BatchSize`), then moves to the next policy. TTL is purely server-side configuration — there is no REST or WebSocket surface for it.

## `IDocumentDatabase`

The primary service. Inject `IDocumentDatabase` to interact with the store from application code.

> **Guarded vs. core.** `IDocumentDatabase` is bound in DI to `RuleGuardedDocumentDatabase`, which enforces access rules on every operation. The concrete `DocumentDatabase` is the **rule-free core** that the guard decorates — inject it *only* in trusted server-side code (seeding, migrations, internal jobs) that should deliberately bypass authorization. Application code subject to access rules must depend on `IDocumentDatabase`, **never** the concrete `DocumentDatabase`.

```csharp
public interface IDocumentDatabase
{
    // Reads
    Task<Document?> GetAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<Document?>> GetAllAsync(IReadOnlyList<string> paths, CancellationToken ct = default);
    Task<QueryResult> QueryAsync(Query query, CancellationToken ct = default);
    Task<long> CountAsync(Query query, CancellationToken ct = default);
    Task<AggregationResult> AggregateAsync(Query query, IReadOnlyList<Aggregation> aggregations, CancellationToken ct = default);

    // Writes — every mutation is a Write[]; singles are sugar
    Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<Write> writes, CancellationToken ct = default);
    Task<Document> AddAsync(string collectionPath, IReadOnlyDictionary<string, Value> fields, CancellationToken ct = default);

    // Transactions (optimistic)
    Task<TransactionHandle> BeginTransactionAsync(CancellationToken ct = default);
    Task<Document?> GetAsync(string transactionId, string path, CancellationToken ct = default);
    Task<QueryResult> QueryAsync(string transactionId, Query query, CancellationToken ct = default);
    Task<IReadOnlyList<WriteResult>> CommitTransactionAsync(string transactionId, IReadOnlyList<Write> writes, CancellationToken ct = default);
    Task RollbackTransactionAsync(string transactionId, CancellationToken ct = default);
    Task<T> RunTransactionAsync<T>(Func<TransactionContext, Task<T>> body, TransactionOptions? options = null, CancellationToken ct = default);

    // Live queries
    IQueryListener Listen(Query query, ListenOptions? options = null);
    IDocumentListener ListenToDocument(string path, ListenOptions? options = null);
}
```

### Single-document listener

`IDocumentDatabase.ListenToDocument(string path, ListenOptions? options = null)` returns an `IDocumentListener` that emits `DocumentSnapshot` values as the target document changes.

```csharp
await using var listener = db.ListenToDocument("users/u1");
await foreach (var snap in listener.Snapshots())
{
    if (snap.Exists)
        Console.WriteLine($"Document: {snap.Document!.Fields["name"]}");
    else
        Console.WriteLine("Document does not exist.");
}
```

`DocumentSnapshot` carries:

| Property | Type | Description |
| --- | --- | --- |
| `Document` | `Document?` | The current document, or `null` when absent |
| `Exists` | `bool` | `true` if the document is present |
| `ReadTime` | `DateTimeOffset` | Server read time for this snapshot |
| `ResumeToken` | `long` | Feed watermark; pass as `ListenOptions.ResumeToken` to resume after reconnect |

Internally, `ListenToDocument` rides on the query listener over a `__name__ == path` query constrained to one document, so the listen is authorized under the **`list`** rule (not the per-document `get` rule). The query is provably constrained to a single path. An invalid (non-document) path throws `RuntimeException(InvalidArgument)`.

> **Authorization note:** single-document listens are authorized under the `list` rule because they ride on the query listener (not the `get` rule). Write a `list` rule (or `RuleOperations.Read`, which expands to both `get` and `list`) to cover the path so that `ListenToDocument` is permitted.

`WriteBatch` is a fluent helper — construct it with `new WriteBatch(db)`:

```csharp
await new WriteBatch(db)
    .Set("users/u1", new Dictionary<string, Value> { ["name"] = new StringValue("Alice") })
    .Set("counters/c1", new Dictionary<string, Value> { ["n"] = new IntegerValue(0) })
    .CommitAsync();
```

### FieldValue transform helpers

`Winche.Database.Runtime.Writes.FieldValue` is a static factory for building field transforms and the delete sentinel without constructing wire objects by hand.

```csharp
using Winche.Database.Runtime.Writes;

var write = new SetWrite
{
    Path   = "counters/c1",
    Fields = new Dictionary<string, Value>
    {
        ["deleted"] = FieldValue.Delete()   // DeleteFieldValue sentinel — goes in Fields
    },
    Transforms = new List<FieldTransform>
    {
        FieldValue.ServerTimestamp("updatedAt"),
        FieldValue.Increment("count", 1L),
        FieldValue.Maximum("highScore", 99.5),
        FieldValue.Minimum("lowScore", 0L),
        FieldValue.ArrayUnion("tags", new StringValue("vip")),
        FieldValue.ArrayRemove("flags", new StringValue("pending")),
    }
};
```

| Factory method | Returns | Notes |
| --- | --- | --- |
| `FieldValue.ServerTimestamp(field)` | `FieldTransform` | Sets the field to the batch commit time. Place in `Transforms`. |
| `FieldValue.Increment(field, long\|double)` | `FieldTransform` | Saturating integer add; promotes to double on mixed. Place in `Transforms`. |
| `FieldValue.Maximum(field, long\|double)` | `FieldTransform` | Keeps the larger value. NaN is the smallest number. Place in `Transforms`. |
| `FieldValue.Minimum(field, long\|double)` | `FieldTransform` | Keeps the smaller value. Place in `Transforms`. |
| `FieldValue.ArrayUnion(field, params Value[])` | `FieldTransform` | Appends elements not already present. Place in `Transforms`. |
| `FieldValue.ArrayRemove(field, params Value[])` | `FieldTransform` | Removes typed-equal elements. Place in `Transforms`. |
| `FieldValue.Delete()` | `DeleteFieldValue` | Removes the field from the document. Place directly in `Fields` (not in `Transforms`). |

> **Design note:** transforms and the delete sentinel are deliberately heterogeneous: transforms go in the out-of-band `Transforms` list (applied after the write data), while `Delete()` is embedded directly in `Fields`. `SetWrite` with `merge: false` does not accept `Delete()` in `Fields`; use `UpdateWrite` or `merge: true` instead.

#### `SetWrite.MergeFields` — explicit merge mask

`SetWrite.MergeFields` is an `IReadOnlyList<FieldPath>?`. When set, only the listed field paths are written; all other paths in the existing document are left untouched. This is the subset-merge alternative to `merge: true` (which merges every present path). For each masked path: if the path is present in `Fields`, that value is written; if the path is absent from `Fields` or carries a `FieldValue.Delete()` sentinel, that path is **deleted** from the document. A masked intermediate path (e.g. `"m"`) replaces the entire subtree at that key with the data's value.

```csharp
var write = new SetWrite
{
    Path   = "users/u1",
    Fields = new Dictionary<string, Value>
    {
        ["displayName"] = new StringValue("Alice"),
        // "score" is absent — will be deleted because it is in the mask
    },
    MergeFields = new[] { FieldPath.Parse("displayName"), FieldPath.Parse("score") }
};
```

Constraints: `MergeFields` cannot be combined with `merge: true` (→ `INVALID_ARGUMENT`). An empty `MergeFields` array is also `INVALID_ARGUMENT`.

### Auto-id documents (`AddAsync`)

`IDocumentDatabase.AddAsync` creates a document with a server-generated 20-character base62 id and returns the created `Document`.

```csharp
var doc = await db.AddAsync("users", new Dictionary<string, Value>
{
    ["name"]  = new StringValue("Alice"),
    ["score"] = new IntegerValue(0),
});
// doc.Path  = "users/<generated-id>"
// doc.Id    = "<generated-id>"
```

The generated id is cryptographically random (20 chars, base62 alphabet). The operation routes through the normal write path with an `exists: false` precondition, so access rules evaluate it as a **create** operation.

To generate an id without a write, use `Winche.Database.Documents.DocumentId.NewId()` directly:

```csharp
string id = DocumentId.NewId();  // 20-char base62 string
```

### Collection listing (privileged)

The concrete `DocumentDatabase` exposes one capability that `IDocumentDatabase` does **not**: enumerating the
subcollection ids directly under a document (or the top-level collections at the database root). This is a
privileged, admin-only operation — so it lives on the **rule-free core**, is intentionally absent from
`IDocumentDatabase`, and is **not** exposed over the REST/WebSocket transports. Inject the concrete
`DocumentDatabase` (trusted server-side code only) to use it.

```csharp
var sub   = await db.ListCollectionIdsAsync("users/u1");   // subcollections under a document
var roots = await db.ListCollectionIdsAsync(null);         // top-level collections (null/empty = root)

// pageSize (default 100, max 300) + opaque pageToken for keyset pagination
var first = await db.ListCollectionIdsAsync(null, pageSize: 50);
var next  = await db.ListCollectionIdsAsync(null, pageSize: 50, pageToken: first.NextPageToken);
// .CollectionIds : IReadOnlyList<string>  — distinct, UTF-8 byte-ordered
// .NextPageToken : string?                — null when there are no more pages
```

A subcollection is listed if **any** document exists beneath it, even when the intermediate parent document is
absent ("missing"). Because it bypasses the rules engine, treat it like the rest of the concrete
`DocumentDatabase` surface — trusted server-side use only.

## Query API

All queries use the typed `Query`; the wire JSON and C# AST share the same shape. See [PROTOCOL](docs/PROTOCOL.md#4-queries) for the complete filter/operator reference.

```csharp
using Winche.Database.Querying.Ast;
using Winche.Database.Documents;
using Winche.Database.Values;

var query = new Query(
    Collection: "users",
    Where: new FieldFilter(FieldPath.Parse("score"), FilterOperator.Gte, new IntegerValue(50)),
    OrderBy: [new Ordering(FieldPath.Parse("score"), SortDirection.Desc)],
    Limit: 25,
    Offset: 50    // skip the first 50 matches (optional)
);

var result = await db.QueryAsync(query);
// result.Documents : IReadOnlyList<Document>
// result.HasMore   : bool
```

### Filter operators

`Eq`, `Ne`, `Gt`, `Gte`, `Lt`, `Lte`, `In`, `NotIn`, `ArrayContains`, `ArrayContainsAny`, `ArrayContainsAll`, `Contains`, `StartsWith`, `EndsWith`, `Regex`

Inequality operators match values of the **same type-class** only.

### Logical operators

`And`, `Or`, `Not` (via `CompositeFilter`)

### Unary operators

`IsNull`, `IsNan`, `Exists` (via `UnaryFilter`)

### Cursor pagination

```csharp
var cursor = new Cursor(Values: [new IntegerValue(lastScore), new StringValue("users/" + lastId)], Before: false);
var nextPage = new Query("users", OrderBy: [...], Limit: 25, Start: cursor);
```

`Before: false` = `startAfter` (exclusive); `Before: true` = `startAt` (inclusive). Mirror for `End`.

#### `Cursor.FromDocument` — snapshot cursors

`Cursor.FromDocument(Document doc, IReadOnlyList<Ordering>? orderBy, bool before)` builds a cursor from a document snapshot — the snapshot-based form of `startAt`/`startAfter`/`endAt`/`endBefore`. For each field in `orderBy` the cursor picks that field's value from `doc`; a `__name__` tiebreaker is always appended as `ReferenceValue(doc.Path)`. A null or empty `orderBy` yields a `__name__`-only cursor. Throws `ArgumentException` if a required `orderBy` field is absent from the document.

```csharp
var orderBy = new List<Ordering> { new(FieldPath.Parse("score"), SortDirection.Desc) };

// startAt(doc) — inclusive lower bound
var start = Cursor.FromDocument(doc, orderBy, before: true);
var page = new Query("users", OrderBy: orderBy, Limit: 25, Start: start);

// startAfter(doc) — exclusive lower bound
var after = Cursor.FromDocument(doc, orderBy, before: false);
```

| Assignment | `before` | Cursor semantic |
| --- | --- | --- |
| `Query.Start` | `true` | `startAt(snapshot)` |
| `Query.Start` | `false` | `startAfter(snapshot)` |
| `Query.End` | `false` | `endAt(snapshot)` |
| `Query.End` | `true` | `endBefore(snapshot)` |

The cursor travels over the wire as plain tagged values — `Cursor.FromDocument` is a C# convenience only; the wire format is unchanged (see `§4.5` of PROTOCOL.md).

### Field selection (`select`)

`Query.Select` is an `IReadOnlyList<FieldPath>?` that limits the fields returned. When set, only the
chosen fields — top-level or nested (e.g. `address.city`) — are projected at the SQL level; the
full document is never loaded. Field selection is orthogonal to authorization (rules still apply
normally).

```csharp
new Query("users", Select: [FieldPath.Parse("displayName"), FieldPath.Parse("address.city")])
```

### Offset (`offset`)

`Query.Offset` skips the first N matching results before applying `Limit`.

```csharp
// skip the first 200 results and return up to 25
new Query("users",
    OrderBy: [new Ordering(FieldPath.Parse("score"), SortDirection.Desc)],
    Limit: 25, Offset: 200)
```

`Offset` must be `>= 0`; a negative value is a validation error (`BAD_OFFSET`). `Offset` may not be combined with `LimitToLast` (`OFFSET_LIMIT_TO_LAST`).

> **Performance note:** the SQL layer still scans and discards the skipped rows, so large offsets have a performance cost.

### Last-N results (`limitToLast`)

`Query.LimitToLast` returns the **last** N results of the ordered query, in ascending order. Requires at least one `OrderBy`.

```csharp
// last 10 items by score, returned in ascending order
new Query("users",
    OrderBy: [new Ordering(FieldPath.Parse("score"), SortDirection.Asc)],
    LimitToLast: 10)
```

`LimitToLast` is mutually exclusive with `Limit` (`LIMIT_CONFLICT`) and with `Offset` (`OFFSET_LIMIT_TO_LAST`). A value `< 1` is a validation error (`BAD_LIMIT_TO_LAST`). At least one `OrderBy` is required (`LIMIT_TO_LAST_NO_ORDER`).

> **`hasMore` semantics for `limitToLast`:** when a query uses `limitToLast`, `result.HasMore == true` means rows exist *before* the returned window (earlier in the original sort order) — not after it. This is the opposite of the `limit` case, where `hasMore` signals more rows after the current page.

### Counting documents

`CountAsync` runs a `COUNT(*)` over the same match as `QueryAsync`, returning a `long` instead of
materializing documents — reusing the `Query`'s collection, filter, and cursor bounds:

```csharp
var total   = await db.CountAsync(new Query("users"));                                   // whole collection
var active  = await db.CountAsync(new Query("users",
    Where: new FieldFilter(FieldPath.Parse("score"), FilterOperator.Gte, new IntegerValue(50))));
var capped  = await db.CountAsync(new Query("users", Limit: 1000));                       // count is capped at 1000
```

An explicit `Limit` **caps** the count; an absent limit counts the
full match. Counting is authorized like a `list` query under the rules engine — the query must
provably satisfy a read rule (rules-are-not-filters); add the constraining filter to the query to
satisfy the rule.

> **`offset` is ignored by `CountAsync`:** if the query carries an `Offset`, `CountAsync` intentionally ignores it — the count reflects the full filter/cursor match, not the windowed view. `count` has no offset concept.

> **App-side operations:** grouping and joins are not supported natively — use `QueryAsync` to
> retrieve the data and compute those in application code. For count, sum, and average see
> [Aggregation queries](#aggregation-queries) below.

### Aggregation queries

`AggregateAsync` runs one or more aggregations — count, sum, and average — over the same match as `QueryAsync` in a **single** round-trip. Results are returned as an `AggregationResult` whose `Values` property is an `IReadOnlyDictionary<string, Value>` keyed by alias.

Build aggregations with the `Aggregation` factory:

| Factory method | Description |
| --- | --- |
| `Aggregation.Count(alias)` | Count matching documents. Result is an `IntegerValue`. Takes no field. |
| `Aggregation.Sum(field, alias)` | Sum a numeric field. Result is an `IntegerValue` when all operands are integers; a `DoubleValue` when any operand is a double (NaN/Infinity propagated; integer overflow promotes to double). Empty match → `IntegerValue(0)`. Non-numeric and missing field values are ignored. |
| `Aggregation.Average(field, alias)` | Average a numeric field. Result is a `DoubleValue`, or `NullValue` when no numeric operand matched. Non-numeric and missing field values are ignored. |

```csharp
using Winche.Database.Aggregation;

var result = await db.AggregateAsync(
    new Query("orders",
        Where: new FieldFilter(FieldPath.Parse("status"), FilterOperator.Eq, new StringValue("shipped"))),
    new[]
    {
        Aggregation.Count("cnt"),
        Aggregation.Sum("total", "s"),
        Aggregation.Average("total", "a"),
    });

long   count   = ((IntegerValue)result.Values["cnt"]).Value;
long   sum     = ((IntegerValue)result.Values["s"]).Value;
double average = ((DoubleValue)result.Values["a"]).Value;
```

Constraints (→ `INVALID_ARGUMENT`):
- At least 1 and at most 5 aggregations per call.
- `Sum` and `Average` require a field; `Count` takes no field.
- The field may not be `__name__`.
- Aliases must be unique and non-empty.

An explicit `Limit` on the query **caps** all aggregations (consistent with `CountAsync` semantics). Aggregations are authorized like a `list` query — the query must provably satisfy a read rule; add the constraining filter to the query to satisfy the rule.

> **Authorization:** aggregations are authorized under the **`list`** rule (field-agnostic), consistent with `CountAsync` and regular queries.

> **Notes:** an explicit query `limit` caps all aggregations; collection-group queries are not yet supported.

## Transactions

```csharp
// Automatic retry on ABORTED (up to 5 attempts with backoff)
var result = await db.RunTransactionAsync(async tx =>
{
    var doc = await tx.GetAsync("accounts/a1");
    var balance = ((IntegerValue)doc!.Fields["balance"]).Value;
    tx.Set("accounts/a1", new Dictionary<string, Value> { ["balance"] = new IntegerValue(balance - 100) });
    tx.Set("accounts/a2", ...);
    return balance;
});

// Manual lifecycle
var handle = await db.BeginTransactionAsync();
var doc    = await db.GetAsync(handle.Id, "accounts/a1");  // recorded read
await db.CommitTransactionAsync(handle.Id, [new SetWrite { ... }]);
```

Transaction configuration (`WincheDatabaseOptions.TransactionConfig`):

| Field | Default | Description |
| --- | --- | --- |
| `IdleTimeoutSpan` | 60 s | Rolled back if no activity for this duration |
| `TotalTimeoutSpan` | 5 min | Maximum lifetime of an open transaction |
| `CleanupInterval` | 1 s | How often expired transactions are swept |

Write-validation limits (`WincheDatabaseOptions.WriteLimits`):

`WriteLimits` is a record on `WincheDatabaseOptions` that configures limits applied to the **resulting document** of every `set` or `update` (not `delete`). Violations throw `INVALID_ARGUMENT` (HTTP 400 over REST) and abort the **entire** batch — nothing is committed.

| Field | Default | Description |
| --- | --- | --- |
| `MaxDocumentSizeBytes` | 1048576 (1 MiB) | Maximum byte size of the resulting document, calculated using the engine's field byte-budget formula |
| `MaxDepth` | 20 | Maximum map/array nesting depth of the resulting document |
| `RejectReservedFieldNames` | `true` | Rejects field names matching the `__*__` pattern (starting and ending with double underscore) |

These limits are configurable — unlike many document databases that hard-code them.

> **Multi-node note:** Transaction state lives in the serving node's in-memory ledger. Multi-node deployments must use sticky routing (all requests in a transaction to the same node) or use the WebSocket API (connection-pinned). Routing violations surface as `ABORTED` — never corruption.

## REST API

Mapped under `/documents` by default (configurable via `MapWincheDatabaseRestApi(prefix: "...")`). Document paths in URL parameters are Base64-encoded (UTF-8 bytes).

**Convenience endpoints:**

| Method | Route | Description |
| --- | --- | --- |
| `GET` | `/documents/{base64Path}` | Get a document |
| `PUT` | `/documents/{base64Path}` | Create or replace a document (returns document after write) |
| `PATCH` | `/documents/{base64Path}` | Patch fields via dotted field paths |
| `DELETE` | `/documents/{base64Path}` | Delete a document (reads first; 404 if missing) |
| `GET` | `/documents/ping` | Health check |

**Colon-verb endpoints:**

| Method | Route | Description |
| --- | --- | --- |
| `POST` | `/documents:add` | Create a document with a generated id (returns `{"document": <document>}`) |
| `POST` | `/documents:commit` | Atomic write batch (or commit a transaction) |
| `POST` | `/documents:beginTransaction` | Begin an optimistic transaction |
| `POST` | `/documents:rollback` | Roll back a transaction (idempotent; unknown id is a no-op) |
| `POST` | `/documents:batchGet` | Bulk read preserving input order; missing docs are `null` |
| `POST` | `/documents:runQuery` | Execute a query |
| `POST` | `/documents:count` | Count documents matching a query (returns `{ "count": N }`) |
| `POST` | `/documents:aggregate` | Run aggregations over a query (count / sum / average) |

Access rules are enforced on all routes. The claims accessor runs as an endpoint filter before every request.

See [PROTOCOL](docs/PROTOCOL.md#6-rest-api) for full request/response examples.

## WebSocket API

Connect at `/documents/ws?access_token=<jwt>`. The connection authenticates at the HTTP upgrade (see [WebSocket authentication](#3-initialize-schema-and-map-routes) above). On a successful upgrade the server sends `welcome` immediately — there is no `hello` handshake. All subsequent operations are exchanged as typed JSON messages over a single WebSocket.

| Message type | Direction | Description |
| --- | --- | --- |
| `welcome` | S→C | Sent immediately on connect; carries `connectionId` |
| `ping` | C→S | Connection health check |
| `doc.get` | C→S | Get a document |
| `doc.getAll` | C→S | Get multiple documents (preserves order, null for missing) |
| `query` | C→S | Execute a one-shot query |
| `count` | C→S | Count documents matching a query (returns `{ "count": N }`) |
| `aggregate` | C→S | Run aggregations over a query (count / sum / average) |
| `add` | C→S | Create a document with a generated id |
| `write` | C→S | Atomic write batch |
| `tx.begin` | C→S | Start an optimistic transaction |
| `tx.get` | C→S | Get a document inside a transaction (recorded read) |
| `tx.query` | C→S | Query inside a transaction (recorded reads) |
| `tx.commit` | C→S | Commit a transaction |
| `tx.rollback` | C→S | Roll back a transaction (idempotent) |
| `listen` | C→S | Subscribe to a live query |
| `doc.listen` | C→S | Subscribe to a single document (rides on the query listener; uses `list` rule) |
| `unlisten` | C→S | Cancel a subscription |
| `response` | S→C | Successful operation result |
| `error` | S→C | Operation error |
| `listen.snapshot` | S→C | Full query state (REPLACES client list) |
| `listen.delta` | S→C | Incremental change (MUTATES client list by index) |

See [PROTOCOL](docs/PROTOCOL.md#7-websocket-protocol) for full message shapes, close codes, and listener protocol details.

## Upgrading to 8.4.0

8.4.0 is an additive feature release — no data migration and no changes required for code that
*consumes* the database. It adds aggregations (`AggregateAsync`), `AddAsync` (auto-id create),
single-document listeners (`ListenToDocument` / `DocumentSnapshot`), `set` merge masks
(`MergeFields`), query `offset`/`limitToLast`, snapshot cursors, write/document limits, and TTL
policies (`UseTtl`).

- **Only relevant if you implement `IDocumentDatabase` yourself** (the built-in `DocumentDatabase`
  is the sole implementation in normal use): the interface gained `AggregateAsync`, `AddAsync`, and
  `ListenToDocument`. `ListenToDocument` ships with a default implementation; `AggregateAsync` and
  `AddAsync` must be implemented by any custom type. Consumers calling the interface are unaffected.

## Upgrading to 8.0.0

8.0.0 is a breaking release. **Schema changes apply automatically on startup**
(`InitializeWincheDatabaseAsync` runs an idempotent migration) — no manual SQL is required for
existing databases. The code/API changes you must make:

- **Secondary indexes are declared by collection ID**, not a full path / `*` wildcard. Replace
  `i.Add("userData/*/sessionHistory", …)` with `i.Add("sessionHistory", …)`. An index now covers
  every collection with that id under any parent (see [Secondary Indexes](#secondary-indexes)).
- **Hooks register with `UseHooks`, and the path moves to registration.** `AddHook<T>()` is removed
  and `DocumentStoreHook.Path` no longer exists. Replace `config.AddHook<MyHook>()` (plus a `Path`
  override on the class) with `config.UseHooks(h => h.Add<MyHook>("{document=**}"))`
  (see [Document Store Hooks](#document-store-hooks)).
- **Caller claims are injected as `IRuleClaimsAccessor`** (no longer a `Func<…>`). If you register
  claims via `MapClaims`, nothing changes; only direct construction of
  `RuleGuardedDocumentDatabase`/`RulesWriteAuthorizer` is affected.
- **Storage columns and tables were renamed** (`document_path`/`document_id`/`collection_path`/
  `collection_id`; `winche_changes`→`winche_documents_changes`,
  `winche_feed_cursors`→`winche_documents_feed_cursors`). This matters only to external SQL/tooling
  that reads the schema directly — the auto-migration upgrades existing databases in place. A
  standalone script is also available at `docs/migrations/2026-06-16-collection-id-rename.sql` for
  manual/out-of-band migration.

## Requirements

- .NET 10.0
- PostgreSQL (any recent version with JSONB support)

## License

[Elastic License 2.0](LICENSE)
