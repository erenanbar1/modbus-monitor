// STS.Core/Poller.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace STS.Core;

public sealed class Poller
{
    private readonly int _initialOfflinePollInterval;
    private readonly int _markOfflineThreshold;
    private const int MaxOfflinePollInterval = 60;

    public record DeviceUpdate(
        int Index,
        STSDevice Device,
        STSReading Reading,
        string Status,
        int ReadTimeMs,
        int CycleNo,
        bool TimedOut
    );
    public event Action<DeviceUpdate>? OnDeviceUpdate;

    private sealed class Entry
    {
        public STSDevice Device { get; }
        public int ConsecutiveTimeouts;
        public int OfflineCycles;
        public int OfflinePollInterval;
        public STSReading Reading = new(-1, -1, -1, -1, -1, -1, -1);
        public int ReadTimeMs;
        public Entry(STSDevice d, int initIv) { Device = d; OfflinePollInterval = initIv; }
    }

    private readonly List<Entry> _entries = new();
    private int _cycleNo;

    public Poller(IEnumerable<STSDevice> devices, int initialOfflinePollInterval = 5, int markOfflineThreshold = 3)
    {
        _initialOfflinePollInterval = initialOfflinePollInterval;
        _markOfflineThreshold = markOfflineThreshold;
        foreach (var d in devices)
            _entries.Add(new Entry(d, initialOfflinePollInterval));
    }

    public void AddDevice(STSDevice device)
    {
        if (_entries.Any(e => e.Device.Id == device.Id))
            return;
        _entries.Add(new Entry(device, _initialOfflinePollInterval));
    }

    //remove a device by Modbus id
    public bool RemoveDevice(byte deviceId)
    {
        int idx = _entries.FindIndex(e => e.Device.Id == deviceId);
        if (idx < 0) return false;
        _entries.RemoveAt(idx);
        return true;
    }

    public void Poll(IModbusClient mb)
    {
        int NextInterval(int cur) => cur <= 0 ? 2 : Math.Min(cur * 2, MaxOfflinePollInterval);
        void Emit(int idx, Entry e, string s, bool to)
            => OnDeviceUpdate?.Invoke(new DeviceUpdate(idx, e.Device, e.Reading, s, e.ReadTimeMs, _cycleNo + 1, to));

        void HandleTimeout(int idx, Entry e)
        {
            string status = e.Device.Online ? "TIMEOUT" : "TIMEOUT (STILL OFFLINE)";
            e.ConsecutiveTimeouts++;
            e.Reading = new(-1, -1, -1, -1, -1, -1, -1);
            int nextIv = NextInterval(e.OfflinePollInterval);
            if (e.Device.Online && e.ConsecutiveTimeouts >= _markOfflineThreshold)
            {
                e.Device.SetOffline();
                status += " [SET OFFLINE]";
                e.OfflineCycles = 0;
                e.OfflinePollInterval = nextIv;
            }
            else if (!e.Device.Online)
            {
                e.OfflinePollInterval = nextIv;
            }
            Emit(idx, e, status, true);
        }

        void HandleSuccess(int idx, Entry e, STSReading reading)
        {
            e.Reading = reading;
            string status = e.Device.Online ? "OK" : "OK (BACK ONLINE)";
            e.Device.SetOnline();
            e.ConsecutiveTimeouts = 0;
            e.OfflineCycles = 0;
            e.OfflinePollInterval = _initialOfflinePollInterval;
            Emit(idx, e, status, false);
        }

        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];

            if (!e.Device.Online)
            {
                e.OfflineCycles++;
                if (e.OfflineCycles < e.OfflinePollInterval)
                {
                    e.ReadTimeMs = 0;
                    e.Reading = new(-1, -1, -1, -1, -1, -1, -1);
                    Emit(i, e, "OFFLINE (SKIP)", false);
                    continue;
                }
                e.OfflineCycles = 0;
            }

            var sw = Stopwatch.StartNew();
            bool ok = e.Device.Poll(
                mb, RegFn.Holding03,
                out var reading, out var timedOut,
                start: 0x0020, count: 0x0038 - 0x0020 + 1);
            sw.Stop();
            e.ReadTimeMs = (int)sw.ElapsedMilliseconds;

            if (!ok || timedOut)
                HandleTimeout(i, e);
            else
                HandleSuccess(i, e, reading);
        }

        _cycleNo++;
    }
}
