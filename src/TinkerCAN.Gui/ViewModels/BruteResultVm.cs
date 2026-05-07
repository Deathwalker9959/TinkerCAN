using CommunityToolkit.Mvvm.ComponentModel;

namespace LINGui.ViewModels;

public partial class BruteResultVm : ObservableObject
{
    [ObservableProperty] private string _id = "00";
    [ObservableProperty] private string _pid = "00";
    [ObservableProperty] private string _payload = "";
    [ObservableProperty] private string _response = "-";
    [ObservableProperty] private string _respData = "";
    [ObservableProperty] private bool _hasResponse;

    public BruteResultVm(string id, string pid, string payload, string response, string respData)
    {
        Id = id;
        Pid = pid;
        Payload = payload;
        Response = response;
        RespData = respData;
        HasResponse = string.Equals(response, "YES", System.StringComparison.OrdinalIgnoreCase);
    }
}
