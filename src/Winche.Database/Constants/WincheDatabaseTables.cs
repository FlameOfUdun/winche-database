namespace Winche.Database.Constants;

/// <summary>Fixed object names. Multi-store deployments isolate via the connection's search_path schema.</summary>
public static class WincheDatabaseTables
{
    public const string Documents = "winche_documents";
    public const string Changes = "winche_documents_changes";
    /// <summary>Open optimistic transactions and their read sets (<c>TransactionStore.Database</c>).</summary>
    public const string Transactions = "winche_documents_transactions";
    /// <summary>One row: the newest transaction any pruned feed row came from (resume tokens older than it can't be checked).</summary>
    public const string FeedPruned = "winche_documents_feed_pruned";
    /// <summary>Intentionally equals <see cref="Changes"/> — the change trigger <c>pg_notify()</c>s on the table-named channel.</summary>
    public const string ChangesNotifyChannel = Changes;
}
