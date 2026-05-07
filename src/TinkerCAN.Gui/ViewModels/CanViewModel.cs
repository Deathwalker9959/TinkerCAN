using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TinkerCAN.Can;
using TinkerCAN.Lin;

namespace LINGui.ViewModels;

public partial class CanViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _mainVm;
    private SerialPort? _canPort;
    private readonly object _canLock = new();
    private Thread? _canRxThread;
    private volatile bool _canRxRun;
    private System.Threading.Timer? _canTxTimer;
    private System.Threading.Timer? _canMultiTimer;
    private System.Threading.Timer? _canBfConstTimer;
    private System.Threading.Timer? _canBfReplayTimer;
    private CancellationTokenSource? _canBfCts;
    private volatile bool _canBfActive;
    private readonly ConcurrentQueue<SLCANProtocol.RxFrame> _canBfRxQ = new();

    // Connection
    [ObservableProperty] private string _selectedCanPort = "";
    [ObservableProperty] private bool _isCanConnected;
    [ObservableProperty] private string _canStatusText = "Disconnected";
    [ObservableProperty] private int _nomRateIdx = 9;  // 125k
    [ObservableProperty] private int _fdRateIdx = 1;   // 2M
    [ObservableProperty] private bool _canSilent;
    [ObservableProperty] private bool _canAutoRetx;

    public string[] NomRateLabels => SLCANProtocol.NomRateLabels;
    public string[] FdRateLabels => SLCANProtocol.FdRateLabels;

    // Signal TX
    [ObservableProperty] private int _sigFrameTypeIdx = 0;
    [ObservableProperty] private string _sigId = "123";
    [ObservableProperty] private int _sigDlcIdx = 8;
    [ObservableProperty] private string _sigData = "DE AD BE EF 00 00 00 00";
    [ObservableProperty] private int _sigIntervalMs = 100;
    [ObservableProperty] private string _sigModifier = "";
    [ObservableProperty] private bool _sigRunning;

    public string[] FrameTypeLabels => new[]
    {
        "t  Standard Data (11-bit)",  "T  Extended Data (29-bit)",
        "r  Standard Remote (11-bit)", "R  Extended Remote (29-bit)",
        "d  CANFD Standard (no BRS)",  "D  CANFD Extended (no BRS)",
        "b  CANFD Standard + BRS",     "B  CANFD Extended + BRS",
    };

    public ObservableCollection<string> DlcOptions { get; } = new();

    // Multi-signal
    public ObservableCollection<CanSigRowVm> MultiRows { get; } = new();
    [ObservableProperty] private bool _multiRunning;
    [ObservableProperty] private string _multiStatus = "";
    private readonly List<CanSigRowVm> _activeMultiRows = new();

    // Brute force
    [ObservableProperty] private string _bfStart = "000";
    [ObservableProperty] private string _bfEnd = "7FF";
    [ObservableProperty] private string _bfStep = "01";
    [ObservableProperty] private int _bfDelay = 5;
    [ObservableProperty] private int _bfRxTimeout = 20;
    [ObservableProperty] private int _bfTypeIdx = 0;
    [ObservableProperty] private int _bfDlc = 8;
    [ObservableProperty] private string _bfData = "00 00 00 00 00 00 00 00";
    [ObservableProperty] private bool _bfConstEnabled;
    [ObservableProperty] private string _bfConstId = "123";
    [ObservableProperty] private string _bfConstData = "00 00 00 00 00 00 00 00";
    [ObservableProperty] private int _bfConstMs = 10;
    [ObservableProperty] private bool _bfReplayLoop;
    [ObservableProperty] private bool _bfRunning;
    [ObservableProperty] private int _bfProgress;
    [ObservableProperty] private string _bfStatus = "";

    public string[] BfTypeLabels => new[]
    {
        "t  Standard (11-bit)", "T  Extended (29-bit)",
        "d  CANFD Std (no BRS)", "D  CANFD Ext (no BRS)",
        "b  CANFD Std + BRS",    "B  CANFD Ext + BRS",
    };

    public ObservableCollection<CanBruteResultVm> BruteResults { get; } = new();

    // Log
    public ObservableCollection<CanLogEntryVm> LogEntries { get; } = new();
    [ObservableProperty] private bool _groupById;
    [ObservableProperty] private long _rxCount;
    [ObservableProperty] private long _txCount;
    private readonly Dictionary<string, CanLogEntryVm> _canIdRowMap = new();

    private readonly byte[] _canTxData = new byte[64];

    public CanViewModel(MainViewModel mainVm)
    {
        _mainVm = mainVm;
        UpdateDlcOptions();

        // Seed multi rows
        MultiRows.Add(new CanSigRowVm { Type = "t", Id = "123", Dlc = "8", Data = "DE AD BE EF 00 00 00 00", IntervalMs = 100, Modifier = "D0=D0+1" });
        MultiRows.Add(new CanSigRowVm { Type = "T", Id = "1FFFFFFF", Dlc = "8", Data = "FF FF FF FF FF FF FF FF", IntervalMs = 200 });

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SigFrameTypeIdx))
                UpdateDlcOptions();
        };
    }

    private void UpdateDlcOptions()
    {
        bool fd = SigFrameTypeIdx >= 4;
        DlcOptions.Clear();
        if (fd)
        {
            foreach (var s in new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "12", "16", "20", "24", "32", "48", "64" })
                DlcOptions.Add(s);
        }
        else
        {
            for (int i = 0; i <= 8; i++)
                DlcOptions.Add(i.ToString());
        }
        SigDlcIdx = Math.Min(DlcOptions.Count - 1, 8);
    }

    // Connection
    [RelayCommand]
    private void ConnectCan()
    {
        if (string.IsNullOrWhiteSpace(SelectedCanPort)) { _mainVm.AddLog("Select CAN port.", LogLevel.Error); return; }

        try
        {
            var p = new SerialPort(SelectedCanPort, 115200, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 100,
                WriteTimeout = 500
            };
            p.Open();

            lock (_canLock) { _canPort = p; }
            IsCanConnected = true;
            CanStatusText = $"● {SelectedCanPort}";

            _canRxRun = true;
            _canRxThread = new Thread(CanRxLoop) { IsBackground = true, Name = "CanRx" };
            _canRxThread.Start();

            _mainVm.AddLog($"CAN port {SelectedCanPort} connected.", LogLevel.Info);
            OpenCan();
        }
        catch (Exception ex)
        {
            _mainVm.AddLog($"CAN connect: {ex.Message}", LogLevel.Error);
        }
    }

    [RelayCommand]
    private void DisconnectCan()
    {
        StopSigLoop();
        StopMulti();
        StopBrute();

        try { SendRaw(SLCANProtocol.Close()); Thread.Sleep(40); } catch { }
        _canRxRun = false;
        lock (_canLock) { _canPort?.Close(); _canPort = null; }

        IsCanConnected = false;
        CanStatusText = "Disconnected";
        _mainVm.AddLog("CAN port disconnected.", LogLevel.Warn);
    }

    [RelayCommand]
    private void OpenCan()
    {
        Task.Run(() =>
        {
            SendRaw(SLCANProtocol.SetMode(CanSilent)); Thread.Sleep(30);
            SendRaw(SLCANProtocol.SetAutoRetransmit(CanAutoRetx)); Thread.Sleep(30);
            SendRaw(SLCANProtocol.SetNomRate(NomRateIdx)); Thread.Sleep(30);
            SendRaw(SLCANProtocol.SetFdRate(FdRateIdx)); Thread.Sleep(30);
            SendRaw(SLCANProtocol.Open());
            Dispatcher.UIThread.Post(() =>
                _mainVm.AddLog($"CAN opened — Nom:{NomRateLabels[NomRateIdx]}  FD:{FdRateLabels[FdRateIdx]}", LogLevel.Warn));
        });
    }

    [RelayCommand]
    private void CloseCan()
    {
        StopSigLoop();
        StopMulti();
        StopBrute();
        SendRaw(SLCANProtocol.Close());
        _mainVm.AddLog("CAN channel closed.", LogLevel.Warn);
    }

    [RelayCommand]
    private void GetVersion()
    {
        SendRaw(SLCANProtocol.GetVersion());
    }

    private void SendRaw(byte[] cmd)
    {
        lock (_canLock) { if (_canPort?.IsOpen == true) _canPort.Write(cmd, 0, cmd.Length); }
    }

    // Signal TX
    [RelayCommand]
    private void SendSigOnce()
    {
        if (_canPort?.IsOpen != true) { _mainVm.AddLog("CAN not connected.", LogLevel.Error); return; }
        var frame = BuildSigFrame();
        if (frame == null) return;

        lock (_canLock) { _canPort.Write(frame, 0, frame.Length); }
        TxCount++;

        bool ext = SigFrameTypeIdx == 1 || SigFrameTypeIdx == 3 || SigFrameTypeIdx == 5 || SigFrameTypeIdx == 7;
        bool fd = SigFrameTypeIdx >= 4;
        string ftype = GetDisplayType(SigFrameTypeIdx);
        int id = ParseHexInt(SigId);
        string idStr = FormatId(id, ext);
        AddCanLog("TX", ftype, idStr, DlcOptions[SigDlcIdx], SigData);
    }

    [RelayCommand]
    private void StartSigLoop()
    {
        if (_canPort?.IsOpen != true) { _mainVm.AddLog("CAN not connected.", LogLevel.Error); return; }
        var frame = BuildSigFrame();
        if (frame == null) return;

        var tokens = SigData.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < Math.Min(tokens.Length, 64); i++)
            byte.TryParse(tokens[i], System.Globalization.NumberStyles.HexNumber, null, out _canTxData[i]);

        string modifier = SigModifier.Trim();
        bool ext = SigFrameTypeIdx == 1 || SigFrameTypeIdx == 3 || SigFrameTypeIdx == 5 || SigFrameTypeIdx == 7;
        bool fd = SigFrameTypeIdx >= 4;
        bool remote = SigFrameTypeIdx == 2 || SigFrameTypeIdx == 3;
        bool brs = SigFrameTypeIdx == 6 || SigFrameTypeIdx == 7;
        int dlcVal = int.TryParse(DlcOptions[SigDlcIdx], out int dv) ? dv : 8;
        string ftype = GetDisplayType(SigFrameTypeIdx);

        SigRunning = true;
        int ms = SigIntervalMs;
        _mainVm.AddLog($"CAN TX loop started every {ms}ms", LogLevel.Warn);

        _canTxTimer = new System.Threading.Timer(_ =>
        {
            if (modifier.Length > 0) Modifier.Apply(modifier, _canTxData);
            int id = ParseHexInt(SigId);

            byte[] f;
            if (remote && !ext) f = SLCANProtocol.RemoteStd(id, dlcVal);
            else if (remote && ext) f = SLCANProtocol.RemoteExt(id, dlcVal);
            else if (fd && !ext) f = SLCANProtocol.SendFdStd(id, _canTxData, dlcVal, brs);
            else if (fd && ext) f = SLCANProtocol.SendFdExt(id, _canTxData, dlcVal, brs);
            else if (!ext) f = SLCANProtocol.SendStd(id, _canTxData, Math.Min(dlcVal, 8));
            else f = SLCANProtocol.SendExt(id, _canTxData, Math.Min(dlcVal, 8));

            lock (_canLock) { if (_canPort?.IsOpen == true) { _canPort.Write(f, 0, f.Length); TxCount++; } }
            AddCanLog("TX", ftype, FormatId(id, ext), dlcVal.ToString(), FormatData(_canTxData, fd, dlcVal));
        }, null, 0, ms);
    }

    [RelayCommand]
    private void StopSigLoop()
    {
        _canTxTimer?.Dispose();
        _canTxTimer = null;
        SigRunning = false;
    }

    private byte[]? BuildSigFrame()
    {
        int id = ParseHexInt(SigId);
        int typeIdx = SigFrameTypeIdx;
        bool remote = typeIdx == 2 || typeIdx == 3;
        bool fd = typeIdx >= 4;
        bool brs = typeIdx == 6 || typeIdx == 7;
        bool ext = typeIdx == 1 || typeIdx == 3 || typeIdx == 5 || typeIdx == 7;

        int dlcOrLen = int.TryParse(DlcOptions[SigDlcIdx], out int dv) ? dv : 8;
        if (remote) return ext ? SLCANProtocol.RemoteExt(id, dlcOrLen) : SLCANProtocol.RemoteStd(id, dlcOrLen);

        var tokens = SigData.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var data = new byte[64];
        for (int i = 0; i < Math.Min(tokens.Length, 64); i++)
            byte.TryParse(tokens[i], System.Globalization.NumberStyles.HexNumber, null, out data[i]);
        Array.Copy(data, _canTxData, 64);

        if (fd) return ext ? SLCANProtocol.SendFdExt(id, data, dlcOrLen, brs) : SLCANProtocol.SendFdStd(id, data, dlcOrLen, brs);
        int dlc = Math.Min(dlcOrLen, 8);
        return ext ? SLCANProtocol.SendExt(id, data, dlc) : SLCANProtocol.SendStd(id, data, dlc);
    }

    // Multi-signal
    [RelayCommand]
    private void AddMultiRow()
    {
        MultiRows.Add(new CanSigRowVm { GridRow = MultiRows.Count });
    }

    [RelayCommand]
    private void RemoveMultiRow(CanSigRowVm? row)
    {
        if (row != null) MultiRows.Remove(row);
    }

    [RelayCommand]
    private void StartMulti()
    {
        if (_canPort?.IsOpen != true) { _mainVm.AddLog("CAN not connected.", LogLevel.Error); return; }

        _activeMultiRows.Clear();
        for (int i = 0; i < MultiRows.Count; i++)
        {
            var row = MultiRows[i];
            if (!row.Enabled) continue;

            row.GridRow = i;
            row.SentCount = 0;
            var bytes = row.Data.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => { byte.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out byte b); return b; })
                .Take(64).ToArray();
            Array.Copy(bytes, row.WorkingData, Math.Min(bytes.Length, 64));
            row.NextMs = Environment.TickCount64;
            _activeMultiRows.Add(row);
        }

        if (_activeMultiRows.Count == 0) { _mainVm.AddLog("No enabled rows.", LogLevel.Warn); return; }

        _canMultiTimer = new System.Threading.Timer(CanMultiTick, null, 0, 5);
        MultiRunning = true;
        MultiStatus = $"Running {_activeMultiRows.Count} signal(s)";
        _mainVm.AddLog($"CAN multi-signal started: {_activeMultiRows.Count} active", LogLevel.Warn);
    }

    [RelayCommand]
    private void StopMulti()
    {
        _canMultiTimer?.Dispose();
        _canMultiTimer = null;
        MultiRunning = false;
        MultiStatus = $"Stopped  ({_activeMultiRows.Sum(r => r.SentCount)} total sent)";
        _mainVm.AddLog("CAN multi-signal stopped.", LogLevel.Warn);
    }

    private void CanMultiTick(object? _)
    {
        long now = Environment.TickCount64;
        foreach (var sig in _activeMultiRows)
        {
            if (now < sig.NextMs) continue;
            sig.NextMs = now + sig.IntervalMs;

            var pending = sig.ConsumePending();
            if (pending != null)
                Array.Copy(pending, sig.WorkingData, Math.Min(pending.Length, 64));

            if (sig.Modifier.Length > 0) Modifier.Apply(sig.Modifier, sig.WorkingData);

            int typeIdx = sig.Type switch { "T" => 1, "r" => 2, "R" => 3, "d" => 4, "D" => 5, "b" => 6, "B" => 7, _ => 0 };
            int id = ParseHexInt(sig.Id);
            int dlc = int.TryParse(sig.Dlc, out int d) ? d : 8;
            var frame = BuildCanFrame(typeIdx, id, sig.WorkingData, dlc);

            lock (_canLock) { if (_canPort?.IsOpen == true) { _canPort.Write(frame, 0, frame.Length); TxCount++; sig.SentCount++; } }

            bool ext = typeIdx == 1 || typeIdx == 3 || typeIdx == 5 || typeIdx == 7;
            bool fd = typeIdx >= 4;
            AddCanLog("TX", GetDisplayType(typeIdx), FormatId(id, ext), dlc.ToString(), FormatData(sig.WorkingData, fd, dlc));

            int gridRow = sig.GridRow;
            long cnt = sig.SentCount;
            string liveData = FormatData(sig.WorkingData, fd, dlc);
            Dispatcher.UIThread.Post(() =>
            {
                if (gridRow < MultiRows.Count)
                {
                    sig.UpdateDataFromWorking(fd ? SLCANProtocol.FdDlcToBytes(SLCANProtocol.BytesToFdDlc(dlc)) : Math.Min(dlc, 8));
                }
            });
        }
    }

    // Brute force
    [RelayCommand]
    private void StartBrute()
    {
        if (_canPort?.IsOpen != true) { _mainVm.AddLog("CAN not connected.", LogLevel.Error); return; }

        int idStart = ParseHexInt(BfStart);
        int idEnd = ParseHexInt(BfEnd);
        int step = Math.Max(1, ParseHexInt(BfStep));
        int delay = BfDelay;
        int rxTo = BfRxTimeout;
        int dlc = BfDlc;
        int typeIdx = BfTypeIdx;
        bool ext = typeIdx == 1 || typeIdx == 3 || typeIdx == 5;
        bool useInc = BfData.Trim().Equals("INC", StringComparison.OrdinalIgnoreCase);
        var dataToks = BfData.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var staticData = new byte[64];
        if (!useInc) { for (int i = 0; i < Math.Min(dataToks.Length, 64); i++) byte.TryParse(dataToks[i], System.Globalization.NumberStyles.HexNumber, null, out staticData[i]); }
        bool sweepPayload = useInc || (dataToks.Length > 0 && dataToks.All(t => string.Equals(t, dataToks[0], StringComparison.OrdinalIgnoreCase)));
        int sweepStart = sweepPayload && !useInc && dataToks.Length > 0 ? staticData[0] : 0;

        if (idEnd < idStart) { _mainVm.AddLog("CAN brute-force end must be >= start.", LogLevel.Error); return; }

        StopSigLoop();
        StopMulti();

        BruteResults.Clear();
        BfProgress = 0;
        BfStatus = "Starting…";
        BfRunning = true;

        _canBfCts = new CancellationTokenSource();
        var ct = _canBfCts.Token;

        int idCount = idEnd - idStart + 1;
        int valueCount = 0; for (int v = sweepStart; v <= 0xFF; v += step) valueCount++;
        int total = sweepPayload ? idCount * valueCount : idCount;

        if (BfConstEnabled)
        {
            int constId = ParseHexInt(BfConstId);
            var constData = new byte[64];
            var constToks = BfConstData.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < Math.Min(constToks.Length, 64); i++)
                byte.TryParse(constToks[i], System.Globalization.NumberStyles.HexNumber, null, out constData[i]);
            var constFrame = BuildCanFrame(typeIdx, constId, constData, dlc);
            int constMs = BfConstMs;

            void SendCanConst()
            {
                if (ct.IsCancellationRequested) return;
                lock (_canLock) { if (_canPort?.IsOpen == true) { _canPort.Write(constFrame, 0, constFrame.Length); TxCount++; } }
            }

            SendCanConst();
            _canBfConstTimer = new System.Threading.Timer(_ => SendCanConst(), null, constMs, constMs);
        }

        Task.Run(() =>
        {
            int done = 0;
            _canBfActive = true;
            lock (_canLock) { _canPort?.DiscardInBuffer(); }
            while (_canBfRxQ.TryDequeue(out _)) { }

            try
            {
                for (int id2 = idStart; id2 <= idEnd && !ct.IsCancellationRequested; id2++)
                {
                    int valueEnd = sweepPayload ? 0xFF : 0;
                    int valueStep = sweepPayload ? step : 1;
                    for (int byteVal = sweepStart; byteVal <= valueEnd && !ct.IsCancellationRequested; byteVal += valueStep)
                    {
                        if (!sweepPayload && byteVal > 0) break;

                        var data2 = sweepPayload ? Enumerable.Repeat((byte)byteVal, dlc).ToArray() : staticData.Take(dlc).ToArray();
                        byte[] f = BuildCanFrame(typeIdx, id2, data2, dlc);

                        lock (_canLock) { _canPort?.Write(f, 0, f.Length); TxCount++; }
                        if (delay > 0) Thread.Sleep(delay);

                        bool gotResp = false;
                        string respStr = "";
                        var deadline = DateTime.UtcNow.AddMilliseconds(rxTo);
                        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                        {
                            if (_canBfRxQ.TryDequeue(out var rf))
                            {
                                gotResp = true;
                                respStr = string.Join(" ", rf.Data.Take(rf.ByteLen).Select(b => b.ToString("X2")));
                                break;
                            }
                            Thread.Sleep(1);
                        }

                        done++;
                        int pct = (int)(done * 100L / total);
                        string ids = ext ? id2.ToString("X8") : id2.ToString("X3");
                        string type = GetBruteTypeCode(typeIdx);
                        string pay = string.Join(" ", data2.Select(b => b.ToString("X2")));

                        Dispatcher.UIThread.Post(() =>
                        {
                            BruteResults.Add(new CanBruteResultVm(type, ids, dlc.ToString(), pay, gotResp ? "YES" : "-", respStr));
                            BfProgress = Math.Min(100, pct);
                            BfStatus = sweepPayload ? $"ID=0x{ids}  Byte=0x{byteVal:X2}  {done}/{total}" : $"ID=0x{ids}  {done}/{total}";
                        });
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Dispatcher.UIThread.Post(() => _mainVm.AddLog($"CAN BF error: {ex.Message}", LogLevel.Error)); }
            finally
            {
                _canBfActive = false;
                _canBfConstTimer?.Dispose();
                _canBfConstTimer = null;
                Dispatcher.UIThread.Post(() =>
                {
                    BfProgress = ct.IsCancellationRequested ? BfProgress : 100;
                    BfStatus = ct.IsCancellationRequested ? $"Stopped {done}/{total}" : $"Done — {done} frames";
                    BfRunning = false;
                });
            }
        }, ct);
    }

    [RelayCommand]
    private void StopBrute()
    {
        _canBfCts?.Cancel();
        _canBfConstTimer?.Dispose();
        _canBfConstTimer = null;
        _canBfReplayTimer?.Dispose();
        _canBfReplayTimer = null;
        BfRunning = false;
    }

    [RelayCommand]
    private async Task ExportBrute()
    {
        _mainVm.AddLog($"Export: {BruteResults.Count} rows", LogLevel.Info);
        await Task.CompletedTask;
    }

    // Log
    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
        _canIdRowMap.Clear();
        RxCount = 0;
        TxCount = 0;
    }

    private void AddCanLog(string dir, string type, string id, string dlc, string data)
    {
        Dispatcher.UIThread.Post(() =>
        {
            long seq = RxCount + TxCount;

            if (GroupById)
            {
                string key = $"{dir}:{id}";
                if (_canIdRowMap.TryGetValue(key, out var existing))
                {
                    existing.Update(seq, data);
                    return;
                }

                var newEntry = new CanLogEntryVm(seq, dir, type, id, dlc, data);
                LogEntries.Add(newEntry);
                _canIdRowMap[key] = newEntry;
            }
            else
            {
                LogEntries.Add(new CanLogEntryVm(seq, dir, type, id, dlc, data));
                if (LogEntries.Count > 5000)
                    LogEntries.RemoveAt(0);
            }
        });
    }

    // RX thread
    private void CanRxLoop()
    {
        var sb = new StringBuilder(128);
        while (_canRxRun)
        {
            int b;
            try
            {
                SerialPort? p;
                lock (_canLock) { p = _canPort; }
                if (p == null || !p.IsOpen) { Thread.Sleep(20); continue; }
                b = p.ReadByte();
            }
            catch (TimeoutException) { continue; }
            catch { Thread.Sleep(20); continue; }

            if (b == 0x0D)
            {
                string line = sb.ToString();
                sb.Clear();
                if (line.Length == 0) continue;

                var frame = SLCANProtocol.ParseFrame(line);
                if (frame != null)
                {
                    RxCount++;
                    if (_canBfActive) { _canBfRxQ.Enqueue(frame); continue; }

                    string idStr = frame.Extended ? frame.Id.ToString("X8") : frame.Id.ToString("X3");
                    string dStr = string.Join(" ", frame.Data.Take(frame.ByteLen).Select(x => x.ToString("X2")));
                    AddCanLog("RX", frame.Type, idStr, frame.Dlc.ToString("X"), dStr);
                }
                else if (line.StartsWith("V") || line.StartsWith("E"))
                {
                    Dispatcher.UIThread.Post(() => _mainVm.AddLog($"CAN device: {line}", LogLevel.Info));
                }
            }
            else if (b == 0x07)
            {
                Dispatcher.UIThread.Post(() => _mainVm.AddLog("CAN TX NACK.", LogLevel.Error));
            }
            else
            {
                sb.Append((char)b);
                if (sb.Length > 512) sb.Clear();
            }
        }
    }

    // Helpers
    private static byte[] BuildCanFrame(int typeIdx, int id, byte[] data, int dlc)
    {
        bool fd = typeIdx >= 2;
        bool ext = typeIdx == 1 || typeIdx == 3 || typeIdx == 5;
        bool brs = typeIdx == 4 || typeIdx == 5;
        if (fd) return ext ? SLCANProtocol.SendFdExt(id, data, dlc, brs) : SLCANProtocol.SendFdStd(id, data, dlc, brs);
        return ext ? SLCANProtocol.SendExt(id, data, Math.Min(dlc, 8)) : SLCANProtocol.SendStd(id, data, Math.Min(dlc, 8));
    }

    private static string GetDisplayType(int typeIdx) => typeIdx switch
    {
        2 or 3 => "Remote",
        4 or 5 => "FD",
        6 or 7 => "FD+BRS",
        _ => "Data",
    };

    private static string GetBruteTypeCode(int typeIdx) => typeIdx switch
    {
        1 => "T",
        2 => "d",
        3 => "D",
        4 => "b",
        5 => "B",
        _ => "t",
    };

    private static string FormatId(int id, bool ext) => ext ? id.ToString("X8") : id.ToString("X3");

    private static string FormatData(byte[] data, bool fd, int dlcOrLen)
    {
        int byteLen = fd ? SLCANProtocol.FdDlcToBytes(SLCANProtocol.BytesToFdDlc(dlcOrLen)) : Math.Min(dlcOrLen, 8);
        return string.Join(" ", data.Take(byteLen).Select(b => b.ToString("X2")));
    }

    private static int ParseHexInt(string? s, int fallback = 0)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int value) ? value : fallback;
    }

    public void Dispose()
    {
        _canTxTimer?.Dispose();
        _canMultiTimer?.Dispose();
        _canBfConstTimer?.Dispose();
        _canBfReplayTimer?.Dispose();
        _canBfCts?.Cancel();
        _canRxRun = false;
        _canPort?.Dispose();
    }
}
