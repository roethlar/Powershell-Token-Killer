using System.Reflection;
using System.Text;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

public sealed class PtkSharedContractsTests
{
    private static readonly string[] EmbeddedAssets =
    [
        "public-tool-contract.json",
        "public-recovery.schema.json",
        "public-state.schema.json",
        "guardian-host-protocol.json",
        "guardian-host-protocol.schema.json",
        "recovery-manifest.schema.json",
        "recovery-manifest.example.json",
    ];

    [Fact]
    public void Embedded_contract_assets_are_exact_canonical_source_bytes()
    {
        foreach (var asset in EmbeddedAssets)
            Assert.Equal(File.ReadAllBytes(ContractPath(asset)), ContractResources.ReadExact(asset));
    }

    [Fact]
    public void Public_tool_contract_is_the_frozen_six_tool_contract_and_domain_digest()
    {
        var contract = PublicToolContractResource.Parse();

        Assert.Equal("ptk.public-contract/1", contract.SchemaVersion);
        Assert.Equal(
            ["ptk_invoke", "ptk_job", "ptk_output", "ptk_reset", "ptk_session", "ptk_state"],
            contract.Tools.Select(tool => tool.Name));
        Assert.All(
            contract.Tools,
            tool => Assert.Equal(
                "object",
                tool.InputSchema.GetProperty("type").GetString()));
        Assert.Equal(
            "db732bfe3fb4b01ddb4d53d977414868f3fbbe2edea083c17a0194f3baa73e00",
            PublicToolContractResource.ComputeDigest().Value);
    }

    [Fact]
    public void Public_tool_contract_dtos_are_validated_getter_only_and_defensively_frozen()
    {
        var parsed = PublicToolContractResource.Parse();
        var source = parsed.Tools.ToArray();
        var snapshot = new PublicToolContractSnapshot(
            parsed.SchemaVersion,
            parsed.ServerIdentity,
            parsed.Instructions,
            parsed.RecoveryDescription,
            source);
        source[0] = source[1];

        Assert.Equal("ptk_invoke", snapshot.Tools[0].Name);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<PublicToolDefinition>)snapshot.Tools)[0] = snapshot.Tools[1]);
        Assert.Throws<ArgumentException>(() => new PublicServerIdentity("", "1"));
        Assert.Throws<ArgumentException>(() => new PublicToolContractSnapshot(
            "future", parsed.ServerIdentity, parsed.Instructions,
            parsed.RecoveryDescription, parsed.Tools));

        PublicToolDefinition owned;
        using (var document = System.Text.Json.JsonDocument.Parse("{}"))
            owned = new PublicToolDefinition("tool", "description", document.RootElement);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, owned.InputSchema.ValueKind);

        Type[] dtoTypes =
        [typeof(PublicServerIdentity), typeof(PublicToolDefinition), typeof(PublicToolContractSnapshot)];
        Assert.All(dtoTypes, type => Assert.All(type.GetProperties(), property =>
            Assert.Null(property.SetMethod)));
    }

    [Theory]
    [InlineData(PublicRecoveryDetailCode.BackendLostBeforeDispatch, RecoveryPhase.Attempting, false)]
    [InlineData(PublicRecoveryDetailCode.HostCircuitOpen, RecoveryPhase.CircuitOpen, false)]
    [InlineData(PublicRecoveryDetailCode.HostRecovering, RecoveryPhase.Backoff, false)]
    [InlineData(PublicRecoveryDetailCode.SessionRecovering, RecoveryPhase.Bootstrap, true)]
    public void Retryable_public_recovery_union_round_trips(
        PublicRecoveryDetailCode detail,
        RecoveryPhase phase,
        bool sessionGate)
    {
        RetryGate gate = sessionGate
            ? new SessionReadyGate(new CanonicalAlias("default"))
            : new HostReadyGate();
        var expected = new PublicRecoveryError(detail, true, 250, phase, 1, gate);

        Assert.Equal(expected, PublicRecoveryCodec.Decode(PublicRecoveryCodec.Encode(expected)));
    }

    [Theory]
    [InlineData(PublicRecoveryDetailCode.HostContainmentUnconfirmed)]
    [InlineData(PublicRecoveryDetailCode.HostContractMismatch)]
    [InlineData(PublicRecoveryDetailCode.HostStartFailed)]
    [InlineData(PublicRecoveryDetailCode.OutcomeUnknown)]
    [InlineData(PublicRecoveryDetailCode.SessionBootstrapFailed)]
    [InlineData(PublicRecoveryDetailCode.SessionRecoveryUnknown)]
    public void Nonretryable_public_recovery_union_round_trips_without_retry_metadata(
        PublicRecoveryDetailCode detail)
    {
        var expected = new PublicRecoveryError(detail, false, null, null, null, null);
        Assert.Equal(expected, PublicRecoveryCodec.Decode(PublicRecoveryCodec.Encode(expected)));
        Assert.Throws<ArgumentException>(() =>
            new PublicRecoveryError(detail, false, 250, null, null, null));
    }

    [Fact]
    public void Public_recovery_rejects_noncanonical_or_invalid_runtime_values()
    {
        var canonical = PublicRecoveryCodec.Encode(new PublicRecoveryError(
            PublicRecoveryDetailCode.BackendLostBeforeDispatch,
            true,
            ContractLimits.MinimumRetryAfterMilliseconds,
            RecoveryPhase.Attempting,
            1,
            new HostReadyGate()));
        var text = Encoding.UTF8.GetString(canonical);

        Assert.Throws<InvalidDataException>(() => PublicRecoveryCodec.Decode(
            Encoding.UTF8.GetBytes(text.Replace(",", ", ", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => PublicRecoveryCodec.Decode(
            Encoding.UTF8.GetBytes(text.Replace(":", ":\t", StringComparison.Ordinal))));
        Assert.Throws<DecoderFallbackException>(() => PublicRecoveryCodec.Decode(
            new byte[] { 0xff, 0xfe }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PublicRecoveryError(
            (PublicRecoveryDetailCode)int.MaxValue, false, null, null, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PublicRecoveryError(
            PublicRecoveryDetailCode.BackendLostBeforeDispatch,
            true,
            ContractLimits.MinimumRetryAfterMilliseconds,
            (RecoveryPhase)int.MaxValue,
            1,
            new HostReadyGate()));
        Assert.Throws<ArgumentNullException>(() => new SessionReadyGate(null!));

        var retryGateConstructors = typeof(RetryGate).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotEmpty(retryGateConstructors);
        Assert.All(retryGateConstructors, constructor => Assert.True(constructor.IsFamilyAndAssembly));
    }

    [Fact]
    public void Public_state_round_trips_and_rejects_noncanonical_state()
    {
        var guardian = new GuardianBootId(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
        var host = new PublicHostStateSnapshot(
            new HostBootId(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")),
            new HostGeneration(3),
            PublicHostState.Ready,
            null,
            0,
            null,
            true,
            null);
        var session = new PublicSessionStateSnapshot(
            new CanonicalAlias("default"),
            DesiredSessionState.Ready,
            PublicSessionState.Ready,
            new WorkerBootId(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")),
            new WorkerGeneration(4),
            new SessionTransitionVersion(2),
            null,
            0,
            null,
            true,
            null,
            false,
            BootstrapState.Restored);
        var expected = new PublicStateSnapshot(guardian, host, [session]);
        var encoded = PublicStateCodec.Encode(expected);

        var decoded = PublicStateCodec.Decode(encoded);
        Assert.Equal(expected.GuardianBootId, decoded.GuardianBootId);
        Assert.Equal(expected.Host, decoded.Host);
        Assert.Equal(expected.Sessions, decoded.Sessions);
        Assert.Equal(encoded, PublicStateCodec.Encode(decoded));

        var uppercase = Encoding.UTF8.GetString(encoded).Replace(
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA",
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            PublicStateCodec.Decode(Encoding.UTF8.GetBytes(uppercase)));

        Assert.Throws<ArgumentException>(() => new PublicHostStateSnapshot(
            new HostBootId(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb")),
            new HostGeneration(3),
            PublicHostState.Starting,
            null,
            1,
            null,
            false,
            null));

        Assert.Throws<ArgumentException>(() =>
            new PublicStateSnapshot(guardian, host,
            [
                new PublicSessionStateSnapshot(
                    new CanonicalAlias("z"), DesiredSessionState.Cold, PublicSessionState.Cold,
                    null, null, new SessionTransitionVersion(0), null, 0, null, false, null,
                    false, BootstrapState.NotApplicable),
                session,
            ]));
    }

    [Fact]
    public void Public_state_rejects_null_identifiers_and_undefined_enums()
    {
        var alias = new CanonicalAlias("default");
        var transition = new SessionTransitionVersion(0);

        Assert.Throws<ArgumentNullException>(() => new PublicStateSnapshot(
            null!,
            new PublicHostStateSnapshot(null, null, PublicHostState.Absent, null, 0, null, false, null),
            []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PublicHostStateSnapshot(
            null, null, (PublicHostState)int.MaxValue, null, 0, null, false, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PublicSessionStateSnapshot(
            alias, (DesiredSessionState)int.MaxValue, PublicSessionState.Cold,
            null, null, transition, null, 0, null, false, null, false,
            BootstrapState.NotApplicable));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PublicSessionStateSnapshot(
            alias, DesiredSessionState.Cold, PublicSessionState.Cold,
            null, null, transition, null, 0, null, false, null, false,
            (BootstrapState)int.MaxValue));

        Type[] identifierTypes =
        [
            typeof(CanonicalAlias), typeof(Sha256Digest), typeof(CapabilityToken),
            typeof(GuardianBootId), typeof(HostBootId), typeof(WorkerBootId),
            typeof(ManifestId), typeof(PlanId), typeof(OperationId), typeof(CallId),
            typeof(HostGeneration), typeof(WorkerGeneration), typeof(PrivateRequestId),
            typeof(HostEventSequence), typeof(SessionTransitionVersion),
            typeof(WorkerGenerationHighWatermark), typeof(PublicJobId),
        ];
        Assert.All(identifierTypes, type =>
        {
            Assert.False(type.IsValueType);
            Assert.True(type.IsSealed);
        });
    }

    [Fact]
    public void Recovery_manifest_example_round_trips_exactly_and_binds_initialize_digest()
    {
        var exact = ContractResources.ReadExact("recovery-manifest.example.json");
        var expectedConfiguration = new Sha256Digest(new string('c', 64));
        var manifest = RecoveryManifestCodec.DecodeForInitialize(exact, expectedConfiguration);

        Assert.Equal(exact, RecoveryManifestCodec.Encode(manifest, appendFinalLf: true));
        Assert.Equal(0, Assert.Single(manifest.WorkerGenerationHighWatermarks).Generation.Value);
        Assert.Throws<InvalidDataException>(() => RecoveryManifestCodec.DecodeForInitialize(
            exact,
            new Sha256Digest(new string('e', 64))));

        var uppercaseUuid = Encoding.UTF8.GetString(exact).Replace(
            "11111111-1111-4111-8111-111111111111",
            "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA",
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => RecoveryManifestCodec.DecodeForInitialize(
            Encoding.UTF8.GetBytes(uppercaseUuid),
            expectedConfiguration));

        Assert.Throws<InvalidDataException>(() => RecoveryManifestCodec.DecodeForInitialize(
            new byte[ContractLimits.MaximumManifestBytes + 1],
            expectedConfiguration));

        var text = Encoding.UTF8.GetString(exact);
        Assert.Throws<InvalidDataException>(() => RecoveryManifestCodec.DecodeForInitialize(
            Encoding.UTF8.GetBytes(text.Replace(",", ", ", StringComparison.Ordinal)),
            expectedConfiguration));
        Assert.Throws<InvalidDataException>(() => RecoveryManifestCodec.DecodeForInitialize(
            Encoding.UTF8.GetBytes(text.Replace(":", ":\t", StringComparison.Ordinal)),
            expectedConfiguration));

        Assert.DoesNotContain(
            typeof(RecoveryManifestCodec).GetMethods(BindingFlags.Public | BindingFlags.Static),
            method => method.Name == "Decode");
    }

    [Fact]
    public void Recovery_bindings_reject_null_and_undefined_runtime_values()
    {
        var digest = new Sha256Digest(new string('a', 64));
        var alias = new CanonicalAlias("dynamic");
        var transition = new SessionTransitionVersion(0);

        Assert.Throws<ArgumentOutOfRangeException>(() => new RecoveryBinding(
            alias, (RecoveryBindingKind)int.MaxValue, null, null, null, false,
            DesiredSessionState.Cold, transition, digest));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecoveryBinding(
            alias, RecoveryBindingKind.Dynamic, null, null, null, false,
            (DesiredSessionState)int.MaxValue, transition, digest));
        Assert.Throws<ArgumentNullException>(() => new RecoveryBinding(
            null!, RecoveryBindingKind.Dynamic, null, null, null, false,
            DesiredSessionState.Cold, transition, digest));
        Assert.Throws<ArgumentNullException>(() => new WorkerGenerationHighWatermarkEntry(
            alias, null!));
    }

    [Fact]
    public void Recovery_template_rejects_invalid_scalars_and_never_exposes_backing_bytes()
    {
        var source = "bootstrap"u8.ToArray();
        var digest = Sha256Digest.Compute(source);
        var template = new RecoveryTemplate(
            new CanonicalAlias("safe"),
            "description",
            30,
            "target",
            "identity",
            false,
            new Sha256Digest(new string('a', 64)),
            digest,
            source);

        source[0] = 0;
        var first = template.GetBootstrapBytes();
        Assert.Equal((byte)'b', first[0]);
        first[0] = 0;
        Assert.Equal((byte)'b', template.GetBootstrapBytes()[0]);

        Assert.Throws<ArgumentException>(() => new RecoveryTemplate(
            new CanonicalAlias("invalid"),
            "bad\ud800text",
            30,
            "target",
            "identity",
            false,
            new Sha256Digest(new string('a', 64)),
            digest,
            "bootstrap"u8.ToArray()));
    }

    [Fact]
    public void Shared_contract_assembly_has_no_runtime_or_host_framework_dependency()
    {
        var names = typeof(ContractLimits).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        Assert.DoesNotContain(names, name => name is not null &&
            (name.Contains("PowerShell", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("ModelContextProtocol", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Hosting", StringComparison.OrdinalIgnoreCase)));

        var project = File.ReadAllText(ProjectPath("PtkSharedContracts", "PtkSharedContracts.csproj"));
        Assert.DoesNotContain("PackageReference", project, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectReference", project, StringComparison.Ordinal);
        Assert.DoesNotContain("FrameworkReference", project, StringComparison.Ordinal);
    }

    private static string ContractPath(string fileName) =>
        ProjectPath("Contracts", "ResilienceR0", fileName);

    private static string ProjectPath(params string[] relative)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var server = Path.Combine(directory.FullName, "server");
            var candidate = Path.Combine([server, .. relative]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"Could not locate server/{string.Join('/', relative)}.");
    }
}
