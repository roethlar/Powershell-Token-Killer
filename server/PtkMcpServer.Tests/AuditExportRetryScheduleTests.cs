using PtkMcpServer.Audit;

namespace PtkMcpServer.Tests;

public sealed class AuditExportRetryScheduleTests
{
    [Fact]
    public void Full_jitter_starts_at_one_second_and_caps_at_sixty_seconds()
    {
        var schedule = new AuditExportRetrySchedule(() => 0.5d);

        Assert.Equal(TimeSpan.FromMilliseconds(500), schedule.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(1), schedule.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(2), schedule.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(4), schedule.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(8), schedule.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(16), schedule.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(30), schedule.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(30), schedule.NextDelay());
    }

    [Fact]
    public void Retry_after_is_honored_exactly_and_still_advances_the_failure_series()
    {
        var schedule = new AuditExportRetrySchedule(() => 0.5d);

        Assert.Equal(
            TimeSpan.FromMinutes(17),
            schedule.NextDelay(TimeSpan.FromMinutes(17)));
        Assert.Equal(TimeSpan.FromSeconds(1), schedule.NextDelay());
    }

    [Fact]
    public void Successful_progress_resets_the_failure_series()
    {
        var schedule = new AuditExportRetrySchedule(() => 0.5d);
        _ = schedule.NextDelay();
        _ = schedule.NextDelay();

        schedule.Reset();

        Assert.Equal(TimeSpan.FromMilliseconds(500), schedule.NextDelay());
    }

    [Fact]
    public void Invalid_retry_or_random_input_fails_closed()
    {
        var schedule = new AuditExportRetrySchedule(() => 0.5d);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            schedule.NextDelay(TimeSpan.FromTicks(-1)));

        Assert.Throws<InvalidOperationException>(() =>
            new AuditExportRetrySchedule(() => 1d).NextDelay());
    }
}
