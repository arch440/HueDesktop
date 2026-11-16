using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HueDesktop;

public sealed partial class MainPage : Page
{
    private static readonly (double Kelvin, Color Color)[] ColorTemperaturePreviewStops =
    [
        (1_000, Color.FromArgb(255, 255, 48, 0)),
        (1_700, Color.FromArgb(255, 255, 112, 0)),
        (2_400, Color.FromArgb(255, 255, 154, 64)),
        (3_000, Color.FromArgb(255, 255, 184, 115)),
        (3_350, Color.FromArgb(255, 255, 194, 138)),
        (4_000, Color.FromArgb(255, 255, 238, 226)),
        (5_000, Color.FromArgb(255, 255, 243, 238)),
        (6_000, Color.FromArgb(255, 249, 245, 255)),
        (6_500, Color.FromArgb(255, 241, 244, 255)),
        (9_500, Color.FromArgb(255, 204, 217, 255)),
        (15_000, Color.FromArgb(255, 158, 184, 255)),
        (27_000, Color.FromArgb(255, 111, 145, 255))
    ];

    private readonly BridgeSettingsStore _settingsStore = new();
    private readonly Dictionary<string, LampControlElements> _lampControls = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherQueueTimer _bridgeRefreshTimer;
    private System.Drawing.Icon? _customTrayIcon;
    private HueBridgeClient? _bridgeClient;
    private string? _homeGroupedLightId;
    private string? _roomLayoutSignature;
    private bool _isUpdatingHomePowerToggle;
    private bool _isHomePowerOperationActive;
    private bool _isBridgeRefreshInProgress;
    private bool _isBridgeRefreshSuspended;
    private int _activeBridgeMutations;
    private long _bridgeStateVersion;

    public ICommand OpenWindowCommand { get; }
    public ICommand ExitApplicationCommand { get; }

    public MainPage()
    {
        OpenWindowCommand = new DelegateCommand(() => App.MainWindow?.ShowFromTray());
        ExitApplicationCommand = new DelegateCommand(() => App.MainWindow?.ExitApplication());
        InitializeComponent();
        _bridgeRefreshTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _bridgeRefreshTimer.Interval = TimeSpan.FromSeconds(10);
        _bridgeRefreshTimer.IsRepeating = true;
        _bridgeRefreshTimer.Tick += BridgeRefreshTimer_Tick;
        Loaded += MainPage_Loaded;
    }

    public void SetBridgeRefreshSuspended(bool suspended)
    {
        if (_isBridgeRefreshSuspended == suspended)
        {
            return;
        }

        _isBridgeRefreshSuspended = suspended;
        if (suspended)
        {
            _bridgeRefreshTimer.Stop();
        }
        else if (_bridgeClient is not null)
        {
            _bridgeRefreshTimer.Stop();
            _bridgeRefreshTimer.Start();
            _ = RefreshBridgeStateAsync();
        }
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainPage_Loaded;
        LoadCustomTrayIcon();
        await LoadSavedConnectionAsync();
    }

    private void LoadCustomTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "HueTrayIcon.ico");
        if (!File.Exists(iconPath))
        {
            return;
        }

        try
        {
            _customTrayIcon = new System.Drawing.Icon(iconPath);
            TrayIcon.Icon = _customTrayIcon;
        }
        catch (IOException exception)
        {
            StatusTextBlock.Text = $"Custom tray icon could not be loaded: {exception.Message}";
        }
        catch (UnauthorizedAccessException exception)
        {
            StatusTextBlock.Text = $"Custom tray icon could not be loaded: {exception.Message}";
        }
        catch (ArgumentException exception)
        {
            StatusTextBlock.Text = $"Custom tray icon is invalid: {exception.Message}";
        }
        catch (ExternalException exception)
        {
            StatusTextBlock.Text = $"Custom tray icon could not be displayed: {exception.Message}";
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        await ConnectToBridgeAsync(BridgeIpBox.Text, ApplicationKeyBox.Password);
    }

    private async Task ConnectToBridgeAsync(string addressText, string applicationKeyText)
    {
        var address = addressText.Trim();
        if (!IPAddress.TryParse(address, out var ipAddress))
        {
            StatusTextBlock.Text = "Enter a valid bridge IP address.";
            return;
        }

        var applicationKey = applicationKeyText.Trim();
        if (applicationKey.Length == 0)
        {
            StatusTextBlock.Text = "Enter the Hue application key.";
            return;
        }

        address = ipAddress.ToString();
        ConnectButton.IsEnabled = false;
        _bridgeRefreshTimer.Stop();
        RoomsPanel.Children.Clear();
        ResetHomePowerState();
        _bridgeClient?.Dispose();
        _bridgeClient = null;
        StatusTextBlock.Text = "Connecting...";

        try
        {
            var bridgeUri = new UriBuilder(Uri.UriSchemeHttps, ipAddress.ToString()).Uri;
            var trustedFingerprint = _settingsStore.LoadTrustedFingerprint(address);
            var connection = await ConnectAsync(bridgeUri, applicationKey, trustedFingerprint);
            if (connection is null)
            {
                StatusTextBlock.Text = "Connection cancelled.";
                return;
            }

            _bridgeClient = connection.Value.Client;
            AddRoomControls(connection.Value.Data);
            if (!_isBridgeRefreshSuspended)
            {
                _bridgeRefreshTimer.Start();
            }
            var connectionMessage = $"Connected. Found {connection.Value.Data.Rooms.Count} room(s).";
            if (_homeGroupedLightId is null)
            {
                connectionMessage += " The bridge did not provide a home-wide grouped light.";
            }

            if (connection.Value.Client.ConnectivityReadWarning is string connectivityWarning)
            {
                connectionMessage += $" Reachability data is unavailable: {connectivityWarning}";
            }

            if (connection.Value.NewlyTrustedFingerprint is string acceptedFingerprint)
            {
                try
                {
                    _settingsStore.SaveTrustedFingerprint(address, acceptedFingerprint);
                }
                catch (IOException exception)
                {
                    connectionMessage += $" The certificate trust could not be saved: {exception.Message}";
                }
                catch (UnauthorizedAccessException exception)
                {
                    connectionMessage += $" The certificate trust could not be saved: {exception.Message}";
                }
                catch (JsonException exception)
                {
                    connectionMessage += $" The certificate trust could not be saved: {exception.Message}";
                }
            }

            try
            {
                if (RememberConnectionCheckBox.IsChecked == true)
                {
                    _settingsStore.Save(address, applicationKey);
                }
                else
                {
                    _settingsStore.Forget();
                }
            }
            catch (IOException exception)
            {
                connectionMessage = $"Connected, but settings could not be saved: {exception.Message}";
            }
            catch (UnauthorizedAccessException exception)
            {
                connectionMessage = $"Connected, but settings could not be saved: {exception.Message}";
            }
            catch (COMException exception)
            {
                connectionMessage = $"Connected, but the Windows Credential Locker failed: {exception.Message}";
            }

            StatusTextBlock.Text = connectionMessage;
            ConnectionPanel.Visibility = Visibility.Collapsed;
            ChangeConnectionButton.Visibility = Visibility.Visible;
        }
        catch (HttpRequestException exception)
        {
            StatusTextBlock.Text = $"Bridge request failed: {exception.Message}";
        }
        catch (JsonException exception)
        {
            StatusTextBlock.Text = $"Saved settings or bridge data are invalid: {exception.Message}";
        }
        catch (InvalidDataException exception)
        {
            StatusTextBlock.Text = exception.Message;
        }
        catch (TaskCanceledException)
        {
            StatusTextBlock.Text = "The bridge request timed out.";
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void ChangeConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectionPanel.Visibility = Visibility.Visible;
        ChangeConnectionButton.Visibility = Visibility.Collapsed;
    }

    private async Task LoadSavedConnectionAsync()
    {
        try
        {
            var savedConnection = _settingsStore.Load();
            if (savedConnection is null)
            {
                return;
            }

            BridgeIpBox.Text = savedConnection.BridgeIp;
            var applicationKey = savedConnection.ApplicationKey;
            if (!string.IsNullOrWhiteSpace(applicationKey))
            {
                ApplicationKeyBox.Password = applicationKey;
                StatusTextBlock.Text = "Saved connection found. Connecting...";
                await ConnectToBridgeAsync(savedConnection.BridgeIp, applicationKey);
            }
            else
            {
                StatusTextBlock.Text = "Saved bridge IP loaded. Enter the application key.";
            }
        }
        catch (IOException exception)
        {
            StatusTextBlock.Text = $"Saved connection could not be loaded: {exception.Message}";
        }
        catch (UnauthorizedAccessException exception)
        {
            StatusTextBlock.Text = $"Saved connection could not be loaded: {exception.Message}";
        }
        catch (JsonException exception)
        {
            StatusTextBlock.Text = $"Saved connection settings are invalid: {exception.Message}";
        }
        catch (COMException exception)
        {
            StatusTextBlock.Text = $"Windows Credential Locker failed: {exception.Message}";
        }
    }

    internal void ReportWindowSettingsError(string message)
    {
        StatusTextBlock.Text = message;
    }

    private async Task<(HueBridgeClient Client, BridgeData Data, string? NewlyTrustedFingerprint)?> ConnectAsync(
        Uri bridgeUri,
        string applicationKey,
        string? trustedFingerprint)
    {
        var firstClient = new HueBridgeClient(bridgeUri, applicationKey, trustedFingerprint);
        HueBridgeClient? ownedClient = firstClient;
        string? newlyTrustedFingerprint = null;

        try
        {
            BridgeData data;
            try
            {
                data = await firstClient.GetRoomsAsync();
            }
            catch (HttpRequestException) when (firstClient.RejectedCertificateFingerprint is string fingerprint)
            {
                firstClient.Dispose();
                ownedClient = null;

                if (!await ConfirmCertificateTrustAsync(fingerprint))
                {
                    return null;
                }

                var pinnedClient = new HueBridgeClient(bridgeUri, applicationKey, fingerprint);
                ownedClient = pinnedClient;
                data = await pinnedClient.GetRoomsAsync();
                newlyTrustedFingerprint = fingerprint;
            }

            var connectedClient = ownedClient
                ?? throw new InvalidOperationException("The Hue bridge connection was not initialized.");
            ownedClient = null;
            return (connectedClient, data, newlyTrustedFingerprint);
        }
        finally
        {
            ownedClient?.Dispose();
        }
    }

    private async Task<bool> ConfirmCertificateTrustAsync(string fingerprint)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Trust Hue Bridge certificate?",
            Content = $"This is the first time this bridge certificate is seen, or its certificate has changed. Verify this SHA-256 fingerprint on your local network before continuing:\n\n{fingerprint}",
            PrimaryButtonText = "Trust this certificate",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void AddRoomControls(BridgeData data)
    {
        RoomsPanel.Children.Clear();
        _lampControls.Clear();

        foreach (var room in data.Rooms.OrderBy(
                     room => room.Metadata?.Name ?? "Unnamed room",
                     StringComparer.CurrentCultureIgnoreCase))
        {
            var roomChildIds = new HashSet<string>(
                room.Children?
                    .Where(child =>
                        string.Equals(child.ResourceType, "device", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(child.ResourceType, "light", StringComparison.OrdinalIgnoreCase))
                    .Select(child => child.ResourceId) ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var roomLights = data.Lights
                .Where(light =>
                    roomChildIds.Contains(light.Id) ||
                    (light.Owner is not null && roomChildIds.Contains(light.Owner.ResourceId)))
                .OrderBy(light => light.Metadata?.Name ?? "Lamp", StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var roomContent = new StackPanel { Spacing = 12 };
            var roomHeader = new Grid { ColumnSpacing = 12 };
            roomHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            roomHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            roomHeader.Children.Add(new TextBlock
            {
                Text = room.Metadata?.Name ?? "Unnamed room",
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            var lampCount = new TextBlock
            {
                Text = roomLights.Count == 1 ? "1 light" : $"{roomLights.Count} lights",
                FontSize = 12,
                Foreground = GetThemeBrush("HueMutedTextBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(lampCount, 1);
            roomHeader.Children.Add(lampCount);
            roomContent.Children.Add(roomHeader);

            var roomScenes = data.Scenes
                .Where(scene => scene.Group is { } group &&
                    string.Equals(group.ResourceType, "room", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(group.ResourceId, room.Id, StringComparison.OrdinalIgnoreCase))
                .OrderBy(scene => scene.Metadata?.Name ?? "Unnamed scene", StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (roomScenes.Count > 0)
            {
                var sceneButtons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8
                };
                foreach (var scene in roomScenes)
                {
                    var sceneButton = new Button
                    {
                        Content = scene.Metadata?.Name ?? "Unnamed scene",
                        Tag = new SceneButtonState(scene.Id, scene.Metadata?.Name ?? "Unnamed scene"),
                        Background = GetThemeBrush("HueAccentSoftBrush"),
                        BorderBrush = GetThemeBrush("HueBorderBrush"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(10),
                        Foreground = GetThemeBrush("HueTextBrush"),
                        Padding = new Thickness(12, 6, 12, 6)
                    };
                    sceneButton.Click += SceneButton_Click;
                    sceneButtons.Children.Add(sceneButton);
                }

                roomContent.Children.Add(new ScrollViewer
                {
                    Content = sceneButtons,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollMode = ScrollMode.Enabled,
                    VerticalScrollMode = ScrollMode.Disabled
                });
            }

            if (roomLights.Count == 0)
            {
                roomContent.Children.Add(new TextBlock
                {
                    Text = "No lamps found in this room.",
                    Margin = new Thickness(8, 0, 0, 0)
                });
            }
            else
            {
                var lampControls = new StackPanel
                {
                    Margin = new Thickness(0),
                    Spacing = 8
                };
                foreach (var light in roomLights)
                {
                    lampControls.Children.Add(CreateLampControl(light));
                }

                roomContent.Children.Add(lampControls);
            }

            RoomsPanel.Children.Add(new Border
            {
                Background = GetThemeBrush("HueCardBrush"),
                BorderBrush = GetThemeBrush("HueBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(18),
                Child = roomContent
            });
        }

        UpdateHomePowerToggle(data.GroupedLights);
        _roomLayoutSignature = CreateRoomLayoutSignature(data);
    }

    private async void SceneButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SceneButtonState scene } button)
        {
            return;
        }

        var bridgeClient = _bridgeClient;
        if (bridgeClient is null)
        {
            StatusTextBlock.Text = "Connect to the bridge before activating a scene.";
            return;
        }

        button.IsEnabled = false;
        BeginBridgeMutation();
        try
        {
            try
            {
                await bridgeClient.ActivateSceneAsync(scene.SceneId);
            }
            catch (HttpRequestException exception)
            {
                StatusTextBlock.Text = $"Could not activate scene: {exception.Message}";
                return;
            }
            catch (TaskCanceledException)
            {
                StatusTextBlock.Text = "The scene activation timed out.";
                return;
            }

            try
            {
                var lights = await bridgeClient.GetLightsAsync();
                UpdateLampControls(lights);
                StatusTextBlock.Text = $"Activated scene '{scene.Name}'.";
                AppendConnectivityReadWarning(bridgeClient);
                await RefreshHomePowerStateAsync(bridgeClient);
            }
            catch (HttpRequestException exception)
            {
                StatusTextBlock.Text = $"Scene activated, but lamp controls could not be refreshed: {exception.Message}";
            }
            catch (TaskCanceledException)
            {
                StatusTextBlock.Text = "Scene activated, but refreshing the lamp controls timed out.";
            }
            catch (JsonException exception)
            {
                StatusTextBlock.Text = $"Scene activated, but the refreshed light data was invalid: {exception.Message}";
            }
            catch (InvalidDataException exception)
            {
                StatusTextBlock.Text = $"Scene activated, but the refreshed light data was invalid: {exception.Message}";
            }
        }
        finally
        {
            button.IsEnabled = true;
            EndBridgeMutation();
        }
    }

    private void UpdateLampControls(IEnumerable<LightResource> lights, bool preserveFocusedControls = false)
    {
        foreach (var light in lights)
        {
            if (!_lampControls.TryGetValue(light.Id, out var controls))
            {
                continue;
            }

            if (light.ConnectivityStatus is not null)
            {
                SetLampReachability(controls, IsLightReachable(light));
            }

            if (light.Dimming?.Brightness is double brightness &&
                (!preserveFocusedControls || controls.BrightnessSlider.FocusState == FocusState.Unfocused) &&
                controls.BrightnessSlider.Tag is LampSliderState sliderState)
            {
                var currentBrightness = (int)Math.Clamp(Math.Round(brightness), 1, 100);
                controls.BrightnessSlider.Value = currentBrightness;
                sliderState.ValueText.Text = $"{currentBrightness}%";
            }

            if (light.State is not null &&
                (!preserveFocusedControls || controls.Toggle.FocusState == FocusState.Unfocused))
            {
                controls.Toggle.Toggled -= LampToggle_Toggled;
                controls.Toggle.IsOn = light.State.IsOn;
                controls.Toggle.Toggled += LampToggle_Toggled;
            }
        }
    }

    private static Brush GetThemeBrush(string resourceKey) =>
        Application.Current.Resources[resourceKey] as Brush
        ?? throw new InvalidOperationException($"The app theme resource '{resourceKey}' is missing.");

    private static bool IsLightReachable(LightResource light) =>
        IsConnectivityStatusReachable(light.ConnectivityStatus);

    private static bool IsConnectivityStatusReachable(string? status) =>
        string.IsNullOrEmpty(status) ||
        string.Equals(status, "connected", StringComparison.OrdinalIgnoreCase);

    private static string CreateRoomLayoutSignature(BridgeData data) =>
        JsonSerializer.Serialize(new
        {
            Rooms = data.Rooms
                .OrderBy(room => room.Id, StringComparer.OrdinalIgnoreCase)
                .Select(room => new
                {
                    room.Id,
                    Name = room.Metadata?.Name,
                    Children = room.Children?
                        .OrderBy(child => child.ResourceType, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(child => child.ResourceId, StringComparer.OrdinalIgnoreCase)
                        .Select(child => new { child.ResourceType, child.ResourceId })
                        .ToArray()
                })
                .ToArray(),
            Lights = data.Lights
                .OrderBy(light => light.Id, StringComparer.OrdinalIgnoreCase)
                .Select(light => new
                {
                    light.Id,
                    Name = light.Metadata?.Name,
                    OwnerId = light.Owner?.ResourceId,
                    CanDim = light.Dimming?.Brightness is not null,
                    HasColor = light.Color?.Xy is not null,
                    MirekMinimum = light.ColorTemperature?.Schema?.MirekMinimum,
                    MirekMaximum = light.ColorTemperature?.Schema?.MirekMaximum
                })
                .ToArray(),
            Scenes = data.Scenes
                .OrderBy(scene => scene.Id, StringComparer.OrdinalIgnoreCase)
                .Select(scene => new
                {
                    scene.Id,
                    Name = scene.Metadata?.Name,
                    GroupId = scene.Group?.ResourceId,
                    GroupType = scene.Group?.ResourceType
                })
                .ToArray()
        });

    private static void SetLampReachability(LampControlElements controls, bool isReachable)
    {
        controls.ReachabilityText.Visibility = isReachable ? Visibility.Collapsed : Visibility.Visible;
        controls.Toggle.IsEnabled = isReachable;
        if (controls.BrightnessSlider.Tag is LampSliderState sliderState)
        {
            controls.BrightnessSlider.IsEnabled = isReachable && sliderState.CanDim;
        }

        if (controls.ColorButton is not null)
        {
            controls.ColorButton.IsEnabled = isReachable;
        }
    }

    private async void BridgeRefreshTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        await RefreshBridgeStateAsync();
    }

    private async Task RefreshBridgeStateAsync()
    {
        var bridgeClient = _bridgeClient;
        if (bridgeClient is null ||
            _isBridgeRefreshSuspended ||
            _isBridgeRefreshInProgress ||
            _activeBridgeMutations > 0)
        {
            return;
        }

        var stateVersion = _bridgeStateVersion;
        _isBridgeRefreshInProgress = true;
        try
        {
            var data = await bridgeClient.GetRoomsAsync();
            if (!ReferenceEquals(bridgeClient, _bridgeClient) ||
                _isBridgeRefreshSuspended ||
                stateVersion != _bridgeStateVersion ||
                _activeBridgeMutations > 0)
            {
                return;
            }

            var layoutSignature = CreateRoomLayoutSignature(data);
            if (!string.Equals(_roomLayoutSignature, layoutSignature, StringComparison.Ordinal))
            {
                AddRoomControls(data);
            }
            else
            {
                UpdateLampControls(data.Lights, preserveFocusedControls: true);
                UpdateHomePowerToggle(data.GroupedLights);
            }

            AppendConnectivityReadWarning(bridgeClient);
        }
        catch (HttpRequestException exception)
        {
            if (ReferenceEquals(bridgeClient, _bridgeClient) && !_isBridgeRefreshSuspended)
            {
                StatusTextBlock.Text = $"Could not refresh bridge data: {exception.Message}";
            }
        }
        catch (TaskCanceledException)
        {
            if (ReferenceEquals(bridgeClient, _bridgeClient) && !_isBridgeRefreshSuspended)
            {
                StatusTextBlock.Text = "Refreshing bridge data timed out.";
            }
        }
        catch (JsonException exception)
        {
            if (ReferenceEquals(bridgeClient, _bridgeClient) && !_isBridgeRefreshSuspended)
            {
                StatusTextBlock.Text = $"Refreshed bridge data was invalid: {exception.Message}";
            }
        }
        catch (InvalidDataException exception)
        {
            if (ReferenceEquals(bridgeClient, _bridgeClient) && !_isBridgeRefreshSuspended)
            {
                StatusTextBlock.Text = $"Refreshed bridge data was invalid: {exception.Message}";
            }
        }
        finally
        {
            _isBridgeRefreshInProgress = false;
        }
    }

    private void BeginBridgeMutation()
    {
        _activeBridgeMutations++;
        _bridgeStateVersion++;
    }

    private void EndBridgeMutation()
    {
        _activeBridgeMutations--;
        _bridgeStateVersion++;
    }

    private void AppendConnectivityReadWarning(HueBridgeClient bridgeClient)
    {
        if (bridgeClient.ConnectivityReadWarning is string warning &&
            !StatusTextBlock.Text.Contains(warning, StringComparison.Ordinal))
        {
            StatusTextBlock.Text += $" Reachability data is unavailable: {warning}";
        }
    }

    private void UpdateHomePowerToggle(IEnumerable<GroupedLightResource> groupedLights)
    {
        var homeGroupedLight = groupedLights.FirstOrDefault(groupedLight =>
            string.Equals(
                groupedLight.Owner?.ResourceType,
                "bridge_home",
                StringComparison.OrdinalIgnoreCase));
        _homeGroupedLightId = homeGroupedLight?.Id;
        if (homeGroupedLight?.State is not OnState homeState)
        {
            SetHomePowerToggleState(false, false);
            return;
        }

        SetHomePowerToggleState(homeState.IsOn, true);
    }

    private void SetHomePowerToggleState(bool isOn, bool isEnabled)
    {
        _isUpdatingHomePowerToggle = true;
        HomePowerToggle.IsOn = isOn;
        HomePowerToggle.IsEnabled = isEnabled && !_isHomePowerOperationActive;
        _isUpdatingHomePowerToggle = false;
    }

    private void ResetHomePowerState()
    {
        _homeGroupedLightId = null;
        _isUpdatingHomePowerToggle = true;
        HomePowerToggle.IsOn = false;
        HomePowerToggle.IsEnabled = false;
        _isUpdatingHomePowerToggle = false;
    }

    private async Task RefreshHomePowerStateAsync(HueBridgeClient bridgeClient)
    {
        if (_homeGroupedLightId is null)
        {
            return;
        }

        try
        {
            var groupedLight = await bridgeClient.GetGroupedLightAsync(_homeGroupedLightId);
            SetHomePowerToggleState(groupedLight.State?.IsOn ?? false, groupedLight.State is not null);
        }
        catch (HttpRequestException exception)
        {
            StatusTextBlock.Text += $" The home switch state could not be refreshed: {exception.Message}";
        }
        catch (TaskCanceledException)
        {
            StatusTextBlock.Text += " The home switch state could not be refreshed because the request timed out.";
        }
        catch (JsonException exception)
        {
            StatusTextBlock.Text += $" The home switch response was invalid: {exception.Message}";
        }
        catch (InvalidDataException exception)
        {
            StatusTextBlock.Text += $" The home switch response was invalid: {exception.Message}";
        }
    }

    private UIElement CreateLampControl(LightResource light)
    {
        var brightness = (int)Math.Clamp(Math.Round(light.Dimming?.Brightness ?? 100), 1, 100);
        var canDim = light.Dimming?.Brightness is not null;
        var isReachable = IsLightReachable(light);
        var isOn = light.State?.IsOn ?? false;
        var valueText = new TextBlock
        {
            Text = canDim ? $"{brightness}%" : "N/A",
            MinWidth = 40,
            Foreground = GetThemeBrush("HueMutedTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var slider = new Slider
        {
            Minimum = 1,
            Maximum = 100,
            StepFrequency = 1,
            Value = brightness,
            MinWidth = 64,
            IsEnabled = canDim && isReachable,
            VerticalAlignment = VerticalAlignment.Center
        };
        slider.Tag = new LampSliderState(light.Id, canDim, valueText);
        slider.ValueChanged += BrightnessSlider_ValueChanged;
        slider.PointerCaptureLost += BrightnessSlider_PointerCaptureLost;
        slider.KeyUp += BrightnessSlider_KeyUp;

        var lampRow = new Grid
        {
            ColumnSpacing = 10
        };
        lampRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
        lampRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lampRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        lampRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lampRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lampName = new StackPanel
        {
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        lampName.Children.Add(new TextBlock
        {
            Text = light.Metadata?.Name ?? "Lamp",
            Foreground = GetThemeBrush("HueTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        var reachabilityText = new TextBlock
        {
            Text = "Not reachable",
            FontSize = 12,
            Foreground = GetThemeBrush("HueAccentBrush"),
            Visibility = isReachable ? Visibility.Collapsed : Visibility.Visible
        };
        lampName.Children.Add(reachabilityText);
        lampRow.Children.Add(lampName);

        var toggle = new ToggleSwitch
        {
            IsOn = isOn,
            IsEnabled = isReachable,
            OnContent = "On",
            OffContent = "Off",
            Tag = new LampToggleState(light.Id)
        };
        toggle.Toggled += LampToggle_Toggled;
        Grid.SetColumn(toggle, 1);
        lampRow.Children.Add(toggle);
        Grid.SetColumn(slider, 2);
        lampRow.Children.Add(slider);
        Grid.SetColumn(valueText, 3);
        lampRow.Children.Add(valueText);

        Button? colorButton = null;
        if (light.Color?.Xy is not null)
        {
            colorButton = AddColorButton(lampRow, new LampColorState(light.Id), isReachable);
        }
        else if (TryGetColorTemperatureRange(light.ColorTemperature, out _, out _))
        {
            colorButton = AddColorButton(lampRow, new LampColorTemperatureState(light.Id), isReachable);
        }

        _lampControls[light.Id] = new LampControlElements(toggle, slider, reachabilityText, colorButton);
        return new Border
        {
            Background = GetThemeBrush("HueSubtleBrush"),
            BorderBrush = GetThemeBrush("HueBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 8, 10, 8),
            Child = lampRow
        };
    }

    private Button AddColorButton(Grid lampRow, object state, bool isReachable)
    {
        var colorButton = new Button
        {
            Content = "Color",
            IsEnabled = isReachable,
            Background = GetThemeBrush("HueCardBrush"),
            BorderBrush = GetThemeBrush("HueAccentBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Foreground = GetThemeBrush("HueAccentBrush"),
            Margin = new Thickness(0),
            Padding = new Thickness(12, 6, 12, 6),
            Tag = state,
            VerticalAlignment = VerticalAlignment.Center
        };
        colorButton.Click += LampColorButton_Click;
        Grid.SetColumn(colorButton, 4);
        lampRow.Children.Add(colorButton);
        return colorButton;
    }

    private static bool TryGetColorTemperatureRange(
        ColorTemperatureState? colorTemperature,
        out int minimumMirek,
        out int maximumMirek)
    {
        minimumMirek = colorTemperature?.Schema?.MirekMinimum ?? 0;
        maximumMirek = colorTemperature?.Schema?.MirekMaximum ?? 0;
        return minimumMirek > 0 && maximumMirek >= minimumMirek;
    }

    private void BrightnessSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (sender is Slider { Tag: LampSliderState state })
        {
            state.ValueText.Text = $"{Math.Round(e.NewValue):0}%";
        }
    }

    private async void BrightnessSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Slider slider)
        {
            await SaveBrightnessAsync(slider);
        }
    }

    private async void BrightnessSlider_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (sender is Slider slider)
        {
            await SaveBrightnessAsync(slider);
        }
    }

    private async Task SaveBrightnessAsync(Slider slider)
    {
        if (slider.Tag is not LampSliderState state)
        {
            return;
        }

        var requestedBrightness = Math.Clamp((int)Math.Round(slider.Value), 1, 100);
        var bridgeClient = _bridgeClient;
        if (bridgeClient is null)
        {
            slider.IsEnabled = false;
            StatusTextBlock.Text = "Connect to the bridge before changing a lamp.";
            return;
        }

        BeginBridgeMutation();
        slider.IsEnabled = false;
        try
        {
            await bridgeClient.SetLampBrightnessAsync(state.LightId, requestedBrightness);
            StatusTextBlock.Text = $"Lamp brightness set to {requestedBrightness}%.";
        }
        catch (HttpRequestException exception)
        {
            StatusTextBlock.Text = $"Could not set lamp brightness: {exception.Message}";
        }
        catch (TaskCanceledException)
        {
            StatusTextBlock.Text = "The brightness update timed out.";
        }
        finally
        {
            try
            {
                await RefreshLampControlAsync(bridgeClient, state.LightId);
            }
            finally
            {
                EndBridgeMutation();
            }
        }
    }

    private async void LampColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        if (button.Tag is LampColorState colorState)
        {
            await ChooseRgbColorAsync(button, colorState);
        }
        else if (button.Tag is LampColorTemperatureState temperatureState)
        {
            await ChooseColorTemperatureAsync(button, temperatureState);
        }
    }

    private async Task ChooseRgbColorAsync(Button button, LampColorState state)
    {
        var bridgeClient = _bridgeClient;
        if (bridgeClient is null)
        {
            StatusTextBlock.Text = "Connect to the bridge before changing a lamp color.";
            return;
        }

        button.IsEnabled = false;
        var mutationStarted = false;
        try
        {
            var light = await bridgeClient.GetLightAsync(state.LightId);
            UpdateLampControls([light]);
            button.IsEnabled = false;
            if (!IsLightReachable(light))
            {
                StatusTextBlock.Text = "The lamp is not reachable.";
                return;
            }

            if (light.Color?.Xy is not XyState currentColor)
            {
                StatusTextBlock.Text = "The bridge did not provide the lamp's current color.";
                return;
            }

            var picker = new ColorPicker
            {
                Color = XyToRgb(currentColor),
                IsAlphaEnabled = false,
                IsColorPreviewVisible = true
            };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Choose lamp color",
                Content = picker,
                PrimaryButtonText = "Apply",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            BeginBridgeMutation();
            mutationStarted = true;
            await bridgeClient.SetLampColorAsync(state.LightId, RgbToXy(picker.Color));
            StatusTextBlock.Text = "Lamp color updated.";
        }
        catch (HttpRequestException exception)
        {
            StatusTextBlock.Text = $"Could not read or update lamp color: {exception.Message}";
        }
        catch (TaskCanceledException)
        {
            StatusTextBlock.Text = "Reading or updating lamp color timed out.";
        }
        catch (JsonException exception)
        {
            StatusTextBlock.Text = $"The bridge returned invalid lamp color data: {exception.Message}";
        }
        catch (InvalidDataException exception)
        {
            StatusTextBlock.Text = exception.Message;
        }
        finally
        {
            try
            {
                await RefreshLampControlAsync(bridgeClient, state.LightId);
            }
            finally
            {
                if (mutationStarted)
                {
                    EndBridgeMutation();
                }
            }
        }
    }

    private async Task ChooseColorTemperatureAsync(Button button, LampColorTemperatureState state)
    {
        var bridgeClient = _bridgeClient;
        if (bridgeClient is null)
        {
            StatusTextBlock.Text = "Connect to the bridge before changing a lamp color.";
            return;
        }

        button.IsEnabled = false;
        var mutationStarted = false;
        try
        {
            var light = await bridgeClient.GetLightAsync(state.LightId);
            UpdateLampControls([light]);
            button.IsEnabled = false;
            if (!IsLightReachable(light))
            {
                StatusTextBlock.Text = "The lamp is not reachable.";
                return;
            }

            if (!TryGetColorTemperatureRange(
                    light.ColorTemperature,
                    out var minimumMirek,
                    out var maximumMirek))
            {
                StatusTextBlock.Text = "The bridge did not provide the lamp's color-temperature range.";
                return;
            }

            var mirekState = light.ColorTemperature!;
            var currentMirek = mirekState.MirekValid == true && mirekState.Mirek is int currentMirekValue
                ? Math.Clamp(currentMirekValue, minimumMirek, maximumMirek)
                : (minimumMirek + maximumMirek) / 2;
            var minimumKelvin = 1_000_000d / maximumMirek;
            var maximumKelvin = 1_000_000d / minimumMirek;
            var currentKelvin = 1_000_000d / currentMirek;
            var temperatureText = new TextBlock
            {
                Text = FormatKelvin(currentKelvin),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var slider = new Slider
            {
                Minimum = minimumKelvin,
                Maximum = maximumKelvin,
                StepFrequency = 1,
                Value = currentKelvin,
                MinWidth = 300
            };
            var previewBrush = new SolidColorBrush(ColorTemperatureToRgb(currentKelvin));
            var preview = new Border
            {
                Width = 84,
                Height = 84,
                Background = previewBrush,
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                VerticalAlignment = VerticalAlignment.Center
            };
            slider.ValueChanged += (_, args) =>
            {
                temperatureText.Text = FormatKelvin(args.NewValue);
                previewBrush.Color = ColorTemperatureToRgb(args.NewValue);
            };

            var pickerControls = new StackPanel { Spacing = 8 };
            pickerControls.Children.Add(new TextBlock { Text = "Color temperature" });
            pickerControls.Children.Add(slider);
            pickerControls.Children.Add(temperatureText);
            var content = new Grid { ColumnSpacing = 16 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.Children.Add(pickerControls);
            Grid.SetColumn(preview, 1);
            content.Children.Add(preview);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Choose color temperature",
                Content = content,
                PrimaryButtonText = "Apply",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var mirek = (int)Math.Round(1_000_000d / slider.Value);
            BeginBridgeMutation();
            mutationStarted = true;
            await bridgeClient.SetLampColorTemperatureAsync(state.LightId, mirek);
            StatusTextBlock.Text = $"Lamp color temperature set to {FormatColorTemperature(mirek)}.";
        }
        catch (HttpRequestException exception)
        {
            StatusTextBlock.Text = $"Could not read or update lamp color temperature: {exception.Message}";
        }
        catch (TaskCanceledException)
        {
            StatusTextBlock.Text = "Reading or updating lamp color temperature timed out.";
        }
        catch (JsonException exception)
        {
            StatusTextBlock.Text = $"The bridge returned invalid color-temperature data: {exception.Message}";
        }
        catch (InvalidDataException exception)
        {
            StatusTextBlock.Text = exception.Message;
        }
        finally
        {
            try
            {
                await RefreshLampControlAsync(bridgeClient, state.LightId);
            }
            finally
            {
                if (mutationStarted)
                {
                    EndBridgeMutation();
                }
            }
        }
    }

    private async Task RefreshLampControlAsync(HueBridgeClient bridgeClient, string lightId)
    {
        try
        {
            UpdateLampControls([await bridgeClient.GetLightAsync(lightId)]);
            AppendConnectivityReadWarning(bridgeClient);
        }
        catch (HttpRequestException exception)
        {
            StatusTextBlock.Text += $" Lamp state could not be refreshed: {exception.Message}";
        }
        catch (TaskCanceledException)
        {
            StatusTextBlock.Text += " Lamp state could not be refreshed because the request timed out.";
        }
        catch (JsonException exception)
        {
            StatusTextBlock.Text += $" Lamp data was invalid: {exception.Message}";
        }
        catch (InvalidDataException exception)
        {
            StatusTextBlock.Text += $" Lamp data was invalid: {exception.Message}";
        }
    }

    private static string FormatColorTemperature(double mirek) =>
        $"{Math.Round(1_000_000 / mirek):N0} K";

    private static string FormatKelvin(double kelvin) =>
        $"{Math.Round(kelvin):N0} K";

    private static Color ColorTemperatureToRgb(double kelvin)
    {
        if (!double.IsFinite(kelvin))
        {
            return Color.FromArgb(255, 255, 255, 255);
        }

        kelvin = Math.Clamp(
            kelvin,
            ColorTemperaturePreviewStops[0].Kelvin,
            ColorTemperaturePreviewStops[^1].Kelvin);
        for (var index = 1; index < ColorTemperaturePreviewStops.Length; index++)
        {
            var upper = ColorTemperaturePreviewStops[index];
            if (kelvin <= upper.Kelvin)
            {
                var lower = ColorTemperaturePreviewStops[index - 1];
                var fraction = (kelvin - lower.Kelvin) / (upper.Kelvin - lower.Kelvin);
                return Color.FromArgb(
                    255,
                    InterpolateColorChannel(lower.Color.R, upper.Color.R, fraction),
                    InterpolateColorChannel(lower.Color.G, upper.Color.G, fraction),
                    InterpolateColorChannel(lower.Color.B, upper.Color.B, fraction));
            }
        }

        return ColorTemperaturePreviewStops[^1].Color;
    }

    private static byte InterpolateColorChannel(byte start, byte end, double fraction) =>
        (byte)Math.Round(start + (end - start) * fraction);

    private static Color XyToRgb(XyState? xy)
    {
        if (xy?.X is not double x || xy.Y is not double y || y <= 0)
        {
            return Color.FromArgb(255, 255, 255, 255);
        }

        const double luminance = 1;
        var X = x * luminance / y;
        var Z = (1 - x - y) * luminance / y;
        var red = X * 3.2406 - luminance * 1.5372 - Z * 0.4986;
        var green = -X * 0.9689 + luminance * 1.8758 + Z * 0.0415;
        var blue = X * 0.0557 - luminance * 0.2040 + Z * 1.0570;
        var max = Math.Max(red, Math.Max(green, blue));
        if (max > 1)
        {
            red /= max;
            green /= max;
            blue /= max;
        }

        return Color.FromArgb(
            255,
            ToSrgbByte(red),
            ToSrgbByte(green),
            ToSrgbByte(blue));
    }

    private static XyState RgbToXy(Color color)
    {
        var red = ToLinear(color.R / 255d);
        var green = ToLinear(color.G / 255d);
        var blue = ToLinear(color.B / 255d);
        var x = red * 0.4124 + green * 0.3576 + blue * 0.1805;
        var y = red * 0.2126 + green * 0.7152 + blue * 0.0722;
        var z = red * 0.0193 + green * 0.1192 + blue * 0.9505;
        var total = x + y + z;

        return total <= 0
            ? new XyState { X = 0.3127, Y = 0.3290 }
            : new XyState { X = x / total, Y = y / total };
    }

    private static byte ToSrgbByte(double value)
    {
        value = Math.Clamp(value, 0, 1);
        var srgb = value <= 0.0031308
            ? 12.92 * value
            : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055;
        return (byte)Math.Round(srgb * 255);
    }

    private static double ToLinear(double value) =>
        value <= 0.04045
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);

    private async void LampToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.Tag is not LampToggleState state)
        {
            return;
        }

        var bridgeClient = _bridgeClient;
        if (bridgeClient is null)
        {
            StatusTextBlock.Text = "Connect to the bridge before changing a lamp.";
            return;
        }

        var requestedState = toggle.IsOn;
        BeginBridgeMutation();
        toggle.IsEnabled = false;

        try
        {
            await bridgeClient.SetLampStateAsync(state.LightId, requestedState);
            StatusTextBlock.Text = $"Lamp turned {(requestedState ? "on" : "off")}.";
        }
        catch (HttpRequestException exception)
        {
            StatusTextBlock.Text = $"Could not update the lamp: {exception.Message}";
        }
        catch (TaskCanceledException)
        {
            StatusTextBlock.Text = "The lamp update timed out.";
        }
        finally
        {
            try
            {
                await RefreshLampControlAsync(bridgeClient, state.LightId);
                await RefreshHomePowerStateAsync(bridgeClient);
            }
            finally
            {
                EndBridgeMutation();
            }
        }
    }

    private async void HomePowerToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingHomePowerToggle ||
            _isHomePowerOperationActive ||
            sender is not ToggleSwitch homePowerToggle)
        {
            return;
        }

        var bridgeClient = _bridgeClient;
        var groupedLightId = _homeGroupedLightId;
        if (bridgeClient is null || groupedLightId is null)
        {
            SetHomePowerToggleState(false, false);
            StatusTextBlock.Text = bridgeClient is null
                ? "Connect to the bridge before changing home lights."
                : "The bridge does not provide a home-wide grouped light.";
            return;
        }

        var requestedIsOn = homePowerToggle.IsOn;
        BeginBridgeMutation();
        _isHomePowerOperationActive = true;
        homePowerToggle.IsEnabled = false;
        RoomsContentControl.IsEnabled = false;
        ChangeConnectionButton.IsEnabled = false;

        try
        {
            await bridgeClient.SetGroupedLightStateAsync(groupedLightId, requestedIsOn);
            StatusTextBlock.Text = requestedIsOn
                ? "Turned on the home lighting group."
                : "Turned off the home lighting group.";
            await RefreshHomeControlsAsync(bridgeClient);
        }
        catch (HttpRequestException exception)
        {
            StatusTextBlock.Text = $"Could not update home lights: {exception.Message}";
            await RefreshHomeControlsAsync(bridgeClient);
        }
        catch (TaskCanceledException)
        {
            StatusTextBlock.Text = "Updating home lights timed out.";
            await RefreshHomeControlsAsync(bridgeClient);
        }
        finally
        {
            _isHomePowerOperationActive = false;
            RoomsContentControl.IsEnabled = true;
            ChangeConnectionButton.IsEnabled = true;
            HomePowerToggle.IsEnabled = _bridgeClient is not null && _homeGroupedLightId is not null;
            EndBridgeMutation();
        }
    }

    private async Task RefreshHomeControlsAsync(HueBridgeClient bridgeClient)
    {
        try
        {
            UpdateLampControls(await bridgeClient.GetLightsAsync());
            AppendConnectivityReadWarning(bridgeClient);
        }
        catch (HttpRequestException exception)
        {
            StatusTextBlock.Text += $" Current light state could not be refreshed: {exception.Message}";
        }
        catch (TaskCanceledException)
        {
            StatusTextBlock.Text += " Current light state could not be refreshed because the request timed out.";
        }
        catch (JsonException exception)
        {
            StatusTextBlock.Text += $" Current light data was invalid: {exception.Message}";
        }
        catch (InvalidDataException exception)
        {
            StatusTextBlock.Text += $" Current light data was invalid: {exception.Message}";
        }

        await RefreshHomePowerStateAsync(bridgeClient);
    }

    private sealed class HueBridgeClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string? _trustedFingerprint;

        public HueBridgeClient(Uri bridgeUri, string applicationKey, string? trustedFingerprint)
        {
            _trustedFingerprint = trustedFingerprint;
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = ValidateCertificate
            };

            _httpClient = new HttpClient(handler)
            {
                BaseAddress = bridgeUri,
                Timeout = TimeSpan.FromSeconds(15)
            };
            _httpClient.DefaultRequestHeaders.Add("hue-application-key", applicationKey);
        }

        public string? RejectedCertificateFingerprint { get; private set; }
        public string? ConnectivityReadWarning { get; private set; }

        public async Task<List<LightResource>> GetLightsAsync()
        {
            var lightsTask = GetResourcesAsync<LightResource>("light");
            var connectivityTask = GetConnectivityResourcesAsync();
            await Task.WhenAll(lightsTask, connectivityTask);

            var lights = await lightsTask;
            ApplyConnectivityStatus(lights, await connectivityTask);
            return lights;
        }

        public async Task<LightResource> GetLightAsync(string lightId)
        {
            var lightsTask = GetResourcesAsync<LightResource>(
                $"light/{Uri.EscapeDataString(lightId)}");
            var connectivityTask = GetConnectivityResourcesAsync();
            await Task.WhenAll(lightsTask, connectivityTask);

            var light = (await lightsTask).FirstOrDefault()
                ?? throw new InvalidDataException($"Hue response did not contain light '{lightId}'.");
            ApplyConnectivityStatus([light], await connectivityTask);
            return light;
        }

        public Task<List<GroupedLightResource>> GetGroupedLightsAsync() =>
            GetResourcesAsync<GroupedLightResource>("grouped_light");

        public async Task<GroupedLightResource> GetGroupedLightAsync(string groupedLightId)
        {
            var groupedLights = await GetResourcesAsync<GroupedLightResource>(
                $"grouped_light/{Uri.EscapeDataString(groupedLightId)}");
            return groupedLights.FirstOrDefault()
                ?? throw new InvalidDataException($"Hue response did not contain grouped light '{groupedLightId}'.");
        }

        public async Task<BridgeData> GetRoomsAsync()
        {
            var roomsTask = GetResourcesAsync<RoomResource>("room");
            var lightsTask = GetLightsAsync();
            var scenesTask = GetResourcesAsync<SceneResource>("scene");
            var groupedLightsTask = GetGroupedLightsAsync();
            await Task.WhenAll(roomsTask, lightsTask, scenesTask, groupedLightsTask);

            return new BridgeData(
                await roomsTask,
                await lightsTask,
                await scenesTask,
                await groupedLightsTask);
        }

        private async Task<List<ConnectivityResource>> GetConnectivityResourcesAsync()
        {
            const string resource = "zigbee_connectivity";
            try
            {
                var connectivityResources = await GetResourcesAsync<ConnectivityResource>(resource);
                ConnectivityReadWarning = null;
                return connectivityResources;
            }
            catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                ConnectivityReadWarning = $"the bridge returned 503 for /clip/v2/resource/{resource}.";
                return [];
            }
        }

        private static void ApplyConnectivityStatus(
            IEnumerable<LightResource> lights,
            IEnumerable<ConnectivityResource> connectivityResources)
        {
            var statusByDevice = CreateConnectivityStatusByDevice(connectivityResources);
            foreach (var light in lights)
            {
                if (light.Owner is { ResourceId.Length: > 0 } owner &&
                    statusByDevice.TryGetValue(owner.ResourceId, out var status))
                {
                    light.ConnectivityStatus = status;
                }
            }
        }

        private static Dictionary<string, string> CreateConnectivityStatusByDevice(
            IEnumerable<ConnectivityResource> connectivityResources)
        {
            var statusByDevice = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var connectivity in connectivityResources)
            {
                if (connectivity.Owner is { ResourceId.Length: > 0 } owner &&
                    !string.IsNullOrEmpty(connectivity.Status))
                {
                    statusByDevice.TryAdd(owner.ResourceId, connectivity.Status);
                }
            }

            return statusByDevice;
        }

        public async Task SetLampStateAsync(string lightId, bool isOn)
        {
            using var response = await _httpClient.PutAsJsonAsync(
                $"clip/v2/resource/light/{Uri.EscapeDataString(lightId)}",
                new { on = new { on = isOn } });
            response.EnsureSuccessStatusCode();
        }

        public async Task SetGroupedLightStateAsync(string groupedLightId, bool isOn)
        {
            using var response = await _httpClient.PutAsJsonAsync(
                $"clip/v2/resource/grouped_light/{Uri.EscapeDataString(groupedLightId)}",
                new { on = new { on = isOn } });
            response.EnsureSuccessStatusCode();
        }

        public async Task SetLampBrightnessAsync(string lightId, int brightness)
        {
            using var response = await _httpClient.PutAsJsonAsync(
                $"clip/v2/resource/light/{Uri.EscapeDataString(lightId)}",
                new { dimming = new { brightness } });
            response.EnsureSuccessStatusCode();
        }

        public async Task SetLampColorAsync(string lightId, XyState color)
        {
            using var response = await _httpClient.PutAsJsonAsync(
                $"clip/v2/resource/light/{Uri.EscapeDataString(lightId)}",
                new { color = new { xy = new { x = color.X, y = color.Y } } });
            response.EnsureSuccessStatusCode();
        }

        public async Task SetLampColorTemperatureAsync(string lightId, int mirek)
        {
            using var response = await _httpClient.PutAsJsonAsync(
                $"clip/v2/resource/light/{Uri.EscapeDataString(lightId)}",
                new { color_temperature = new { mirek } });
            response.EnsureSuccessStatusCode();
        }

        public async Task ActivateSceneAsync(string sceneId)
        {
            using var response = await _httpClient.PutAsJsonAsync(
                $"clip/v2/resource/scene/{Uri.EscapeDataString(sceneId)}",
                new { recall = new { action = "active" } });
            response.EnsureSuccessStatusCode();
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }

        private bool ValidateCertificate(
            HttpRequestMessage? request,
            X509Certificate2? certificate,
            X509Chain? chain,
            SslPolicyErrors errors)
        {
            if (certificate is null)
            {
                return false;
            }

            var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
            if (_trustedFingerprint is not null)
            {
                if (!string.Equals(fingerprint, _trustedFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    RejectedCertificateFingerprint ??= fingerprint;
                    return false;
                }

                return true;
            }

            if (errors == SslPolicyErrors.None)
            {
                return true;
            }

            RejectedCertificateFingerprint ??= fingerprint;
            return false;
        }

        private async Task<List<T>> GetResourcesAsync<T>(string resource)
        {
            using var response = await _httpClient.GetAsync($"clip/v2/resource/{resource}");
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Hue request to '/clip/v2/resource/{resource}' failed with HTTP " +
                    $"{(int)response.StatusCode} ({response.ReasonPhrase}).",
                    null,
                    response.StatusCode);
            }

            var result = await response.Content.ReadFromJsonAsync<HueApiResponse<T>>();
            return result?.Data
                ?? throw new InvalidDataException($"Hue response for '{resource}' did not contain a data array.");
        }

    }

    private sealed record BridgeData(
        List<RoomResource> Rooms,
        List<LightResource> Lights,
        List<SceneResource> Scenes,
        List<GroupedLightResource> GroupedLights);

    private sealed class HueApiResponse<T>
    {
        [JsonPropertyName("data")]
        public List<T>? Data { get; set; }
    }

    private sealed class RoomResource
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("metadata")]
        public RoomMetadata? Metadata { get; set; }

        [JsonPropertyName("children")]
        public List<ResourceReference>? Children { get; set; }
    }

    private sealed class SceneResource
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("group")]
        public ResourceReference? Group { get; set; }

        [JsonPropertyName("metadata")]
        public SceneMetadata? Metadata { get; set; }
    }

    private sealed class GroupedLightResource
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("owner")]
        public ResourceReference? Owner { get; set; }

        [JsonPropertyName("on")]
        public OnState? State { get; set; }
    }

    private sealed class SceneMetadata
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class RoomMetadata
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class ResourceReference
    {
        [JsonPropertyName("rid")]
        public string ResourceId { get; set; } = string.Empty;

        [JsonPropertyName("rtype")]
        public string ResourceType { get; set; } = string.Empty;
    }

    private sealed class LightResource
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonIgnore]
        public string? ConnectivityStatus { get; set; }

        [JsonPropertyName("owner")]
        public ResourceReference? Owner { get; set; }

        [JsonPropertyName("metadata")]
        public LightMetadata? Metadata { get; set; }

        [JsonPropertyName("dimming")]
        public DimmingState? Dimming { get; set; }

        [JsonPropertyName("on")]
        public OnState? State { get; set; }

        [JsonPropertyName("color")]
        public ColorState? Color { get; set; }

        [JsonPropertyName("color_temperature")]
        public ColorTemperatureState? ColorTemperature { get; set; }
    }

    private sealed class ConnectivityResource
    {
        [JsonPropertyName("owner")]
        public ResourceReference? Owner { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }
    }

    private sealed class LightMetadata
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class DimmingState
    {
        [JsonPropertyName("brightness")]
        public double? Brightness { get; set; }
    }

    private sealed class OnState
    {
        [JsonPropertyName("on")]
        public bool IsOn { get; set; }
    }

    private sealed class ColorState
    {
        [JsonPropertyName("xy")]
        public XyState? Xy { get; set; }
    }

    private sealed class ColorTemperatureState
    {
        [JsonPropertyName("mirek")]
        public int? Mirek { get; set; }

        [JsonPropertyName("mirek_valid")]
        public bool? MirekValid { get; set; }

        [JsonPropertyName("mirek_schema")]
        public MirekSchema? Schema { get; set; }
    }

    private sealed class MirekSchema
    {
        [JsonPropertyName("mirek_minimum")]
        public int? MirekMinimum { get; set; }

        [JsonPropertyName("mirek_maximum")]
        public int? MirekMaximum { get; set; }
    }

    private sealed class XyState
    {
        [JsonPropertyName("x")]
        public double? X { get; set; }

        [JsonPropertyName("y")]
        public double? Y { get; set; }
    }

    private sealed class LampSliderState(string lightId, bool canDim, TextBlock valueText)
    {
        public string LightId { get; } = lightId;
        public bool CanDim { get; } = canDim;
        public TextBlock ValueText { get; } = valueText;
    }

    private sealed class LampColorState(string lightId)
    {
        public string LightId { get; } = lightId;
    }

    private sealed class LampColorTemperatureState(string lightId)
    {
        public string LightId { get; } = lightId;
    }

    private sealed class LampToggleState(string lightId)
    {
        public string LightId { get; } = lightId;
    }

    private sealed record LampControlElements(
        ToggleSwitch Toggle,
        Slider BrightnessSlider,
        TextBlock ReachabilityText,
        Button? ColorButton);

    private sealed record SceneButtonState(string SceneId, string Name);

    private sealed class DelegateCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}
