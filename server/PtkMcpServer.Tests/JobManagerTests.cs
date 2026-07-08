namespace PtkMcpServer.Tests;

public sealed class JobManagerTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ptk-job-tests-" + Guid.NewGuid().ToString("N"));
    private readonly JobManager _jobs;

    public JobManagerTests() => _jobs = new JobManager(_dir);

    public void Dispose()
    {
        _jobs.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* logs may lag a beat */ }
    }

    private async Task<JobSnapshot> WaitForExitAsync(int id, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            var snapshot = _jobs.Snapshot(id)!;
            if (!snapshot.Running) return snapshot;
            Assert.True(DateTime.UtcNow < deadline, $"job {id} did not exit within {timeoutSeconds}s");
            await Task.Delay(200);
        }
    }

    [Fact]
    public async Task Job_runs_to_completion_and_output_is_readable()
    {
        var job = _jobs.Start("'job says hello'");

        var final = await WaitForExitAsync(job.Id);
        Assert.Equal(0, final.ExitCode);

        var read = _jobs.ReadOutput(job.Id, 0)!.Value;
        Assert.Contains("job says hello", read.Text);
        Assert.True(read.NextOffset > 0);
    }

    [Fact]
    public async Task Offset_paging_returns_only_new_output()
    {
        var job = _jobs.Start("'first'; 'second'");
        await WaitForExitAsync(job.Id);

        var all = _jobs.ReadOutput(job.Id, 0)!.Value;
        Assert.Contains("second", all.Text);

        var again = _jobs.ReadOutput(job.Id, all.NextOffset)!.Value;
        Assert.Equal(string.Empty, again.Text);
        Assert.Equal(all.NextOffset, again.NextOffset);
    }

    [Fact]
    public async Task Exit_code_propagates_from_the_job_script()
    {
        var job = _jobs.Start("exit 7");

        var final = await WaitForExitAsync(job.Id);
        Assert.Equal(7, final.ExitCode);
    }

    [Fact]
    public async Task Kill_terminates_a_running_job()
    {
        var job = _jobs.Start("Start-Sleep -Seconds 300");
        Assert.True(_jobs.Snapshot(job.Id)!.Running);

        Assert.True(_jobs.Kill(job.Id));

        var final = await WaitForExitAsync(job.Id);
        Assert.False(final.Running);
        Assert.False(_jobs.Kill(job.Id)); // already dead
    }

    [Fact]
    public async Task KillAll_reports_how_many_jobs_it_stopped()
    {
        var a = _jobs.Start("Start-Sleep -Seconds 300");
        var b = _jobs.Start("Start-Sleep -Seconds 300");

        Assert.Equal(2, _jobs.KillAll());
        await WaitForExitAsync(a.Id);
        await WaitForExitAsync(b.Id);
        Assert.Equal(0, _jobs.KillAll());
    }

    [Fact]
    public void Missing_job_reads_as_null()
    {
        Assert.Null(_jobs.Snapshot(999));
        Assert.Null(_jobs.ReadOutput(999, 0));
        Assert.False(_jobs.Kill(999));
    }
}
