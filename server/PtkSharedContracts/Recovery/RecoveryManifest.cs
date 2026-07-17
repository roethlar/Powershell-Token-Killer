using System.Buffers;
using System.Globalization;
using System.Text;

namespace PtkSharedContracts;

public enum RecoveryBindingKind { Default, Dynamic, Template }

public sealed record RecoveryTemplate
{
    private readonly byte[] _bootstrapBytes;

    public RecoveryTemplate(
        CanonicalAlias name,
        string description,
        int startupTimeoutSeconds,
        string declaredTarget,
        string declaredIdentity,
        bool allowColdBackground,
        Sha256Digest templateDigest,
        Sha256Digest bootstrapDigest,
        ReadOnlyMemory<byte> bootstrapBytes)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(templateDigest);
        ArgumentNullException.ThrowIfNull(bootstrapDigest);
        if (name.Value == "default") throw new ArgumentException("default is not a template name.", nameof(name));
        ValidateText(description, 512, nameof(description));
        ValidateText(declaredTarget, 256, nameof(declaredTarget));
        ValidateText(declaredIdentity, 256, nameof(declaredIdentity));
        if (startupTimeoutSeconds is < 1 or > 86_400)
            throw new ArgumentOutOfRangeException(nameof(startupTimeoutSeconds));
        if (bootstrapBytes.Length > ContractLimits.MaximumScriptBytes)
            throw new ArgumentOutOfRangeException(nameof(bootstrapBytes));
        if (Sha256Digest.Compute(bootstrapBytes.Span) != bootstrapDigest)
            throw new ArgumentException("Bootstrap digest does not match exact bootstrap bytes.", nameof(bootstrapDigest));
        Name = name; Description = description; StartupTimeoutSeconds = startupTimeoutSeconds;
        DeclaredTarget = declaredTarget; DeclaredIdentity = declaredIdentity;
        AllowColdBackground = allowColdBackground; TemplateDigest = templateDigest;
        BootstrapDigest = bootstrapDigest; _bootstrapBytes = bootstrapBytes.ToArray();
    }

    public CanonicalAlias Name { get; }
    public string Description { get; }
    public int StartupTimeoutSeconds { get; }
    public string DeclaredTarget { get; }
    public string DeclaredIdentity { get; }
    public bool AllowColdBackground { get; }
    public Sha256Digest TemplateDigest { get; }
    public Sha256Digest BootstrapDigest { get; }
    public int BootstrapByteCount => _bootstrapBytes.Length;
    public byte[] GetBootstrapBytes() => _bootstrapBytes.ToArray();
    internal ReadOnlySpan<byte> BootstrapSpan => _bootstrapBytes;

    private static void ValidateText(string value, int maximumScalars, string name)
    {
        ArgumentNullException.ThrowIfNull(value);
        var count = 0;
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
            if (status != OperationStatus.Done)
                throw new ArgumentException("Text must contain only valid Unicode scalar values.", name);
            count++;
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
                throw new ArgumentException("Text cannot contain control characters.", name);
            remaining = remaining[consumed..];
        }
        if (count is < 1 || count > maximumScalars)
            throw new ArgumentOutOfRangeException(name);
    }
}

public sealed record RecoveryBinding
{
    public RecoveryBinding(
        CanonicalAlias alias,
        RecoveryBindingKind bindingKind,
        CanonicalAlias? templateName,
        Sha256Digest? templateDigest,
        Sha256Digest? bootstrapDigest,
        bool allowColdBackground,
        DesiredSessionState desiredState,
        SessionTransitionVersion transitionVersion,
        Sha256Digest bindingDigest)
    {
        ArgumentNullException.ThrowIfNull(alias);
        ArgumentNullException.ThrowIfNull(transitionVersion);
        ArgumentNullException.ThrowIfNull(bindingDigest);
        if (!Enum.IsDefined(bindingKind)) throw new ArgumentOutOfRangeException(nameof(bindingKind));
        if (!Enum.IsDefined(desiredState)) throw new ArgumentOutOfRangeException(nameof(desiredState));
        var isDefault = alias.Value == "default";
        var templateFields = templateName is not null && templateDigest is not null && bootstrapDigest is not null;
        var valid = bindingKind switch
        {
            RecoveryBindingKind.Default => isDefault && !templateFields &&
                templateName is null && templateDigest is null && bootstrapDigest is null,
            RecoveryBindingKind.Dynamic => !isDefault && templateName is null &&
                templateDigest is null && bootstrapDigest is null,
            RecoveryBindingKind.Template => !isDefault && templateFields,
            _ => false,
        };
        if (!valid) throw new ArgumentException("Binding kind and template fields are inconsistent.");
        Alias = alias; BindingKind = bindingKind; TemplateName = templateName;
        TemplateDigest = templateDigest; BootstrapDigest = bootstrapDigest;
        AllowColdBackground = allowColdBackground; DesiredState = desiredState;
        TransitionVersion = transitionVersion; BindingDigest = bindingDigest;
    }

    public CanonicalAlias Alias { get; }
    public RecoveryBindingKind BindingKind { get; }
    public CanonicalAlias? TemplateName { get; }
    public Sha256Digest? TemplateDigest { get; }
    public Sha256Digest? BootstrapDigest { get; }
    public bool AllowColdBackground { get; }
    public DesiredSessionState DesiredState { get; }
    public SessionTransitionVersion TransitionVersion { get; }
    public Sha256Digest BindingDigest { get; }
}

public sealed record WorkerGenerationHighWatermarkEntry
{
    public WorkerGenerationHighWatermarkEntry(
        CanonicalAlias alias,
        WorkerGenerationHighWatermark generation)
    {
        ArgumentNullException.ThrowIfNull(alias);
        ArgumentNullException.ThrowIfNull(generation);
        Alias = alias;
        Generation = generation;
    }

    public CanonicalAlias Alias { get; }
    public WorkerGenerationHighWatermark Generation { get; }
}

public sealed record RecoveryManifest
{
    public RecoveryManifest(
        GuardianBootId guardianBootId,
        HostGeneration hostGeneration,
        Sha256Digest catalogDigest,
        Sha256Digest configurationDigest,
        IEnumerable<RecoveryTemplate> templates,
        IEnumerable<RecoveryBinding> bindings,
        IEnumerable<WorkerGenerationHighWatermarkEntry> workerGenerationHighWatermarks,
        HostGeneration hostGenerationHighWatermark)
    {
        ArgumentNullException.ThrowIfNull(guardianBootId);
        ArgumentNullException.ThrowIfNull(hostGeneration);
        ArgumentNullException.ThrowIfNull(catalogDigest);
        ArgumentNullException.ThrowIfNull(configurationDigest);
        ArgumentNullException.ThrowIfNull(hostGenerationHighWatermark);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(workerGenerationHighWatermarks);
        var frozenTemplates = templates.ToArray();
        var frozenBindings = bindings.ToArray();
        var frozenMarks = workerGenerationHighWatermarks.ToArray();
        if (frozenTemplates.Any(value => value is null) ||
            frozenBindings.Any(value => value is null) ||
            frozenMarks.Any(value => value is null))
            throw new ArgumentException("Manifest collections cannot contain null entries.");
        RequireOrdered(frozenTemplates.Select(value => value.Name.Value), ContractLimits.MaximumTemplates, "templates");
        RequireOrdered(frozenBindings.Select(value => value.Alias.Value), ContractLimits.MaximumAliases, "bindings", requireNonempty: true);
        RequireOrdered(frozenMarks.Select(value => value.Alias.Value), ContractLimits.MaximumAliases, "worker high watermarks", requireNonempty: true);
        if (hostGeneration != hostGenerationHighWatermark)
            throw new ArgumentException("Host generation must equal its high watermark.");
        if (frozenBindings.Count(value => value.Alias.Value == "default" &&
                value.BindingKind == RecoveryBindingKind.Default) != 1)
            throw new ArgumentException("Manifest must contain exactly one default binding.");
        var marks = frozenMarks.Select(value => value.Alias.Value).ToHashSet(StringComparer.Ordinal);
        if (frozenBindings.Any(value => !marks.Contains(value.Alias.Value)))
            throw new ArgumentException("Every binding requires a worker-generation high watermark.");
        var byName = frozenTemplates.ToDictionary(value => value.Name.Value, StringComparer.Ordinal);
        foreach (var binding in frozenBindings.Where(value => value.BindingKind == RecoveryBindingKind.Template))
        {
            if (!byName.TryGetValue(binding.TemplateName!.Value, out var template) ||
                template.TemplateDigest != binding.TemplateDigest ||
                template.BootstrapDigest != binding.BootstrapDigest)
                throw new ArgumentException("Template binding does not match the frozen template.");
        }
        GuardianBootId = guardianBootId; HostGeneration = hostGeneration;
        CatalogDigest = catalogDigest; ConfigurationDigest = configurationDigest;
        Templates = Array.AsReadOnly(frozenTemplates); Bindings = Array.AsReadOnly(frozenBindings);
        WorkerGenerationHighWatermarks = Array.AsReadOnly(frozenMarks);
        HostGenerationHighWatermark = hostGenerationHighWatermark;
    }

    public string SchemaVersion => "ptk.recovery-manifest/1";
    public GuardianBootId GuardianBootId { get; }
    public HostGeneration HostGeneration { get; }
    public Sha256Digest CatalogDigest { get; }
    public Sha256Digest ConfigurationDigest { get; }
    public IReadOnlyList<RecoveryTemplate> Templates { get; }
    public IReadOnlyList<RecoveryBinding> Bindings { get; }
    public IReadOnlyList<WorkerGenerationHighWatermarkEntry> WorkerGenerationHighWatermarks { get; }
    public HostGeneration HostGenerationHighWatermark { get; }

    private static void RequireOrdered(
        IEnumerable<string> values,
        int maximum,
        string name,
        bool requireNonempty = false)
    {
        var array = values.ToArray();
        if (array.Length > maximum || requireNonempty && array.Length == 0 ||
            !array.SequenceEqual(array.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            array.Distinct(StringComparer.Ordinal).Count() != array.Length)
            throw new ArgumentException($"{name} must be bounded, unique, and ordinally ordered.", name);
    }
}
