using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LINGui.ViewModels;

namespace LINGui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.LogText) && vm.AutoScroll)
                        Dispatcher.UIThread.Post(() =>
                            LogBox.FindDescendantOfType<ScrollViewer>()?.ScrollToEnd(),
                            DispatcherPriority.Background);
                };
        };
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();
}
