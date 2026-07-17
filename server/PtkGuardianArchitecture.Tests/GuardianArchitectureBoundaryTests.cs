using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PtkGuardianArchitecture.Tests;

public sealed class GuardianArchitectureBoundaryTests
{
    private const string GuardianAssemblyName = "PtkMcpGuardian";
    private const string SharedAssemblyName = "PtkSharedContracts";
    private const string ServerAssemblyName = "PtkMcpServer";
    private const string GuardianSentinel = "Ownership/PublicJobIdAllocator.cs";

    private static readonly string[] RequiredGuardianAuditCompileInputs =
    [
        "AuditAdminDispositionFailure.cs",
        "AuditAdminFailure.cs",
        "AuditAdminOperations.cs",
        "AuditAnchoredSpoolPrefixRetention.cs",
        "AuditAnchoredWriterPreparation.cs",
        "AuditBootExportSource.cs",
        "AuditCallMetadata.cs",
        "AuditClosedSpoolChainReader.cs",
        "AuditClosedSpoolExportPump.cs",
        "AuditCompletedChainRetirement.cs",
        "AuditEffectiveIdentity.cs",
        "AuditEvent.cs",
        "AuditEvidenceOrphanReconciler.cs",
        "AuditEvidenceRetentionAudit.cs",
        "AuditEvidenceSpoolScanner.cs",
        "AuditExportAcknowledgmentObserver.cs",
        "AuditExportCheckpoint.cs",
        "AuditExportCheckpointStore.cs",
        "AuditExportConfiguration.cs",
        "AuditExportCoordinator.cs",
        "AuditExportLoop.cs",
        "AuditExportRetrySchedule.cs",
        "AuditExportTransitionRecorder.cs",
        "AuditHealth.cs",
        "AuditJournal.cs",
        "AuditJournalFactory.cs",
        "AuditLiveSpoolReader.cs",
        "AuditOperatorDispositionIntent.cs",
        "AuditOperatorDispositionOutcome.cs",
        "AuditOptions.cs",
        "AuditOtlpHttpExporter.cs",
        "AuditOtlpRecordMapper.cs",
        "AuditOutputRequestProtector.cs",
        "AuditRuntimeResources.cs",
        "AuditServerLifecycle.cs",
        "AuditSpoolQuotaLease.cs",
        "AuditSpoolRecordCodec.cs",
        "AuditSpoolSegmentIdentity.cs",
        "AuditStartupConfiguration.cs",
        "ExportConfigurationIdentity.cs",
        "FileAuditJournalSink.cs",
        "ScriptEvidenceStore.cs",
        "ScriptEvidenceStoreProvider.cs",
        "SecureAuditStorage.cs",
    ];

    private static readonly string[] RequiredServerAuditCompileInputs =
    [
        "AuditCallContext.cs",
        "AuditCallFilter.cs",
        "AuditCallMetadataCapture.cs",
        "AuditRuntimeGate.cs",
    ];

    private static readonly string[] RequiredGuardianOutputCompileInputs =
    [
        "OutputProvenance.cs",
        "OutputRecoverySummary.cs",
        "OutputStore.cs",
    ];

    private static readonly string[] RequiredGuardianOutputTypeDefinitions =
    [
        "PtkMcpServer.ForegroundOutputCapture",
        "PtkMcpServer.OutputArtifactContent",
        "PtkMcpServer.OutputArtifactState",
        "PtkMcpServer.OutputArtifactStateCodes",
        "PtkMcpServer.OutputArtifactStatus",
        "PtkMcpServer.OutputCaptureReservation",
        "PtkMcpServer.OutputProvenance",
        "PtkMcpServer.OutputProvenanceMachineCodes",
        "PtkMcpServer.OutputReadResult",
        "PtkMcpServer.OutputRecoverySummary",
        "PtkMcpServer.OutputSearchMatch",
        "PtkMcpServer.OutputSearchResult",
        "PtkMcpServer.OutputSealResult",
        "PtkMcpServer.OutputStore",
        "PtkMcpServer.OutputStoreOptions",
    ];

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static readonly HashSet<string> ForbiddenBehaviorTypeNames = new(
        StringComparer.Ordinal)
    {
        "RunspaceHost",
        "SessionRuntime",
        "DefaultSessionRuntimeFactory",
        "ISessionOperations",
        "ISessionLifetime",
        "JobManager",
        "ExecutionPlanner",
        "ColdCommandResolution",
        "TrustedPreflightClassifier",
        "BashProcessRunner",
        "RtkProcessRunner",
        "BashExecutableIdentity",
        "ExecutableFileIdentity",
        "WorkerProcessEntry",
        "WorkerServer",
        "WorkerOperationScheduler",
    };

    private static readonly HashSet<string> ForbiddenSourceIdentifiers = new(
        ForbiddenBehaviorTypeNames.Concat(
        [
            "Process",
            "ProcessStartInfo",
            "Interaction",
            "AssemblyLoadContext",
            "NativeLibrary",
            "Expression",
            "GetMethod",
            "GetMethods",
            "GetMember",
            "GetMembers",
            "GetConstructor",
            "GetConstructors",
            "GetTypeInfo",
            "GetRuntimeMethod",
            "GetRuntimeMethods",
            "InvokeMember",
            "CreateDelegate",
            "DynamicInvoke",
            "GetDelegateForFunctionPointer",
            "CreateInstanceFrom",
            "CreateInstanceFromAndUnwrap",
            "CreateComInstanceFrom",
            "ExecuteAssembly",
            "ExecuteAssemblyByName",
        ]),
        StringComparer.Ordinal);

    private static readonly HashSet<string> AssemblyLoadMethods = new(
        [
            "Load", "LoadFrom", "LoadFile", "LoadModule", "UnsafeLoadFrom",
            "LoadWithPartialName", "ReflectionOnlyLoad", "ReflectionOnlyLoadFrom",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> AppDomainLoadMethods = new(
        [
            "Load", "CreateInstance", "CreateInstanceAndUnwrap",
            "CreateInstanceFrom", "CreateInstanceFromAndUnwrap",
            "ExecuteAssembly", "ExecuteAssemblyByName", "DefineDynamicAssembly",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> NativeLibraryLoadMethods = new(
        ["Load", "TryLoad", "GetExport", "TryGetExport"],
        StringComparer.Ordinal);

    // Native interop is fail-closed. R1 audit/storage extraction adds only the
    // exact module/entry-point pairs it proves necessary; a new import cannot
    // ride along merely because its name is absent from a launch blacklist.
    private static readonly HashSet<NativeImport> AllowedNativeImports =
    [
        new("advapi32.dll", "GetTokenInformation"),
        new("advapi32.dll", "OpenProcessToken"),
        new("advapi32.dll", "SetEntriesInAcl"),
        new("advapi32.dll", "SetNamedSecurityInfo"),
        new("kernel32.dll", "CloseHandle"),
        new("kernel32.dll", "CreateFileW"),
        new("kernel32.dll", "FlushFileBuffers"),
        new("kernel32.dll", "GetCurrentProcess"),
        new("kernel32.dll", "GetFileInformationByHandle"),
        new("kernel32.dll", "GetFileInformationByHandleEx"),
        new("kernel32.dll", "GetFinalPathNameByHandleW"),
        new("kernel32.dll", "LocalFree"),
        new("kernel32.dll", "MoveFileEx"),
        new("kernel32.dll", "SetFileInformationByHandle"),
        new("libc", "acl_free"),
        new("libc", "acl_get_file"),
        new("libc", "acl_init"),
        new("libc", "acl_set_file"),
        new("libc", "close"),
        new("libc", "fallocate"),
        new("libc", "fclonefileat"),
        new("libc", "fstat"),
        new("libc", "fstat$INODE64"),
        new("libc", "fstatvfs"),
        new("libc", "fsync"),
        new("libc", "geteuid"),
        new("libc", "link"),
        new("libc", "lstat"),
        new("libc", "lstat$INODE64"),
        new("libc", "open"),
        new("libc", "rename"),
        new("libc", "statx"),
    ];

    private static readonly HashSet<string> ForbiddenCatalogSourceIdentifiers = new(
        [
            "File",
            "FileInfo",
            "FileStream",
            "FileSystem",
            "FileSystemInfo",
            "FileSystemWatcher",
            "Directory",
            "DirectoryInfo",
            "DriveInfo",
            "RandomAccess",
            "StreamReader",
            "BinaryReader",
            "Path",
            "Environment",
            "ConfigurationManager",
            "IConfiguration",
            "IConfigurationRoot",
        ],
        StringComparer.Ordinal);

    private static readonly string[] RequiredSharedCompileInputs =
    [
        "ContractPrimitives.cs",
        "GuardianHost/GuardianHostProtocol.cs",
        "Public/PublicContract.cs",
        "Public/PublicRecovery.cs",
        "Public/PublicState.cs",
        "Recovery/RecoveryManifest.cs",
        "Recovery/RecoveryManifestCodec.cs",
    ];

    [Fact]
    public void Project_graph_is_exact_one_way_and_resource_closed()
    {
        var paths = RepositoryPaths.Create();
        var closure = EvaluateClosure(paths.GuardianProject);
        var projectsByName = closure.ToDictionary(
            project => project.AssemblyName,
            StringComparer.Ordinal);

        AssertExactSet(
            projectsByName.Keys,
            [GuardianAssemblyName, SharedAssemblyName],
            "guardian-safe project closure");

        var guardian = projectsByName[GuardianAssemblyName];
        var shared = projectsByName[SharedAssemblyName];
        AssertExactSet(
            guardian.ProjectReferences,
            [paths.SharedProject],
            "PtkMcpGuardian project references",
            PathComparer);
        Assert.Empty(shared.ProjectReferences);

        AssertPackages(
            guardian,
            [
                new("Google.Protobuf", "3.35.1", null, null),
                new(
                    "Grpc.Tools",
                    "2.82.0",
                    "all",
                    "runtime; build; native; contentfiles; analyzers; buildtransitive"),
                new("Microsoft.Extensions.Hosting", "10.0.9", null, null),
            ]);
        AssertPackages(shared, []);

        foreach (var project in closure)
        {
            Assert.Empty(project.ExplicitReferences);
            AssertExactSet(
                project.FrameworkReferences,
                ["Microsoft.NETCore.App"],
                $"{project.AssemblyName} framework references");
            Assert.NotEmpty(project.CompileInputs);
        }

        AssertCompileSentinel(guardian, GuardianSentinel);
        foreach (var sentinel in RequiredSharedCompileInputs)
            AssertCompileSentinel(shared, sentinel);

        AssertExactCompileDirectory(
            guardian,
            "Audit",
            RequiredGuardianAuditCompileInputs,
            "guardian audit ownership");
        AssertExactCompileDirectory(
            guardian,
            "Output",
            RequiredGuardianOutputCompileInputs,
            "guardian output ownership");
        Assert.Empty(shared.ProtobufInputs);
        AssertExactSet(
            guardian.ProtobufInputs,
            [NormalizePath(Path.Combine(guardian.ProjectDirectory, "Protos", "audit_otlp.proto"))],
            "guardian protobuf ownership",
            PathComparer);
        Assert.All(
            guardian.ProtobufInputs,
            protobuf => Assert.True(File.Exists(protobuf), $"Protobuf source is absent: {protobuf}"));
        Assert.True(File.Exists(Path.Combine(
            guardian.ProjectDirectory,
            "Protos",
            "LICENSE.OpenTelemetry-Apache-2.0.txt")));

        AssertExactResources(paths, guardian, shared);

        var server = EvaluateProject(paths.ServerProject);
        Assert.Contains(
            paths.GuardianProject,
            server.ProjectReferences,
            PathComparer);
        Assert.Contains(
            paths.SharedProject,
            server.ProjectReferences,
            PathComparer);
        Assert.DoesNotContain(paths.ServerProject, guardian.ProjectReferences, PathComparer);
        Assert.DoesNotContain(paths.ServerProject, shared.ProjectReferences, PathComparer);
        AssertExactCompileDirectory(
            server,
            "Audit",
            RequiredServerAuditCompileInputs,
            "server audit adapters");
        foreach (var outputSource in RequiredGuardianOutputCompileInputs)
        {
            Assert.DoesNotContain(
                server.CompileInputs,
                path => Path.GetFileName(path).Equals(outputSource, PathComparison));
        }
        Assert.False(File.Exists(Path.Combine(
            server.ProjectDirectory,
            "Execution",
            "OutputStore.cs")));
        Assert.Empty(server.ProtobufInputs);
        Assert.DoesNotContain(
            server.PackageReferences,
            package => package.Identity.Equals("Google.Protobuf", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            server.PackageReferences,
            package => package.Identity.Equals("Grpc.Tools", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(
            server.ProjectDirectory,
            "Protos",
            "audit_otlp.proto")));
        Assert.False(File.Exists(Path.Combine(
            server.ProjectDirectory,
            "Protos",
            "LICENSE.OpenTelemetry-Apache-2.0.txt")));

        var auditAdmin = EvaluateProject(paths.AuditAdminProject);
        AssertExactSet(
            auditAdmin.ProjectReferences,
            [paths.GuardianProject],
            "PtkAuditAdmin project references",
            PathComparer);
    }

    [Fact]
    public void Output_definitions_are_compiled_only_by_guardian()
    {
        var paths = RepositoryPaths.Create();
        var guardian = EvaluateProject(paths.GuardianProject);
        var server = EvaluateProject(paths.ServerProject);
        var guardianDefinitions = ReadCompiledTypeDefinitions(guardian);
        var serverDefinitions = ReadSourceTypeDefinitions(server);

        foreach (var typeName in RequiredGuardianOutputTypeDefinitions)
        {
            Assert.Contains(typeName, guardianDefinitions, StringComparer.Ordinal);
            Assert.DoesNotContain(typeName, serverDefinitions, StringComparer.Ordinal);
        }
    }

    [Fact]
    public void Restore_closure_has_no_PowerShell_runtime_host_or_fixture_assets()
    {
        var paths = RepositoryPaths.Create();
        var closure = EvaluateClosure(paths.GuardianProject);
        var allowedProjects = new HashSet<string>(
            [GuardianAssemblyName, SharedAssemblyName],
            StringComparer.OrdinalIgnoreCase);
        var violations = new List<string>();

        Assert.Equal(2, closure.Count);
        foreach (var project in closure)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(project.ProjectAssetsFile),
                $"{project.AssemblyName} did not report ProjectAssetsFile.");
            Assert.True(
                File.Exists(project.ProjectAssetsFile),
                $"Restore assets are absent for {project.AssemblyName}: {project.ProjectAssetsFile}");

            using var document = JsonDocument.Parse(File.ReadAllBytes(project.ProjectAssetsFile));
            var root = document.RootElement;
            var restoredProject = root
                .GetProperty("project")
                .GetProperty("restore")
                .GetProperty("projectPath")
                .GetString();
            Assert.True(
                PathComparer.Equals(project.ProjectPath, NormalizePath(restoredProject!)),
                $"Assets for {project.AssemblyName} belong to '{restoredProject}', not '{project.ProjectPath}'.");

            var libraries = root.GetProperty("libraries");
            foreach (var library in libraries.EnumerateObject())
            {
                var kind = library.Value.GetProperty("type").GetString();
                var identity = LibraryIdentity(library.Name);
                if (string.Equals(kind, "project", StringComparison.OrdinalIgnoreCase))
                {
                    if (!allowedProjects.Contains(identity))
                        violations.Add($"{project.AssemblyName}: project asset {library.Name}");
                }
                else if (IsForbiddenAsset(identity) || IsForbiddenAsset(library.Name))
                {
                    violations.Add($"{project.AssemblyName}: package asset {library.Name}");
                }
            }

            foreach (var target in root.GetProperty("targets").EnumerateObject())
            {
                foreach (var value in EnumerateJsonNamesAndStrings(target.Value))
                {
                    if (IsForbiddenAsset(value))
                        violations.Add($"{project.AssemblyName}/{target.Name}: {value}");
                }
            }
        }

        AssertNoViolations(violations);
    }

    [Fact]
    public void Compiled_guardian_assemblies_have_no_forbidden_metadata_or_launch_imports()
    {
        var paths = RepositoryPaths.Create();
        var closure = EvaluateClosure(paths.GuardianProject);
        var violations = new List<string>();
        var actualNativeImports = new HashSet<NativeImport>();
        var scannedAssemblies = 0;
        var scannedTypes = 0;

        Assert.Equal(2, closure.Count);
        foreach (var project in closure)
        {
            Assert.True(
                File.Exists(project.TargetPath),
                $"Build output is absent for {project.AssemblyName}: {project.TargetPath}");
            using var stream = File.OpenRead(project.TargetPath);
            using var pe = new PEReader(stream);
            Assert.True(pe.HasMetadata, $"{project.TargetPath} has no managed metadata.");
            var reader = pe.GetMetadataReader();
            Assert.True(reader.IsAssembly, $"{project.TargetPath} is not an assembly.");
            var actualName = reader.GetString(reader.GetAssemblyDefinition().Name);
            Assert.Equal(project.AssemblyName, actualName);
            Assert.True(reader.TypeDefinitions.Count > 1, $"{actualName} has no owned types.");
            scannedAssemblies++;

            foreach (var handle in reader.AssemblyReferences)
            {
                var name = reader.GetString(reader.GetAssemblyReference(handle).Name);
                if (IsForbiddenAssemblyReference(name))
                    violations.Add($"{actualName}: assembly reference {name}");
            }

            foreach (var handle in reader.TypeReferences)
            {
                var reference = reader.GetTypeReference(handle);
                var typeName = QualifiedTypeName(
                    reader.GetString(reference.Namespace),
                    reader.GetString(reference.Name));
                if (IsForbiddenType(typeName))
                    violations.Add($"{actualName}: type reference {typeName}");
            }

            foreach (var handle in reader.TypeDefinitions)
            {
                var definition = reader.GetTypeDefinition(handle);
                var typeName = QualifiedTypeName(
                    reader.GetString(definition.Namespace),
                    reader.GetString(definition.Name));
                scannedTypes++;
                if (IsForbiddenType(typeName))
                    violations.Add($"{actualName}: type definition {typeName}");
            }

            foreach (var handle in reader.MemberReferences)
            {
                var member = reader.GetMemberReference(handle);
                var parent = ReferencedTypeName(reader, member.Parent);
                if (parent is not null && IsForbiddenType(parent))
                {
                    violations.Add(
                        $"{actualName}: member reference {parent}.{reader.GetString(member.Name)}");
                }
                else if (parent is not null &&
                         IsForbiddenDynamicMember(parent, reader.GetString(member.Name)))
                {
                    violations.Add(
                        $"{actualName}: dynamic loader member {parent}.{reader.GetString(member.Name)}");
                }
            }

            foreach (var handle in reader.MethodDefinitions)
            {
                var method = reader.GetMethodDefinition(handle);
                if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0) continue;
                var import = method.GetImport();
                var entryPoint = reader.GetString(import.Name);
                var module = reader.GetString(reader.GetModuleReference(import.Module).Name);
                actualNativeImports.Add(new NativeImport(module, entryPoint));
                if (IsForbiddenNativeEntryPoint(entryPoint) ||
                    !AllowedNativeImports.Contains(new NativeImport(module, entryPoint)))
                {
                    violations.Add($"{actualName}: unapproved native import {module}!{entryPoint}");
                }
            }
        }

        Assert.Equal(2, scannedAssemblies);
        Assert.True(scannedTypes > 2, "No guardian-safe metadata types were scanned.");
        AssertExactSet(
            actualNativeImports,
            AllowedNativeImports,
            "compiled guardian native imports");
        AssertNoViolations(violations);
    }

    [Fact]
    public void Native_interop_allowlist_is_exact_audit_storage_closure()
    {
        AssertExactSet(
            AllowedNativeImports,
            [
                new NativeImport("advapi32.dll", "GetTokenInformation"),
                new NativeImport("advapi32.dll", "OpenProcessToken"),
                new NativeImport("advapi32.dll", "SetEntriesInAcl"),
                new NativeImport("advapi32.dll", "SetNamedSecurityInfo"),
                new NativeImport("kernel32.dll", "CloseHandle"),
                new NativeImport("kernel32.dll", "CreateFileW"),
                new NativeImport("kernel32.dll", "FlushFileBuffers"),
                new NativeImport("kernel32.dll", "GetCurrentProcess"),
                new NativeImport("kernel32.dll", "GetFileInformationByHandle"),
                new NativeImport("kernel32.dll", "GetFileInformationByHandleEx"),
                new NativeImport("kernel32.dll", "GetFinalPathNameByHandleW"),
                new NativeImport("kernel32.dll", "LocalFree"),
                new NativeImport("kernel32.dll", "MoveFileEx"),
                new NativeImport("kernel32.dll", "SetFileInformationByHandle"),
                new NativeImport("libc", "acl_free"),
                new NativeImport("libc", "acl_get_file"),
                new NativeImport("libc", "acl_init"),
                new NativeImport("libc", "acl_set_file"),
                new NativeImport("libc", "close"),
                new NativeImport("libc", "fallocate"),
                new NativeImport("libc", "fclonefileat"),
                new NativeImport("libc", "fstat"),
                new NativeImport("libc", "fstat$INODE64"),
                new NativeImport("libc", "fstatvfs"),
                new NativeImport("libc", "fsync"),
                new NativeImport("libc", "geteuid"),
                new NativeImport("libc", "link"),
                new NativeImport("libc", "lstat"),
                new NativeImport("libc", "lstat$INODE64"),
                new NativeImport("libc", "open"),
                new NativeImport("libc", "rename"),
                new NativeImport("libc", "statx"),
            ],
            "guardian native import allowlist");
    }

    [Fact]
    public void Evaluated_compile_inputs_are_owned_and_have_no_dynamic_or_process_escape()
    {
        var paths = RepositoryPaths.Create();
        var closure = EvaluateClosure(paths.GuardianProject);
        var violations = new List<string>();
        var scannedFiles = 0;

        Assert.Equal(2, closure.Count);
        foreach (var project in closure)
        {
            Assert.NotEmpty(project.CompileInputs);
            foreach (var sourcePath in project.CompileInputs)
            {
                scannedFiles++;
                Assert.True(File.Exists(sourcePath), $"Compile input is absent: {sourcePath}");
                if (!IsWithin(project.ProjectDirectory, sourcePath))
                {
                    violations.Add($"{project.AssemblyName}: external Compile input {sourcePath}");
                    continue;
                }
                if (ContainsReparsePoint(project.ProjectDirectory, sourcePath))
                {
                    violations.Add($"{project.AssemblyName}: linked Compile input {sourcePath}");
                    continue;
                }

                var source = File.ReadAllText(sourcePath);
                var tree = CSharpSyntaxTree.ParseText(
                    source,
                    new CSharpParseOptions(LanguageVersion.Preview),
                    sourcePath);
                var syntaxErrors = tree.GetDiagnostics()
                    .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                    .ToArray();
                Assert.True(
                    syntaxErrors.Length == 0,
                    $"{sourcePath} has syntax errors: {string.Join(Environment.NewLine, syntaxErrors.Select(error => error.ToString()))}");
                var root = tree.GetRoot();
                var isFrozenCatalog = sourcePath.EndsWith(
                    $"Ownership{Path.DirectorySeparatorChar}FrozenSessionCatalog.cs",
                    PathComparison);

                foreach (var token in root.DescendantTokens(descendIntoTrivia: false))
                {
                    if (token.IsKind(SyntaxKind.IdentifierToken))
                    {
                        var identifier = token.ValueText;
                        if (ForbiddenSourceIdentifiers.Contains(identifier) ||
                            IsForbiddenNativeEntryPoint(identifier))
                        {
                            violations.Add($"{sourcePath}: forbidden identifier {identifier}");
                        }
                        if (isFrozenCatalog &&
                            ForbiddenCatalogSourceIdentifiers.Contains(identifier))
                        {
                            violations.Add(
                                $"{sourcePath}: frozen catalog external-source identifier {identifier}");
                        }
                    }
                    else if (token.Value is string text &&
                             (ContainsForbiddenRuntimeName(text) ||
                              IsForbiddenNativeEntryPoint(text)))
                    {
                        violations.Add($"{sourcePath}: forbidden string value {text}");
                    }
                }

                foreach (var name in root.DescendantNodes().OfType<NameSyntax>())
                {
                    var text = name.ToString().Replace("global::", string.Empty, StringComparison.Ordinal);
                    if (ContainsForbiddenRuntimeName(text))
                        violations.Add($"{sourcePath}: forbidden name {text}");
                }

                var aliases = root.DescendantNodes()
                    .OfType<UsingDirectiveSyntax>()
                    .Where(usingDirective => usingDirective.Alias is not null && usingDirective.Name is not null)
                    .ToDictionary(
                        usingDirective => usingDirective.Alias!.Name.Identifier.ValueText,
                        usingDirective => usingDirective.Name!.ToString(),
                        StringComparer.Ordinal);
                foreach (var member in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                {
                    if (DynamicLoad(member, aliases) is { } dynamicLoad)
                        violations.Add($"{sourcePath}: dynamic loader {dynamicLoad}");
                }
            }
        }

        Assert.True(
            scannedFiles >= RequiredSharedCompileInputs.Length + 1,
            $"Only {scannedFiles} guardian-safe Compile inputs were scanned.");
        AssertNoViolations(violations);
    }

    [Fact]
    public void Native_launch_classifier_covers_supported_Windows_and_Unix_entry_points()
    {
        string[] entryPoints =
        [
            "CreateProcessW",
            "CreateProcessAsUserW",
            "CreateProcessWithLogonW",
            "CreateProcessWithTokenW",
            "CreateProcessInternalW",
            "ShellExecuteW",
            "ShellExecuteExW",
            "WinExec",
            "NtCreateUserProcess",
            "RtlCreateUserProcess",
            "NtCreateProcess",
            "NtCreateProcessEx",
            "ZwCreateProcess",
            "ZwCreateProcessEx",
            "fork",
            "fork1",
            "forkpty",
            "vfork",
            "_Fork",
            "rfork",
            "daemon",
            "clone",
            "clone3",
            "__clone",
            "__clone2",
            "execv",
            "execve",
            "execvp",
            "execvpe",
            "execl",
            "execlp",
            "execle",
            "execveat",
            "fexecve",
            "execvP",
            "_execl",
            "_execle",
            "_execlp",
            "_execlpe",
            "_execv",
            "_execve",
            "_execvp",
            "_execvpe",
            "_wexecl",
            "_wexecle",
            "_wexeclp",
            "_wexeclpe",
            "_wexecv",
            "_wexecve",
            "_wexecvp",
            "_wexecvpe",
            "posix_spawn",
            "posix_spawnp",
            "spawnl",
            "spawnle",
            "spawnlp",
            "spawnlpe",
            "spawnv",
            "spawnve",
            "spawnvp",
            "spawnvpe",
            "_spawnl",
            "_spawnle",
            "_spawnlp",
            "_spawnlpe",
            "_spawnv",
            "_spawnve",
            "_spawnvp",
            "_spawnvpe",
            "_wspawnl",
            "_wspawnle",
            "_wspawnlp",
            "_wspawnlpe",
            "_wspawnv",
            "_wspawnve",
            "_wspawnvp",
            "_wspawnvpe",
            "system",
            "_wsystem",
            "popen",
            "popen64",
            "_popen",
            "_wpopen",
            "wordexp",
            "syscall",
        ];

        Assert.All(entryPoints, entryPoint =>
            Assert.True(IsNativeLaunchName(entryPoint), entryPoint));
        Assert.False(IsNativeLaunchName("GetCurrentProcessId"));
        Assert.False(IsNativeLaunchName("waitpid"));
    }

    [Theory]
    [InlineData("LoadLibraryW")]
    [InlineData("LoadLibraryExA")]
    [InlineData("LoadPackagedLibrary")]
    [InlineData("GetProcAddress")]
    [InlineData("LdrLoadDll")]
    [InlineData("LdrGetProcedureAddress")]
    [InlineData("dlopen")]
    [InlineData("dlmopen")]
    [InlineData("android_dlopen_ext")]
    [InlineData("dlsym")]
    [InlineData("dlvsym")]
    [InlineData("NSAddImage")]
    [InlineData("NSLookupSymbolInImage")]
    public void Native_loader_and_resolver_classifier_closes_indirect_launch_paths(
        string entryPoint)
    {
        Assert.True(IsForbiddenNativeEntryPoint(entryPoint), entryPoint);
        Assert.False(IsNativeLaunchName(entryPoint), entryPoint);
    }

    [Theory]
    [InlineData("advapi32.dll", "CreateServiceW")]
    [InlineData("advapi32.dll", "StartServiceW")]
    [InlineData("kernel32.dll", "GetProcAddress")]
    [InlineData("libdl", "dlsym")]
    public void Native_import_allowlist_rejects_unapproved_launch_and_resolution_surfaces(
        string module,
        string entryPoint)
    {
        Assert.DoesNotContain(new NativeImport(module, entryPoint), AllowedNativeImports);
        Assert.DoesNotContain(
            AllowedNativeImports,
            import => IsForbiddenNativeEntryPoint(import.EntryPoint));
    }

    [Theory]
    [InlineData("System.Reflection.Assembly", "Load", true)]
    [InlineData("System.Reflection.Assembly", "LoadFrom", true)]
    [InlineData("System.Reflection.Assembly", "LoadWithPartialName", true)]
    [InlineData("System.Reflection.Assembly", "ReflectionOnlyLoadFrom", true)]
    [InlineData("System.Reflection.Assembly", "GetManifestResourceStream", false)]
    [InlineData("System.AppDomain", "Load", true)]
    [InlineData("System.AppDomain", "CreateInstanceAndUnwrap", true)]
    [InlineData("System.AppDomain", "CreateInstanceFromAndUnwrap", true)]
    [InlineData("System.AppDomain", "ExecuteAssembly", true)]
    [InlineData("System.Runtime.Loader.AssemblyLoadContext", "LoadFromAssemblyPath", true)]
    [InlineData("System.Runtime.InteropServices.NativeLibrary", "TryGetExport", true)]
    [InlineData("System.Type", "GetTypeFromProgID", true)]
    [InlineData("System.Type", "GetMethod", true)]
    [InlineData("System.Type", "InvokeMember", true)]
    [InlineData("System.Reflection.MethodBase", "Invoke", true)]
    [InlineData("System.Reflection.MethodInfo", "CreateDelegate", true)]
    [InlineData("System.Delegate", "CreateDelegate", true)]
    [InlineData("System.Delegate", "DynamicInvoke", true)]
    [InlineData("System.Runtime.InteropServices.Marshal", "GetDelegateForFunctionPointer", true)]
    [InlineData("System.Activator", "CreateInstance", true)]
    [InlineData("System.Activator", "CreateInstanceFrom", true)]
    [InlineData("System.Activator", "CreateComInstanceFrom", true)]
    public void Compiled_member_classifier_blocks_dynamic_load_but_allows_resource_reads(
        string qualifiedTypeName,
        string memberName,
        bool expected)
    {
        Assert.Equal(expected, IsForbiddenDynamicMember(qualifiedTypeName, memberName));
    }

    [Theory]
    [InlineData("System.Reflection.Emit.DynamicMethod", true)]
    [InlineData("System.Linq.Expressions.Expression", true)]
    [InlineData("Microsoft.VisualBasic.Interaction", true)]
    [InlineData("System.Reflection.Assembly", false)]
    [InlineData("System.IO.File", false)]
    public void Compiled_type_classifier_blocks_dynamic_code_generation_namespaces(
        string qualifiedTypeName,
        bool expected)
    {
        Assert.Equal(expected, IsForbiddenType(qualifiedTypeName));
    }

    [Theory]
    [InlineData("Microsoft.VisualBasic.Core", true)]
    [InlineData("Microsoft.VisualBasic", true)]
    [InlineData("System.Runtime", false)]
    public void Compiled_assembly_classifier_blocks_framework_process_launch_facades(
        string assemblyName,
        bool expected)
    {
        Assert.Equal(expected, IsForbiddenAssemblyReference(assemblyName));
    }

    [Theory]
    [InlineData("Microsoft.VisualBasic.Interaction", true)]
    [InlineData("System.Management.Automation.PowerShell", true)]
    [InlineData("System.Reflection.Assembly", false)]
    public void Source_runtime_name_classifier_blocks_forbidden_framework_facades(
        string sourceName,
        bool expected)
    {
        Assert.Equal(expected, ContainsForbiddenRuntimeName(sourceName));
    }

    [Theory]
    [InlineData(
        "using System.Reflection; sealed class C { Assembly M(byte[] bytes) => Assembly.Load(bytes); }")]
    [InlineData(
        "using System; using System.Reflection; sealed class C { Assembly M(byte[] bytes) { Func<byte[], Assembly> load = Assembly.Load; return load(bytes); } }")]
    [InlineData(
        "using System; sealed class C { object M(byte[] bytes) => AppDomain.CurrentDomain.Load(bytes); }")]
    [InlineData(
        "using Domain = System.AppDomain; sealed class C { object M(byte[] bytes) => Domain.CurrentDomain.Load(bytes); }")]
    [InlineData(
        "using System; sealed class C { object M(string path) => Activator.CreateInstanceFrom(path, \"T\"); }")]
    [InlineData(
        "using System; sealed class C { object M(string path) => AppDomain.CurrentDomain.CreateInstanceFromAndUnwrap(path, \"T\"); }")]
    public void Source_classifier_blocks_direct_delegate_and_AppDomain_load_forms(string source)
    {
        Assert.NotEmpty(DynamicLoadsInSource(source));
    }

    [Fact]
    public void Source_classifier_allows_embedded_contract_resource_reads()
    {
        const string source =
            "using System.IO; sealed class C { Stream? M() => typeof(C).Assembly.GetManifestResourceStream(\"contract\"); }";

        Assert.Empty(DynamicLoadsInSource(source));
    }

    [Fact]
    public void Frozen_catalog_external_source_classifier_covers_reader_surfaces()
    {
        string[] forbidden =
        [
            "File",
            "Directory",
            "FileStream",
            "RandomAccess",
            "StreamReader",
            "Path",
            "Environment",
            "ConfigurationManager",
            "IConfiguration",
        ];

        Assert.All(forbidden, identifier =>
            Assert.Contains(identifier, ForbiddenCatalogSourceIdentifiers));
        Assert.DoesNotContain("IncrementalHash", ForbiddenCatalogSourceIdentifiers);
        Assert.DoesNotContain("Encoding", ForbiddenCatalogSourceIdentifiers);
    }

    private static IReadOnlyList<EvaluatedProject> EvaluateClosure(string rootProject)
    {
        var pending = new Queue<string>();
        var visited = new Dictionary<string, EvaluatedProject>(PathComparer);
        pending.Enqueue(NormalizePath(rootProject));
        while (pending.TryDequeue(out var projectPath))
        {
            if (visited.ContainsKey(projectPath)) continue;
            var project = EvaluateProject(projectPath);
            visited.Add(projectPath, project);
            foreach (var reference in project.ProjectReferences)
                pending.Enqueue(reference);
        }
        return visited.Values.ToArray();
    }

    private static EvaluatedProject EvaluateProject(string projectPath)
    {
        projectPath = NormalizePath(projectPath);
        Assert.True(File.Exists(projectPath), $"Required project is absent: {projectPath}");
        var result = RunDotnet(
            "msbuild",
            projectPath,
            "-nologo",
            "-verbosity:quiet",
            $"-property:Configuration={BuildConfiguration}",
            "-getProperty:AssemblyName;TargetPath;ProjectAssetsFile",
            "-getItem:ProjectReference;PackageReference;Reference;FrameworkReference;Compile;EmbeddedResource;Protobuf");
        Assert.True(
            result.ExitCode == 0,
            $"MSBuild evaluation failed for {projectPath}:{Environment.NewLine}{result.StandardError}{Environment.NewLine}{result.StandardOutput}");

        using var document = JsonDocument.Parse(ExtractJson(result.StandardOutput));
        var root = document.RootElement;
        var properties = root.GetProperty("Properties");
        var items = root.GetProperty("Items");
        var assemblyName = properties.GetProperty("AssemblyName").GetString();
        Assert.False(string.IsNullOrWhiteSpace(assemblyName));

        return new EvaluatedProject(
            projectPath,
            assemblyName!,
            NormalizePath(properties.GetProperty("TargetPath").GetString()!),
            NormalizePath(properties.GetProperty("ProjectAssetsFile").GetString()!),
            ReadItems(items, "ProjectReference", item => NormalizePath(RequiredItem(item, "FullPath"))),
            ReadItems(items, "PackageReference", item => new PackageReference(
                RequiredItem(item, "Identity"),
                OptionalItem(item, "Version"),
                OptionalItem(item, "PrivateAssets"),
                OptionalItem(item, "IncludeAssets"))),
            ReadItems(items, "Reference", item => RequiredItem(item, "Identity")),
            ReadItems(items, "FrameworkReference", item => RequiredItem(item, "Identity")),
            ReadItems(items, "Compile", item => NormalizePath(RequiredItem(item, "FullPath"))),
            ReadItems(items, "EmbeddedResource", item => new EmbeddedResource(
                NormalizePath(RequiredItem(item, "FullPath")),
                OptionalItem(item, "LogicalName"))),
            ReadItems(items, "Protobuf", item => NormalizePath(RequiredItem(item, "FullPath"))));
    }

    private static void AssertPackages(
        EvaluatedProject project,
        IReadOnlyCollection<PackageExpectation> expected)
    {
        var expectedByName = expected.ToDictionary(package => package.Identity, StringComparer.OrdinalIgnoreCase);
        var actualByName = project.PackageReferences.ToDictionary(package => package.Identity, StringComparer.OrdinalIgnoreCase);
        AssertExactSet(
            actualByName.Keys,
            expectedByName.Keys,
            $"{project.AssemblyName} package references",
            StringComparer.OrdinalIgnoreCase);
        foreach (var (identity, expectedPackage) in expectedByName)
        {
            var actual = actualByName[identity];
            Assert.Equal(expectedPackage.Version, actual.Version);
            Assert.Equal(expectedPackage.PrivateAssets, EmptyToNull(actual.PrivateAssets));
            Assert.Equal(expectedPackage.IncludeAssets, EmptyToNull(actual.IncludeAssets));
        }
    }

    private static void AssertCompileSentinel(EvaluatedProject project, string relativePath)
    {
        var expected = NormalizePath(Path.Combine(
            project.ProjectDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Contains(expected, project.CompileInputs, PathComparer);
    }

    private static void AssertExactCompileDirectory(
        EvaluatedProject project,
        string relativeDirectory,
        IReadOnlyCollection<string> expected,
        string label)
    {
        var directory = NormalizePath(Path.Combine(project.ProjectDirectory, relativeDirectory));
        var actual = project.CompileInputs
            .Where(path => IsWithin(directory, path))
            .Select(path => Path.GetRelativePath(directory, path)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .ToArray();
        AssertExactSet(actual, expected, label, StringComparer.Ordinal);
    }

    private static void AssertExactResources(
        RepositoryPaths paths,
        EvaluatedProject guardian,
        EvaluatedProject shared)
    {
        Assert.Empty(guardian.EmbeddedResources);
        var contractRoot = Path.Combine(paths.ServerDirectory, "Contracts", "ResilienceR0");
        var expected = new Dictionary<string, string>(PathComparer)
        {
            [NormalizePath(Path.Combine(contractRoot, "public-tool-contract.json"))] =
                "PtkSharedContracts.Contracts.public-tool-contract.json",
            [NormalizePath(Path.Combine(contractRoot, "public-recovery.schema.json"))] =
                "PtkSharedContracts.Contracts.public-recovery.schema.json",
            [NormalizePath(Path.Combine(contractRoot, "public-state.schema.json"))] =
                "PtkSharedContracts.Contracts.public-state.schema.json",
            [NormalizePath(Path.Combine(contractRoot, "guardian-host-protocol.json"))] =
                "PtkSharedContracts.Contracts.guardian-host-protocol.json",
            [NormalizePath(Path.Combine(contractRoot, "guardian-host-protocol.schema.json"))] =
                "PtkSharedContracts.Contracts.guardian-host-protocol.schema.json",
            [NormalizePath(Path.Combine(contractRoot, "recovery-manifest.schema.json"))] =
                "PtkSharedContracts.Contracts.recovery-manifest.schema.json",
            [NormalizePath(Path.Combine(contractRoot, "recovery-manifest.example.json"))] =
                "PtkSharedContracts.Contracts.recovery-manifest.example.json",
        };
        Assert.Equal(expected.Count, shared.EmbeddedResources.Count);
        foreach (var resource in shared.EmbeddedResources)
        {
            Assert.True(
                expected.TryGetValue(resource.FullPath, out var logicalName),
                $"Unexpected shared-contract resource: {resource.FullPath}");
            Assert.Equal(logicalName, resource.LogicalName);
            Assert.True(File.Exists(resource.FullPath), $"Contract resource is absent: {resource.FullPath}");
        }
    }

    private static string? ReferencedTypeName(MetadataReader reader, EntityHandle handle) =>
        handle.Kind switch
        {
            HandleKind.TypeReference => TypeReferenceName(reader, (TypeReferenceHandle)handle),
            HandleKind.TypeDefinition => TypeDefinitionName(reader, (TypeDefinitionHandle)handle),
            _ => null,
        };

    private static string TypeReferenceName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        return QualifiedTypeName(reader.GetString(type.Namespace), reader.GetString(type.Name));
    }

    private static string TypeDefinitionName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        return QualifiedTypeName(reader.GetString(type.Namespace), reader.GetString(type.Name));
    }

    private static IReadOnlyCollection<string> ReadCompiledTypeDefinitions(EvaluatedProject project)
    {
        Assert.True(
            File.Exists(project.TargetPath),
            $"Build output is absent for {project.AssemblyName}: {project.TargetPath}");
        using var stream = File.OpenRead(project.TargetPath);
        using var pe = new PEReader(stream);
        Assert.True(pe.HasMetadata, $"{project.TargetPath} has no managed metadata.");
        var reader = pe.GetMetadataReader();
        return reader.TypeDefinitions
            .Select(handle => TypeDefinitionName(reader, handle))
            .ToArray();
    }

    private static IReadOnlyCollection<string> ReadSourceTypeDefinitions(EvaluatedProject project)
    {
        var definitions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourcePath in project.CompileInputs)
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(sourcePath));
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (declaration.Ancestors().OfType<BaseTypeDeclarationSyntax>().Any()) continue;
                var declaredNamespace = declaration.Ancestors()
                    .OfType<BaseNamespaceDeclarationSyntax>()
                    .FirstOrDefault()
                    ?.Name
                    .ToString() ?? string.Empty;
                definitions.Add(QualifiedTypeName(
                    declaredNamespace,
                    declaration.Identifier.ValueText));
            }
        }
        return definitions;
    }

    private static string QualifiedTypeName(string @namespace, string name) =>
        string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";

    private static bool IsForbiddenType(string qualifiedName)
    {
        var separator = qualifiedName.LastIndexOf('.');
        var @namespace = separator < 0 ? string.Empty : qualifiedName[..separator];
        var name = separator < 0 ? qualifiedName : qualifiedName[(separator + 1)..];
        return @namespace.StartsWith("System.Management.Automation", StringComparison.Ordinal) ||
               @namespace.StartsWith("Microsoft.VisualBasic", StringComparison.Ordinal) ||
               @namespace.StartsWith("System.Reflection.Emit", StringComparison.Ordinal) ||
               @namespace.StartsWith("System.Linq.Expressions", StringComparison.Ordinal) ||
               (@namespace == "System.Diagnostics" &&
                name is "Process" or "ProcessStartInfo") ||
               ForbiddenBehaviorTypeNames.Contains(name);
    }

    private static bool IsForbiddenAssemblyReference(string name) =>
        name.Equals(ServerAssemblyName, StringComparison.OrdinalIgnoreCase) ||
        name.Equals("System.Management.Automation", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("System.Diagnostics.Process", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Microsoft.VisualBasic", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Microsoft.PowerShell", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Microsoft.Management.Infrastructure", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Microsoft.WSMan", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("PtkResilienceTestFixture", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("PtkContainmentTestFixture", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("PtkRtkTestFixture", StringComparison.OrdinalIgnoreCase);

    private static bool IsForbiddenDynamicMember(string qualifiedTypeName, string memberName) =>
        qualifiedTypeName switch
        {
            "System.Reflection.Assembly" => AssemblyLoadMethods.Contains(memberName),
            "System.AppDomain" => AppDomainLoadMethods.Contains(memberName),
            "System.Runtime.Loader.AssemblyLoadContext" =>
                memberName.StartsWith("Load", StringComparison.Ordinal),
            "System.Runtime.InteropServices.NativeLibrary" =>
                NativeLibraryLoadMethods.Contains(memberName),
            "System.Type" => memberName is
                "GetType" or "GetTypeFromCLSID" or "GetTypeFromProgID" or
                "GetMethod" or "GetMethods" or "GetMember" or "GetMembers" or
                "GetConstructor" or "GetConstructors" or "GetTypeInfo" or
                "InvokeMember",
            "System.Reflection.MethodBase" => memberName is "Invoke" or "GetMethodFromHandle",
            "System.Reflection.MethodInfo" => memberName is "Invoke" or "CreateDelegate",
            "System.Delegate" => memberName is "CreateDelegate" or "DynamicInvoke",
            "System.Runtime.InteropServices.Marshal" =>
                memberName.StartsWith("GetDelegateForFunctionPointer", StringComparison.Ordinal),
            "System.Activator" => IsActivatorLoadMethod(memberName),
            _ => false,
        };

    private static bool IsForbiddenAsset(string value)
    {
        var normalized = value.Replace('\\', '/');
        var leaf = normalized[(normalized.LastIndexOf('/') + 1)..];
        return normalized.Contains("Microsoft.PowerShell", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("System.Management.Automation", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("Microsoft.Management.Infrastructure", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("Microsoft.WSMan", StringComparison.OrdinalIgnoreCase) ||
               leaf.Equals("PtkMcpServer.dll", StringComparison.OrdinalIgnoreCase) ||
               leaf.Equals("PtkMcpServer", StringComparison.OrdinalIgnoreCase) ||
               leaf.StartsWith("PtkResilienceTestFixture", StringComparison.OrdinalIgnoreCase) ||
               leaf.StartsWith("PtkContainmentTestFixture", StringComparison.OrdinalIgnoreCase) ||
               leaf.StartsWith("PtkRtkTestFixture", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsForbiddenRuntimeName(string value) =>
        value.Contains("System.Management.Automation", StringComparison.Ordinal) ||
        value.Contains("Microsoft.VisualBasic", StringComparison.Ordinal) ||
        value.Contains("Microsoft.PowerShell.SDK", StringComparison.Ordinal);

    private static bool IsNativeLaunchName(string name)
    {
        if (name.StartsWith("CreateProcess", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("ShellExecute", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("posix_spawn", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("NtCreateProcess", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("ZwCreateProcess", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("RtlCreateUserProcess", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return name is
            "WinExec" or "NtCreateUserProcess" or
            "fork" or "fork1" or "forkpty" or "vfork" or "_Fork" or "rfork" or "daemon" or
            "clone" or "clone3" or "__clone" or "__clone2" or
            "execv" or "execve" or "execvp" or "execvpe" or
            "execl" or "execlp" or "execle" or "execveat" or "fexecve" or "execvP" or
            "_execl" or "_execle" or "_execlp" or "_execlpe" or
            "_execv" or "_execve" or "_execvp" or "_execvpe" or
            "_wexecl" or "_wexecle" or "_wexeclp" or "_wexeclpe" or
            "_wexecv" or "_wexecve" or "_wexecvp" or "_wexecvpe" or
            "spawnl" or "spawnle" or "spawnlp" or "spawnlpe" or
            "spawnv" or "spawnve" or "spawnvp" or "spawnvpe" or
            "_spawnl" or "_spawnle" or "_spawnlp" or "_spawnlpe" or
            "_spawnv" or "_spawnve" or "_spawnvp" or "_spawnvpe" or
            "_wspawnl" or "_wspawnle" or "_wspawnlp" or "_wspawnlpe" or
            "_wspawnv" or "_wspawnve" or "_wspawnvp" or "_wspawnvpe" or
            "system" or "_wsystem" or
            "popen" or "popen64" or "_popen" or "_wpopen" or
            "wordexp" or "syscall";
    }

    private static bool IsForbiddenNativeEntryPoint(string name) =>
        IsNativeLaunchName(name) || IsNativeLoaderOrResolverName(name);

    private static bool IsNativeLoaderOrResolverName(string name)
    {
        if (name.StartsWith("LoadLibrary", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("GetProcAddress", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("LdrGetProcedureAddress", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name is
            "LoadPackagedLibrary" or "LdrLoadDll" or
            "dlopen" or "dlmopen" or "android_dlopen_ext" or "dlsym" or "dlvsym" or
            "NSAddImage" or "NSLookupSymbolInImage" or "NSLookupAndBindSymbol" or
            "NSAddressOfSymbol";
    }

    private static IReadOnlyList<string> DynamicLoadsInSource(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview));
        var syntaxErrors = tree.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(
            syntaxErrors.Length == 0,
            string.Join(Environment.NewLine, syntaxErrors.Select(error => error.ToString())));
        var root = tree.GetRoot();
        var aliases = root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Where(usingDirective => usingDirective.Alias is not null && usingDirective.Name is not null)
            .ToDictionary(
                usingDirective => usingDirective.Alias!.Name.Identifier.ValueText,
                usingDirective => usingDirective.Name!.ToString(),
                StringComparer.Ordinal);
        return root.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Select(member => DynamicLoad(member, aliases))
            .Where(value => value is not null)
            .Select(value => value!)
            .ToArray();
    }

    private static string? DynamicLoad(
        MemberAccessExpressionSyntax member,
        IReadOnlyDictionary<string, string> aliases)
    {
        var method = member.Name.Identifier.ValueText;
        var receiver = member.Expression.ToString().Replace("global::", string.Empty, StringComparison.Ordinal);
        receiver = ExpandAliasPrefix(receiver, aliases);
        var receiverLeaf = receiver[(receiver.LastIndexOf('.') + 1)..];

        if ((receiverLeaf == "Assembly" || receiver.EndsWith(".Assembly", StringComparison.Ordinal)) &&
            AssemblyLoadMethods.Contains(method))
        {
            return $"{receiver}.{method}";
        }
        if ((receiver == "AppDomain" ||
             receiver.StartsWith("AppDomain.", StringComparison.Ordinal) ||
             receiver.StartsWith("System.AppDomain.", StringComparison.Ordinal)) &&
            AppDomainLoadMethods.Contains(method))
        {
            return $"{receiver}.{method}";
        }
        if (receiver.Contains("AssemblyLoadContext", StringComparison.Ordinal) &&
            method.StartsWith("Load", StringComparison.Ordinal))
        {
            return $"{receiver}.{method}";
        }
        if (receiver.Contains("NativeLibrary", StringComparison.Ordinal) &&
            NativeLibraryLoadMethods.Contains(method))
        {
            return $"{receiver}.{method}";
        }
        if ((receiverLeaf == "Type" &&
             method is "GetType" or "GetTypeFromCLSID" or "GetTypeFromProgID") ||
            (receiverLeaf == "Activator" && IsActivatorLoadMethod(method)))
        {
            return $"{receiver}.{method}";
        }
        return null;
    }

    private static bool IsActivatorLoadMethod(string memberName) =>
        memberName == "CreateInstance" ||
        memberName.StartsWith("CreateInstanceFrom", StringComparison.Ordinal) ||
        memberName.StartsWith("CreateComInstanceFrom", StringComparison.Ordinal);

    private static string ExpandAliasPrefix(
        string receiver,
        IReadOnlyDictionary<string, string> aliases)
    {
        var separator = receiver.IndexOf('.');
        var prefix = separator < 0 ? receiver : receiver[..separator];
        if (!aliases.TryGetValue(prefix, out var expanded)) return receiver;
        return separator < 0 ? expanded : expanded + receiver[separator..];
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static bool ContainsReparsePoint(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
        }
        return false;
    }

    private static IEnumerable<string> EnumerateJsonNamesAndStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var value in EnumerateJsonNamesAndStrings(property.Value))
                        yield return value;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                foreach (var value in EnumerateJsonNamesAndStrings(item))
                    yield return value;
                break;
            case JsonValueKind.String:
                yield return element.GetString()!;
                break;
        }
    }

    private static string LibraryIdentity(string libraryKey)
    {
        var separator = libraryKey.IndexOf('/');
        return separator < 0 ? libraryKey : libraryKey[..separator];
    }

    private static void AssertNoViolations(IEnumerable<string> violations)
    {
        var distinct = violations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(
            distinct.Length == 0,
            $"Guardian boundary violations:{Environment.NewLine}{string.Join(Environment.NewLine, distinct)}");
    }

    private static void AssertExactSet<T>(
        IEnumerable<T> actual,
        IEnumerable<T> expected,
        string label,
        IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;
        var actualSet = new HashSet<T>(actual, comparer);
        var expectedSet = new HashSet<T>(expected, comparer);
        Assert.True(
            actualSet.SetEquals(expectedSet),
            $"Unexpected {label}.{Environment.NewLine}" +
            $"Expected: {string.Join(", ", expectedSet)}{Environment.NewLine}" +
            $"Actual: {string.Join(", ", actualSet)}");
    }

    private static IReadOnlyList<T> ReadItems<T>(
        JsonElement items,
        string name,
        Func<JsonElement, T> select)
    {
        if (!items.TryGetProperty(name, out var values)) return [];
        return values.EnumerateArray().Select(select).ToArray();
    }

    private static string RequiredItem(JsonElement item, string name)
    {
        Assert.True(item.TryGetProperty(name, out var value), $"MSBuild item lacks {name}.");
        var text = value.GetString();
        Assert.False(string.IsNullOrWhiteSpace(text), $"MSBuild item {name} is empty.");
        return text!;
    }

    private static string? OptionalItem(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) ? EmptyToNull(value.GetString()) : null;

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static string ExtractJson(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        Assert.True(start >= 0 && end >= start, $"MSBuild returned no JSON:{Environment.NewLine}{output}");
        return output[start..(end + 1)];
    }

    private static ProcessResult RunDotnet(params string[] arguments)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var start = new ProcessStartInfo(string.IsNullOrWhiteSpace(host) ? "dotnet" : host)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Could not start the dotnet host.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"dotnet {string.Join(' ', arguments)} timed out.");
        }
        return new ProcessResult(
            process.ExitCode,
            stdout.GetAwaiter().GetResult(),
            stderr.GetAwaiter().GetResult());
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string BuildConfiguration =>
        typeof(GuardianArchitectureBoundaryTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

    private sealed record PackageExpectation(
        string Identity,
        string Version,
        string? PrivateAssets,
        string? IncludeAssets);

    private readonly record struct NativeImport(string Module, string EntryPoint);

    private sealed record PackageReference(
        string Identity,
        string? Version,
        string? PrivateAssets,
        string? IncludeAssets);

    private sealed record EmbeddedResource(string FullPath, string? LogicalName);

    private sealed record EvaluatedProject(
        string ProjectPath,
        string AssemblyName,
        string TargetPath,
        string ProjectAssetsFile,
        IReadOnlyList<string> ProjectReferences,
        IReadOnlyList<PackageReference> PackageReferences,
        IReadOnlyList<string> ExplicitReferences,
        IReadOnlyList<string> FrameworkReferences,
        IReadOnlyList<string> CompileInputs,
        IReadOnlyList<EmbeddedResource> EmbeddedResources,
        IReadOnlyList<string> ProtobufInputs)
    {
        internal string ProjectDirectory => Path.GetDirectoryName(ProjectPath)!;
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record RepositoryPaths(
        string ServerDirectory,
        string GuardianProject,
        string SharedProject,
        string ServerProject,
        string AuditAdminProject)
    {
        internal static RepositoryPaths Create()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null &&
                   !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            {
                directory = directory.Parent;
            }
            Assert.NotNull(directory);
            var server = NormalizePath(Path.Combine(directory!.FullName, "server"));
            return new RepositoryPaths(
                server,
                NormalizePath(Path.Combine(server, "PtkMcpGuardian", "PtkMcpGuardian.csproj")),
                NormalizePath(Path.Combine(server, "PtkSharedContracts", "PtkSharedContracts.csproj")),
                NormalizePath(Path.Combine(server, "PtkMcpServer", "PtkMcpServer.csproj")),
                NormalizePath(Path.Combine(server, "PtkAuditAdmin", "PtkAuditAdmin.csproj")));
        }
    }
}
