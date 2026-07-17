using System.Reflection;
using PtkMcpGuardian.Ownership;
using PtkMcpServer.Audit;
using PtkMcpServer.Sessions;

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
}
