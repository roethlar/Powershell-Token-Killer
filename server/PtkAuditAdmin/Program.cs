using PtkMcpServer.Audit;

Environment.ExitCode = Run(args);

static int Run(string[] args)
{
    try
    {
        if (!TryParse(args, out var command))
        {
            Console.Error.WriteLine(
                "usage: ptk-audit-admin evidence read --id <uuid> | " +
                "evidence export --id <uuid> --output <absolute-path> | " +
                "disposition --boot-id <uuid> --event-id <uuid> " +
                "(--verified-receipt-digest <sha256> | --acknowledged-gap-reason <code>)");
            return 2;
        }

        using var startup = AuditStartupConfiguration.LoadFromEnvironment();
        var version = typeof(AuditOptions).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        using var journal = AuditAdminJournalSession.Open(startup.AuditOptions, version);
        var operations = new AuditAdminOperations(startup.AuditOptions, journal.Journal);

        switch (command)
        {
            case ReadEvidence read:
                operations.ReadEvidence(read.EvidenceId, Console.OpenStandardOutput());
                break;
            case ExportEvidence export:
                operations.ExportEvidence(export.EvidenceId, export.OutputPath);
                Console.Out.WriteLine("evidence export completed");
                break;
            case Disposition disposition:
                var id = operations.ApplyPermanentBlockDisposition(
                    disposition.BootId,
                    disposition.EventId,
                    disposition.Proof);
                Console.Out.WriteLine($"disposition {id:D} completed");
                break;
            default:
                throw new InvalidOperationException();
        }
        return 0;
    }
    catch (Exception exception) when (exception is not (
        OutOfMemoryException or StackOverflowException or AccessViolationException))
    {
        // Administration inputs can include sensitive paths and evidence.
        // Never reflect exception text, arguments, or payload bytes.
        Console.Error.WriteLine("audit administration failed");
        return 1;
    }
}

static bool TryParse(string[] args, out AdminCommand command)
{
    command = null!;
    if (args.Length == 4 &&
        args[0] == "evidence" && args[1] == "read" && args[2] == "--id")
    {
        command = new ReadEvidence(args[3]);
        return true;
    }
    if (args.Length == 6 &&
        args[0] == "evidence" && args[1] == "export" &&
        args[2] == "--id" && args[4] == "--output")
    {
        command = new ExportEvidence(args[3], args[5]);
        return true;
    }
    if (args.Length != 7 || args[0] != "disposition" ||
        args[1] != "--boot-id" || args[3] != "--event-id" ||
        !Guid.TryParseExact(args[2], "D", out var bootId) ||
        !Guid.TryParseExact(args[4], "D", out var eventId))
    {
        return false;
    }

    AuditOperatorDispositionProof proof;
    if (args[5] == "--verified-receipt-digest")
        proof = AuditOperatorDispositionProof.VerifiedReceipt(args[6]);
    else if (args[5] == "--acknowledged-gap-reason")
        proof = AuditOperatorDispositionProof.AcknowledgedGap(args[6]);
    else
        return false;
    command = new Disposition(bootId, eventId, proof);
    return true;
}

abstract record AdminCommand;
sealed record ReadEvidence(string EvidenceId) : AdminCommand;
sealed record ExportEvidence(string EvidenceId, string OutputPath) : AdminCommand;
sealed record Disposition(
    Guid BootId,
    Guid EventId,
    AuditOperatorDispositionProof Proof) : AdminCommand;
