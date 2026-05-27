# Winche.Database

A JSON document database layer built on top of PostgreSQL. Store, query, and subscribe to JSON documents — with PostgreSQL as the storage backend via JSONB.

Supports real-time subscriptions, ACID transactions, aggregation pipelines, conflict-free sync mutations, document lifecycle hooks, and integrates with [Winche.Sentinel](https://github.com/FlameOfUdun/winche-sentinel) for per-document access control.

## Packages

| Package | Description |
| --- | --- |
| `Winche.Database` | Core document store: CRUD, queries, transactions, subscriptions, aggregations, sync, hooks, and access rule framework |
| `Winche.Database.AspNetCore` | Shared ASP.NET Core abstractions: `DocumentClaimsAccessor` and the `SetCallerClaimsAccessor` registration extension |
| `Winche.Database.AspNetCore.Rest` | ASP.NET Core minimal API REST endpoints |
| `Winche.Database.AspNetCore.WebSockets` | ASP.NET Core WebSocket protocol, real-time event dispatch, and connection management |

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
  },
  "WincheDatabase": {
    "Schema": "public",
    "TableName": "documents",
    "TransactionConfig": {
      "TotalTimeoutSpan": "00:05:00",
      "IdleTimeoutSpan": "00:05:00",
      "CleanupInterval": "00:00:01"
    }
  }
}
```

The connection string key is `WincheDatabase`, with `DefaultConnection` as a fallback.

### 2. Register services

```csharp
using Winche.Database.DependencyInjection;
using Winche.Database.AspNetCore.DependencyInjection;
using Winche.Database.AspNetCore.Rest.DependencyInjection;
using Winche.Database.AspNetCore.WebSockets.DependencyInjection;

builder.Services
    .AddWincheDatabase(builder.Configuration, config =>
    {
        // Access rules (evaluated on every protected operation)
        config.AddDocumentAccessRule<MyReadRule>();
        config.AddDocumentAccessRule<MyWriteRule>();

        // Document lifecycle hooks
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
app.UseWincheDatabase();           // Ensures the documents table and indexes exist
app.UseWincheDatabaseWsApi();      // WebSocket endpoint: /documents/ws
app.UseWincheDatabaseRestApi();    // REST routes under /documents (configurable)
```

## Features

- **Document storage** — Store arbitrary JSON documents; each document gets automatic metadata (`id`, `version`, `createdAt`, `updatedAt`)
- **Querying** — Filter with 16 conditional operators, sort, limit, offset, and cursor-based pagination
- **Real-time subscriptions** — Subscribe to queries and receive live push events over WebSocket when matching documents change
- **ACID transactions** — Multi-document transactions with commit/rollback, idle timeout, total timeout, and automatic cleanup
- **Aggregation pipelines** — Multi-stage pipelines: `match`, `lookup`, `unwind`, `group`, `project`, `sort`, `limit`, `skip`
- **Batch operations** — Atomic commit of multiple set/update/delete operations in a single request
- **Sync mutations** — Conflict-free document mutations (`Set`, `Update`, `Delete`) with optional base-version conflict detection
- **Access control** — Per-document and collection-level access rules via Winche.Sentinel; OR semantics (any matching rule grants access)
- **Lifecycle hooks** — React to document set, update, and delete events with path-scoped hooks
- **PostgreSQL backend** — All data stored as JSONB; queries translated to native PostgreSQL SQL

## Access Rules

Access rules determine whether a caller can perform an operation. Implement `DocumentAccessRule` and register it with `AddDocumentAccessRule<T>()`.

```csharp
using Winche.Database.Abstraction;
using Winche.Database.Core.Models;
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

**Semantics:** Access is granted if **any** matching rule returns `true` (OR). If no rule matches the path and operation, access is denied.

**Query access** is checked per-document after the query runs — documents for which the caller is denied are silently dropped from the result set, so partial results are returned rather than an error.

**Aggregation access** is checked at the collection level (the `Collection` of each `MatchStage` or `LookupStage`). Individual result rows are not filtered.

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

Hooks let you react to document mutations. Implement `DocumentStoreHook` and register with `AddDocumentStoreHook<T>()`.

```csharp
using Winche.Database.Abstraction;
using Winche.Database.Core.Models;

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

## `IDocumentManager`

The core service. Inject `IDocumentManager` to interact with the store directly from application code.

```csharp
public interface IDocumentManager
{
    // Protected — access rules enforced
    Task<Document?>      GetAsync(string path, CancellationToken ct = default);
    Task<Document>       SetAsync(string path, JsonObject data, CancellationToken ct = default);
    Task<Document?>      UpdateAsync(string path, JsonObject patch, CancellationToken ct = default);
    Task<bool>           DeleteAsync(string path, CancellationToken ct = default);
    Task<QueryResult>    QueryAsync(Query query, CancellationToken ct = default);
    Task<AggregateResult> AggregateAsync(AggregationPipeline pipeline, CancellationToken ct = default);
    Task<CommitResult>   CommitAsync(OperationBatch batch, CancellationToken ct = default);
    Task<SyncResult>     SyncAsync(MutationBatch batch, CancellationToken ct = default);

    // Unprotected — bypass access rules (server-side use only)
    Task<Document?>      GetUnprotectedAsync(string path, CancellationToken ct = default);
    Task<Document>       SetUnprotectedAsync(string path, JsonObject data, CancellationToken ct = default);
    Task<Document?>      UpdateUnprotectedAsync(string path, JsonObject patch, CancellationToken ct = default);
    Task<bool>           DeleteUnprotectedAsync(string path, CancellationToken ct = default);
    Task<QueryResult>    QueryUnprotectedAsync(Query query, CancellationToken ct = default);
    Task<AggregateResult> AggregateUnprotectedAsync(AggregationPipeline pipeline, CancellationToken ct = default);
    Task<CommitResult>   CommitUnprotectedAsync(OperationBatch batch, CancellationToken ct = default);
    Task<SyncResult>     SyncUnprotectedAsync(string path, List<Mutation> mutations, CancellationToken ct = default);
}
```

Cascade deletes are automatic: deleting a document path also removes all documents nested under it.

## Query API

```csharp
var result = await manager.QueryAsync(new Query
{
    Collection = "users",
    Where      = /* WhereNode — see below */,
    OrderBy    = [new SortNode("age", SortDirection.Desc)],
    Limit      = 50,
    StartAfter = [lastSeenAge, lastSeenId],  // cursor pagination
});

// result.Documents : List<Document>
// result.Total     : long (total matching rows, ignoring limit)
```

### Conditional operators

`Eq`, `Ne`, `Gt`, `Gte`, `Lt`, `Lte`, `In`, `Nin`, `Contains`, `StartsWith`, `EndsWith`, `Regex`, `ArrContains`, `ArrContainsAny`, `ArrContainsAll`, `Exists`

### Logical operators

`And`, `Or`, `Not`

### Cursor pagination

Pass the ordered field values of the last seen document in `StartAfter` (exclusive) or `StartAt` (inclusive). `EndBefore` and `EndAt` cap the range from the other end. Values must correspond positionally to the `OrderBy` fields.

## Aggregation Pipelines

```csharp
var result = await manager.AggregateAsync(new AggregationPipeline
{
    Stages =
    [
        new MatchStage("orders", /* optional WhereNode filter */),
        new LookupStage("users", localField: "userId", foreignField: "id", as: "user"),
        new UnwindStage("user", as: "user"),
        new GroupStage(
            Keys:         [new GroupKey("status", "status")],
            Accumulators: [new AccumulatorField("total", AggFunction.Sum, "amount")]
        ),
        new SortStage([new SortNode("total", SortDirection.Desc)]),
        new LimitStage(10),
    ]
});
```

Available stages: `MatchStage`, `LookupStage`, `UnwindStage`, `GroupStage`, `ProjectStage`, `SortStage`, `LimitStage`, `SkipStage`.

Available accumulator functions: `Count`, `Sum`, `Avg`, `Min`, `Max`, `Push`, `AddToSet`, `First`, `Last`.

Available field types for typed field references: `Text`, `Numeric`, `Boolean`, `Timestamp`, `Integer`, `BigInt`, `Double`, `Date`, `Uuid`, `Jsonb`.

## Transactions

```csharp
// Transactions are managed over the WebSocket API.
// The flow: TransactionBegin → operations → TransactionCommit (or TransactionRollback)
// Idle and total timeouts are enforced automatically; expired transactions are rolled back.
```

Transaction configuration (`TransactionConfig`):

| Field | Default | Description |
| --- | --- | --- |
| `TotalTimeoutSpan` | 5 min | Maximum lifetime of an open transaction |
| `IdleTimeoutSpan` | 5 min | Rolled back if no activity for this duration |
| `CleanupInterval` | 1 s | How often expired transactions are swept |

## Sync Mutations

Sync applies an ordered list of mutations to a single document path, with optional conflict detection:

```csharp
var result = await manager.SyncAsync(new MutationBatch
{
    Path = "notes/abc",
    Mutations =
    [
        new Mutation { Type = MutationType.Set,    Data = newData, BaseVersion = 3 },
        new Mutation { Type = MutationType.Update, Data = patch },
        new Mutation { Type = MutationType.Delete },
    ]
});

// result.HasConflict : true if server version != BaseVersion of the first mutation
// result.Document    : current server document (after apply, or current if conflict)
// result.AppliedCount: number of mutations applied before conflict/error
```

If `BaseVersion` is set and the server document's version does not match, the batch is rejected without modification and `HasConflict = true` is returned.

## REST API

Mapped under `/documents` by default (configurable via `UseWincheDatabaseRestApi(prefix: "...")`).

Document paths in URL parameters are Base64-encoded to avoid routing conflicts.

| Method | Route | Description |
| --- | --- | --- |
| `GET` | `/documents/{base64path}` | Get a document |
| `PUT` | `/documents/{base64path}` | Create or replace a document |
| `PATCH` | `/documents/{base64path}` | Partially update a document |
| `DELETE` | `/documents/{base64path}` | Delete a document (cascade) |
| `POST` | `/documents/query` | Execute a query |
| `POST` | `/documents/aggregate` | Execute an aggregation pipeline |
| `POST` | `/documents/commit` | Atomic batch of operations |
| `POST` | `/documents/synchronize` | Apply sync mutations |
| `GET` | `/documents/ping` | Health check |

Access rules are enforced on all routes. The claims accessor runs as an endpoint filter before every request.

## WebSocket API

Connect at `/documents/ws`. All operations are exchanged as typed JSON messages. The WebSocket transport supports the full feature set including real-time subscriptions and multi-operation transactions.

| Message type | Description |
| --- | --- |
| `system.ping` | Connection health check |
| `document.get` | Get a document |
| `document.set` | Create or replace a document |
| `document.update` | Patch a document |
| `document.delete` | Delete a document |
| `query.execute` | Run a one-shot query |
| `query.subscribe` | Subscribe to a live query |
| `query.unsubscribe` | Cancel a subscription |
| `aggregate.execute` | Run an aggregation pipeline |
| `transaction.begin` | Start a transaction |
| `transaction.get` | Get inside a transaction |
| `transaction.set` | Set inside a transaction |
| `transaction.update` | Update inside a transaction |
| `transaction.delete` | Delete inside a transaction |
| `transaction.query` | Query inside a transaction |
| `transaction.commit` | Commit a transaction |
| `transaction.rollback` | Roll back a transaction |
| `batch.commit` | Atomic batch commit |
| `sync.push` | Apply sync mutations |

## Requirements

- .NET 10.0
- PostgreSQL (any recent version with JSONB support)

## License

Elastic License 2.0
