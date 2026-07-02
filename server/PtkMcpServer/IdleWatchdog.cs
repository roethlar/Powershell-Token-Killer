using Microsoft.Extensions.Hosting;

namespace PtkMcpServer;

/// <summary>
/// Lifetime backstop: ends the process when no runspace activity has happened for
/// the idle timeout. Claude Code normally kills the stdio child when the session
/// ends; this catches the orphan case where it doesn't (SessionEnd is not
/// guaranteed to fire), so a warm server never outlives its usefulness by more
/// than the timeout.
/// </summary>
public sealed class IdleWatchdog(
    TimeSpan idleTimeout,
    Func<DateTimeOffset> lastActivityUtc,
    Action onIdle) : BackgroundService
{
    private static readonly TimeSpan MinPoll = TimeSpan.FromMilliseconds(50);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var idleDeadline = lastActivityUtc() + idleTimeout;
            var wait = idleDeadline - DateTimeOffset.UtcNow;
            if (wait <= TimeSpan.Zero)
            {
                onIdle();
                return;
            }
            await Task.Delay(wait < MinPoll ? MinPoll : wait, stoppingToken);
        }
    }
}
