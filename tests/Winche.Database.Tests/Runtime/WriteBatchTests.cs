using Winche.Database.Documents;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.Tests.Runtime;

file sealed class CapturingDb : DatabaseTestDouble
{
    public IReadOnlyList<Write>? Captured;
    public override Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<Write> writes, CancellationToken ct = default)
    {
        Captured = writes;
        return Task.FromResult<IReadOnlyList<WriteResult>>(
            [.. writes.Select(_ => new WriteResult(DateTimeOffset.UnixEpoch))]);
    }
}

public class WriteBatchTests
{
    [Fact]
    public async Task Batch_BuffersInOrder_AndCommitsOnce()
    {
        var db = new CapturingDb();
        var batch = new WriteBatch(db)
            .Set("c/a", new Dictionary<string, Value> { ["x"] = new IntegerValue(1) })
            .Update("c/a", new Dictionary<FieldPath, Value> { [FieldPath.Parse("y")] = new IntegerValue(2) })
            .Delete("c/b");

        Assert.Equal(3, batch.Count);
        var results = await batch.CommitAsync();

        Assert.Equal(3, results.Count);
        Assert.Collection(db.Captured!,
            w => Assert.IsType<SetWrite>(w),
            w => Assert.IsType<UpdateWrite>(w),
            w => Assert.IsType<DeleteWrite>(w));
    }

    [Fact]
    public async Task Batch_PassesOptionsThrough()
    {
        var db = new CapturingDb();
        await new WriteBatch(db)
            .Set("c/a", new Dictionary<string, Value>(), merge: true,
                transforms: [new FieldTransform(FieldPath.Parse("at"), TransformKind.ServerTimestamp)],
                precondition: new Precondition(Exists: true))
            .Delete("c/sub", precondition: null, cascade: true)
            .CommitAsync();

        var set = Assert.IsType<SetWrite>(db.Captured![0]);
        Assert.True(set.Merge);
        Assert.Single(set.Transforms!);
        Assert.True(set.Precondition!.Exists);
        Assert.True(Assert.IsType<DeleteWrite>(db.Captured[1]).Cascade);
    }
}
