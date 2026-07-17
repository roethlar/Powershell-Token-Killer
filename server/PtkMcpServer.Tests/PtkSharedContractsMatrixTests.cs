using System.Reflection;
using System.Text;
using System.Text.Json;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

public sealed class PtkSharedContractsMatrixTests
{
    private const int RetryAfter = ContractLimits.MinimumRetryAfterMilliseconds;
    private static readonly GuardianBootId GuardianId =
        new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly HostBootId HostId =
        new(Guid.Parse("22222222-2222-4222-8222-222222222222"));
    private static readonly WorkerBootId WorkerId =
        new(Guid.Parse("33333333-3333-4333-8333-333333333333"));
    private static readonly Sha256Digest CatalogDigest = Digest('a');
    private static readonly Sha256Digest ConfigurationDigest = Digest('b');

    [Fact]
    public void Every_public_host_state_has_a_schema_valid_stable_round_trip()
    {
        var states = Enum.GetValues<PublicHostState>();
        Assert.Equal(9, states.Length);

        foreach (var state in states)
        {
            var host = ValidHost(state);
            var snapshot = new PublicStateSnapshot(GuardianId, host, []);
            var encoded = PublicStateCodec.Encode(snapshot);
            var decoded = PublicStateCodec.Decode(encoded);

            Assert.Equal(state, decoded.Host.State);
            Assert.Equal(host, decoded.Host);
            Assert.Equal(encoded, PublicStateCodec.Encode(decoded));
        }
    }

    [Fact]
    public void Every_public_session_state_has_a_schema_valid_stable_round_trip()
    {
        var states = Enum.GetValues<PublicSessionState>();
        Assert.Equal(14, states.Length);

        foreach (var state in states)
        {
            var session = ValidSession(state);
            var snapshot = new PublicStateSnapshot(
                GuardianId,
                ValidHost(PublicHostState.Ready),
                [session]);
            var encoded = PublicStateCodec.Encode(snapshot);
            var decoded = PublicStateCodec.Decode(encoded);

            Assert.Equal(state, Assert.Single(decoded.Sessions).State);
            Assert.Equal(session, Assert.Single(decoded.Sessions));
            Assert.Equal(encoded, PublicStateCodec.Encode(decoded));
        }
    }

    [Fact]
    public void Public_host_state_rejects_invalid_identity_recovery_and_readiness_combinations()
    {
        var invalid = new Action[]
        {
            () => _ = Host(null, null, PublicHostState.Ready, null, 0, null, true),
            () => _ = Host(HostId, new HostGeneration(1), PublicHostState.Absent, null, 0, null, false),
            () => _ = Host(HostId, null, PublicHostState.Starting, null, 0, null, false),
            () => _ = Host(null, new HostGeneration(1), PublicHostState.Stopped, null, 0, null, false),
            () => _ = Host(null, null, PublicHostState.Backoff, RecoveryPhase.Attempting, 1, RetryAfter, false),
            () => _ = Host(null, null, PublicHostState.Backoff, RecoveryPhase.Backoff, 0, RetryAfter, false),
            () => _ = Host(null, null, PublicHostState.Backoff, RecoveryPhase.Backoff, 1, null, false),
            () => _ = Host(null, null, PublicHostState.Absent, null, 0, RetryAfter, false),
            () => _ = Host(HostId, new HostGeneration(1), PublicHostState.Starting, null, 1, null, false),
            () => _ = Host(HostId, new HostGeneration(1), PublicHostState.Starting, RecoveryPhase.Backoff, 1, RetryAfter, false),
            () => _ = Host(HostId, new HostGeneration(1), PublicHostState.Recovering, RecoveryPhase.CircuitOpen, 1, RetryAfter, false),
            () => _ = Host(HostId, new HostGeneration(1), PublicHostState.Ready, null, 0, null, false),
            () => _ = Host(null, null, PublicHostState.Stopped, null, 0, null, true),
            () => _ = Host(null, null, (PublicHostState)999, null, 0, null, false),
            () => _ = Host(null, null, PublicHostState.Absent, (RecoveryPhase)999, 1, RetryAfter, false),
        };

        foreach (var action in invalid) Assert.ThrowsAny<ArgumentException>(action);
    }

    [Fact]
    public void Public_session_state_rejects_invalid_identity_recovery_and_readiness_combinations()
    {
        var alias = new CanonicalAlias("default");
        var transition = new SessionTransitionVersion(0);
        var invalid = new Action[]
        {
            () => _ = Session(alias, PublicSessionState.Ready, null, null, null, 0, null, true),
            () => _ = Session(alias, PublicSessionState.Cold, WorkerId, new WorkerGeneration(1), null, 0, null, false),
            () => _ = Session(alias, PublicSessionState.Ready, WorkerId, null, null, 0, null, true),
            () => _ = Session(alias, PublicSessionState.Bootstrapping, null, null, RecoveryPhase.Bootstrap, 1, RetryAfter, false),
            () => _ = Session(alias, PublicSessionState.Quarantined, null, null, null, 0, null, false),
            () => _ = Session(alias, PublicSessionState.Starting, null, null, RecoveryPhase.Attempting, 1, RetryAfter, false),
            () => _ = Session(alias, PublicSessionState.Backoff, null, null, RecoveryPhase.Attempting, 1, RetryAfter, false),
            () => _ = Session(alias, PublicSessionState.Recovering, null, null, RecoveryPhase.Bootstrap, 1, RetryAfter, false),
            () => _ = Session(alias, PublicSessionState.CircuitOpen, null, null, RecoveryPhase.CircuitOpen, 0, RetryAfter, false),
            () => _ = Session(alias, PublicSessionState.HalfOpen, null, null, RecoveryPhase.HalfOpen, 1, null, false),
            () => _ = Session(alias, PublicSessionState.Cold, null, null, null, 0, RetryAfter, false),
            () => _ = Session(alias, PublicSessionState.Ready, WorkerId, new WorkerGeneration(1), null, 0, null, false),
            () => _ = Session(alias, PublicSessionState.Cold, null, null, null, 0, null, true),
            () => _ = new PublicSessionStateSnapshot(alias, (DesiredSessionState)999,
                PublicSessionState.Cold, null, null, transition, null, 0, null, false, null,
                false, BootstrapState.NotApplicable),
            () => _ = new PublicSessionStateSnapshot(alias, DesiredSessionState.Cold,
                (PublicSessionState)999, null, null, transition, null, 0, null, false, null,
                false, BootstrapState.NotApplicable),
            () => _ = new PublicSessionStateSnapshot(alias, DesiredSessionState.Cold,
                PublicSessionState.Cold, null, null, transition, null, 0, null, false, null,
                false, (BootstrapState)999),
        };

        foreach (var action in invalid) Assert.ThrowsAny<ArgumentException>(action);
    }

    [Fact]
    public void Recovery_manifest_round_trips_all_binding_kinds_and_allows_extra_marks()
    {
        var manifest = ManifestWithAllBindingKinds(includeExtraMark: true);
        var compact = RecoveryManifestCodec.Encode(manifest);
        var transferred = RecoveryManifestCodec.Encode(manifest, appendFinalLf: true);

        Assert.Equal((byte)'\n', transferred[^1]);
        Assert.Equal(compact, transferred[..^1]);
        Assert.True(transferred.Length <= ContractLimits.MaximumManifestBytes);

        foreach (var encoded in new[] { compact, transferred })
        {
            var decoded = RecoveryManifestCodec.DecodeForInitialize(encoded, ConfigurationDigest);
            Assert.Equal(
                [RecoveryBindingKind.Default, RecoveryBindingKind.Dynamic, RecoveryBindingKind.Template],
                decoded.Bindings.Select(value => value.BindingKind));
            Assert.Equal(["archived", "default", "dynamic", "templated"],
                decoded.WorkerGenerationHighWatermarks.Select(value => value.Alias.Value));
            Assert.Equal("bootstrap"u8.ToArray(), Assert.Single(decoded.Templates).GetBootstrapBytes());
            Assert.Equal(encoded, RecoveryManifestCodec.Encode(decoded, appendFinalLf: encoded[^1] == (byte)'\n'));
        }

        Assert.Throws<InvalidDataException>(() => RecoveryManifestCodec.DecodeForInitialize(
            compact,
            Digest('c')));
    }

    [Fact]
    public void Recovery_manifest_decoder_zeroes_temporary_bootstrap_bytes()
    {
        using var document = JsonDocument.Parse(
            """{"bootstrap_raw_base64":"Ym9vdHN0cmFw"}""");
        byte[]? successfulBuffer = null;

        var length = RecoveryManifestCodec.DecodeBootstrapBytes(
            document.RootElement,
            bootstrap =>
            {
                successfulBuffer = bootstrap;
                Assert.Equal("bootstrap"u8.ToArray(), bootstrap);
                return bootstrap.Length;
            });

        Assert.Equal(9, length);
        Assert.NotNull(successfulBuffer);
        Assert.All(successfulBuffer, value => Assert.Equal(0, value));

        byte[]? failedBuffer = null;
        var failure = Assert.Throws<InvalidOperationException>(() =>
            RecoveryManifestCodec.DecodeBootstrapBytes<int>(
                document.RootElement,
                bootstrap =>
                {
                    failedBuffer = bootstrap;
                    throw new InvalidOperationException("Injected template failure.");
                }));

        Assert.Equal("Injected template failure.", failure.Message);
        Assert.NotNull(failedBuffer);
        Assert.All(failedBuffer, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Recovery_manifest_decoder_never_materializes_the_full_document_as_a_string()
    {
        var source = File.ReadAllText(RecoveryManifestCodecSourcePath());

        Assert.DoesNotContain("StrictUtf8.GetString", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Encoding.UTF8.GetString", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Recovery_manifest_codec_clears_intermediate_encode_buffers()
    {
        using var stream = new MemoryStream();
        stream.Write("temporary manifest bytes"u8);
        Assert.True(stream.TryGetBuffer(out var buffer));
        var written = buffer.AsSpan(0, checked((int)stream.Length));
        Assert.Contains(written.ToArray(), value => value != 0);

        RecoveryManifestCodec.ClearMemoryStreamBuffer(stream);

        Assert.All(written.ToArray(), value => Assert.Equal(0, value));

        var source = File.ReadAllText(RecoveryManifestCodecSourcePath());
        Assert.Contains("ClearMemoryStreamBuffer(stream)", source, StringComparison.Ordinal);
        Assert.Contains(
            "CryptographicOperations.ZeroMemory(compact)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CryptographicOperations.ZeroMemory(canonical)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_recovery_manifest_decode_clears_owned_template_bootstrap_bytes()
    {
        var encoded = RecoveryManifestCodec.Encode(
            ManifestWithAllBindingKinds(includeExtraMark: false));
        byte[]? decodedBootstrap = null;

        var failure = Assert.Throws<InvalidDataException>(() =>
            RecoveryManifestCodec.DecodeForInitializeObserved(
                encoded,
                Digest('c'),
                template =>
                {
                    var field = typeof(RecoveryTemplate).GetField(
                        "_bootstrapBytes",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    decodedBootstrap = Assert.IsType<byte[]>(field!.GetValue(template));
                    Assert.Contains(decodedBootstrap, value => value != 0);
                }));

        Assert.Equal(
            "Manifest configuration digest does not match initialize.",
            failure.Message);
        Assert.NotNull(decodedBootstrap);
        Assert.All(decodedBootstrap, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Recovery_manifest_rejects_collection_order_count_null_and_coverage_failures()
    {
        var firstTemplate = Template("alpha", "a"u8.ToArray(), Digest('1'));
        var secondTemplate = Template("beta", "b"u8.ToArray(), Digest('2'));
        var defaultBinding = DefaultBinding();
        var dynamicBinding = DynamicBinding("dynamic");
        var defaultMark = Mark("default", 0);
        var dynamicMark = Mark("dynamic", 1);

        Assert.Throws<ArgumentException>(() => NewManifest(
            [secondTemplate, firstTemplate], [defaultBinding], [defaultMark]));
        Assert.Throws<ArgumentException>(() => NewManifest(
            [], [dynamicBinding, defaultBinding], [defaultMark, dynamicMark]));
        Assert.Throws<ArgumentException>(() => NewManifest(
            [], [defaultBinding, dynamicBinding], [dynamicMark, defaultMark]));
        Assert.Throws<ArgumentException>(() => NewManifest(
            [firstTemplate, firstTemplate], [defaultBinding], [defaultMark]));
        Assert.Throws<ArgumentException>(() => NewManifest(
            [], [defaultBinding, defaultBinding], [defaultMark]));
        Assert.Throws<ArgumentException>(() => NewManifest(
            [], [defaultBinding], [defaultMark, defaultMark]));
        Assert.Throws<ArgumentException>(() => NewManifest([], [], [defaultMark]));
        Assert.Throws<ArgumentException>(() => NewManifest([], [defaultBinding], []));
        Assert.Throws<ArgumentException>(() => NewManifest([], [defaultBinding, dynamicBinding], [defaultMark]));
        Assert.Throws<ArgumentException>(() => NewManifest([], [dynamicBinding], [dynamicMark]));
        Assert.Throws<ArgumentException>(() => NewManifest(
            [null!], [defaultBinding], [defaultMark]));
        Assert.Throws<ArgumentException>(() => NewManifest(
            [], [null!], [defaultMark]));
        Assert.Throws<ArgumentException>(() => NewManifest(
            [], [defaultBinding], [null!]));
        Assert.Throws<ArgumentException>(() => new RecoveryManifest(
            GuardianId, new HostGeneration(2), CatalogDigest, ConfigurationDigest,
            [], [defaultBinding], [defaultMark], new HostGeneration(3)));

        var tooManyBindings = new List<RecoveryBinding> { defaultBinding };
        var tooManyMarks = new List<WorkerGenerationHighWatermarkEntry> { defaultMark };
        for (var index = 0; index < ContractLimits.MaximumAliases; index++)
        {
            var alias = $"x{index:D3}";
            tooManyBindings.Add(DynamicBinding(alias));
            tooManyMarks.Add(Mark(alias, index));
        }
        Assert.Throws<ArgumentException>(() => NewManifest([], tooManyBindings, tooManyMarks));

        var tooManyTemplates = Enumerable.Range(0, ContractLimits.MaximumTemplates + 1)
            .Select(index => Template($"t{index:D3}", [], Digest('3')))
            .ToArray();
        Assert.Throws<ArgumentException>(() => NewManifest(
            tooManyTemplates, [defaultBinding], [defaultMark]));
    }

    [Fact]
    public void Recovery_manifest_accepts_exact_collection_maxima_and_defensively_freezes_them()
    {
        var emptyDigest = Sha256Digest.Compute([]);
        var templates = Enumerable.Range(0, ContractLimits.MaximumTemplates)
            .Select(index => Template($"t{index:D3}", [], emptyDigest))
            .ToList();
        var bindings = new List<RecoveryBinding> { DefaultBinding() };
        var marks = new List<WorkerGenerationHighWatermarkEntry> { Mark("default", 0) };
        for (var index = 0; index < ContractLimits.MaximumAliases - 1; index++)
        {
            var alias = $"x{index:D3}";
            bindings.Add(DynamicBinding(alias));
            marks.Add(Mark(alias, index));
        }

        var manifest = NewManifest(templates, bindings, marks);
        templates.Clear();
        bindings.Clear();
        marks.Clear();

        Assert.Equal(ContractLimits.MaximumTemplates, manifest.Templates.Count);
        Assert.Equal(ContractLimits.MaximumAliases, manifest.Bindings.Count);
        Assert.Equal(ContractLimits.MaximumAliases, manifest.WorkerGenerationHighWatermarks.Count);

        var encoded = RecoveryManifestCodec.Encode(manifest, appendFinalLf: true);
        var decoded = RecoveryManifestCodec.DecodeForInitialize(encoded, ConfigurationDigest);
        Assert.Equal(ContractLimits.MaximumTemplates, decoded.Templates.Count);
        Assert.Equal(ContractLimits.MaximumAliases, decoded.Bindings.Count);
    }

    [Fact]
    public void Recovery_manifest_template_references_and_digests_are_closed()
    {
        var template = Template("profile", "bootstrap"u8.ToArray(), Digest('4'));
        var defaultBinding = DefaultBinding();
        var defaultMark = Mark("default", 0);
        var profileMark = Mark("profile-session", 0);

        Assert.Throws<ArgumentException>(() => NewManifest(
            [],
            [defaultBinding, TemplateBinding("profile-session", "missing", template.TemplateDigest, template.BootstrapDigest)],
            [defaultMark, profileMark]));
        Assert.Throws<ArgumentException>(() => NewManifest(
            [template],
            [defaultBinding, TemplateBinding("profile-session", "profile", Digest('5'), template.BootstrapDigest)],
            [defaultMark, profileMark]));
        Assert.Throws<ArgumentException>(() => NewManifest(
            [template],
            [defaultBinding, TemplateBinding("profile-session", "profile", template.TemplateDigest, Digest('6'))],
            [defaultMark, profileMark]));

        Assert.Throws<ArgumentException>(() => new RecoveryBinding(
            new CanonicalAlias("default"), RecoveryBindingKind.Dynamic, null, null, null,
            false, DesiredSessionState.Ready, new SessionTransitionVersion(0), Digest('7')));
        Assert.Throws<ArgumentException>(() => new RecoveryBinding(
            new CanonicalAlias("dynamic"), RecoveryBindingKind.Template,
            new CanonicalAlias("profile"), template.TemplateDigest, null,
            false, DesiredSessionState.Ready, new SessionTransitionVersion(0), Digest('7')));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecoveryBinding(
            new CanonicalAlias("dynamic"), (RecoveryBindingKind)999, null, null, null,
            false, DesiredSessionState.Ready, new SessionTransitionVersion(0), Digest('7')));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecoveryBinding(
            new CanonicalAlias("dynamic"), RecoveryBindingKind.Dynamic, null, null, null,
            false, (DesiredSessionState)999, new SessionTransitionVersion(0), Digest('7')));
    }

    [Fact]
    public void Recovery_template_and_codec_enforce_scalar_base64_and_field_boundaries()
    {
        var maximumBootstrap = new byte[ContractLimits.MaximumScriptBytes];
        var maximumTemplate = new RecoveryTemplate(
            new CanonicalAlias("maximum"),
            new string('d', 512),
            86_400,
            new string('t', 256),
            new string('i', 256),
            true,
            Digest('8'),
            Sha256Digest.Compute(maximumBootstrap),
            maximumBootstrap);
        Assert.Equal(ContractLimits.MaximumScriptBytes, maximumTemplate.BootstrapByteCount);

        _ = new RecoveryTemplate(
            new CanonicalAlias("minimum"), "d", 1, "t", "i", false,
            Digest('9'), Sha256Digest.Compute(Array.Empty<byte>()), ReadOnlyMemory<byte>.Empty);

        Assert.Throws<ArgumentException>(() => Template("default", [], Digest('a')));
        Assert.ThrowsAny<ArgumentException>(() => NewTemplate("empty-description", "", 1, "t", "i", []));
        Assert.ThrowsAny<ArgumentException>(() => NewTemplate("long-description", new string('d', 513), 1, "t", "i", []));
        Assert.ThrowsAny<ArgumentException>(() => NewTemplate("control", "bad\ntext", 1, "t", "i", []));
        Assert.ThrowsAny<ArgumentException>(() => NewTemplate("empty-target", "d", 1, "", "i", []));
        Assert.ThrowsAny<ArgumentException>(() => NewTemplate("long-target", "d", 1, new string('t', 257), "i", []));
        Assert.ThrowsAny<ArgumentException>(() => NewTemplate("empty-identity", "d", 1, "t", "", []));
        Assert.ThrowsAny<ArgumentException>(() => NewTemplate("long-identity", "d", 1, "t", new string('i', 257), []));
        Assert.Throws<ArgumentOutOfRangeException>(() => NewTemplate("zero-timeout", "d", 0, "t", "i", []));
        Assert.Throws<ArgumentOutOfRangeException>(() => NewTemplate("long-timeout", "d", 86_401, "t", "i", []));
        Assert.Throws<ArgumentOutOfRangeException>(() => NewTemplate(
            "large-bootstrap", "d", 1, "t", "i", new byte[ContractLimits.MaximumScriptBytes + 1]));
        Assert.Throws<ArgumentException>(() => new RecoveryTemplate(
            new CanonicalAlias("wrong-digest"), "d", 1, "t", "i", false,
            Digest('a'), Digest('b'), "content"u8.ToArray()));

        var manifest = ManifestWithAllBindingKinds(includeExtraMark: false);
        var encoded = RecoveryManifestCodec.Encode(manifest);
        var text = Encoding.UTF8.GetString(encoded);
        Assert.Contains("Ym9vdHN0cmFw", text, StringComparison.Ordinal);
        var noncanonical = text.Replace("Ym9vdHN0cmFw", "Ym9v dHN0cmFw", StringComparison.Ordinal);
        Assert.NotEqual(text, noncanonical);
        Assert.Throws<InvalidDataException>(() => RecoveryManifestCodec.DecodeForInitialize(
            Encoding.UTF8.GetBytes(noncanonical),
            ConfigurationDigest));

        var oversizedTransfer = new byte[ContractLimits.MaximumManifestBytes + 1];
        oversizedTransfer[^1] = (byte)'\n';
        Assert.Throws<InvalidDataException>(() => RecoveryManifestCodec.DecodeForInitialize(
            oversizedTransfer,
            ConfigurationDigest));
    }

    private static PublicHostStateSnapshot ValidHost(PublicHostState state) => state switch
    {
        PublicHostState.Absent => Host(null, null, state, null, 0, null, false),
        PublicHostState.Backoff => Host(null, null, state, RecoveryPhase.Backoff, 1, RetryAfter, false),
        PublicHostState.CircuitOpen => Host(null, null, state, RecoveryPhase.CircuitOpen, 1, RetryAfter, false),
        PublicHostState.ContainmentUnconfirmed => Host(HostId, new HostGeneration(1), state, null, 0, null, false),
        PublicHostState.HalfOpen => Host(HostId, new HostGeneration(1), state, RecoveryPhase.HalfOpen, 1, RetryAfter, false),
        PublicHostState.Ready => Host(HostId, new HostGeneration(1), state, null, 0, null, true),
        PublicHostState.Recovering => Host(HostId, new HostGeneration(1), state, RecoveryPhase.Containment, 1, RetryAfter, false),
        PublicHostState.Starting => Host(HostId, new HostGeneration(1), state, null, 0, null, false),
        PublicHostState.Stopped => Host(null, null, state, null, 0, null, false),
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static PublicHostStateSnapshot Host(
        HostBootId? bootId, HostGeneration? generation, PublicHostState state,
        RecoveryPhase? phase, long attempt, int? retryAfter, bool ready) =>
        new(bootId, generation, state, phase, attempt, retryAfter, ready, null);

    private static PublicSessionStateSnapshot ValidSession(PublicSessionState state)
    {
        var identity = state is PublicSessionState.Ready or PublicSessionState.Resetting or
            PublicSessionState.Faulted or PublicSessionState.Quarantined or PublicSessionState.Recovering or
            PublicSessionState.Bootstrapping or PublicSessionState.HalfOpen;
        var (phase, attempt, retryAfter) = state switch
        {
            PublicSessionState.Recovering => (RecoveryPhase.Containment, 1L, (int?)RetryAfter),
            PublicSessionState.Backoff => (RecoveryPhase.Backoff, 1L, (int?)RetryAfter),
            PublicSessionState.Bootstrapping => (RecoveryPhase.Bootstrap, 1L, (int?)RetryAfter),
            PublicSessionState.CircuitOpen => (RecoveryPhase.CircuitOpen, 1L, (int?)RetryAfter),
            PublicSessionState.HalfOpen => (RecoveryPhase.HalfOpen, 1L, (int?)RetryAfter),
            _ => ((RecoveryPhase?)null, 0L, (int?)null),
        };
        return Session(
            new CanonicalAlias("default"), state,
            identity ? WorkerId : null,
            identity ? new WorkerGeneration(1) : null,
            phase, attempt, retryAfter,
            state == PublicSessionState.Ready);
    }

    private static PublicSessionStateSnapshot Session(
        CanonicalAlias alias, PublicSessionState state, WorkerBootId? bootId,
        WorkerGeneration? generation, RecoveryPhase? phase, long attempt,
        int? retryAfter, bool ready) =>
        new(
            alias,
            state == PublicSessionState.Cold ? DesiredSessionState.Cold : DesiredSessionState.Ready,
            state,
            bootId,
            generation,
            new SessionTransitionVersion(0),
            phase,
            attempt,
            retryAfter,
            ready,
            null,
            state is PublicSessionState.Recovering or PublicSessionState.RecoveryUnknown,
            state switch
            {
                PublicSessionState.Starting or PublicSessionState.Bootstrapping => BootstrapState.Pending,
                PublicSessionState.Ready => BootstrapState.Restored,
                PublicSessionState.Faulted or PublicSessionState.Quarantined => BootstrapState.Failed,
                PublicSessionState.RecoveryUnknown => BootstrapState.Unknown,
                _ => BootstrapState.NotApplicable,
            });

    private static RecoveryManifest ManifestWithAllBindingKinds(bool includeExtraMark)
    {
        var template = Template("profile", "bootstrap"u8.ToArray(), Digest('c'));
        var marks = new List<WorkerGenerationHighWatermarkEntry>();
        if (includeExtraMark) marks.Add(Mark("archived", 9));
        marks.Add(Mark("default", 0));
        marks.Add(Mark("dynamic", 1));
        marks.Add(Mark("templated", 2));
        return NewManifest(
            [template],
            [
                DefaultBinding(),
                DynamicBinding("dynamic"),
                TemplateBinding("templated", "profile", template.TemplateDigest, template.BootstrapDigest),
            ],
            marks);
    }

    private static RecoveryManifest NewManifest(
        IEnumerable<RecoveryTemplate> templates,
        IEnumerable<RecoveryBinding> bindings,
        IEnumerable<WorkerGenerationHighWatermarkEntry> marks) =>
        new(
            GuardianId,
            new HostGeneration(7),
            CatalogDigest,
            ConfigurationDigest,
            templates,
            bindings,
            marks,
            new HostGeneration(7));

    private static RecoveryTemplate Template(string name, byte[] bootstrap, Sha256Digest templateDigest) =>
        new(
            new CanonicalAlias(name),
            "description",
            30,
            "target",
            "identity",
            false,
            templateDigest,
            Sha256Digest.Compute(bootstrap),
            bootstrap);

    private static RecoveryTemplate NewTemplate(
        string name, string description, int timeout, string target, string identity, byte[] bootstrap) =>
        new(
            new CanonicalAlias(name), description, timeout, target, identity, false,
            Digest('d'), Sha256Digest.Compute(bootstrap), bootstrap);

    private static RecoveryBinding DefaultBinding() =>
        new(
            new CanonicalAlias("default"), RecoveryBindingKind.Default,
            null, null, null, false, DesiredSessionState.Ready,
            new SessionTransitionVersion(0), Digest('e'));

    private static RecoveryBinding DynamicBinding(string alias) =>
        new(
            new CanonicalAlias(alias), RecoveryBindingKind.Dynamic,
            null, null, null, false, DesiredSessionState.Ready,
            new SessionTransitionVersion(0), Digest('f'));

    private static RecoveryBinding TemplateBinding(
        string alias, string templateName, Sha256Digest templateDigest, Sha256Digest bootstrapDigest) =>
        new(
            new CanonicalAlias(alias), RecoveryBindingKind.Template,
            new CanonicalAlias(templateName), templateDigest, bootstrapDigest,
            false, DesiredSessionState.Ready, new SessionTransitionVersion(0), Digest('0'));

    private static WorkerGenerationHighWatermarkEntry Mark(string alias, long generation) =>
        new(new CanonicalAlias(alias), new WorkerGenerationHighWatermark(generation));

    private static Sha256Digest Digest(char character) => new(new string(character, 64));

    private static string RecoveryManifestCodecSourcePath(
        [System.Runtime.CompilerServices.CallerFilePath] string testSourcePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testSourcePath) ??
                throw new InvalidOperationException("Test source path is unavailable."),
            "..",
            "PtkSharedContracts",
            "Recovery",
            "RecoveryManifestCodec.cs"));
}
