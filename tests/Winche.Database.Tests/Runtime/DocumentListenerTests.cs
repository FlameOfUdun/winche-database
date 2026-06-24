using System.Runtime.CompilerServices;
using Winche.Database.Documents;
using Winche.Database.Runtime.Listening;
using Winche.Database.Values;

namespace Winche.Database.Tests.Runtime;

public class DocumentListenerTests
{
    private sealed class FakeQueryListener(IReadOnlyList<QuerySnapshot> snapshots) : IQueryListener
    {
        public async IAsyncEnumerable<QuerySnapshot> Snapshots([EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var s in snapshots) { yield return s; await Task.Yield(); }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static Document Doc(string path) => new()
    {
        Path = path, Id = path[(path.LastIndexOf('/') + 1)..], Collection = path[..path.LastIndexOf('/')],
        Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(1) },
        CreateTime = DateTimeOffset.UnixEpoch, UpdateTime = DateTimeOffset.UnixEpoch, Version = 1,
    };

    [Fact]
    public async Task Projects_Present_And_Absent_PreservingMetadata()
    {
        var t = DateTimeOffset.UnixEpoch;
        var present = new QuerySnapshot([Doc("c/a")], [], t, 7);
        var absent = new QuerySnapshot([], [], t, 9);
        IDocumentListener listener = new DocumentListener(new FakeQueryListener([present, absent]));

        var results = new List<DocumentSnapshot>();
        await foreach (var d in listener.Snapshots())
            results.Add(d);

        Assert.Equal(2, results.Count);

        Assert.True(results[0].Exists);
        Assert.Equal("c/a", results[0].Document!.Path);
        Assert.Equal(7, results[0].ResumeToken);
        Assert.Equal(t, results[0].ReadTime);

        Assert.False(results[1].Exists);
        Assert.Null(results[1].Document);
        Assert.Equal(9, results[1].ResumeToken);
        Assert.Equal(t, results[1].ReadTime);
    }
}
