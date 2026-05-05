using WincheDatabase.Store.Operands;

namespace WincheDatabase.Store.Interfaces;

public interface ITransactionManager
{
    Task<Transaction> BeginAsync(CancellationToken ct = default);
}
