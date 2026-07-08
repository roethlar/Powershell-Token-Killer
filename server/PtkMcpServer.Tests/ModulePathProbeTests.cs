using Xunit;

namespace PtkMcpServer.Tests;

/// <summary>
/// Guards the module-discovery probe order (release-distribution plan,
/// slice 1): an installed layout beside the binary must win over a repo
/// checkout the session's cwd happens to sit in, and the cwd probe must
/// still work when nothing ships alongside the binary.
/// </summary>
// ProcessEnvironment collection: mutates PTK_MODULE_PATH, which a parallel
// reset-driven environment restore would otherwise wipe mid-test.
[Collection("ProcessEnvironment")]
public class ModulePathProbeTests
{
    /// <summary>Copies the real module (psd1 + psm1) into {root}/src so a
    /// concurrently constructed RunspaceHost that races onto the planted
    /// copy imports identical content.</summary>
    private static string PlantModule(string root)
    {
        var srcDir = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        var repoSrc = FindRepoSrc();
        var psd1 = Path.Combine(srcDir, "PwshTokenCompressor.psd1");
        File.Copy(Path.Combine(repoSrc, "PwshTokenCompressor.psd1"), psd1, overwrite: true);
        File.Copy(
            Path.Combine(repoSrc, "PwshTokenCompressor.psm1"),
            Path.Combine(srcDir, "PwshTokenCompressor.psm1"),
            overwrite: true);
        return psd1;
    }

    private static string FindRepoSrc()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PwshTokenCompressor.psd1");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "src");
            }
        }
        throw new InvalidOperationException("Repo src/ not found upward from the test base directory.");
    }

    [Fact]
    public void InstalledLayoutBesideBinaryWinsOverCwdCheckout()
    {
        var plantedBaseDirModule = Path.Combine(AppContext.BaseDirectory, "src", "PwshTokenCompressor.psd1");
        var cwdRoot = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"ptk-probe-cwd-{Guid.NewGuid():N}")).FullName;
        var savedCwd = Directory.GetCurrentDirectory();
        var savedEnv = Environment.GetEnvironmentVariable("PTK_MODULE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_MODULE_PATH", null);
            PlantModule(AppContext.BaseDirectory); // the installed layout
            PlantModule(cwdRoot);                  // a checkout the session sits in
            Directory.SetCurrentDirectory(cwdRoot);

            var resolved = RunspaceHost.ResolveModulePath();

            Assert.Equal(plantedBaseDirModule, resolved);
        }
        finally
        {
            Directory.SetCurrentDirectory(savedCwd);
            Environment.SetEnvironmentVariable("PTK_MODULE_PATH", savedEnv);
            Directory.Delete(Path.Combine(AppContext.BaseDirectory, "src"), recursive: true);
            Directory.Delete(cwdRoot, recursive: true);
        }
    }

    [Fact]
    public void CwdProbeStillFindsCheckoutWhenNothingShipsBesideBinary()
    {
        // Goes through ResolveModulePath (not the probe helper directly) so a
        // refactor that drops the cwd start from the real composition fails
        // this test rather than passing vacuously.
        var isolatedBase = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"ptk-probe-base-{Guid.NewGuid():N}", "bin")).FullName;
        var cwdRoot = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"ptk-probe-cwd-{Guid.NewGuid():N}")).FullName;
        var savedEnv = Environment.GetEnvironmentVariable("PTK_MODULE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PTK_MODULE_PATH", null);
            var cwdModule = PlantModule(cwdRoot);

            var resolved = RunspaceHost.ResolveModulePath(baseDirStart: isolatedBase, cwdStart: cwdRoot);

            Assert.Equal(cwdModule, resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PTK_MODULE_PATH", savedEnv);
            Directory.Delete(Directory.GetParent(isolatedBase)!.FullName, recursive: true);
            Directory.Delete(cwdRoot, recursive: true);
        }
    }
}
