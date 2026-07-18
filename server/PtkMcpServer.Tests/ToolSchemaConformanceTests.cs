using System.ComponentModel.DataAnnotations;
using System.Reflection;
using ModelContextProtocol.Server;
using PtkMcpServer.Tools;

namespace PtkMcpServer.Tests;

// JSON Schema draft 2020-12 conformance for generated MCP tool input schemas.
//
// The RangeAttribute (Type, string, string) constructor stores its operands as
// strings, and the schema mapper emits them verbatim, producing
// "minimum": "0" / "maximum": "..." string values. Draft 2020-12 requires
// numbers, and strict MCP clients (including the Anthropic API) reject the
// whole tools/list with a 400 when any tool schema violates this. These tests
// pin the invariant at the attribute level so the string-operand constructor
// cannot be reintroduced on any tool parameter.
public sealed class ToolSchemaConformanceTests
{
    // 2^53 - 1: the largest integral value exactly representable as a double,
    // and the bound Number.MAX_SAFE_INTEGER-style clients validate against.
    private const double MaxSafeInteger = 9007199254740991d;

    public static IEnumerable<object[]> ToolMethodParameters()
    {
        var toolTypes = typeof(OutputTool).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null);
        foreach (var type in toolTypes)
        {
            var toolMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null);
            foreach (var method in toolMethods)
            {
                foreach (var parameter in method.GetParameters())
                {
                    yield return [type.Name, method.Name, parameter.Name!, parameter];
                }
            }
        }
    }

    [Fact]
    public void Tool_discovery_finds_parameters()
    {
        Assert.NotEmpty(ToolMethodParameters());
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
