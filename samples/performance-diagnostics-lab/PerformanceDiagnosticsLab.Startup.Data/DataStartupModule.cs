using PerformanceDiagnosticsLab.Contracts;
using System.Runtime.CompilerServices;

namespace PerformanceDiagnosticsLab.Startup.Data;

public sealed class DataStartupModule : IStartupModule
{
    public string Name => "Data";

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Initialize(StartupContext _)
    {
        SeedDataRepository.Open();
    }
}

internal static class SeedDataRepository
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Open()
    {
        var payload = PayloadReader.Read();
        var records = PayloadParser.Parse(payload);
        RecordMaterializer.Materialize(records);
    }
}

internal static class PayloadReader
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static byte[] Read()
    {
        const int payloadBytes = 4 * 1024 * 1024;
        var payload = GC.AllocateUninitializedArray<byte>(payloadBytes);
        new Random(42).NextBytes(payload);
        return payload;
    }
}

internal static class PayloadParser
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int[] Parse(byte[] payload)
    {
        var records = new int[payload.Length / 64];
        for (var index = 0; index < records.Length; index++)
        {
            records[index] = BitConverter.ToInt32(payload, index * 64);
        }

        return records;
    }
}

internal static class RecordMaterializer
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Materialize(IReadOnlyList<int> records)
    {
        var checksum = 0L;
        foreach (var record in records)
        {
            checksum += Math.Abs((long)record);
        }

        GC.KeepAlive(checksum);
    }
}
