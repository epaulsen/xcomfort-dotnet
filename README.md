# xcomfort-dotnet
Dotnet library for working with XComfort Bridge

## Web application

Requires the .NET 10 SDK. Run the minimal API without containers:

```sh
dotnet run --project src/XComfortNet.Web
```

The API references the XComfortNet library. `GET /` identifies the application;
`GET /health` reports application health. Bridge connections, MQTT publishing,
and Home Assistant discovery are not implemented yet.

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
variables, ready for a future MQTT client. Inside the container network, the
broker is reachable at `mqtt:1883`.

Open Home Assistant through its dashboard endpoint and complete onboarding.
Then add the MQTT integration using broker `mqtt`, port `1883`, and no
credentials. This initial setup is manual; no entities are exposed yet.
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
