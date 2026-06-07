namespace Winche.Database.Querying.Ast;

public enum FilterOperator
{
    Eq, Ne, Gt, Gte, Lt, Lte,
    In, NotIn,
    ArrayContains, ArrayContainsAny,
    // Winche extensions
    ArrayContainsAll, Contains, StartsWith, EndsWith, Regex,
}

public enum CompositeOp { And, Or, Not }

public enum UnaryOp { IsNull, IsNan, Exists }

public enum SortDirection { Asc, Desc }
