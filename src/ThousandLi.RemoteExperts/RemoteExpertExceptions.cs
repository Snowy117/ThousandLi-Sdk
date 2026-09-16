using JetBrains.Annotations;

namespace ThousandLi.RemoteExperts;

/// <summary>
/// Stable error codes carried by platform problem+json responses (<c>code</c> extension field) and
/// by terminal <c>failed</c> SSE frames. These values mirror the platform standalone invocation
/// wire contract one-to-one; unknown future codes surface through the base
/// <see cref="RemoteExpertException.ErrorCode" /> property instead of a dedicated type.
/// </summary>
public static class RemoteExpertErrorCodes
{
    /// <summary>The requested contract triple (version/fingerprint) is incompatible with the platform registration.</summary>
    public const string ContractMismatch = "contract-mismatch";

    /// <summary>The contract id is not registered on the platform.</summary>
    public const string UnknownContract = "unknown-contract";

    /// <summary>The expert package id is not present in the platform package catalog.</summary>
    public const string UnknownPackage = "unknown-package";

    /// <summary>The package exists but the caller is not authorized to use it.</summary>
    public const string PackageNotUsable = "package-not-usable";

    /// <summary>The request input failed contract input-schema validation.</summary>
    public const string Validation = "validation";

    /// <summary>The same (caller, idempotency key) pair is already bound to a different contract/package identity.</summary>
    public const string IdempotencyConflict = "idempotency-conflict";

    /// <summary>The invocation exceeded its execution timeout.</summary>
    public const string Timeout = "timeout";

    /// <summary>The invocation was cancelled by the caller.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The invocation failed with a non-cancellation exception (stable surface for server-internal failures).</summary>
    public const string Internal = "internal";

    /// <summary>The invocation does not exist or belongs to another caller (endpoint-level 404 code).</summary>
    public const string NotFound = "not-found";
}

/// <summary>
/// Base exception for every remote expert invocation failure except caller cancellation, which uses
/// <see cref="RemoteInvocationCancelledException" /> to preserve .NET cancellation semantics. The
/// resolved bearer token is redacted before any error body is embedded in the message.
/// </summary>
public class RemoteExpertException(
    string? message,
    string? errorCode = null,
    int? statusCode = null,
    Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    /// <summary>The stable wire error code, or <see langword="null" /> when the failure is client-local.</summary>
    public string? ErrorCode { get; } = errorCode;

    /// <summary>The HTTP status code of the failing response, when the failure came from HTTP.</summary>
    public int? StatusCode { get; } = statusCode;
}

/// <summary>
/// The requested contract triple does not match the platform registration. Carries the
/// reconciliation data from the platform <c>contract-mismatch</c> problem extensions when present.
/// </summary>
public sealed class RemoteContractMismatchException(
    string message,
    int? statusCode = null,
    string? requiredVersion = null,
    string? requiredFingerprint = null,
    string? availableVersion = null,
    string? availableFingerprint = null,
    Exception? innerException = null)
    : RemoteExpertException(message, RemoteExpertErrorCodes.ContractMismatch, statusCode, innerException)
{
    /// <summary>The version the invocation required, as <c>major.minor</c> (from problem extensions).</summary>
    public string? RequiredVersion { get; } = requiredVersion;

    /// <summary>The fingerprint the invocation required (from problem extensions).</summary>
    public string? RequiredFingerprint { get; } = requiredFingerprint;

    /// <summary>The version the platform has available, as <c>major.minor</c> (from problem extensions).</summary>
    public string? AvailableVersion { get; } = availableVersion;

    /// <summary>The fingerprint the platform has available (from problem extensions).</summary>
    public string? AvailableFingerprint { get; } = availableFingerprint;
}

/// <summary>The requested contract id is not registered on the platform.</summary>
public sealed class RemoteUnknownContractException(
    string message, int? statusCode = null, Exception? innerException = null)
    : RemoteExpertException(message, RemoteExpertErrorCodes.UnknownContract, statusCode, innerException);

/// <summary>The configured expert package id is not present in the platform package catalog.</summary>
public sealed class RemoteUnknownPackageException(
    string message, int? statusCode = null, Exception? innerException = null)
    : RemoteExpertException(message, RemoteExpertErrorCodes.UnknownPackage, statusCode, innerException);

/// <summary>The expert package exists but the caller is not authorized to use it.</summary>
public sealed class RemotePackageNotUsableException(
    string message, int? statusCode = null, Exception? innerException = null)
    : RemoteExpertException(message, RemoteExpertErrorCodes.PackageNotUsable, statusCode, innerException);

/// <summary>The request input failed the contract input-schema validation.</summary>
public sealed class RemoteValidationException(
    string message, int? statusCode = null, Exception? innerException = null)
    : RemoteExpertException(message, RemoteExpertErrorCodes.Validation, statusCode, innerException);

/// <summary>The same (caller, idempotency key) pair is already bound to a different invocation identity.</summary>
public sealed class RemoteIdempotencyConflictException(
    string message, int? statusCode = null, Exception? innerException = null)
    : RemoteExpertException(message, RemoteExpertErrorCodes.IdempotencyConflict, statusCode, innerException);

/// <summary>The remote invocation exceeded its configured timeout.</summary>
public sealed class RemoteInvocationTimeoutException(
    string message, int? statusCode = null, Exception? innerException = null)
    : RemoteExpertException(message, RemoteExpertErrorCodes.Timeout, statusCode, innerException);

/// <summary>The remote invocation terminated with a server-internal failure.</summary>
public sealed class RemoteInternalException(
    string message, int? statusCode = null, Exception? innerException = null)
    : RemoteExpertException(message, RemoteExpertErrorCodes.Internal, statusCode, innerException);

/// <summary>The invocation does not exist (any more) or belongs to another caller.</summary>
public sealed class RemoteInvocationNotFoundException(
    string message, int? statusCode = null, Exception? innerException = null)
    : RemoteExpertException(message, RemoteExpertErrorCodes.NotFound, statusCode, innerException);

/// <summary>
/// The remote endpoint violated the SSE/wire frame contract (malformed frames, non-monotonic event
/// ordinals, unknown frame types). This is a client-side diagnosis without a stable wire code.
/// </summary>
public sealed class RemoteProtocolException(
    string message, Exception? innerException = null)
    : RemoteExpertException(message, errorCode: null, statusCode: null, innerException);

/// <summary>
/// The remote invocation was cancelled. Derives from <see cref="OperationCanceledException" /> so
/// that callers cancelling through the runner's cancellation token observe standard .NET
/// cancellation semantics, while a remote terminal <c>cancelled</c> frame surfaces the same type.
/// </summary>
public sealed class RemoteInvocationCancelledException(
    string message,
    Exception? innerException = null) : OperationCanceledException(message, innerException);

/// <summary>Maps a stable wire error code (plus optional problem payload) onto the typed exception family.</summary>
internal static class RemoteExpertExceptionFactory
{
    public static Exception Create(
        string? errorCode,
        string message,
        int? statusCode = null,
        JsonProblemPayload? problem = null,
        Exception? innerException = null) => errorCode switch
        {
            RemoteExpertErrorCodes.ContractMismatch when problem?.Mismatch is { } mismatch => new RemoteContractMismatchException(
                message, statusCode,
                mismatch.RequiredVersion, mismatch.RequiredFingerprint,
                mismatch.AvailableVersion, mismatch.AvailableFingerprint, innerException),
            RemoteExpertErrorCodes.ContractMismatch => new RemoteContractMismatchException(message, statusCode),
            RemoteExpertErrorCodes.UnknownContract => new RemoteUnknownContractException(message, statusCode, innerException),
            RemoteExpertErrorCodes.UnknownPackage => new RemoteUnknownPackageException(message, statusCode, innerException),
            RemoteExpertErrorCodes.PackageNotUsable => new RemotePackageNotUsableException(message, statusCode, innerException),
            RemoteExpertErrorCodes.Validation => new RemoteValidationException(message, statusCode, innerException),
            RemoteExpertErrorCodes.IdempotencyConflict => new RemoteIdempotencyConflictException(message, statusCode, innerException),
            RemoteExpertErrorCodes.Timeout => new RemoteInvocationTimeoutException(message, statusCode, innerException),
            RemoteExpertErrorCodes.Cancelled => new RemoteInvocationCancelledException(message, innerException),
            RemoteExpertErrorCodes.Internal => new RemoteInternalException(message, statusCode, innerException),
            RemoteExpertErrorCodes.NotFound => new RemoteInvocationNotFoundException(message, statusCode, innerException),
            _ => new RemoteExpertException(message, errorCode, statusCode, innerException)
        };
}

/// <summary>The parsed problem+json payload of an error response.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed record JsonProblemPayload(
    string? Type,
    string? Title,
    int? Status,
    string? Detail,
    string? Code,
    RemoteContractMismatchDetails? Mismatch = null)
{
    public string Describe() =>
        Detail is { Length: > 0 } ? Detail
        : Title is { Length: > 0 } ? Title
        : "the remote platform rejected the request";
}

/// <summary>The <c>contract-mismatch</c> problem extensions: required vs available triples.</summary>
internal sealed record RemoteContractMismatchDetails(
    string? RequiredVersion,
    string? RequiredFingerprint,
    string? AvailableVersion,
    string? AvailableFingerprint)
{
    public static RemoteContractMismatchDetails? TryParse(System.Text.Json.JsonElement extensions)
    {
        if (extensions.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;
        return new RemoteContractMismatchDetails(
            ReadString(extensions, "requiredVersion"),
            ReadString(extensions, "requiredFingerprint"),
            ReadString(extensions, "availableVersion"),
            ReadString(extensions, "availableFingerprint"));
    }

    private static string? ReadString(in System.Text.Json.JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;
}
