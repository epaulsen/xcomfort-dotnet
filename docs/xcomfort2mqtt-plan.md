# Xcomfort2Mqtt implementation plan

## Goal and scope

Turn `XComfortNet.Web` into a standalone bridge between xComfort and Home
Assistant, using MQTT and Home Assistant discovery. The operating model is
similar to Zigbee2MQTT: discover devices, publish state and events, and translate
MQTT commands into device commands.

Keep `XComfortNet` independent of MQTT, ASP.NET Core, and Home Assistant.
Planning is complete enough to define the first milestone; implementation has
not started.

Initial scope:

- Local communication with one xComfort Bridge.
- Read-only inventory and state before introducing control.
- Sensors first, then lights and appliance switches, followed by other platforms.
- Configuration and health endpoints, but no frontend yet.
- No device pairing, firmware updates, or replacement of the Eaton configuration
  application.

## Reference implementation

The Python integration is available in the authorized local checkout:

`/Users/erling.paulsen/code/personal/ha-xcomfort-bridge-fork`

Its implementation has three layers:

| Layer | Responsibility |
| --- | --- |
| Connection | WebSocket framing, RSA/AES, authentication, session tokens, message counters, and ACK |
| xComfort model | Components, device channels, rooms, scenes, state updates, and bridge commands |
| Home Assistant adapters | Entity metadata, value conversion, lifecycle, control, and some domain behavior |

Important references:

- [Connection and authentication](/Users/erling.paulsen/code/personal/ha-xcomfort-bridge-fork/custom_components/xcomfort_bridge/xcomfort/connection.py)
- [Inventory and message handling](/Users/erling.paulsen/code/personal/ha-xcomfort-bridge-fork/custom_components/xcomfort_bridge/xcomfort/bridge.py)
- [Climate behavior](/Users/erling.paulsen/code/personal/ha-xcomfort-bridge-fork/custom_components/xcomfort_bridge/climate.py)
- [Button gestures](/Users/erling.paulsen/code/personal/ha-xcomfort-bridge-fork/custom_components/xcomfort_bridge/event.py)
- [Sensor and energy behavior](/Users/erling.paulsen/code/personal/ha-xcomfort-bridge-fork/custom_components/xcomfort_bridge/sensor.py)

Use Python as a behavioral reference, not as a class-for-class template.
Retain the required Apache-2.0 license notices and attribution when porting
source-derived behavior.

## Findings that shape the port

### Inventory and identity

- An xComfort component represents physical hardware.
- An xComfort device represents a logical channel or function. One component
  can have several devices.
- A room aggregates state and may provide climate control.
- A scene invokes a predefined action on the Bridge.

Represent a physical component as one Home Assistant device, with entities for
its channels and measurements. Represent rooms and the Bridge separately.
Avoid duplicating battery, signal, temperature, or humidity entities across
channels of the same component.

The Python device classification depends on component type, device type, and
usage. A switching actuator is not necessarily a light. The enum catalogue is
larger than the functionality actually implemented by the integration.

### Snapshot and incremental updates

The connection requests initial data with messages 240 and 242 and sends message
2 during initialization. Initial inventory arrives through `SET_ALL_DATA`
(300), with a `lastItem` completion marker. Bridge metadata arrives separately
through message 303.

Device state arrives through message 291 and batched state updates through
message 310, among other supported messages.

The .NET implementation must:

- Distinguish an established connection from a fully loaded inventory.
- Process all data in a snapshot message before declaring it complete.
- Handle late metadata, component records, sensor channels, and room climate
  configuration.
- Merge partial updates without resetting fields omitted from a message.
- Distinguish unknown values from valid zero, false, or off values.
- Preserve unfamiliar fields and tolerate unknown message and device types.
- Reconcile snapshots after reconnect without duplicating entities or events.

Only remove entities after explicit deletion or a successfully completed,
authoritative inventory reconciliation, not because the Bridge is unavailable.

### Device-specific behavior

- Dimmer brightness uses the range 0-99.
- Shade position is inverted relative to Home Assistant.
- Shade commands must respect reported safety state and supported capabilities.
- Multisensor pushbuttons can expose a separate temperature/humidity channel
  belonging to the same component.
- RC Touch has a virtual rocker relationship that requires explicit handling.
- Measurements and diagnostics can be encoded in `info[]`, not just direct
  payload properties.
- Button events must not be generated from initial snapshots or replayed state.
- The Python integration deduplicates rapid button updates and derives double
  presses in the Home Assistant adapter.
- Climate entities are room-based and may only become discoverable after a
  later room-heating update.

### Behavior outside the Python protocol package

Do not port only the `xcomfort` package:

- Climate commands have behavior in the Home Assistant climate adapter that is
  not reliably represented by the existing Python room methods.
- HVAC transitions use different payloads from preset and setpoint changes.
- Some preset changes require multiple commands.
- Single/double-press recognition resides in the event adapter.
- Energy sensors integrate reported power and restore totals through Home
  Assistant. These are calculated values, not necessarily hardware energy
  readings.

Move protocol and device behavior into the .NET library. Keep Home
Assistant-specific representation in the MQTT/discovery layer.

## Existing .NET building blocks

Reuse and harden the existing WebSocket, cryptography, handshake, and message
identifier implementations.

The existing model classes are primarily protocol DTOs. Keep that role separate
from domain state and MQTT payloads:

| Model category | Examples |
| --- | --- |
| Protocol DTO | Authentication, initial data, device update, command payload |
| Domain model | Component, channel, room, scene, normalized state, button event |
| MQTT/discovery model | Discovery configuration, state payload, command payload |

Before expanding functionality, establish coverage for payload serialization,
framing, ACK/NACK, cancellation, disposal, reconnect, and snapshot readiness.
The existing test project currently contains only an empty placeholder test.

Expose a usable inventory snapshot, typed state/events, and device commands.
The library should do more than log received protocol messages.

## Proposed architecture

```text
xComfort Bridge
       |
       | WebSocket
       v
XComfortNet
  connection lifecycle
  protocol DTOs and message handling
  inventory and normalized domain state
  events and commands
       |
       | snapshot / state / events / commands
       v
XComfortNet.Web
  background services
  MQTT connection and command routing
  Home Assistant discovery mappings
  configuration and health endpoints
       |
       | MQTT
       v
Mosquitto <-> Home Assistant
```

Keep the existing production projects initially; no extra architectural
projects are required for the first milestone.

Use MQTTnet as the proposed MQTT client. Bridge and MQTT reconnect lifecycles
must be independent. Preserve the latest state while MQTT is unavailable, but
do not replay stale control commands or old button events after reconnect.

The runtime service must not require a Home Assistant access token. A local
token may be used separately for integration verification.

## MQTT and discovery contract

Use Home Assistant MQTT device discovery, with one retained discovery payload
per physical component and multiple entities in that payload. Include the
required device and origin metadata.

Proposed topic structure:

```text
homeassistant/device/xcomfort2mqtt_<bridge>_<component>/config
xcomfort2mqtt/<bridge>/device/<device>/state
xcomfort2mqtt/<bridge>/device/<device>/set
xcomfort2mqtt/<bridge>/device/<device>/event
xcomfort2mqtt/<bridge>/room/<room>/state
xcomfort2mqtt/<bridge>/availability
xcomfort2mqtt/availability
```

Finalize exact command schemas and identifiers before the first published
entities.

Contract requirements:

- Stable entity IDs derived from stable Bridge, component/channel, and function
  identifiers. Do not derive identity from names or IP addresses.
- Retained discovery and latest state; non-retained commands and button events.
- Service availability via MQTT Last Will, plus explicit Bridge availability.
  An entity should require both to be available.
- Rediscovery and state publication on MQTT reconnect and Home Assistant birth.
- Removal of obsolete retained discovery configurations, including across
  service restarts.
- Validation of command targets, payloads, and supported capabilities.
- No automatic replay of control commands after connection failures.
- Prefer Bridge-reported state over optimistic local state. A protocol ACK alone
  does not prove that physical equipment reached the requested state.
- Correct units, device classes, state classes, and unknown-value handling.

Proposed entity mapping:

| xComfort function | Home Assistant MQTT platform |
| --- | --- |
| Light/dimmer | light |
| Appliance/load | switch |
| Temperature, humidity, power, valve position, diagnostics | sensor |
| Door/window contact | binary_sensor |
| Pushbutton/remote channel | event |
| Shade/blind | cover |
| Room heating | climate |
| Bridge scene | scene |

Expose only capabilities confirmed by the inventory and implemented protocol
handling. Do not advertise cooling or advanced functions merely because their
constants exist.

MQTT discovery does not automatically migrate entities or automations from the
Python custom integration. Treat any production migration as separate work.

References:

- [MQTT device discovery](https://www.home-assistant.io/integrations/mqtt/#device-discovery-payload)
- [MQTT event entities](https://www.home-assistant.io/integrations/event.mqtt/)

## Live-test safety and configuration

A test Bridge must not be assumed isolated from physical equipment merely
because it is a clone or is used in a test environment.

For live testing:

- Start with read-only inventory and state collection.
- Lighting commands are permitted for verification.
- Obtain explicit approval before each heating control action, including
  changes through room control, presets, or scenes that affect heating.
- Treat unknown devices and scenes with unknown effects as read-only.
- Prevent automated tests and command routing from bypassing these restrictions.
- Use an isolated MQTT broker. If sharing infrastructure later, namespace both
  topics and discovery/device/entity identifiers to avoid collisions between
  cloned installations.

Keep Bridge addresses, authentication keys, and Home Assistant tokens in local,
Git-ignored configuration. Do not include them in this plan or test fixtures.
Sanitize captured protocol traffic before using it as test data.

## Implementation milestones

### 1. Protocol foundation

- Stabilize connection, authentication, framing, counters, ACK/NACK, and cleanup.
- Introduce bounded initialization and a single controlled reconnect lifecycle.
- Capture representative, anonymized protocol messages from a live Bridge.
- Add focused tests for serialization, fragmented frames, malformed/unknown
  messages, cancellation, and snapshot completion.

Exit condition: a live Bridge can be read reliably, disconnected, and
reconnected without leaked connections, duplicate consumers, or false events.

### 2. Read-only domain model

- Model components, channels, rooms, scenes, capabilities, and normalized state.
- Support inventory plus incremental updates and late-arriving relationships.
- Separate state changes from transient events.
- Keep unavailable/unknown values distinct from valid readings.

Exit condition: a stable inventory and changing measurements are accessible
through the library, independent of Home Assistant and MQTT.

### 3. First vertical MQTT milestone

- Publish discovery, state, and availability for one representative
  temperature/humidity sensor.
- Verify automatic discovery and updates through the Aspire environment.
- Verify stable identity and correct availability across application, broker,
  Home Assistant, and Bridge restarts.

Exit condition: one real sensor works end to end in Home Assistant without
manual entity configuration.

### 4. Bidirectional lighting control

- Implement light on/off and dimming with command validation and state feedback.
- Verify Home Assistant -> MQTT -> Bridge -> reported state -> Home Assistant.
- Add appliance switches to the protocol and mapping, but only exercise
  explicitly permitted physical targets.
- Reject stale retained commands and commands to unsupported or disallowed
  targets.

Exit condition: one permitted light can be controlled reliably, with physical
state changes reflected back into Home Assistant.

### 5. Extended platform support

- Button events and gesture handling without startup/reconnect replay.
- Door/window contacts.
- Shades with capability and safety handling.
- Room climate with dedicated tests for mode, preset, setpoint, and command
  sequencing.
- Scene activation and lifecycle, subject to live-test safety restrictions.

Exit condition: each supported platform has tested mappings and command
semantics, rather than only a defined enum value.

### 6. Operational completeness

- Diagnostics and optional power sensors.
- A separately defined energy integration/persistence strategy, if needed.
- Discovery reconciliation, deletion, and reconnect behavior.
- Health reporting, useful diagnostics, and redacted logging.

Do not count service downtime as measured energy consumption or publish an
invented cumulative total after restart.

## Validation strategy

- Unit tests for protocol handling, partial-state merges, classifications,
  conversions, IDs, discovery payloads, and command generation.
- A simulated Bridge for repeatable normal, failure, and reconnect scenarios.
- Aspire integration tests with real Mosquitto and Home Assistant.
- Live read-only verification against physical equipment.
- Live lighting control for the first bidirectional milestone.
- Explicit approval before any live heating command.

Key scenarios include out-of-order inventory, late metadata, duplicate updates,
unknown readings, entity deletion, restart recovery, and absence of phantom
button events.

## Decisions to finalize during implementation

- Select the first sensor and permitted lighting target from read-only inventory.
- Freeze MQTT schemas and the stable identity convention before publishing.
- Define initial heating capabilities from actual room configuration; do not
  assume cooling support.
- Decide whether energy accumulation belongs in this service or is left to Home
  Assistant initially.

The first implementation target is one sensor end to end, followed by one
controllable light. Frontend work and production migration remain outside that
first delivery.
