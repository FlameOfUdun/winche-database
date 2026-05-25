using Winche.Database.Operands;

namespace Winche.Database.Interfaces;

public interface ITransactionManager
{
    Task<Transaction> BeginAsync(CancellationToken ct = default);
}
