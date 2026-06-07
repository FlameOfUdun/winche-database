# Release Notes — Winche.Database v3

> v3 is a **major breaking release**. Wire format, public .NET API, and REST/WS endpoints all changed. There is **no upgrade path from v2 data** — v2 stored untyped JSON, v3 stores typed-value JSONB; deployments start fresh (see "Data migration" below).

---

## Breaking Changes

### `StoreOptions.Schema` and `StoreOptions.TableName` removed

Both configuration properties have been removed. All database object names are now fixed:

| Object | Fixed name |
| - | - |
| Documents table | `winche_documents` |
| Changes feed table | `winche_changes` |
| Feed cursors table | `winche_feed_cursors` |
| Helper functions | `winche_rank`, `winche_num`, `winche_num2`, `winche_text`, `winche_bytes`, `winche_key`, `winche_f8key`, `winche_eskey`, `winche_notify_change` |

Non-`public` schemas are supported via the connection string's `Search Path` setting. Multi-store deployments isolate via schema-per-store. Remove `TableName` and `Schema` from your `appsettings.json` `WincheDatabase` section.

### Registration API: explicit options, conventional names

`AddWincheDatabase` no longer takes `IConfiguration` — configuration is explicit through a single options lambda, and `DependencyConfigurator` is merged into it:

```csharp
builder.Services.AddWincheDatabase(opts =>
{
    opts.ConnectionString = builder.Configuration.GetConnectionString("WincheDatabase")!;  // required
    opts.TransactionConfig = new() { IdleTimeoutSpan = TimeSpan.FromMinutes(1) };          // optional
    opts.AddDocumentAccessRule<MyRule>();
    opts.AddDocumentStoreHook<MyHook>();
    opts.AddIndexDefinition<MyIndex>();
    opts.SetCallerClaimsAccessor<MyAccessor>();
});
```

The library no longer reads any configuration section; bind from `IConfiguration` yourself if desired.

| v2 | v3 |
| - | - |
| `AddWincheDatabase(IConfiguration, Action<DependencyConfigurator>?)` | `AddWincheDatabase(Action<WincheDatabaseOptions>)` |
| `DependencyConfigurator` | `WincheDatabaseOptions` (also carries `ConnectionString`, `TransactionConfig`, `ChangeFeed`) |
| `StoreOptions` (`Winche.Database.Models`) | merged into `WincheDatabaseOptions` (`Winche.Database.DependencyInjection`); runtime components consume `IOptions<WincheDatabaseOptions>` |
| `app.UseWincheDatabase()` | `await app.InitializeWincheDatabaseAsync()` |
| `app.UseWincheDatabaseRestApi()` | `app.MapWincheDatabaseRestApi()` |
| `app.UseWincheDatabaseWsApi()` | `app.MapWincheDatabaseWsApi()` |

The `Map*` names follow ASP.NET Core convention (these methods register endpoints, not middleware).

### Wire format: tagged values

All field values now use the **tagged-value encoding** (`{"integerValue":"42"}`, `{"stringValue":"..."}`). The previous untyped JSON encoding is gone entirely. Every client that reads or writes documents must adopt the new format.

| Before (v2) | After (v3) |
| - | - |
| `{"age": 42}` | `{"age": {"integerValue": "42"}}` |
| `{"name": "Alice"}` | `{"name": {"stringValue": "Alice"}}` |
| `{"active": true}` | `{"active": {"booleanValue": true}}` |

Full type table: `nullValue`, `booleanValue`, `integerValue` (int64 as string), `doubleValue` (number or `"NaN"`/`"Infinity"`/`"-Infinity"`), `timestampValue` (RFC 3339 Z µs), `stringValue`, `bytesValue` (base64), `referenceValue`, `geoPointValue`, `arrayValue`, `mapValue`. See [PROTOCOL.md §1](PROTOCOL.md#1-values).

### WebSocket protocol v3 replaces v2 entirely

The v2 WS message vocabulary used colon discriminators (`system:ping`, `document:get`, `query:subscribe`, `transaction:begin`, etc.) — these are **gone**. The new protocol uses dot discriminators with a `hello`/`welcome` handshake, `doc.get`, `doc.getAll`, `query`, `aggregate`, `write`, `tx.begin`/`tx.get`/`tx.query`/`tx.commit`/`tx.rollback`, `listen`/`unlisten`. See [PROTOCOL.md §8](PROTOCOL.md#8-websocket-protocol-v3).

The first frame must be a `hello` with `"protocol": 3`. Any other value closes the socket with code `4400`.

### REST endpoint changes

| v2 | v3 |
| - | - |
| `POST /documents/query` | `POST /documents:runQuery` |
| `POST /documents/aggregate` | `POST /documents:aggregate` |
| `POST /documents/commit` | `POST /documents:commit` |
| `POST /documents/synchronize` | Removed — use `:commit` with preconditions |

New endpoints: `POST /documents:beginTransaction`, `POST /documents:rollback`, `POST /documents:batchGet`.

The `{prefix}` is configurable (default `documents`). Convenience `GET/PUT/PATCH/DELETE /{prefix}/{base64Path}` routes are unchanged.

### C# API: `IDocumentManager` → `IDocumentDatabase`

The `IDocumentManager` interface and its entire twin-method (protected/unprotected) API are removed. Inject `IDocumentDatabase` instead.

| v2 | v3 |
| - | - |
| `IDocumentManager` | `IDocumentDatabase` |
| `SetAsync(path, JsonObject)` | `WriteAsync([new SetWrite { Path, Fields }])` |
| `UpdateAsync(path, JsonObject)` | `WriteAsync([new UpdateWrite { Path, Fields }])` |
| `DeleteAsync(path)` | `WriteAsync([new DeleteWrite { Path }])` |
| `CommitAsync(OperationBatch)` | `WriteAsync(writes)` |
| `SyncAsync(MutationBatch)` | `WriteAsync` with `Precondition(UpdateTime: ...)` |
| `GetUnprotectedAsync / SetUnprotectedAsync / …` | Removed (guard is a decorator; core is always unguarded) |

Removed types: `IDocumentManager`, `ISubscriptionManager`, `ISubscriptionRegistry`, `IChangeProcessor`, `IEventChannel`, `ISubscriptionEventHandler`, `ITransactionManager`, `ITransactionRegistry`, `IHookInvocationDispatcher`, `HookInvocationDispatcher`, `HookInvocationProcessor`, `DocumentChange`, `QueryChange`, `QueryGroup` (old), `QuerySubscription`, `SubscriptionEvent`, `BatchOperation`, `OperationBatch`, `CommitResult`, `Mutation`, `MutationBatch`, `SyncResult`, `Transaction` (operand model).

The hook dispatch channel architecture (`HookInvocationDispatcher` / `HookInvocationProcessor` / `IHookInvocationDispatcher`) has been removed entirely. Hooks now execute inline and sequentially inside `HookFeedConsumer`, which is driven by `DurableConsumerRunner` with at-least-once retry semantics.

New public types: `IDocumentDatabase`, `WriteBatch`, `SetWrite`, `UpdateWrite`, `DeleteWrite`, `Precondition`, `FieldTransform`, `TransformKind`, `WriteResult`, `TransactionHandle`, `TransactionContext`, `IQueryListener`, `QuerySnapshot`, `DocumentChangeInfo`, `ListenChangeType`, `ListenOptions`.

---

## Behavior Changes

### Inequality filters: same-type-class only

Inequality operators (`gt`, `gte`, `lt`, `lte`) now match **only values in the same type-class** as the operand. Previously, mixed-type comparisons were attempted via implicit coercion. Now `score > {"integerValue":"5"}` will not match a `stringValue` field named `score`, even if it happens to compare as greater.

### `nullValue` strictness

`{"nullValue": x}` where `x` is **not JSON null** is now rejected with `INVALID_ARGUMENT`. Previously, non-null payloads were silently ignored.

### Regex operator: case-sensitive

The `regex` string operator is now always case-sensitive, matching Postgres `~` (POSIX regex). Previously, case sensitivity depended on the Postgres collation.

### `sum` / `avg` result typing

`sum` always returns `doubleValue` (even when all inputs are integers). `count` returns `integerValue`. `avg` always returns `doubleValue`; `avg` over an empty group or a field containing only non-numeric values returns `nullValue`. Previously, the numeric type of accumulator results was unspecified.

### Cascade delete: guard at root only

`DeleteWrite(Cascade: true)` now guards the access rule check against the **root path only** (the `path` in the write). Previously the guard was checked individually for each descendant document.

Cascade delete is **explicit opt-in** per write (`"cascade": true`). Previously, every delete was automatically a cascade. Non-cascade deletes now delete exactly one document.

### Dot-keyed `UpdateWrite` fields are parsed as field paths

`UpdateWrite.Fields` keys are now strictly parsed as dotted field paths. A key like `"address.city"` traverses `address → city`. If an intermediate segment is a scalar (not a map) in the existing document, the scalar is **replaced** with a nested map and traversal continues — the path is applied, not rejected.

Previously, dot notation in update keys was not consistently parsed.

### Nested `deleteField` now allowed in merge-sets

`{"deleteField": true}` is now legal at any map depth inside `SetWrite(Merge: true).Fields`. It was previously only accepted at the top level of `UpdateWrite` field keys. See [PROTOCOL.md §3.6](PROTOCOL.md#36-deletefield-sentinel).

### Listener wire: delta protocol

The listener subscription model changed from an ids-only internal event model to a full delta wire protocol (`listen.snapshot` / `listen.delta` with indexed `changes` array and `count` checksum). Existing subscription client code must be rewritten against the new WS protocol.

`listen.snapshot` **replaces** the client's local list. `listen.delta` **mutates** it by index. The `count` field is a checksum; on mismatch, the client should re-subscribe.

### Hooks: transport + idempotency

Hooks are now invoked via the durable **change feed** (`changes` table), not inline in the write path. Delivery is:

- **True at-least-once end-to-end:** hooks execute inline and sequentially inside `HookFeedConsumer`. A hook that throws causes `DurableConsumerRunner` to retry the **entire batch** with capped backoff (1 s → 30 s). The cursor advances only after all hooks in a batch succeed.
- **Cross-node** (hooks fire from any node reading the feed, including for writes from remote nodes).
- **After commit** (not inline; hooks cannot veto writes).
- **A failing hook blocks only the hooks consumer** — it does not affect listeners or other consumers.

Hook implementations **must be idempotent** — the same batch can be delivered more than once. Use the document `version` as an idempotency key.

### REST `PUT` performs a read after write

`PUT /{prefix}/{base64Path}` now returns the document as it exists in the database after the write (guard read after write). Previously it returned the submitted payload directly. The extra read is the source of truth.

### REST `DELETE` requires a read

`DELETE /{prefix}/{base64Path}` now reads the document first and returns 404 if it is already missing. Previously (v2), deleting a non-existent document returned 404 as well, but without the explicit read-first — v3 makes this behaviour explicit and also makes cascade opt-in (`"cascade": true`) rather than automatic.

### REST error contract changed (breaking for v2 REST clients)

The v2 REST API returned error bodies in a `{"error": ...}` envelope and only mapped 403 and 500 responses. v3 changes this entirely:

- Error body shape: `{"status": "<STATUS>", "message": "<text>", "details": <object|null>}`
- Full status → HTTP code mapping (NOT_FOUND → 404, ALREADY_EXISTS/ABORTED → 409, FAILED_PRECONDITION → 412, PERMISSION_DENIED → 403, UNAUTHENTICATED → 401, DEADLINE_EXCEEDED → 504, INTERNAL → 500, INVALID_ARGUMENT/INVALID_QUERY/body binding errors → 400)

**This is breaking for all v2 REST clients** — they must update their error parsing.

---

## No Data Migration Required

v3 stores typed-value JSONB natively. v2 stored untyped JSON — the formats are incompatible at the storage level. v3 deployments **start fresh** from an empty schema; there is no upgrade path from v2 data. The claim that "only the wire changes" is incorrect — the `data` column format changed entirely.
