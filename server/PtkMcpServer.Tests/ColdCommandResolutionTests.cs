using System.Diagnostics;
using System.Management.Automation;

namespace PtkMcpServer.Tests;

[Collection("ProcessEnvironment")]
public sealed class ColdCommandResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ptk-cold-resolution-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Windows_same_directory_ps1_precedes_PATHEXT_application()
    {
        Directory.CreateDirectory(_root);
        var script = Path.Combine(_root, "fixture.ps1");
        var application = Path.Combine(_root, "fixture.EXE");
        File.WriteAllText(script, "'script'");
        File.WriteAllText(application, "application");

        var resolved = ColdPathCommandResolver.Resolve(
            "fixture",
            _root,
            ".EXE;.CMD",
            windows: true,
            workingDirectory: _root);

        Assert.NotNull(resolved);
        Assert.Equal(CommandTypes.ExternalScript, resolved.CommandType);
        Assert.Equal(Path.GetFullPath(script), resolved.Source);
    }

    [Fact]
    public void Windows_unset_PATHEXT_does_not_invent_executable_extensions()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "fixture.exe"), "application");

        var resolved = ColdPathCommandResolver.Resolve(
            "fixture",
            _root,
            pathExtensionsValue: null,
            windows: true,
            workingDirectory: _root);

        Assert.Null(resolved);
    }

    [Fact]
    public void Windows_PATHEXT_application_precedes_extensionless_application()
    {
        Directory.CreateDirectory(_root);
        var withExtension = Path.Combine(_root, "fixture.EXE");
        var extensionless = Path.Combine(_root, "fixture");
        File.WriteAllText(withExtension, "pathext");
        File.WriteAllText(extensionless, "extensionless");

        var resolved = ColdPathCommandResolver.Resolve(
            "fixture",
            _root,
            ".EXE",
            windows: true,
            workingDirectory: _root);

        Assert.NotNull(resolved);
        Assert.Equal(CommandTypes.Application, resolved.CommandType);
        Assert.Equal(Path.GetFullPath(withExtension), resolved.Source);

        File.Delete(withExtension);
        resolved = ColdPathCommandResolver.Resolve(
            "fixture",
            _root,
            ".EXE",
            windows: true,
            workingDirectory: _root);
        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(extensionless), resolved.Source);
    }

    [Fact]
    public void Relative_PATH_is_uncertain_before_cwd_and_exact_after_cwd()
    {
        var tools = Directory.CreateDirectory(Path.Combine(_root, "tools"));
        var application = Path.Combine(tools.FullName, "fixture.EXE");
        File.WriteAllText(application, "application");

        var uncertain = ColdPathCommandResolver.Resolve(
            "fixture",
            "tools",
            ".EXE",
            windows: true);
        var resolved = ColdPathCommandResolver.Resolve(
            "fixture",
            "tools",
            ".EXE",
            windows: true,
            workingDirectory: _root);

        Assert.NotNull(uncertain);
        Assert.Equal((CommandTypes)0, uncertain.CommandType);
        Assert.True(uncertain.ResolutionUncertain);
        Assert.Null(uncertain.Source);
        Assert.NotNull(resolved);
        Assert.Equal(CommandTypes.Application, resolved.CommandType);
        Assert.Equal(Path.GetFullPath(application), resolved.Source);
    }

    [Fact]
    public void Windows_PATH_tokenization_ignores_empty_and_leading_space_without_dequoting()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "fixture.EXE"), "application");

        var empty = ColdPathCommandResolver.Resolve(
            "fixture",
            ";",
            ".EXE",
            windows: true,
            workingDirectory: _root);
        var quoted = ColdPathCommandResolver.Resolve(
            "fixture",
            $"\"{_root}\"",
            ".EXE",
            windows: true,
            workingDirectory: _root);
        var leadingSpace = ColdPathCommandResolver.Resolve(
            "fixture",
            "  " + _root,
            ".EXE",
            windows: true,
            workingDirectory: _root);

        Assert.False(empty?.CommandType == CommandTypes.Application);
        Assert.False(quoted?.CommandType == CommandTypes.Application);
        Assert.Equal(CommandTypes.Application, leadingSpace?.CommandType);
    }

    [Fact]
    public void Resolver_matches_live_PowerShell_PATH_tokenization_on_this_platform()
    {
        Directory.CreateDirectory(_root);
        var fileName = OperatingSystem.IsWindows() ? "fixture.EXE" : "fixture";
        var application = Path.Combine(_root, fileName);
        File.WriteAllText(application, "application");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                application,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var cases = new Dictionary<string, string>
        {
            ["exact"] = _root,
            ["empty"] = string.Empty,
            ["quoted"] = $"\"{_root}\"",
            ["trailing_space"] = _root + " ",
            ["leading_space"] = "  " + _root,
        };
        foreach (var (label, pathValue) in cases)
        {
            var resolved = ColdPathCommandResolver.Resolve(
                "fixture",
                pathValue,
                ".EXE",
                OperatingSystem.IsWindows(),
                _root);
            var live = ResolveWithLivePowerShell(pathValue);

            Assert.True(
                resolved?.CommandType == live?.CommandType &&
                string.Equals(
                    resolved?.Source,
                    live?.Source,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal),
                $"Resolver differed from live PowerShell for {label} PATH tokenization.");
        }
    }

    [Fact]
    public void Windows_PATHEXT_entries_are_appended_exactly_without_trimming_or_dot_invention()
    {
        Directory.CreateDirectory(_root);
        var spaced = Path.Combine(_root, "fixture .EXE");
        File.WriteAllText(spaced, "spaced extension");

        var resolved = ColdPathCommandResolver.Resolve(
            "fixture",
            _root,
            " .EXE",
            windows: true,
            workingDirectory: _root);

        Assert.NotNull(resolved);
        Assert.Equal(CommandTypes.Application, resolved.CommandType);
        Assert.Equal(Path.GetFullPath(spaced), resolved.Source);

        File.Delete(spaced);
        var withoutDot = Path.Combine(_root, "fixtureEXE");
        File.WriteAllText(withoutDot, "no dot extension");
        resolved = ColdPathCommandResolver.Resolve(
            "fixture",
            _root,
            "EXE",
            windows: true,
            workingDirectory: _root);

        Assert.NotNull(resolved);
        Assert.Equal(CommandTypes.Application, resolved.CommandType);
        Assert.Equal(Path.GetFullPath(withoutDot), resolved.Source);
    }

    [Fact]
    public void Windows_explicit_extension_still_honors_earlier_directory_script_precedence()
    {
        var first = Directory.CreateDirectory(Path.Combine(_root, "first"));
        var second = Directory.CreateDirectory(Path.Combine(_root, "second"));
        var script = Path.Combine(first.FullName, "fixture.exe.ps1");
        File.WriteAllText(script, "'script'");
        File.WriteAllText(Path.Combine(second.FullName, "fixture.exe"), "application");

        var resolved = ColdPathCommandResolver.Resolve(
            "fixture.exe",
            first.FullName + ";" + second.FullName,
            ".EXE",
            windows: true,
            workingDirectory: _root);

        Assert.NotNull(resolved);
        Assert.Equal(CommandTypes.ExternalScript, resolved.CommandType);
        Assert.Equal(Path.GetFullPath(script), resolved.Source);
    }

    [Fact]
    public void Windows_explicit_extension_prefers_exact_before_appended_script()
    {
        Directory.CreateDirectory(_root);
        var exact = Path.Combine(_root, "fixture.exe");
        File.WriteAllText(exact, "application");
        File.WriteAllText(Path.Combine(_root, "fixture.exe.ps1"), "'script'");

        var resolved = ColdPathCommandResolver.Resolve(
            "fixture.exe",
            _root,
            ".EXE",
            windows: true,
            workingDirectory: _root);

        Assert.NotNull(resolved);
        Assert.Equal(CommandTypes.Application, resolved.CommandType);
        Assert.Equal(Path.GetFullPath(exact), resolved.Source);
    }

    [Fact]
    public void Windows_PATHEXT_candidate_order_is_preserved()
    {
        Directory.CreateDirectory(_root);
        var first = Path.Combine(_root, "fixture.CMD");
        File.WriteAllText(first, "first");
        File.WriteAllText(Path.Combine(_root, "fixture.EXE"), "second");

        var resolved = ColdPathCommandResolver.Resolve(
            "fixture",
            _root,
            ".CMD;.EXE",
            windows: true,
            workingDirectory: _root);

        Assert.NotNull(resolved);
        Assert.Equal(CommandTypes.Application, resolved.CommandType);
        Assert.Equal(Path.GetFullPath(first), resolved.Source);
    }

    [Fact]
    public void Windows_candidate_order_matches_live_PowerShell()
    {
        if (!OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(_root);
        var exact = Path.Combine(_root, "fixture.EXE");
        var pathextFirst = Path.Combine(_root, "fixture.CMD");
        var script = Path.Combine(_root, "fixture.ps1");
        File.WriteAllText(exact, "application");
        File.WriteAllText(pathextFirst, "application");
        File.WriteAllText(script, "'script'");

        var resolved = ColdPathCommandResolver.Resolve(
            "fixture",
            _root,
            ".CMD;.EXE",
            windows: true,
            workingDirectory: _root);
        var live = ResolveWithLivePowerShell(_root, "fixture", ".CMD;.EXE");
        Assert.Equal(CommandTypes.ExternalScript, resolved?.CommandType);
        Assert.Equal(resolved?.CommandType, live?.CommandType);
        Assert.Equal(resolved?.Source, live?.Source, ignoreCase: true);

        File.Delete(script);
        resolved = ColdPathCommandResolver.Resolve(
            "fixture",
            _root,
            ".CMD;.EXE",
            windows: true,
            workingDirectory: _root);
        live = ResolveWithLivePowerShell(_root, "fixture", ".CMD;.EXE");
        Assert.Equal(Path.GetFullPath(pathextFirst), resolved?.Source);
        Assert.Equal(resolved?.Source, live?.Source, ignoreCase: true);

        File.WriteAllText(Path.Combine(_root, "fixture.exe.ps1"), "'script'");
        resolved = ColdPathCommandResolver.Resolve(
            "fixture.exe",
            _root,
            ".CMD;.EXE",
            windows: true,
            workingDirectory: _root);
        live = ResolveWithLivePowerShell(_root, "fixture.exe", ".CMD;.EXE");
        Assert.Equal(Path.GetFullPath(exact), resolved?.Source);
        Assert.Equal(resolved?.Source, live?.Source, ignoreCase: true);
    }

    [Fact]
    public void Frozen_target_identity_rejects_content_or_resolution_change()
    {
        Directory.CreateDirectory(_root);
        var first = Path.Combine(_root, "first.bin");
        var second = Path.Combine(_root, "second.bin");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        var captured = ColdCommandTargetIdentity.TryCapture(
            "fixture",
            new ResolvedCommand(CommandTypes.Application, first, first),
            _root);

        Assert.NotNull(captured);
        Assert.True(captured.Matches(
            new ResolvedCommand(CommandTypes.Application, first, first)));
        Assert.False(captured.Matches(
            new ResolvedCommand(CommandTypes.Application, second, second)));

        File.WriteAllText(first, "changed");
        Assert.False(captured.Matches(
            new ResolvedCommand(CommandTypes.Application, first, first)));
        Assert.False(captured.Matches(
            new ResolvedCommand(CommandTypes.ExternalScript, first, first)));
    }

    [Fact]
    public void Frozen_target_identity_requires_absolute_source_and_re_resolves_PATH()
    {
        Directory.CreateDirectory(_root);
        var firstDirectory = Directory.CreateDirectory(Path.Combine(_root, "first"));
        var secondDirectory = Directory.CreateDirectory(Path.Combine(_root, "second"));
        var fileName = OperatingSystem.IsWindows() ? "fixture.EXE" : "fixture";
        var first = Path.Combine(firstDirectory.FullName, fileName);
        var second = Path.Combine(secondDirectory.FullName, fileName);
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode executable =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(first, executable);
            File.SetUnixFileMode(second, executable);
        }

        var relativeSource = ".ptk-cold-relative-" + Guid.NewGuid().ToString("N");
        var relativeFixture = Path.Combine(Directory.GetCurrentDirectory(), relativeSource);
        File.WriteAllText(relativeFixture, "relative");
        try
        {
            Assert.False(Path.IsPathFullyQualified(relativeSource));
            Assert.Null(ColdCommandTargetIdentity.TryCapture(
                "fixture",
                new ResolvedCommand(
                    CommandTypes.Application,
                    relativeSource,
                    fileName),
                _root));
        }
        finally
        {
            File.Delete(relativeFixture);
        }

        var savedPath = Environment.GetEnvironmentVariable("PATH");
        var savedPathExtensions = Environment.GetEnvironmentVariable("PATHEXT");
        try
        {
            Environment.SetEnvironmentVariable("PATH", firstDirectory.FullName);
            Environment.SetEnvironmentVariable("PATHEXT", ".EXE");
            var resolved = ColdPathCommandResolver.Resolve("fixture", _root);
            var captured = ColdCommandTargetIdentity.TryCapture(
                "fixture",
                resolved,
                _root);

            Assert.NotNull(captured);
            Assert.True(captured.MatchesCurrentResolution());

            Environment.SetEnvironmentVariable("PATH", "first");
            Assert.True(captured.MatchesCurrentResolution());

            File.WriteAllText(first, "changed");
            Assert.False(captured.MatchesCurrentResolution());
            File.WriteAllText(first, "first");
            Assert.True(captured.MatchesCurrentResolution());

            Environment.SetEnvironmentVariable(
                "PATH",
                secondDirectory.FullName + Path.PathSeparator + firstDirectory.FullName);
            Assert.False(captured.MatchesCurrentResolution());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
            Environment.SetEnvironmentVariable("PATHEXT", savedPathExtensions);
        }
    }

    private ResolvedCommand? ResolveWithLivePowerShell(
        string pathValue,
        string commandName = "fixture",
        string pathExtensions = ".EXE")
    {
        var pwsh = JobPwshExecutable.ResolveFromPath().AbsolutePath;
        Assert.False(string.IsNullOrWhiteSpace(pwsh));
        var startInfo = new ProcessStartInfo
        {
            FileName = pwsh,
            WorkingDirectory = _root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            $"$c = Get-Command -Name '{commandName.Replace("'", "''")}' " +
            "-CommandType Application,ExternalScript " +
            "-ErrorAction SilentlyContinue; if ($null -ne $c) { " +
            "[Console]::Out.WriteLine(('{0}|{1}' -f $c.CommandType, $c.Source)) }; exit 0");
        startInfo.Environment["PATH"] = pathValue;
        startInfo.Environment["PATHEXT"] = pathExtensions;

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        if (output.Length == 0) return null;

        var separator = output.IndexOf('|');
        Assert.True(separator > 0, output);
        Assert.True(Enum.TryParse<CommandTypes>(output[..separator], out var commandType));
        var source = output[(separator + 1)..];
        return new ResolvedCommand(commandType, source, source);
    }
}
