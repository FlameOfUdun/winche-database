namespace Winche.Database.Constants;

/// <summary>Fixed object names. Multi-store deployments isolate via the connection's search_path schema.</summary>
public static class WincheTables
{
    public const string Documents = "winche_documents";
    public const string Changes = "winche_documents_changes";
    public const string FeedCursors = "winche_documents_feed_cursors";
    public const string ChangesNotifyChannel = "winche_documents_changes";
}
