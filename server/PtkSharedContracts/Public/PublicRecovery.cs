using System.Text;
using System.Text.Json;

namespace PtkSharedContracts;

public enum PublicRecoveryDetailCode
{
    BackendLostBeforeDispatch,
    HostCircuitOpen,
    HostContainmentUnconfirmed,
    HostContractMismatch,
    HostRecovering,
    HostStartFailed,
    OutcomeUnknown,
    SessionBootstrapFailed,
    SessionRecovering,
    SessionRecoveryUnknown,
}

public enum RecoveryPhase
{
    Attempting,
    Backoff,
    Bootstrap,
    CircuitOpen,
    Containment,
    HalfOpen,
}

public abstract class RetryGate
{
    private protected RetryGate() { }

    public sealed override bool Equals(object? value) =>
        value is RetryGate other && EqualsCore(other);

    public sealed override int GetHashCode() => GetHashCodeCore();

    private protected abstract bool EqualsCore(RetryGate other);
    private protected abstract int GetHashCodeCore();
}

public sealed class HostReadyGate : RetryGate
{
    private protected override bool EqualsCore(RetryGate other) => other is HostReadyGate;
    private protected override int GetHashCodeCore() => typeof(HostReadyGate).GetHashCode();
}

public sealed class SessionReadyGate : RetryGate
{
    public SessionReadyGate(CanonicalAlias alias)
    {
        ArgumentNullException.ThrowIfNull(alias);
        Alias = alias;
    }

    public CanonicalAlias Alias { get; }

    private protected override bool EqualsCore(RetryGate other) =>
        other is SessionReadyGate session && Alias == session.Alias;

    private protected override int GetHashCodeCore() => HashCode.Combine(typeof(SessionReadyGate), Alias);
}

public sealed record PublicRecoveryError
{
    public PublicRecoveryError(
        PublicRecoveryDetailCode detailCode,
        bool retryable,
        int? retryAfterMilliseconds,
        RecoveryPhase? recoveryPhase,
        long? recoveryAttempt,
        RetryGate? retryGate)
    {
        if (!Enum.IsDefined(detailCode))
            throw new ArgumentOutOfRangeException(nameof(detailCode));
        if (recoveryPhase is { } definedPhase && !Enum.IsDefined(definedPhase))
            throw new ArgumentOutOfRangeException(nameof(recoveryPhase));
        if (retryGate is not (null or HostReadyGate or SessionReadyGate))
            throw new ArgumentException("Retry gate is outside the frozen union.", nameof(retryGate));
        var shouldRetry = detailCode is
            PublicRecoveryDetailCode.BackendLostBeforeDispatch or
            PublicRecoveryDetailCode.HostCircuitOpen or
            PublicRecoveryDetailCode.HostRecovering or
            PublicRecoveryDetailCode.SessionRecovering;
        if (retryable != shouldRetry)
            throw new ArgumentException("Retryability does not match the detail code.", nameof(retryable));
        if (!retryable)
        {
            if (retryAfterMilliseconds is not null || recoveryPhase is not null ||
                recoveryAttempt is not null || retryGate is not null)
                throw new ArgumentException("Nonretryable recovery errors cannot carry retry metadata.");
        }
        else
        {
            if (retryAfterMilliseconds is < ContractLimits.MinimumRetryAfterMilliseconds or
                > ContractLimits.MaximumRetryAfterMilliseconds ||
                recoveryPhase is null || recoveryAttempt is null or <= 0 || retryGate is null)
                throw new ArgumentException("Retryable recovery metadata is incomplete or out of bounds.");
            if (detailCode == PublicRecoveryDetailCode.HostCircuitOpen &&
                recoveryPhase != global::PtkSharedContracts.RecoveryPhase.CircuitOpen)
                throw new ArgumentException("host_circuit_open requires circuit_open.");
            if (detailCode == PublicRecoveryDetailCode.HostRecovering &&
                recoveryPhase == global::PtkSharedContracts.RecoveryPhase.CircuitOpen)
                throw new ArgumentException("host_recovering cannot use circuit_open.");
            if (detailCode == PublicRecoveryDetailCode.SessionRecovering && retryGate is not SessionReadyGate)
                throw new ArgumentException("session_recovering requires a session gate.");
        }

        DetailCode = detailCode;
        Retryable = retryable;
        RetryAfterMilliseconds = retryAfterMilliseconds;
        RecoveryPhase = recoveryPhase;
        RecoveryAttempt = recoveryAttempt;
        RetryGate = retryGate;
    }

    public PublicRecoveryDetailCode DetailCode { get; }
    public bool Retryable { get; }
    public int? RetryAfterMilliseconds { get; }
    public RecoveryPhase? RecoveryPhase { get; }
    public long? RecoveryAttempt { get; }
    public RetryGate? RetryGate { get; }
}

public static class PublicRecoveryCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(PublicRecoveryError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("detail_code", DetailCode(error.DetailCode));
            writer.WriteBoolean("retryable", error.Retryable);
            if (error.RetryAfterMilliseconds is { } retryAfter)
                writer.WriteNumber("retry_after_ms", retryAfter);
            else writer.WriteNull("retry_after_ms");
            if (error.RecoveryPhase is { } phase)
                writer.WriteString("recovery_phase", Phase(phase));
            else writer.WriteNull("recovery_phase");
            if (error.RecoveryAttempt is { } attempt)
                writer.WriteNumber("recovery_attempt", attempt);
            else writer.WriteNull("recovery_attempt");
            writer.WritePropertyName("retry_gate");
            switch (error.RetryGate)
            {
                case null:
                    writer.WriteNullValue();
                    break;
                case HostReadyGate:
                    writer.WriteStartObject();
                    writer.WriteString("kind", "host_ready");
                    writer.WriteEndObject();
                    break;
                case SessionReadyGate session:
                    writer.WriteStartObject();
                    writer.WriteString("kind", "session_ready");
                    writer.WriteString("alias", session.Alias.Value);
                    writer.WriteEndObject();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(error));
            }
            writer.WriteEndObject();
        }
        var bytes = stream.ToArray();
        if (bytes.Length is < 2 or > ContractLimits.MaximumPublicRecoveryBytes)
            throw new InvalidDataException("Public recovery error exceeds its frozen bound.");
        return bytes;
    }

    public static PublicRecoveryError Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is < 2 or > ContractLimits.MaximumPublicRecoveryBytes)
            throw new InvalidDataException("Public recovery error is outside its frozen bound.");
        _ = StrictUtf8.GetString(bytes.Span);
        if (bytes.Span.IndexOf((byte)'\r') >= 0 || bytes.Span.IndexOf((byte)'\n') >= 0)
            throw new InvalidDataException("Public recovery error must be compact JSON.");
        using var document = JsonDocument.Parse(bytes, StrictJson.DocumentOptions);
        StrictJson.RejectDuplicateProperties(document.RootElement);
        var root = document.RootElement;
        StrictJson.RequirePropertyOrder(
            root,
            "detail_code", "retryable", "retry_after_ms", "recovery_phase", "recovery_attempt", "retry_gate");
        var detail = ParseDetail(StrictJson.RequiredString(root, "detail_code"));
        var retryable = root.GetProperty("retryable").GetBoolean();
        var retryAfter = NullableInt32(root.GetProperty("retry_after_ms"));
        RecoveryPhase? phase = root.GetProperty("recovery_phase").ValueKind == JsonValueKind.Null
            ? null
            : ParsePhase(root.GetProperty("recovery_phase").GetString()!);
        var attempt = NullableInt64(root.GetProperty("recovery_attempt"));
        var gateValue = root.GetProperty("retry_gate");
        RetryGate? gate = null;
        if (gateValue.ValueKind != JsonValueKind.Null)
        {
            var kind = StrictJson.RequiredString(gateValue, "kind");
            gate = kind switch
            {
                "host_ready" when gateValue.EnumerateObject().Count() == 1 => new HostReadyGate(),
                "session_ready" => ParseSessionGate(gateValue),
                _ => throw new InvalidDataException("Unknown retry gate."),
            };
        }
        var decoded = new PublicRecoveryError(detail, retryable, retryAfter, phase, attempt, gate);
        if (!Encode(decoded).AsSpan().SequenceEqual(bytes.Span))
            throw new InvalidDataException("Public recovery error is not in canonical compact form.");
        return decoded;
    }

    internal static string DetailCode(PublicRecoveryDetailCode value) => value switch
    {
        PublicRecoveryDetailCode.BackendLostBeforeDispatch => "backend_lost_before_dispatch",
        PublicRecoveryDetailCode.HostCircuitOpen => "host_circuit_open",
        PublicRecoveryDetailCode.HostContainmentUnconfirmed => "host_containment_unconfirmed",
        PublicRecoveryDetailCode.HostContractMismatch => "host_contract_mismatch",
        PublicRecoveryDetailCode.HostRecovering => "host_recovering",
        PublicRecoveryDetailCode.HostStartFailed => "host_start_failed",
        PublicRecoveryDetailCode.OutcomeUnknown => "outcome_unknown",
        PublicRecoveryDetailCode.SessionBootstrapFailed => "session_bootstrap_failed",
        PublicRecoveryDetailCode.SessionRecovering => "session_recovering",
        PublicRecoveryDetailCode.SessionRecoveryUnknown => "session_recovery_unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    internal static string Phase(RecoveryPhase value) => value switch
    {
        RecoveryPhase.Attempting => "attempting",
        RecoveryPhase.Backoff => "backoff",
        RecoveryPhase.Bootstrap => "bootstrap",
        RecoveryPhase.CircuitOpen => "circuit_open",
        RecoveryPhase.Containment => "containment",
        RecoveryPhase.HalfOpen => "half_open",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    internal static PublicRecoveryDetailCode ParseDetail(string value) => value switch
    {
        "backend_lost_before_dispatch" => PublicRecoveryDetailCode.BackendLostBeforeDispatch,
        "host_circuit_open" => PublicRecoveryDetailCode.HostCircuitOpen,
        "host_containment_unconfirmed" => PublicRecoveryDetailCode.HostContainmentUnconfirmed,
        "host_contract_mismatch" => PublicRecoveryDetailCode.HostContractMismatch,
        "host_recovering" => PublicRecoveryDetailCode.HostRecovering,
        "host_start_failed" => PublicRecoveryDetailCode.HostStartFailed,
        "outcome_unknown" => PublicRecoveryDetailCode.OutcomeUnknown,
        "session_bootstrap_failed" => PublicRecoveryDetailCode.SessionBootstrapFailed,
        "session_recovering" => PublicRecoveryDetailCode.SessionRecovering,
        "session_recovery_unknown" => PublicRecoveryDetailCode.SessionRecoveryUnknown,
        _ => throw new InvalidDataException("Unknown recovery detail code."),
    };

    internal static RecoveryPhase ParsePhase(string value) => value switch
    {
        "attempting" => RecoveryPhase.Attempting,
        "backoff" => RecoveryPhase.Backoff,
        "bootstrap" => RecoveryPhase.Bootstrap,
        "circuit_open" => RecoveryPhase.CircuitOpen,
        "containment" => RecoveryPhase.Containment,
        "half_open" => RecoveryPhase.HalfOpen,
        _ => throw new InvalidDataException("Unknown recovery phase."),
    };

    private static SessionReadyGate ParseSessionGate(JsonElement value)
    {
        StrictJson.RequirePropertyOrder(value, "kind", "alias");
        return new SessionReadyGate(new CanonicalAlias(StrictJson.RequiredString(value, "alias")));
    }

    private static int? NullableInt32(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed,
        _ => throw new InvalidDataException("Expected nullable int32."),
    };

    private static long? NullableInt64(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.Number when value.TryGetInt64(out var parsed) => parsed,
        _ => throw new InvalidDataException("Expected nullable int64."),
    };
}
