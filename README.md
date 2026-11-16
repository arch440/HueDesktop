# Hue Desktop

A minimal unpackaged WinUI 3 app for controlling Hue rooms over the Hue CLIP API v2.

Enter the bridge IP and Hue application key, connect, then use the home power switch, each room's scene buttons, and each lamp's own on/off switch and 1-100% brightness slider. The home power switch operates the Hue Bridge's shared home lighting group, so other Hue clients share its state and restore behavior. After activating a scene, the app refreshes the displayed lamp power and brightness values from the bridge. Lamps reported as disconnected or having a connectivity issue are labeled **Not reachable**, and their controls are disabled. Full-color lamps have a Color button with an RGB picker. Color-temperature-only lamps also have a Color button, with a temperature slider limited to the range reported by that lamp. Apply a selection to update the light. Lamp state is not persisted locally; current power, brightness, and color values are read from the Hue Bridge. **Remember IP and key on this PC** is enabled by default: the IP is stored in the user's Local AppData settings, and the key is stored in Windows Credential Locker, not in the settings file. A saved connection is attempted automatically on the next launch. The window size is also saved locally and restored on the next launch. Uncheck the option and connect to forget the saved connection. After connecting, the connection form collapses; use **Change connection** to reopen it. Room cards group the lamps but do not change them as a group. Brightness is sent when the user finishes dragging the slider or releases a keyboard adjustment. The custom Hue icon appears in the window title bar, taskbar, and system tray. Minimize the window or click its **X** button to hide it in the system tray; click the tray icon or select **Open Hue lights** to restore it. Use **Close** in the tray menu to exit the app. Windows may place the icon under the taskbar's hidden-icons arrow. If Windows does not trust the bridge's HTTPS certificate, the app displays its SHA-256 fingerprint the first time and saves the accepted fingerprint for that bridge IP. A changed certificate triggers a new confirmation.

## Requirements

- .NET 10 SDK
- Windows App SDK 2.6.5
- H.NotifyIcon.WinUI 2.4.1 (restored through the project)
- Windows 10 SDK targeting pack 10.0.19041.0 or later

## Build and run

From the workspace root, restore/build and run:

```powershell
dotnet run --project .\HueDesktop.csproj -c Release
```

`dotnet run` uses the build output under `bin`, which contains the WinUI and .NET runtime dependencies and is not the folder to distribute. To get the clean, self-contained x64 app, publish outside the project directory:

```powershell
dotnet publish .\HueDesktop.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64
```

The publish folder contains the executable and may include a debugging-symbols `.pdb` file. The Hue icon is embedded in the executable and is used by default for its Explorer and Start menu shortcut icon, as well as by the running app. Distribute only `HueDesktop.exe` and keep its name unchanged.

## API behavior

- Uses HTTPS and the `hue-application-key` request header.
- Reads `/clip/v2/resource/room`, `/clip/v2/resource/light`, and `/clip/v2/resource/zigbee_connectivity`; room children and each light's owner associate lamps with rooms and device connectivity.
- Rooms, lamps, scenes, home power, and lamp reachability are refreshed from the bridge every 10 seconds while the window is open. Refreshing pauses when the app is minimized to the system tray and resumes when the window is restored. The layout is rebuilt only when rooms, lamps, or scenes are added, removed, renamed, or reconfigured. If the Zigbee connectivity endpoint returns HTTP 503, the app continues connecting and warns that reachability data is unavailable; other bridge request failures remain errors.
- Changes a lamp's power through `PUT /clip/v2/resource/light/{id}` with `{"on":{"on":true}}` or `{"on":{"on":false}}`.
- Changes a lamp's brightness through `PUT /clip/v2/resource/light/{id}` with `{"dimming":{"brightness":50}}` (range 1-100).
- Changes home-wide power through the `bridge_home`-owned resource at `PUT /clip/v2/resource/grouped_light/{id}`, using the Hue Bridge's shared grouped-light state.
- Changes a color-capable lamp's color through the Hue `color.xy` value, converted from the selected RGB color.
- Changes a color-temperature-only lamp through `PUT /clip/v2/resource/light/{id}` with `{"color_temperature":{"mirek":250}}`, constrained by the lamp's reported mirek schema.
- Lists `/clip/v2/resource/scene` and activates each room scene through `PUT /clip/v2/resource/scene/{id}` with `{"recall":{"action":"active"}}`.
- A bridge certificate that is not system-trusted is rejected until the user confirms its SHA-256 fingerprint. The accepted fingerprint is pinned per bridge IP in Local AppData; a changed certificate triggers a new confirmation.

The bridge API description is in [rest/hue.yaml](./rest/hue.yaml).
