using PtkMcpServer.Worker;

namespace PtkMcpServer.Tests;

[Collection(WindowsProcessCreationCollection.Name)]
public sealed class WorkerBootstrapProcessEnvironmentTests
{
    [Fact]
    public void Default_source_captures_and_removes_both_reserved_variables()
    {
        var priorRequest = Environment.GetEnvironmentVariable(
            WorkerBootstrapEnvironment.RequestHandle);
        var priorEvent = Environment.GetEnvironmentVariable(
            WorkerBootstrapEnvironment.EventHandle);
        try
        {
            Environment.SetEnvironmentVariable(
                WorkerBootstrapEnvironment.RequestHandle,
                "101");
            Environment.SetEnvironmentVariable(
                WorkerBootstrapEnvironment.EventHandle,
                "202");

            var values = WorkerBootstrapCapture.CaptureAndRemove();

            Assert.Equal(new WorkerBootstrapValues("101", "202"), values);
            Assert.Null(Environment.GetEnvironmentVariable(
                WorkerBootstrapEnvironment.RequestHandle));
            Assert.Null(Environment.GetEnvironmentVariable(
                WorkerBootstrapEnvironment.EventHandle));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                WorkerBootstrapEnvironment.RequestHandle,
                priorRequest);
            Environment.SetEnvironmentVariable(
                WorkerBootstrapEnvironment.EventHandle,
                priorEvent);
        }
    }
}
