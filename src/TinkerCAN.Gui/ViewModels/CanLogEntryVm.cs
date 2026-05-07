using CommunityToolkit.Mvvm.ComponentModel;

namespace LINGui.ViewModels;

public partial class CanLogEntryVm : ObservableObject
{
    [ObservableProperty] private long _seq;
    [ObservableProperty] private string _timestamp = "";
    [ObservableProperty] private string _dir = "RX";
    [ObservableProperty] private string _type = "Data";
    [ObservableProperty] private string _id = "000";
    [ObservableProperty] private string _dlc = "8";
    [ObservableProperty] private long _count = 1;
    [ObservableProperty] private string _data = "";
    [ObservableProperty] private bool _isTx;

    public CanLogEntryVm(long seq, string dir, string type, string id, string dlc, string data)
    {
        Seq = seq;
        Timestamp = System.DateTime.Now.ToString("HH:mm:ss.fff");
        Dir = dir;
        Type = type;
        Id = id;
        Dlc = dlc;
        Data = data;
        IsTx = dir == "TX";
    }

    public void Update(long newSeq, string newData)
    {
        Seq = newSeq;
        Timestamp = System.DateTime.Now.ToString("HH:mm:ss.fff");
        Data = newData;
        Count++;
    }
}
