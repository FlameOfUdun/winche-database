using Winche.Database.Documents;
using Winche.Sentinel.AspNetCore.Abstraction;

namespace Winche.Database.AspNetCore.Abstraction;

public abstract class DocumentClaimsAccessor : HttpCallerClaimsAccessor<Document>;
