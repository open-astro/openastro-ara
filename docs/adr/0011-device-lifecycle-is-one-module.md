# A device's lifecycle is one module

Status: proposed

## Context

The daemon has ten device classes (Camera, Telescope, Focuser, FilterWheel, Rotator, Dome, Switch, FlatDevice, SafetyMonitor, ObservingConditions), and each writes its own connect, disconnect, forget, connect supersede, refresh loop and connection-state publish. A fix reaches only the devices someone remembers: `DeviceHealthWatch` covers four of the ten, and its own comment says the probe alone misses a bridge that answers Connected while every read fails. Telescope has its own "clean read since connect" gate, special-cased by type in `FaultReactionService`. Each class builds its Alpaca client inline, so a lifecycle test has to dial a dead port or run a simulator.

## Decision

We decided (2026-11-08, [#1696](https://github.com/open-astro/openastro-ara/issues/1696)) that a device's lifecycle is one module held by each device class. The module owns connect, disconnect, forget, supersede, the refresh loop, the probe, read health, the clean-read gate and the state publish. A device class gives it a device adapter, names its essential read and states how many failed reads make it unhealthy. All ten devices get the same probe, health and clean-read rules. SafetyMonitor keeps its threshold of one failed read, so a dead monitor is never read as safe for longer than it is today. The migration runs Rotator and Dome first, then the six small devices, then Telescope, then Camera; the unused Placeholder device classes go in the first slice.

We decided (2026-11-12, [#1808](https://github.com/open-astro/openastro-ara/issues/1808)) that a command the Sequencer waits on is one module too. It owns the off-thread call, the hard timeout, the settle poll, the stall or fault verdict, the fault publish and the abandoned call. A device class declares its timeout values and its terminal condition. An unreadable read never counts as done; a device whose only signal is its motion flag declares that flag optional, which Focuser and Rotator do. A command with nothing to wait for, such as the mount's Sync or a Switch write, goes through the module with no terminal condition. Command faults do not count against read health. Camera exposure and moves started over REST are outside it. Rotator pilots it, and the rest follow the lifecycle's order.

## Rules

1. A device class holds the lifecycle module and supplies only its device adapter, its essential read and its failed-read threshold. Scope: `OpenAstroAra.Server/Services/RotatorService.cs`, `OpenAstroAra.Server/Services/DomeService.cs`, `OpenAstroAra.Server/Services/FocuserService.cs`, `OpenAstroAra.Server/Services/FilterWheelService.cs`, `OpenAstroAra.Server/Services/FlatDeviceService.cs`, `OpenAstroAra.Server/Services/SwitchService.cs`, `OpenAstroAra.Server/Services/SafetyMonitorService.cs`, `OpenAstroAra.Server/Services/ObservingConditionsService.cs`, `OpenAstroAra.Server/Services/TelescopeService.cs`, `OpenAstroAra.Server/Services/CameraService.cs`. Checked by: review (a migrated device class that writes its own connect generation, refresh timer or disconnect ordering).
2. Every device gets the probe, the read-health check and the clean-read gate from the module, and no reaction depends on the device's type to read them. Scope: `OpenAstroAra.Server/Services/DeviceHealthWatch.cs`, `OpenAstroAra.Server/Services/FaultReactionService.cs`. Checked by: review (a per-`DeviceType` check on health or clean read, or a device that opts out of one).
3. SafetyMonitor's failed-read threshold is one, and a failed read reports the monitor as not safe. Scope: `OpenAstroAra.Server/Services/SafetyMonitorService.cs`. Checked by: review (a threshold above one, or a failed read that leaves a safe verdict standing).
4. An Alpaca client is built only by a device adapter, and the lifecycle module is tested against a fake device. Scope: `OpenAstroAra.Server/Services/*Service.cs`. Checked by: review (a `new Alpaca` device client outside an adapter, or a lifecycle test that dials a port).
5. A command the Sequencer waits on runs through the command module, and a device class supplies only its timeout values and terminal condition. Scope: `OpenAstroAra.Server/Services/RotatorService.Mediator.cs`, `OpenAstroAra.Server/Services/DomeService.Mediator.cs`, `OpenAstroAra.Server/Services/FocuserService.Mediator.cs`, `OpenAstroAra.Server/Services/FilterWheelService.Mediator.cs`, `OpenAstroAra.Server/Services/SwitchService.Mediator.cs`, `OpenAstroAra.Server/Services/TelescopeService.Mediator.cs`. Checked by: review (a migrated device class with its own timeout race, settle loop, fault publish or abandoned-call handler).
6. An unreadable read never counts as a finished command; only a device that declares its motion flag optional may stop waiting on it. Scope: `OpenAstroAra.Server/Services/RotatorService.Mediator.cs`, `OpenAstroAra.Server/Services/DomeService.Mediator.cs`, `OpenAstroAra.Server/Services/FocuserService.Mediator.cs`, `OpenAstroAra.Server/Services/FilterWheelService.Mediator.cs`, `OpenAstroAra.Server/Services/SwitchService.Mediator.cs`, `OpenAstroAra.Server/Services/TelescopeService.Mediator.cs`. Checked by: review (a settle that ends on an unreadable read for a device with an independent terminal condition).
7. A command fault goes to the fault hub and never changes read health or the clean-read gate. Scope: `OpenAstroAra.Server/Services/DeviceLifecycle.cs`, `OpenAstroAra.Server/Services/RotatorService.Mediator.cs`, `OpenAstroAra.Server/Services/DomeService.Mediator.cs`, `OpenAstroAra.Server/Services/FocuserService.Mediator.cs`, `OpenAstroAra.Server/Services/FilterWheelService.Mediator.cs`, `OpenAstroAra.Server/Services/SwitchService.Mediator.cs`, `OpenAstroAra.Server/Services/TelescopeService.Mediator.cs`. Checked by: review (a command outcome passed to the lifecycle's health check).

## Alternatives rejected

- **A shared base class**: less code to move, but the lifecycle's interface becomes every protected member, and a test has to build a whole device class to reach it.
- **Keep today's health gaps**: a pure refactor with no new faults, but it keeps the hole that the health check exists to close on six devices.
- **Only the eight small devices**: avoids the two hardest classes, but leaves two more copies of the lifecycle where the most fixes land.
- **Settle after five unreadable reads on every device**: one rule with no declaration, but a mount or dome could be reported settled while its motion is unconfirmed.
- **Count a stalled command against read health**: one health verdict per device, but the escalator already acts on repeated command faults and the two would double-count.
- **One failed-read threshold for all ten**: simpler, but SafetyMonitor demotes on the first failed read today, and three reads would delay an unsafe verdict.

## Consequences

- A lifecycle fix reaches every device, and it is tested once against a fake.
- Dome, FlatDevice, ObservingConditions, Switch and SafetyMonitor can raise faults they never raised before, which users will see as new degraded states.
- Alpaca stays the one production adapter (ADR 0003); the second adapter is the test fake.
- A stall or timeout fix reaches every commanded device, and the hard timeout gets a test it never had.
- The reconnector and auto-connect can address a device through the module and drop their `DeviceType` tables.

## How to reverse

Copy the module's behaviour back into each device class and rebuild each class's Alpaca client inline. Tests written against the fake device stop applying and have to be rewritten against a port or a simulator.

## Links

- [#1696](https://github.com/open-astro/openastro-ara/issues/1696)
- [#1808](https://github.com/open-astro/openastro-ara/issues/1808) (the command module)
- [#1246](https://github.com/open-astro/openastro-ara/issues/1246) (recovered only after a clean read)
- [`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §42 (hardware fault recovery)
- ADR 0003
