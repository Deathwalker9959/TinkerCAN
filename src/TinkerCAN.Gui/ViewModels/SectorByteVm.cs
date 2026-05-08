using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LINGui.ViewModels;

public partial class SectorByteVm : ObservableObject
{
    [ObservableProperty] private byte _value;
    [ObservableProperty] private bool _changed;
    [ObservableProperty] private bool _active = true;
    [ObservableProperty] private bool _showBits;

    public ObservableCollection<SectorBitVm> Bits { get; } = new();

    public string HexText => Value.ToString("X2");
    public string CellBackground => !Active ? "#252526" : Changed ? "#8B1A1A" : "#2D2D30";
    public string CellForeground => Active ? "#E0E0E0" : "#4A4A4A";

    public SectorByteVm(byte value)
    {
        Value = value;
        for (int i = 7; i >= 0; i--)
            Bits.Add(new SectorBitVm(((value >> i) & 1) == 1));
    }

    public void Update(byte newValue, bool active)
    {
        bool changed = newValue != Value;
        for (int i = 7; i >= 0; i--)
        {
            int bitIdx = 7 - i;
            Bits[bitIdx].Changed = ((newValue >> i) & 1) != ((Value >> i) & 1);
            Bits[bitIdx].Value = ((newValue >> i) & 1) == 1;
            Bits[bitIdx].Active = active;
        }
        Value = newValue;
        Changed = changed;
        Active = active;
        OnPropertyChanged(nameof(HexText));
        OnPropertyChanged(nameof(CellBackground));
        OnPropertyChanged(nameof(CellForeground));
    }

    public void SetActive(bool active)
    {
        Active = active;
        foreach (var bit in Bits) bit.Active = active;
        OnPropertyChanged(nameof(CellBackground));
        OnPropertyChanged(nameof(CellForeground));
    }

    partial void OnChangedChanged(bool value) => OnPropertyChanged(nameof(CellBackground));
    partial void OnActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(CellBackground));
        OnPropertyChanged(nameof(CellForeground));
    }
}
