# Release Notes — Winche.Database v4

> v4 is a **major release scoped to authorization**. There are no wire-format, storage, or public .NET API signature changes from v3 — but two **behavioral** changes to access control can break existing deployments: aggregation now requires its own grant, and access-rule resolution is now OR / grant-only. No data migration is required.

**Dependency:** requires **Winche.Sentinel 3.0.0** (and `Winche.Sentinel.AspNetCore 3.0.0`).

---

## Breaking Changes

### Aggregation requires the `Aggregate` operation

Aggregation (`POST /documents:aggregate`, the WS `aggregate` message, and `IDocumentDatabase.AggregateAsync`) is now gated by the new **`AccessOperation.Aggregate`** instead of `Read`. The check is performed at the **collection level** on the `collection` of **every `match` and `lookup` stage**, before the pipeline runs, and is **deny-by-default**.

Granting `Read` on a collection no longer authorizes aggregating over it. This closes a confidentiality gap: an aggregate result (`count`/`sum`/`min`/`max`, or `push`/`first`) can reveal information about documents the caller cannot read individually, and per-row filtering cannot secure a scalar. A `lookup` embeds foreign-document fields into the pipeline, so the foreign collection requires its own `Aggregate` grant too.

| v3 | v4 |
| - | - |
| Aggregation gated by `AccessOperation.Read` on each `match`/`lookup` collection | Aggregation gated by `AccessOperation.Aggregate` on each `match`/`lookup` collection |
| `Read` on a collection implied aggregate access | `Read` and `Aggregate` are independent grants |

**Required action:** for every collection you aggregate over (including `lookup` targets), add `AccessOperation.Aggregate` to a matching rule. Without it, aggregation returns `PERMISSION_DENIED`.

```csharp
public override IReadOnlySet<AccessOperation> Operations =>
    new HashSet<AccessOperation> { AccessOperation.Read, AccessOperation.Aggregate };
```

### Access-rule resolution is now OR / grant-only

Via Winche.Sentinel 3.0.0, rule evaluation changed from **first-match-wins** to **OR / grant-only** (Firestore-style):

- A request is allowed if **any** rule whose path pattern and operations set match returns `true`.
- A matching rule that returns `false` **no longer vetoes** — it simply does not grant; evaluation continues to the next matching rule.
- If at least one rule matched but none granted, access is denied (`AccessDeniedException`); if no rule matched the path and operation at all, `NoRulesMatchedException`.
- **Registration order no longer affects the decision.**

| v3 | v4 |
| - | - |
| First matching rule (by registration order) decides; `false` denies and stops | Any matching rule that returns `true` grants; `false` does not veto |
| Order-sensitive | Order-independent |

**Required action:** review any rule set that relied on the old semantics:

- A rule that returned `false` to **deny** a request no longer does so — denial now happens only by the **absence** of a granting rule (default-deny).
- A broad grant (e.g. a `**` rule) can **no longer be narrowed** by a more specific rule. Because a grant cannot be revoked, **grant narrowly**: grant an operation only where it should be allowed (e.g. an owner-scoped read rule), and let default-deny cover the rest. A blanket `** → Read` grant will make per-document read rules ineffective.

---

## No Data Migration Required

v4 changes only authorization behavior. Storage format, wire format, and public .NET API signatures are unchanged from v3.
