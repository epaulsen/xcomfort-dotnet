# xcomfort-dotnet
Dotnet library for working with XComfort Bridge

## Web application

Requires the .NET 10 SDK and an MQTT broker. Put local Bridge credentials in
`src/XComfortNet.Web/appsettings.Development.json` (excluded from Git):

```json
{
  "XComfort": {
    "Host": "<bridge-ip>",
    "AuthKey": "<auth-key>"
  }
}
```

Run the API against an existing broker:

```sh
dotnet run --project src/XComfortNet.Web
```

The broker defaults to `localhost:1883`; override `Mqtt:Host` and `Mqtt:Port`
in local configuration. `GET /` identifies the application; `GET /health`
reports application health. `GET /api/lights` shows Bridge/MQTT connection status
and all verified lights without exposing credentials. `GET /api/light` remains
available for compatibility.

The application reads the full inventory and exposes all verified lights:
actuator type 100/101 with explicit light usage (0). Channels belonging to the
same physical component share a Home Assistant device, with separate light
entities and state/command topics. Switch and
dimmer commands are supported; heating, scenes, rooms, and other devices are
never controlled. Entity identities depend on Bridge/device IDs, not inventory
order. The previous single-light discovery document is retired automatically;
its entity unique ID is preserved.

Discovery, state, and availability are retained under `homeassistant/device/`
and `xcomfort2mqtt/`. Commands use JSON light payloads (`state` and optional
`brightness`, scale 0-99). Retained, malformed, unsupported, or misaddressed
commands are ignored. Home Assistant uses optimistic light control for immediate
on/off and brightness feedback in the UI. Published MQTT state and the API still
contain only Bridge-reported state; reported changes update Home Assistant as
before. If a command fails, the assumed UI state may remain incorrect until a
new state update arrives.
MQTT and Bridge connections retry independently. Removed or reclassified lights
are removed from published discovery when the running service receives updated
inventory; commands are always checked against the current verified inventory.

### Climate, measurements and diagnostics

`GET /api/devices` includes lights, room climates, measurements, and diagnostics.
Only rooms explicitly reported with `temperatureOnly: false` become climate
entities, including when heating configuration arrives after the initial
inventory. Linked thermostat/sensor readings override room measurements.

Climate entities support off/heat, target temperature, and Frost Protection,
eco, and comfort presets. Preset-specific temperature ranges are enforced.
Commands address verified room IDs only; unknown rooms, unsupported commands,
invalid values, and retained commands are rejected. Climate state is never
optimistic. Command sequencing follows the Python integration, including the
Bridge's `confirmed` flag and manual-mode transition when changing presets.

Measurements include reported temperature, humidity, power, heating demand,
and valve position. Diagnostics include device temperature, signal quality,
RSSI where reported, battery buckets (0/25/50/75/100%), and Bridge identity,
firmware, address, and remote-access status. Remote-access diagnostics are
read-only. Physical channels and diagnostics share component devices; room
climate and room measurements share room devices. Unknown readings remain
unknown rather than becoming zero.

Each power sensor also gets **Estimated energy** in kWh. This integrates the
last reported power while the Bridge is connected; it is not a hardware energy
meter. Totals persist in the Git-ignored `src/XComfortNet.Web/data/energy.json`,
saved every 30 seconds and on graceful shutdown. Offline time is not counted;
an abrupt stop can lose the last unsaved interval. Preserve this data directory
when deploying in a container.

Live heating validation in this lab is read-only unless separately authorized.
Protocol command payloads and sequencing are tested without controlling heaters.

## Local integration environment

Requires the .NET 10 SDK and a running Docker-compatible container runtime:

```sh
dotnet run --project test/XComfortNet.AppHost --launch-profile http
```

The Aspire AppHost starts the web application, Eclipse Mosquitto, and Home
Assistant. Open the dashboard URL printed at startup to find their endpoints
and logs. Host ports for the containers are allocated automatically to avoid
conflicts. The HTTP profile is intended for local development only; the HTTPS
profile requires a trusted .NET development certificate.

The web application receives `Mqtt:Host` and `Mqtt:Port` through environment
variables. Inside the container network, the
broker is reachable at `mqtt:1883`.

Open Home Assistant through its dashboard endpoint and complete onboarding.
Then add the MQTT integration using broker `mqtt`, port `1883`, and no
credentials. This initial setup is manual; all verified lights are then
discovered automatically.
Home Assistant configuration persists in the AppHost's `homeassistant` folder,
bind-mounted to `/config`. Generated configuration, credentials, and databases
are excluded from Git. Mosquitto messages persist in a named Docker volume.
Container images track the Home Assistant `stable` and Mosquitto `2` tags.

Existing data in the previous `xcomfort-homeassistant-config` Docker volume is
not deleted or automatically migrated to the folder.

**Local testing only:** Mosquitto allows anonymous, unencrypted connections.
Do not expose these services to untrusted networks or use this configuration
in production. Home Assistant uses a bridge network rather than host
networking, so LAN multicast discovery is not available in this setup.
