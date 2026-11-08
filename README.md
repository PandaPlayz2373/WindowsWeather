<p align="center"><img src="brand/logo-512.png" width="160" alt="Windows Weather logo"></p>

# Windows Weather

The weather outside, on your Windows desktop. When it snows where you are, snow falls across your screen and piles
up on the tops of your windows and the taskbar. Rain falls from the clouds and collects in puddles. Clouds drift
across the top of the screen at their real height, fog thickens with the real visibility, the sun catches the top
edges of your windows, and in autumn leaves fall.

It all reacts to you: drag a window fast and the snow flies off, resize it and the snow breaks up and falls, sweep
the cursor through a cloud and it drifts away. Clicks always go straight through to your windows.

## Download

**[Download the latest WindowsWeather.exe](https://github.com/PandaPlayz2373/WindowsWeather/releases/latest)** (one
file, about 8 MB, nothing to install).

1. Put `WindowsWeather.exe` anywhere you like (Documents is fine) and double-click it.
2. Windows may show **"Windows protected your PC"** because the app is new and not code-signed. Click
   **More info**, then **Run anyway**. Some antivirus programs also check a new app for a few minutes the first
   time it runs.
3. A small window icon appears in the tray (bottom-right, sometimes behind the **^** arrow). Click it for the
   menu. Can't find the icon? Double-click `WindowsWeather.exe` again and the settings open. It starts with Windows
   from then on; switch that off in the menu if you prefer.

Needs Windows 10 (version 2004 or later) or Windows 11, 64-bit.

## What it shows

| Outside | On your desktop |
|---|---|
| Snow | Flakes fall and pile up on window tops, the taskbar and notification pop-ups; heavier snowfall means more flakes, deeper snow on the ground means taller piles |
| Rain, drizzle, storms | Rain falls from the clouds, splashes, and pools into puddles on window tops; storms add lightning strikes that fork down from the clouds and knock the snow and water off whatever they hit |
| Hail | Pellets that bounce off window tops |
| Clouds | Low, mid and high clouds drawn at their real heights; the cursor pushes them aside and they drift back together |
| Fog | A soft layer over the whole screen, as thick as the real visibility |
| Sun | Light along the top edges of your windows, brightest on the side facing the real sun |
| Sunset | Clouds lit gold, pink and violet from the inside as the real sun sets |
| Freezing and humid | Frost crystals slowly grow in from the edges of the screen |
| Autumn | Leaves tumble down, more when it is windy, and settle on your windows |
| Clear night | The real moon, in its real phase and place; an aurora on nights strong enough to see one from where you are |
| Wind | Gusts push the snow, rain and leaves, and blow snow off the edges |
| Poor air quality | A faint haze |


**Disasters** (pick them in the menu; they never come on by themselves). They really move your windows, and always put
them back: a few seconds after it's over every window glides back to exactly where it was, and straight away if you
pause or quit. Maximized and always-on-top windows are left alone (except that a big quake knocks maximized windows out
of maximized for a moment), and if you grab a window yourself it's yours again.

| Disaster | What it does |
|---|---|
| Tornado | A funnel drops out of the clouds and crosses the screen, picks up your windows, swings them around and throws them, and tears the snow, puddles and leaves off everything it crosses |
| Earthquake | A rumble, then the main shock: windows shake and walk, snow and leaves are shaken off, your taskbar icons jump off the taskbar, and a strong one cracks the screen (for a few seconds) |
| Flood | Water rises from the bottom of the screen; windows float up and bob on it, taskbar icons float away, then it drains and everything settles back |

Every effect has its own sliders in the effect picker: lightning frequency and strength for storms, density and height
for clouds, flake size and pile height for snow, size, speed and power for the tornado, and so on. The menu also lets you
preview any effect, adjust how much snow, rain, leaves and clouds you get, set a city by hand,
scrub through the time of day, and see what the app is costing your computer right now.

## Light on your computer

Measured on a laptop with an Intel Core Ultra 7 (8 cores), snowing at 30 frames a second:

| | CPU (share of the whole laptop) | Memory |
|---|---|---|
| Snowing | about 0.4% | 10 to 40 MB |
| Snowing on battery (20 fps) | about 0.3% | |
| Sun glow, nothing moving | close to 0 (it stops drawing) | |
| Paused | about 0.01% | |

It draws with the graphics card through DirectComposition, stops drawing entirely whenever nothing on screen is
moving, and pauses by itself for full-screen games and videos, when the screen is locked or off, and on battery
saver.

## Privacy

There are no accounts, no ads and no analytics. To show your local weather the app sends your approximate location
to these free services, and nothing else (full details in the [privacy policy](PRIVACY.md); switch **Use online
weather** off in settings and it makes no network requests at all):

- **Your location**: Windows location services if you allow them, otherwise an approximate location from your IP
  address ([ipwho.is](https://ipwho.is), [ipapi.co](https://ipapi.co)). You can type a city instead in the menu.
- **Place name**: [BigDataCloud](https://www.bigdatacloud.com) reverse geocoding.
- **Weather and air quality**: [Open-Meteo](https://open-meteo.com), every 15 minutes.
- **Aurora index**: [NOAA Space Weather Prediction Center](https://www.swpc.noaa.gov).

Settings and a small log are kept in `%APPDATA%\WindowsWeather` on your computer.

## Uninstall

Click the tray icon, open settings (the gear) and choose **Quit**. Then delete `WindowsWeather.exe` and the folder
`%APPDATA%\WindowsWeather`. Turn off **Start with Windows** in settings first if you want the startup entry
removed too, or run [`scripts/uninstall.ps1`](scripts/uninstall.ps1), which does all of it.

## Build it yourself

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download) and Visual Studio Build Tools with the C++ workload
(for Native AOT). Then:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/install.ps1
```

That builds a single native exe, copies it to `%LOCALAPPDATA%\Programs\WindowsWeather` and starts it. The code is in
[`app/`](app): C# with Direct2D, DirectWrite and DirectComposition through
[Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows), published with Native AOT.

## Credits

Weather and air-quality data by [Open-Meteo.com](https://open-meteo.com) (CC BY 4.0). Aurora data from NOAA SWPC.
Place names by BigDataCloud.

## Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

- Every release is built by [GitHub Actions](.github/workflows/build.yml) from the source in this repository, and only
  those builds are signed.
- Committers and reviewers: [PandaPlayz2373](https://github.com/PandaPlayz2373)
- Approvers (each signed release is approved by hand): [PandaPlayz2373](https://github.com/PandaPlayz2373)
- Privacy: see the [privacy policy](PRIVACY.md). The program only contacts the services listed there, and only to
  show the weather; it can be switched to fully offline.

## License

[MIT](LICENSE)
