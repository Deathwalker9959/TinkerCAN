using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TinkerCAN.Lin;

namespace LINGui.ViewModels;

public partial class LinViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _mainVm;
    private SerialPort? _port;
    private readonly object _portLock = new();
    private DispatcherTimer? _singleTimer;
    private System.Threading.Timer? _multiTimer;
    private System.Threading.Timer? _bfConstTimer;
    private System.Threading.Timer? _bfReplayTimer;
    private CancellationTokenSource? _bfCts;

    // Single signal
    [ObservableProperty] private string _sigId = "22";
    [ObservableProperty] private string _sigPid = "E2";
    [ObservableProperty] private int _sigLen = 8;
    [ObservableProperty] private bool _sigV2 = true;
    [ObservableProperty] private string _sigD0 = "FF";
    [ObservableProperty] private string _sigD1 = "FF";
    [ObservableProperty] private string _sigD2 = "FF";
    [ObservableProperty] private string _sigD3 = "FF";
    [ObservableProperty] private string _sigD4 = "FF";
    [ObservableProperty] private string _sigD5 = "FF";
    [ObservableProperty] private string _sigD6 = "FF";
    [ObservableProperty] private string _sigD7 = "FF";
    [ObservableProperty] private string _sigModifier = "D0=D0+1\n";
    [ObservableProperty] private int _sigIntervalMs = 100;
    [ObservableProperty] private long _sigCount;
    [ObservableProperty] private bool _sigRunning;

    // Multi-signal
    public ObservableCollection<SigRowVm> MultiRows { get; } = new();
    [ObservableProperty] private bool _multiRunning;
    [ObservableProperty] private string _multiStatus = "";
    private readonly List<SigRowVm> _activeMultiRows = new();

    // Brute force
    [ObservableProperty] private string _bfStart = "00";
    [ObservableProperty] private string _bfEnd = "3F";
    [ObservableProperty] private string _bfStep = "11";
    [ObservableProperty] private int _bfDelay = 20;
    [ObservableProperty] private int _bfRxTimeout = 30;
    [ObservableProperty] private int _bfDlc = 8;
    [ObservableProperty] private bool _bfV2 = true;
    [ObservableProperty] private bool _bfConstEnabled;
    [ObservableProperty] private string _bfConstId = "3C";
    [ObservableProperty] private string _bfConstData = "FF FF FF FF FF FF FF FF";
    [ObservableProperty] private int _bfConstMs = 10;
    [ObservableProperty] private bool _bfBroadcast;
    [ObservableProperty] private string _bfBroadcastData = "FF FF FF FF FF FF FF FF";
    [ObservableProperty] private bool _bfReplayLoop;
    [ObservableProperty] private bool _bfRunning;
    [ObservableProperty] private int _bfProgress;
    [ObservableProperty] private string _bfStatus = "";
    public ObservableCollection<BruteResultVm> BruteResults { get; } = new();

    private readonly byte[] _sigData = new byte[8];

    public LinViewModel(MainViewModel mainVm)
    {
        _mainVm = mainVm;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SigId))
                UpdatePid();
        };
        UpdatePid();

        // Seed multi-signal rows
        MultiRows.Add(new SigRowVm { Id = "06", Data = "F0 0F 00 00 00 00 00 00", Length = 2, Checksum = "V2", IntervalMs = 100, Modifier = "D0=D0+1" });
        MultiRows.Add(new SigRowVm { Id = "22", Data = "FF FF FF FF FF FF FF FF", Length = 8, Checksum = "V2", IntervalMs = 200 });
    }

    public void SetPort(SerialPort? port)
    {
        lock (_portLock) { _port = port; }
    }

    private void UpdatePid()
    {
        try
        {
            byte id = ParseHexByte(SigId);
            SigPid = LINProtocol.CalcParity((byte)(id & 0x3F)).ToString("X2");
        }
        catch { SigPid = "??"; }
    }

    // Single signal
    [RelayCommand]
    private void SendOnce()
    {
        if (_port?.IsOpen != true) { _mainVm.AddLog("Not connected.", LogLevel.Error); return; }
        DoSend();
    }

    [RelayCommand]
    private void StartSingle()
    {
        if (_port?.IsOpen != true) { _mainVm.AddLog("Not connected.", LogLevel.Error); return; }
        _singleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SigIntervalMs) };
        _singleTimer.Tick += (_, _) => DoSend();
        _singleTimer.Start();
        SigRunning = true;
        _mainVm.AddLog($"Generator running @ {SigIntervalMs} ms", LogLevel.Warn);
    }

    [RelayCommand]
    private void StopSingle()
    {
        _singleTimer?.Stop();
        _singleTimer = null;
        SigRunning = false;
        _mainVm.AddLog("Generator stopped.", LogLevel.Warn);
    }

    private void DoSend()
    {
        byte id = (byte)(ParseHexByte(SigId) & 0x3F);
        _sigData[0] = ParseHexByte(SigD0);
        _sigData[1] = ParseHexByte(SigD1);
        _sigData[2] = ParseHexByte(SigD2);
        _sigData[3] = ParseHexByte(SigD3);
        _sigData[4] = ParseHexByte(SigD4);
        _sigData[5] = ParseHexByte(SigD5);
        _sigData[6] = ParseHexByte(SigD6);
        _sigData[7] = ParseHexByte(SigD7);

        Modifier.Apply(SigModifier, _sigData);

        SigD0 = _sigData[0].ToString("X2");
        SigD1 = _sigData[1].ToString("X2");
        SigD2 = _sigData[2].ToString("X2");
        SigD3 = _sigData[3].ToString("X2");
        SigD4 = _sigData[4].ToString("X2");
        SigD5 = _sigData[5].ToString("X2");
        SigD6 = _sigData[6].ToString("X2");
        SigD7 = _sigData[7].ToString("X2");

        bool enh = SigV2;
        byte[] frame = LINProtocol.HostSend(id, _sigData, SigLen, enh);

        try
        {
            lock (_portLock)
            {
                if (_port?.IsOpen == true)
                {
                    _port.Write(frame, 0, 16);
                    _port.BaseStream.Flush();
                }
            }
            SigCount++;
            string data = string.Join(" ", _sigData.Take(SigLen).Select(b => b.ToString("X2")));
            _mainVm.AddLog($"#{SigCount,-6} TX  ID={id:X2}(PID={LINProtocol.CalcParity(id):X2}) Len={SigLen} [{data}] cs={frame[14]:X2}", LogLevel.TX);
        }
        catch (Exception ex)
        {
            _mainVm.AddLog($"Send error: {ex.Message}", LogLevel.Error);
            StopSingle();
        }
    }

    // Multi-signal
    [RelayCommand]
    private void AddMultiRow()
    {
        MultiRows.Add(new SigRowVm { GridRow = MultiRows.Count });
    }

    [RelayCommand]
    private void RemoveMultiRow(SigRowVm? row)
    {
        if (row != null) MultiRows.Remove(row);
    }

    [RelayCommand]
    private void StartMulti()
    {
        if (_port?.IsOpen != true) { _mainVm.AddLog("Not connected.", LogLevel.Error); return; }

        _activeMultiRows.Clear();
        for (int i = 0; i < MultiRows.Count; i++)
        {
            var row = MultiRows[i];
            row.GridRow = i;
            row.SentCount = 0;
            var bytes = row.Data.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => ParseHexByte(t)).Take(8).ToArray();
            Array.Copy(bytes, row.WorkingData, Math.Min(bytes.Length, 8));
            row.NextMs = Environment.TickCount64;
            _activeMultiRows.Add(row);
        }

        if (_activeMultiRows.Count == 0 || !_activeMultiRows.Any(r => r.Enabled)) { _mainVm.AddLog("No enabled rows.", LogLevel.Warn); return; }

        _multiTimer = new System.Threading.Timer(MultiTick, null, 0, 5);
        MultiRunning = true;
        MultiStatus = $"Running {_activeMultiRows.Count} signal(s)";
        _mainVm.AddLog($"Multi-signal started: {_activeMultiRows.Count} active", LogLevel.Warn);
    }

    [RelayCommand]
    private void StopMulti()
    {
        _multiTimer?.Dispose();
        _multiTimer = null;
        MultiRunning = false;
        MultiStatus = $"Stopped  ({_activeMultiRows.Sum(r => r.SentCount)} total sent)";
        _mainVm.AddLog("Multi-signal stopped.", LogLevel.Warn);
    }

    private void MultiTick(object? _)
    {
        if (_port?.IsOpen != true) return;
        long now = Environment.TickCount64;

        foreach (var sig in _activeMultiRows)
        {
            if (!sig.Enabled) continue;
            if (now < sig.NextMs) continue;
            sig.NextMs = now + sig.IntervalMs;

            var pending = sig.ConsumePending();
            if (pending != null)
                Array.Copy(pending, sig.WorkingData, 8);

            Modifier.Apply(sig.Modifier, sig.WorkingData);

            byte id = (byte)(ParseHexByte(sig.Id) & 0x3F);
            bool enh = sig.Checksum == "V2";
            byte[] frame = LINProtocol.HostSend(id, sig.WorkingData, sig.Length, enh);

            try
            {
                lock (_portLock)
                {
                    if (_port?.IsOpen == true)
                    {
                        _port.Write(frame, 0, 16);
                        _port.BaseStream.Flush();
                    }
                }
                sig.SentCount++;

                Dispatcher.UIThread.Post(() =>
                {
                    sig.UpdateDataFromWorking();
                });
            }
            catch { }
        }
    }

    // Brute force
    [RelayCommand]
    private void StartBrute()
    {
        if (_port?.IsOpen != true) { _mainVm.AddLog("Not connected.", LogLevel.Error); return; }

        byte idStart = (byte)(ParseHexByte(BfStart) & 0x3F);
        byte idEnd = (byte)(ParseHexByte(BfEnd) & 0x3F);
        int step = Math.Max(1, (int)ParseHexByte(BfStep));
        int delay = BfDelay;
        int rxTimeout = BfRxTimeout;
        int dlc = BfDlc;
        bool enh = BfV2;
        bool broadcast = BfBroadcast;
        byte[] bcastBytes = BfBroadcastData.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => ParseHexByte(t)).Take(8).ToArray();
        if (bcastBytes.Length < 8) Array.Resize(ref bcastBytes, 8);

        if (idEnd < idStart) { _mainVm.AddLog("ID end must be ≥ ID start.", LogLevel.Error); return; }

        StopSingle();
        StopMulti();

        BruteResults.Clear();
        BfProgress = 0;
        BfStatus = "Starting…";
        BfRunning = true;

        _bfCts = new CancellationTokenSource();
        var ct = _bfCts.Token;

        // Start constant signal if enabled
        if (BfConstEnabled)
        {
            byte constId = (byte)(ParseHexByte(BfConstId) & 0x3F);
            byte[] constData = BfConstData.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => ParseHexByte(t)).Take(8).ToArray();
            if (constData.Length < 8) Array.Resize(ref constData, 8);
            var constFrame = LINProtocol.HostSend(constId, constData, dlc, enh);
            int constMs = BfConstMs;

            void SendConst()
            {
                if (ct.IsCancellationRequested) return;
                lock (_portLock)
                {
                    if (_port?.IsOpen == true)
                    {
                        _port.Write(constFrame, 0, 16);
                        _port.BaseStream.Flush();
                    }
                }
            }

            SendConst();
            _bfConstTimer = new System.Threading.Timer(_ => SendConst(), null, constMs, constMs);
        }

        int total = broadcast ? (idEnd - idStart + 1) : (idEnd - idStart + 1) * ((0xFF / step) + 1);

        Task.Run(() =>
        {
            int done = 0;
            try
            {
                lock (_portLock) { _port?.DiscardInBuffer(); }

                for (byte id = idStart; id <= idEnd && !ct.IsCancellationRequested; id++)
                {
                    if (broadcast)
                    {
                        var data = bcastBytes.Take(dlc).ToArray();
                        var frame = LINProtocol.HostSend(id, data, dlc, enh);
                        lock (_portLock) { _port?.Write(frame, 0, 16); _port?.BaseStream.Flush(); }
                        if (delay > 0) Thread.Sleep(delay);

                        var (gotResp, respData) = TryReadFrame(rxTimeout);
                        done++;
                        int pct = (int)(done * 100L / total);
                        string pid = LINProtocol.CalcParity(id).ToString("X2");
                        string pay = string.Join(" ", data.Select(b => b.ToString("X2")));
                        string rsp = gotResp ? "YES" : "-";
                        string rsd = gotResp ? string.Join(" ", respData.Skip(6).Take(respData[5] <= 8 ? respData[5] : 0).Select(b => b.ToString("X2"))) : "";

                        Dispatcher.UIThread.Post(() =>
                        {
                            BruteResults.Add(new BruteResultVm(id.ToString("X2"), pid, pay, rsp, rsd));
                            BfProgress = Math.Min(100, pct);
                            BfStatus = $"ID=0x{id:X2}  {done}/{total}";
                        });
                        continue;
                    }

                    for (int byteVal = 0; byteVal <= 0xFF && !ct.IsCancellationRequested; byteVal += step)
                    {
                        byte bv = (byte)byteVal;
                        var data = Enumerable.Repeat(bv, dlc).ToArray();
                        var frame = LINProtocol.HostSend(id, data, dlc, enh);

                        lock (_portLock) { _port?.Write(frame, 0, 16); _port?.BaseStream.Flush(); }
                        if (delay > 0) Thread.Sleep(delay);

                        var (gotResp, respData) = TryReadFrame(rxTimeout);
                        done++;
                        int pct = (int)(done * 100L / total);
                        string pid = LINProtocol.CalcParity(id).ToString("X2");
                        string pay = string.Join(" ", data.Select(b => b.ToString("X2")));
                        string rsp = gotResp ? "YES" : "-";
                        string rsd = gotResp ? string.Join(" ", respData.Skip(6).Take(respData[5] <= 8 ? respData[5] : 0).Select(b => b.ToString("X2"))) : "";

                        Dispatcher.UIThread.Post(() =>
                        {
                            BruteResults.Add(new BruteResultVm(id.ToString("X2"), pid, pay, rsp, rsd));
                            BfProgress = Math.Min(100, pct);
                            BfStatus = $"ID=0x{id:X2}  Byte=0x{bv:X2}  {done}/{total}";
                        });
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Dispatcher.UIThread.Post(() => _mainVm.AddLog($"Brute error: {ex.Message}", LogLevel.Error)); }
            finally
            {
                _bfConstTimer?.Dispose();
                _bfConstTimer = null;
                Dispatcher.UIThread.Post(() =>
                {
                    BfProgress = ct.IsCancellationRequested ? BfProgress : 100;
                    BfStatus = ct.IsCancellationRequested ? $"Stopped at {done}/{total}" : $"Done — {done} frames sent";
                    BfRunning = false;
                });
            }
        }, ct);
    }

    [RelayCommand]
    private void StopBrute()
    {
        _bfCts?.Cancel();
        _bfConstTimer?.Dispose();
        _bfConstTimer = null;
        _bfReplayTimer?.Dispose();
        _bfReplayTimer = null;
        BfRunning = false;
    }

    [RelayCommand]
    private async Task ExportBrute()
    {
        // CSV export — placeholder for now
        _mainVm.AddLog($"Export: {BruteResults.Count} rows", LogLevel.Info);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void ReplayBrute(BruteResultVm? result)
    {
        if (_port?.IsOpen != true) { _mainVm.AddLog("Not connected.", LogLevel.Error); return; }
        if (result == null) return;

        byte id = (byte)(ParseHexByte(result.Id) & 0x3F);
        byte[] data = result.Payload.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => ParseHexByte(t)).Take(8).ToArray();
        if (data.Length < 8) Array.Resize(ref data, 8);
        bool enh = BfV2;
        var frame = LINProtocol.HostSend(id, data, data.Length, enh);

        lock (_portLock) { _port?.Write(frame, 0, 16); _port?.BaseStream.Flush(); }
        _mainVm.AddLog($"Replayed ID=0x{id:X2}  {result.Payload}", LogLevel.Info);
    }

    private (bool ok, byte[] frame) TryReadFrame(int timeoutMs)
    {
        var buf = new byte[16];
        int read = 0;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (read < 16 && DateTime.UtcNow < deadline)
        {
            int avail;
            lock (_portLock)
            {
                if (_port?.IsOpen != true) return (false, buf);
                avail = _port.BytesToRead;
                if (avail > 0)
                {
                    int n = _port.Read(buf, read, Math.Min(avail, 16 - read));
                    read += n;
                }
            }
            if (read < 16) Thread.Sleep(1);
        }

        if (read < 16) return (false, buf);
        byte cmd = buf[0];
        if (cmd != 0x33 && cmd != 0x44 && cmd != 0x55 && cmd != 0xDD) return (false, buf);
        int sum = 0;
        for (int i = 0; i < 15; i++) sum += buf[i];
        byte cs = (byte)((~sum & 0xFF) + 1);
        return (buf[15] == cs, buf);
    }

    private static byte ParseHexByte(string? s, byte fallback = 0)
    {
        if (s == null) return fallback;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return s.Length > 0 && s.All(c => "0123456789ABCDEFabcdef".Contains(c))
            ? Convert.ToByte(s.Length > 2 ? s[^2..] : s, 16)
            : fallback;
    }

    public void Dispose()
    {
        _singleTimer?.Stop();
        _multiTimer?.Dispose();
        _bfConstTimer?.Dispose();
        _bfReplayTimer?.Dispose();
        _bfCts?.Cancel();
    }
}
