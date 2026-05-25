namespace Winche.Database.AST.Models
{
    public enum ConditionalOperator { 
        Eq, 
        Ne, 
        Gt, 
        Gte, 
        Lt, 
        Lte, 
        In, 
        Nin, 
        Contains, 
        StartsWith, 
        EndsWith, 
        Regex, 
        ArrContains, 
        ArrContainsAny, 
        ArrContainsAll, 
        Exists 
    }

    public enum LogicalOperator 
    { 
        And, 
        Or, 
        Not 
    }

    public enum SortDirection 
    { 
        Asc, 
        Desc 
    }

    public enum CursorBound 
    { 
        StartAfter, 
        StartAt, 
        EndBefore, 
        EndAt 
    }

    public enum FieldType 
    { 
        Text, 
        Numeric, 
        Boolean, 
        Timestamp, 
        Integer, 
        BigInt, 
        Double, 
        Date, 
        Uuid, 
        Jsonb 
    }

    public enum AggFunction
    {
        Count,
        Sum,
        Avg,
        Min,
        Max,
        Push,
        AddToSet,
        First,
        Last,
    }
}
