using Winche.Database.Documents;
using Winche.Database.Runtime.ChangeFeed;

namespace Winche.Database.Runtime.Listening;

/// <summary>Single-document listener snapshot (single-document snapshot contract).</summary>
public sealed record DocumentSnapshot(Document? Document, bool Exists, DateTimeOffset ReadTime, ChangeCursor ResumeToken, bool Current = false);

/// <summary>A live single-document subscription.</summary>
public interface IDocumentListener : ISubscriptionListener<DocumentSnapshot>;
