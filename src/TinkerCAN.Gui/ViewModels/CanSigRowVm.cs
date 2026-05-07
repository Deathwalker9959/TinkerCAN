using System;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LINGui.ViewModels;

public partial class CanSigRowVm : ObservableObject
{
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _type = "t";
    [ObservableProperty] private string _id = "123";
    [ObservableProperty] private string _dlc = "8";
    [ObservableProperty] private string _data = "DE AD BE EF 00 00 00 00";
    [ObservableProperty] private int _intervalMs = 100;
    [ObservableProperty] private string _modifier = "";
    [ObservableProperty] private long _sentCount;

    public int GridRow { get; set; }
    public byte[] WorkingData { get; } = new byte[64];
    public long NextMs { get; set; }
    public volatile byte[]? PendingData;

    private bool _suppressPending;

    public byte[]? ConsumePending()
    {
        if (_suppressPending) return null;
        return Interlocked.Exchange(ref PendingData, null);
    }

    public void UpdateDataFromWorking(int byteLen)
    {
        _suppressPending = true;
        Data = string.Join(" ", WorkingData.AsSpan(0, Math.Min(byteLen, 64)).ToArray().Select(b => b.ToString("X2")));
        _suppressPending = false;
    }
}
