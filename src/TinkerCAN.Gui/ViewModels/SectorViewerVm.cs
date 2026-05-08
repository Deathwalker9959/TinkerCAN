using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LINGui.ViewModels;

public partial class SectorViewerVm : ObservableObject, IDisposable
{
    [ObservableProperty] private bool _showBits;
    [ObservableProperty] private bool _hideInactive;
    [ObservableProperty] private string _status = "";

    public ObservableCollection<SectorRowVm> Rows { get; } = new();
    private readonly Dictionary<string, SectorRowVm> _byId = new();
    private readonly System.Threading.Timer _inactiveTimer;

    public SectorViewerVm()
    {
        _inactiveTimer = new System.Threading.Timer(_ => CheckInactive(), null, 1000, 1000);
    }

    public void UpdateFrame(string id, byte[] data)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_byId.TryGetValue(id, out var row))
            {
                row = new SectorRowVm(id);
                _byId[id] = row;
                int idx = 0;
                while (idx < Rows.Count && string.Compare(Rows[idx].Id, id, StringComparison.OrdinalIgnoreCase) < 0)
                    idx++;
                Rows.Insert(idx, row);
            }
            row.Update(data, ShowBits);
            Status = $"{Rows.Count} IDs";
        });
    }

    partial void OnShowBitsChanged(bool value)
    {
        foreach (var row in Rows)
            row.SetShowBits(value);
    }

    partial void OnHideInactiveChanged(bool value)
    {
        foreach (var row in Rows)
            row.UpdateVisibility(!value || row.Active);
    }

    private void CheckInactive()
    {
        var now = DateTime.UtcNow;
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var row in Rows)
            {
                if (row.Active && (now - row.LastSeen).TotalMilliseconds > 5000)
                {
                    row.SetInactive();
                    if (HideInactive) row.UpdateVisibility(false);
                }
            }
        });
    }

    [RelayCommand]
    private void Clear()
    {
        Rows.Clear();
        _byId.Clear();
        Status = "";
    }

    public void Dispose() => _inactiveTimer.Dispose();
}
