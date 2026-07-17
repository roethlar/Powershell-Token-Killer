using System.Text;
using System.Text.Json;

namespace PtkSharedContracts;

public static class RecoveryManifestCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(RecoveryManifest manifest, bool appendFinalLf = false)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", manifest.SchemaVersion);
            writer.WriteString("guardian_boot_id", manifest.GuardianBootId.ToString());
            writer.WriteNumber("host_generation", manifest.HostGeneration.Value);
            writer.WriteString("catalog_digest", manifest.CatalogDigest.Value);
            writer.WriteString("configuration_sha256", manifest.ConfigurationDigest.Value);
            writer.WritePropertyName("templates"); writer.WriteStartArray();
            foreach (var value in manifest.Templates) WriteTemplate(writer, value);
            writer.WriteEndArray();
            writer.WritePropertyName("bindings"); writer.WriteStartArray();
            foreach (var value in manifest.Bindings) WriteBinding(writer, value);
            writer.WriteEndArray();
            writer.WritePropertyName("worker_generation_high_watermarks"); writer.WriteStartArray();
            foreach (var value in manifest.WorkerGenerationHighWatermarks)
            {
                writer.WriteStartObject(); writer.WriteString("alias", value.Alias.Value);
                writer.WriteNumber("generation", value.Generation.Value); writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteNumber("host_generation_high_watermark", manifest.HostGenerationHighWatermark.Value);
            writer.WriteEndObject();
        }
        var compact = stream.ToArray();
        var transferredBytes = checked(compact.Length + (appendFinalLf ? 1 : 0));
        if (compact.Length < 2 || transferredBytes > ContractLimits.MaximumManifestBytes)
            throw new InvalidDataException("Recovery manifest is outside its frozen bound.");
        return appendFinalLf ? [.. compact, (byte)'\n'] : compact;
    }

    public static RecoveryManifest DecodeForInitialize(
        ReadOnlyMemory<byte> encoded,
        Sha256Digest initializeConfigurationDigest) =>
        DecodeCore(encoded, initializeConfigurationDigest ??
            throw new ArgumentNullException(nameof(initializeConfigurationDigest)));

    private static RecoveryManifest DecodeCore(
        ReadOnlyMemory<byte> encoded,
        Sha256Digest initializeConfigurationDigest)
    {
        if (encoded.Length is < 2 or > ContractLimits.MaximumManifestBytes)
            throw new InvalidDataException("Recovery manifest is outside its frozen bound.");
        var bytes = encoded;
        var hasFinalLf = bytes.Span[^1] == (byte)'\n';
        if (hasFinalLf) bytes = bytes[..^1];
        if (bytes.Span.IndexOf((byte)'\r') >= 0 || bytes.Span.IndexOf((byte)'\n') >= 0)
            throw new InvalidDataException("Recovery manifest must be one compact JSON object.");
        _ = StrictUtf8.GetString(bytes.Span);
        using var document = JsonDocument.Parse(bytes, StrictJson.DocumentOptions);
        StrictJson.RejectDuplicateProperties(document.RootElement);
        var root = document.RootElement;
        StrictJson.RequirePropertyOrder(root, "schema_version", "guardian_boot_id", "host_generation",
            "catalog_digest", "configuration_sha256", "templates", "bindings",
            "worker_generation_high_watermarks", "host_generation_high_watermark");
        if (root.GetProperty("schema_version").GetString() != "ptk.recovery-manifest/1")
            throw new InvalidDataException("Unknown recovery manifest version.");
        var templates = root.GetProperty("templates").EnumerateArray().Select(ReadTemplate).ToArray();
        var bindings = root.GetProperty("bindings").EnumerateArray().Select(ReadBinding).ToArray();
        var marks = root.GetProperty("worker_generation_high_watermarks").EnumerateArray().Select(value =>
        {
            StrictJson.RequirePropertyOrder(value, "alias", "generation");
            return new WorkerGenerationHighWatermarkEntry(
                new CanonicalAlias(StrictJson.RequiredString(value, "alias")),
                new WorkerGenerationHighWatermark(RequiredLong(value, "generation")));
        }).ToArray();
        var manifest = new RecoveryManifest(
            new GuardianBootId(ParseCanonicalUuidV4(StrictJson.RequiredString(root, "guardian_boot_id"))),
            new HostGeneration(RequiredLong(root, "host_generation")),
            new Sha256Digest(StrictJson.RequiredString(root, "catalog_digest")),
            new Sha256Digest(StrictJson.RequiredString(root, "configuration_sha256")),
            templates, bindings, marks,
            new HostGeneration(RequiredLong(root, "host_generation_high_watermark")));
        if (manifest.ConfigurationDigest != initializeConfigurationDigest)
            throw new InvalidDataException("Manifest configuration digest does not match initialize.");
        if (!Encode(manifest, appendFinalLf: hasFinalLf).AsSpan().SequenceEqual(encoded.Span))
            throw new InvalidDataException("Recovery manifest is not in canonical compact form.");
        return manifest;
    }

    private static void WriteTemplate(Utf8JsonWriter writer, RecoveryTemplate value)
    {
        writer.WriteStartObject(); writer.WriteString("name", value.Name.Value);
        writer.WriteString("description", value.Description);
        writer.WriteNumber("startup_timeout_seconds", value.StartupTimeoutSeconds);
        writer.WriteString("declared_target", value.DeclaredTarget);
        writer.WriteString("declared_identity", value.DeclaredIdentity);
        writer.WriteBoolean("allow_cold_background", value.AllowColdBackground);
        writer.WriteString("template_digest", value.TemplateDigest.Value);
        writer.WriteString("bootstrap_digest", value.BootstrapDigest.Value);
        writer.WriteString("bootstrap_raw_base64", Convert.ToBase64String(value.BootstrapSpan));
        writer.WriteEndObject();
    }

    private static RecoveryTemplate ReadTemplate(JsonElement value)
    {
        StrictJson.RequirePropertyOrder(value, "name", "description", "startup_timeout_seconds",
            "declared_target", "declared_identity", "allow_cold_background", "template_digest",
            "bootstrap_digest", "bootstrap_raw_base64");
        byte[] bootstrap;
        try { bootstrap = Convert.FromBase64String(value.GetProperty("bootstrap_raw_base64").GetString()!); }
        catch (FormatException exception) { throw new InvalidDataException("Bootstrap is not canonical base64.", exception); }
        if (Convert.ToBase64String(bootstrap) != value.GetProperty("bootstrap_raw_base64").GetString())
            throw new InvalidDataException("Bootstrap is not canonical base64.");
        return new RecoveryTemplate(
            new CanonicalAlias(StrictJson.RequiredString(value, "name")),
            StrictJson.RequiredString(value, "description"),
            checked((int)RequiredLong(value, "startup_timeout_seconds")),
            StrictJson.RequiredString(value, "declared_target"),
            StrictJson.RequiredString(value, "declared_identity"),
            value.GetProperty("allow_cold_background").GetBoolean(),
            new Sha256Digest(StrictJson.RequiredString(value, "template_digest")),
            new Sha256Digest(StrictJson.RequiredString(value, "bootstrap_digest")),
            bootstrap);
    }

    private static void WriteBinding(Utf8JsonWriter writer, RecoveryBinding value)
    {
        writer.WriteStartObject(); writer.WriteString("alias", value.Alias.Value);
        writer.WriteString("binding_kind", value.BindingKind switch
        { RecoveryBindingKind.Default => "default", RecoveryBindingKind.Dynamic => "dynamic",
          RecoveryBindingKind.Template => "template", _ => throw new ArgumentOutOfRangeException() });
        WriteNullable(writer, "template_name", value.TemplateName?.Value);
        WriteNullable(writer, "template_digest", value.TemplateDigest?.Value);
        WriteNullable(writer, "bootstrap_digest", value.BootstrapDigest?.Value);
        writer.WriteBoolean("allow_cold_background", value.AllowColdBackground);
        writer.WriteString("desired_state", value.DesiredState switch
        {
            DesiredSessionState.Cold => "cold",
            DesiredSessionState.Ready => "ready",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        });
        writer.WriteNumber("transition_version", value.TransitionVersion.Value);
        writer.WriteString("binding_digest", value.BindingDigest.Value); writer.WriteEndObject();
    }

    private static RecoveryBinding ReadBinding(JsonElement value)
    {
        StrictJson.RequirePropertyOrder(value, "alias", "binding_kind", "template_name", "template_digest",
            "bootstrap_digest", "allow_cold_background", "desired_state", "transition_version", "binding_digest");
        var templateName = NullableString(value.GetProperty("template_name"));
        var templateDigest = NullableString(value.GetProperty("template_digest"));
        var bootstrapDigest = NullableString(value.GetProperty("bootstrap_digest"));
        return new RecoveryBinding(
            new CanonicalAlias(StrictJson.RequiredString(value, "alias")),
            StrictJson.RequiredString(value, "binding_kind") switch
            { "default" => RecoveryBindingKind.Default, "dynamic" => RecoveryBindingKind.Dynamic,
              "template" => RecoveryBindingKind.Template, _ => throw new InvalidDataException("Unknown binding kind.") },
            templateName is null ? null : new CanonicalAlias(templateName),
            templateDigest is null ? null : new Sha256Digest(templateDigest),
            bootstrapDigest is null ? null : new Sha256Digest(bootstrapDigest),
            value.GetProperty("allow_cold_background").GetBoolean(),
            StrictJson.RequiredString(value, "desired_state") switch
            { "cold" => DesiredSessionState.Cold, "ready" => DesiredSessionState.Ready,
              _ => throw new InvalidDataException("Unknown desired state.") },
            new SessionTransitionVersion(RequiredLong(value, "transition_version")),
            new Sha256Digest(StrictJson.RequiredString(value, "binding_digest")));
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    { if (value is null) writer.WriteNull(name); else writer.WriteString(name, value); }
    private static string? NullableString(JsonElement value) => value.ValueKind == JsonValueKind.Null
        ? null : value.ValueKind == JsonValueKind.String ? value.GetString() : throw new InvalidDataException();
    private static long RequiredLong(JsonElement value, string name) => value.GetProperty(name).TryGetInt64(out var parsed)
        ? parsed : throw new InvalidDataException($"'{name}' must be int64.");

    private static Guid ParseCanonicalUuidV4(string text)
    {
        if (!Guid.TryParseExact(text, "D", out var parsed) ||
            !string.Equals(text, parsed.ToString("D"), StringComparison.Ordinal) ||
            text[14] != '4' || text[19] is not ('8' or '9' or 'a' or 'b'))
            throw new InvalidDataException("Guardian boot ID must be a lowercase canonical UUIDv4.");
        return parsed;
    }
}
