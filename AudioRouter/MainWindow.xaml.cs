using System.ComponentModel;

namespace AudioRouter;

public partial class MainWindow
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _viewModel.Dispose();
    }
}
