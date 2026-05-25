# Winche.Database

A JSON document database layer built on top of PostgreSQL. Store, query, and subscribe to JSON documents using a structured query language — with PostgreSQL as the storage backend via JSONB.

Supports real-time subscriptions, ACID transactions, aggregation pipelines, and integrates with [Winche.Sentinel](https://github.com/FlameOfUdun/winche-sentinel) for access control.

## Install

```cmd
dotnet add package Winche.Database
```

Add the REST or WebSocket API integrations as needed:

```cmd
dotnet add package Winche.Database.AspNetCore.Rest
dotnet add package Winche.Database.AspNetCore.WebSockets
```

## Quick Start

### 1. Configure `appsettings.json`

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
builder.Services.AddWincheDatabase(connectionString, builder.Configuration);

// Add REST API (optional)
builder.Services.AddWincheDatabaseRestApi();

// Add WebSocket API (optional)
builder.Services.AddWincheDatabaseWsApi();
```

### 3. Initialize schema and map endpoints

```csharp
// Ensures the documents table and indexes exist
app.UseWincheDatabase();

// Map REST routes (default prefix: "documents")
app.UseWincheDatabaseRestApi();

// Map WebSocket route (connects at /documents/ws)
app.UseWincheDatabaseWsApi();
```

See [samples/WebApi](samples/WebApi) for a complete working example.

## Features

- **Document storage** — Store arbitrary JSON documents with automatic metadata (id, version, timestamps)
- **Querying** — Filter with 19+ conditional operators, sort, limit, offset, and cursor-based pagination
- **Real-time subscriptions** — Subscribe to queries and receive live updates over WebSocket when documents change
- **Transactions** — ACID transactions with commit/rollback, idle timeout, and automatic cleanup
- **Aggregation pipelines** — MongoDB-style pipeline stages: `match`, `lookup`, `unwind`, `group`, `project`, `sort`, `limit`, `skip`
- **Batch operations** — Atomic commit of multiple operations in a single request
- **Sync mutations** — Conflict-free document mutations (`Set`, `Update`, `Delete`) via mutation batches
- **Access control** — Document-level access rules via Winche.Sentinel integration
- **PostgreSQL backend** — All data stored as JSONB; queries translated to native PostgreSQL SQL

## Packages

| Package | Description |
| --- | --- |
| `Winche.Database` | Core document store: schema, CRUD, queries, transactions, subscriptions |
| `Winche.Database.AspNetCore.Rest` | ASP.NET Core REST endpoints |
| `Winche.Database.AspNetCore.WebSockets` | ASP.NET Core WebSocket protocol and real-time event dispatch |

## Requirements

- .NET 10.0
- PostgreSQL (any recent version with JSONB support)

## License

Elastic License 2.0
