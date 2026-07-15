namespace PtkMcpServer.Tests;

// The Windows launcher briefly marks only its selected child handles
// inheritable before CreateProcessW. xUnit guarantees that a collection with
// this flag does not overlap any other collection, including ordinary tests
// which spawn unrelated processes.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WindowsProcessCreationCollection
{
    public const string Name = "Windows process creation";
}
