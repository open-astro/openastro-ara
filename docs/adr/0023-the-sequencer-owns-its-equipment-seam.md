# The Sequencer owns its equipment seam, and a missing capability refuses

Status: proposed

## Context

The Sequencer was ported from NINA with NINA's equipment seam: twelve mediator interfaces, each a fat interface (`IDeviceMediator` plus role members, consumers and events), and a `Headless*Mediator` stub for each. On 2026-11-16 the Server implemented nine of them in `*Service.Mediator.cs` partials (4,195 lines), served a tenth (imaging) from the camera service, and ran the last two (flat panel, weather) as stubs in production DI. The Sequencer calls 34 role members and `GetInfo`, and it calls `Rescan`, `Connect` and `Disconnect` by reflection (39 names). The rest of each interface exists only because NINA's WPF shell used it.

The port rules told the port to copy NINA as is and to leave "a placeholder that compiles" where a construct could not be translated ([`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) section 0 rules 2 and 4). The placeholders stayed:
- The Connect instructions matched a profile device id that nothing in the daemon writes, against `Rescan` lists that eight services stub empty. Connect Equipment never found a device, Connect All Equipment connected nothing and reported success, and Disconnect Equipment failed. Meanwhile the Server already had a reconnect path, `IEquipmentReconnector` over the remembered devices (§52.1), used by auto-connect, the REST reconnect and the fault ladder.
- `SetUSBLimit`, `SetDewHeater` and a readout-mode write were empty bodies that the instructions reported as done.
- A move on a device that is not connected returned the cached position, and a mount command on a parked mount returned `false`.

The bugs #1238, #1187, #1123, #1190, #1189 and #1188 were this one class: the Sequencer acted on a value that stood for "nothing happened".

## Decision

We decided (2026-11-16, [#1545](https://github.com/open-astro/openastro-ara/issues/1545)) that the Sequencer's equipment seam is Ara's own and departs from NINA's.

The Sequencer declares one narrow interface per device role, holding only what the Sequencer calls. The Server service that owns the device's lifecycle implements it directly: the same singleton serves the REST interface and the role interface, with no adapter class and no all-device interface. The Connect instructions use one connections port keyed by role. The Server's reconnector implements that port, so a Sequencer connect finds the same remembered device as auto-connect and the REST reconnect, a Sequencer disconnect is a user disconnect, and no device class gets connect logic of its own ([0011](0011-device-lifecycle-is-one-module.md) rule 1). Code outside the Sequencer that used a mediator gets a narrow port in its own project.

A member of an equipment interface never returns a value the caller can act on that stands for "did not happen". A device that is not connected, a capability the device does not have, and a command the rig must decline throw one typed refusal that names its cause. The refusal derives from `SequenceEntityFailedException`, and its causes are declared once, each mapped to an Alpaca error: a capability the device lacks (`NotSupported`) is 0x400 (NotImplemented), a device that is not connected is 0x407 (NotConnected), and a parked or busy device is 0x40B (InvalidOperation); a cause that comes from rig state with no device call (not known, disconnected by the user) maps to none. A command that ran and did not confirm publishes its fault and then throws `SequenceEntityFailedException`, as the command module of 0011 does today.

Playbook rule 2 ("a port, not a redesign") no longer applies to this seam, and rule 4's compilable placeholder no longer applies to an equipment member. The playbook file is not edited; this record is the departure.

The mediator interfaces, their consumer interfaces and the twelve `Headless*Mediator` stubs are deleted, one device role per change. This record stays proposed while that runs: each of those changes is its pilot, and the change that deletes the last mediator makes it accepted.

## Rules

1. The Sequencer calls a device only through a role interface or the connections port declared in `OpenAstroAra.Sequencer`, and never reaches a device by reflection. Scope: `OpenAstroAra.Sequencer/**/*.cs`. Checked by: review (a `GetType().GetMethod(` on a device, or a parameter typed `I<Role>Mediator`, in a file the change moves).
2. A Server service implements its role interface directly, and the reconnector implements the connections port; no class exists only to adapt one of them. Scope: `OpenAstroAra.Server/**/*.cs`. Checked by: review (a class whose only job is to forward each member of a role interface or the port to another object).
3. A role interface holds only members the Sequencer calls, and no interface holds the members of more than one device role. The connections port holds only connect, recover, disconnect and two state reads (is connected, disconnected by the user), keyed by role, and no device member. Scope: `OpenAstroAra.Sequencer/**/*.cs`. Checked by: review (a member with no Sequencer call site, or an interface that inherits two role interfaces).
4. An equipment member never returns `false`, zero, `NaN`, `null`, an empty value or an unchanged cached value to say a command did not happen. Scope: `OpenAstroAra.Server/Services/*Service*.cs`. Checked by: review (a `return false`, `return 0`, a cached read or an empty body on a path where the device is absent, parked, busy, read-only or lacks the capability).
5. A device that is not connected, a capability the device lacks and a command the rig must decline throw the refusal with its cause, and a refusal publishes no fault. Scope: `OpenAstroAra.Server/Services/*Service*.cs`, `OpenAstroAra.Server/Services/EquipmentReconnector.cs`, `OpenAstroAra.Sequencer/**/*.cs`. Checked by: review (a refusal path that publishes to the fault hub, or a refusal thrown as a plain `SequenceEntityFailedException` or `InvalidOperationException`).
6. A command that ran and was not confirmed publishes its fault once and throws `SequenceEntityFailedException`, as rules 5 to 7 of [0011](0011-device-lifecycle-is-one-module.md) require. Scope: `OpenAstroAra.Server/Services/*Service*.cs`. Checked by: review (a stall or driver failure that returns instead of throwing, or publishes twice).
7. A capability the Alpaca device surface lacks is refused, never emulated or reached through a vendor path, and a capability Alpaca reaches through another device is driven through that device. Scope: `OpenAstroAra.Server/Services/CameraService*.cs`. Checked by: review (a USB-limit or heater write that is an empty body, or a dew-heater write that does not go through the paired Switch port).
8. A Sequencer connect finds a lifecycle device (every role on `DeviceLifecycle`) only through the reconnector's remembered or retained device, and a Sequencer disconnect of a lifecycle device records the user's disconnect, which recovery and the Reconnect trigger leave alone. The guider (PHD2, not on the lifecycle module) is connected when the profile names a guider type, and has no user-disconnect gate: a stated gap. Scope: `OpenAstroAra.Server/Services/EquipmentReconnector.cs`, `OpenAstroAra.Sequencer/**/*.cs`. Checked by: review (a read of `<Role>Settings.Id`, a discovery call on the connect path, or a disconnect that bypasses the service's `DisconnectAsync`).
9. Code outside the Sequencer that needs a device takes a narrow port declared in its own project or a role interface, and no new `I<Role>Mediator` type is added. Scope: `OpenAstroAra.Server/Services/*.cs`, `OpenAstroAra.PlateSolving/**/*.cs`, `OpenAstroAra.Equipment/**/*.cs`. Checked by: review (a new interface named `I*Mediator`, or a new parameter typed with one).

## Alternatives rejected

- **Keep NINA's mediators and fill the stubs**: the smallest diff, but each interface keeps members nobody calls, and a new stub is one `{ }` away; the six bugs came from exactly that shape.
- **Make each service's `Rescan`, `Connect` and `Disconnect` real**: works for the instructions, but it is a second reconnect path beside the reconnector, it can drift from auto-connect and the fault ladder, and it puts connect logic in device classes against 0011 rule 1.
- **One interface for all devices**: one type to inject, but every test fake implements members of nine devices, and the Sequencer can call a mount member on a camera.
- **An adapter class per role over the services**: leaves the services alone, but adds twelve classes that forward and drift; the services already own the lifecycle and the command module.
- **A connect base on every role interface**: each service would forward to the reconnector, which resolves the services: eleven forwarders and a cycle.
- **Return a result value instead of throwing**: explicit at the call site, but NINA's instructions signal failure by throwing and every caller would change; ADR 0022 chose the same for the REST path.
- **Reuse ADR 0022's refusal type**: one exception for the rig, but that type is the REST answer of a declined command and does not exist in code; this record declares the cause list once so 0022 can reuse it.

## Consequences

- A fix to a Sequencer-called command lands in the service that owns the device, and a test builds that service over a fake device (0011 rule 4); no `Headless*Mediator` fake exists.
- Instructions that were silent now act or fail loudly. Connect Equipment and Connect All Equipment connect the remembered device or throw naming it. Set USB Limit refuses. Dew Heater drives the paired heater port or refuses when none is paired. Users see new run errors where the run used to continue on nothing.
- Connect All Equipment connects the Safety Monitor first and stops if it fails. Switch Profile refuses while the daemon has one profile, instead of disconnecting the rig and then failing.
- A lifecycle device the sequence disconnected stays disconnected through the fault ladder and the Reconnect trigger, until an explicit connect; the Reconnect trigger does not fire for it. The guider is the exception: a Reconnect trigger redials a guider the sequence disconnected, because the guider's recovery has no user-disconnect gate and adding one changes the fault ladder for every caller.
- A Stop during a connect ends the instruction's wait at once with a cancellation, but the lifecycle's background connect is not cancelled, so the device can still finish connecting after the Stop.
- A move on a device that is not connected throws where it returned a cached value; a Validate that races a disconnect now stops the sequence.
- Stored sequences are unchanged: ADR 0018's `$type` names and members stay, and only constructor parameter types change.
- 0011 rules 5 to 7 name each device's files by one glob per role, so they hold while the partials shrink.
- Until the last role moves, a role not yet moved keeps its mediator for its own instructions; the Connect instructions use only the connections port from the first slice on.

## How to reverse

Re-add the mediator interfaces and the twelve stubs, change the Sequencer's constructor parameter types back, and point the Connect instructions at the mediators again. The behaviour is the harder part to undo: the refusals become `false` returns again, which is the bug class this record closes. Tests that build the services over a fake device keep working; the role tests would be rewritten against the mediator types.

## Links

- [#1545](https://github.com/open-astro/openastro-ara/issues/1545)
- Closed bugs of the class: [#1238](https://github.com/open-astro/openastro-ara/issues/1238), [#1187](https://github.com/open-astro/openastro-ara/issues/1187), [#1123](https://github.com/open-astro/openastro-ara/issues/1123), [#1190](https://github.com/open-astro/openastro-ara/issues/1190), [#1189](https://github.com/open-astro/openastro-ara/issues/1189), [#1188](https://github.com/open-astro/openastro-ara/issues/1188); symptom [#1229](https://github.com/open-astro/openastro-ara/issues/1229)
- [`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) section 0 rules 2 and 4 (relaxed for this seam), §52.1 (remembered devices)
- ADR 0003 (Alpaca only), ADR 0011 (device lifecycle and command module), ADR 0018 (sequence JSON), ADR 0022 (a refusal is declared once); #1127, #1539, #1663, #1710
