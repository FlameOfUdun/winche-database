using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using WincheDatabase.Core.Models;
using WincheDatabase.Store.Constants;
using WincheDatabase.Store.Models;
using WincheDatabase.Store.Operations;
using WincheSentinel.Core.Abstraction;

namespace WincheDatabase.Store.Services;

public sealed class DocumentObjectAccessor(
    IOptions<StoreOptions> options,
    [FromKeyedServices(ServiceKeys.DATA_SOURCE_KEY)] NpgsqlDataSource source 
) : IResourceObjectAccessor<Document>
{
    private readonly string _table = options.Value.TableName;

    public async Task<Document?> GetAsync(string path, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        return await new GetOperation(conn, null, _table).ExecuteAsync(path, ct);
    }
}
