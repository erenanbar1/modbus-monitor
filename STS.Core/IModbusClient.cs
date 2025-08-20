// STS.Core/IModbusClient.cs
using System;

namespace STS.Core;

public interface IModbusClient : IDisposable
{
    /// <summary>Reads a contiguous block of registers.</summary>
    /// <returns>Number of registers read (== count on success).</returns>
    int ReadBlock(RegFn fn, byte slaveId, ushort startAddress, ushort count, ushort[] dest);

    bool LastCallTimedOut { get; }
}
