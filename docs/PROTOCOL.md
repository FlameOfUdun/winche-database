# Winche.Database — Wire Protocol Reference

**Last updated:** 2026-06-21

> This document is the authoritative reference for all wire formats: typed values, documents, write operations, queries, error codes, the REST API, and the WebSocket protocol. Every JSON example is a shape verified against the parser source and integration tests.

---

## Table of Contents

1. [Values](#1-values)
2. [Documents & Paths](#2-documents--paths)
3. [Writes](#3-writes)
4. [Queries](#4-queries)
5. [Errors](#5-errors)
6. [REST API](#6-rest-api)
7. [WebSocket Protocol](#7-websocket-protocol)
8. [Operational Notes](#8-operational-notes)

---

## 1. Values

All field values use a **tagged-value** encoding. Each value is a JSON object with exactly one type-discriminator key. The parser (`ValueSerializer`) rejects objects with zero or more than one key.

### 1.1 Type table

| C# type | Wire key | Payload | Notes |
| - | - | - | - |
| `NullValue` | `nullValue` | `null` (JSON null) | Payload **must** be JSON null — any other JSON value is `invalid-argument` |
| `BooleanValue` | `booleanValue` | JSON boolean | |
| `IntegerValue` | `integerValue` | string or JSON number (int64) | Prefer string form for large values; both accepted |
| `DoubleValue` | `doubleValue` | JSON number **or** one of the strings `"NaN"`, `"Infinity"`, `"-Infinity"` | |
| `TimestampValue` | `timestampValue` | RFC 3339 string, UTC (`Z`), microsecond precision (`yyyy-MM-ddTHH:mm:ss.ffffffZ`) | Stored truncated to µs (sub-µs ticks dropped) |
| `StringValue` | `stringValue` | JSON string | Unicode; code-point order for sorting |
| `BytesValue` | `bytesValue` | standard base64 string | |
| `ReferenceValue` | `docRefValue` | document path string (e.g. `"users/u1"`) | A document |
| `FileRefValue` | `fileRefValue` | file path string (e.g. `"scans/a.png"`) | A file in Winche Storage |
| `UserRefValue` | `userRefValue` | uid string (e.g. `"u1"`) | A Winche Auth user |
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
{"docRefValue": "users/u1"}
{"fileRefValue": "scans/a.png"}
{"userRefValue": "u1"}
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
| Document reference | 70 | Whole-path Unicode code-point (byte) comparison — **not** per-segment; this differs from per-segment ordering for ids containing chars < `'/'` (U+002F) |
| File reference | 72 | Path, Unicode code-point comparison |
| User reference | 74 | Uid, Unicode code-point comparison |
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

### 2.3 Auto-generated document ids

`Winche.Database.Documents.DocumentId.NewId()` generates a 20-character base62 id using a cryptographically random source. This is used internally by `AddAsync` / the `:add` REST verb / the `add` WS message, and is also available directly for pre-generating ids on the client side.

```csharp
string id = DocumentId.NewId();  // e.g. "aB3dEfGhIjKlMnOpQrSt"
```

### 2.4 Field paths

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

- Minimum: 1 write. Maximum: **500** writes (`invalid-argument` beyond that).
- Each write is a JSON object with exactly **one** of the shape keys `set`, `update`, or `delete`. Having zero or more than one is `invalid-argument`.

### 3.2 Set write

```json
{
  "set": {
    "path":        "users/u1",
    "fields":      {"name": {"stringValue": "Alice"}},
    "merge":       false,
    "mergeFields": ["displayName", "address.city"],
    "transforms":  [],
    "precondition": {"exists": false}
  }
}
```

| Field | Type | Description |
| - | - | - |
| `path` | string | Required. Document path (even segments). |
| `fields` | object | Required. Map of literal key → tagged value. |
| `merge` | boolean | Default `false`. When `true`: deep-merge into existing document instead of replacing it. Mutually exclusive with `mergeFields`. |
| `mergeFields` | array of strings | Optional. Dotted field-path strings (e.g. `["a", "b.c"]`). When set, only the listed paths are written; all other existing paths are left untouched. Mutually exclusive with `merge: true`. Must be non-empty. |
| `transforms` | array | Optional. See [§3.7 Transforms](#37-transforms). |
| `precondition` | object | Optional. See [§3.5 Preconditions](#35-preconditions). |

**Merge semantics (`merge: true`):** When `merge: true` the write recurses through `MapValue` fields and merges them. Top-level keys in `fields` that are `mapValue`s are merged recursively. Non-map existing fields at the same key are replaced. The `deleteField` sentinel is legal as a map value at any depth inside a merge-set (see [§3.6 deleteField](#36-deletefield-sentinel)).

**Merge-mask semantics (`mergeFields`):** Only the paths listed in `mergeFields` are written. For each masked path: if the path is present in `fields`, that value is set; if the path is absent from `fields` (or is a `deleteField` sentinel), that path is deleted from the document. Paths not in the mask are left completely untouched. A masked intermediate path (e.g. `"m"`) replaces the entire subtree at that key with the data's value. `mergeFields` and `merge: true` are mutually exclusive (→ `invalid-argument`). An empty `mergeFields` array is `invalid-argument`.

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

The update creates or patches individual nested fields without disturbing siblings. An `UpdateWrite` on a missing document yields `not-found`.

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

At least one of `exists` or `updateTime` must be set (an object with neither is `invalid-argument`).

| Check | Failure |
| - | - |
| `exists: true` on missing doc | `not-found` |
| `exists: false` on existing doc | `already-exists` |
| `updateTime` mismatch (µs-exact) | `failed-precondition` |

Precondition failures abort the **entire** batch — nothing is applied.

### 3.6 deleteField sentinel

`{"deleteField": true}` is a write-time sentinel that removes a field.

| Context | Allowed |
| - | - |
| `UpdateWrite.Fields` values (any depth via dotted path keys) | Yes |
| `SetWrite(merge: true).Fields` values — top-level or inside `mapValue` at any depth | Yes |
| `SetWrite(mergeFields: [...]).Fields` values — for a masked path, signals deletion of that path | Yes |
| `SetWrite(merge: false).Fields` values | No — `invalid-argument` |
| Inside any `ArrayValue` (at any depth) | No — `invalid-argument` |
| In transform operands | No — `invalid-argument` |

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

**C# API — `FieldValue` factory.** `Winche.Database.Runtime.Writes.FieldValue` builds `FieldTransform` objects and the `DeleteFieldValue` sentinel without constructing wire objects by hand:

| Method | Placement | Notes |
| - | - | - |
| `FieldValue.ServerTimestamp(field)` | `Transforms` list | Sets the field to the batch commit time |
| `FieldValue.Increment(field, long\|double)` | `Transforms` list | Saturating int add; promotes to double on mixed |
| `FieldValue.Maximum(field, long\|double)` | `Transforms` list | Keeps the larger value; NaN is the smallest |
| `FieldValue.Minimum(field, long\|double)` | `Transforms` list | Keeps the smaller value |
| `FieldValue.ArrayUnion(field, params Value[])` | `Transforms` list | Appends elements not already present |
| `FieldValue.ArrayRemove(field, params Value[])` | `Transforms` list | Removes typed-equal elements |
| `FieldValue.Delete()` | `Fields` map | Removes the field; only valid in `UpdateWrite` or `SetWrite(merge: true)` |

`Delete()` returns a `DeleteFieldValue` sentinel that is embedded directly in the `Fields` map (equivalent to the `{"deleteField": true}` wire form — see §3.6). All other methods return a `FieldTransform` that goes in the `Transforms` list (out-of-band, applied after the write data). Transforms and the delete sentinel are therefore deliberately heterogeneous.

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

### 4.1 Query wire shape

```json
{
  "collection": "users",
  "where": { ... },
  "orderBy": [ ... ],
  "limit": 25,
  "offset": 50,
  "limitToLast": 10,
  "select": ["displayName", "address.city"],
  "start": {"values": [...], "before": true},
  "end":   {"values": [...], "before": false}
}
```

| Field | Type | Required | Description |
| - | - | - | - |
| `collection` | string | Yes | Collection path (e.g. `users/u1/notes`), or with `collectionGroup` a collection id (`notes`). |
| `collectionGroup` | boolean | No | `true`: every collection with this id, under any parent (10.5.0). |
| `where` | object | No | Filter — see §4.2. |
| `orderBy` | array | No | Sort keys — see §4.3. |
| `limit` | integer | No | Maximum documents to return. Default **100** when omitted. Mutually exclusive with `limitToLast`. |
| `offset` | integer | No | Skip the first N matching results (`>= 0`). Composes with `limit`. Mutually exclusive with `limitToLast`. |
| `limitToLast` | integer | No | Return the **last** N results of the ordered query, in ascending order (`>= 1`). Requires at least one `orderBy`. Mutually exclusive with `limit` and `offset`. |
| `select` | array | No | Field-projection list — see below. |
| `start` | cursor | No | Lower bound cursor — see §4.4. |
| `end` | cursor | No | Upper bound cursor. |

**`offset` — skip N results.** When present, the first `offset` matching documents are discarded before `limit` is applied. Must be `>= 0` (negative → `BAD_OFFSET`). Cannot be combined with `limitToLast` (`OFFSET_LIMIT_TO_LAST`). `offset` is ignored by the `:count` / `CountAsync` path — the count always reflects the full filter/cursor match (`count` has no offset concept).

**`limitToLast` — last N results.** Returns the last N documents in the ordered result set, delivered in ascending order (the query runs as if the sort directions were reversed, then the results are flipped back). Must be `>= 1` (→ `BAD_LIMIT_TO_LAST`). Requires at least one `orderBy` (→ `LIMIT_TO_LAST_NO_ORDER`). Cannot be combined with `limit` (`LIMIT_CONFLICT`) or `offset` (`OFFSET_LIMIT_TO_LAST`). **`hasMore` semantics:** for a `limitToLast` query, `hasMore: true` means rows exist *before* the returned window (earlier in the original sort order) — not after it.

**`select` — field projection.** When present, `select` is a JSON array of dotted field-path strings (e.g. `["displayName", "address.city"]`). The response documents contain **only** those fields; top-level and nested paths are both supported. Document id, path, and metadata (`createTime`, `updateTime`, `version`) are always preserved. Projection is applied server-side — the full document is never materialized. `select` is orthogonal to authorization: rules are evaluated against the full document as normal.

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

**C# convenience — `Cursor.FromDocument`.** `Cursor.FromDocument(Document doc, IReadOnlyList<Ordering>? orderBy, bool before)` derives cursor values from a document snapshot: for each `orderBy` field it reads that field's value from `doc`, then appends the implicit `__name__` tiebreaker as `{"docRefValue": "<doc.Path>"}`. A null or empty `orderBy` produces a `__name__`-only cursor. Throws `ArgumentException` if a required field is absent from the document. The resulting `Cursor` is identical on the wire to one constructed by hand — `Cursor.FromDocument` is a client-side convenience only.

| `Query` property | `before` | Equivalent |
| - | - | - |
| `Start` | `true` | `startAt(snapshot)` |
| `Start` | `false` | `startAfter(snapshot)` |
| `End` | `false` | `endAt(snapshot)` |
| `End` | `true` | `endBefore(snapshot)` |

---

## 5. Errors

Every Winche API — this one, storage, auth and functions — answers a failed request the same way: an `error` object
with a `code` from one shared list, a `message` for people, and, where it helps, a more precise `reason` and `details`.

### 5.1 Error codes

| Code | HTTP | Here |
| - | - | - |
| `invalid-argument` | 400 | Malformed request, bad field types, batch size exceeded, write shape invalid; a query that doesn't parse or validate has `reason: "invalid-query"` (`details.jsonPath` for JSON parse errors, `details.code` for plan-validation failures) |
| `failed-precondition` | 400 | `updateTime` precondition mismatch |
| `unauthenticated` | 401 | Authentication token invalid or missing |
| `permission-denied` | 403 | Access rule denied the operation |
| `not-found` | 404 | `UpdateWrite`/`exists:true` precondition on a missing document |
| `already-exists` | 409 | `exists:false` precondition on an existing document |
| `aborted` | 409 | Transaction conflict, expired transaction, or unknown transaction id |
| `resource-exhausted` | 429 | Too many live subscriptions on one connection |
| `deadline-exceeded` | 504 | Operation timed out |
| `internal` | 500 | Unexpected server error (always a bug; `details.errorId` finds it in the server's log) |

The full list (`cancelled`, `unknown`, `out-of-range`, `unimplemented`, `unavailable`, `data-loss`, …) is shared by
every Winche API; these are the ones the database sends.

### 5.2 Error body (REST) and error frame (WebSocket)

```json
{"error": {"code": "not-found", "message": "Document 'users/u99' does not exist."}}
```

`reason` and `details` appear only when there is something in them:

```json
{"error": {"code": "invalid-argument", "reason": "invalid-query", "message": "Unknown operator", "details": {"jsonPath": "$.where.op"}}}
{"error": {"code": "invalid-argument", "reason": "invalid-query", "message": "'limit' and 'limitToLast' are mutually exclusive.", "details": {"code": "LIMIT_CONFLICT"}}}
```

A WebSocket error frame carries the same object: `{"type": "error", "id": "req-42", "error": {"code": "…", "message": "…"}}`.

### 5.3 HTTP status

As in §5.1, the same for every Winche API. Body binding errors are `invalid-argument` (400).

### 5.4 Plan-validation error codes

These codes appear in `details.code` for `invalid-query` plan-validation failures (`PlanValidationException`):

| Code | Meaning |
| - | - |
| `EMPTY_COLLECTION` | `collection` is empty |
| `BAD_COLLECTION_PATH` | `collection` is not a valid collection path |
| `BAD_LIMIT` | `limit` is less than 1 |
| `BAD_OFFSET` | `offset` is negative |
| `BAD_LIMIT_TO_LAST` | `limitToLast` is less than 1 |
| `LIMIT_CONFLICT` | Both `limit` and `limitToLast` are set |
| `OFFSET_LIMIT_TO_LAST` | Both `offset` and `limitToLast` are set |
| `LIMIT_TO_LAST_NO_ORDER` | `limitToLast` is set but no `orderBy` is present |
| `CURSOR_ARITY` | A cursor has the wrong number of values (1..number of sort keys) |
| `CURSOR_TYPE` | A `__name__` cursor value is not a string or reference |
| `EMPTY_COMPOSITE` | An `and`/`or`/`not` composite has no children |
| `NOT_ARITY` | A `not` composite does not have exactly one child |
| `NAME_OPERATOR` | `__name__` used with an unsupported operator (only `eq`/`ne`/`gt`/`gte`/`lt`/`lte`) |
| `COMPARE_OP` | `compare` used with an unsupported operator (only `eq`/`ne`/`gt`/`gte`/`lt`/`lte`) |
| `OPERAND_TYPE` | An operator's operand has the wrong type (e.g. `in` needs a non-empty array; `startsWith` needs a string) |

---

## 6. REST API

The REST API is mounted at a configurable prefix (default `documents`). All paths below use `documents` as the prefix.

### 6.1 Convenience endpoints

Document paths in URL segments are **Base64-encoded** (standard, UTF-8 bytes) to avoid routing conflicts with slashes.

| Method | Route | Request body | Response | Description |
| - | - | - | - | - |
| `GET` | `/documents/v1/{base64Path}` | — | `Document` or 404 | Get a document |
| `PUT` | `/documents/v1/{base64Path}` | `{"fields": {…}}` | `Document` | Create or replace (set without merge). Performs a read after write to return the current document. |
| `PATCH` | `/documents/v1/{base64Path}` | `{"fields": {…}}` | `Document` or 404 | Patch fields (update). Field keys are dotted paths. |
| `DELETE` | `/documents/v1/{base64Path}` | — | 204 or 404 | Delete (cascade). Returns 404 if missing (performs read first). |
| `GET` | `/documents/v1/ping` | — | 200 | Health check |

### 6.2 Colon-verb endpoints

These endpoints use literal-colon URL segments and are mapped **outside** the `/{prefix}` route group (ASP.NET Core route groups cannot contain a `:` segment). They still receive the same built-in claims and error filters, which are applied to each verb route individually. `MapWincheDatabaseRestApi` returns a single composite `IEndpointConventionBuilder` spanning the CRUD/ping group **and** every colon-verb, so any convention you apply to the return value (e.g. `.RequireAuthorization()`, rate limiting) lands on all endpoints at once.

| Endpoint | Request body | Response | Description |
| - | - | - | - |
| `POST /documents/v1:add` | `{"collection": "<collectionPath>", "fields": {…}}` | `{"document": <Document>}` | Create a document with a generated 20-char base62 id. Evaluated as a **create** by the rules engine. |
| `POST /documents/v1:commit` | `{"writes": [...], "transaction"?: "id"}` | `{"writeResults": [...]}` | Atomic write batch. With `transaction`: commits the transaction. |
| `POST /documents/v1:beginTransaction` | `{}` | `{"transaction": "id"}` | Begin an optimistic transaction. |
| `POST /documents/v1:rollback` | `{"transaction": "id"}` | `{}` | Roll back (idempotent; unknown id is a no-op). |
| `POST /documents/v1:batchGet` | `{"documents": ["path1","path2",...], "transaction"?: "id"}` | `{"documents": [Document\|null, ...]}` | Bulk read preserving input order; missing docs are `null`. |
| `POST /documents/v1:runQuery` | `{"query": <Query>, "transaction"?: "id"}` | `{"documents": [...], "hasMore": false}` | Execute a query. Default `limit` = 100 when omitted. |
| `POST /documents/v1:count` | `{"query": <Query>}` | `{"count": N}` | Count documents matching the query. An explicit `limit` caps the count; omitted = full match (the 100 default does **not** apply). Authorized as a `list` operation — the query must provably satisfy a read rule or the request is rejected with `permission-denied`. |
| `POST /documents/v1:aggregate` | `{"query": <Query>, "aggregations": [...]}` | `{"result": {<alias>: <tagged value>, …}}` | Run one or more aggregations (count / sum / average) over a query. An explicit `limit` caps all aggregations. Authorized as a `list` operation. |

### 6.3 Request/response examples

**:add**

```json
POST /documents/v1:add
{
  "collection": "users",
  "fields": {
    "name":  {"stringValue": "Alice"},
    "score": {"integerValue": "0"}
  }
}

200 OK
{
  "document": {
    "path":       "users/aB3dEfGhIjKlMnOpQrSt",
    "id":         "aB3dEfGhIjKlMnOpQrSt",
    "collection": "users",
    "fields": {
      "name":  {"stringValue": "Alice"},
      "score": {"integerValue": "0"}
    },
    "createTime": "2026-06-22T10:00:00+00:00",
    "updateTime": "2026-06-22T10:00:00+00:00",
    "version": 1
  }
}
```

Errors: missing or non-string `collection`, or missing `fields` → `invalid-argument` (HTTP 400); create denied by rules → `permission-denied` (HTTP 403).

> **Design note:** unlike the CRUD convenience endpoints (which encode the document path in a Base64 URL segment), `:add` carries `collection` in the JSON request body. This is consistent with the other colon-verbs (`:commit`, `:runQuery`, `:count`), which also take structured JSON bodies. Winche's colon-verb form is the deliberate, uniform choice.

**:commit (batch)**

```json
POST /documents/v1:commit
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
POST /documents/v1:beginTransaction
{}

200 OK
{"transaction": "6a3f2e..."}

POST /documents/v1:batchGet
{"documents": ["users/u1"], "transaction": "6a3f2e..."}

200 OK
{"documents": [{"path": "users/u1", "fields": {...}, ...}]}

POST /documents/v1:commit
{"writes": [{"set": {"path": "users/u1", "fields": {"score": {"integerValue": "99"}}}}],
 "transaction": "6a3f2e..."}

200 OK
{"writeResults": [{"updateTime": "2026-06-07T12:00:01+00:00", "transformResults": null}]}
```

**:runQuery**

```json
POST /documents/v1:runQuery
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

**:count**

```json
POST /documents/v1:count
{
  "query": {
    "collection": "users",
    "where": {"field": "score", "op": "gte", "value": {"integerValue": "50"}}
  }
}

200 OK
{"count": 42}
```

**:aggregate**

Request body: `query` (required) and `aggregations` (required, 1–5 items). Each aggregation has a `kind` (`"count"`, `"sum"`, or `"average"`), an `alias` (non-empty, unique within the request), and for `sum`/`average` a `field` (dotted field path; may not be `__name__`). `count` takes no `field`.

```json
POST /documents/v1:aggregate
{
  "query": {
    "collection": "orders",
    "where": {"field": "status", "op": "eq", "value": {"stringValue": "shipped"}}
  },
  "aggregations": [
    {"kind": "count",   "alias": "cnt"},
    {"kind": "sum",     "field": "total", "alias": "s"},
    {"kind": "average", "field": "total", "alias": "a"}
  ]
}

200 OK
{
  "result": {
    "cnt": {"integerValue": "2"},
    "s":   {"integerValue": "350"},
    "a":   {"doubleValue": 175.0}
  }
}
```

Each value in the response is a tagged value (see §1). `count` → `integerValue`. `sum` → `integerValue` when all matched operands are integers; `doubleValue` when any is a double (NaN/Infinity propagated; integer overflow → double); empty match → `{"integerValue":"0"}`. `average` → `doubleValue`, or `{"nullValue":null}` when no numeric operand matched.

Errors: bad `kind`, missing `alias`, duplicate alias, empty alias, missing `aggregations` array, more than 5 aggregations, `sum`/`average` with no `field`, `count` with a `field`, field is `__name__` → `invalid-argument` (HTTP 400); denied by rules → `permission-denied` (HTTP 403).

> **Authorization:** `:aggregate` is authorized under the **`list`** rule (field-agnostic, same as `:count` and `:runQuery`). The query must provably satisfy a read rule or the request is rejected with `permission-denied`.

> **Notes:** an explicit `limit` on the query caps all aggregations; collection-group queries are not supported.

### 6.4 Sticky-transaction caveat

> **IMPORTANT — Multi-node deployments**
>
> Transaction state lives in the **serving node's in-memory ledger**. In a multi-node (horizontally scaled) deployment, all requests belonging to the same transaction must be routed to the **same node** (sticky sessions, consistent hashing, or similar) for the duration of the transaction — from `:beginTransaction` through `:commit` or `:rollback`.
>
> If a request in an active transaction is routed to a different node, the transaction id will be unknown to that node and the operation will return `aborted`. No data corruption occurs — an `aborted` response is always safe to retry with a new transaction.
>
> For use cases requiring transaction-safe multi-node operation without sticky routing, use the **WebSocket API** instead: a WS connection is pinned to one server for its lifetime, so the transaction is always on the correct node.

---

## 7. WebSocket Protocol

### 7.1 Connection and authentication

Connect via standard WebSocket to `/{prefix}/ws` (default `/documents/v1/ws`).

**Authentication is at the HTTP upgrade.** Because browsers cannot set `Authorization` headers on WebSocket upgrades, the library ships `UseWincheWsQueryToken()` middleware. Place it **before** `UseAuthentication()`: it promotes a `?access_token=<token>` query parameter to an `Authorization: Bearer …` header so the registered authentication handler can validate it at the upgrade. Gate the endpoint with `.RequireAuthorization()` to reject unauthenticated upgrades before the socket is accepted.

An invalid or missing token results in an **HTTP 401** response — the WebSocket connection is never opened. There is no in-band authentication exchange.

On a successful upgrade the server **immediately** sends:

```json
{"type": "welcome", "connectionId": "a1b2c3..."}
```

The connection's identity is fixed for its lifetime. On token expiry the client reconnects.

> **Security notes:** always require TLS in production (tokens in query strings appear in server logs and proxies); use short-lived tokens and reconnect on expiry.

### 7.2 Close codes

| Code | Meaning |
| - | - |
| `4400` | Protocol violation (binary frame or other connection-level protocol error) |
| `4413` | Incoming frame too large (default 1 MiB; configurable via `WsOptions.MaxFrameBytes`) |
| `4401` | Access revoked (the user was deleted, disabled or signed out everywhere) — reconnect with a fresh token |
| `4429` | The user opened more connections than `WsOptions.MaxConnectionsPerUser`; this, their oldest, was closed |
| `1013` | Send queue overflow — server could not drain outbound frames (queue limit 64; configurable via `WsOptions.SendQueueLimit`) |
| `1000` | Normal closure (server-initiated clean shutdown) |

### 7.3 Envelope and correlation

Every **client** request carries a client-chosen `id`:

```json
{"type": "doc.get", "id": "req-42", "path": "users/u1"}
```

Every operation produces exactly one terminal server frame: either a `response` or an `error`. Both carry the same `id`:

```json
{"type": "response", "id": "req-42", "result": {"document": {...}}}
{"type": "error",    "id": "req-42", "error": {"code": "not-found", "message": "..."}}
```

Listener events are the only server frames that are **not** responses; they carry `subscriptionId` instead of `id`.

Inbound processing is **serial per connection** — the server awaits each handler before reading the next frame. Responses and listener events interleave through the single-writer send channel.

Binary frames are rejected with close code `4400`. Malformed JSON mid-connection yields an `error` frame; the connection remains open.

### 7.4 Operations

#### Reads

```json
Client: {"type": "doc.get", "id": "1", "path": "users/u1"}
Server: {"type": "response", "id": "1", "result": {"document": {…} | null}}

Client: {"type": "doc.getAll", "id": "2", "paths": ["users/u1", "users/u2"]}
Server: {"type": "response", "id": "2", "result": {"documents": [{…}, null]}}

Client: {"type": "query", "id": "3",
         "query": {"collection": "users", "where": {"field": "score", "op": "gt", "value": {"integerValue": "0"}}}}
Server: {"type": "response", "id": "3", "result": {"documents": [...], "hasMore": false}}

Client: {"type": "count", "id": "4",
         "query": {"collection": "users", "where": {"field": "score", "op": "gte", "value": {"integerValue": "50"}}}}
Server: {"type": "response", "id": "4", "result": {"count": 42}}

Client: {
  "type": "aggregate",
  "id": "5",
  "query": {
    "collection": "orders",
    "where": {"field": "status", "op": "eq", "value": {"stringValue": "shipped"}}
  },
  "aggregations": [
    {"kind": "count",   "alias": "cnt"},
    {"kind": "sum",     "field": "total", "alias": "s"},
    {"kind": "average", "field": "total", "alias": "a"}
  ]
}
Server: {
  "type": "response",
  "id": "5",
  "result": {
    "result": {
      "cnt": {"integerValue": "2"},
      "s":   {"integerValue": "350"},
      "a":   {"doubleValue": 175.0}
    }
  }
}
```

> **Note on the nested `result.result`:** the outer `result` is the standard WS response envelope (every `response` frame has `"result": {…}`); the inner `result` is the aggregate result object, mirroring the REST `:aggregate` response body. So the full path to the alias map is `response.result.result`.

> **Authorization:** `aggregate` is authorized under the **`list`** rule (field-agnostic, same as `count` and `query`). The query must provably satisfy a read rule or the server returns an `error` frame with `permission-denied`.

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

#### Add (auto-id)

```json
Client: {"type": "add", "id": "6", "collection": "users",
         "fields": {"name": {"stringValue": "Alice"}, "score": {"integerValue": "0"}}}
Server: {"type": "response", "id": "6", "result": {"document": {
  "path": "users/aB3dEfGhIjKlMnOpQrSt", "id": "aB3dEfGhIjKlMnOpQrSt",
  "collection": "users",
  "fields": {"name": {"stringValue": "Alice"}, "score": {"integerValue": "0"}},
  "createTime": "2026-06-22T10:00:00+00:00", "updateTime": "2026-06-22T10:00:00+00:00", "version": 1
}}}
```

On denial:

```json
Server: {"type": "error", "id": "6", "error": {"code": "permission-denied", "message": "Access denied."}}
```

#### Keepalive

```json
Client: {"type": "ping", "id": "6"}
Server: {"type": "response", "id": "6", "result": {}}
```

### 7.5 Transactions

Transactions are **connection-owned**. The WS layer enforces ownership: a `tx.get`, `tx.query`, or `tx.commit` from a different connection than the one that called `tx.begin` returns `aborted` — not a security error, just an ownership boundary.

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
Server: {"type": "error", "id": "t4", "error": {"code": "aborted", "message": "Transaction conflict."}}
```

Rollback (idempotent):

```json
Client: {"type": "tx.rollback", "id": "t5", "transactionId": "abc123..."}
Server: {"type": "response", "id": "t5", "result": {}}
```

**Transaction expiry:** Transactions idle out after **60 seconds** of no activity (default; configurable via `WincheDatabaseOptions.TransactionConfig.IdleTimeoutSpan`). The absolute maximum lifetime is **5 minutes** (configurable via `TotalTimeoutSpan`). Access on an expired or unknown transaction id yields `aborted`.

### 7.6 Listener protocol

#### Subscribe

```json
Client: {"type": "listen", "id": "s1", "query": {"collection": "users"}}
Server: {"type": "response", "id": "s1", "result": {"subscriptionId": "sub-xyz"}}
```

Optional resume:

```json
Client: {"type": "listen", "id": "s1", "query": {"collection": "users"}, "resumeToken": 42}
```

The optional `"protocol": <int>` field declares the client's wire protocol version
(current: 2). It gates the `deleted` change kind (see Delta below).

#### Single-document subscribe (`doc.listen`)

`doc.listen` subscribes to a single document by path. The server sends the same `response` → `listen.snapshot` → `listen.delta` sequence as `listen`; the `documents` array in each frame holds 0 or 1 elements (absent or present).

```json
Client: {"type": "doc.listen", "id": "d1", "path": "users/u1"}
Server: {"type": "response", "id": "d1", "result": {"subscriptionId": "sub-doc-abc"}}
```

Optional resume:

```json
Client: {"type": "doc.listen", "id": "d1", "path": "users/u1", "resumeToken": 42}
```

The optional `"protocol": <int>` field declares the client's wire protocol version
(current: 2). It gates the `deleted` change kind (see Delta below).

First event — document present:

```json
{
  "type":           "listen.snapshot",
  "subscriptionId": "sub-doc-abc",
  "documents":      [{"path": "users/u1", "fields": {...}, ...}],
  "readTime":       "2026-06-22T10:00:00+00:00",
  "resumeToken":    101
}
```

First event — document absent:

```json
{
  "type":           "listen.snapshot",
  "subscriptionId": "sub-doc-abc",
  "documents":      [],
  "readTime":       "2026-06-22T10:00:00+00:00",
  "resumeToken":    101
}
```

Subsequent changes arrive as `listen.delta` frames (same shape as query deltas). Use the existing `unlisten` message with the returned `subscriptionId` to cancel.

Errors:

```json
Client: {"type": "doc.listen", "id": "d2", "path": "users"}
Server: {"type": "error", "id": "d2", "error": {"code": "invalid-argument", "message": "..."}}
```

```json
Server: {"type": "error", "id": "d1", "error": {"code": "permission-denied", "message": "Access denied."}}
```

> **Authorization note:** `doc.listen` is authorized under the **`get`** rule on the document path — single-document reads (one-shot or realtime) are governed by the per-document get rule, never the parent-collection `list` rule. Use `RuleOperations.Read` (which expands to both `get` and `list`) or an explicit `get` rule to permit single-document listens.

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
| `deleted` | position in previous snapshot | `-1` |
| `modified` | position in previous snapshot | position in new snapshot |

`removed` means the document left this query's result (deleted, or out of the
`limit`/`offset`/cursor window, or no longer matching `where`). `deleted` means the
document **no longer exists** — the server determines this by checking existence at diff
time, so it is reliable even across coalesced snapshots. Clients use `deleted` to evict the
document from any local cache; `removed` only drops it from this query's view.

**Negotiation:** the `deleted` kind is sent **only** to clients that advertise
`"protocol": >= 2` on the `listen` / `doc.listen` subscribe frame. Clients that omit
`protocol` (or send `< 2`) receive `removed` for every removal, exactly as before. A
single-document (`doc.listen`) removal is always a true deletion, so it is reported as
`deleted` for protocol >= 2 clients and `removed` otherwise.

`count` is the result size **after** applying the delta. Use it as a checksum: if your local count differs from `count` after applying all changes, re-subscribe.

The WS layer tracks the last document list it actually **sent** per subscription and re-diffs each runtime snapshot against it. Deltas are therefore exact relative to client state even across runtime-channel coalescing (dropped intermediate snapshots). An empty re-diff produces no frame.

#### Resume semantics

The `resumeToken` on every listener event is an opaque integer: store it and send it back unchanged. (Since 10.5.0 it encodes the writing transaction the state covers through, and is negative; a token from an older server — a feed sequence number — is not trusted and gets one full snapshot.) A token is current only if every change it covers had committed when it was issued, so a write that commits after a later one can't be missed on resume.

| Scenario | Result |
| - | - |
| Resume token is current (no feed rows after it touch the query collection) | **Silent** — no initial snapshot until a real change occurs |
| Resume token is stale (feed rows exist after it for the collection) | **Reset** — a full `listen.snapshot` is sent immediately |
| Resume token is too old (pruning reached past it) or unrecognized (incl. pre-10.5.0 tokens) | **Reset** — same as stale |

The first event after a stale/reset resume is a `listen.snapshot` (not a delta), allowing the client to rebuild its state.

#### Current marker (`listen.current`)

When a resume token is **current** (no feed rows after it touch the query), the server
no longer stays silent: it sends a single `listen.current` frame so the client knows the
subscription is live and its cached data is up to date.

```json
{"type": "listen.current", "subscriptionId": "sub-xyz", "resumeToken": 57}
```

It carries no documents — the client keeps its existing state and clears its "from cache"
flag. A subsequent real change arrives as a normal `listen.snapshot`/`listen.delta`.
Clients that predate this frame treat an unknown frame type as ignorable, so the marker is
backward compatible.

#### Backpressure

The runtime listener channel is a **bounded coalescing channel** (capacity 1, drop-oldest). If the client is consuming snapshots slowly, intermediate runtime snapshots are silently dropped and only the latest state is delivered. The re-diff at the WS adapter layer ensures the delta is still correct against whatever state was last sent.

The **send queue** is bounded at 64 frames (configurable). If it fills up, the connection is closed with code `1013`.

---

### 7.7 Expanding references

`doc.get`, `doc.getAll`, `query`, `listen` and `doc.listen` take `"expand": ["patient", "patient.doctor", "team", "scan", "author"]`
(REST: `GET …/{path}?expand=patient,patient.doctor`; `:batchGet` and `:runQuery` take `"expand"` in the body).

- **Paths:** each segment names a field; maps are walked into, lists apply the rest to each item, and a `docRefValue`
  applies the rest to the document it points to. `patient.doctor` follows `patient`, then `doctor` in that document.
  At most 10 paths, 4 references deep, 100 documents (and files and users) per response; past that, references stay
  as they are.
- **Documents:** come beside the results under `"included": { "<path>": <document>, … }`, each once however often
  it's referenced (cycles included). The fields themselves are unchanged.
- **Files and users:** a `fileRefValue` or `userRefValue` a path ends at is resolved by Winche.Storage / Winche Auth
  and comes under `"resolved": { "file:<path>": {…}, "user:<uid>": {…} }`: the file's record (as storage returns it,
  never a download link) if the storage rules let the caller `get` it; the user's public profile (`uid`,
  `displayName`, `photoUrl`, and the claims the app lists as public) to signed-in callers, the whole account to its
  own user. A plain string is never resolved: store references with their types.
- **Rules:** each referenced document is read as the caller (its `get` rule). One they may not read, or that doesn't
  exist, is simply not in `included`/`resolved`: the client can't tell which.
- **Listening:** `listen.snapshot` carries the full `included` and `resolved`. Each `listen.delta` carries `included`
  and `resolved` (new or changed since the last frame) and `excluded` / `unresolved` (no longer there: deleted,
  unreadable, or no longer referenced). When only referenced things changed, the delta has `"changes": []`. The
  server listens to every referenced document and to storage and auth changes. A subscription with `expand` ignores
  `resumeToken` and always starts with a full `listen.snapshot`.

## 8. Operational Notes

### 8.1 Hooks: feed-driven, at-least-once, idempotent

Document lifecycle hooks (`DocumentStoreHook`) are invoked **inline and sequentially** by a `HookFeedConsumer`, which reads from the durable `changes` feed table.

- **One cursor per hook type (10.4.0+):** each hook type runs as its own durable consumer, named `hooks:<full type name>`, with its own cursor in `winche_documents_feed_cursors`. A hook that fails holds back only itself; the other hook types carry on. All registrations of one type share its cursor.
- **One node at a time:** every node runs the runners, but each consumer delivers only on the node holding its Postgres advisory lock; another node takes over if that one stops.
- **True at-least-once end-to-end:** the cursor moves past a change only after its hooks succeed. When a change fails, the cursor stays just before it and only that change is retried, with capped exponential backoff (1 s → 30 s); the changes before it in the batch are not redone.
- **Dead letters:** a change that keeps failing for `ChangeFeed.DeadLetterAfter` (default 1 hour, measured from its first failure and kept across restarts) is copied to `winche_documents_dead_letters` with the last error, and the hook moves on. `ChangeFeedConsumers` lists, replays (`ReplayDeadLettersAsync`) or discards them. Set `DeadLetterAfter = null` to retry for ever instead.
- **Idempotency required:** delivery is at-least-once (a retry, a replay or a node change can deliver a change again), so hook implementations must be idempotent. The document `version` field is a reliable idempotency key.
- **First boot:** a consumer without a cursor starts from now; historical documents are not replayed. On the upgrade to 10.4.0, each hook type's cursor starts from the old shared `hooks` cursor, so nothing is skipped or replayed. A hook type added (or renamed) later starts from now.
- **Status:** `ChangeFeedConsumers.GetStatusAsync()` reports, per consumer, how far behind it is, whether it is failing (since when, how often, the last error) and how many dead letters it has. `AddHealthChecks().AddWincheChangeFeed()` (Winche.Database.AspNetCore) turns that into a health check that reports Degraded — never Unhealthy, since restarting doesn't fix a failing hook.

### 8.2 Feed retention and pruning

The `changes` table is pruned automatically. A row older than `Retention` is deleted once every durable consumer has delivered it; a consumer that is behind keeps its rows, up to `MaxRetention`, after which they are deleted anyway and the pruner logs an error with the number of changes some consumer never got. Cursors of consumers the host no longer runs are ignored.

| `WincheDatabaseOptions.ChangeFeed` field | Default | Description |
| - | - | - |
| `Retention` | 7 days | Delivered feed rows older than this are pruned |
| `MaxRetention` | 30 days | Feed rows older than this are pruned even if undelivered |
| `DeadLetterAfter` | 1 hour | How long one change may keep failing before it is dead-lettered (`null` = never) |
| `PruneInterval` | 10 minutes | How often the pruner runs |
| `PollInterval` | 2 seconds | Poll fallback (in addition to `pg_notify` wake-ups) |
| `BatchSize` | 500 | Maximum feed rows read per pump batch |

### 8.3 Delivery order

Since 10.3.0 the feed is gap-free: rows are read in (writing transaction, `seq`) order and only from transactions older than the oldest one still running, so a change that commits after a later one is held back, never skipped. Across transactions the order matches commit order only loosely, so consumers should read the document's current state (as hooks do) and use the `version` for idempotency rather than assume strict ordering.

### 8.4 Listener delivery is per-node

`QueryListenerRegistry` is in-process (per-node). Listeners on node A do not see writes processed only on node B's feed pump batch. Each node's feed pump reads from the shared `changes` table and dispatches to that node's listeners. In a multi-node deployment, all nodes receive the same feed events (independently) and update their own in-process listener groups accordingly.

### 8.5 Filtered indexes

`IndexDefinition` subclasses may override `Where` to define a partial index predicate. The predicate is evaluated by a restricted literal SQL emitter at index-sync time — it supports `And` composites, field equality/range comparisons, and `Exists`/`IsNull` unary checks. Expressions not in the restricted subset cause an `ArgumentException` at index-sync time with a descriptive message.

### 8.6 Transaction configuration

| `WincheDatabaseOptions.TransactionConfig` field | Default | Description |
| - | - | - |
| `IdleTimeoutSpan` | 60 seconds | Transaction idle-out after this period of no activity |
| `TotalTimeoutSpan` | 5 minutes | Absolute maximum transaction lifetime |
| `CleanupInterval` | 1 second | How often the expiry sweeper runs |

### 8.7 TTL (auto-expiry) — server-side only, no wire surface

TTL policies are configured entirely server-side via `AddWincheDatabase` (`UseTtl`) and have **no REST or WebSocket wire surface**. Clients cannot register, query, or cancel TTL policies over the API.

The TTL sweeper runs on the configured `SweepInterval` (default 5 minutes) and deletes documents whose registered field holds a past `timestampValue`. Deletes are executed through the normal write path: they appear on the change feed as ordinary `removed` events and are therefore visible to live-query listeners and document lifecycle hooks. The sweeper is system-initiated and bypasses security rules. By default the delete cascades to the document's subcollections; set `CascadeDelete = false` to delete only the matched document.
