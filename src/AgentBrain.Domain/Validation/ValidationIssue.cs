namespace AgentBrain.Domain.Validation;

public enum IssueSeverity
{
    Warning,
    Error,
}

/// <summary>Stable machine-readable issue codes. Messages are for humans; tools match on codes.</summary>
public static class IssueCodes
{
    public const string Required = "required";
    public const string UnknownField = "unknown_field";
    public const string DuplicateField = "duplicate_field";
    public const string InvalidType = "invalid_type";
    public const string InvalidValue = "invalid_value";
    public const string TooLong = "too_long";
    public const string TooMany = "too_many";
    public const string NotApplicable = "not_applicable_to_kind";
    public const string InvalidReference = "invalid_reference";
    public const string UnsupportedSchema = "unsupported_schema";
    public const string Inconsistent = "inconsistent";
    public const string FileNameMismatch = "file_name_mismatch";
}

/// <summary>One validation finding, attached to a field path such as <c>applies.packages[0].versions</c>.</summary>
public sealed record ValidationIssue(IssueSeverity Severity, string Code, string Field, string Message, int? Line = null)
{
    public static ValidationIssue Error(string code, string field, string message, int? line = null) => new(IssueSeverity.Error, code, field, message, line);

    public static ValidationIssue Warning(string code, string field, string message, int? line = null) => new(IssueSeverity.Warning, code, field, message, line);

    public override string ToString() => $"{Severity.ToString().ToLowerInvariant()} {Code} at {Field}: {Message}";
}
