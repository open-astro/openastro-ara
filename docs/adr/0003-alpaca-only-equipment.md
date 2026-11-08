# Equipment is controlled only through ASCOM Alpaca

Status: accepted

## Context

NINA reaches equipment through ASCOM COM drivers on Windows and a set of bundled vendor SDKs, and carries brand-specific branches for mounts whose drivers misbehave. None of the COM path runs on a Linux Pi, every vendor SDK is another native binary to build and update for arm64, and every brand branch is a quirk to keep forever. ASCOM Alpaca is the same device model over HTTP, it runs on any OS, and ConformU gives drivers a formal conformance test.

## Decision

We decided during the port ([`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §6 and §52) that Ara controls every device through ASCOM Alpaca and nothing else: no ASCOM COM, no INDI or INDIGO client, no vendor SDK for device control, and no brand-specific behaviour. A device whose driver speaks something else reaches Ara through a bridge that exposes it as Alpaca (AlpacaBridge, INDIGO's Alpaca server, AlpacaPi). Ara reads Alpaca's capability flags and adapts to them, and a driver bug is reported upstream rather than worked around. The one bounded addition (2026-08-02, §77) is planetary video capture, which Alpaca has no API for: the daemon may call the camera vendor SDK's video subset, and only while the camera is detached from Alpaca.

## Rules

1. Device control goes through the `ASCOM.Alpaca.*` packages: no project references ASCOM COM components, an INDI or INDIGO client, or a vendor camera, mount or focuser SDK for device control. Scope: `**/*.csproj`. Checked by: review (a new package or native library reference for a device protocol other than Alpaca).
2. No code branches on a device's brand, model or driver name; behaviour follows the Alpaca capability flags and properties. Scope: `OpenAstroAra.Equipment/**/*.cs`, `OpenAstroAra.Server/Services/**/*.cs`. Checked by: review (a comparison against a device `Name`, `Description` or `DriverInfo` string that changes behaviour).
3. A vendor SDK binding is added only for §77 planetary video, and it runs only while the camera is disconnected from Alpaca. Scope: `OpenAstroAra.Equipment/**/*.cs`, `OpenAstroAra.Server/**/*.cs`. Checked by: review (a `DllImport` or `LibraryImport` of a vendor library outside the planetary capture path, or one called while the camera's Alpaca connection is open).

## Alternatives rejected

- **INDI or INDIGO as a second native protocol**: the Linux-native choice, but neither has a ConformU-style conformance suite, so their driver quirks would be Ara's to discover and carry; both already reach Ara through a bridge.
- **Vendor SDKs in the daemon** (as NINA and ASIAir do): direct, fast access to cameras, but one native binary per vendor per architecture, each with its own update cycle and crash surface inside the daemon.
- **A mount quirks database**: fixes a misbehaving mount today, but it grows forever and hides driver bugs from the people who can fix them.

## Consequences

- Ara runs on any OS the daemon builds for and has one equipment provider, `AlpacaEquipmentProvider`.
- A user with COM, INDI or INDIGO equipment runs a bridge, and the docs point them at one.
- A driver bug stays a bug until its author fixes it; Ara does not ship a workaround.
- High-frame-rate capture never goes through Alpaca; planetary capture lives in §77's own engine.
- `CoolingFanInterlock.IsThermalSwitchDevice` picks the cooling fan switch by device name, which breaks rule 2. [#1700](https://github.com/open-astro/openastro-ara/issues/1700) tracks the fix.

## How to reverse

Adding a second protocol means a second `IEquipmentProvider`, a per-protocol connect and discovery flow in the client and the wizard, and a per-driver test matrix. Adding vendor SDKs means building, bundling and updating native binaries for each vendor in the `.deb`.

## Links

- [`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §6 (Alpaca-only equipment), §52 (Alpaca-only commitment and feature detection), §68 (AlpacaBridge contract), §77 (planetary capture)
- [ConformU](https://github.com/ASCOMInitiative/ConformU)
