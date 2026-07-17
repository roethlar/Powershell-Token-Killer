using System.Buffers;
using System.Text;
using System.Text.Json;

namespace PtkSharedContracts;

/// <summary>
/// Strict typed codec for the complete frozen guardian/host protocol v1 union.
/// Encoded frames exclude the required transport LF; the reader and writer own
/// that delimiter.
/// </summary>
public static class GuardianHostProtocolCodec
{
    public static byte[] Encode(GuardianHostMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return GuardianHostRawProtocol.Encode(ToRaw(message), message.Sender);
    }

    public static GuardianHostMessage Decode(
        ReadOnlyMemory<byte> encodedFrame,
        GuardianHostPeer sender) =>
        FromRaw(GuardianHostRawProtocol.Decode(encodedFrame, sender));

    internal static GuardianHostRawEnvelope ToRaw(GuardianHostMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message switch
        {
            GuardianHostHello value => Raw(value,
                ("host_pid", value.HostPid),
                ("host_executable_sha256", value.HostExecutableDigest.Value),
                ("host_build_sha256", value.HostBuildDigest.Value),
                ("public_contract_sha256", value.PublicContractDigest.Value),
                ("configuration_sha256", value.ConfigurationDigest.Value),
                ("request_channel_owned", value.RequestChannelOwned),
                ("event_channel_owned", value.EventChannelOwned)),
            GuardianHostInitialize value => Raw(value,
                ("request_id", value.RequestId.Value),
                ("guardian_protocol_version", value.GuardianProtocolVersion),
                ("host_protocol_version", value.HostProtocolVersion),
                ("host_executable_sha256", value.HostExecutableDigest.Value),
                ("host_build_sha256", value.HostBuildDigest.Value),
                ("public_contract_sha256", value.PublicContractDigest.Value),
                ("configuration_sha256", value.ConfigurationDigest.Value),
                ("package_manifest_sha256", value.PackageManifestDigest.Value),
                ("maximum_manifest_bytes", value.MaximumManifestBytes),
                ("maximum_manifest_chunk_raw_bytes", value.MaximumManifestChunkRawBytes),
                ("maximum_aliases", value.MaximumAliases),
                ("maximum_templates", value.MaximumTemplates)),
            GuardianHostReady value => Raw(value,
                ("initialize_request_id", value.InitializeRequestId.Value),
                ("manifest_id", value.ManifestId.Value),
                ("manifest_sha256", value.ManifestDigest.Value),
                ("host_pid", value.HostPid)),
            GuardianHostRequest value => RequestRaw(value),
            GuardianHostCancel value => Raw(value,
                ("request_id", value.RequestId.Value),
                ("target_request_id", value.TargetRequestId.Value),
                ("reason", Wire(value.Reason))),
            GuardianHostEvent value => EventRaw(value),
            GuardianHostResponse value => ResponseRaw(value),
            GuardianHostShutdown value => Raw(value,
                ("request_id", value.RequestId.Value),
                ("deadline_unix_time_milliseconds", value.DeadlineUnixTimeMilliseconds),
                ("reason", Wire(value.Reason))),
            _ => throw new ArgumentException("Message is outside the frozen protocol union.", nameof(message)),
        };
    }

    internal static GuardianHostMessage FromRaw(GuardianHostRawEnvelope raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        try
        {
            return raw.Kind switch
            {
                GuardianHostMessageKind.Hello => DecodeHello(raw),
                GuardianHostMessageKind.Initialize => DecodeInitialize(raw),
                GuardianHostMessageKind.Ready => DecodeReady(raw),
                GuardianHostMessageKind.Request => DecodeRequest(raw),
                GuardianHostMessageKind.Cancel => DecodeCancel(raw),
                GuardianHostMessageKind.Event => DecodeEvent(raw),
                GuardianHostMessageKind.Response => DecodeResponse(raw),
                GuardianHostMessageKind.Shutdown => DecodeShutdown(raw),
                _ => throw InvalidField("kind"),
            };
        }
        catch (GuardianHostProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidOperationException or FormatException or OverflowException)
        {
            throw new GuardianHostProtocolException(
                "invalid_field",
                "Private protocol frame could not be bound to its typed contract.",
                exception);
        }
    }

    private static GuardianHostRawEnvelope Raw(
        GuardianHostMessage message,
        params (string Name, object? Value)[] values) =>
        GuardianHostRawProtocol.Create(
            message.Kind,
            message.GuardianBootId.Value,
            message.HostBootId.Value,
            message.HostGeneration.Value,
            values);

    private static GuardianHostRawEnvelope RequestRaw(GuardianHostRequest value) => Raw(value,
        ("request_id", value.RequestId.Value),
        ("method", Wire(value.Method)),
        ("deadline_unix_time_milliseconds", value.DeadlineUnixTimeMilliseconds),
        ("session_alias", value.SessionAlias?.Value),
        ("session_transition_version", value.SessionTransitionVersion?.Value),
        ("worker_boot_id", value.WorkerIdentity?.BootId.Value),
        ("worker_generation", value is WorkerCreateCapabilityGrantRequest grant
            ? grant.WorkerGeneration.Value
            : value.WorkerIdentity?.Generation.Value),
        ("plan_id", value.OperationIdentity?.PlanId.Value),
        ("operation_id", value.OperationIdentity?.OperationId.Value),
        ("payload", RequestPayload(value)));

    private static JsonElement RequestPayload(GuardianHostRequest value) => value switch
    {
        ManifestHeaderRequest item => Object(
            ("manifest_id", item.ManifestId.Value),
            ("total_bytes", item.TotalBytes),
            ("chunk_count", item.ChunkCount),
            ("manifest_sha256", item.ManifestDigest.Value),
            ("alias_count", item.AliasCount),
            ("template_count", item.TemplateCount)),
        ManifestChunkRequest item => RawChunk(
            item.ManifestId.Value,
            item.ChunkIndex,
            item.Offset,
            item.RawByteCount,
            item.RawSpan,
            item.RawDigest),
        ManifestSealRequest item => Object(
            ("manifest_id", item.ManifestId.Value),
            ("total_bytes", item.TotalBytes),
            ("chunk_count", item.ChunkCount),
            ("manifest_sha256", item.ManifestDigest.Value)),
        OperationRequest item => OperationPayload(item.Operation),
        WorkerCreateCapabilityGrantRequest item => Object(
            ("source_event_sequence", item.SourceEventSequence.Value),
            ("token", item.Token.Value),
            ("worker_generation", item.GrantedWorkerGeneration.Value)),
        WorkerContainmentAckRequest item => Object(
            ("source_event_sequence", item.SourceEventSequence.Value)),
        _ => throw new ArgumentException("Request is outside the frozen method union.", nameof(value)),
    };

    private static JsonElement OperationPayload(GuardianHostOperation operation) => Object(
        ("operation", Wire(operation.Kind)),
        ("call_id", operation.CallId.Value),
        ("dispatch_capability", Object(
            ("token", operation.DispatchCapability.Token.Value),
            ("call_id", operation.DispatchCapability.CallId.Value),
            ("expires_unix_time_milliseconds", operation.DispatchCapability.ExpiresUnixTimeMilliseconds))),
        ("output_capability", operation.OutputCapability is null
            ? null
            : Object(
                ("token", operation.OutputCapability.Token.Value),
                ("maximum_bytes", operation.OutputCapability.MaximumBytes),
                ("expires_unix_time_milliseconds", operation.OutputCapability.ExpiresUnixTimeMilliseconds))),
        ("arguments", OperationArguments(operation)));

    private static JsonElement OperationArguments(GuardianHostOperation operation) => operation switch
    {
        InvokeForegroundOperation item => Object(
            ("script", item.Script), ("raw", item.Raw), ("route", Wire(item.Route))),
        InvokeBackgroundOperation item => Object(
            ("script", item.Script), ("raw", item.Raw), ("route", Wire(item.Route)),
            ("public_job_id", item.PublicJobId.Value)),
        JobListOperation => Object(),
        JobStatusOperation item => JobIdentityArguments(item),
        JobOutputOperation item => Object(
            ("public_job_id", item.PublicJobId.Value),
            ("job_capability", item.JobCapability.Value),
            ("offset", item.Offset)),
        JobKillOperation item => JobIdentityArguments(item),
        ResetOperation item => GenerationArguments(item),
        SessionOpenOperation item => Object(
            ("template", item.Template?.Value),
            ("allowColdBackground", item.AllowColdBackground)),
        SessionCloseOperation item => GenerationArguments(item),
        SessionRestartOperation item => GenerationArguments(item),
        _ => throw new ArgumentException("Operation is outside the frozen union.", nameof(operation)),
    };

    private static JsonElement JobIdentityArguments(GuardianHostJobIdentityOperation item) => Object(
        ("public_job_id", item.PublicJobId.Value),
        ("job_capability", item.JobCapability.Value));

    private static JsonElement GenerationArguments(GuardianHostGenerationOperation item) => Object(
        ("expectedGeneration", item.ExpectedGeneration),
        ("force", item.Force));

    private static GuardianHostRawEnvelope EventRaw(GuardianHostEvent value) => Raw(value,
        ("event_sequence", value.EventSequence.Value),
        ("event_type", Wire(value.EventType)),
        ("request_id", value.RequestId?.Value),
        ("session_alias", value.SessionAlias.Value),
        ("session_transition_version", value.SessionTransitionVersion.Value),
        ("worker_boot_id", value.WorkerIdentity?.BootId.Value),
        ("worker_generation", value.WorkerIdentity?.Generation.Value),
        ("plan_id", value.OperationIdentity?.PlanId.Value),
        ("operation_id", value.OperationIdentity?.OperationId.Value),
        ("payload", EventPayload(value)));

    private static JsonElement EventPayload(GuardianHostEvent value) => value switch
    {
        WorkerCreateCapabilityRequestedEvent item => Object(
            ("binding_digest", item.BindingDigest.Value),
            ("startup_deadline_unix_time_milliseconds", item.StartupDeadlineUnixTimeMilliseconds)),
        WorkerContainmentPendingEvent item => ContainmentPayload(item.ContainmentIdentity, pending: true),
        WorkerContainmentArmedEvent item => ContainmentPayload(item.ContainmentIdentity, pending: false),
        WorkerContainmentRemoveRequestedEvent item => ContainmentPayload(item.ContainmentIdentity, pending: false),
        OperationDeliveryEvent item => Object(
            ("dispatch_token", item.DispatchToken.Value),
            ("delivery_state", Wire(item.DeliveryState)),
            ("worker_request_id", item.WorkerRequestId?.Value)),
        SessionLifecycleEvent item => Object(
            ("previous_state", item.PreviousState is { } previous ? Wire(previous) : null),
            ("state", Wire(item.State)),
            ("reason_code", Wire(item.Reason)),
            ("ready_for_effects", item.ReadyForEffects),
            ("warm_state_lost", item.WarmStateLost),
            ("bootstrap_state", Wire(item.BootstrapState))),
        WorkerLostEvent item => Object(
            ("reason_code", Wire(item.Reason)),
            ("exit_code", item.ExitCode),
            ("termination_certainty", Wire(item.TerminationCertainty)),
            ("effects_state", Wire(item.EffectsState))),
        WorkerDiagnosticChunkEvent item => Object(
            ("stream", Wire(item.Stream)),
            ("chunk_index", item.ChunkIndex),
            ("offset", item.Offset),
            ("raw_bytes", item.RawByteCount),
            ("raw_base64", Convert.ToBase64String(item.RawSpan)),
            ("raw_sha256", item.RawDigest.Value),
            ("end_of_stream", item.EndOfStream)),
        WorkerDiagnosticTruncatedEvent item => Object(
            ("stream", Wire(item.Stream)),
            ("captured_bytes", item.CapturedBytes),
            ("discarded_bytes", item.DiscardedBytes)),
        JobLifecycleEvent item => Object(
            ("public_job_id", item.PublicJobId.Value),
            ("state", Wire(item.State)),
            ("exit_code", item.ExitCode),
            ("output_state", Wire(item.OutputState)),
            ("output_bytes", item.OutputBytes),
            ("output_sha256", item.OutputDigest?.Value)),
        OutputChunkEvent item => Object(
            ("output_capability_token", item.OutputCapabilityToken.Value),
            ("chunk_index", item.ChunkIndex),
            ("offset", item.Offset),
            ("raw_bytes", item.RawByteCount),
            ("raw_base64", Convert.ToBase64String(item.RawSpan)),
            ("raw_sha256", item.RawDigest.Value)),
        OutputSealEvent item => Object(
            ("output_capability_token", item.OutputCapabilityToken.Value),
            ("state", Wire(item.State)),
            ("total_bytes", item.TotalBytes),
            ("output_sha256", item.OutputDigest.Value)),
        _ => throw new ArgumentException("Event is outside the frozen union.", nameof(value)),
    };

    private static JsonElement ContainmentPayload(
        GuardianHostContainmentIdentity identity,
        bool pending) => Object(
        ("broker_pid", identity.BrokerPid),
        ("broker_start_identity_high", identity.BrokerStartIdentityHigh),
        ("broker_start_identity_low", identity.BrokerStartIdentityLow),
        ("worker_pid", identity.WorkerPid),
        ("worker_start_identity_high", identity.WorkerStartIdentityHigh),
        ("worker_start_identity_low", identity.WorkerStartIdentityLow),
        (pending ? "intended_pgid" : "pgid", identity.ProcessGroupId));

    private static GuardianHostRawEnvelope ResponseRaw(GuardianHostResponse value) => value switch
    {
        GuardianHostSuccessResponse item => Raw(item,
            ("request_id", item.RequestId.Value),
            ("status", "ok"),
            ("payload", SuccessPayload(item.Payload)),
            ("error", null)),
        GuardianHostErrorResponse item => Raw(item,
            ("request_id", item.RequestId.Value),
            ("status", "error"),
            ("payload", null),
            ("error", Object(
                ("detail_code", Wire(item.Error.DetailCode)),
                ("message_code", Wire(item.Error.MessageCode))))),
        _ => throw new ArgumentException("Response is outside the frozen union.", nameof(value)),
    };

    private static JsonElement SuccessPayload(GuardianHostSuccessPayload value) => value switch
    {
        ManifestHeaderAccepted item => Object(
            ("response_type", Wire(item.ResponseType)),
            ("manifest_id", item.ManifestId.Value),
            ("next_chunk_index", item.NextChunkIndex),
            ("next_offset", item.NextOffset)),
        ManifestChunkAccepted item => Object(
            ("response_type", Wire(item.ResponseType)),
            ("manifest_id", item.ManifestId.Value),
            ("chunk_index", item.ChunkIndex),
            ("next_chunk_index", item.NextChunkIndex),
            ("next_offset", item.NextOffset)),
        ManifestSealed item => Object(
            ("response_type", Wire(item.ResponseType)),
            ("manifest_id", item.ManifestId.Value),
            ("manifest_sha256", item.ManifestDigest.Value),
            ("total_bytes", item.TotalBytes)),
        OperationCompleted item => Object(
            ("response_type", Wire(item.ResponseType)),
            ("operation", Wire(item.OperationKind)),
            ("result", OperationResult(item.Result))),
        ControlAcknowledged item => Object(
            ("response_type", Wire(item.ResponseType)),
            ("source_event_sequence", item.SourceEventSequence.Value)),
        ShutdownAccepted item => Object(("response_type", Wire(item.ResponseType))),
        _ => throw new ArgumentException("Success payload is outside the frozen union.", nameof(value)),
    };

    private static JsonElement OperationResult(GuardianHostOperationResult value) => value switch
    {
        GuardianHostTextOperationResult item => Object(("text", item.Text)),
        InvokeBackgroundResult item => Object(
            ("public_job_id", item.PublicJobId.Value),
            ("job_capability", item.JobCapability.Value)),
        GuardianHostSessionOperationResult item => Object(
            ("alias", item.Alias.Value),
            ("state", Wire(item.State)),
            ("worker_boot_id", item.WorkerIdentity?.BootId.Value),
            ("worker_generation", item.WorkerIdentity?.Generation.Value),
            ("transition_version", item.TransitionVersion.Value),
            ("ready_for_effects", item.ReadyForEffects),
            ("warm_state_lost", item.WarmStateLost),
            ("bootstrap_state", Wire(item.BootstrapState))),
        _ => throw new ArgumentException("Operation result is outside the frozen union.", nameof(value)),
    };

    private static JsonElement RawChunk(
        Guid manifestId,
        int chunkIndex,
        int offset,
        int rawByteCount,
        ReadOnlySpan<byte> rawBytes,
        Sha256Digest digest) => Object(
        ("manifest_id", manifestId),
        ("chunk_index", chunkIndex),
        ("offset", offset),
        ("raw_bytes", rawByteCount),
        ("raw_base64", Convert.ToBase64String(rawBytes)),
        ("raw_sha256", digest.Value));

    private static JsonElement Object(params (string Name, object? Value)[] values)
    {
        var fields = new Dictionary<string, object?>(values.Length, StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            if (!fields.TryAdd(name, value))
                throw new ArgumentException($"Duplicate JSON field '{name}'.", nameof(values));
        }
        return JsonSerializer.SerializeToElement(fields);
    }

    private static string Wire<T>(T value) where T : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
        var name = value.ToString();
        var builder = new StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (char.IsUpper(character) && index > 0) builder.Append('_');
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private static GuardianHostProtocolException InvalidField(string name) =>
        new("invalid_field", $"Private protocol field '{name}' is invalid.");

    private static GuardianHostHello DecodeHello(GuardianHostRawEnvelope raw) => new(
        GuardianId(raw), HostId(raw), HostGenerationValue(raw),
        Int32(raw.Value("host_pid")),
        Digest(raw.Value("host_executable_sha256")),
        Digest(raw.Value("host_build_sha256")),
        Digest(raw.Value("public_contract_sha256")),
        Digest(raw.Value("configuration_sha256")));

    private static GuardianHostInitialize DecodeInitialize(GuardianHostRawEnvelope raw) => new(
        GuardianId(raw), HostId(raw), HostGenerationValue(raw),
        RequestId(raw.Value("request_id")),
        Digest(raw.Value("host_executable_sha256")),
        Digest(raw.Value("host_build_sha256")),
        Digest(raw.Value("public_contract_sha256")),
        Digest(raw.Value("configuration_sha256")),
        Digest(raw.Value("package_manifest_sha256")));

    private static GuardianHostReady DecodeReady(GuardianHostRawEnvelope raw) => new(
        GuardianId(raw), HostId(raw), HostGenerationValue(raw),
        RequestId(raw.Value("initialize_request_id")),
        Manifest(raw.Value("manifest_id")),
        Digest(raw.Value("manifest_sha256")),
        Int32(raw.Value("host_pid")));

    private static GuardianHostCancel DecodeCancel(GuardianHostRawEnvelope raw) => new(
        GuardianId(raw), HostId(raw), HostGenerationValue(raw),
        RequestId(raw.Value("request_id")),
        RequestId(raw.Value("target_request_id")),
        Parse<GuardianHostCancelReason>(raw.Value("reason"), "reason"));

    private static GuardianHostShutdown DecodeShutdown(GuardianHostRawEnvelope raw) => new(
        GuardianId(raw), HostId(raw), HostGenerationValue(raw),
        RequestId(raw.Value("request_id")),
        Int64(raw.Value("deadline_unix_time_milliseconds")),
        Parse<GuardianHostShutdownReason>(raw.Value("reason"), "reason"));

    private static GuardianHostRequest DecodeRequest(GuardianHostRawEnvelope raw)
    {
        var guardian = GuardianId(raw);
        var host = HostId(raw);
        var hostGeneration = HostGenerationValue(raw);
        var requestId = RequestId(raw.Value("request_id"));
        var payload = raw.Value("payload");
        var method = Parse<GuardianHostRequestMethod>(raw.Value("method"), "method");

        return method switch
        {
            GuardianHostRequestMethod.ManifestHeader => new ManifestHeaderRequest(
                guardian, host, hostGeneration, requestId,
                Manifest(payload.GetProperty("manifest_id")),
                Int32(payload.GetProperty("total_bytes")),
                Digest(payload.GetProperty("manifest_sha256")),
                Int32(payload.GetProperty("alias_count")),
                Int32(payload.GetProperty("template_count"))),
            GuardianHostRequestMethod.ManifestChunk => new ManifestChunkRequest(
                guardian, host, hostGeneration, requestId,
                Manifest(payload.GetProperty("manifest_id")),
                Int32(payload.GetProperty("chunk_index")),
                RawBytes(payload)),
            GuardianHostRequestMethod.ManifestSeal => new ManifestSealRequest(
                guardian, host, hostGeneration, requestId,
                Manifest(payload.GetProperty("manifest_id")),
                Int32(payload.GetProperty("total_bytes")),
                Digest(payload.GetProperty("manifest_sha256"))),
            GuardianHostRequestMethod.Operation => new OperationRequest(
                guardian, host, hostGeneration, requestId,
                Int64(raw.Value("deadline_unix_time_milliseconds")),
                Alias(raw.Value("session_alias")),
                Transition(raw.Value("session_transition_version")),
                NullableWorker(raw),
                NullableOperationIdentity(raw),
                DecodeOperation(payload)),
            GuardianHostRequestMethod.WorkerCreateCapabilityGrant =>
                DecodeWorkerCreateCapabilityGrant(raw, payload),
            GuardianHostRequestMethod.WorkerContainmentPendingAck =>
                DecodeContainmentAck<WorkerContainmentPendingAckRequest>(raw, payload,
                    static (g, h, hg, r, d, a, t, w, s) =>
                        new WorkerContainmentPendingAckRequest(g, h, hg, r, d, a, t, w, s)),
            GuardianHostRequestMethod.WorkerContainmentArmedAck =>
                DecodeContainmentAck<WorkerContainmentArmedAckRequest>(raw, payload,
                    static (g, h, hg, r, d, a, t, w, s) =>
                        new WorkerContainmentArmedAckRequest(g, h, hg, r, d, a, t, w, s)),
            GuardianHostRequestMethod.WorkerContainmentRemoveAck =>
                DecodeContainmentAck<WorkerContainmentRemoveAckRequest>(raw, payload,
                    static (g, h, hg, r, d, a, t, w, s) =>
                        new WorkerContainmentRemoveAckRequest(g, h, hg, r, d, a, t, w, s)),
            _ => throw InvalidField("method"),
        };
    }

    private delegate T ContainmentAckFactory<T>(
        GuardianBootId guardianBootId,
        HostBootId hostBootId,
        HostGeneration hostGeneration,
        PrivateRequestId requestId,
        long deadline,
        CanonicalAlias alias,
        SessionTransitionVersion transition,
        GuardianHostWorkerIdentity worker,
        HostEventSequence sourceSequence)
        where T : WorkerContainmentAckRequest;

    private static WorkerCreateCapabilityGrantRequest DecodeWorkerCreateCapabilityGrant(
        GuardianHostRawEnvelope raw,
        JsonElement payload)
    {
        var envelopeGeneration = Int64(raw.Value("worker_generation"));
        var grantedGeneration = Int64(payload.GetProperty("worker_generation"));
        if (envelopeGeneration != grantedGeneration) throw InvalidField("worker_generation");
        return new WorkerCreateCapabilityGrantRequest(
            GuardianId(raw), HostId(raw), HostGenerationValue(raw),
            RequestId(raw.Value("request_id")),
            Int64(raw.Value("deadline_unix_time_milliseconds")),
            Alias(raw.Value("session_alias")),
            Transition(raw.Value("session_transition_version")),
            new WorkerGeneration(envelopeGeneration),
            new HostEventSequence(Int64(payload.GetProperty("source_event_sequence"))),
            Token(payload.GetProperty("token")));
    }

    private static T DecodeContainmentAck<T>(
        GuardianHostRawEnvelope raw,
        JsonElement payload,
        ContainmentAckFactory<T> factory)
        where T : WorkerContainmentAckRequest => factory(
            GuardianId(raw), HostId(raw), HostGenerationValue(raw),
            RequestId(raw.Value("request_id")),
            Int64(raw.Value("deadline_unix_time_milliseconds")),
            Alias(raw.Value("session_alias")),
            Transition(raw.Value("session_transition_version")),
            RequiredWorker(raw),
            new HostEventSequence(Int64(payload.GetProperty("source_event_sequence"))));

    private static GuardianHostOperation DecodeOperation(JsonElement payload)
    {
        var kind = Parse<GuardianHostOperationKind>(payload.GetProperty("operation"), "operation");
        var callId = new CallId(GuidValue(payload.GetProperty("call_id")));
        var dispatch = DecodeDispatchCapability(payload.GetProperty("dispatch_capability"));
        var output = NullableOutputCapability(payload.GetProperty("output_capability"));
        var arguments = payload.GetProperty("arguments");
        return kind switch
        {
            GuardianHostOperationKind.InvokeForeground => new InvokeForegroundOperation(
                callId, dispatch, Required(output, "output_capability"),
                Text(arguments.GetProperty("script")),
                Boolean(arguments.GetProperty("raw")),
                Parse<GuardianHostInvokeRoute>(arguments.GetProperty("route"), "route")),
            GuardianHostOperationKind.InvokeBackground => new InvokeBackgroundOperation(
                callId, dispatch, Required(output, "output_capability"),
                Text(arguments.GetProperty("script")),
                Boolean(arguments.GetProperty("raw")),
                Parse<GuardianHostInvokeRoute>(arguments.GetProperty("route"), "route"),
                new PublicJobId(Int64(arguments.GetProperty("public_job_id")))),
            GuardianHostOperationKind.JobList => new JobListOperation(callId, dispatch),
            GuardianHostOperationKind.JobStatus => new JobStatusOperation(
                callId, dispatch,
                new PublicJobId(Int64(arguments.GetProperty("public_job_id"))),
                Token(arguments.GetProperty("job_capability"))),
            GuardianHostOperationKind.JobOutput => new JobOutputOperation(
                callId, dispatch, Required(output, "output_capability"),
                new PublicJobId(Int64(arguments.GetProperty("public_job_id"))),
                Token(arguments.GetProperty("job_capability")),
                Int64(arguments.GetProperty("offset"))),
            GuardianHostOperationKind.JobKill => new JobKillOperation(
                callId, dispatch,
                new PublicJobId(Int64(arguments.GetProperty("public_job_id"))),
                Token(arguments.GetProperty("job_capability"))),
            GuardianHostOperationKind.Reset => new ResetOperation(
                callId, dispatch,
                Int64(arguments.GetProperty("expectedGeneration")),
                Boolean(arguments.GetProperty("force"))),
            GuardianHostOperationKind.SessionOpen => new SessionOpenOperation(
                callId, dispatch,
                NullableAlias(arguments.GetProperty("template")),
                Boolean(arguments.GetProperty("allowColdBackground"))),
            GuardianHostOperationKind.SessionClose => new SessionCloseOperation(
                callId, dispatch,
                Int64(arguments.GetProperty("expectedGeneration")),
                Boolean(arguments.GetProperty("force"))),
            GuardianHostOperationKind.SessionRestart => new SessionRestartOperation(
                callId, dispatch,
                Int64(arguments.GetProperty("expectedGeneration")),
                Boolean(arguments.GetProperty("force"))),
            _ => throw InvalidField("operation"),
        };
    }

    private static DispatchCapability DecodeDispatchCapability(JsonElement value) => new(
        Token(value.GetProperty("token")),
        new CallId(GuidValue(value.GetProperty("call_id"))),
        Int64(value.GetProperty("expires_unix_time_milliseconds")));

    private static OutputCapability? NullableOutputCapability(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null
            ? null
            : new OutputCapability(
                Token(value.GetProperty("token")),
                Int32(value.GetProperty("maximum_bytes")),
                Int64(value.GetProperty("expires_unix_time_milliseconds")));

    private static GuardianHostEvent DecodeEvent(GuardianHostRawEnvelope raw)
    {
        var guardian = GuardianId(raw);
        var host = HostId(raw);
        var hostGeneration = HostGenerationValue(raw);
        var sequence = new HostEventSequence(Int64(raw.Value("event_sequence")));
        var requestId = NullableRequestId(raw.Value("request_id"));
        var alias = Alias(raw.Value("session_alias"));
        var transition = Transition(raw.Value("session_transition_version"));
        var worker = NullableWorker(raw);
        var operation = NullableOperationIdentity(raw);
        var payload = raw.Value("payload");
        var eventType = Parse<GuardianHostEventType>(raw.Value("event_type"), "event_type");

        return eventType switch
        {
            GuardianHostEventType.WorkerCreateCapabilityRequested =>
                new WorkerCreateCapabilityRequestedEvent(
                    guardian, host, hostGeneration, sequence, alias, transition,
                    Digest(payload.GetProperty("binding_digest")),
                    Int64(payload.GetProperty("startup_deadline_unix_time_milliseconds"))),
            GuardianHostEventType.WorkerContainmentPending => new WorkerContainmentPendingEvent(
                guardian, host, hostGeneration, sequence, alias, transition,
                Required(worker, "worker_boot_id"), DecodeContainment(payload)),
            GuardianHostEventType.WorkerContainmentArmed => new WorkerContainmentArmedEvent(
                guardian, host, hostGeneration, sequence, alias, transition,
                Required(worker, "worker_boot_id"), DecodeContainment(payload)),
            GuardianHostEventType.WorkerContainmentRemoveRequested =>
                new WorkerContainmentRemoveRequestedEvent(
                    guardian, host, hostGeneration, sequence, alias, transition,
                    Required(worker, "worker_boot_id"), DecodeContainment(payload)),
            GuardianHostEventType.OperationDelivery => new OperationDeliveryEvent(
                guardian, host, hostGeneration, sequence,
                Required(requestId, "request_id"), alias, transition,
                Required(worker, "worker_boot_id"), operation,
                Token(payload.GetProperty("dispatch_token")),
                Parse<GuardianHostDeliveryState>(payload.GetProperty("delivery_state"), "delivery_state"),
                NullableRequestId(payload.GetProperty("worker_request_id"))),
            GuardianHostEventType.SessionLifecycle => new SessionLifecycleEvent(
                guardian, host, hostGeneration, sequence, requestId, alias, transition, worker,
                NullableEnum<PublicSessionState>(payload.GetProperty("previous_state"), "previous_state"),
                Parse<PublicSessionState>(payload.GetProperty("state"), "state"),
                Parse<GuardianHostSessionLifecycleReason>(payload.GetProperty("reason_code"), "reason_code"),
                Boolean(payload.GetProperty("ready_for_effects")),
                Boolean(payload.GetProperty("warm_state_lost")),
                Parse<BootstrapState>(payload.GetProperty("bootstrap_state"), "bootstrap_state")),
            GuardianHostEventType.WorkerLost => new WorkerLostEvent(
                guardian, host, hostGeneration, sequence, requestId, alias, transition,
                Required(worker, "worker_boot_id"), operation,
                Parse<GuardianHostWorkerLostReason>(payload.GetProperty("reason_code"), "reason_code"),
                NullableInt32(payload.GetProperty("exit_code")),
                Parse<GuardianHostTerminationCertainty>(payload.GetProperty("termination_certainty"), "termination_certainty"),
                Parse<GuardianHostEffectsState>(payload.GetProperty("effects_state"), "effects_state")),
            GuardianHostEventType.WorkerDiagnosticChunk => new WorkerDiagnosticChunkEvent(
                guardian, host, hostGeneration, sequence, requestId, alias, transition,
                Required(worker, "worker_boot_id"), operation,
                Parse<GuardianHostDiagnosticStream>(payload.GetProperty("stream"), "stream"),
                Int64(payload.GetProperty("chunk_index")),
                Int32(payload.GetProperty("offset")), RawBytes(payload),
                Boolean(payload.GetProperty("end_of_stream"))),
            GuardianHostEventType.WorkerDiagnosticTruncated =>
                new WorkerDiagnosticTruncatedEvent(
                    guardian, host, hostGeneration, sequence, requestId, alias, transition,
                    Required(worker, "worker_boot_id"), operation,
                    Parse<GuardianHostDiagnosticStream>(payload.GetProperty("stream"), "stream"),
                    Int64(payload.GetProperty("discarded_bytes"))),
            GuardianHostEventType.JobLifecycle => new JobLifecycleEvent(
                guardian, host, hostGeneration, sequence, requestId, alias, transition,
                Required(worker, "worker_boot_id"), operation,
                new PublicJobId(Int64(payload.GetProperty("public_job_id"))),
                Parse<GuardianHostJobState>(payload.GetProperty("state"), "state"),
                NullableInt32(payload.GetProperty("exit_code")),
                Parse<GuardianHostOutputState>(payload.GetProperty("output_state"), "output_state"),
                Int32(payload.GetProperty("output_bytes")),
                NullableDigest(payload.GetProperty("output_sha256"))),
            GuardianHostEventType.OutputChunk => new OutputChunkEvent(
                guardian, host, hostGeneration, sequence,
                Required(requestId, "request_id"), alias, transition,
                Required(worker, "worker_boot_id"), operation,
                Token(payload.GetProperty("output_capability_token")),
                Int64(payload.GetProperty("chunk_index")),
                Int32(payload.GetProperty("offset")), RawBytes(payload)),
            GuardianHostEventType.OutputSeal => new OutputSealEvent(
                guardian, host, hostGeneration, sequence,
                Required(requestId, "request_id"), alias, transition,
                Required(worker, "worker_boot_id"), operation,
                Token(payload.GetProperty("output_capability_token")),
                Parse<GuardianHostOutputSealState>(payload.GetProperty("state"), "state"),
                Int32(payload.GetProperty("total_bytes")),
                Digest(payload.GetProperty("output_sha256"))),
            _ => throw InvalidField("event_type"),
        };
    }

    private static GuardianHostContainmentIdentity DecodeContainment(JsonElement value) => new(
        UInt32(value.GetProperty("broker_pid")),
        UInt64(value.GetProperty("broker_start_identity_high")),
        UInt64(value.GetProperty("broker_start_identity_low")),
        UInt32(value.GetProperty("worker_pid")),
        UInt64(value.GetProperty("worker_start_identity_high")),
        UInt64(value.GetProperty("worker_start_identity_low")));

    private static GuardianHostResponse DecodeResponse(GuardianHostRawEnvelope raw)
    {
        var guardian = GuardianId(raw);
        var host = HostId(raw);
        var hostGeneration = HostGenerationValue(raw);
        var requestId = RequestId(raw.Value("request_id"));
        return Text(raw.Value("status")) switch
        {
            "ok" => new GuardianHostSuccessResponse(
                guardian, host, hostGeneration, requestId,
                DecodeSuccessPayload(raw.Value("payload"))),
            "error" => new GuardianHostErrorResponse(
                guardian, host, hostGeneration, requestId,
                DecodePrivateError(raw.Value("error"))),
            _ => throw InvalidField("status"),
        };
    }

    private static GuardianHostSuccessPayload DecodeSuccessPayload(JsonElement value)
    {
        var type = Parse<GuardianHostResponseType>(value.GetProperty("response_type"), "response_type");
        return type switch
        {
            GuardianHostResponseType.ManifestHeaderAccepted => new ManifestHeaderAccepted(
                Manifest(value.GetProperty("manifest_id"))),
            GuardianHostResponseType.ManifestChunkAccepted => new ManifestChunkAccepted(
                Manifest(value.GetProperty("manifest_id")),
                Int32(value.GetProperty("chunk_index")),
                Int32(value.GetProperty("next_offset"))),
            GuardianHostResponseType.ManifestSealed => new ManifestSealed(
                Manifest(value.GetProperty("manifest_id")),
                Digest(value.GetProperty("manifest_sha256")),
                Int32(value.GetProperty("total_bytes"))),
            GuardianHostResponseType.OperationCompleted => new OperationCompleted(
                DecodeOperationResult(
                    Parse<GuardianHostOperationKind>(value.GetProperty("operation"), "operation"),
                    value.GetProperty("result"))),
            GuardianHostResponseType.ControlAcknowledged => new ControlAcknowledged(
                new HostEventSequence(Int64(value.GetProperty("source_event_sequence")))),
            GuardianHostResponseType.ShutdownAccepted => new ShutdownAccepted(),
            _ => throw InvalidField("response_type"),
        };
    }

    private static GuardianHostOperationResult DecodeOperationResult(
        GuardianHostOperationKind kind,
        JsonElement value) => kind switch
    {
        GuardianHostOperationKind.InvokeForeground => new InvokeForegroundResult(
            Text(value.GetProperty("text"))),
        GuardianHostOperationKind.InvokeBackground => new InvokeBackgroundResult(
            new PublicJobId(Int64(value.GetProperty("public_job_id"))),
            Token(value.GetProperty("job_capability"))),
        GuardianHostOperationKind.JobList => new JobListResult(Text(value.GetProperty("text"))),
        GuardianHostOperationKind.JobStatus => new JobStatusResult(Text(value.GetProperty("text"))),
        GuardianHostOperationKind.JobOutput => new JobOutputResult(Text(value.GetProperty("text"))),
        GuardianHostOperationKind.JobKill => new JobKillResult(Text(value.GetProperty("text"))),
        GuardianHostOperationKind.Reset => new ResetResult(
            Alias(value.GetProperty("alias")),
            Parse<PublicSessionState>(value.GetProperty("state"), "state"),
            NullableWorker(value),
            Transition(value.GetProperty("transition_version")),
            Boolean(value.GetProperty("ready_for_effects")),
            Boolean(value.GetProperty("warm_state_lost")),
            Parse<BootstrapState>(value.GetProperty("bootstrap_state"), "bootstrap_state")),
        GuardianHostOperationKind.SessionOpen => new SessionOpenResult(
            Alias(value.GetProperty("alias")),
            Parse<PublicSessionState>(value.GetProperty("state"), "state"),
            NullableWorker(value),
            Transition(value.GetProperty("transition_version")),
            Boolean(value.GetProperty("ready_for_effects")),
            Boolean(value.GetProperty("warm_state_lost")),
            Parse<BootstrapState>(value.GetProperty("bootstrap_state"), "bootstrap_state")),
        GuardianHostOperationKind.SessionClose => new SessionCloseResult(
            Alias(value.GetProperty("alias")),
            Parse<PublicSessionState>(value.GetProperty("state"), "state"),
            NullableWorker(value),
            Transition(value.GetProperty("transition_version")),
            Boolean(value.GetProperty("ready_for_effects")),
            Boolean(value.GetProperty("warm_state_lost")),
            Parse<BootstrapState>(value.GetProperty("bootstrap_state"), "bootstrap_state")),
        GuardianHostOperationKind.SessionRestart => new SessionRestartResult(
            Alias(value.GetProperty("alias")),
            Parse<PublicSessionState>(value.GetProperty("state"), "state"),
            NullableWorker(value),
            Transition(value.GetProperty("transition_version")),
            Boolean(value.GetProperty("ready_for_effects")),
            Boolean(value.GetProperty("warm_state_lost")),
            Parse<BootstrapState>(value.GetProperty("bootstrap_state"), "bootstrap_state")),
        _ => throw InvalidField("operation"),
    };

    private static GuardianHostPrivateError DecodePrivateError(JsonElement value)
    {
        var detail = Parse<GuardianHostPrivateDetailCode>(value.GetProperty("detail_code"), "detail_code");
        var message = Parse<GuardianHostPrivateMessageCode>(value.GetProperty("message_code"), "message_code");
        var error = new GuardianHostPrivateError(detail);
        if (error.MessageCode != message) throw InvalidField("message_code");
        return error;
    }

    private static GuardianBootId GuardianId(GuardianHostRawEnvelope raw) =>
        new(raw.GuardianBootId);

    private static HostBootId HostId(GuardianHostRawEnvelope raw) => new(raw.HostBootId);

    private static HostGeneration HostGenerationValue(GuardianHostRawEnvelope raw) =>
        new(raw.HostGeneration);

    private static PrivateRequestId RequestId(JsonElement value) => new(Int64(value));

    private static PrivateRequestId? NullableRequestId(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : RequestId(value);

    private static CanonicalAlias Alias(JsonElement value) => new(Text(value));

    private static CanonicalAlias? NullableAlias(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : Alias(value);

    private static SessionTransitionVersion Transition(JsonElement value) => new(Int64(value));

    private static ManifestId Manifest(JsonElement value) => new(GuidValue(value));

    private static Sha256Digest Digest(JsonElement value) => new(Text(value));

    private static Sha256Digest? NullableDigest(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : Digest(value);

    private static CapabilityToken Token(JsonElement value) => new(Text(value));

    private static GuardianHostWorkerIdentity? NullableWorker(GuardianHostRawEnvelope raw)
    {
        var boot = raw.Value("worker_boot_id");
        return boot.ValueKind == JsonValueKind.Null
            ? null
            : new GuardianHostWorkerIdentity(
                new WorkerBootId(GuidValue(boot)),
                new WorkerGeneration(Int64(raw.Value("worker_generation"))));
    }

    private static GuardianHostWorkerIdentity RequiredWorker(GuardianHostRawEnvelope raw) =>
        Required(NullableWorker(raw), "worker_boot_id");

    private static GuardianHostWorkerIdentity? NullableWorker(JsonElement value)
    {
        var boot = value.GetProperty("worker_boot_id");
        return boot.ValueKind == JsonValueKind.Null
            ? null
            : new GuardianHostWorkerIdentity(
                new WorkerBootId(GuidValue(boot)),
                new WorkerGeneration(Int64(value.GetProperty("worker_generation"))));
    }

    private static GuardianHostOperationIdentity? NullableOperationIdentity(GuardianHostRawEnvelope raw)
    {
        var plan = raw.Value("plan_id");
        return plan.ValueKind == JsonValueKind.Null
            ? null
            : new GuardianHostOperationIdentity(
                new PlanId(GuidValue(plan)),
                new OperationId(GuidValue(raw.Value("operation_id"))));
    }

    private static byte[] RawBytes(JsonElement value) =>
        Convert.FromBase64String(Text(value.GetProperty("raw_base64")));

    private static T Required<T>(T? value, string name) where T : class =>
        value ?? throw InvalidField(name);

    private static string Text(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw InvalidField("string");

    private static bool Boolean(JsonElement value) =>
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw InvalidField("boolean");

    private static int Int32(JsonElement value) =>
        Integral(value) is var parsed && parsed >= int.MinValue && parsed <= int.MaxValue
            ? decimal.ToInt32(parsed)
            : throw InvalidField("int32");

    private static int? NullableInt32(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : Int32(value);

    private static long Int64(JsonElement value) =>
        Integral(value) is var parsed && parsed >= long.MinValue && parsed <= long.MaxValue
            ? decimal.ToInt64(parsed)
            : throw InvalidField("int64");

    private static uint UInt32(JsonElement value) =>
        Integral(value) is var parsed && parsed >= uint.MinValue && parsed <= uint.MaxValue
            ? decimal.ToUInt32(parsed)
            : throw InvalidField("uint32");

    private static ulong UInt64(JsonElement value) =>
        Integral(value) is var parsed && parsed >= ulong.MinValue && parsed <= ulong.MaxValue
            ? decimal.ToUInt64(parsed)
            : throw InvalidField("uint64");

    private static decimal Integral(JsonElement value)
    {
        if (!value.TryGetDecimal(out var parsed) || parsed != decimal.Truncate(parsed))
            throw InvalidField("integer");
        return parsed;
    }

    private static Guid GuidValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var parsed)
            ? parsed
            : throw InvalidField("uuid");

    private static T Parse<T>(JsonElement value, string name) where T : struct, Enum
    {
        if (value.ValueKind != JsonValueKind.String) throw InvalidField(name);
        var wire = value.GetString();
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(Wire(candidate), wire, StringComparison.Ordinal)) return candidate;
        }
        throw InvalidField(name);
    }

    private static T? NullableEnum<T>(JsonElement value, string name) where T : struct, Enum =>
        value.ValueKind == JsonValueKind.Null ? null : Parse<T>(value, name);
}

/// <summary>Incremental bounded reader for typed LF-terminated protocol frames.</summary>
public sealed class GuardianHostProtocolReader
{
    private readonly GuardianHostRawProtocolReader _reader;

    public GuardianHostProtocolReader(Stream stream, GuardianHostPeer sender) =>
        _reader = new GuardianHostRawProtocolReader(stream, sender);

    internal GuardianHostProtocolReader(
        Stream stream,
        GuardianHostPeer sender,
        ArrayPool<byte> framePool) =>
        _reader = new GuardianHostRawProtocolReader(stream, sender, framePool);

    public async ValueTask<GuardianHostMessage?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var raw = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return raw is null ? null : GuardianHostProtocolCodec.FromRaw(raw);
    }
}

/// <summary>Serializes typed LF-terminated frames and latches ambiguous transport failure.</summary>
public sealed class GuardianHostProtocolWriter
{
    private readonly GuardianHostPeer _sender;
    private readonly GuardianHostRawProtocolWriter _writer;

    public GuardianHostProtocolWriter(Stream stream, GuardianHostPeer sender)
    {
        if (!Enum.IsDefined(sender)) throw new ArgumentOutOfRangeException(nameof(sender));
        _sender = sender;
        _writer = new GuardianHostRawProtocolWriter(stream, sender);
    }

    public ValueTask WriteAsync(
        GuardianHostMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Sender != _sender)
            throw new ArgumentException("Message direction does not match the writer.", nameof(message));
        return _writer.WriteAsync(GuardianHostProtocolCodec.ToRaw(message), cancellationToken);
    }
}
