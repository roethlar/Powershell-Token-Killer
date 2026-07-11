using System.Management.Automation;

namespace PtkMcpServer.Tests;

public sealed class TrustedPreflightClassifierTests
{
    private const string RtkPath = "/trusted/rtk";

    [Fact]
    public void Snapshot_is_case_insensitive_type_exact_and_cloneable()
    {
        var original = new TrustedCommandSnapshot();
        original.Set("GiT", CommandTypes.All,
            new ResolvedCommand(CommandTypes.Application, "/usr/bin/git"));
        original.Set("missing", CommandTypes.All, null);

        var clone = original.Clone();
        clone.Set("git", CommandTypes.All,
            new ResolvedCommand(CommandTypes.Function, Definition: "shadow"));

        Assert.Equal(CommandTypes.Application, original.Resolve("git", CommandTypes.All)!.CommandType);
        Assert.Equal(CommandTypes.Function, clone.Resolve("GIT", CommandTypes.All)!.CommandType);
        Assert.Null(original.Resolve("git", CommandTypes.Application));
        Assert.Null(original.Resolve("MISSING", CommandTypes.All));
        Assert.Null(original.Resolve("uncaptured", CommandTypes.All));
    }

    [Fact]
    public void Required_names_include_nested_and_error_recovered_commands_once()
    {
        var names = TrustedPreflightClassifier.GetRequiredCommandNames(
            "function f { export X=1 }; EXPORT Y=2; if $true; then");

        Assert.Contains("export", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("then", names, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1, names.Count(name => name.Equals("export", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Resolver_rewrites_one_application_and_preserves_constant_text()
    {
        var commands = Application("git", "/usr/bin/git");

        Assert.Equal(
            "& '/trusted/rtk' git commit -m \"hello world\"",
            TrustedPreflightClassifier.ResolveScript(
                "git commit -m \"hello world\"", "auto", RtkPath, commands));
        Assert.Equal(
            "& '/trusted/o''brien/rtk' git status",
            TrustedPreflightClassifier.ResolveScript(
                "git status", "auto", "/trusted/o'brien/rtk", commands));
    }

    [Theory]
    [InlineData("")]
    [InlineData("param(); git status")]
    [InlineData("begin { git status }")]
    [InlineData("process { git status }")]
    [InlineData("git status; git diff")]
    [InlineData("if ($true) { git status }")]
    [InlineData("git status | Out-Null")]
    [InlineData("1 + 2")]
    [InlineData("& git status")]
    [InlineData("git log -1 > out.txt")]
    [InlineData("$cmd status")]
    [InlineData("rtk gain")]
    [InlineData("/opt/RTK.EXE gain")]
    [InlineData("git commit -m \"$msg\"")]
    [InlineData("git -flag:$value")]
    [InlineData("git status ||| (")]
    public void Resolver_keeps_every_non_single_constant_command_shape_unchanged(string script)
    {
        Assert.Equal(
            script,
            TrustedPreflightClassifier.ResolveScript(
                script, "auto", RtkPath, Application("git", "/usr/bin/git")));
    }

    [Theory]
    [InlineData(CommandTypes.Alias, null, null)]
    [InlineData(CommandTypes.Function, null, null)]
    [InlineData(CommandTypes.Cmdlet, null, null)]
    [InlineData(CommandTypes.ExternalScript, "/tmp/git.ps1", null)]
    [InlineData(CommandTypes.Application, "/tmp/git.cmd", null)]
    [InlineData(CommandTypes.Application, "/tmp/git.BAT", null)]
    public void Auto_route_keeps_non_native_or_batch_resolution_on_PowerShell(
        CommandTypes type,
        string? source,
        string? definition)
    {
        var commands = new TrustedCommandSnapshot();
        commands.Set("git", CommandTypes.All, new ResolvedCommand(type, source, definition));

        Assert.Equal(
            "git status",
            TrustedPreflightClassifier.ResolveScript("git status", "auto", RtkPath, commands));
    }

    [Fact]
    public void Resolver_honors_absent_rtk_pwsh_and_forced_rtk_contracts()
    {
        var commands = new TrustedCommandSnapshot();
        commands.Set("Get-ChildItem", CommandTypes.All,
            new ResolvedCommand(CommandTypes.Cmdlet));

        Assert.Equal(
            "git status",
            TrustedPreflightClassifier.ResolveScript("git status", "auto", null, commands));
        Assert.Equal(
            "git status",
            TrustedPreflightClassifier.ResolveScript("git status", "auto", string.Empty, commands));
        Assert.Equal(
            "git status",
            TrustedPreflightClassifier.ResolveScript("git status", "PWSH", RtkPath, commands));
        Assert.Equal(
            "& '/trusted/rtk' Get-ChildItem",
            TrustedPreflightClassifier.ResolveScript("Get-ChildItem", "RTK", RtkPath, commands));
        Assert.Equal(
            "git status | Out-Null",
            TrustedPreflightClassifier.ResolveScript(
                "git status | Out-Null", "rtk", RtkPath, commands));
    }

    public static TheoryData<string, string> BashDetections => new()
    {
        { "cat <<EOF\nhello\nEOF", "heredoc" },
        { "cat <<'EOF'\nhello\nEOF", "heredoc" },
        { "if [ -f x.txt ]; then echo hi; fi", "if/then" },
        { "[ -f x.txt ]", "test expression" },
        { "[[ -f x.txt ]]", "test expression" },
        { "for i in 1 2 3; do echo $i; done", "do/done" },
        { "greet() { echo hi; }", "function definition" },
        { "diff <(sort a.txt) <(sort b.txt)", "process substitution" },
        { "export FOO=1", "export" },
        { "FOO=bar echo hi", "environment-variable prefix" },
        { "local x=1", "local" },
        { "source ./env.sh", "source" },
        { "set -e", "shell options" },
        { "set -euo pipefail", "shell options" },
        { "echo `date`", "backticks" },
        { "echo `date +%s`", "backticks" },
    };

    [Theory]
    [MemberData(nameof(BashDetections))]
    public void Dialect_classifier_names_every_frozen_construct(string script, string expected)
    {
        var finding = TrustedPreflightClassifier.GetShellDialectFinding(
            script, StockCommands());

        Assert.Contains(expected, finding, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string> FalsePositiveScripts => new()
    {
        "echo hi && echo there",
        "Get-Date | Out-String",
        "node --version 2>/dev/null",
        "echo $(1+1)",
        "echo 'literal $x'",
        "bash -lc 'echo hi'",
        "bash -lc 'local x=1; export FOO=1'",
        "git commit -m 'set -e belongs in the message'",
        "Set-Variable -Name x -Value 1",
        "set",
        "dotnet test --filter Name=Foo",
        "source $path",
        "Write-Host `n",
        "Write-Host `n `t",
        "echo 'a `date` b'",
        "Write-Output `tColumn` Name",
        "Get-ChildItem C:\\Temp\\",
        "echo a \\\necho b",
    };

    [Theory]
    [MemberData(nameof(FalsePositiveScripts))]
    public void Dialect_classifier_keeps_the_frozen_false_positive_set_silent(string script)
    {
        Assert.Null(TrustedPreflightClassifier.GetShellDialectFinding(script, StockCommands()));
    }

    [Theory]
    [InlineData(CommandTypes.Alias)]
    [InlineData(CommandTypes.Function)]
    [InlineData(CommandTypes.Cmdlet)]
    [InlineData(CommandTypes.Application)]
    public void Any_real_warm_resolution_exempts_a_bash_collision(CommandTypes type)
    {
        var commands = StockCommands();
        commands.Set("export", CommandTypes.All,
            new ResolvedCommand(type, type == CommandTypes.Application ? "/usr/bin/export" : null));

        Assert.Null(TrustedPreflightClassifier.GetShellDialectFinding("export X=1", commands));
    }

    [Theory]
    [InlineData("function export { param($Assignment) $Assignment }; export X=1")]
    [InlineData("Set-Alias export Write-Output; export X=1")]
    [InlineData("Set-Alias -Name export -Value Write-Output; export X=1")]
    [InlineData("Set-Alias -Name:export -Value Write-Output; export X=1")]
    [InlineData("New-Alias export Write-Output; export X=1")]
    [InlineData("Set-Alias set __mySet; set -e")]
    public void Preceding_supported_local_definitions_exempt_their_uses(string script)
    {
        Assert.Null(TrustedPreflightClassifier.GetShellDialectFinding(script, StockCommands()));
    }

    [Theory]
    [InlineData("export X=1; Set-Alias export Write-Output")]
    [InlineData("export X=1; function export { param($value) $value }")]
    [InlineData("Set-Alias -Scope Global -Name export -Value Write-Output; export X=1")]
    public void Later_or_unsupported_local_definitions_do_not_exempt_the_use(string script)
    {
        Assert.Contains(
            "export",
            TrustedPreflightClassifier.GetShellDialectFinding(script, StockCommands()),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_recursive_use_inside_its_own_definition_is_exempt()
    {
        const string script =
            "function export { param($Assignment) if ($Assignment -eq 'again') { export X=1 } else { $Assignment } }; export again";

        Assert.Null(TrustedPreflightClassifier.GetShellDialectFinding(script, StockCommands()));
    }

    [Theory]
    [InlineData(CommandTypes.Alias, "Set-Variable", true)]
    [InlineData(CommandTypes.Alias, "Write-Output", false)]
    [InlineData(CommandTypes.Function, null, false)]
    [InlineData(CommandTypes.Application, null, false)]
    public void Set_flags_only_while_resolution_is_the_stock_alias(
        CommandTypes type,
        string? definition,
        bool shouldFlag)
    {
        var commands = new TrustedCommandSnapshot();
        commands.Set("set", CommandTypes.All, new ResolvedCommand(type, Definition: definition));

        var finding = TrustedPreflightClassifier.GetShellDialectFinding("set -euo pipefail", commands);

        Assert.Equal(shouldFlag, finding?.Contains("shell options", StringComparison.OrdinalIgnoreCase) == true);
    }

    public static TheoryData<string> BlankedOrUnrelatedParseFatalEvidence => new()
    {
        "Write-Output > # cat <<EOF",
        "Write-Output x\"<<EOF\" >",
        "if x\"foo`\"then\"",
        "Write-Output >; Write-Output x\"foo`\n<<EOF\"",
        "Write-Output >; $x = @'\ncat <<EOF\n'@",
        "Write-Output then; if $true",
        "if $true; Write-Output then",
        "function then { 'ok' }; if $true; Get-Date; then",
        "Set-Alias then Write-Output; if $true; Get-Date; then",
        "foo 'bar'() { echo hi; }",
    };

    [Theory]
    [MemberData(nameof(BlankedOrUnrelatedParseFatalEvidence))]
    public void Parse_fatal_evidence_is_blanked_local_and_never_synthesized(string script)
    {
        Assert.Null(TrustedPreflightClassifier.GetShellDialectFinding(script, StockCommands()));
    }

    [Theory]
    [InlineData(CommandTypes.Alias)]
    [InlineData(CommandTypes.Function)]
    [InlineData(CommandTypes.Application)]
    public void Resolved_parse_recovery_keyword_is_not_bash_evidence(CommandTypes type)
    {
        var commands = StockCommands();
        commands.Set("then", CommandTypes.All,
            new ResolvedCommand(type, type == CommandTypes.Application ? "/usr/bin/then" : null));

        Assert.Null(TrustedPreflightClassifier.GetShellDialectFinding(
            "if $true; Get-Date; then", commands));
    }

    private static TrustedCommandSnapshot Application(string name, string source)
    {
        var commands = new TrustedCommandSnapshot();
        commands.Set(name, CommandTypes.All,
            new ResolvedCommand(CommandTypes.Application, source));
        return commands;
    }

    private static TrustedCommandSnapshot StockCommands()
    {
        var commands = new TrustedCommandSnapshot();
        commands.Set("set", CommandTypes.All,
            new ResolvedCommand(CommandTypes.Alias, Definition: "Set-Variable"));
        return commands;
    }
}
