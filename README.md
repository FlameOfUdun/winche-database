# Winche.Database

[![NuGet version](https://img.shields.io/nuget/v/Winche.Database.svg)](https://www.nuget.org/packages/Winche.Database)

A JSON document database layer built on top of PostgreSQL. Store, query, and subscribe to JSON documents — with PostgreSQL as the storage backend via JSONB.

Supports real-time live queries with indexed delta listeners, optimistic ACID transactions, durable document lifecycle hooks, filtered secondary indexes, and a built-in Firestore-style access-rules engine (Winche.Rules).

**Protocol docs:**

- [PROTOCOL](docs/PROTOCOL.md) — wire format reference (values, writes, queries, REST API, WebSocket protocol)

## Packages

| Package | Description |
| --- | --- |
| `Winche.Database` | Core document store: typed values, CRUD, queries, transactions, live listeners, hooks, filtered indexes, and Firestore-style access rules (Winche.Rules) |
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

        // Firestore-style access rules (evaluated in memory on every operation)
        config.UseRules(r =>
            r.Match("users/{userId}", u =>
            {
                u.Allow(RuleOperations.Read,  Expr.Auth("uid").Eq(Expr.Param("userId")));
                u.Allow(RuleOperations.Write, Expr.Auth("uid").Eq(Expr.Param("userId")));
            }));

        // Document lifecycle hooks (at-least-once, feed-driven, must be idempotent)
        config.AddHook<MyHook>();

        // Secondary indexes
        config.UseIndexes(i => i.Add("userData/*/sessionHistory", new IndexField("startedAt", SortDirection.Desc)));

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
- **Querying** — 15 filter operators (including Winche extensions: `arrayContainsAll`, `contains`, `startsWith`, `endsWith`, `regex`, field-compare); `and`/`or`/`not` composites; `orderBy` with `__name__` tiebreaker; cursor-based pagination (`startAt`/`startAfter`/`endAt`/`endBefore`); field projection via `select`
- **Live queries with delta listeners** — Subscribe to a query and receive an initial full snapshot, then indexed `added`/`modified`/`removed` deltas with `count` checksum; `listen.snapshot` REPLACES client state, `listen.delta` MUTATES it
- **Optimistic transactions** — Optimistic read-version ledger; `ABORTED` on conflict with safe retry; `RunTransactionAsync` with automatic retry; reads-before-writes enforced; idle (60 s) and absolute (5 min) timeouts
- **Batch writes** — Atomic commit of up to 500 `set`/`update`/`delete` operations with field transforms, preconditions, and a single commit timestamp; use `new WriteBatch(db)` for a fluent builder
- **Field transforms** — `serverTimestamp`, `increment` (saturating int, promotes to double on mixed), `maximum`, `minimum`, `arrayUnion`, `arrayRemove`
- **Durable hooks** — Feed-driven, true at-least-once end-to-end; hooks execute inline+sequentially per batch; failed batches retried with capped backoff; hooks fire for writes from any node; cursor persisted across restarts; idempotency required
- **Secondary indexes** — composite expression indexes per collection; `IndexDefinition.Where` for filtered/partial indexes (agreement tested against engine); **wildcard `Path` patterns** back a whole family of sibling subcollections from one definition (see below)
- **Access control** — Firestore-style in-memory rules engine (Winche.Rules); OR semantics with default-deny; for `list`/query operations the query must provably satisfy a read rule or it is rejected with PERMISSION_DENIED (rules are not post-filters)
- **PostgreSQL backend** — All data stored as typed JSONB; queries compiled to native PostgreSQL SQL with `winche_rank`/`winche_num`/`winche_text` helper functions; `IMMUTABLE` functions back expression indexes

## Secondary Indexes

An `IndexDefinition` declares a composite expression index over the `winche_*` ordering family for a
collection. `IndexDefinition.Path` is **either** an exact collection path **or** a wildcard pattern
with `*` at document-id positions, letting one definition back a whole family of sibling
subcollections:

```csharp
// record — one definition backs every user's per-entity subcollection
config.UseIndexes(i =>
    i.Add("userData/*/sessionHistory", new IndexField("startedAt", SortDirection.Desc)));
```

You may pin some ancestor ids and wildcard others (`orgs/acme/teams/*/members`). Per-entity queries
(`collection = "userData/alice/sessionHistory"`) use the matching pattern index automatically —
**no change to query code**.

Pattern grammar (violations throw `InvalidPathPatternException` at schema-sync):

- A collection path has an **odd** number of non-empty segments.
- `*` is allowed **only at document-id positions** and must be a **whole** segment (no `a*`, `**`).
- Literal segments match `[A-Za-z0-9_-]+`.

Cross-collection ("collection group") queries — querying a field across *all* members of a family at
once — are not yet supported.

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

| Expression | Firestore equivalent |
| --- | --- |
| `Expr.Auth("uid")` | `request.auth.uid` |
| `Expr.Auth("token", "role")` | `request.auth.token.role` |
| `Expr.Param("userId")` | path-capture variable `userId` |
| `Expr.Resource("data", "ownerId")` | `resource.data.ownerId` (existing document) |
| `Expr.RequestResource("data", "status")` | `request.resource.data.status` (full post-write doc, with field transforms resolved) |
| `Expr.Time()` | `request.time` |
| `Expr.Exists(path)` | `exists(path)` — cross-document existence check |
| `Expr.Get(path)` | `get(path)` — cross-document read |
| `Expr.Any(a, b)` | `a \|\| b` (OR) |
| `Expr.All(a, b)` | `a && b` (AND) |
| `.Eq(...)` / `.Ne(...)` / `.Lt(...)` etc. | comparison operators |

### Semantics

Rules **grant** access; there is no explicit deny (Firestore-style). A request is allowed if **any**
rule whose path pattern and operations set match returns `true` (OR). A matching rule that returns
`false` does not veto — it simply doesn't grant. If no rule grants, access is denied (default-deny).
Multiple `UseRules` calls accumulate — each registered ruleset's blocks are OR-combined with all
others. Registration order does not affect the decision.

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

Hooks let you react to document mutations. Implement `DocumentStoreHook` and register with `AddHook<T>()`. Hooks execute **inline and sequentially** inside `HookFeedConsumer`, which is driven by a durable feed runner. Delivery is **true at-least-once end-to-end** — the cursor advances only after all hooks in a batch succeed, and failed batches are retried with capped backoff (1 s → 30 s). Implementations **must be idempotent** (use the document `version` as an idempotency key).

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

> **Guarded vs. core.** `IDocumentDatabase` is bound in DI to `RuleGuardedDocumentDatabase`, which enforces access rules on every operation. The concrete `DocumentDatabase` is the **rule-free core** that the guard decorates — inject it *only* in trusted server-side code (seeding, migrations, internal jobs) that should deliberately bypass authorization. Application code subject to access rules must depend on `IDocumentDatabase`, **never** the concrete `DocumentDatabase`.

```csharp
public interface IDocumentDatabase
{
    // Reads
    Task<Document?> GetAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<Document?>> GetAllAsync(IReadOnlyList<string> paths, CancellationToken ct = default);
    Task<QueryResult> QueryAsync(Query query, CancellationToken ct = default);
    Task<long> CountAsync(Query query, CancellationToken ct = default);

    // Writes — every mutation is a Write[]; singles are sugar
    Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<Write> writes, CancellationToken ct = default);

    // Transactions (optimistic)
    Task<TransactionHandle> BeginTransactionAsync(CancellationToken ct = default);
    Task<Document?> GetAsync(string transactionId, string path, CancellationToken ct = default);
    Task<QueryResult> QueryAsync(string transactionId, Query query, CancellationToken ct = default);
    Task<IReadOnlyList<WriteResult>> CommitTransactionAsync(string transactionId, IReadOnlyList<Write> writes, CancellationToken ct = default);
    Task RollbackTransactionAsync(string transactionId, CancellationToken ct = default);
    Task<T> RunTransactionAsync<T>(Func<TransactionContext, Task<T>> body, TransactionOptions? options = null, CancellationToken ct = default);

    // Live queries
    IQueryListener Listen(Query query, ListenOptions? options = null);
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

All queries use the typed `Query`; the wire JSON and C# AST share the same shape. See [PROTOCOL](docs/PROTOCOL.md#4-queries) for the complete filter/operator reference.

```csharp
using Winche.Database.Querying.Ast;
using Winche.Database.Documents;
using Winche.Database.Values;

var query = new Query(
    Collection: "users",
    Where: new FieldFilter(FieldPath.Parse("score"), FilterOperator.Gte, new IntegerValue(50)),
    OrderBy: [new Ordering(FieldPath.Parse("score"), SortDirection.Desc)],
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

`And`, `Or`, `Not` (via `CompositeFilter`)

### Unary operators

`IsNull`, `IsNan`, `Exists` (via `UnaryFilter`)

### Cursor pagination

```csharp
var cursor = new Cursor(Values: [new IntegerValue(lastScore), new StringValue("users/" + lastId)], Before: false);
var nextPage = new Query("users", OrderBy: [...], Limit: 25, Start: cursor);
```

`Before: false` = `startAfter` (exclusive); `Before: true` = `startAt` (inclusive). Mirror for `End`.

### Field selection (`select`)

`Query.Select` is an `IReadOnlyList<FieldPath>?` that limits the fields returned. When set, only the
chosen fields — top-level or nested (e.g. `address.city`) — are projected at the SQL level; the
full document is never loaded. Field selection is orthogonal to authorization (rules still apply
normally).

```csharp
new Query("users", Select: [FieldPath.Parse("displayName"), FieldPath.Parse("address.city")])
```

### Counting documents

`CountAsync` runs a `COUNT(*)` over the same match as `QueryAsync`, returning a `long` instead of
materializing documents — reusing the `Query`'s collection, filter, and cursor bounds:

```csharp
var total   = await db.CountAsync(new Query("users"));                                   // whole collection
var active  = await db.CountAsync(new Query("users",
    Where: new FieldFilter(FieldPath.Parse("score"), FilterOperator.Gte, new IntegerValue(50))));
var capped  = await db.CountAsync(new Query("users", Limit: 1000));                       // count is capped at 1000
```

An explicit `Limit` **caps** the count (Firestore `count()` semantics); an absent limit counts the
full match. Counting is authorized like a `list` query under the rules engine — the query must
provably satisfy a read rule (rules-are-not-filters); add the constraining filter to the query to
satisfy the rule.

> **App-side aggregation:** aggregation beyond counting (sum, average, grouping, joins) is performed
> app-side over per-collection rule-checked queries. Use `QueryAsync` or `CountAsync` to retrieve
> the data, then compute aggregates in application code.

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
| `POST` | `/documents:count` | Count documents matching a query (returns `{ "count": N }`) |

Access rules are enforced on all routes. The claims accessor runs as an endpoint filter before every request.

See [PROTOCOL](docs/PROTOCOL.md#7-rest-api) for full request/response examples.

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

See [PROTOCOL](docs/PROTOCOL.md#8-websocket-protocol) for full message shapes, close codes, and listener protocol details.

## Requirements

- .NET 10.0
- PostgreSQL (any recent version with JSONB support)

## License

[Elastic License 2.0](LICENSE)
