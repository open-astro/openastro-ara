# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

One Flutter codebase (`client/openastroara_client`) runs on Windows, iOS, Linux, macOS and Android (the spec calls these five "WILMA"), and it uses one custom Ara design language on all of them rather than per-OS native conventions. The desktop is the primary surface. Mobile Companion Mode (playbook §41) is a spec only: the iOS/Android platform folders build and side-load, but mobile distribution is deferred.

## Users

- **Primary: imagers who want the ASIAir model but open.** Deep-sky astrophotographers who like the appliance workflow (the rig runs the night, the laptop or phone plans and monitors) but want vendor-neutral gear over ASCOM Alpaca and open-source software instead of a closed box tied to one brand's hardware.
- **Secondary: NINA users going headless.** Experienced NINA users who want their rig to run unattended on a Pi. They should recognise the layout and terminology right away.

The NINA-like full UI is the default today. A proposed Simple Mode (see Capabilities and Constraints) would give the primary user an ASIAir-like path through the same app.

The typical setting is outdoors at night, at the telescope or within LAN range of it. Users set up and plan in the evening, start a sequence, then sleep while the rig works. They check in from a laptop or phone, or get woken by a notification.

## Product Purpose

OpenAstro Ara (short brand: **ARA**) has two parts:

- **OpenAstroAra.Server:** a headless ASP.NET Core daemon on .NET 10, running on ARM64 Linux (Raspberry Pi 4/5 is the reference; other Debian Trixie arm64 SBCs are best-effort). It owns all imaging-session state, controls equipment only over ASCOM Alpaca, and runs sequences, plate solving, guiding (PHD2/OpenAstro Guider), capture and recovery.
- **The OpenAstro Ara app:** a planning workstation and monitor. It is not a thin client. Sky atlas, Tonight's Sky, sequence editing, framing, profile editing and the image library all work with no rig connected.

Success means that once the user closes the laptop, imaging keeps going, and the night ends with usable data, calibration frames and a parked mount, without the user having to be awake for it.

## Positioning

An open appliance with NINA depth. ARA has ASIAir's "close the laptop, imaging keeps going" model, but it is open source (MPL-2.0 server, AGPL-3.0 client), vendor-neutral over ASCOM Alpaca and runs on any ARM64 Pi-class board. Its capabilities descend from NINA (safety policies, calibration workflows, mosaics, meridian flip, diagnostics, session analytics), and every one of them is made easy to find.

## Operating Context

- **Discovery and control:** the daemon listens on port 5555 and announces itself over mDNS. The app discovers it, or the user adds it by hostname or IP. One client holds the **control slot** at a time. A second client can request a takeover, and the current client sees an Allow / Keep-connected prompt.
- **Network:** LAN-only in v0.0.1 (no TLS, no auth). The network is treated as trusted, or reached over a VPN.
- **First run:** a profile setup wizard (about 18 screens in 7 stages: site and horizon, optics and camera, mount, guiding, plate solving, safety policies, storage, sky data). Skip is always available.
- **Night use:** the app has a persisted night mode, a red colour filter over the whole UI (⌘N / Ctrl+N), so users keep their dark adaptation at the telescope.
- **Equipment:** the top bar shows one chip per device (camera, mount, filter wheel, focuser, rotator, guider, …). Everything is discovered over Alpaca, typically through AlpacaBridge on the same Pi.
- **Unattended night ("the 3 a.m. story"):** end-of-session panel or twilight sky flats, user-configurable safety policies, per-device fault recovery, notifications, and an always-visible **Emergency Stop** on the bottom status bar of every tab.
- **Distribution:** server releases ship as a `.deb` through apt.openastro.net. The user docs are `docs/DEPLOY.md`, `docs/RUNNING.md` and `docs/USER_GUIDE.md`.

## Capabilities and Constraints

- **Equipment:** ASCOM Alpaca only, with no COM. That is what makes headless Linux possible.
- **Settings:** every setting must be findable through smart settings search (⌘K / Ctrl+K), and the ? affordances open contextual help. PR rule: a setting that isn't searchable doesn't merge.
- **Offline:** sky catalogs ship inside the server package, so the planetarium, search and Tonight's Sky work offline. The only download is the ASTAP plate-solver star database, sized to the camera's field of view.
- **Lineage:** a hard fork of NINA `master` (3.2 line). Inherited files keep their original copyright headers.
- **Status:** pre-release. v0.0.1 is feature-complete and pending its first tag (`v0.0.1-ara.1`), and v0.1.0 work is underway. Open work is tracked as GitHub issues labelled P1–P5. Outside contributions are paused.
- **Specs:** `design/PORT_PLAYBOOK.md` is the product spec, cited by § number throughout code and reviews. `design/API_CONTRACT.md` is the live API contract.
- **Under consideration: Simple Mode.** A second presentation of the same app that hides most toggles and options for an ASIAir-like experience, alongside the NINA-like full UI. Decided: the NINA-like full UI stays the default for now and keeps its current layout unchanged; all new layout work happens inside Simple Mode, and on desktop, Simple Mode only hides options, never removes them: the daemon and profile are unchanged, hidden settings keep their values, and switching modes resets nothing. Phones are different: they run §41 Companion Mode, which builds on Simple Mode but has no full UI to switch to. The full UI is not meant to work on phones, but phones must still include the setup wizard, the Sky Atlas, and plate-solver and autofocus configuration. The sequence editor and sequence templates stay off phones for now. Simple Mode's rule is 4×90% and KISS: the top 90 % most used features, 90 % of the time, 90 % faster, and only the bare minimum for a successful imaging night (user, 2026-11-03). Defaults, automation and self-recovery come first, it asks only what can't be known and sets it once, and it still gives the user some control, with ASIAir as the reference for how much. Simple Mode has no search and never links to the full UI; its own Settings hold only that minimum (`design/briefs/simple-mode-settings.md`). Open: which controls stay visible in both modes (to be defined).
- **Undecided:** mobile Companion Mode distribution (store signing, CI legs), and remote or authenticated access beyond the LAN.

## Brand Commitments

- **Names:** "ARA" is the short brand and "OpenAstro Ara" the long brand. The daemon is `OpenAstroAra.Server`, and the app is "the OpenAstro Ara app".
- **NINA layout:** the client deliberately mirrors NINA's layout (top bar, left panel, center tabs, right panel, bottom status bar), workflow patterns and control terminology ("Exposure", "Gain", "Offset", "Cooler Target", "Framing Assistant", "Sky Atlas", "Plate Solve", "Dither", …) so NINA users feel at home. Surfaces must also make sense to someone who has never used NINA.
- **NINA IP boundary (playbook §25.1):** never copy NINA's bitmap icons, splash screen, sample imagery, wordmark or any stylised "N.I.N.A." rendering.
- **Voice:** plain, honest and specific. The UI states exactly what happened or will happen (for example, the guide's "stops honestly if the sky climbs above … the usable brightness window").

## Evidence on Hand

- **App icon:** `client/openastroara_client/icon_sources/` (1024 px master plus iOS/Android variants) and `client/openastroara_client/assets/icons/app_icon.png`.
- **Bundled sky data and audio:** `client/openastroara_client/assets/catalogs/`, `assets/stellarium/` (AGPL-licensed offline sky data) and `assets/audio/`.
- **Feature documentation:** `docs/USER_GUIDE.md`, `docs/RELEASE_NOTES.md`, `CHANGELOG.md`.
- **Attribution:** `LICENSE.txt`, `COPYING`, `AUTHORS`, `NOTICE.md`.
- **Absent, never fabricate:** testimonials, user counts, customers, press, benchmarks, pricing, hardware certifications or endorsements. There is also no tagged release yet.

## Product Principles

1. **The rig owns the night.** Every decision assumes the user is asleep: the daemon must recover, stay safe, finish honestly and park without a client attached.
2. **Better than NINA, not equivalent to it.** Beyond the familiar layout, every UX decision must be clearly better than NINA's, especially in settings discoverability, diagnostics, analytics, calibration and mount safety.
3. **Robust capabilities, friendly surfaces.** All power-user features ship, and none hides behind buried menus, vague labels or "find the right tab" UX. Capability without discoverability counts as a regression.
4. **Open and vendor-neutral.** Any Alpaca device and any ARM64 Pi-class board. No lock-in to one brand's ecosystem.
5. **Planning works anywhere.** The app is a real workstation without the rig. The rig is needed only to execute.

## Accessibility & Inclusion

No formal standard is set yet. The known operating need is night use: low-light viewing and preserving dark adaptation, which night mode handles today.
