# Winche.Database v3 — Wire Protocol Reference

**Version:** 3 | **Last updated:** 2026-06-07

> This document is the authoritative reference for all v3 wire formats: typed values, documents, write operations, queries, aggregation pipelines, error codes, the REST API, and the WebSocket protocol. Every JSON example is a shape verified against the parser source and integration tests.

---

## Table of Contents

1. [Values](#1-values)
2. [Documents & Paths](#2-documents--paths)
3. [Writes](#3-writes)
4. [Queries](#4-queries)
5. [Pipelines](#5-pipelines)
6. [Errors](#6-errors)
7. [REST API](#7-rest-api)
8. [WebSocket Protocol v3](#8-websocket-protocol-v3)
9. [Operational Notes](#9-operational-notes)

---

## 1. Values

All field values use a **tagged-value** encoding. Each value is a JSON object with exactly one type-discriminator key. The parser (`ValueSerializer`) rejects objects with zero or more than one key.

### 1.1 Type table

| C# type | Wire key | Payload | Notes |
| - | - | - | - |
| `NullValue` | `nullValue` | `null` (JSON null) | Payload **must** be JSON null — any other JSON value is `INVALID_ARGUMENT` |
| `BooleanValue` | `booleanValue` | JSON boolean | |
| `IntegerValue` | `integerValue` | string or JSON number (int64) | Prefer string form for large values; both accepted |
| `DoubleValue` | `doubleValue` | JSON number **or** one of the strings `"NaN"`, `"Infinity"`, `"-Infinity"` | |
| `TimestampValue` | `timestampValue` | RFC 3339 string, UTC (`Z`), microsecond precision (`yyyy-MM-ddTHH:mm:ss.ffffffZ`) | Stored truncated to µs (sub-µs ticks dropped) |
| `StringValue` | `stringValue` | JSON string | Unicode; code-point order for sorting |
| `BytesValue` | `bytesValue` | standard base64 string | |
| `ReferenceValue` | `referenceValue` | document path string (e.g. `"users/u1"`) | |
| `GeoPointValue` | `geoPointValue` | `{"latitude": <number>, "longitude": <number>}` | |
| `ArrayValue` | `arrayValue` | `{"values": [<value>, …]}` | `values` key may be absent for an empty array |
| `MapValue` | `mapValue` | `{"fields": {"<key>": <value>, …}}` | `fields` key may be absent for an empty map |

### 1.2 Wire examples

```json
{"nullValue": null}
{"booleanValue": true}
{"integerValue": "9007199254740993"}
{"doubleValue": 1.5}
{"doubleValue": "NaN"}
{"doubleValue": "Infinity"}
{"doubleValue": "-Infinity"}
{"timestampValue": "2026-06-07T12:34:56.000000Z"}
{"stringValue": "hello"}
{"bytesValue": "SGVsbG8="}
{"referenceValue": "users/u1"}
{"geoPointValue": {"latitude": 59.913, "longitude": 10.752}}
{"arrayValue": {"values": [{"integerValue": "1"}, {"integerValue": "2"}]}}
{"arrayValue": {}}
{"mapValue": {"fields": {"city": {"stringValue": "Oslo"}}}}
{"mapValue": {}}
```

### 1.3 Cross-type ordering (TypeRank)

The engine uses a total order across all types. The numeric rank values are:

| TypeRank | Rank | Notes |
| - | - | - |
| Null | 10 | |
| Boolean | 20 | |
| NaN | 29 | NaN sorts **before** all finite numbers |
| Number | 30 | `IntegerValue` and `DoubleValue` share this rank |
| Timestamp | 40 | |
| String | 50 | Unicode code-point order (`COLLATE "C"` in Postgres) |
| Bytes | 60 | Lexicographic byte order |
| Reference | 70 | Whole-path Unicode code-point (byte) comparison — **not** per-segment; deliberately diverges from Firestore for ids containing chars < `'/'` (U+002F) |
| GeoPoint | 80 | Latitude first, then longitude |
| Array | 90 | Element-wise; shorter prefix array sorts first |
| Map | 100 | Keys sorted by code-point order; compared **interleaved** (key₁, value₁, key₂, value₂, …); shorter map wins on a prefix tie. Example: `{"a":{"integerValue":"1"},"b":{"integerValue":"0"}}` < `{"a":{"integerValue":"2"}}` — key `"a"` ties, value `1 < 2` decides |

### 1.4 Numeric equality and special values

- `IntegerValue(5)` and `DoubleValue(5.0)` are considered **equal** in comparisons and in array/set membership tests. They share TypeRank `Number`.
- `-0.0 == 0.0` in scalar comparisons (IEEE 754 negative zero is treated as zero). **Note:** inside composite `winche_key` encodings used for array and map ordering, SQL orders −0.0 < +0.0.
- `NaN` has TypeRank 29 (before other numbers). Two NaN values compare equal in typed equality; NaN is the smallest in max/min transforms.
- Inequality filters (`gt`, `gte`, `lt`, `lte`) match only values in the **same type-class** as the operand. A filter `age > {"integerValue":"5"}` matches integer and double fields only — not strings or timestamps named `age`.

---

## 2. Documents & Paths

### 2.1 Document wire shape

```json
{
  "path":       "users/u1",
  "id":         "u1",
  "collection": "users",
  "fields": {
    "name":  {"stringValue": "Alice"},
    "score": {"integerValue": "42"},
    "meta":  {"mapValue": {"fields": {"active": {"booleanValue": true}}}}
  },
  "createTime": "2026-06-07T10:00:00+00:00",
  "updateTime": "2026-06-07T10:05:00+00:00",
  "version":    3
}
```

`fields` is a flat object whose values are tagged values. Nested structures use `mapValue`.

> **Timestamp serialization:** metadata timestamps (`createTime`, `updateTime`, `readTime`, and `writeResults[].updateTime`) serialize as ISO 8601 with `+00:00` offset and trailing fractional zeros trimmed (e.g. `1970-01-01T00:00:00+00:00`, `2026-06-07T10:05:00.001+00:00`). Only **tagged** `{"timestampValue":"..."}` payloads use the fixed microsecond UTC form (`yyyy-MM-ddTHH:mm:ss.ffffffZ`).

### 2.2 Path rules

- A **document path** is a `/`-separated string with an **even** number of non-empty segments: `collection/id` (2), `col/id/subcol/id2` (4), etc.
- A **collection path** has an **odd** number of segments (used internally; cascade delete accepts an odd-segment path).
- Segments must be non-empty. Leading/trailing slashes are not accepted.

### 2.3 Field paths

A `FieldPath` is a dot-separated sequence of one or more non-empty segments, e.g. `address.city`.

| Context | Interpretation |
| - | - |
| `UpdateWrite.Fields` keys | Each key is a dotted **path** — traverses into nested maps |
| `SetWrite.Fields` keys | Literal map **key** — never dot-parsed |
| Query `field` / `orderBy[].field` | Dotted path — traverses into nested maps |

The accessor for `address.city` in storage is `data->'address'->'mapValue'->'fields'->'city'`; the `FieldPath` type is the single owner of this translation.

The special pseudo-field `__name__` refers to the document path; it is valid in `orderBy` as a tiebreaker and in cursor values.

---

## 3. Writes

### 3.1 Batch structure

Every mutation goes through a `writes[]` array. REST uses `:commit`; WS uses `write`. A batch is **atomic** — it commits at a single timestamp or not at all.

- Minimum: 1 write. Maximum: **500** writes (`INVALID_ARGUMENT` beyond that).
- Each write is a JSON object with exactly **one** of the shape keys `set`, `update`, or `delete`. Having zero or more than one is `INVALID_ARGUMENT`.

### 3.2 Set write

```json
{
  "set": {
    "path":        "users/u1",
    "fields":      {"name": {"stringValue": "Alice"}},
    "merge":       false,
    "transforms":  [],
    "precondition": {"exists": false}
  }
}
```

| Field | Type | Description |
| - | - | - |
| `path` | string | Required. Document path (even segments). |
| `fields` | object | Required. Map of literal key → tagged value. |
| `merge` | boolean | Default `false`. When `true`: deep-merge into existing document instead of replacing it. |
| `transforms` | array | Optional. See [§3.4 Transforms](#37-transforms). |
| `precondition` | object | Optional. See [§3.5 Preconditions](#35-preconditions). |

**Merge semantics:** When `merge: true` the write recurses through `MapValue` fields and merges them. Top-level keys in `fields` that are `mapValue`s are merged recursively. Non-map existing fields at the same key are replaced. The `deleteField` sentinel is legal as a map value at any depth inside a merge-set (see [§3.6 deleteField](#36-deletefield-sentinel)).

### 3.3 Update write

```json
{
  "update": {
    "path":   "users/u1",
    "fields": {
      "address.city":  {"stringValue": "Oslo"},
      "address.old":   {"deleteField": true}
    },
    "transforms":  [],
    "precondition": {"updateTime": "2026-06-07T10:05:00+00:00"}
  }
}
```

| Field | Type | Description |
| - | - | - |
| `path` | string | Required. |
| `fields` | object | Required. Keys are **dotted field paths** (traversal). Values are tagged values or `{"deleteField": true}`. |
| `transforms` | array | Optional. |
| `precondition` | object | Optional. `UpdateWrite` always has an implicit `exists: true` precondition in addition to any explicit one. |

The update creates or patches individual nested fields without disturbing siblings. An `UpdateWrite` on a missing document yields `NOT_FOUND`.

### 3.4 Delete write

```json
{
  "delete": {
    "path":        "users/u1",
    "cascade":     false,
    "precondition": {"exists": true}
  }
}
```

| Field | Type | Description |
| - | - | - |
| `path` | string | Required. |
| `cascade` | boolean | Default `false`. When `true`, also deletes all documents nested under this path (Winche extension; explicit opt-in). |
| `precondition` | object | Optional. |

### 3.5 Preconditions

```json
{"exists": true}
{"exists": false}
{"updateTime": "2026-06-07T10:05:00+00:00"}
{"exists": true, "updateTime": "2026-06-07T10:05:00+00:00"}
```

At least one of `exists` or `updateTime` must be set (an object with neither is `INVALID_ARGUMENT`).

| Check | Failure |
| - | - |
| `exists: true` on missing doc | `NOT_FOUND` |
| `exists: false` on existing doc | `ALREADY_EXISTS` |
| `updateTime` mismatch (µs-exact) | `FAILED_PRECONDITION` |

Precondition failures abort the **entire** batch — nothing is applied.

### 3.6 deleteField sentinel

`{"deleteField": true}` is a write-time sentinel that removes a field.

| Context | Allowed |
| - | - |
| `UpdateWrite.Fields` values (any depth via dotted path keys) | Yes |
| `SetWrite(merge: true).Fields` values — top-level or inside `mapValue` at any depth | Yes |
| `SetWrite(merge: false).Fields` values | No — `INVALID_ARGUMENT` |
| Inside any `ArrayValue` (at any depth) | No — `INVALID_ARGUMENT` |
| In transform operands | No — `INVALID_ARGUMENT` |

**Mask semantics:** The sentinel removes the leaf field. If removing a field leaves a map empty, that **empty map is preserved** (no phantom removal of parent maps). A scalar parent referenced via a dotted-path key is left untouched by the sentinel — only the exact target field is affected.

**Nested map example (merge-set):**

```json
{
  "set": {
    "path": "c/a",
    "merge": true,
    "fields": {
      "m": {
        "mapValue": {
          "fields": {
            "drop": {"deleteField": true},
            "keep": {"integerValue": "1"}
          }
        }
      }
    }
  }
}
```

After applying: `m.drop` is removed; `m.keep` has value `1`; any other existing fields under `m` are untouched.

### 3.7 Transforms

A transform is applied to a single field **after** the write data has been applied. Multiple transforms in one write may not target the same field.

```json
[
  {"field": "count",  "kind": "increment",       "operand": {"integerValue": "1"}},
  {"field": "high",   "kind": "maximum",          "operand": {"doubleValue": 99.5}},
  {"field": "low",    "kind": "minimum",          "operand": {"integerValue": "0"}},
  {"field": "tags",   "kind": "arrayUnion",       "operand": {"arrayValue": {"values": [{"stringValue": "new"}]}}},
  {"field": "old",    "kind": "arrayRemove",      "operand": {"arrayValue": {"values": [{"stringValue": "x"}]}}},
  {"field": "time",   "kind": "serverTimestamp"}
]
```

| Kind | Operand required | Semantics |
| - | - | - |
| `serverTimestamp` | None | Sets the field to the batch commit time (a `TimestampValue`). |
| `increment` | Numeric (`integerValue` or `doubleValue`) | `int + int → integer` (saturating at `long` bounds); any double involved → `double`. Non-number or missing existing → operand. |
| `maximum` | Numeric | Keeps the larger value (numeric comparison). NaN is the smallest number. Non-number/missing → operand. |
| `minimum` | Numeric | Keeps the smaller value. Non-number/missing → operand. |
| `arrayUnion` | `arrayValue` | Appends operand elements not already typed-equal-present. Operand itself is deduplicated. Non-array existing → operand array. |
| `arrayRemove` | `arrayValue` | Removes all typed-equal elements. Non-array existing → empty array. |

Transform results are returned in `WriteResult.transformResults` keyed by field path.

### 3.8 Write results

Each write produces one `WriteResult` in the response:

```json
{
  "writeResults": [
    {"updateTime": "2026-06-07T12:34:56.000001+00:00", "transformResults": null},
    {"updateTime": "2026-06-07T12:34:56.000001+00:00",
     "transformResults": {"count": {"integerValue": "3"}}}
  ]
}
```

All writes in a batch share the same `updateTime` (single commit timestamp). `transformResults` is always present: `null` when the write had no transforms, or an object keyed by field path when transforms were applied.

---

## 4. Queries

### 4.1 QueryAst wire shape

```json
{
  "collection": "users",
  "where": { ... },
  "orderBy": [ ... ],
  "limit": 25,
  "start": {"values": [...], "before": true},
  "end":   {"values": [...], "before": false}
}
```

| Field | Type | Required | Description |
| - | - | - | - |
| `collection` | string | Yes | Non-empty collection name. |
| `where` | object | No | Filter — see §4.2. |
| `orderBy` | array | No | Sort keys — see §4.3. |
| `limit` | integer | No | Maximum documents to return. Default **100** when omitted. |
| `start` | cursor | No | Lower bound cursor — see §4.4. |
| `end` | cursor | No | Upper bound cursor. |

Query response:

```json
{"documents": [...], "hasMore": false}
```

### 4.2 Filters

Each filter is a JSON object with exactly one recognized shape key.

**Field filter** — compare a field to a value:

```json
{"field": "age", "op": "gte", "value": {"integerValue": "18"}}
```

**Composite filter** — logical combination:

```json
{"and": [
  {"field": "status", "op": "eq", "value": {"stringValue": "active"}},
  {"field": "score",  "op": "gt", "value": {"integerValue": "100"}}
]}
```

```json
{"or": [ ... ]}
{"not": {"field": "deleted", "op": "eq", "value": {"booleanValue": true}}}
```

**Unary filter** — field state checks:

```json
{"unary": "isNull",  "field": "nickname"}
{"unary": "isNan",   "field": "ratio"}
{"unary": "exists",  "field": "email"}
```

**Field-compare filter** (Winche extension) — compare two fields:

```json
{"compare": {"left": "start", "op": "lt", "right": "end"}}
```

### 4.3 Filter operators

| Wire string | Applies to | Description |
| - | - | - |
| `eq` | Any | Typed equality (int 5 == double 5.0) |
| `ne` | Any | Typed inequality |
| `gt` | Same type-class only | Greater than |
| `gte` | Same type-class only | Greater than or equal |
| `lt` | Same type-class only | Less than |
| `lte` | Same type-class only | Less than or equal |
| `in` | Any | Field value is in operand array |
| `notIn` | Any | Field value is not in operand array |
| `arrayContains` | Array fields | Array field contains the operand value |
| `arrayContainsAny` | Array fields | Array field contains any element of the operand array |
| `arrayContainsAll` | Array fields | Array field contains all elements of the operand array (Winche extension) |
| `contains` | String fields | Substring match (case-sensitive) |
| `startsWith` | String fields | Prefix match (case-sensitive) |
| `endsWith` | String fields | Suffix match (case-sensitive) |
| `regex` | String fields | Regular expression match (case-sensitive) |

Inequality operators (`gt`, `gte`, `lt`, `lte`) are **same-type-class only**: the filter matches only values whose TypeRank equals the operand's TypeRank (Number, String, Timestamp, etc.). A numeric filter will never match a string field, even if the field name is the same.

Unary operators: `isNull`, `isNan`, `exists`.

### 4.4 orderBy and tiebreaker

```json
"orderBy": [
  {"field": "score", "direction": "desc"},
  {"field": "name",  "direction": "asc"}
]
```

`direction` is `"asc"` (default, may be omitted) or `"desc"`.

The engine automatically appends `__name__` (the document path) as a tiebreaker with the direction of the **last explicit sort key**. This ensures a stable total order. Cursor values must include the tiebreaker value when using multi-key sorts.

> **Implicit `exists` filter:** for every field named in `orderBy`, the engine adds an implicit `{"unary":"exists","field":"<fieldName>"}` filter. Documents missing any ordered field are excluded from the result set.

### 4.5 Cursors

```json
"start": {"values": [{"integerValue": "100"}, {"stringValue": "users/u5"}], "before": true},
"end":   {"values": [{"integerValue": "50"}],                                "before": false}
```

| Combination | Semantic |
| - | - |
| `before: true` | `startAt` — inclusive lower bound |
| `before: false` | `startAfter` — exclusive lower bound |
| `end.before: true` | `endBefore` — exclusive upper bound |
| `end.before: false` | `endAt` — inclusive upper bound |

`values` must be tagged values corresponding positionally to `orderBy` fields (including the implicit `__name__` tiebreaker if it is needed). Cursor arity may be any **prefix** of the sort keys (1 to N values); the remaining sort keys are unconstrained.

---

## 5. Pipelines

Pipelines are an array of stage objects. Each stage object has exactly **one** of the stage keys. The pipeline JSON wrapper key is `"pipeline"`.

### 5.1 Pipeline request shape

```json
{
  "pipeline": [
    {"match": {"collection": "orders", "where": { ... }}},
    {"group": {"keys": [...], "accumulators": [...]}}
  ]
}
```

Pipeline response:

```json
{"rows": [ {"total": {"integerValue": "42"}} ]}
```

### 5.2 Stage reference

| Stage key | Body shape | Description |
| - | - | - |
| `match` | `{"collection": "col", "where": {…}}` | Initial collection scan with optional filter. `where` is a full filter object (§4.2). |
| `filter` | A filter object (§4.2) | Post-stage filter, like MongoDB `$match` without a collection. |
| `lookup` | See §5.3 | Left join from a related collection. |
| `unwind` | `{"field": "tags", "as": "tag", "preserveNullAndEmpty": false}` | Deconstructs an array field into one row per element. |
| `group` | `{"keys": [...], "accumulators": [...], "having": {…}}` | Aggregation. See §5.4. |
| `project` | `{"fields": [...]}` | Reshape output. See §5.5. |
| `sort` | `[{"field": "score", "direction": "desc"}]` | Order rows. |
| `limit` | integer | Maximum output rows. |
| `skip` | integer | Skip the first N rows. |

### 5.3 Lookup stage

```json
{
  "lookup": {
    "collection":   "products",
    "localField":   "productId",
    "foreignField": "id",
    "as":           "product",
    "where":        { ... },
    "orderBy":      [{"field": "name", "direction": "asc"}],
    "limit":        100
  }
}
```

`limit` defaults to 100. `where` and `orderBy` are optional additional filters/sorts on the joined collection.

### 5.4 Group stage

```json
{
  "group": {
    "keys": [
      {"as": "status", "field": "status"}
    ],
    "accumulators": [
      {"as": "total", "fn": "sum",   "field": "amount"},
      {"as": "n",     "fn": "count"}
    ],
    "having": {"field": "total", "op": "gt", "value": {"integerValue": "0"}}
  }
}
```

`keys` may be an empty array for a global aggregation. `having` is an optional post-group filter. `field` is required for all accumulator functions except `count`.

**Accumulator functions:**

| Wire string | Description |
| - | - |
| `count` | Number of rows in the group |
| `sum` | Sum of numeric field values |
| `avg` | Average of numeric field values |
| `min` | Minimum value in the group |
| `max` | Maximum value in the group |
| `push` | Array of all values |
| `addToSet` | Array of unique values |
| `first` | First value in group order |
| `last` | Last value in group order |

### 5.5 Project stage

```json
{
  "project": {
    "fields": [
      {"as": "name",  "field": "user.name"},
      {"as": "zero",  "value": {"integerValue": "0"}},
      {"as": "total", "fn": "sum", "field": "amount"}
    ]
  }
}
```

Each projection is exactly one of:

- `{"as": "...", "field": "..."}` — field reference (dotted path)
- `{"as": "...", "value": <tagged-value>}` — literal value
- `{"as": "...", "fn": "<aggFunction>", "field": "..."}` — aggregate expression (for use in post-group projection)

---

## 6. Errors

### 6.1 Error status vocabulary

All errors — WS `error` frames and REST error bodies — use this status vocabulary:

| Status | Description |
| - | - |
| `INVALID_ARGUMENT` | Malformed request, bad field types, batch size exceeded, write shape invalid |
| `INVALID_QUERY` | Query or pipeline parse error (surfaces on **both** REST 400 and WS error frame; `details.jsonPath` for JSON parse errors, `details.code` for plan-validation failures) |
| `NOT_FOUND` | `UpdateWrite`/`exists:true` precondition on a missing document |
| `ALREADY_EXISTS` | `exists:false` precondition on an existing document |
| `FAILED_PRECONDITION` | `updateTime` precondition mismatch |
| `ABORTED` | Transaction conflict, expired transaction, or unknown transaction id |
| `PERMISSION_DENIED` | Access rule denied the operation |
| `UNAUTHENTICATED` | Authentication token invalid or missing |
| `DEADLINE_EXCEEDED` | Operation timed out |
| `INTERNAL` | Unexpected server error (always a bug; report it) |

### 6.2 REST error body

```json
{"status": "NOT_FOUND", "message": "Document 'users/u99' does not exist.", "details": null}
```

`details` may be null. For `INVALID_QUERY` parse errors (REST and WS) it contains `jsonPath`:

```json
{"status": "INVALID_QUERY", "message": "Unknown operator", "details": {"jsonPath": "$.where.op"}}
```

For `INVALID_QUERY` plan-validation failures it contains `code`:

```json
{"status": "INVALID_QUERY", "message": "orderBy field missing from where clause", "details": {"code": "ORDERBY_FIELD_NOT_FILTERED"}}
```

### 6.3 REST status → HTTP code mapping

| Status | HTTP |
| - | - |
| `NOT_FOUND` | 404 |
| `ALREADY_EXISTS`, `ABORTED` | 409 |
| `FAILED_PRECONDITION` | 412 |
| `PERMISSION_DENIED` | 403 |
| `UNAUTHENTICATED` | 401 |
| `DEADLINE_EXCEEDED` | 504 |
| `INTERNAL` | 500 |
| `INVALID_ARGUMENT`, `INVALID_QUERY`, body binding errors | 400 |

---

## 7. REST API

The REST API is mounted at a configurable prefix (default `documents`). All paths below use `documents` as the prefix.

### 7.1 Convenience endpoints

Document paths in URL segments are **Base64-encoded** (standard, UTF-8 bytes) to avoid routing conflicts with slashes.

| Method | Route | Request body | Response | Description |
| - | - | - | - | - |
| `GET` | `/documents/{base64Path}` | — | `Document` or 404 | Get a document |
| `PUT` | `/documents/{base64Path}` | `{"fields": {…}}` | `Document` | Create or replace (set without merge). Performs a read after write to return the current document. |
| `PATCH` | `/documents/{base64Path}` | `{"fields": {…}}` | `Document` or 404 | Patch fields (update). Field keys are dotted paths. |
| `DELETE` | `/documents/{base64Path}` | — | 204 or 404 | Delete (cascade). Returns 404 if missing (performs read first). |
| `GET` | `/documents/ping` | — | 200 | Health check |

### 7.2 Colon-verb endpoints

These endpoints use literal-colon URL segments. They receive the built-in claims and error filters but live outside the `/{prefix}` group — `configure` customizations apply only to the `/{prefix}` group. Use the `configureVerbs` parameter to apply customizations to the colon-verb routes.

| Endpoint | Request body | Response | Description |
| - | - | - | - |
| `POST /documents:commit` | `{"writes": [...], "transaction"?: "id"}` | `{"writeResults": [...]}` | Atomic write batch. With `transaction`: commits the transaction. |
| `POST /documents:beginTransaction` | `{}` | `{"transaction": "id"}` | Begin an optimistic transaction. |
| `POST /documents:rollback` | `{"transaction": "id"}` | `{}` | Roll back (idempotent; unknown id is a no-op). |
| `POST /documents:batchGet` | `{"documents": ["path1","path2",...], "transaction"?: "id"}` | `{"documents": [Document\|null, ...]}` | Bulk read preserving input order; missing docs are `null`. |
| `POST /documents:runQuery` | `{"query": <QueryAst>, "transaction"?: "id"}` | `{"documents": [...], "hasMore": false}` | Execute a query. Default `limit` = 100 when omitted. |
| `POST /documents:aggregate` | `{"pipeline": <PipelineAst>}` | `{"rows": [...]}` | Execute an aggregation pipeline. Requires the `Aggregate` access operation on every `match`/`lookup` collection — distinct from `Read`, deny-by-default — else `PERMISSION_DENIED`. |

### 7.3 Request/response examples

**:commit (batch)**

```json
POST /documents:commit
{
  "writes": [
    {"set": {"path": "users/u1", "fields": {"name": {"stringValue": "Alice"}}}},
    {"set": {"path": "counters/c1", "fields": {"n": {"integerValue": "0"}},
             "transforms": [{"field": "n", "kind": "increment", "operand": {"integerValue": "1"}}]}}
  ]
}

200 OK
{
  "writeResults": [
    {"updateTime": "2026-06-07T12:00:00.000001+00:00", "transformResults": null},
    {"updateTime": "2026-06-07T12:00:00.000001+00:00",
     "transformResults": {"n": {"integerValue": "1"}}}
  ]
}
```

**:beginTransaction + :batchGet + :commit**

```json
POST /documents:beginTransaction
{}

200 OK
{"transaction": "6a3f2e..."}

POST /documents:batchGet
{"documents": ["users/u1"], "transaction": "6a3f2e..."}

200 OK
{"documents": [{"path": "users/u1", "fields": {...}, ...}]}

POST /documents:commit
{"writes": [{"set": {"path": "users/u1", "fields": {"score": {"integerValue": "99"}}}}],
 "transaction": "6a3f2e..."}

200 OK
{"writeResults": [{"updateTime": "2026-06-07T12:00:01+00:00", "transformResults": null}]}
```

**:runQuery**

```json
POST /documents:runQuery
{
  "query": {
    "collection": "users",
    "where": {"field": "score", "op": "gte", "value": {"integerValue": "50"}},
    "orderBy": [{"field": "score", "direction": "desc"}],
    "limit": 10
  }
}

200 OK
{"documents": [...], "hasMore": false}
```

**:aggregate**

```json
POST /documents:aggregate
{
  "pipeline": {
    "pipeline": [
      {"match": {"collection": "orders"}},
      {"group": {
        "keys": [{"as": "status", "field": "status"}],
        "accumulators": [{"as": "total", "fn": "sum", "field": "amount"}]
      }}
    ]
  }
}

200 OK
{"rows": [{"status": {"stringValue": "paid"}, "total": {"doubleValue": 1234.5}}]}
```

### 7.4 Sticky-transaction caveat

> **IMPORTANT — Multi-node deployments**
>
> Transaction state lives in the **serving node's in-memory ledger**. In a multi-node (horizontally scaled) deployment, all requests belonging to the same transaction must be routed to the **same node** (sticky sessions, consistent hashing, or similar) for the duration of the transaction — from `:beginTransaction` through `:commit` or `:rollback`.
>
> If a request in an active transaction is routed to a different node, the transaction id will be unknown to that node and the operation will return `ABORTED`. No data corruption occurs — an `ABORTED` response is always safe to retry with a new transaction.
>
> For use cases requiring transaction-safe multi-node operation without sticky routing, use the **WebSocket API** instead: a WS connection is pinned to one server for its lifetime, so the transaction is always on the correct node.

---

## 8. WebSocket Protocol v3

### 8.1 Connection and handshake

Connect via standard WebSocket to `/{prefix}/ws` (default `/documents/ws`).

**Handshake sequence:**

1. The **first frame** the client sends must be a `hello` message, within **10 seconds**. Missing the deadline closes the socket with code `4408`.
2. The server authenticates the token (if any) and replies with `welcome` on success or `error` + close `4401` on authentication failure.
3. An unsupported `protocol` value sends `error` + close `4400`. A non-`hello` first frame also closes `4400`.
4. After `welcome`, the connection is fully open for operations.

```json
Client: {"type": "hello", "protocol": 3, "token": "<optional-jwt>"}
Server: {"type": "welcome", "connectionId": "a1b2c3...", "protocol": 3}
```

On failure:

```json
Server: {"type": "error", "status": "UNAUTHENTICATED", "message": "Token rejected."}
```

(socket then closes with code `4401`)

### 8.2 Close codes

| Code | Meaning |
| - | - |
| `4400` | Protocol violation (wrong protocol version, non-`hello` first frame, binary frame) |
| `4401` | Authentication failed |
| `4408` | Hello timeout (10 s default; configurable via `WsOptions.HelloTimeout`) |
| `4413` | Incoming frame too large (default 1 MiB; configurable via `WsOptions.MaxFrameBytes`) |
| `1013` | Send queue overflow — server could not drain outbound frames (queue limit 64; configurable via `WsOptions.SendQueueLimit`) |
| `1000` | Normal closure (server-initiated clean shutdown) |

### 8.3 Envelope and correlation

Every **client** request carries a client-chosen `id`:

```json
{"type": "doc.get", "id": "req-42", "path": "users/u1"}
```

Every operation produces exactly one terminal server frame: either a `response` or an `error`. Both carry the same `id`:

```json
{"type": "response", "id": "req-42", "result": {"document": {...}}}
{"type": "error",    "id": "req-42", "status": "NOT_FOUND", "message": "..."}
```

Listener events are the only server frames that are **not** responses; they carry `subscriptionId` instead of `id`.

Inbound processing is **serial per connection** — the server awaits each handler before reading the next frame. Responses and listener events interleave through the single-writer send channel.

Binary frames are rejected with close code `4400`. Malformed JSON mid-connection yields an `error` frame; the connection remains open.

### 8.4 Operations

#### Reads

```json
Client: {"type": "doc.get", "id": "1", "path": "users/u1"}
Server: {"type": "response", "id": "1", "result": {"document": {…} | null}}

Client: {"type": "doc.getAll", "id": "2", "paths": ["users/u1", "users/u2"]}
Server: {"type": "response", "id": "2", "result": {"documents": [{…}, null]}}

Client: {"type": "query", "id": "3",
         "query": {"collection": "users", "where": {"field": "score", "op": "gt", "value": {"integerValue": "0"}}}}
Server: {"type": "response", "id": "3", "result": {"documents": [...], "hasMore": false}}

Client: {"type": "aggregate", "id": "4",
         "pipeline": {"pipeline": [{"match": {"collection": "users"}},
                                   {"group": {"keys": [], "accumulators": [{"as": "n", "fn": "count"}]}}]}}
Server: {"type": "response", "id": "4", "result": {"rows": [{"n": {"integerValue": "5"}}]}}
```

#### Writes

```json
Client: {
  "type": "write",
  "id": "5",
  "writes": [
    {"set": {"path": "users/u1", "fields": {"name": {"stringValue": "Alice"}}}},
    {"update": {"path": "users/u2", "fields": {"active": {"booleanValue": false}}}},
    {"delete": {"path": "users/u3"}}
  ]
}
Server: {"type": "response", "id": "5", "result": {"writeResults": [
  {"updateTime": "2026-06-07T12:00:00+00:00", "transformResults": null},
  {"updateTime": "2026-06-07T12:00:00+00:00", "transformResults": null},
  {"updateTime": "2026-06-07T12:00:00+00:00", "transformResults": null}
]}}
```

#### Keepalive

```json
Client: {"type": "ping", "id": "6"}
Server: {"type": "response", "id": "6", "result": {}}
```

#### Auth refresh

```json
Client: {"type": "auth.refresh", "id": "7", "token": "<new-jwt>"}
Server: {"type": "response", "id": "7", "result": {}}
```

After a successful `auth.refresh`, subsequent listener deliveries and access-rule evaluations use the new claims. Listener subscriptions that depended on the old claims will produce `removed` deltas for newly-invisible documents.

A failed refresh returns an `error` frame with status `UNAUTHENTICATED`; the connection's claims remain unchanged.

### 8.5 Transactions

Transactions are **connection-owned**. The WS layer enforces ownership: a `tx.get`, `tx.query`, or `tx.commit` from a different connection than the one that called `tx.begin` returns `ABORTED` — not a security error, just an ownership boundary.

On disconnect, the server **best-effort rolls back** all open transactions belonging to that connection.

```json
Client: {"type": "tx.begin", "id": "t1"}
Server: {"type": "response", "id": "t1", "result": {"transactionId": "abc123..."}}

Client: {"type": "tx.get", "id": "t2", "transactionId": "abc123...", "path": "users/u1"}
Server: {"type": "response", "id": "t2", "result": {"document": {…}}}

Client: {"type": "tx.query", "id": "t3", "transactionId": "abc123...",
         "query": {"collection": "users", "where": {...}}}
Server: {"type": "response", "id": "t3", "result": {"documents": [...], "hasMore": false}}

Client: {
  "type": "tx.commit", "id": "t4", "transactionId": "abc123...",
  "writes": [{"set": {"path": "users/u1", "fields": {"score": {"integerValue": "99"}}}}]
}
Server: {"type": "response", "id": "t4", "result": {"writeResults": [...]}}
```

On conflict:

```json
Server: {"type": "error", "id": "t4", "status": "ABORTED", "message": "Transaction conflict."}
```

Rollback (idempotent):

```json
Client: {"type": "tx.rollback", "id": "t5", "transactionId": "abc123..."}
Server: {"type": "response", "id": "t5", "result": {}}
```

**Transaction expiry:** Transactions idle out after **60 seconds** of no activity (default; configurable via `WincheDatabaseOptions.TransactionConfig.IdleTimeoutSpan`). The absolute maximum lifetime is **5 minutes** (configurable via `TotalTimeoutSpan`). Access on an expired or unknown transaction id yields `ABORTED`.

### 8.6 Listener protocol

#### Subscribe

```json
Client: {"type": "listen", "id": "s1", "query": {"collection": "users"}}
Server: {"type": "response", "id": "s1", "result": {"subscriptionId": "sub-xyz"}}
```

Optional resume:

```json
Client: {"type": "listen", "id": "s1", "query": {"collection": "users"}, "resumeToken": 42}
```

#### Unsubscribe

```json
Client: {"type": "unlisten", "id": "s2", "subscriptionId": "sub-xyz"}
Server: {"type": "response", "id": "s2", "result": {}}
```

#### Snapshot (full state — REPLACES client state)

The **first** event after subscribing is always a full snapshot:

```json
{
  "type":           "listen.snapshot",
  "subscriptionId": "sub-xyz",
  "documents":      [{…}, {…}],
  "readTime":       "2026-06-07T12:00:00+00:00",
  "resumeToken":    57
}
```

`documents` is the complete ordered result. The client **replaces** its local copy with this list. All subsequent changes arrive as deltas until the next snapshot reset.

#### Delta (incremental mutation — MUTATES client state)

```json
{
  "type":           "listen.delta",
  "subscriptionId": "sub-xyz",
  "changes": [
    {"kind": "added",    "document": {…}, "oldIndex": -1, "newIndex": 1},
    {"kind": "modified", "document": {…}, "oldIndex": 0,  "newIndex": 0},
    {"kind": "removed",  "document": {…}, "oldIndex": 2,  "newIndex": -1}
  ],
  "count":       3,
  "readTime":    "2026-06-07T12:00:01+00:00",
  "resumeToken": 58
}
```

| Change kind | `oldIndex` | `newIndex` |
| - | - | - |
| `added` | `-1` | position in new snapshot |
| `removed` | position in previous snapshot | `-1` |
| `modified` | position in previous snapshot | position in new snapshot |

`count` is the result size **after** applying the delta. Use it as a checksum: if your local count differs from `count` after applying all changes, re-subscribe.

The WS layer tracks the last document list it actually **sent** per subscription and re-diffs each runtime snapshot against it. Deltas are therefore exact relative to client state even across runtime-channel coalescing (dropped intermediate snapshots). An empty re-diff produces no frame.

#### Resume semantics

The `resumeToken` on every listener event is the feed sequence watermark.

| Scenario | Result |
| - | - |
| Resume token is current (no feed rows after it touch the query collection) | **Silent** — no initial snapshot until a real change occurs |
| Resume token is stale (feed rows exist after it for the collection) | **Reset** — a full `listen.snapshot` is sent immediately |
| Resume token is too old or unrecognized | **Reset** — same as stale |

The first event after a stale/reset resume is a `listen.snapshot` (not a delta), allowing the client to rebuild its state.

#### Backpressure

The runtime listener channel is a **bounded coalescing channel** (capacity 1, drop-oldest). If the client is consuming snapshots slowly, intermediate runtime snapshots are silently dropped and only the latest state is delivered. The re-diff at the WS adapter layer ensures the delta is still correct against whatever state was last sent.

The **send queue** is bounded at 64 frames (configurable). If it fills up, the connection is closed with code `1013`.

---

## 9. Operational Notes

### 9.1 Hooks: feed-driven, at-least-once, idempotent

Document lifecycle hooks (`DocumentStoreHook`) are invoked **inline and sequentially** by `HookFeedConsumer`, which reads from the durable `changes` feed table.

- **Feed-driven:** hooks fire from every node reading the shared feed, not from the write path directly. A write on any node triggers hook delivery on all nodes that have a `HookFeedConsumer` running.
- **Inline and sequential execution:** within each batch, hooks execute inline in the `HookFeedConsumer`. A hook that throws propagates the failure to the `DurableConsumerRunner`.
- **True at-least-once end-to-end:** the cursor is persisted **only after all hooks in a batch succeed**. On failure the runner retries the **same batch** with capped exponential backoff (1 s → 30 s). A poison hook blocks only the hooks consumer; it does not affect other consumers or listeners.
- **Idempotency required:** because delivery is at-least-once (and because the seq/commit-order race described below can cause redelivery), hook implementations must be idempotent. The document `version` field is a reliable idempotency key — hooks can record the last-processed version per path.
- **Durable cursor:** the cursor (`HookFeedConsumer.DurableName = "hooks"`) is persisted in `winche_feed_cursors`. On restart, the consumer resumes from the saved position and catches up on any writes that occurred while it was down.
- **First boot:** on first deploy, the cursor is initialized to `MAX(seq)` at startup — historical documents are not replayed.

### 9.2 Feed retention and pruning

The `changes` table is pruned automatically. Feed rows older than the configured retention period are deleted by a background pruner.

| `WincheDatabaseOptions.ChangeFeed` field | Default | Description |
| - | - | - |
| `Retention` | 7 days | Feed rows older than this are eligible for pruning |
| `PruneInterval` | 10 minutes | How often the pruner runs |
| `PollInterval` | 2 seconds | Poll fallback (in addition to `pg_notify` wake-ups) |
| `BatchSize` | 500 | Maximum feed rows read per pump batch |

### 9.3 Seq/commit-order caveat

PostgreSQL assigns `BIGSERIAL` sequence values (`seq`) **before** a transaction commits. Two concurrent write transactions can therefore commit out of `seq` order: transaction A (seq 5) may commit **after** transaction B (seq 6). A consumer that advances past seq 6 immediately after B commits will permanently miss seq 5 when A later commits.

The practical window for this race is the p99 write-transaction duration — typically sub-millisecond on a healthy cluster. The cursor save uses `GREATEST(current, incoming)` so a backwards-advancing cursor can only cause redelivery, never an additional skip.

**Consequence:** the feed delivers mutations in `seq` order, which is **close to** but **not guaranteed to be** commit order. Hook implementations and feed consumers must tolerate occasional out-of-order delivery and should use idempotency keys (e.g. document `version`) rather than assuming strict ordering.

### 9.4 Listener delivery is per-node

`ListenerRegistry` is in-process (per-node). Listeners on node A do not see writes processed only on node B's feed pump batch. Each node's feed pump reads from the shared `changes` table and dispatches to that node's listeners. In a multi-node deployment, all nodes receive the same feed events (independently) and update their own in-process listener groups accordingly.

### 9.5 Filtered indexes

`IndexDefinition` subclasses may override `Where` to define a partial index predicate. The predicate is evaluated by a restricted literal SQL emitter at index-sync time — it supports `And` composites, field equality/range comparisons, and `Exists`/`IsNull` unary checks. Expressions not in the restricted subset cause an `ArgumentException` at index-sync time with a descriptive message.

### 9.6 Transaction configuration

| `WincheDatabaseOptions.TransactionConfig` field | Default | Description |
| - | - | - |
| `IdleTimeoutSpan` | 60 seconds | Transaction idle-out after this period of no activity |
| `TotalTimeoutSpan` | 5 minutes | Absolute maximum transaction lifetime |
| `CleanupInterval` | 1 second | How often the expiry sweeper runs |
