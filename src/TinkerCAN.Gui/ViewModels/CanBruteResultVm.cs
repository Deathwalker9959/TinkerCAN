using CommunityToolkit.Mvvm.ComponentModel;

namespace LINGui.ViewModels;

public partial class CanBruteResultVm : ObservableObject
{
    [ObservableProperty] private string _type = "t";
    [ObservableProperty] private string _id = "000";
    [ObservableProperty] private string _dlc = "8";
    [ObservableProperty] private string _payload = "";
    [ObservableProperty] private string _ack = "-";
    [ObservableProperty] private string _respData = "";
    [ObservableProperty] private bool _hasAck;

    public CanBruteResultVm(string type, string id, string dlc, string payload, string ack, string respData)
    {
        Type = type;
        Id = id;
        Dlc = dlc;
        Payload = payload;
        Ack = ack;
        RespData = respData;
        HasAck = string.Equals(ack, "YES", System.StringComparison.OrdinalIgnoreCase);
    }
}
