namespace Winche.Database.Runtime.ChangeFeed;

/// <summary>
/// A change-feed cursor: the id of the transaction that produced a row (<see cref="Xid"/>) paired
/// with the intra-transaction <see cref="Seq"/> (see <see cref="ChangeRecord.Seq"/>). Ordering is
/// lexicographic on (Xid, Seq) — the same order the reader uses in SQL via the row-constructor
/// comparison <c>(xid, seq) &gt; ($1, $2)</c> — so a comparison here always agrees with a comparison
/// in the database.
/// <para>
/// <see cref="Xid"/> holds the raw <c>xid8</c> value: an unsigned, non-wrapping 64-bit transaction
/// counter. It is carried as <see cref="ulong"/> (not <see cref="long"/>) precisely so a value above
/// <see cref="long.MaxValue"/> round-trips exactly — see <see cref="ChangeFeedReader"/> for why the
/// value crosses the wire as text rather than a native binary parameter.
/// </para>
/// </summary>
public readonly record struct ChangeCursor(ulong Xid, long Seq) : IComparable<ChangeCursor>
{
    /// <summary>The cursor below any row that can ever exist — a safe "beginning of time" lower bound.</summary>
    public static readonly ChangeCursor Zero = new(0, 0);

    public int CompareTo(ChangeCursor other)
    {
        var xidCompare = Xid.CompareTo(other.Xid);
        return xidCompare != 0 ? xidCompare : Seq.CompareTo(other.Seq);
    }

    public static bool operator <(ChangeCursor left, ChangeCursor right) => left.CompareTo(right) < 0;
    public static bool operator >(ChangeCursor left, ChangeCursor right) => left.CompareTo(right) > 0;
    public static bool operator <=(ChangeCursor left, ChangeCursor right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ChangeCursor left, ChangeCursor right) => left.CompareTo(right) >= 0;

    /// <summary>Wire/persistence form: "{Xid}:{Seq}". Round-trips via <see cref="Parse"/>.</summary>
    public override string ToString() => $"{Xid}:{Seq}";

    public static ChangeCursor Parse(string token) =>
        TryParse(token, out var cursor) ? cursor : throw new FormatException("Invalid change cursor.");

    /// <summary>
    /// Non-throwing parse. The token reaches this from client input, so every rejection — missing
    /// separator, non-numeric part, or a component too large for its type — has to come back as a
    /// plain false rather than as an exception a caller might not expect (an over-range component
    /// overflows rather than mis-formats, and those are two different exception types).
    /// </summary>
    public static bool TryParse(string? token, out ChangeCursor cursor)
    {
        cursor = default;
        if (token is null) return false;

        var i = token.IndexOf(':');
        if (i < 0) return false;
        if (!ulong.TryParse(token.AsSpan(0, i), out var xid)) return false;
        if (!long.TryParse(token.AsSpan(i + 1), out var seq) || seq < 0) return false;

        cursor = new ChangeCursor(xid, seq);
        return true;
    }
}
