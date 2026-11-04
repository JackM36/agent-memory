using AgentBrain.Domain.Entries;

namespace AgentBrain.Application.Common;

/// <summary>Machine-readable error outcomes shared by every host (plan §7.6).</summary>
public enum ErrorCode
{
    ValidationFailed,
    ConfigInvalid,
    NotFound,
    SectionNotFound,
    RevisionConflict,
    LockTimeout,
    ReadOnly,
    UnsupportedOperation,
    Unauthorized,
    Forbidden,
    SecretDetected,
    BudgetTooSmall,
    ProviderUnavailable,
    ProtocolVersionUnsupported,
    IndexUnavailable,
    Internal,
}

public static class ErrorCodes
{
    public static readonly WireTable<ErrorCode> Names = new(
        (ErrorCode.ValidationFailed, "validation_failed"),
        (ErrorCode.ConfigInvalid, "config_invalid"),
        (ErrorCode.NotFound, "not_found"),
        (ErrorCode.SectionNotFound, "section_not_found"),
        (ErrorCode.RevisionConflict, "revision_conflict"),
        (ErrorCode.LockTimeout, "lock_timeout"),
        (ErrorCode.ReadOnly, "read_only"),
        (ErrorCode.UnsupportedOperation, "unsupported_operation"),
        (ErrorCode.Unauthorized, "unauthorized"),
        (ErrorCode.Forbidden, "forbidden"),
        (ErrorCode.SecretDetected, "secret_detected"),
        (ErrorCode.BudgetTooSmall, "budget_too_small"),
        (ErrorCode.ProviderUnavailable, "provider_unavailable"),
        (ErrorCode.ProtocolVersionUnsupported, "protocol_version_unsupported"),
        (ErrorCode.IndexUnavailable, "index_unavailable"),
        (ErrorCode.Internal, "internal"));
}
