using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Database.Constants;
using Winche.Database.Core.Models;
using Winche.Database.Models;
using Winche.Database.Operations;
using WincheSentinel.Interfaces;

namespace Winche.Database.Services;

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
