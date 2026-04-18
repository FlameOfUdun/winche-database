using WincheDatabase.Store.Models;

namespace WincheDatabase.Store.Abstraction;

public interface IChangeProcessor
{
    Task ProcessAsync(DocumentChange change, CancellationToken ct = default);
}
