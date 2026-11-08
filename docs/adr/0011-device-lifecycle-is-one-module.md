# A device's lifecycle is one module

Status: proposed

## Context

The daemon has ten device classes (Camera, Telescope, Focuser, FilterWheel, Rotator, Dome, Switch, FlatDevice, SafetyMonitor, ObservingConditions), and each writes its own connect, disconnect, forget, connect supersede, refresh loop and connection-state publish. A fix reaches only the devices someone remembers: `DeviceHealthWatch` covers four of the ten, and its own comment says the probe alone misses a bridge that answers Connected while every read fails. Telescope has its own "clean read since connect" gate, special-cased by type in `FaultReactionService`. Each class builds its Alpaca client inline, so a lifecycle test has to dial a dead port or run a simulator.

## Decision

We decided (2026-11-08, [#1696](https://github.com/open-astro/openastro-ara/issues/1696)) that a device's lifecycle is one module held by each device class. The module owns connect, disconnect, forget, supersede, the refresh loop, the probe, read health, the clean-read gate and the state publish. A device class gives it a device adapter, names its essential read and states how many failed reads make it unhealthy. All ten devices get the same probe, health and clean-read rules. SafetyMonitor keeps its threshold of one failed read, so a dead monitor is never read as safe for longer than it is today. The migration runs Rotator and Dome first, then the six small devices, then Telescope, then Camera; the unused Placeholder device classes go in the first slice.

## Rules

1. A device class holds the lifecycle module and supplies only its device adapter, its essential read and its failed-read threshold. Scope: `OpenAstroAra.Server/Services/RotatorService.cs`, `OpenAstroAra.Server/Services/DomeService.cs`, `OpenAstroAra.Server/Services/FocuserService.cs`, `OpenAstroAra.Server/Services/FilterWheelService.cs`, `OpenAstroAra.Server/Services/FlatDeviceService.cs`, `OpenAstroAra.Server/Services/SwitchService.cs`, `OpenAstroAra.Server/Services/SafetyMonitorService.cs`, `OpenAstroAra.Server/Services/ObservingConditionsService.cs`, `OpenAstroAra.Server/Services/TelescopeService.cs`, `OpenAstroAra.Server/Services/CameraService.cs`. Checked by: review (a migrated device class that writes its own connect generation, refresh timer or disconnect ordering).
2. Every device gets the probe, the read-health check and the clean-read gate from the module, and no reaction depends on the device's type to read them. Scope: `OpenAstroAra.Server/Services/DeviceHealthWatch.cs`, `OpenAstroAra.Server/Services/FaultReactionService.cs`. Checked by: review (a per-`DeviceType` check on health or clean read, or a device that opts out of one).
3. SafetyMonitor's failed-read threshold is one, and a failed read reports the monitor as not safe. Scope: `OpenAstroAra.Server/Services/SafetyMonitorService.cs`. Checked by: review (a threshold above one, or a failed read that leaves a safe verdict standing).
4. An Alpaca client is built only by a device adapter, and the lifecycle module is tested against a fake device. Scope: `OpenAstroAra.Server/Services/*Service.cs`. Checked by: review (a `new Alpaca` device client outside an adapter, or a lifecycle test that dials a port).

## Alternatives rejected

- **A shared base class**: less code to move, but the lifecycle's interface becomes every protected member, and a test has to build a whole device class to reach it.
- **Keep today's health gaps**: a pure refactor with no new faults, but it keeps the hole that the health check exists to close on six devices.
- **Only the eight small devices**: avoids the two hardest classes, but leaves two more copies of the lifecycle where the most fixes land.
- **One failed-read threshold for all ten**: simpler, but SafetyMonitor demotes on the first failed read today, and three reads would delay an unsafe verdict.

## Consequences

- A lifecycle fix reaches every device, and it is tested once against a fake.
- Dome, FlatDevice, ObservingConditions, Switch and SafetyMonitor can raise faults they never raised before, which users will see as new degraded states.
- Alpaca stays the one production adapter (ADR 0003); the second adapter is the test fake.
- The reconnector and auto-connect can address a device through the module and drop their `DeviceType` tables.

## How to reverse

Copy the module's behaviour back into each device class and rebuild each class's Alpaca client inline. Tests written against the fake device stop applying and have to be rewritten against a port or a simulator.

## Links

- [#1696](https://github.com/open-astro/openastro-ara/issues/1696)
- [#1246](https://github.com/open-astro/openastro-ara/issues/1246) (recovered only after a clean read)
- [`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §42 (hardware fault recovery)
- ADR 0003
