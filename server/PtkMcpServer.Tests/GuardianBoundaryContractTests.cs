using System.Reflection;
using PtkMcpGuardian.Ownership;
using PtkMcpServer.Audit;
using PtkMcpServer.Sessions;
using PtkMcpServer.Tools;

namespace PtkMcpServer.Tests;

public sealed class GuardianBoundaryContractTests
{
    [Fact]
    public void Guardian_interfaces_have_the_exact_internal_member_shape()
    {
        AssertInternalInterface(
            typeof(IAuditRuntimeResources),
            [typeof(IDisposable)],
            [("Journal", typeof(AuditJournal))],
            [
                ("StartExporter", typeof(void), Type.EmptyTypes),
                ("StopExporterAsync", typeof(Task), Type.EmptyTypes),
            ]);
        AssertInternalInterface(
            typeof(IAuditBoundaryCall),
            [],
            [
                ("AuthorizationPersistenceFailed", typeof(bool)),
                ("TerminalWritten", typeof(bool)),
                ("UserExecutionStarted", typeof(bool)),
            ],
            [
                (
                    "CompleteFromFilter",
                    typeof(void),
                    [typeof(string), typeof(long)]),
            ]);
        AssertInternalInterface(
            typeof(IAuditAdmissionOwner),
            [],
            [("Health", typeof(AuditHealth))],
            [
                ("Touch", typeof(void), Type.EmptyTypes),
                (
                    "TryBeginCall",
                    typeof(bool),
                    [
                        typeof(AuditCallMetadata),
                        typeof(string),
                        typeof(IAuditBoundaryCall).MakeByRefType(),
                        typeof(IDisposable).MakeByRefType(),
                        typeof(string).MakeByRefType(),
                    ]),
            ]);
        AssertInternalInterface(
            typeof(IOrderedOwnedLifetime),
            [typeof(IDisposable)],
            [],
            [("ShutdownAsync", typeof(Task), Type.EmptyTypes)]);
        AssertInternalInterface(
            typeof(IOutputArtifactReader),
            [],
            [],
            [
                (
                    "Read",
                    typeof(OutputReadResult),
                    [typeof(string), typeof(long), typeof(int)]),
                (
                    "Search",
                    typeof(OutputSearchResult),
                    [typeof(string), typeof(string), typeof(long), typeof(int)]),
                (
                    "Status",
                    typeof(OutputArtifactStatus),
                    [typeof(string)]),
            ]);
        Assert.Equal(
            ["handle", "offset", "maxBytes"],
            typeof(IOutputArtifactReader).GetMethod("Read")!
                .GetParameters().Select(parameter => parameter.Name));
        Assert.Equal(
            ["handle", "pattern", "offset", "maxBytes"],
            typeof(IOutputArtifactReader).GetMethod("Search")!
                .GetParameters().Select(parameter => parameter.Name));
        Assert.Equal(
            ["handle"],
            typeof(IOutputArtifactReader).GetMethod("Status")!
                .GetParameters().Select(parameter => parameter.Name));
        AssertOutputCaptureOwnerShape();

        var admissionParameters = typeof(IAuditAdmissionOwner)
            .GetMethod(nameof(IAuditAdmissionOwner.TryBeginCall))!
            .GetParameters();
        Assert.False(admissionParameters[0].IsOut);
        Assert.False(admissionParameters[1].IsOut);
        Assert.All(admissionParameters[2..], parameter => Assert.True(parameter.IsOut));
    }

    [Fact]
    public void Server_adapters_implement_only_the_guardian_safe_ownership_shapes()
    {
        AssertExplicitImplementation<AuditRuntimeResources, IAuditRuntimeResources>();
        AssertExplicitImplementation<AuditCallContext, IAuditBoundaryCall>();
        AssertExplicitImplementation<AuditRuntimeGate, IAuditAdmissionOwner>();
        AssertExplicitImplementation<OutputStore, IOutputArtifactReader>();
        AssertExplicitImplementation<OutputStore, IOutputCaptureOwner>();
        Assert.Contains(
            typeof(IOrderedOwnedLifetime),
            typeof(ISessionLifetime).GetInterfaces());
        Assert.Contains(
            typeof(IOrderedOwnedLifetime),
            typeof(SessionRuntime).GetInterfaces());

        var fields = typeof(AuditRuntimeGate).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Single(fields, field => field.FieldType == typeof(IOrderedOwnedLifetime));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(ISessionLifetime));
        Assert.Single(fields, field => field.FieldType == typeof(IAuditRuntimeResources));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(AuditRuntimeResources));
        Assert.Single(
            fields,
            field => field.FieldType == typeof(Func<IAuditRuntimeResources>));
        Assert.DoesNotContain(
            fields,
            field => field.FieldType == typeof(Func<AuditRuntimeResources>));

        var runOwned = Assert.Single(
            typeof(AuditRuntimeGate)
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic),
            method => method.Name == "RunSessionAfterStarted");
        var lifetimeParameter = Assert.Single(runOwned.GetGenericArguments());
        Assert.Equal(
            [typeof(IOrderedOwnedLifetime)],
            lifetimeParameter.GetGenericParameterConstraints());
    }

    [Fact]
    public void Output_adapters_preserve_concrete_boundaries_and_use_guardian_capabilities_inside()
    {
        var output = typeof(OutputTool).GetMethod(
            nameof(OutputTool.Output),
            BindingFlags.Public | BindingFlags.Static)!;
        AssertParameterContract(
            output,
            [
                "store",
                "handle",
                "action",
                "offset",
                "maxBytes",
                "pattern",
                "cancellationToken",
                "auditContext",
            ],
            [
                typeof(OutputStore),
                typeof(string),
                typeof(string),
                typeof(long),
                typeof(int),
                typeof(string),
                typeof(CancellationToken),
                typeof(AuditCallContextAccessor),
            ]);
        var outputCore = typeof(OutputTool).GetMethod(
            "OutputCore",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(IOutputArtifactReader), outputCore.GetParameters()[0].ParameterType);
        Assert.DoesNotContain(
            outputCore.GetParameters(),
            parameter => parameter.ParameterType == typeof(OutputStore));

        var sessionInterface = typeof(ISessionOperations).GetMethod(
            nameof(ISessionOperations.InvokeAsync))!;
        AssertParameterContract(
            sessionInterface,
            [
                "script",
                "cancellationToken",
                "raw",
                "route",
                "background",
                "timeoutSeconds",
                "auditContext",
                "outputStore",
            ],
            [
                typeof(string),
                typeof(CancellationToken),
                typeof(bool),
                typeof(string),
                typeof(bool),
                typeof(int),
                typeof(AuditCallContextAccessor),
                typeof(OutputStore),
            ]);
        var sessionBoundary = Assert.Single(
            typeof(SessionRuntime).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic),
            method => method.Name == "InvokeAsync");
        Assert.Equal(typeof(OutputStore), sessionBoundary.GetParameters()[^1].ParameterType);
        var sessionCore = Assert.Single(
            typeof(SessionRuntime).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic),
            method => method.Name == "InvokeCoreAsync");
        Assert.Equal(typeof(IOutputCaptureOwner), sessionCore.GetParameters()[^1].ParameterType);
        Assert.DoesNotContain(
            sessionCore.GetParameters(),
            parameter => parameter.ParameterType == typeof(OutputStore));

        var commitBoundary = typeof(JobManager).GetMethod(
            nameof(JobManager.CommitStart),
            BindingFlags.Public | BindingFlags.Instance)!;
        AssertParameterContract(
            commitBoundary,
            ["plan", "onTerminal", "deadline", "cancellationToken", "outputStore"],
            [
                typeof(JobStartPlan),
                typeof(Func<JobSnapshot, Task>),
                typeof(DateTimeOffset?),
                typeof(CancellationToken),
                typeof(OutputStore),
            ]);
        var commitCore = typeof(JobManager).GetMethod(
            "CommitStartCore",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Equal(typeof(IOutputCaptureOwner), commitCore.GetParameters()[^1].ParameterType);
        Assert.DoesNotContain(
            commitCore.GetParameters(),
            parameter => parameter.ParameterType == typeof(OutputStore));
        var admittedStart = typeof(JobManager).GetMethod(
            "CommitAdmittedStart",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Equal(
            typeof(IOutputCaptureOwner),
            admittedStart.GetParameters()[^1].ParameterType);
        Assert.DoesNotContain(
            admittedStart.GetParameters(),
            parameter => parameter.ParameterType == typeof(OutputStore));
        var prepareRecovery = typeof(JobManager).GetMethod(
            "PrepareOutputRecovery",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Equal(
            typeof(IOutputCaptureOwner),
            prepareRecovery.GetParameters()[1].ParameterType);
        Assert.DoesNotContain(
            prepareRecovery.GetParameters(),
            parameter => parameter.ParameterType == typeof(OutputStore));

        var captureField = typeof(ForegroundOutputCapture).GetField(
            "_store",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(typeof(IOutputCaptureOwner), captureField.FieldType);
        var captureConstructor = Assert.Single(
            typeof(ForegroundOutputCapture).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Equal(
            typeof(IOutputCaptureOwner),
            captureConstructor.GetParameters()[0].ParameterType);

        var jobEntry = typeof(JobManager).GetNestedType(
            "JobEntry",
            BindingFlags.NonPublic)!;
        Assert.Equal(
            typeof(IOutputCaptureOwner),
            jobEntry.GetField("OutputRecoveryStore")!.FieldType);
        Assert.DoesNotContain(
            jobEntry.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            field => field.FieldType == typeof(OutputStore));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("read")]
    [InlineData("search")]
    public void Output_core_dispatches_through_an_interface_only_reader(string action)
    {
        var reader = new FakeOutputArtifactReader();

        var response = OutputTool.OutputCore(
            reader,
            "opaque-handle",
            action,
            offset: 0,
            maxBytes: 64,
            pattern: action == "search" ? "needle" : null,
            cancellationToken: CancellationToken.None,
            auditContext: null);

        Assert.Contains($"action={action}", response, StringComparison.Ordinal);
        Assert.Equal(action == "status" ? 1 : 0, reader.StatusCalls);
        Assert.Equal(action == "read" ? 1 : 0, reader.ReadCalls);
        Assert.Equal(action == "search" ? 1 : 0, reader.SearchCalls);
    }

    [Fact]
    public async Task Foreground_capture_dispatches_through_an_interface_only_owner()
    {
        var owner = new FakeOutputCaptureOwner();
        using var capture = new ForegroundOutputCapture(owner);

        Assert.Equal(1234, capture.MaximumArtifactBytes);
        await capture.PrepareAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        var recovery = await capture.SealAsync(
            new OutputArtifactContent(
                "unused",
                [],
                [],
                [],
                ExitCode: 0,
                OutputProvenance.DirectText),
            TimeSpan.FromSeconds(1));

        Assert.Equal("output_store_capacity", recovery.DetailCode);
        Assert.Equal(1, owner.MaximumArtifactBytesCalls);
        Assert.Equal(1, owner.TryStartCalls);
        Assert.Equal(1, owner.TryReserveCalls);
    }

    private static void AssertOutputCaptureOwnerShape()
    {
        var type = typeof(IOutputCaptureOwner);
        Assert.True(type.IsInterface);
        Assert.True(type.IsNotPublic);
        Assert.Empty(type.GetInterfaces());

        var property = Assert.Single(type.GetProperties(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly));
        Assert.Equal("MaximumArtifactBytes", property.Name);
        Assert.Equal(typeof(long), property.PropertyType);
        Assert.True(property.CanRead);
        Assert.False(property.CanWrite);

        var methods = type.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .ToArray();
        Assert.Equal(2, methods.Length);

        var reserve = Assert.Single(methods, method => method.Name == "TryReserve");
        Assert.False(reserve.IsGenericMethod);
        Assert.Equal(typeof(bool), reserve.ReturnType);
        Assert.Equal(
            [
                typeof(string),
                typeof(OutputCaptureReservation).MakeByRefType(),
                typeof(string).MakeByRefType(),
            ],
            reserve.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.False(reserve.GetParameters()[0].IsOut);
        Assert.True(reserve.GetParameters()[1].IsOut);
        Assert.True(reserve.GetParameters()[2].IsOut);

        var start = Assert.Single(
            methods,
            method => method.Name == "TryStartForegroundOperation");
        Assert.True(start.IsGenericMethodDefinition);
        Assert.Equal(typeof(bool), start.ReturnType);
        var genericArgument = Assert.Single(start.GetGenericArguments());
        Assert.Equal(
            [
                typeof(Func<>).MakeGenericType(genericArgument),
                typeof(Task<>).MakeGenericType(genericArgument).MakeByRefType(),
            ],
            start.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.False(start.GetParameters()[0].IsOut);
        Assert.True(start.GetParameters()[1].IsOut);
        Assert.Equal(
            ["work", "operation"],
            start.GetParameters().Select(parameter => parameter.Name));
        Assert.Equal(
            ["sessionAlias", "reservation", "failure"],
            reserve.GetParameters().Select(parameter => parameter.Name));

        var nullability = new NullabilityInfoContext();
        AssertNullableOut(
            nullability,
            start.GetParameters().Single(parameter => parameter.Name == "operation"));
        AssertNullableOut(
            nullability,
            reserve.GetParameters().Single(parameter => parameter.Name == "reservation"));
        AssertNullableOut(
            nullability,
            reserve.GetParameters().Single(parameter => parameter.Name == "failure"));
    }

    private static void AssertNullableOut(
        NullabilityInfoContext context,
        ParameterInfo parameter)
    {
        Assert.True(parameter.IsOut);
        var info = context.Create(parameter);
        Assert.Equal(NullabilityState.Nullable, info.ReadState);
        Assert.Equal(NullabilityState.Nullable, info.WriteState);
    }

    private static void AssertParameterContract(
        MethodInfo method,
        string[] names,
        Type[] types)
    {
        Assert.Equal(names, method.GetParameters().Select(parameter => parameter.Name));
        Assert.Equal(types, method.GetParameters().Select(parameter => parameter.ParameterType));
    }

    private static void AssertInternalInterface(
        Type type,
        Type[] inheritedInterfaces,
        (string Name, Type Type)[] properties,
        (string Name, Type ReturnType, Type[] ParameterTypes)[] methods)
    {
        Assert.True(type.IsInterface);
        Assert.True(type.IsNotPublic);
        Assert.Equal(inheritedInterfaces, type.GetInterfaces());

        var actualProperties = type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(properties.Length, actualProperties.Length);
        for (var index = 0; index < properties.Length; index++)
        {
            Assert.Equal(properties[index].Name, actualProperties[index].Name);
            Assert.Equal(properties[index].Type, actualProperties[index].PropertyType);
            Assert.True(actualProperties[index].CanRead);
            Assert.False(actualProperties[index].CanWrite);
        }

        var actualMethods = type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(methods.Length, actualMethods.Length);
        for (var index = 0; index < methods.Length; index++)
        {
            Assert.Equal(methods[index].Name, actualMethods[index].Name);
            Assert.Equal(methods[index].ReturnType, actualMethods[index].ReturnType);
            Assert.Equal(
                methods[index].ParameterTypes,
                actualMethods[index].GetParameters().Select(parameter => parameter.ParameterType));
        }
    }

    private static void AssertExplicitImplementation<TImplementation, TInterface>()
    {
        var map = typeof(TImplementation).GetInterfaceMap(typeof(TInterface));
        Assert.NotEmpty(map.InterfaceMethods);
        Assert.Equal(map.InterfaceMethods.Length, map.TargetMethods.Length);
        Assert.All(map.TargetMethods, method => Assert.True(method.IsPrivate));
    }

    private sealed class FakeOutputArtifactReader : IOutputArtifactReader
    {
        internal int StatusCalls { get; private set; }
        internal int ReadCalls { get; private set; }
        internal int SearchCalls { get; private set; }

        public OutputArtifactStatus Status(string handle)
        {
            StatusCalls++;
            return new OutputArtifactStatus(
                OutputArtifactState.Available,
                Bytes: 3,
                Complete: true,
                OutputProvenance.DirectText,
                ExpiresUtc: null,
                DetailCode: null);
        }

        public OutputReadResult Read(string handle, long offset, int maximumBytes)
        {
            ReadCalls++;
            return new OutputReadResult(
                OutputArtifactState.Available,
                "abc",
                offset,
                NextOffset: 3,
                TotalBytes: 3,
                BytesRead: 3,
                Complete: true,
                OutputProvenance.DirectText,
                DetailCode: null);
        }

        public OutputSearchResult Search(
            string handle,
            string pattern,
            long offset,
            int maximumBytes)
        {
            SearchCalls++;
            return new OutputSearchResult(
                OutputArtifactState.Available,
                [new OutputSearchMatch(0, "abc")],
                offset,
                NextOffset: 3,
                TotalBytes: 3,
                BytesScanned: 3,
                Complete: true,
                OutputProvenance.DirectText,
                DetailCode: null);
        }
    }

    private sealed class FakeOutputCaptureOwner : IOutputCaptureOwner
    {
        internal int MaximumArtifactBytesCalls { get; private set; }
        internal int TryStartCalls { get; private set; }
        internal int TryReserveCalls { get; private set; }

        public long MaximumArtifactBytes
        {
            get
            {
                MaximumArtifactBytesCalls++;
                return 1234;
            }
        }

        public bool TryStartForegroundOperation<T>(
            Func<T> work,
            out Task<T>? operation)
        {
            TryStartCalls++;
            operation = Task.FromResult(work());
            return true;
        }

        public bool TryReserve(
            string sessionAlias,
            out OutputCaptureReservation? reservation,
            out string? failure)
        {
            TryReserveCalls++;
            reservation = null;
            failure = "capacity";
            return false;
        }
    }
}
