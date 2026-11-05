using System.Buffers.Binary;
using WaveFlume.Core;

namespace WaveFlume.Plc;

/// <summary>CRC-16/MODBUS (poly 0xA001 reflected, init 0xFFFF). Check value "123456789" → 0x4B37.</summary>
public static class Crc16
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)(crc >> 1 ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    /// <summary>CRC over float32 values as IEEE-754 big-endian bytes — independent of wire word order.</summary>
    public static ushort OfFloats(ReadOnlySpan<float> values)
    {
        var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(i * 4), values[i]);
        return Compute(bytes);
    }
}

/// <summary>Splits a trajectory into PLC ring-buffer chunks (spec §9.2) and uploads them.</summary>
public static class TrajectoryChunker
{
    /// <summary>Largest whole-sample chunk that fits both the tag and one protocol write.</summary>
    public static int ValuesPerChunk(int maxBlockWords, int wordsPerValue, int tagLength, int paddles)
    {
        int fit = Math.Min(tagLength, maxBlockWords / wordsPerValue) / paddles * paddles;
        if (fit < paddles)
            throw new InvalidOperationException("TrajChunk cannot hold one sample for every paddle within MaxBlockWords.");
        return fit;
    }

    public static IEnumerable<TrajectoryChunk> Split(Trajectory traj, int valuesPerChunk)
    {
        int paddles = traj.PaddleCount;
        if (paddles == 0 || valuesPerChunk % paddles != 0)
            throw new ArgumentException("Chunk size must be a whole number of samples.", nameof(valuesPerChunk));
        int samplesPerChunk = valuesPerChunk / paddles;
        int total = traj.SampleCount;
        ushort seq = 1;
        for (int s0 = 0; s0 < total; s0 += samplesPerChunk, seq = unchecked((ushort)(seq + 1)))
        {
            var values = new float[valuesPerChunk];
            for (int s = 0; s < samplesPerChunk; s++)
            {
                int src = Math.Min(s0 + s, total - 1); // pad the last chunk with the final sample
                for (int p = 0; p < paddles; p++)
                    values[s * paddles + p] = (float)Units.MToMm(traj.PositionsM[p][src]);
            }
            yield return new TrajectoryChunk(seq, values, Crc16.OfFloats(values), total);
        }
    }

    public static async Task<bool> UploadAsync(
        IPlcDriver driver, Trajectory traj, int valuesPerChunk, IProgress<double>? progress, CancellationToken ct)
    {
        int chunks = (traj.SampleCount * traj.PaddleCount + valuesPerChunk - 1) / valuesPerChunk, sent = 0;
        foreach (var chunk in Split(traj, valuesPerChunk))
        {
            ct.ThrowIfCancellationRequested();
            if (!await driver.UploadTrajectoryChunkAsync(chunk, ct).ConfigureAwait(false))
                return false;
            progress?.Report((double)++sent / chunks);
        }
        return true;
    }
}
