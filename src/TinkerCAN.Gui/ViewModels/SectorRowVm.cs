using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LINGui.ViewModels;

public partial class SectorRowVm : ObservableObject
{
    public string Id { get; }
    public ObservableCollection<SectorByteVm> Bytes { get; } = new();

    [ObservableProperty] private bool _active = true;
    [ObservableProperty] private bool _rowVisible = true;
    [ObservableProperty] private string _rowForeground = "#E0E0E0";

    public DateTime LastSeen { get; private set; } = DateTime.UtcNow;

    public SectorRowVm(string id) => Id = id;

    public void Update(byte[] data, bool showBits)
    {
        LastSeen = DateTime.UtcNow;
        Active = true;
        RowForeground = "#E0E0E0";

        for (int i = 0; i < data.Length; i++)
        {
            if (i < Bytes.Count)
                Bytes[i].Update(data[i], true);
            else
                Bytes.Add(new SectorByteVm(data[i]) { ShowBits = showBits });
        }
        for (int i = data.Length; i < Bytes.Count; i++)
            Bytes[i].SetActive(false);
    }

    public void SetInactive()
    {
        Active = false;
        RowForeground = "#555555";
        foreach (var b in Bytes) b.SetActive(false);
    }

    public void UpdateVisibility(bool visible) => RowVisible = visible;

    public void SetShowBits(bool show)
    {
        foreach (var b in Bytes) b.ShowBits = show;
    }
}
