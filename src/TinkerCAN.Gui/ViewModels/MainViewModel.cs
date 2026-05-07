using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LINGui.Models;
using LINGui.Services;

namespace LINGui.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "TinkerCAN — LIN / CAN Signal Generator";
    [ObservableProperty] private string _selectedPort = "";
    [ObservableProperty] private string _linBaud = "19200";
    [ObservableProperty] private string _statusText = "Disconnected";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private string _logText = "";

    public ObservableCollection<string> Ports { get; } = new();

    // Tab VMs
    public LinViewModel LinViewModel { get; }
    public CanViewModel CanViewModel { get; }

    private SerialPort? _linPort;
    private readonly object _linPortLock = new();

    public MainViewModel()
    {
        LinViewModel = new LinViewModel(this);
        CanViewModel = new CanViewModel(this);
        RefreshPorts();
        AddLog("Ready.", LogLevel.Info);
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        Ports.Clear();
        var ports = PortScanner.List();
        foreach (var p in ports)
            Ports.Add(p);

        if (Ports.Count > 0 && string.IsNullOrEmpty(SelectedPort))
            SelectedPort = Ports[0];

        AddLog($"Found {Ports.Count} port(s).", LogLevel.Info);
    }

    [RelayCommand]
    private async Task Connect()
    {
        if (string.IsNullOrWhiteSpace(SelectedPort))
        {
            AddLog("No port selected.", LogLevel.Error);
            return;
        }

        if (!int.TryParse(LinBaud, out int baud) || baud < 4800 || baud > 20000)
        {
            AddLog("LIN baud must be 4800–20000.", LogLevel.Error);
            return;
        }

        AddLog($"--- Connecting {SelectedPort} @ USB=460800 / LIN={baud} ---", LogLevel.Warn);

        await Task.Run(() =>
        {
            SerialPort? p = null;
            try
            {
                // DTR probe
                try
                {
                    using var probe = new SerialPort(SelectedPort, 460800, Parity.None, 8, StopBits.One);
                    probe.DtrEnable = true;
                    probe.Open();
                    System.Threading.Thread.Sleep(50);
                }
                catch { }

                p = new SerialPort(SelectedPort, 460800, Parity.None, 8, StopBits.One);
                p.Encoding = System.Text.Encoding.Default;
                p.DtrEnable = true;
                p.ReadTimeout = 500;
                p.ReceivedBytesThreshold = 16;
                p.Open();
                p.DiscardInBuffer();
                AddLog($"  Opened  IsOpen={p.IsOpen}", LogLevel.Info);

                // Arm dongle: mode=0 → 100ms → mode=1 → 100ms
                var m0 = TinkerCAN.Lin.LINProtocol.ModeCommand(0, baud, 28, 100);
                lock (_linPortLock) { p.Write(m0, 0, 16); p.BaseStream.Flush(); }
                AddLog($"  TX mode=0", LogLevel.Info);
                System.Threading.Thread.Sleep(100);

                var m1 = TinkerCAN.Lin.LINProtocol.ModeCommand(1, baud, 28, 100);
                lock (_linPortLock) { p.Write(m1, 0, 16); p.BaseStream.Flush(); }
                AddLog($"  TX mode=1", LogLevel.Info);
                System.Threading.Thread.Sleep(100);

                lock (_linPortLock) { _linPort = p; }
                LinViewModel.SetPort(p);

                IsConnected = true;
                StatusText = $"Connected: {SelectedPort}";
                AddLog($"Ready — {SelectedPort}  USB=460800  LIN={baud}", LogLevel.Warn);
            }
            catch (Exception ex)
            {
                p?.Dispose();
                AddLog($"CONNECT FAILED: {ex.Message}", LogLevel.Error);
            }
        });
    }

    [RelayCommand]
    private void Disconnect()
    {
        LinViewModel.SetPort(null);
        lock (_linPortLock) { _linPort?.Dispose(); _linPort = null; }
        IsConnected = false;
        StatusText = "Disconnected";
        AddLog("Disconnected.", LogLevel.Warn);
    }

    [RelayCommand]
    private void NewConfig()
    {
        // Reset to defaults — full implementation in step 3/4 when LIN/CAN VMs exist
        LinBaud = "19200";
        AddLog("Config reset to defaults.", LogLevel.Info);
    }

    [RelayCommand]
    private async Task OpenConfig(Window? window)
    {
        if (window == null) return;

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Config",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } }
        });

        if (files.Count == 0) return;

        try
        {
            var path = files[0].Path.LocalPath;
            var json = await File.ReadAllTextAsync(path);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json);
            if (cfg == null) throw new Exception("Invalid config file.");

            LinBaud = cfg.LinBaud.ToString();
            // Load into LIN/CAN VMs — step 3/4
            AddLog($"Config loaded: {Path.GetFileName(path)}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            AddLog($"Load failed: {ex.Message}", LogLevel.Error);
        }
    }

    [RelayCommand]
    private async Task SaveConfig(Window? window)
    {
        if (window == null) return;

        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Config",
            DefaultExtension = "json",
            SuggestedFileName = "tinkercan-config.json",
            FileTypeChoices = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } }
        });

        if (file == null) return;

        try
        {
            var cfg = new AppConfig
            {
                LinBaud = int.TryParse(LinBaud, out int b) ? b : 19200,
                // Serialize from LIN/CAN VMs — step 3/4
            };

            var opts = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(cfg, opts);
            await File.WriteAllTextAsync(file.Path.LocalPath, json);
            AddLog($"Config saved: {Path.GetFileName(file.Path.LocalPath)}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            AddLog($"Save failed: {ex.Message}", LogLevel.Error);
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogText = "";
    }

    public void AddLog(string message, LogLevel level)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            string timestamp = System.DateTime.Now.ToString("HH:mm:ss.fff");
            string line = $"[{timestamp}] {message}\n";
            LogText += line;

            // Trim to last 5000 lines
            var lines = LogText.Split('\n');
            if (lines.Length > 5000)
                LogText = string.Join('\n', lines.Skip(lines.Length - 5000));
        });
    }
}
