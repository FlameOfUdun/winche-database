using Winche.Database.Core.Models;
using Winche.Sentinel.AspNetCore.Abstraction;

namespace Winche.Database.AspNetCore.Abstraction;

public abstract class DocumentClaimsAccessor : HttpCallerClaimsAccessor<Document>;
