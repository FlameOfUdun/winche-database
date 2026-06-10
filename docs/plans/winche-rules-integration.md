# Winche.Database ⟵ Winche.Rules integration plan

Replace Winche.Database's Sentinel-based authorization with the new **Winche.Rules** engine
(`C:\Users\Ehsan Rashidi\Desktop\Winche\.NET\Winche.Rules`), matching Firestore's model **in memory**.

## Principles (non-negotiable)
- **Firestore-faithful.** In-memory evaluation against the loaded document; "rules are not filters" for
  queries (reject, don't auto-filter); `request.resource` = the **full post-write document**.
- **No SQL translation of rules. No separate `SELECT (predicate)` round-trip.** (Both were the reverted approach.)
- **Reference Winche.Rules via a local `ProjectReference`** for now (NuGet later):
  `..\..\..\Winche.Rules\src\Winche.Rules\Winche.Rules.csproj` from `src/Winche.Database`.
- Any divergence from Firestore must be called out explicitly.

## Winche.Rules API the adapters use (engine is done; read it under the Winche.Rules working dir)
- `RuleValue` — `Null/Bool/Int/Double/String/Bytes/Timestamp/Path/List/Map` (factories `RuleValue.String(..)` etc.).
- `RuleContext { RuleValue Resource; RuleValue Request; IReadOnlyDictionary<string,RuleValue> Params; IDocumentSource? Documents }`.
- `IDocumentSource { Task<bool> ExistsAsync(path,ct); Task<RuleValue> GetAsync(path,ct) }` — for `get()`/`exists()`.
- `Ruleset` + `RulesetBuilder.Build(r => r.Match("users/{userId}", u => u.Allow(RuleOperations.Read, expr)))`.
- `RulesetEvaluator.AllowsAsync(ruleset, RuleOperation, documentPath, ctx, ct)` — get/create/update/delete.
- `QueryAnalyzer.Allows(ruleset, QueryConstraints, ctx)` — list (sound prove-or-reject).
- `QueryConstraints(collection, IReadOnlyList<QueryConstraint>)`, `QueryConstraint(IReadOnlyList<string> Field, CompareOp, RuleValue)`.
- `RuleOperation { Get, List, Create, Update, Delete }`, `RuleOperations.Read/Write/All/Of(..)`.

## Winche.Database touch points (current state)
- `Documents/Document.cs` (`IReadOnlyDictionary<string,Value> Fields`, path), `Values/Value.cs` (discriminated: `MapValue`, etc.).
- `Querying/Ast/QueryAst.cs` (`Query`), `Querying/Ast/FilterAst.cs` (`FieldFilter(FieldPath,FilterOperator,Value)`), `Querying/Ast/Enums.cs` (`FilterOperator`).
- `Runtime/IDocumentDatabase.cs`, `Runtime/GuardedDocumentDatabase.cs` (the guard to replace), `Runtime/Listening/GuardedQueryListener.cs`.
- `Runtime/Writes/DocumentMerger.cs` (compute post-write state), `Runtime/Writes/Write.cs`.
- `DependencyInjection/WincheDatabaseOptions.cs` + `ServiceCollectionExtensions.cs` (registration), `AspNetCore/.../DocumentClaimsAccessor.cs` (claims).

---

## Phase 0 — Reference + adapters (pure mapping, no behavior change)
**One Sonnet subagent.** Deliverables, all under `src/Winche.Database/Authorization/`:
1. Add the local `ProjectReference` to Winche.Rules in `Winche.Database.csproj`.
2. `ValueToRuleValue` — map Winche `Value` → `RuleValue` (every Value case → the right RuleValue kind; `MapValue`→Map, arrays→List, timestamps→Timestamp, etc.).
3. `DocumentToResource` — `Document` (+ path) → `RuleValue.Map { "data": <fields map>, "id": <last path segment>, "__name__": Path(path) }`.
4. `RequestBuilder` — build the `request` map `{ auth: {uid, token:{…}} | Null, resource: <RuleValue|Null>, method: <string>, time: Timestamp(now) }` from a claims dictionary + the incoming/post-write doc.
5. `QueryToConstraints` — `Query` → `QueryConstraints`: map each top-level `FieldFilter` to a `QueryConstraint(field-path, CompareOp, RuleValue)`; map `FilterOperator`→`CompareOp` for ==,!=,<,<=,>,>=; **skip** operators with no sound mapping (array-contains, in, etc.) — omitting them is safe (the analyzer will reject if the rule needs them).
6. `PostgresDocumentSource : IDocumentSource` — `get`/`exists` a document by path → `RuleValue` (reuse the existing read path / `DocumentDatabase`), **read-only, cached per request** (dictionary keyed by path within one authorization).

**Acceptance:** solution builds; unit tests for `ValueToRuleValue`, `DocumentToResource`, `QueryToConstraints` (round-trip/representative cases). **Do NOT** touch the guard or DI behavior yet. **Do NOT** add any SQL predicate building.

## Phase 1 — Rule registration + single-document guard (get / create / update / delete)
**One Sonnet subagent.**
1. DI: register a `Ruleset` (built via `RulesetBuilder`) + claims accessor + `PostgresDocumentSource`. Replace `AddDocumentAccessRule<T>()` with a `Ruleset`-based registration on `WincheDatabaseOptions`.
2. Reimplement the guard's **get** and **write** paths on Winche.Rules:
   - **Get(path):** load the doc (it's the read), build `RuleContext { Resource=DocumentToResource(doc), Request=RequestBuilder(claims, method:"get"), Documents=source }`, call `RulesetEvaluator.AllowsAsync(ruleset, Get, path, ctx)`. Deny → throw the existing access-denied error; treat denied like not-found (no existence leak).
   - **Write:** per write, decide op — **create** if the doc doesn't exist (resource = Null), **update** if it does, **delete** for deletes. Build `request.resource` = the **full post-write document** via `DocumentMerger` (for create/update), `Resource` = existing doc. `AllowsAsync(ruleset, op, path, ctx)`. Deny → throw before applying; batch denial rejects the whole batch.
3. Integration tests (owner-only get; create-requires-owner; update owner-immutable; delete; default-deny).

**Acceptance:** get/create/update/delete enforced in-memory via Winche.Rules; tests green. **Flag** if computing the post-write doc needs anything beyond `DocumentMerger`.

## Phase 2 — list/query + aggregate
**One Sonnet subagent.**
1. **Query/list:** `QueryToConstraints(query)` → `QueryAnalyzer.Allows(ruleset, constraints, ctx)`. Not allowed → **throw** (rules-are-not-filters: reject the query; do NOT post-filter or fold a predicate). Allowed → run the query unchanged.
2. **Count / aggregate:** count/sum/avg over a single collection → analyze like `list`. **Cross-collection pipeline (`$lookup`/`$group`/`$project`/`$unwind`) is a Firestore divergence** — per the agreed design, do not gate it per-row; leave the pipeline out of the Firestore-authorized path (document the limitation). Surface this for confirmation rather than inventing a gate.
3. Tests: constrained query allowed; unconstrained rejected; public/owner; weaker-range rejected.

## Phase 3 — listeners
**One Sonnet subagent.** A listen is a query: analyze the listen query at subscribe (reject if not provable). Live results are then constrained by the query itself, so the existing change-matcher needs no rule evaluation. Replace `GuardedQueryListener`'s per-doc Sentinel filtering. Tests for accepted/rejected listens.

## Phase 4 — remove Sentinel + migrate
**One Sonnet subagent.** Delete `DocumentAccessRule`, the Sentinel dependency from the auth path, and dead guard code; migrate samples + tests to the `Ruleset` API; whole solution + test suites green. Update this doc's status.

---

## Status
- **Phase 0 ✅** — adapters (`ValueToRuleValue`, `DocumentToResource`, `RequestBuilder`, `QueryToConstraints`, `PostgresDocumentSource`) under `src/Winche.Database/Authorization/`; local ProjectReference added; 42 unit tests.
- **Phase 1 ✅** — `Authorization/RuleGuardedDocumentDatabase.cs`: get + write (create/update/delete) via `RulesetEvaluator`, two-pass batch atomicity, `request.resource` = post-write doc via `DocumentMerger`/`FieldMutator`. 8 integration tests. Built directly (not wired to DI yet). Carried items:
  - **(Phase 4)** Throws `Winche.Sentinel.Models.AccessDeniedException` to match current error mapping — replace with a native exception + update `Wire/ErrorMapper.cs` when Sentinel is removed.
  - **(resolved ✅)** `request.resource` now resolves server-side **transforms** (serverTimestamp/increment/arrayUnion/arrayRemove/max/min) via `TransformApplier`, mirroring `WriteApplier`'s order; one captured timestamp threads through so `serverTimestamp == request.time`. 12 integration tests.

- **Phase 2 ✅** — `RuleGuardedDocumentDatabase`: `QueryAsync`/`CountAsync`/transactional query authorized via `QueryAnalyzer.Allows` (reject-or-allow, never post-filtered); `GetAllAsync` authorizes each path as a get (shared `AuthorizeGetAsync` helper). 5 integration tests (256 total). **Aggregate pipeline deliberately left unauthorized pending the decision below.**
  - **(decided)** The aggregate pipeline is being **removed** from the product (→ per-collection rule-checked queries + app-layer aggregation, plus a new `select`/projection on collection queries). So NO rule authorization is built for `AggregateAsync` — it stays passthrough until the feature is removed in a separate effort. When `select` lands, projection is applied AFTER authorization (orthogonal to rules; no leakage).

- **Phase 3 ✅** — `RuleGuardedDocumentDatabase.Listen` authorizes the query at subscribe via `QueryAnalyzer` (reuses `AuthorizeListQuery`); provably-safe → delegate to `inner.Listen` with NO wrapper/per-doc filtering (the query's constraints guarantee every live result). Claims captured at subscribe (re-auth on token refresh = future work). 3 tests incl. live snapshot delivery. Old `GuardedQueryListener` untouched (removed in Phase 4).

- **Phase 4a ✅** (additive — nothing flipped/removed): native `Authorization/AccessDeniedException(path, operation)` + `ErrorMapper` maps it to PERMISSION_DENIED (Sentinel mapping kept); `IRuleClaimsAccessor` + internal `SentinelRuleClaimsAccessorBridge`; `WincheDatabaseOptions.UseRules(Ruleset)` / `UseRules(Action<RulesetBuilder>)` + `Ruleset` singleton; `RuleGuardedDocumentDatabase` registered by concrete type (wraps the CORE db, claims via `IRuleClaimsAccessor`) and now throws the native exception. Default `IDocumentDatabase` unchanged (still Sentinel guard). 4 DI integration tests + ErrorMapper unit tests. Unit 436 / integ 263 green.
- **Phase 4b ✅** — default `IDocumentDatabase` now resolves to `RuleGuardedDocumentDatabase`; default empty/deny-all `Ruleset` registered (overridden by `UseRules`). REST/WS test hosts migrated from `AddDocumentAccessRule` to `UseRules` (catch-all allow-all for CRUD hosts; owner-scoped where auth is asserted). Engine pre-req fixed: recursive `{document=**}` now governs `list` (33 Winche.Rules tests). **Behavior change:** old guard post-filtered lists per-claims (non-Firestore); new guard rejects unconstrained queries (rules-are-not-filters) — `ClaimsIsolation` test redesigned to constrained queries. 699 tests green. Sentinel + old guard still present.
- **Phase 4c ✅ — Winche.Sentinel fully removed. AUTH INTEGRATION COMPLETE.** Deleted the old guard, `GuardedQueryListener`, `DocumentAccessRule`, `AddDocumentAccessRule`, `SentinelRuleClaimsAccessorBridge`, `GuardedDatabaseTests`. Introduced native `Querying/IPathPatternMatcher` + `PathPatternMatcher` (Sentinel's matcher was also used by `IndexScopeResolver` + `HookFeedConsumer`, not just auth). AspNetCore `DocumentClaimsAccessor` is now `AsyncLocal`-based implementing native `IRuleClaimsAccessor` (REST endpoint filter + WS `ConnectionScope.ApplyClaims` set it; singleton-safe, no captive dep). `ErrorMapper` Sentinel arms removed. Both Sentinel package references gone. **Zero `Winche.Sentinel` in source.** 436 unit + 254 integration green (verified). Sample left for user to migrate.

## Product changes (separate from the auth integration)
- **Remove the aggregate pipeline ✅** — deleted `AggregateAsync`, `Pipeline` AST/parser/planner/SQL/executor/serialization, and the REST/WS aggregate routes; `CountAsync`/`QueryAsync` untouched. Zero `Pipeline`/`Aggregate` in source.
- **Add `select` (field projection) ✅** — `Query.Select` (`IReadOnlyList<FieldPath>?`, top-level + nested); **SQL-level** projection via `Querying/Sql/ProjectionSql` (codec-aware nested `jsonb_build_object` with `mapValue`/`fields` wrappers from a prefix tree, `CASE`/`strip_nulls` for absent/non-map paths, ancestor-wins precedence; field names parameterized; tags centralized in `Values/WireTags`). Hooked in `SqlCompiler` (replaces the `data` column); post-fetch `FieldProjector` removed. **The full document never enters app memory** (Postgres returns only the trimmed JSONB; only the DB heap read remains, which would need covering indexes). Round-trips through REST/WS; **orthogonal to authorization** (`QueryToConstraints` ignores `Select`). Behavioral parity proven by the unchanged `QueryProjectionTests`; +11 unit / +8 integration edge-case tests. 407 unit / 236 integration green.
- Final state: **408 unit + 230 integration tests green** (one pre-existing flaky `DurableCursorTests` timing test passes on re-run).

## Orchestration
Opus writes/asks; each phase is dispatched to a **fresh Sonnet subagent** with a self-contained spec (it has no conversation context). Opus reviews each phase against this plan before the next. No `git commit` without explicit ask.
```
