using System.Text;
using PtkSharedContracts;

namespace PtkMcpServer.Tests;

public sealed class PublicRecoveryMatrixTests
{
    [Fact]
    public void Every_detail_phase_and_gate_combination_matches_the_frozen_closed_table()
    {
        RecoveryPhase?[] phases = [null, .. Enum.GetValues<RecoveryPhase>().Cast<RecoveryPhase?>()];
        RetryGate?[] gates =
        [
            null,
            new HostReadyGate(),
            new SessionReadyGate(new CanonicalAlias("default")),
        ];
        var tested = 0;

        foreach (var detail in Enum.GetValues<PublicRecoveryDetailCode>())
        foreach (var phase in phases)
        foreach (var gate in gates)
        {
            tested++;
            var retryable = IsRetryable(detail);
            var allowed = IsAllowed(detail, phase, gate);
            Func<PublicRecoveryError> create = () => new PublicRecoveryError(
                detail,
                retryable,
                retryable ? ContractLimits.MinimumRetryAfterMilliseconds : null,
                phase,
                retryable ? 1 : null,
                gate);

            if (allowed)
            {
                var expected = create();
                Assert.Equal(expected, PublicRecoveryCodec.Decode(PublicRecoveryCodec.Encode(expected)));
            }
            else
            {
                Assert.Throws<ArgumentException>(create);
            }
        }

        Assert.Equal(210, tested);
    }

    [Fact]
    public void Retryability_delay_and_attempt_bounds_are_exact()
    {
        var gate = new HostReadyGate();
        Assert.Throws<ArgumentException>(() => Error(retryAfter: 249, attempt: 1, gate));
        _ = Error(retryAfter: 250, attempt: 1, gate);
        _ = Error(retryAfter: 60_000, attempt: long.MaxValue, gate);
        Assert.Throws<ArgumentException>(() => Error(retryAfter: 60_001, attempt: 1, gate));
        Assert.Throws<ArgumentException>(() => Error(retryAfter: 250, attempt: 0, gate));

        foreach (var detail in Enum.GetValues<PublicRecoveryDetailCode>())
        {
            Assert.Throws<ArgumentException>(() => new PublicRecoveryError(
                detail,
                !IsRetryable(detail),
                null,
                null,
                null,
                null));
        }
    }

    [Fact]
    public void Decode_rejects_malformed_noncanonical_duplicate_unknown_and_overbound_content()
    {
        var canonical = PublicRecoveryCodec.Encode(Error(
            ContractLimits.MinimumRetryAfterMilliseconds,
            1,
            new HostReadyGate()));
        var text = Encoding.UTF8.GetString(canonical);

        Assert.Throws<DecoderFallbackException>(() =>
            PublicRecoveryCodec.Decode(new byte[] { 0xff, 0xfe }));
        Assert.Throws<InvalidDataException>(() => PublicRecoveryCodec.Decode(
            Encoding.UTF8.GetBytes(text.Replace(",", ", ", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => PublicRecoveryCodec.Decode(
            Encoding.UTF8.GetBytes(text.Replace(
                "\"retryable\":true",
                "\"retryable\":true,\"retryable\":true",
                StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => PublicRecoveryCodec.Decode(
            Encoding.UTF8.GetBytes(text.Replace(
                "\"retryable\":true",
                "\"future\":null,\"retryable\":true",
                StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => PublicRecoveryCodec.Decode(
            Encoding.UTF8.GetBytes(text.Replace(
                "backend_lost_before_dispatch",
                "future_detail",
                StringComparison.Ordinal))));

        var exactMaximum = new byte[ContractLimits.MaximumPublicRecoveryBytes];
        canonical.CopyTo(exactMaximum, 0);
        exactMaximum.AsSpan(canonical.Length).Fill((byte)' ');
        var atBoundary = Assert.Throws<InvalidDataException>(() =>
            PublicRecoveryCodec.Decode(exactMaximum));
        Assert.Contains("canonical compact form", atBoundary.Message, StringComparison.Ordinal);

        var overBoundary = Assert.Throws<InvalidDataException>(() =>
            PublicRecoveryCodec.Decode(
                new byte[ContractLimits.MaximumPublicRecoveryBytes + 1]));
        Assert.Contains("outside its frozen bound", overBoundary.Message, StringComparison.Ordinal);
    }

    private static PublicRecoveryError Error(int retryAfter, long attempt, RetryGate gate) => new(
        PublicRecoveryDetailCode.BackendLostBeforeDispatch,
        true,
        retryAfter,
        RecoveryPhase.Attempting,
        attempt,
        gate);

    private static bool IsRetryable(PublicRecoveryDetailCode detail) => detail is
        PublicRecoveryDetailCode.BackendLostBeforeDispatch or
        PublicRecoveryDetailCode.HostCircuitOpen or
        PublicRecoveryDetailCode.HostRecovering or
        PublicRecoveryDetailCode.SessionRecovering;

    private static bool IsAllowed(
        PublicRecoveryDetailCode detail,
        RecoveryPhase? phase,
        RetryGate? gate)
    {
        if (!IsRetryable(detail)) return phase is null && gate is null;
        if (phase is null || gate is null) return false;
        if (detail == PublicRecoveryDetailCode.HostCircuitOpen &&
            phase != RecoveryPhase.CircuitOpen) return false;
        if (detail == PublicRecoveryDetailCode.HostRecovering &&
            phase == RecoveryPhase.CircuitOpen) return false;
        if (detail == PublicRecoveryDetailCode.SessionRecovering &&
            gate is not SessionReadyGate) return false;
        return true;
    }
}
