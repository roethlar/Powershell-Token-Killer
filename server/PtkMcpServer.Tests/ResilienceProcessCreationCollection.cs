namespace PtkMcpServer.Tests;

// The native resilience fixture deliberately creates and hard-kills process
// groups. Keep it isolated from every other process-creation test so a failed
// assertion cannot race an unrelated child launch while cleanup is underway.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ResilienceProcessCreationCollection
{
    public const string Name = "Resilience process creation";
}
