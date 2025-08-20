// STS.Core/STSDevice.cs
using System;
using System.Text.RegularExpressions;

namespace STS.Core;

public record STSReading(
    double OutputVoltageV,
    double OutputCurrentA,
    double Source1VoltageV,
    double Source2VoltageV,
    double DiffVoltageV,
    double FrequencyHz,
    int ActiveSource   // 0: NONE, 1: SRC1, 2: SRC2
)
{
    public string ActiveSourceName => ActiveSource switch
    {
        1 => "SRC1",
        2 => "SRC2",
        _ => "NONE"
    };
}

public class STSDevice
{
    private const ushort BASE = 0x0020;
    private const ushort END = 0x0038;
    private const ushort COUNT = END - BASE + 1;

    public byte Id { get; }
    public bool Online { get; private set; } = true;
    public bool LastPollTimedOut { get; private set; }

    public STSDevice(int slaveId) => Id = (byte)slaveId;

    public void SetOnline() => Online = true;
    public void SetOffline() => Online = false;

    /// <summary>
    /// Poll with optional window and per-call timeout.
    /// Defaults to the full [BASE..END] block if not specified.
    /// </summary>
    public bool Poll(
        IModbusClient bus,
        RegFn fn,
        out STSReading reading,
        out bool lastPollTimedOut,
        ushort start = BASE,
        ushort count = COUNT
    )
    {
        var regs = new ushort[count];
        int rc = bus.ReadBlock(fn, Id, start, count, regs);
        lastPollTimedOut = bus.LastCallTimedOut;
        LastPollTimedOut = lastPollTimedOut;

        if (rc != count)
        {
            reading = default!;
            return false;
        }

        // When reading a sub-window, synthesize STSReading from what we have
        if (start == BASE && count == COUNT)
        {
            reading = Decode_0020_0038(regs);
        }
        else
        {
            // Minimal decode so callers can still publish quickly
            var full = new ushort[COUNT];
            // place subset into full (others remain 0)
            Array.Copy(regs, 0, full, start - BASE, regs.Length);
            reading = Decode_0020_0038(full);
        }

        return true;
    }

    private static STSReading Decode_0020_0038(ushort[] r)
    {
        static int I(int reg, int baseAddr) => reg - baseAddr;
        int b = BASE;

        double outputVoltageV = r[I(0x0020, b)];
        double outputCurrentA = r[I(0x0021, b)] * 0.1;
        double source1VoltageV = r[I(0x0022, b)];
        double source2VoltageV = r[I(0x0023, b)];
        double diffVoltageV = r[I(0x0024, b)];
        double frequencyHz = r[I(0x002F, b)] * 0.1;
        int activeSource = r[I(0x0038, b)];

        return new STSReading(outputVoltageV, outputCurrentA,
                              source1VoltageV, source2VoltageV,
                              diffVoltageV, frequencyHz, activeSource);
    }
}
