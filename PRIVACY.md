# Privacy policy

Windows Weather has no accounts, no ads, no analytics and no telemetry. It never sends anything about you or your
computer to the author.

## What it sends, and to whom

To show the weather where you are, it makes these requests. Each one goes straight from your computer to the service
named, over HTTPS:

| What | Sent to | When |
|---|---|---|
| Your location from Windows location services, if you allow them (Settings > Privacy & security > Location) | Stays on your computer; only the coordinates are used in the requests below | At start, then about every 6 hours |
| An approximate location from your IP address, only if Windows location is off | [ipwho.is](https://ipwho.is), or [ipapi.co](https://ipapi.co) as a fallback | At most once a day |
| Coordinates, to get a place name | [BigDataCloud](https://www.bigdatacloud.com) | When the location changes |
| A city you type in the menu, to find it | [Open-Meteo geocoding](https://open-meteo.com) | When you set it |
| Coordinates, for the weather and air quality | [Open-Meteo](https://open-meteo.com) | Every 15 minutes while it is running |
| Nothing about you: a public request for the aurora index | [NOAA Space Weather Prediction Center](https://www.swpc.noaa.gov) | With each weather update |

Those services see the request and your IP address, as any website would. Their own privacy policies apply.

## Turning it off

- **Type a city** in the menu (Location) and no location lookup is made: only the city search and the weather
  request for that city.
- **Switch off "Use online weather"** in the menu's settings and the app makes no network requests at all. You can
  still choose any effect by hand.

## What stays on your computer

Your settings, the last known location and weather, and a small log are kept in `%APPDATA%\WindowsWeather`. Delete
that folder to remove them. If "Start with Windows" is on, the app adds one entry to
`HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`; switching it off removes it.
