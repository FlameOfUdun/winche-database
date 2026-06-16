namespace Winche.Database.Abstraction;

/// <summary>
/// Binds a Firestore-style path pattern to the <see cref="DocumentStoreHook"/> that fires for
/// matching documents. The hook supplies behavior; the path is supplied at registration time
/// (mirroring how <see cref="Documents.IndexDefinition"/> supplies a collection id), so one hook
/// type can be bound to multiple paths.
///
/// <para>The pattern uses the same grammar as the rules engine: literal segments, <c>{id}</c>
/// single-segment captures, and a trailing recursive wildcard <c>{document=**}</c>. Use
/// <c>"{document=**}"</c> to match every document. Bare <c>*</c>/<c>**</c> are not valid.</para>
/// </summary>
/// <param name="Path">The path pattern selecting which documents fire <paramref name="Hook"/>.</param>
/// <param name="Hook">The hook behavior invoked for matching documents.</param>
public sealed record HookRegistration(string Path, DocumentStoreHook Hook);
