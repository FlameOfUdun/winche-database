namespace Winche.Database.Runtime.History;

/// <summary>
/// Who the writes in the current flow are made by, for the document history (<see cref="DocumentHistory"/>):
/// <code>using (DocumentWriteActor.Use($"console:{email}")) await db.WriteAsync(...);</code>
/// Without it a write is attributed to the caller's user id (their claims' <c>uid</c>, else <c>sub</c>), and a write
/// with neither (a hook, a background job) to no one.
/// </summary>
public static class DocumentWriteActor
{
    private static readonly AsyncLocal<string?> Actor = new();

    /// <summary>The actor set for this flow, or null.</summary>
    public static string? Current => Actor.Value;

    /// <summary>Attributes writes in this flow to <paramref name="actor"/> until disposed.</summary>
    public static IDisposable Use(string? actor)
    {
        var previous = Actor.Value;
        Actor.Value = actor;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            Actor.Value = previous;
        }
    }
}
