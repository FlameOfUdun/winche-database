using Winche.Database.Models;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Transactions;

namespace Winche.Database.Tests.Runtime;

public class TransactionLedgerTests
{
    private static TransactionLedger Ledger(TimeSpan? idle = null, TimeSpan? total = null) =>
        new(new TransactionConfig
        {
            IdleTimeoutSpan = idle ?? TimeSpan.FromMinutes(1),
            TotalTimeoutSpan = total ?? TimeSpan.FromMinutes(5),
        });

    private static readonly DateTimeOffset T0 = new(2026, 6, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Begin_RecordReads_Consume_RoundTrip()
    {
        var ledger = Ledger();
        var id = ledger.Begin(T0);
        ledger.RecordRead(id, "c/a", T0.AddSeconds(-5), now: T0);
        ledger.RecordRead(id, "c/missing", null, now: T0);

        var readSet = ledger.Consume(id, now: T0);
        Assert.Equal(2, readSet.Count);
        Assert.Equal(T0.AddSeconds(-5), readSet["c/a"]);
        Assert.Null(readSet["c/missing"]);

        // consumed → gone
        var ex = Assert.Throws<TransactionAbortedException>(() => ledger.Consume(id, now: T0));
        Assert.Equal(RuntimeStatus.Aborted, ex.Status);
    }

    [Fact]
    public void RereadSameVersion_Ok_ChangedVersion_Aborts()
    {
        var ledger = Ledger();
        var id = ledger.Begin(T0);
        ledger.RecordRead(id, "c/a", T0, now: T0);
        ledger.RecordRead(id, "c/a", T0, now: T0);                       // identical re-read fine

        Assert.Throws<TransactionAbortedException>(() =>
            ledger.RecordRead(id, "c/a", T0.AddTicks(10), now: T0));     // doc changed mid-transaction
        // entry is dropped after the conflict
        Assert.Throws<TransactionAbortedException>(() => ledger.Consume(id, now: T0));
    }

    [Fact]
    public void UnknownId_Aborts()
    {
        var ledger = Ledger();
        Assert.Throws<TransactionAbortedException>(() => ledger.RecordRead("nope", "c/a", null, now: T0));
        Assert.Throws<TransactionAbortedException>(() => ledger.Consume("nope", now: T0));
    }

    [Fact]
    public void IdleExpiry_Aborts()
    {
        var ledger = Ledger(idle: TimeSpan.FromSeconds(10));
        var id = ledger.Begin(T0);
        ledger.RecordRead(id, "c/a", null, now: T0.AddSeconds(9));        // touch keeps it alive
        ledger.RecordRead(id, "c/a", null, now: T0.AddSeconds(18));       // 9s after last touch — alive
        Assert.Throws<TransactionAbortedException>(() =>
            ledger.Consume(id, now: T0.AddSeconds(29)));                   // 11s idle → expired
    }

    [Fact]
    public void AbsoluteExpiry_Aborts_EvenWhenBusy()
    {
        var ledger = Ledger(idle: TimeSpan.FromMinutes(10), total: TimeSpan.FromSeconds(30));
        var id = ledger.Begin(T0);
        for (var s = 5; s <= 25; s += 5)
            ledger.RecordRead(id, "c/a", null, now: T0.AddSeconds(s));
        Assert.Throws<TransactionAbortedException>(() => ledger.Consume(id, now: T0.AddSeconds(31)));
    }

    [Fact]
    public void Rollback_Idempotent()
    {
        var ledger = Ledger();
        var id = ledger.Begin(T0);
        ledger.Rollback(id);
        ledger.Rollback(id);                                              // no-op, no throw
        ledger.Rollback("never-existed");
        Assert.Throws<TransactionAbortedException>(() => ledger.Consume(id, now: T0));
    }

    [Fact]
    public void RemoveExpired_SweepsOnlyExpired()
    {
        var ledger = Ledger(idle: TimeSpan.FromSeconds(10));
        var dead = ledger.Begin(T0);
        var alive = ledger.Begin(T0.AddSeconds(15));
        Assert.Equal(1, ledger.RemoveExpired(now: T0.AddSeconds(20)));    // 'dead' idled out
        Assert.Throws<TransactionAbortedException>(() => ledger.Consume(dead, now: T0.AddSeconds(20)));
        ledger.Consume(alive, now: T0.AddSeconds(20));                    // still fine
    }
}
