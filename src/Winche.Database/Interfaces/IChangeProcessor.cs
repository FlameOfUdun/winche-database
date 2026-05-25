using Winche.Database.Models;

namespace Winche.Database.Interfaces;

public interface IChangeProcessor
{
    Task ProcessAsync(DocumentChange change, CancellationToken ct = default);
}
