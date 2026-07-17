using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PtkSharedContracts;

public sealed class PublicServerIdentity
{
    public PublicServerIdentity(string name, string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(version);
        Name = name;
        Version = version;
    }

    public string Name { get; }
    public string Version { get; }
}

public sealed class PublicToolDefinition
{
    public PublicToolDefinition(string name, string description, JsonElement inputSchema)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(description);
        if (inputSchema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Tool input schema must be a JSON object.", nameof(inputSchema));
        Name = name;
        Description = description;
        InputSchema = inputSchema.Clone();
    }

    public string Name { get; }
    public string Description { get; }
    public JsonElement InputSchema { get; }
}

public sealed class PublicToolContractSnapshot
{
    private static readonly string[] FrozenToolNames =
    ["ptk_invoke", "ptk_job", "ptk_output", "ptk_reset", "ptk_session", "ptk_state"];

    public PublicToolContractSnapshot(
        string schemaVersion,
        PublicServerIdentity serverIdentity,
        string instructions,
        string recoveryDescription,
        IEnumerable<PublicToolDefinition> tools)
    {
        if (schemaVersion != "ptk.public-contract/1")
            throw new ArgumentException("Unknown public contract version.", nameof(schemaVersion));
        ArgumentNullException.ThrowIfNull(serverIdentity);
        ArgumentException.ThrowIfNullOrEmpty(instructions);
        ArgumentException.ThrowIfNullOrEmpty(recoveryDescription);
        ArgumentNullException.ThrowIfNull(tools);
        var frozen = tools.ToArray();
        if (frozen.Any(tool => tool is null) ||
            !frozen.Select(tool => tool.Name).SequenceEqual(FrozenToolNames, StringComparer.Ordinal))
            throw new ArgumentException("Tools must be the frozen ordered six-tool contract.", nameof(tools));
        SchemaVersion = schemaVersion;
        ServerIdentity = serverIdentity;
        Instructions = instructions;
        RecoveryDescription = recoveryDescription;
        Tools = Array.AsReadOnly(frozen);
    }

    public string SchemaVersion { get; }
    public PublicServerIdentity ServerIdentity { get; }
    public string Instructions { get; }
    public string RecoveryDescription { get; }
    public IReadOnlyList<PublicToolDefinition> Tools { get; }
}

public static class ContractResources
{
    private const string Prefix = "PtkSharedContracts.Contracts.";
    private static readonly IReadOnlySet<string> KnownAssets = new HashSet<string>(StringComparer.Ordinal)
    {
        "public-tool-contract.json",
        "public-recovery.schema.json",
        "public-state.schema.json",
        "guardian-host-protocol.json",
        "guardian-host-protocol.schema.json",
        "recovery-manifest.schema.json",
        "recovery-manifest.example.json",
    };

    public static byte[] ReadExact(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (!KnownAssets.Contains(fileName))
            throw new ArgumentException("Unknown shared contract resource.", nameof(fileName));
        using var stream = typeof(ContractResources).Assembly.GetManifestResourceStream(Prefix + fileName)
            ?? throw new InvalidOperationException($"Embedded contract resource '{fileName}' is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

public static class PublicToolContractResource
{
    private const string FileName = "public-tool-contract.json";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] ExactUtf8 => ContractResources.ReadExact(FileName);

    public static Sha256Digest ComputeDigest()
    {
        var bytes = ExactUtf8;
        var payload = WithoutOneFinalLf(bytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes("ptk.public-contract/1\0"));
        hash.AppendData(payload);
        return new Sha256Digest(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    public static PublicToolContractSnapshot Parse()
    {
        var bytes = ExactUtf8;
        _ = StrictUtf8.GetString(bytes);
        StrictJson.RejectBomCrAndInvalidFinalLf(bytes, requireFinalLf: true);
        using var document = JsonDocument.Parse(bytes, StrictJson.DocumentOptions);
        StrictJson.RejectDuplicateProperties(document.RootElement);
        var root = document.RootElement;
        StrictJson.RequirePropertyOrder(
            root,
            "schema_version", "server_identity", "instructions", "recovery_description", "tools_list");
        if (root.GetProperty("schema_version").GetString() != "ptk.public-contract/1")
            throw new InvalidDataException("Unknown public contract version.");

        var identity = root.GetProperty("server_identity");
        StrictJson.RequirePropertyOrder(identity, "name", "version");
        var list = root.GetProperty("tools_list");
        StrictJson.RequirePropertyOrder(list, "tools");
        var tools = new List<PublicToolDefinition>();
        foreach (var tool in list.GetProperty("tools").EnumerateArray())
        {
            StrictJson.RequirePropertyOrder(tool, "name", "description", "inputSchema");
            tools.Add(new PublicToolDefinition(
                StrictJson.RequiredString(tool, "name"),
                StrictJson.RequiredString(tool, "description"),
                tool.GetProperty("inputSchema").Clone()));
        }
        var names = tools.Select(tool => tool.Name).ToArray();
        if (!names.SequenceEqual(
                ["ptk_invoke", "ptk_job", "ptk_output", "ptk_reset", "ptk_session", "ptk_state"],
                StringComparer.Ordinal))
            throw new InvalidDataException("Public tool order is not the frozen six-tool contract.");

        return new PublicToolContractSnapshot(
            "ptk.public-contract/1",
            new PublicServerIdentity(
                StrictJson.RequiredString(identity, "name"),
                StrictJson.RequiredString(identity, "version")),
            StrictJson.RequiredString(root, "instructions"),
            StrictJson.RequiredString(root, "recovery_description"),
            tools.AsReadOnly());
    }

    private static ReadOnlySpan<byte> WithoutOneFinalLf(byte[] bytes)
    {
        StrictJson.RejectBomCrAndInvalidFinalLf(bytes, requireFinalLf: true);
        return bytes.AsSpan(0, bytes.Length - 1);
    }
}

internal static class StrictJson
{
    internal static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = ContractLimits.MaximumJsonDepth,
    };

    internal static void RejectBomCrAndInvalidFinalLf(ReadOnlySpan<byte> bytes, bool requireFinalLf)
    {
        if (bytes.Length == 0 ||
            bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ||
            bytes.IndexOf((byte)'\r') >= 0)
            throw new InvalidDataException("Contract bytes are not canonical strict UTF-8 JSON.");
        if (requireFinalLf && (bytes[^1] != (byte)'\n' ||
            bytes.Length > 1 && bytes[^2] == (byte)'\n'))
            throw new InvalidDataException("Contract resource must end in exactly one LF.");
    }

    internal static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException($"Duplicate JSON property '{property.Name}'.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    internal static void RequirePropertyOrder(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.EnumerateObject().Select(property => property.Name)
                .SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException("JSON properties are missing, unknown, or out of order.");
    }

    internal static string RequiredString(JsonElement value, string name)
    {
        var property = value.GetProperty(name);
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(property.GetString()))
            throw new InvalidDataException($"JSON property '{name}' must be a nonempty string.");
        return property.GetString()!;
    }
}
