namespace PtkMcpServer.Tests;

public class IdleWatchdogTests
{
    [Fact]
    public async Task Fires_once_the_idle_timeout_elapses()
    {
        var fired = new TaskCompletionSource();
        var stale = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        using var watchdog = new IdleWatchdog(
            idleTimeout: TimeSpan.FromMilliseconds(100),
            lastActivityUtc: () => stale,
            onIdle: () => fired.TrySetResult());

        await watchdog.StartAsync(CancellationToken.None);

        var completed = await Task.WhenAny(fired.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(fired.Task, completed);
    }

    [Fact]
    public async Task Does_not_fire_while_activity_keeps_arriving()
    {
        var fired = false;
        using var watchdog = new IdleWatchdog(
            idleTimeout: TimeSpan.FromMilliseconds(100),
            lastActivityUtc: () => DateTimeOffset.UtcNow,
            onIdle: () => fired = true);

        await watchdog.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        await watchdog.StopAsync(CancellationToken.None);

        Assert.False(fired);
    }

    [Fact]
    public async Task RunspaceHost_invocations_refresh_last_activity()
    {
        using var host = new RunspaceHost(callTimeout: TimeSpan.FromSeconds(60));
        var before = host.LastActivityUtc;

        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await host.InvokeAsync("'touch'");

        Assert.True(host.LastActivityUtc > before);
    }
}
