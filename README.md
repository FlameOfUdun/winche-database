# WincheDatabase

A JSON document database layer built on top of PostgreSQL. WincheDatabase lets you store, query, and subscribe to JSON documents using a structured query language — with PostgreSQL as the storage backend via JSONB.

It exposes both a **REST API** and a **WebSocket API**, supports real-time subscriptions, ACID transactions, aggregation pipelines, and integrates with [WincheSentinel](https://github.com/FlameOfUdun/winche-sentinel) for access control.

---

## Features

- **Document storage** — Store arbitrary JSON documents with automatic metadata (id, version, timestamps)
- **Querying** — Filter with 19+ conditional operators, sort, limit, offset, and cursor-based pagination
- **Real-time subscriptions** — Subscribe to queries and receive live updates over WebSocket when documents change
- **Transactions** — ACID transactions with commit/rollback, idle timeout, and automatic cleanup
- **Aggregation pipelines** — MongoDB-style pipeline stages: `match`, `lookup`, `unwind`, `group`, `project`, `sort`, `limit`, `skip`
- **Batch operations** — Atomic commit of multiple operations in a single request
- **Sync mutations** — Conflict-free document mutations (`Set`, `Update`, `Delete`) via mutation batches
- **Access control** — Document-level access rules via WincheSentinel integration
- **PostgreSQL backend** — All data stored as JSONB; queries translated to native PostgreSQL SQL

---

## Architecture

```text
REST / WebSocket API Layer
        ↓
  Store (Business Logic)
        ↓
  SQL Translation (AST → PostgreSQL)
        ↓
  Core Models
        ↓
    PostgreSQL
```

| Project | Role |
| --- | --- |
| `WincheDatabase.Core` | Shared domain model (`Document`) |
| `WincheDatabase.AST` | Query language models and JSON deserializers |
| `WincheDatabase.SQL` | Translates AST queries into PostgreSQL SQL via Npgsql |
| `WincheDatabase.Store` | Business logic: CRUD, transactions, subscriptions, change notifications |
| `WincheDatabase.REST` | ASP.NET Core REST endpoints |
| `WincheDatabase.WS` | WebSocket message protocol, routing, and real-time event dispatch |

---

## Requirements

- .NET 10.0
- PostgreSQL (any recent version with JSONB support)

---

## Setup

The [nuget.config](nuget.config) in this repository already points to the correct feed — no additional configuration is needed.

### 1. Configure `appsettings.json`

Store options are read from the `WincheDatabase` configuration section:

```json
{
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

### 2. Register services

```csharp
builder.Services.AddWincheDatabaseDocumentStore(connectionString, builder.Configuration);

// Add REST API (optional)
builder.Services.AddWincheDatabaseRestApi();

// Add WebSocket API (optional)
builder.Services.AddWincheDatabaseWsApi();
```

### 3. Initialize schema and map endpoints

```csharp
// Ensures the documents table and indexes exist
app.UseWincheDatabaseDocumentStore();

// Map REST routes (default prefix: "documents")
app.UseWincheDatabaseRestApi();

// Map WebSocket route (default prefix: "documents", connects at /documents/ws)
app.UseWincheDatabaseWsApi();
```

See [Applications/WebApi](Applications/WebApi) for a complete working example.

---

## License

See [LICENSE](./LICENSE)
