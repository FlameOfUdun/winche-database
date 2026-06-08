# Release Notes — Winche.Database v5

> v5 is a **major release scoped to the public .NET API surface**. The query/pipeline AST types are renamed (the `Ast` suffix and the pipeline `Stage` suffix are dropped) and the logical-plan layer is now internal. There are **no wire-format, storage, or behavioral changes** — every JSON shape, REST/WS endpoint, and runtime semantic is identical to v4. No data migration is required.

This affects only .NET callers that construct queries/pipelines in code (`IDocumentDatabase.QueryAsync`/`AggregateAsync`/`Listen`). Callers that talk to the REST/WS wire protocol are unaffected.

---

## Breaking Changes

### AST types renamed — `Ast` suffix dropped

The query and pipeline AST records no longer carry an `Ast` suffix (it was redundant with the `Winche.Database.Querying.Ast` namespace). Update type names in any code that builds queries or pipelines.

**Query family:**

| v4 | v5 |
| - | - |
| `QueryAst` | `Query` |
| `FilterAst` | `Filter` |
| `FieldFilterAst` | `FieldFilter` |
| `CompositeFilterAst` | `CompositeFilter` |
| `UnaryFilterAst` | `UnaryFilter` |
| `FieldCompareAst` | `FieldCompare` |
| `OrderAst` | `Ordering` |
| `CursorAst` | `Cursor` |

**Pipeline family** (the stage `Stage` suffix is also dropped):

| v4 | v5 |
| - | - |
| `PipelineAst` | `Pipeline` |
| `StageAst` | `Stage` |
| `MatchStageAst` | `Match` |
| `FilterStageAst` | `Where` |
| `LookupStageAst` | `Lookup` |
| `UnwindStageAst` | `Unwind` |
| `GroupStageAst` | `Group` |
| `ProjectStageAst` | `Project` |
| `SortStageAst` | `Sort` |
| `LimitStageAst` | `Limit` |
| `SkipStageAst` | `Skip` |
| `GroupKeyAst` | `GroupKey` |
| `AccumulatorAst` | `Accumulator` |
| `ProjectionAst` | `Projection` |
| `ProjectExprAst` | `ProjectExpr` |
| `FieldRefExprAst` | `FieldRefExpr` |
| `LiteralExprAst` | `LiteralExpr` |
| `AggFuncExprAst` | `AggFuncExpr` |

Notes:

- **`FilterStageAst` → `Where`**, not `Filter` — `Filter` is the query filter-expression base in the same namespace. The `$filter` stage maps to the `Where` type; its predicate property is renamed from `Where` to **`Predicate`** (`new Where(predicate)`). The wire key is still `"filter"`.
- Enums are unchanged (`AggFunction`, `CompositeOp`, `FilterOperator`, `SortDirection`, `UnaryOp`).
- The serialization helper classes keep their names (`QueryAstWriter`, `QueryAstJsonConverter`, `PipelineAstWriter`, `PipelineAstJsonConverter`).
- Namespaces are unchanged — types remain in `Winche.Database.Querying.Ast`.

Before / after:

```csharp
// v4
var q = new QueryAst("users",
    Where: new FieldFilterAst(FieldPath.Parse("score"), FilterOperator.Gte, new IntegerValue(50)),
    OrderBy: [new OrderAst(FieldPath.Parse("score"), SortDirection.Desc)]);

var p = new PipelineAst([
    new MatchStageAst("orders", Where: null),
    new GroupStageAst(
        [new GroupKeyAst("status", FieldPath.Parse("status"))],
        [new AccumulatorAst("total", AggFunction.Sum, FieldPath.Parse("amount"))]),
]);

// v5
var q = new Query("users",
    Where: new FieldFilter(FieldPath.Parse("score"), FilterOperator.Gte, new IntegerValue(50)),
    OrderBy: [new Ordering(FieldPath.Parse("score"), SortDirection.Desc)]);

var p = new Pipeline([
    new Match("orders", Where: null),
    new Group(
        [new GroupKey("status", FieldPath.Parse("status"))],
        [new Accumulator("total", AggFunction.Sum, FieldPath.Parse("amount"))]),
]);
```

### Logical-plan layer is now internal

The `Winche.Database.Querying.Planning` model types (`LogicalPlan`, `PlanNode` and all plan nodes, `SortKey`, `SortBoundary`, and the plan-side leaf records) are now `internal`, along with the entry points that traffic in them (`Normalizer`, `PipelineNormalizer`, `SqlCompiler`). These were implementation detail — produced by the normalizers, consumed by the SQL compilers — and never part of a supported public contract.

The runtime surface (`IDocumentDatabase`, the concrete `DocumentDatabase`, `QueryResult`/`PipelineResult`, executors, transactions, listeners) is unchanged. If you were depending on the plan types directly (you almost certainly were not), that code must move to the public query/pipeline API.

**Required action:** rename the AST types per the tables above. The compiler will flag every site. No behavioral or wire changes accompany this release.

### REST/WS mapping methods return a convention builder

`MapWincheDatabaseRestApi` and `MapWincheDatabaseWsApi` now return an `IEndpointConventionBuilder` (previously `WebApplication`), so cross-cutting policy — `.RequireAuthorization()`, rate limiting, CORS, metadata — can be applied to the whole mapped surface at once. For REST the returned builder covers the CRUD routes **and** every colon-verb (`:commit`, `:runQuery`, `:aggregate`, …), so applying authorization to it can no longer silently miss the verbs.

| v4 | v5 |
| - | - |
| `Map*` returned `WebApplication` | `Map*` return `IEndpointConventionBuilder` |
| REST `configure` / `configureVerbs` callbacks | removed — chain conventions on the returned builder |
| WS mapped `app.UseWebSockets()` for you | caller must call `app.UseWebSockets()` before mapping |

- **REST:** the `configure: Action<RouteGroupBuilder>` and `configureVerbs: Action<RouteHandlerBuilder>` parameters are **removed**. Use the returned builder instead — it applies uniformly to the group and all verbs.
- **WS:** `MapWincheDatabaseWsApi` no longer calls `app.UseWebSockets()` internally. **You must call `app.UseWebSockets()` yourself before mapping.** This fails at *runtime* (the upgrade returns `400`), not at compile time, so update your startup.

**Required action:** add `app.UseWebSockets()` before `MapWincheDatabaseWsApi()`; replace any use of the REST `configure`/`configureVerbs` callbacks with conventions on the returned builder (e.g. `app.MapWincheDatabaseRestApi().RequireAuthorization()`).

WebSocket connection auth remains in-band via the `hello`-frame token (`IWsAuthenticator`); `.RequireAuthorization()` gates the HTTP upgrade and is an optional, separate layer (see README).

---

## No Data Migration Required

v5 changes only .NET type names and type visibility. Storage format, wire format, and runtime behavior are identical to v4.
