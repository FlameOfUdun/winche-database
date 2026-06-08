# Winche.Database

[![NuGet version](https://img.shields.io/nuget/v/Winche.Database.svg)](https://www.nuget.org/packages/Winche.Database)

A JSON document database layer built on top of PostgreSQL. Store, query, and subscribe to JSON documents — with PostgreSQL as the storage backend via JSONB.

Supports real-time live queries with indexed delta listeners, optimistic ACID transactions, aggregation pipelines, durable document lifecycle hooks, filtered secondary indexes, and integrates with [Winche.Sentinel](https://github.com/FlameOfUdun/winche-sentinel) for per-document access control.

**Protocol and migration docs:**

- [PROTOCOL](docs/PROTOCOL.md) — complete v3 wire format reference (values, writes, queries, pipelines, REST API, WebSocket protocol)
- [RELEASE-NOTES-v3](docs/RELEASE-NOTES-v3.md) — breaking changes and behavior changes from v2

## Packages

| Package | Description |
| --- | --- |
| `Winche.Database` | Core document store: typed values, CRUD, queries, transactions, live listeners, aggregation pipelines, hooks, filtered indexes, and access rule framework |
| `Winche.Database.AspNetCore` | Shared ASP.NET Core abstractions: `DocumentClaimsAccessor` and the `SetCallerClaimsAccessor` registration extension |
| `Winche.Database.AspNetCore.Rest` | ASP.NET Core minimal API REST endpoints |
| `Winche.Database.AspNetCore.WebSockets` | ASP.NET Core WebSocket protocol v3, real-time delta listeners, and connection management |

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

builder.Services
    .AddWincheDatabase(config =>
    {
        // Connection (required); use "Search Path=myschema" for non-public schemas
        config.ConnectionString = builder.Configuration.GetConnectionString("WincheDatabase")!;

        // Store behavior (optional; these are the defaults)
        config.TransactionConfig = new() { IdleTimeoutSpan = TimeSpan.FromMinutes(1) };

        // Access rules (evaluated on every protected operation)
        config.AddDocumentAccessRule<MyReadRule>();
        config.AddDocumentAccessRule<MyWriteRule>();

        // Document lifecycle hooks (at-least-once, feed-driven, must be idempotent)
        config.AddDocumentStoreHook<MyHook>();

        // Secondary index definitions
        config.AddIndexDefinition<MyIndexDefinition>();

        // Claims accessor — reads auth context from the HTTP request or WebSocket connection
        config.SetCallerClaimsAccessor<MyClaimsAccessor>();
    })
    .AddWincheDatabaseWsApi();   // WebSocket transport
```

`SetCallerClaimsAccessor` is provided by `Winche.Database.AspNetCore.DependencyInjection` and registers the accessor for both the REST and WebSocket transports in one call.

### 3. Initialize schema and map routes

```csharp
await app.InitializeWincheDatabaseAsync();   // Creates winche_* tables/functions and syncs indexes
app.MapWincheDatabaseWsApi();                // WebSocket endpoint: /documents/ws
app.MapWincheDatabaseRestApi();              // REST routes under /documents (configurable)
```

## Features

- **typed engine** — 11 tagged value types (`null`, `bool`, `int64`, `double`, `timestamp`, `string`, `bytes`, `reference`, `geopoint`, `array`, `map`); cross-type total order; same-type-class inequality semantics; `__name__` tiebreaker; int/double numeric equality
- **Document storage** — Store documents as typed field maps; each document carries `path`, `id`, `collection`, `createTime`, `updateTime`, and `version` metadata
- **Querying** — 15 filter operators (including Winche extensions: `arrayContainsAll`, `contains`, `startsWith`, `endsWith`, `regex`, field-compare); `and`/`or`/`not` composites; `orderBy` with `__name__` tiebreaker; cursor-based pagination (`startAt`/`startAfter`/`endAt`/`endBefore`)
- **Live queries with delta listeners** — Subscribe to a query and receive an initial full snapshot, then indexed `added`/`modified`/`removed` deltas with `count` checksum; `listen.snapshot` REPLACES client state, `listen.delta` MUTATES it
- **Optimistic transactions** — Optimistic read-version ledger; `ABORTED` on conflict with safe retry; `RunTransactionAsync` with automatic retry; reads-before-writes enforced; idle (60 s) and absolute (5 min) timeouts
- **Batch writes** — Atomic commit of up to 500 `set`/`update`/`delete` operations with field transforms, preconditions, and a single commit timestamp; use `new WriteBatch(db)` for a fluent builder
- **Field transforms** — `serverTimestamp`, `increment` (saturating int, promotes to double on mixed), `maximum`, `minimum`, `arrayUnion`, `arrayRemove`
- **Aggregation pipelines** — Multi-stage pipelines: `match`, `filter`, `lookup`, `unwind`, `group` (with `having`), `project`, `sort`, `limit`, `skip`; 9 accumulators: `count`, `sum`, `avg`, `min`, `max`, `push`, `addToSet`, `first`, `last`
- **Durable hooks** — Feed-driven, true at-least-once end-to-end; hooks execute inline+sequentially per batch; failed batches retried with capped backoff; hooks fire for writes from any node; cursor persisted across restarts; idempotency required
- **Filtered secondary indexes** — `IndexDefinition.Where` for partial indexes; agreement tested against engine
- **Access control** — Per-document and collection-level access rules via Winche.Sentinel; OR semantics with default-deny (any matching rule that grants allows; nothing grants ⇒ denied); reads filtered post-execution; aggregations require a dedicated `Aggregate` grant (read access does not imply aggregate access)
- **PostgreSQL backend** — All data stored as typed JSONB; queries compiled to native PostgreSQL SQL with `winche_rank`/`winche_num`/`winche_text` helper functions; `IMMUTABLE` functions back expression indexes

## Access Rules

Access rules determine whether a caller can perform an operation. Implement `DocumentAccessRule` and register it with `AddDocumentAccessRule<T>()`.

```csharp
using Winche.Database.Abstraction;
using Winche.Database.Documents;
using Winche.Sentinel.Models;

public class OwnerReadRule : DocumentAccessRule
{
    // Path pattern: literal segments and {param} wildcards; ** matches any depth
    public override string Path => "users/{userId}";

    public override IReadOnlySet<AccessOperation> Operations =>
        new HashSet<AccessOperation> { AccessOperation.Read };

    public override Task<bool> EvaluateAsync(AccessContext<Document> context, CancellationToken ct)
    {
        var uid = context.Claims.TryGetValue("uid", out var v) ? v as string : null;
        context.Params.TryGetValue("userId", out var userId);
        return Task.FromResult(uid != null && uid == userId);
    }
}
```

**Semantics:** Rules **grant** access; there is no explicit deny (Firestore-style). A request is allowed if **any** rule whose path pattern and `Operations` set match returns `true` (OR). A matching rule that returns `false` does not veto — it simply doesn't grant. If no rule grants, access is denied (default-deny). Registration order does not affect the decision.

Because a grant cannot be revoked by another rule, **grant narrowly**: don't write a broad `**` read grant and expect a more specific rule to restrict it — instead grant read only where it should be allowed (e.g. an owner-scoped rule like the one above) and let default-deny cover the rest.

The operations are `Read`, `Write`, `Delete`, and `Aggregate`.

**Query access** is checked per-document after the query runs — documents for which the caller is denied are silently dropped from the result set, so partial results are returned rather than an error.

**Aggregation access** is gated by the dedicated `Aggregate` operation, checked at the collection level on the `collection` of every `match` **and** `lookup` stage before the pipeline runs. It is deny-by-default and independent of `Read`: granting read access to a collection does **not** authorize aggregating over it, because an aggregate result (count/sum/min/max, or `push`/`first`) can reveal information about documents the caller cannot read individually. Every collection whose rows can reach the output — including `lookup` targets — needs its own `Aggregate` grant. Individual result rows are not filtered; the collection-level grant is the entire boundary.

### Claims Accessor

To supply per-request caller claims, implement `DocumentClaimsAccessor` (from `Winche.Database.AspNetCore`):

```csharp
using Winche.Database.AspNetCore.Abstraction;

public class CallerClaimsAccessor : DocumentClaimsAccessor
{
    public override IReadOnlyDictionary<string, object?> MapClaims(HttpContext httpContext)
    {
        var uid = httpContext.User.FindFirst("sub")?.Value;
        return uid is null
            ? ImmutableDictionary<string, object?>.Empty
            : new Dictionary<string, object?> { ["uid"] = uid };
    }
}
```

Register it once and it is shared by both the REST and WebSocket transports:

```csharp
config.SetCallerClaimsAccessor<CallerClaimsAccessor>();
```

## Document Store Hooks

Hooks let you react to document mutations. Implement `DocumentStoreHook` and register with `AddDocumentStoreHook<T>()`. Hooks execute **inline and sequentially** inside `HookFeedConsumer`, which is driven by a durable feed runner. Delivery is **true at-least-once end-to-end** — the cursor advances only after all hooks in a batch succeed, and failed batches are retried with capped backoff (1 s → 30 s). Implementations **must be idempotent** (use the document `version` as an idempotency key).

```csharp
using Winche.Database.Abstraction;
using Winche.Database.Documents;

public class AuditHook : DocumentStoreHook
{
    // Limit this hook to a specific subtree; use "**" to match everything
    public override string Path => "orders/**";

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

## `IDocumentDatabase`

The primary service. Inject `IDocumentDatabase` to interact with the store from application code.

> **Guarded vs. core.** `IDocumentDatabase` is bound in DI to `GuardedDocumentDatabase`, which enforces access rules on every operation. The concrete `DocumentDatabase` is the **rule-free core** that the guard decorates — inject it *only* in trusted server-side code (seeding, migrations, internal jobs) that should deliberately bypass authorization. Application code subject to access rules must depend on `IDocumentDatabase`, **never** the concrete `DocumentDatabase`.

```csharp
public interface IDocumentDatabase
{
    // Reads
    Task<Document?> GetAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<Document?>> GetAllAsync(IReadOnlyList<string> paths, CancellationToken ct = default);
    Task<QueryResult> QueryAsync(QueryAst query, CancellationToken ct = default);
    Task<PipelineResult> AggregateAsync(PipelineAst pipeline, CancellationToken ct = default);

    // Writes — every mutation is a Write[]; singles are sugar
    Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<Write> writes, CancellationToken ct = default);

    // Transactions (optimistic)
    Task<TransactionHandle> BeginTransactionAsync(CancellationToken ct = default);
    Task<Document?> GetAsync(string transactionId, string path, CancellationToken ct = default);
    Task<QueryResult> QueryAsync(string transactionId, QueryAst query, CancellationToken ct = default);
    Task<IReadOnlyList<WriteResult>> CommitTransactionAsync(string transactionId, IReadOnlyList<Write> writes, CancellationToken ct = default);
    Task RollbackTransactionAsync(string transactionId, CancellationToken ct = default);
    Task<T> RunTransactionAsync<T>(Func<TransactionContext, Task<T>> body, TransactionOptions? options = null, CancellationToken ct = default);

    // Live queries
    IQueryListener Listen(QueryAst query, ListenOptions? options = null);
}
```

`WriteBatch` is a fluent helper — construct it with `new WriteBatch(db)`:

```csharp
await new WriteBatch(db)
    .Set("users/u1", new Dictionary<string, Value> { ["name"] = new StringValue("Alice") })
    .Set("counters/c1", new Dictionary<string, Value> { ["n"] = new IntegerValue(0) })
    .CommitAsync();
```

## Query API

All queries use the typed `QueryAst`; the wire JSON and C# AST share the same shape. See [PROTOCOL](docs/PROTOCOL.md#4-queries) for the complete filter/operator reference.

```csharp
using Winche.Database.Querying.Ast;
using Winche.Database.Documents;
using Winche.Database.Values;

var query = new QueryAst(
    Collection: "users",
    Where: new FieldFilterAst(FieldPath.Parse("score"), FilterOperator.Gte, new IntegerValue(50)),
    OrderBy: [new OrderAst(FieldPath.Parse("score"), SortDirection.Desc)],
    Limit: 25
);

var result = await db.QueryAsync(query);
// result.Documents : IReadOnlyList<Document>
// result.HasMore   : bool
```

### Filter operators

`Eq`, `Ne`, `Gt`, `Gte`, `Lt`, `Lte`, `In`, `NotIn`, `ArrayContains`, `ArrayContainsAny`, `ArrayContainsAll`, `Contains`, `StartsWith`, `EndsWith`, `Regex`

Inequality operators match values of the **same type-class** only.

### Logical operators

`And`, `Or`, `Not` (via `CompositeFilterAst`)

### Unary operators

`IsNull`, `IsNan`, `Exists` (via `UnaryFilterAst`)

### Cursor pagination

```csharp
var cursor = new CursorAst(Values: [new IntegerValue(lastScore), new StringValue("users/" + lastId)], Before: false);
var nextPage = new QueryAst("users", OrderBy: [...], Limit: 25, Start: cursor);
```

`Before: false` = `startAfter` (exclusive); `Before: true` = `startAt` (inclusive). Mirror for `End`.

## Aggregation Pipelines

```csharp
using Winche.Database.Querying.Ast;

var pipeline = new PipelineAst([
    new MatchStageAst("orders", Where: null),
    new LookupStageAst("users", FieldPath.Parse("userId"), FieldPath.Parse("id"), "user"),
    new UnwindStageAst(FieldPath.Parse("user"), "user"),
    new GroupStageAst(
        Keys: [new GroupKeyAst("status", FieldPath.Parse("status"))],
        Accumulators: [new AccumulatorAst("total", AggFunction.Sum, FieldPath.Parse("amount"))]
    ),
    new SortStageAst([new OrderAst(FieldPath.Parse("total"), SortDirection.Desc)]),
    new LimitStageAst(10),
]);

var result = await db.AggregateAsync(pipeline);
// result.Rows : IReadOnlyList<IReadOnlyDictionary<string, Value>>
```

Available stages: `MatchStageAst`, `FilterStageAst`, `LookupStageAst`, `UnwindStageAst`, `GroupStageAst`, `ProjectStageAst`, `SortStageAst`, `LimitStageAst`, `SkipStageAst`.

Available accumulator functions: `Count`, `Sum`, `Avg`, `Min`, `Max`, `Push`, `AddToSet`, `First`, `Last`.

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
| `POST` | `/documents:commit` | Atomic write batch (or commit a transaction) |
| `POST` | `/documents:beginTransaction` | Begin an optimistic transaction |
| `POST` | `/documents:rollback` | Roll back a transaction (idempotent; unknown id is a no-op) |
| `POST` | `/documents:batchGet` | Bulk read preserving input order; missing docs are `null` |
| `POST` | `/documents:runQuery` | Execute a query |
| `POST` | `/documents:aggregate` | Execute an aggregation pipeline |

Access rules are enforced on all routes. The claims accessor runs as an endpoint filter before every request.

See [PROTOCOL](docs/PROTOCOL.md#7-rest-api) for full request/response examples.

## WebSocket API

Connect at `/documents/ws`. All operations are exchanged as typed JSON messages over a single WebSocket. Handshake: send `{"type":"hello","protocol":3}` — receive `{"type":"welcome","connectionId":"...","protocol":3}`.

| Message type | Direction | Description |
| --- | --- | --- |
| `hello` | C→S | Handshake (first frame, protocol 3) |
| `welcome` | S→C | Handshake accepted |
| `ping` | C→S | Connection health check |
| `auth.refresh` | C→S | Swap connection claims mid-session |
| `doc.get` | C→S | Get a document |
| `doc.getAll` | C→S | Get multiple documents (preserves order, null for missing) |
| `query` | C→S | Execute a one-shot query |
| `aggregate` | C→S | Execute an aggregation pipeline |
| `write` | C→S | Atomic write batch |
| `tx.begin` | C→S | Start an optimistic transaction |
| `tx.get` | C→S | Get a document inside a transaction (recorded read) |
| `tx.query` | C→S | Query inside a transaction (recorded reads) |
| `tx.commit` | C→S | Commit a transaction |
| `tx.rollback` | C→S | Roll back a transaction (idempotent) |
| `listen` | C→S | Subscribe to a live query |
| `unlisten` | C→S | Cancel a subscription |
| `response` | S→C | Successful operation result |
| `error` | S→C | Operation error |
| `listen.snapshot` | S→C | Full query state (REPLACES client list) |
| `listen.delta` | S→C | Incremental change (MUTATES client list by index) |

See [PROTOCOL](docs/PROTOCOL.md#8-websocket-protocol-v3) for full message shapes, close codes, and listener protocol details.

## Requirements

- .NET 10.0
- PostgreSQL (any recent version with JSONB support)

## License

[Elastic License 2.0](LICENSE)
