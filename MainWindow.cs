using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using Windows.Graphics;

namespace HueDesktop;

public sealed class MainWindow : Window
{
    private readonly MainPage _mainPage;
    private readonly AppWindow _appWindow;
    private readonly BridgeSettingsStore _settingsStore = new();
    private readonly DispatcherQueueTimer _windowSizeSaveTimer;
    private SizeInt32? _pendingWindowSize;
    private bool _hiddenToTray;
    private bool _allowClose;

    public MainWindow()
    {
        Title = "Hue lights";
        _mainPage = new MainPage();
        Content = _mainPage;

        var windowId = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        _appWindow = AppWindow.GetFromWindowId(windowId);
        RestoreWindowSize(windowId);

        _windowSizeSaveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _windowSizeSaveTimer.Interval = TimeSpan.FromMilliseconds(500);
        _windowSizeSaveTimer.IsRepeating = false;
        _windowSizeSaveTimer.Tick += WindowSizeSaveTimer_Tick;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "HueTrayIcon.ico");
        if (File.Exists(iconPath))
        {
            _appWindow.SetTaskbarIcon(iconPath);
            _appWindow.SetTitleBarIcon(iconPath);
        }

        _appWindow.Changed += AppWindow_Changed;
        _appWindow.Closing += AppWindow_Closing;
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
    }

    public void ExitApplication()
    {
        _allowClose = true;
        Application.Current.Exit();
    }

    public void ShowFromTray()
    {
        _hiddenToTray = false;
        if (_appWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }

        WindowExtensions.Show(this, disableEfficiencyMode: false);
        Activate();
        UpdateBridgeRefreshSuspension();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!_hiddenToTray &&
            sender.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Minimized)
        {
            _hiddenToTray = true;
            _mainPage.SetBridgeRefreshSuspended(true);
            WindowExtensions.Hide(this, enableEfficiencyMode: false);
        }

        if (args.DidSizeChange &&
            sender.Presenter is OverlappedPresenter restoredPresenter &&
            restoredPresenter.State == OverlappedPresenterState.Restored)
        {
            _pendingWindowSize = sender.Size;
            _windowSizeSaveTimer.Stop();
            _windowSizeSaveTimer.Start();
        }

        UpdateBridgeRefreshSuspension();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        _hiddenToTray = true;
        WindowExtensions.Hide(this, enableEfficiencyMode: false);
        UpdateBridgeRefreshSuspension();
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            UpdateBridgeRefreshSuspension();
        }
    }

    private void UpdateBridgeRefreshSuspension()
    {
        var isMinimized = _appWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Minimized;
        _mainPage.SetBridgeRefreshSuspended(_hiddenToTray || !_appWindow.IsVisible || isMinimized);
    }

    private void RestoreWindowSize(WindowId windowId)
    {
        try
        {
            var savedSize = _settingsStore.LoadWindowSize();
            if (savedSize is null)
            {
                return;
            }

            var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest).WorkArea;
            _appWindow.Resize(new SizeInt32(
                Math.Min(savedSize.Value.Width, workArea.Width),
                Math.Min(savedSize.Value.Height, workArea.Height)));
        }
        catch (IOException exception)
        {
            _mainPage.ReportWindowSettingsError($"Saved window size could not be loaded: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            _mainPage.ReportWindowSettingsError($"Saved window size could not be loaded: {exception.Message}");
        }
        catch (System.Text.Json.JsonException exception)
        {
            _mainPage.ReportWindowSettingsError($"Saved window settings are invalid: {exception.Message}");
        }
    }

    private void WindowSizeSaveTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        SavePendingWindowSize();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _windowSizeSaveTimer.Stop();
        if (_appWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Restored)
        {
            _pendingWindowSize = _appWindow.Size;
        }

        SavePendingWindowSize();
        _windowSizeSaveTimer.Tick -= WindowSizeSaveTimer_Tick;
        _appWindow.Changed -= AppWindow_Changed;
        _appWindow.Closing -= AppWindow_Closing;
        Activated -= MainWindow_Activated;
    }

    private void SavePendingWindowSize()
    {
        if (_pendingWindowSize is not { } size)
        {
            return;
        }

        try
        {
            _settingsStore.SaveWindowSize(size.Width, size.Height);
            _pendingWindowSize = null;
        }
        catch (IOException exception)
        {
            _mainPage.ReportWindowSettingsError($"Window size could not be saved: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            _mainPage.ReportWindowSettingsError($"Window size could not be saved: {exception.Message}");
        }
        catch (System.Text.Json.JsonException exception)
        {
            _mainPage.ReportWindowSettingsError($"Window settings are invalid: {exception.Message}");
        }
    }
}
