using System.Text;

namespace PtkMcpServer.Tests;

public sealed class TransferredOutputStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"ptk-transferred-output-{Guid.NewGuid():N}");

    [Fact]
    public void Transferred_incomplete_utf8_is_published_byte_exact_without_invented_provenance()
    {
        using var store = CreateStore();
        Assert.True(store.TryReserve("default", out var reservation, out var failure));
        Assert.Null(failure);
        using var ownedReservation = reservation!;
        var bytes = Encoding.UTF8.GetBytes("α-first\n[stderr]\nverbatim\n");
        var expected = bytes.ToArray();

        var seal = ownedReservation.SealTransferredUtf8(
            bytes,
            complete: false,
            incompleteReason: "host_generation_lost");
        ownedReservation.CompleteObserved();
        Array.Fill<byte>(bytes, 0x58);

        Assert.True(seal.Success);
        Assert.NotNull(seal.Handle);
        Assert.Equal(OutputArtifactState.Incomplete, seal.State);
        Assert.Equal(expected.Length, seal.Bytes);
        Assert.Equal("host_generation_lost", seal.DetailCode);
        var status = store.Status(seal.Handle!);
        Assert.Null(status.Provenance);
        Assert.False(status.Complete);
        var read = store.Read(seal.Handle!, 0, OutputStore.MaximumReadBytes);
        Assert.Equal(expected.Length, read.BytesRead);
        Assert.Equal(Encoding.UTF8.GetString(expected), read.Text);
    }

    [Fact]
    public void Transferred_complete_utf8_can_preserve_known_provenance()
    {
        using var store = CreateStore();
        Assert.True(store.TryReserve("default", out var reservation, out _));
        using var ownedReservation = reservation!;
        var bytes = Encoding.UTF8.GetBytes("exact output");

        var seal = ownedReservation.SealTransferredUtf8(
            bytes,
            complete: true,
            incompleteReason: null,
            OutputProvenance.PowerShellObjects);
        ownedReservation.CompleteObserved();

        Assert.True(seal.Success);
        Assert.Equal(OutputArtifactState.Available, seal.State);
        var status = store.Status(seal.Handle!);
        Assert.True(status.Complete);
        Assert.Equal(OutputProvenance.PowerShellObjects, status.Provenance);
        Assert.Null(status.DetailCode);
    }

    [Fact]
    public void Invalid_transferred_utf8_never_claims_the_reservation()
    {
        using var store = CreateStore();
        Assert.True(store.TryReserve("default", out var reservation, out _));
        using var ownedReservation = reservation!;

        Assert.Throws<DecoderFallbackException>(() =>
            ownedReservation.SealTransferredUtf8(
                new byte[] { 0xc3, 0x28 },
                complete: true,
                incompleteReason: null));
        Assert.True(ownedReservation.TryCancel());
    }

    private OutputStore CreateStore() => new(new OutputStoreOptions(
        _root,
        TimeSpan.FromMinutes(5),
        TimeSpan.FromHours(1),
        MaximumArtifactBytes: 1024,
        MaximumSessionBytes: 4096,
        MaximumAggregateBytes: 8192));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }
}
