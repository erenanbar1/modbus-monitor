// STS.Core/SerialModbusClient.cs
using NModbus;
using NModbus.IO;     // IStreamResource
using NModbus.Serial; // SerialPortAdapter
using System;
using System.IO;
using System.IO.Ports;
using System.Text.RegularExpressions;

namespace STS.Core
{
    public sealed class SerialModbusClient : IModbusClient, IDisposable
    {
        private readonly SerialPort _port;
        private readonly IModbusMaster _master;

        public bool LastCallTimedOut { get; private set; }

        public SerialModbusClient(string portName, int baud, Parity parity, int dataBits, StopBits stopBits, int timeoutMs)
        {
            _port = new SerialPort(portName, baud, parity, dataBits, stopBits)
            {
                ReadTimeout = timeoutMs,
                WriteTimeout = timeoutMs
            };
            _port.Open();

            var factory = new ModbusFactory();
            IStreamResource resource = new SerialPortAdapter(_port);
            _master = factory.CreateRtuMaster(resource);
        }

        public int ReadBlock(RegFn fn, byte slaveId, ushort startAddress, ushort count, ushort[] dest)
            => ReadBlock(fn, slaveId, startAddress, count, dest, perCallTimeoutMs: null);

        public int ReadBlock(RegFn fn, byte slaveId, ushort startAddress, ushort count, ushort[] dest, int? perCallTimeoutMs)
        {
            LastCallTimedOut = false;
            int origR = _port.ReadTimeout, origW = _port.WriteTimeout;

            try
            {
                if (perCallTimeoutMs.HasValue)
                {
                    _port.ReadTimeout = perCallTimeoutMs.Value;
                    _port.WriteTimeout = perCallTimeoutMs.Value;
                }

                ushort[] regs = fn switch
                {
                    RegFn.Holding03 => _master.ReadHoldingRegisters(slaveId, startAddress, count),
                    RegFn.Input04 => _master.ReadInputRegisters(slaveId, startAddress, count),
                    _ => throw new NotSupportedException("Unknown function")
                };

                Array.Copy(regs, dest, regs.Length);
                return regs.Length;
            }
            catch (TimeoutException)
            {
                LastCallTimedOut = true;
                return 0;
            }
            catch (IOException io) when (io.InnerException is TimeoutException)
            {
                LastCallTimedOut = true;
                return 0;
            }
            catch
            {
                return 0;
            }
            finally
            {
                if (perCallTimeoutMs.HasValue)
                {
                    _port.ReadTimeout = origR;
                    _port.WriteTimeout = origW;
                }
            }
        }

        public void Dispose()
        {
            try { _port?.Close(); } catch { /* ignore */ }
            _port?.Dispose();
        }
    }
}
