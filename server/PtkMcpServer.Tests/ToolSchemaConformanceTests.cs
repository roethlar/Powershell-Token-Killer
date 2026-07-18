using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PtkMcpServer.Tools;

namespace PtkMcpServer.Tests;

// JSON Schema draft 2020-12 conformance for generated MCP tool input schemas.
//
// The RangeAttribute (Type, string, string) constructor stores its operands as
// strings, and the schema mapper emits them verbatim, producing
// "minimum": "0" / "maximum": "..." string values. Draft 2020-12 requires
// numbers, and strict MCP clients (including the Anthropic API) reject the
// whole tools/list with a 400 when any tool schema violates this.
//
// The primary guard here operates on the ACTUAL generated schemas: every
// [McpServerTool] method in the server assembly is materialized through the
// same SDK factory the production server uses (McpServerTool.Create), and the
// resulting input_schema JSON is walked by a schema-position-aware structural
// validator for draft 2020-12 keyword typing. A separate self-test proves the
// validator detects the legacy string-operand attribute, so the guard is not
// tautological. Attribute-level pins remain as a fast early signal.
public sealed class ToolSchemaConformanceTests
{
    // 2^53 - 1: the largest integral value exactly representable as a double,
    // and the bound Number.MAX_SAFE_INTEGER-style clients validate against.
    private const double MaxSafeInteger = 9007199254740991d;

    // ---------------------------------------------------------------------
    // Schema generation through the production SDK factory
    // ---------------------------------------------------------------------

    private static readonly Lazy<IServiceProvider> SchemaServices = new(BuildSchemaServices);

    // Registers every DI-injected tool parameter type (discovered by
    // convention: any parameter that is not schema-eligible and not a
    // framework-bound type) so IServiceProviderIsService reports it as a
    // service and the factory excludes it from the schema, exactly as the
    // production host does. The factories throw: schema generation must never
    // resolve an instance.
    private static IServiceProvider BuildSchemaServices()
    {
        var services = new ServiceCollection();
        var serviceTypes = ToolMethods()
            .SelectMany(entry => entry.Method.GetParameters())
            .Where(parameter => !IsSchemaEligible(parameter.ParameterType)
                && parameter.ParameterType != typeof(CancellationToken))
            .Select(parameter => parameter.ParameterType)
            .Distinct();
        foreach (var serviceType in serviceTypes)
        {
            services.AddSingleton(serviceType, _ => throw new InvalidOperationException(
                $"{serviceType.Name} is registered for schema generation only and must never be resolved."));
        }
        return services.BuildServiceProvider();
    }

    private static bool IsSchemaEligible(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(string)
            || underlying == typeof(decimal)
            || underlying.IsPrimitive
            || underlying.IsEnum;
    }

    private static IEnumerable<(Type Type, MethodInfo Method)> ToolMethods()
    {
        var toolTypes = typeof(OutputTool).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .OrderBy(type => type.FullName, StringComparer.Ordinal);
        foreach (var type in toolTypes)
        {
            var toolMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
                .OrderBy(method => method.Name, StringComparer.Ordinal);
            foreach (var method in toolMethods)
            {
                yield return (type, method);
            }
        }
    }

    private static string GenerateInputSchemaJson(MethodInfo method)
    {
        var tool = McpServerTool.Create(
            method,
            target: null,
            new McpServerToolCreateOptions { Services = SchemaServices.Value });
        return tool.ProtocolTool.InputSchema.GetRawText();
    }

    public static IEnumerable<object[]> GeneratedToolSchemas()
    {
        foreach (var (type, method) in ToolMethods())
        {
            yield return [$"{type.Name}.{method.Name}", GenerateInputSchemaJson(method)];
        }
    }

    private static JsonDocument ParseStrict(string json)
        => JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });

    [Fact]
    public void Tool_discovery_finds_all_five_tools()
    {
        // One [McpServerTool] method per tool class; update alongside the
        // public contract when a tool is added.
        Assert.Equal(5, ToolMethods().Count());
    }

    [Theory]
    [MemberData(nameof(GeneratedToolSchemas))]
    public void Generated_input_schema_conforms_to_draft_2020_12(string tool, string schemaJson)
    {
        using var document = ParseStrict(schemaJson);
        var violations = new List<string>();
        ValidateSchema(document.RootElement, "#", violations);
        Assert.True(violations.Count == 0, $"{tool}: {string.Join("; ", violations)}");
    }

    [Fact]
    public void Generated_output_offset_bounds_are_numeric_and_match_public_contract()
    {
        var method = typeof(OutputTool).GetMethod(nameof(OutputTool.Output))!;
        using var document = ParseStrict(GenerateInputSchemaJson(method));
        var offset = document.RootElement.GetProperty("properties").GetProperty("offset");
        Assert.Equal(JsonValueKind.Number, offset.GetProperty("minimum").ValueKind);
        Assert.Equal(JsonValueKind.Number, offset.GetProperty("maximum").ValueKind);
        Assert.Equal(0d, offset.GetProperty("minimum").GetDouble());
        Assert.Equal(MaxSafeInteger, offset.GetProperty("maximum").GetDouble());
    }

    // Proves the validator is not tautological: the legacy string-operand
    // attribute, run through the same SDK factory, must produce a schema the
    // walker rejects. If this test ever fails because the SDK begins coercing
    // string operands to numbers, the pinned failure mode is gone and this
    // guard should be re-examined — that is a signal, not a nuisance.
    [Fact]
    public void Validator_detects_legacy_string_operand_range_attribute()
    {
        var method = typeof(ToolSchemaConformanceTests).GetMethod(
            nameof(LegacyStringRangeProbe), BindingFlags.NonPublic | BindingFlags.Static)!;
        using var document = ParseStrict(GenerateInputSchemaJson(method));
        var violations = new List<string>();
        ValidateSchema(document.RootElement, "#", violations);
        Assert.NotEmpty(violations);
        Assert.Contains(violations, violation => violation.Contains("minimum") || violation.Contains("maximum"));
    }

    private static void LegacyStringRangeProbe(
        [Range(typeof(long), "0", "9223372036854775807")] long offset)
        => _ = offset;

    // ---------------------------------------------------------------------
    // Draft 2020-12 structural validator (schema-position aware)
    // ---------------------------------------------------------------------

    private static readonly HashSet<string> AllowedTypeNames = new(StringComparer.Ordinal)
    {
        "null", "boolean", "object", "array", "number", "string", "integer",
    };

    private static readonly HashSet<string> NumericOperandKeywords = new(StringComparer.Ordinal)
    {
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
    };

    private static readonly HashSet<string> NonNegativeIntegerKeywords = new(StringComparer.Ordinal)
    {
        "minLength", "maxLength", "minItems", "maxItems",
        "minContains", "maxContains", "minProperties", "maxProperties",
    };

    private static readonly HashSet<string> StringKeywords = new(StringComparer.Ordinal)
    {
        "title", "description", "$comment", "pattern", "format", "$ref", "$anchor", "$schema", "$id",
    };

    private static readonly HashSet<string> SingleSubschemaKeywords = new(StringComparer.Ordinal)
    {
        "items", "contains", "additionalProperties", "propertyNames",
        "not", "if", "then", "else", "unevaluatedItems", "unevaluatedProperties",
    };

    private static readonly HashSet<string> SubschemaMapKeywords = new(StringComparer.Ordinal)
    {
        "properties", "patternProperties", "$defs", "definitions", "dependentSchemas",
    };

    private static readonly HashSet<string> SubschemaArrayKeywords = new(StringComparer.Ordinal)
    {
        "allOf", "anyOf", "oneOf", "prefixItems",
    };

    private static void ValidateSchema(JsonElement schema, string path, List<string> violations)
    {
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            violations.Add($"{path}: a schema must be an object or a boolean, found {schema.ValueKind}");
            return;
        }

        foreach (var property in schema.EnumerateObject())
        {
            var name = property.Name;
            var value = property.Value;
            var location = $"{path}/{name}";

            if (NumericOperandKeywords.Contains(name))
            {
                if (value.ValueKind != JsonValueKind.Number)
                {
                    violations.Add($"{location}: draft 2020-12 requires a number, found {value.ValueKind} {value.GetRawText()}");
                    continue;
                }

                var operand = value.GetDouble();
                if (operand < -MaxSafeInteger || operand > MaxSafeInteger)
                {
                    violations.Add($"{location}: operand {value.GetRawText()} exceeds the interoperable safe-integer range");
                }

                if (name == "multipleOf" && operand <= 0)
                {
                    violations.Add($"{location}: multipleOf must be strictly positive");
                }
            }
            else if (NonNegativeIntegerKeywords.Contains(name))
            {
                if (value.ValueKind != JsonValueKind.Number
                    || !value.TryGetInt64(out var integer)
                    || integer < 0)
                {
                    violations.Add($"{location}: draft 2020-12 requires a non-negative integer, found {value.GetRawText()}");
                }
            }
            else if (StringKeywords.Contains(name))
            {
                if (value.ValueKind != JsonValueKind.String)
                {
                    violations.Add($"{location}: must be a string, found {value.ValueKind}");
                }
            }
            else if (name == "type")
            {
                ValidateTypeKeyword(value, location, violations);
            }
            else if (name == "required")
            {
                if (value.ValueKind != JsonValueKind.Array
                    || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                {
                    violations.Add($"{location}: must be an array of strings");
                }
            }
            else if (name == "enum")
            {
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
                {
                    violations.Add($"{location}: must be a non-empty array");
                }
            }
            else if (SingleSubschemaKeywords.Contains(name))
            {
                // Draft 2020-12 "items" is a single schema; the pre-2020-12
                // array form moved to "prefixItems".
                ValidateSchema(value, location, violations);
            }
            else if (SubschemaMapKeywords.Contains(name))
            {
                if (value.ValueKind != JsonValueKind.Object)
                {
                    violations.Add($"{location}: must be an object of subschemas, found {value.ValueKind}");
                    continue;
                }

                foreach (var entry in value.EnumerateObject())
                {
                    ValidateSchema(entry.Value, $"{location}/{entry.Name}", violations);
                }
            }
            else if (SubschemaArrayKeywords.Contains(name))
            {
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
                {
                    violations.Add($"{location}: must be a non-empty array of subschemas, found {value.ValueKind}");
                    continue;
                }

                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    ValidateSchema(item, $"{location}/{index}", violations);
                    index++;
                }
            }

            // Unknown keywords are annotations under draft 2020-12 and pass
            // through ("default", "examples", "const", vendor extensions).
        }
    }

    private static void ValidateTypeKeyword(JsonElement value, string location, List<string> violations)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            if (!AllowedTypeNames.Contains(value.GetString()!))
            {
                violations.Add($"{location}: unknown type '{value.GetString()}'");
            }

            return;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String
                    || !AllowedTypeNames.Contains(item.GetString()!)
                    || !seen.Add(item.GetString()!))
                {
                    violations.Add($"{location}: type arrays must be unique arrays of primitive type names");
                    return;
                }
            }

            return;
        }

        violations.Add($"{location}: must be a string or array of strings, found {value.ValueKind}");
    }

    // ---------------------------------------------------------------------
    // Attribute-level pins (fast early signal; the generated-schema tests
    // above are the authoritative guard)
    // ---------------------------------------------------------------------

    public static IEnumerable<object[]> ToolMethodParameters()
    {
        foreach (var (type, method) in ToolMethods())
        {
            foreach (var parameter in method.GetParameters())
            {
                yield return [type.Name, method.Name, parameter.Name!, parameter];
            }
        }
    }

    [Theory]
    [MemberData(nameof(ToolMethodParameters))]
    public void Range_attributes_use_numeric_operands_within_safe_integer_bounds(
        string toolType, string toolMethod, string parameterName, ParameterInfo parameter)
    {
        var range = parameter.GetCustomAttribute<RangeAttribute>();
        if (range is null)
        {
            return;
        }

        var label = $"{toolType}.{toolMethod}({parameterName})";
        Assert.False(range.Minimum is string, $"{label}: Range minimum is a string; draft 2020-12 requires a number.");
        Assert.False(range.Maximum is string, $"{label}: Range maximum is a string; draft 2020-12 requires a number.");

        var minimum = Convert.ToDouble(range.Minimum, System.Globalization.CultureInfo.InvariantCulture);
        var maximum = Convert.ToDouble(range.Maximum, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(minimum, -MaxSafeInteger, MaxSafeInteger);
        Assert.InRange(maximum, minimum, MaxSafeInteger);
    }

    [Fact]
    public void Output_offset_range_matches_public_contract()
    {
        var parameter = typeof(OutputTool).GetMethod(nameof(OutputTool.Output))!
            .GetParameters().Single(p => p.Name == "offset");
        var range = parameter.GetCustomAttribute<RangeAttribute>()!;
        Assert.Equal(0d, range.Minimum);
        Assert.Equal(MaxSafeInteger, range.Maximum);
    }
}
