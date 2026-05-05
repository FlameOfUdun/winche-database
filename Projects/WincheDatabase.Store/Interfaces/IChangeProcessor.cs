using WincheDatabase.Store.Models;

namespace WincheDatabase.Store.Interfaces;

public interface IChangeProcessor
{
    Task ProcessAsync(DocumentChange change, CancellationToken ct = default);
}
