using WincheDatabase.Store.Operands;

namespace WincheDatabase.Store.Abstraction;

public interface ITransactionManager
{
    Task<Transaction> BeginAsync(CancellationToken ct = default);
}
