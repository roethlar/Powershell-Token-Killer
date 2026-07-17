using System.Text;
using System.Text.Json;

namespace PtkSharedContracts;

public enum PublicHostState
{
    Absent, Backoff, CircuitOpen, ContainmentUnconfirmed, HalfOpen,
    Ready, Recovering, Starting, Stopped,
}

public enum PublicSessionState
{
    Cold, Starting, Ready, Resetting, Closing, Faulted, Lost, Quarantined,
    Recovering, Backoff, Bootstrapping, CircuitOpen, HalfOpen, RecoveryUnknown,
}

public enum DesiredSessionState { Cold, Ready }
public enum BootstrapState { NotApplicable, Pending, Restored, Failed, Unknown }

public sealed record PublicHostStateSnapshot
{
    public PublicHostStateSnapshot(
        HostBootId? bootId,
        HostGeneration? generation,
        PublicHostState state,
        RecoveryPhase? recoveryPhase,
        long recoveryAttempt,
        int? retryAfterMilliseconds,
        bool readyForEffects,
        PublicRecoveryDetailCode? lastFailureCode)
    {
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (recoveryPhase is { } definedPhase && !Enum.IsDefined(definedPhase))
            throw new ArgumentOutOfRangeException(nameof(recoveryPhase));
        if (lastFailureCode is { } definedFailure && !Enum.IsDefined(definedFailure))
            throw new ArgumentOutOfRangeException(nameof(lastFailureCode));
        if (recoveryAttempt < 0) throw new ArgumentOutOfRangeException(nameof(recoveryAttempt));
        ValidateRetry(recoveryPhase, recoveryAttempt, retryAfterMilliseconds);
        var identityRequired = state is PublicHostState.Starting or PublicHostState.Ready or
            PublicHostState.Recovering or PublicHostState.ContainmentUnconfirmed or PublicHostState.HalfOpen;
        if ((bootId is not null) != identityRequired || (generation is not null) != identityRequired)
            throw new ArgumentException("Host identity does not match its state.");
        var expectedPhase = state switch
        {
            PublicHostState.Backoff => global::PtkSharedContracts.RecoveryPhase.Backoff,
            PublicHostState.CircuitOpen => global::PtkSharedContracts.RecoveryPhase.CircuitOpen,
            PublicHostState.HalfOpen => global::PtkSharedContracts.RecoveryPhase.HalfOpen,
            _ => (RecoveryPhase?)null,
        };
        if (expectedPhase is not null && recoveryPhase != expectedPhase)
            throw new ArgumentException("Host recovery phase does not match its state.");
        if (state == PublicHostState.Recovering && recoveryPhase is not (
                global::PtkSharedContracts.RecoveryPhase.Containment or
                global::PtkSharedContracts.RecoveryPhase.Attempting or
                global::PtkSharedContracts.RecoveryPhase.Bootstrap))
            throw new ArgumentException("Recovering host has an invalid phase.");
        if (state == PublicHostState.Starting && recoveryPhase is not (
                null or global::PtkSharedContracts.RecoveryPhase.Attempting))
            throw new ArgumentException("Starting host has an invalid phase.");
        if (state == PublicHostState.Starting && recoveryPhase is null && recoveryAttempt != 0)
            throw new ArgumentException("An initial host start must use recovery attempt zero.");
        if (state is PublicHostState.Absent or PublicHostState.ContainmentUnconfirmed or
            PublicHostState.Ready or PublicHostState.Stopped && recoveryPhase is not null)
            throw new ArgumentException("Nonautomatic host state cannot carry a recovery phase.");
        if (readyForEffects != (state == PublicHostState.Ready))
            throw new ArgumentException("Host readiness does not match its state.");

        BootId = bootId; Generation = generation; State = state; RecoveryPhase = recoveryPhase;
        RecoveryAttempt = recoveryAttempt; RetryAfterMilliseconds = retryAfterMilliseconds;
        ReadyForEffects = readyForEffects; LastFailureCode = lastFailureCode;
    }

    public HostBootId? BootId { get; }
    public HostGeneration? Generation { get; }
    public PublicHostState State { get; }
    public RecoveryPhase? RecoveryPhase { get; }
    public long RecoveryAttempt { get; }
    public int? RetryAfterMilliseconds { get; }
    public bool ReadyForEffects { get; }
    public PublicRecoveryDetailCode? LastFailureCode { get; }

    internal static void ValidateRetry(RecoveryPhase? phase, long attempt, int? retryAfter)
    {
        if (phase is null)
        {
            if (retryAfter is not null) throw new ArgumentException("Retry delay requires a recovery phase.");
            return;
        }
        if (attempt <= 0 || retryAfter is null ||
            retryAfter < ContractLimits.MinimumRetryAfterMilliseconds ||
            retryAfter > ContractLimits.MaximumRetryAfterMilliseconds)
            throw new ArgumentException("Automatic recovery metadata is incomplete or out of bounds.");
    }
}

public sealed record PublicSessionStateSnapshot
{
    public PublicSessionStateSnapshot(
        CanonicalAlias alias,
        DesiredSessionState desiredState,
        PublicSessionState state,
        WorkerBootId? workerBootId,
        WorkerGeneration? generation,
        SessionTransitionVersion transitionVersion,
        RecoveryPhase? recoveryPhase,
        long recoveryAttempt,
        int? retryAfterMilliseconds,
        bool readyForEffects,
        PublicRecoveryDetailCode? lastFailureCode,
        bool warmStateLost,
        BootstrapState bootstrapState)
    {
        ArgumentNullException.ThrowIfNull(alias);
        ArgumentNullException.ThrowIfNull(transitionVersion);
        if (!Enum.IsDefined(desiredState)) throw new ArgumentOutOfRangeException(nameof(desiredState));
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (recoveryPhase is { } definedPhase && !Enum.IsDefined(definedPhase))
            throw new ArgumentOutOfRangeException(nameof(recoveryPhase));
        if (lastFailureCode is { } definedFailure && !Enum.IsDefined(definedFailure))
            throw new ArgumentOutOfRangeException(nameof(lastFailureCode));
        if (!Enum.IsDefined(bootstrapState)) throw new ArgumentOutOfRangeException(nameof(bootstrapState));
        if (recoveryAttempt < 0) throw new ArgumentOutOfRangeException(nameof(recoveryAttempt));
        PublicHostStateSnapshot.ValidateRetry(recoveryPhase, recoveryAttempt, retryAfterMilliseconds);
        if ((workerBootId is null) != (generation is null))
            throw new ArgumentException("Worker identity must be paired.");
        var identityRequired = state is PublicSessionState.Ready or
            PublicSessionState.Bootstrapping or PublicSessionState.Quarantined;
        if (state == PublicSessionState.Cold && workerBootId is not null ||
            identityRequired && workerBootId is null)
            throw new ArgumentException("Worker identity does not match its state.");
        var expectedPhase = state switch
        {
            PublicSessionState.Backoff => global::PtkSharedContracts.RecoveryPhase.Backoff,
            PublicSessionState.Bootstrapping => global::PtkSharedContracts.RecoveryPhase.Bootstrap,
            PublicSessionState.CircuitOpen => global::PtkSharedContracts.RecoveryPhase.CircuitOpen,
            PublicSessionState.HalfOpen => global::PtkSharedContracts.RecoveryPhase.HalfOpen,
            _ => (RecoveryPhase?)null,
        };
        if (expectedPhase is not null && recoveryPhase != expectedPhase)
            throw new ArgumentException("Session recovery phase does not match its state.");
        if (state == PublicSessionState.Recovering && recoveryPhase is not (
                global::PtkSharedContracts.RecoveryPhase.Containment or
                global::PtkSharedContracts.RecoveryPhase.Attempting))
            throw new ArgumentException("Recovering session has an invalid phase.");
        var manual = state is PublicSessionState.Cold or PublicSessionState.Starting or
            PublicSessionState.Ready or PublicSessionState.Resetting or PublicSessionState.Closing or
            PublicSessionState.Faulted or PublicSessionState.Lost or PublicSessionState.Quarantined or
            PublicSessionState.RecoveryUnknown;
        if (manual && recoveryPhase is not null)
            throw new ArgumentException("Nonautomatic session state cannot carry a recovery phase.");
        if (readyForEffects != (state == PublicSessionState.Ready))
            throw new ArgumentException("Session readiness does not match its state.");

        Alias = alias; DesiredState = desiredState; State = state; WorkerBootId = workerBootId;
        Generation = generation; TransitionVersion = transitionVersion; RecoveryPhase = recoveryPhase;
        RecoveryAttempt = recoveryAttempt; RetryAfterMilliseconds = retryAfterMilliseconds;
        ReadyForEffects = readyForEffects; LastFailureCode = lastFailureCode;
        WarmStateLost = warmStateLost; BootstrapState = bootstrapState;
    }

    public CanonicalAlias Alias { get; }
    public DesiredSessionState DesiredState { get; }
    public PublicSessionState State { get; }
    public WorkerBootId? WorkerBootId { get; }
    public WorkerGeneration? Generation { get; }
    public SessionTransitionVersion TransitionVersion { get; }
    public RecoveryPhase? RecoveryPhase { get; }
    public long RecoveryAttempt { get; }
    public int? RetryAfterMilliseconds { get; }
    public bool ReadyForEffects { get; }
    public PublicRecoveryDetailCode? LastFailureCode { get; }
    public bool WarmStateLost { get; }
    public BootstrapState BootstrapState { get; }
}

public sealed record PublicStateSnapshot
{
    public PublicStateSnapshot(
        GuardianBootId guardianBootId,
        PublicHostStateSnapshot host,
        IEnumerable<PublicSessionStateSnapshot> sessions)
    {
        ArgumentNullException.ThrowIfNull(guardianBootId);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(sessions);
        var frozen = sessions.ToArray();
        if (frozen.Any(value => value is null) ||
            frozen.Length > ContractLimits.MaximumAliases ||
            !frozen.Select(value => value.Alias.Value)
                .SequenceEqual(frozen.Select(value => value.Alias.Value).Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            frozen.Select(value => value.Alias.Value).Distinct(StringComparer.Ordinal).Count() != frozen.Length)
            throw new ArgumentException("Session aliases must be bounded, unique, and ordinally ordered.", nameof(sessions));
        GuardianBootId = guardianBootId; Host = host; Sessions = Array.AsReadOnly(frozen);
    }

    public string SchemaVersion => "ptk.public-state/1";
    public GuardianBootId GuardianBootId { get; }
    public PublicHostStateSnapshot Host { get; }
    public IReadOnlyList<PublicSessionStateSnapshot> Sessions { get; }
}

public static class PublicStateCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(PublicStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", snapshot.SchemaVersion);
            writer.WriteString("guardian_boot_id", snapshot.GuardianBootId.ToString());
            writer.WritePropertyName("host");
            WriteHost(writer, snapshot.Host);
            writer.WritePropertyName("sessions");
            writer.WriteStartArray();
            foreach (var session in snapshot.Sessions) WriteSession(writer, session);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static PublicStateSnapshot Decode(ReadOnlyMemory<byte> bytes)
    {
        _ = StrictUtf8.GetString(bytes.Span);
        using var document = JsonDocument.Parse(bytes, StrictJson.DocumentOptions);
        StrictJson.RejectDuplicateProperties(document.RootElement);
        var root = document.RootElement;
        StrictJson.RequirePropertyOrder(root, "schema_version", "guardian_boot_id", "host", "sessions");
        if (root.GetProperty("schema_version").GetString() != "ptk.public-state/1")
            throw new InvalidDataException("Unknown public state version.");
        var guardian = new GuardianBootId(RequiredGuid(root, "guardian_boot_id"));
        var host = ReadHost(root.GetProperty("host"));
        var sessions = root.GetProperty("sessions").EnumerateArray().Select(ReadSession).ToArray();
        return new PublicStateSnapshot(guardian, host, sessions);
    }

    private static void WriteHost(Utf8JsonWriter writer, PublicHostStateSnapshot value)
    {
        writer.WriteStartObject();
        WriteUuid(writer, "boot_id", value.BootId?.Value);
        WriteInt64(writer, "generation", value.Generation?.Value);
        writer.WriteString("state", HostState(value.State));
        WritePhase(writer, value.RecoveryPhase);
        writer.WriteNumber("recovery_attempt", value.RecoveryAttempt);
        WriteInt32(writer, "retry_after_ms", value.RetryAfterMilliseconds);
        writer.WriteBoolean("ready_for_effects", value.ReadyForEffects);
        WriteFailure(writer, value.LastFailureCode);
        writer.WriteEndObject();
    }

    private static void WriteSession(Utf8JsonWriter writer, PublicSessionStateSnapshot value)
    {
        writer.WriteStartObject();
        writer.WriteString("alias", value.Alias.Value);
        writer.WriteString("desired_state", value.DesiredState switch
        {
            DesiredSessionState.Cold => "cold",
            DesiredSessionState.Ready => "ready",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        });
        writer.WriteString("state", SessionState(value.State));
        WriteUuid(writer, "worker_boot_id", value.WorkerBootId?.Value);
        WriteInt64(writer, "generation", value.Generation?.Value);
        writer.WriteNumber("transition_version", value.TransitionVersion.Value);
        WritePhase(writer, value.RecoveryPhase);
        writer.WriteNumber("recovery_attempt", value.RecoveryAttempt);
        WriteInt32(writer, "retry_after_ms", value.RetryAfterMilliseconds);
        writer.WriteBoolean("ready_for_effects", value.ReadyForEffects);
        WriteFailure(writer, value.LastFailureCode);
        writer.WriteBoolean("warm_state_lost", value.WarmStateLost);
        writer.WriteString("bootstrap_state", Bootstrap(value.BootstrapState));
        writer.WriteEndObject();
    }

    private static PublicHostStateSnapshot ReadHost(JsonElement value)
    {
        StrictJson.RequirePropertyOrder(value, "boot_id", "generation", "state", "recovery_phase",
            "recovery_attempt", "retry_after_ms", "ready_for_effects", "last_failure_code");
        var boot = NullableGuid(value.GetProperty("boot_id"));
        var generation = NullableLong(value.GetProperty("generation"));
        return new PublicHostStateSnapshot(
            boot is null ? null : new HostBootId(boot.Value),
            generation is null ? null : new HostGeneration(generation.Value),
            ParseHostState(StrictJson.RequiredString(value, "state")),
            NullablePhase(value.GetProperty("recovery_phase")),
            RequiredLong(value, "recovery_attempt"),
            NullableInt(value.GetProperty("retry_after_ms")),
            value.GetProperty("ready_for_effects").GetBoolean(),
            NullableFailure(value.GetProperty("last_failure_code")));
    }

    private static PublicSessionStateSnapshot ReadSession(JsonElement value)
    {
        StrictJson.RequirePropertyOrder(value, "alias", "desired_state", "state", "worker_boot_id", "generation",
            "transition_version", "recovery_phase", "recovery_attempt", "retry_after_ms", "ready_for_effects",
            "last_failure_code", "warm_state_lost", "bootstrap_state");
        var boot = NullableGuid(value.GetProperty("worker_boot_id"));
        var generation = NullableLong(value.GetProperty("generation"));
        return new PublicSessionStateSnapshot(
            new CanonicalAlias(StrictJson.RequiredString(value, "alias")),
            StrictJson.RequiredString(value, "desired_state") switch
            { "cold" => DesiredSessionState.Cold, "ready" => DesiredSessionState.Ready, _ => throw new InvalidDataException() },
            ParseSessionState(StrictJson.RequiredString(value, "state")),
            boot is null ? null : new WorkerBootId(boot.Value),
            generation is null ? null : new WorkerGeneration(generation.Value),
            new SessionTransitionVersion(RequiredLong(value, "transition_version")),
            NullablePhase(value.GetProperty("recovery_phase")),
            RequiredLong(value, "recovery_attempt"),
            NullableInt(value.GetProperty("retry_after_ms")),
            value.GetProperty("ready_for_effects").GetBoolean(),
            NullableFailure(value.GetProperty("last_failure_code")),
            value.GetProperty("warm_state_lost").GetBoolean(),
            ParseBootstrap(StrictJson.RequiredString(value, "bootstrap_state")));
    }

    private static string HostState(PublicHostState value) => value switch
    {
        PublicHostState.Absent => "absent", PublicHostState.Backoff => "backoff",
        PublicHostState.CircuitOpen => "circuit_open", PublicHostState.ContainmentUnconfirmed => "containment_unconfirmed",
        PublicHostState.HalfOpen => "half_open", PublicHostState.Ready => "ready",
        PublicHostState.Recovering => "recovering", PublicHostState.Starting => "starting",
        PublicHostState.Stopped => "stopped", _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static PublicHostState ParseHostState(string value) => value switch
    {
        "absent" => PublicHostState.Absent, "backoff" => PublicHostState.Backoff,
        "circuit_open" => PublicHostState.CircuitOpen, "containment_unconfirmed" => PublicHostState.ContainmentUnconfirmed,
        "half_open" => PublicHostState.HalfOpen, "ready" => PublicHostState.Ready,
        "recovering" => PublicHostState.Recovering, "starting" => PublicHostState.Starting,
        "stopped" => PublicHostState.Stopped, _ => throw new InvalidDataException("Unknown host state."),
    };

    private static string SessionState(PublicSessionState value) => value switch
    {
        PublicSessionState.Cold => "cold", PublicSessionState.Starting => "starting",
        PublicSessionState.Ready => "ready", PublicSessionState.Resetting => "resetting",
        PublicSessionState.Closing => "closing", PublicSessionState.Faulted => "faulted",
        PublicSessionState.Lost => "lost", PublicSessionState.Quarantined => "quarantined",
        PublicSessionState.Recovering => "recovering", PublicSessionState.Backoff => "backoff",
        PublicSessionState.Bootstrapping => "bootstrapping", PublicSessionState.CircuitOpen => "circuit_open",
        PublicSessionState.HalfOpen => "half_open", PublicSessionState.RecoveryUnknown => "recovery_unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static PublicSessionState ParseSessionState(string value) => Enum.GetValues<PublicSessionState>()
        .SingleOrDefault(candidate => SessionState(candidate) == value) is var parsed && SessionState(parsed) == value
            ? parsed : throw new InvalidDataException("Unknown session state.");

    private static string Bootstrap(BootstrapState value) => value switch
    { BootstrapState.NotApplicable => "not_applicable", BootstrapState.Pending => "pending",
      BootstrapState.Restored => "restored", BootstrapState.Failed => "failed", BootstrapState.Unknown => "unknown",
      _ => throw new ArgumentOutOfRangeException(nameof(value)) };

    private static BootstrapState ParseBootstrap(string value) => value switch
    { "not_applicable" => BootstrapState.NotApplicable, "pending" => BootstrapState.Pending,
      "restored" => BootstrapState.Restored, "failed" => BootstrapState.Failed,
      "unknown" => BootstrapState.Unknown, _ => throw new InvalidDataException("Unknown bootstrap state.") };

    private static void WriteUuid(Utf8JsonWriter writer, string name, Guid? value)
    { if (value is null) writer.WriteNull(name); else writer.WriteString(name, value.Value.ToString("D")); }
    private static void WriteInt64(Utf8JsonWriter writer, string name, long? value)
    { if (value is null) writer.WriteNull(name); else writer.WriteNumber(name, value.Value); }
    private static void WriteInt32(Utf8JsonWriter writer, string name, int? value)
    { if (value is null) writer.WriteNull(name); else writer.WriteNumber(name, value.Value); }
    private static void WritePhase(Utf8JsonWriter writer, RecoveryPhase? value)
    { if (value is null) writer.WriteNull("recovery_phase"); else writer.WriteString("recovery_phase", PublicRecoveryCodec.Phase(value.Value)); }
    private static void WriteFailure(Utf8JsonWriter writer, PublicRecoveryDetailCode? value)
    { if (value is null) writer.WriteNull("last_failure_code"); else writer.WriteString("last_failure_code", PublicRecoveryCodec.DetailCode(value.Value)); }
    private static Guid RequiredGuid(JsonElement root, string name) =>
        ParseCanonicalUuidV4(StrictJson.RequiredString(root, name), name);
    private static Guid? NullableGuid(JsonElement value) => value.ValueKind == JsonValueKind.Null
        ? null
        : ParseCanonicalUuidV4(value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException("Expected nullable UUIDv4."), "UUIDv4");
    private static long RequiredLong(JsonElement root, string name) => root.GetProperty(name).TryGetInt64(out var value) ? value : throw new InvalidDataException();
    private static long? NullableLong(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.TryGetInt64(out var parsed) ? parsed : throw new InvalidDataException();
    private static int? NullableInt(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.TryGetInt32(out var parsed) ? parsed : throw new InvalidDataException();
    private static RecoveryPhase? NullablePhase(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : PublicRecoveryCodec.ParsePhase(value.GetString()!);
    private static PublicRecoveryDetailCode? NullableFailure(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : PublicRecoveryCodec.ParseDetail(value.GetString()!);

    private static Guid ParseCanonicalUuidV4(string text, string name)
    {
        if (!Guid.TryParseExact(text, "D", out var parsed) ||
            !string.Equals(text, parsed.ToString("D"), StringComparison.Ordinal) ||
            text[14] != '4' || text[19] is not ('8' or '9' or 'a' or 'b'))
            throw new InvalidDataException($"'{name}' must be a lowercase canonical UUIDv4.");
        return parsed;
    }
}
