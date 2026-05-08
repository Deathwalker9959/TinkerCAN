using System;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LINGui.ViewModels;

public partial class SigRowVm : ObservableObject
{
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _id = "06";
    [ObservableProperty] private string _data = "FF FF FF FF FF FF FF FF";
    [ObservableProperty] private int _length = 8;
    [ObservableProperty] private string _checksum = "V2";
    [ObservableProperty] private int _intervalMs = 100;
    [ObservableProperty] private string _modifier = "";
    [ObservableProperty] private long _sentCount;

    public int GridRow { get; set; }
    public byte[] WorkingData { get; } = new byte[8];
    public long NextMs { get; set; }
    public volatile byte[]? PendingData;

    private bool _suppressPending;

    public byte[]? ConsumePending()
    {
        if (_suppressPending) return null;
        return Interlocked.Exchange(ref PendingData, null);
    }

    public void UpdateDataFromWorking()
    {
        _suppressPending = true;
        Data = string.Join(" ", WorkingData.AsSpan(0, Math.Min(Length, 8)).ToArray().Select(b => b.ToString("X2")));
        _suppressPending = false;
    }

    partial void OnDataChanged(string value)
    {
        if (_suppressPending) return;
        var bytes = value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => { byte.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out byte b); return b; })
            .Take(8).ToArray();
        PendingData = bytes;
    }
}
