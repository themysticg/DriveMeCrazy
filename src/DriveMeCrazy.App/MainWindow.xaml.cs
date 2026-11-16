using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DriveMeCrazy.App.ViewModels;
using DriveMeCrazy.Core.Services;

namespace DriveMeCrazy.App;

public partial class MainWindow : Window
{
    private readonly StartupOptions _options;
    private readonly MainViewModel _vm;

    public MainWindow(StartupOptions options)
    {
        _options = options;
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        AppInfo.ApplyTheme(options.Theme ?? _vm.Settings.Theme);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_options.ScreenshotDir is { } dir)
        {
            await RunScreenshotModeAsync(dir);
            return;
        }
        if (_options.ScanOnStart) await _vm.ScanAsync();
    }

    /// <summary>Scans, renders every page to PNG, then exits. Used for README screenshots and smoke tests.</summary>
    private async Task RunScreenshotModeAsync(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            if (_options.ScanOnStart) await _vm.ScanAsync();
            for (int i = 0; i < _vm.NavItems.Count; i++)
            {
                var nav = _vm.NavItems[i];
                _vm.SelectedNav = nav;
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                await Task.Delay(400);
                Save(Path.Combine(dir, $"{i + 1}-{nav.Key}.png"));
                if (nav.Page is DriversPageViewModel drivers)
                {
                    drivers.SelectedTab = 1;
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    await Task.Delay(400);
                    Save(Path.Combine(dir, $"{i + 1}-{nav.Key}-old.png"));
                    drivers.SelectedTab = 0;
                }
            }
            _vm.SelectedNav = _vm.NavItems[0];
            await Task.Delay(300);
            File.WriteAllText(Path.Combine(dir, "summary.txt"),
                $"drivers={_vm.DriverList.Items.Count} windows={_vm.WindowsList.Items.Count} store={_vm.StoreList.Items.Count} " +
                $"recommended={_vm.RecommendedCount} oldDrivers={_vm.OldDriverCount} storeStatus={_vm.StoreSource.Message} wuStatus={_vm.WindowsSource.Message}");
        }
        catch (Exception ex)
        {
            Log.Error("Screenshot mode failed", ex);
            File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString());
        }
        Application.Current.Shutdown();
    }

    private void Save(string path)
    {
        Root.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(Root);
        var bmp = new RenderTargetBitmap(
            (int)Math.Ceiling(Root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(Root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(Root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
