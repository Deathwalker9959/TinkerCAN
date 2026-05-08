using CommunityToolkit.Mvvm.ComponentModel;

namespace LINGui.ViewModels;

public partial class SectorBitVm : ObservableObject
{
    [ObservableProperty] private bool _value;
    [ObservableProperty] private bool _changed;
    [ObservableProperty] private bool _active = true;

    public string BitBackground => !Active ? "#252526" : Changed ? "#8B1A1A" : Value ? "#3A5A8A" : "#1C1C1C";

    public SectorBitVm(bool value) => Value = value;

    partial void OnChangedChanged(bool value) => OnPropertyChanged(nameof(BitBackground));
    partial void OnActiveChanged(bool value) => OnPropertyChanged(nameof(BitBackground));
    partial void OnValueChanged(bool value) => OnPropertyChanged(nameof(BitBackground));
}
