using System.Text;

namespace WaveFlume.Plc;

/// <summary>Generates docs/PLC_IO_MAP.md from TagMap profiles (spec §9.2) so the doc never drifts from the code.</summary>
public static class PlcIoMap
{
    public static string ToMarkdown(IEnumerable<TagMap> profiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# PLC I/O map").AppendLine();
        sb.AppendLine("Generated from `config/plc_profiles/*.json` by `PlcIoMap.ToMarkdown` — edit the profiles, then regenerate");
        sb.AppendLine("(`WAVEFLUME_REGEN_DOCS=1 dotnet test`). Do not hand-edit.").AppendLine();

        sb.AppendLine("## Safety — defence in depth").AppendLine();
        sb.AppendLine("- The hardwired E-stop chain is the primary stop. The `EStop` command bit is an additional request only.");
        sb.AppendLine("- **The PLC must enforce the same limits as the PC gatekeeper**: stroke clamp (≤ ±450 mm ceiling, default ±350 mm),");
        sb.AppendLine("  velocity ≤ 1.57 m/s, the configured acceleration limit, and must refuse trajectories that break them.");
        sb.AppendLine("- PC heartbeat (`HeartbeatPc`) toggles every telemetry cycle. If it stops changing for longer than the");
        sb.AppendLine("  configured timeout while running, the PLC must Smooth Stop and set `FaultWord.CommsLoss`.");
        sb.AppendLine("- The PLC heartbeat (`Heartbeat`) must change every PLC cycle; the PC Smooth Stops when it freezes.").AppendLine();

        sb.AppendLine("## CmdWord bits (PC → PLC)").AppendLine();
        AppendBits<CommandBits>(sb, "Enable and ModeTrajectory are levels; the other bits are pulses — act on the rising edge.");
        sb.AppendLine("## StatusWord bits (PLC → PC)").AppendLine();
        AppendBits<StatusBits>(sb, null);
        sb.AppendLine("## FaultWord bits (PLC → PC)").AppendLine();
        AppendBits<FaultBits>(sb, "DriveFault and StrokeLimit are hard faults: the PC commands E-stop.");

        sb.AppendLine("## Parametric mode").AppendLine();
        sb.AppendLine("PC writes `Period_s`, `Ramp_s`, `Cycles` (number of waves), `Amp_mm[1..N]`, `Phase_deg[1..N]`, then `CmdWord` with");
        sb.AppendLine("ModeTrajectory = 0. The PLC generates `X_p(t) = R(t)·Amp_p·sin(2πt/T + Phase_p)` with the half-cosine ramp up and down.").AppendLine();

        sb.AppendLine("## Trajectory mode (chunk handshake)").AppendLine();
        sb.AppendLine("1. Chunk 1 only: PC writes `Cycles` = total samples per paddle and sets ModeTrajectory = 1.");
        sb.AppendLine("2. PC waits until `BufferLevel` (free samples in the PLC ring) ≥ samples in the chunk.");
        sb.AppendLine("3. PC writes `TrajChunk` (mm, sample-major: s0p1..s0pN, s1p1..), `ChunkCrc`, `ChunkSeq`, then `ChunkReady` = 1.");
        sb.AppendLine("4. PLC checks `ChunkSeq` = previous + 1 (chunk 1 restarts) and the CRC, copies the samples, clears `ChunkReady`,");
        sb.AppendLine("   and sets `ChunkAck` = `ChunkSeq`. On a bad CRC or sequence it sets `FaultWord.ChunkError` and does not acknowledge.");
        sb.AppendLine("5. The last chunk is padded by repeating its final sample; play only `Cycles` samples.");
        sb.AppendLine("6. CRC: CRC-16/MODBUS (poly 0xA001 reflected, init 0xFFFF) over the chunk values as IEEE-754 float32");
        sb.AppendLine("   **big-endian bytes**, independent of the wire word order. Check value of ASCII \"123456789\" = 0x4B37.");
        sb.AppendLine("7. Samples are at `command_fs_hz`; the PLC interpolates to the servo cycle.").AppendLine();

        foreach (var map in profiles)
        {
            bool words = map.Protocol is PlcProtocolType.ModbusTcp or PlcProtocolType.OmronFins;
            sb.AppendLine($"## Profile: {map.Name} ({map.Protocol})").AppendLine();
            if (words) sb.AppendLine($"32-bit word order: **{map.WordOrder}**. BOOL occupies a whole word (0/1).").AppendLine();
            sb.AppendLine(words
                ? "| Signal | Address | Words | Type | Length | Scale | Offset | R/W | Units |"
                : "| Signal | Address | Type | Length | Scale | Offset | R/W | Units |");
            sb.AppendLine(words ? "|---|---|---|---|---|---|---|---|---|" : "|---|---|---|---|---|---|---|---|");
            foreach (var t in map.Tags)
            {
                string addr = t.Address.Replace("|", "\\|");
                sb.AppendLine(words
                    ? $"| {t.Signal} | `{addr}` | {WordCodec.WordCount(t)} | {t.DataType} | {t.Length} | {t.Scale} | {t.Offset} | {t.Access} | {t.Units.Replace("|", "\\|")} |"
                    : $"| {t.Signal} | `{addr}` | {t.DataType} | {t.Length} | {t.Scale} | {t.Offset} | {t.Access} | {t.Units.Replace("|", "\\|")} |");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void AppendBits<T>(StringBuilder sb, string? note) where T : struct, Enum
    {
        sb.AppendLine("| Bit | Name |").AppendLine("|---|---|");
        foreach (var v in Enum.GetValues<T>())
        {
            ushort n = Convert.ToUInt16(v);
            if (n != 0 && (n & (n - 1)) == 0)
                sb.AppendLine($"| {System.Numerics.BitOperations.Log2(n)} | {v} |");
        }
        sb.AppendLine();
        if (note is not null) sb.AppendLine(note).AppendLine();
    }
}
