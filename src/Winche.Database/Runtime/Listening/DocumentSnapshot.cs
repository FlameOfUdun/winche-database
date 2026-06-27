using Winche.Database.Documents;

namespace Winche.Database.Runtime.Listening;

/// <summary>Single-document listener snapshot (single-document snapshot contract).</summary>
public sealed record DocumentSnapshot(Document? Document, bool Exists, DateTimeOffset ReadTime, long ResumeToken);

/// <summary>A live single-document subscription.</summary>
public interface IDocumentListener : ISubscriptionListener<DocumentSnapshot>;
