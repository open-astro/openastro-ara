# Guide Camera custody is one module in the daemon

Status: proposed

## Context

Four activities use the Guide Camera: Guiding, the Polar Align Routine, guide focus and the Guider live view. The rules for handing it over sit in four places and disagree. Starting Guiding ends a Polar Align Routine, but the routine neither stops nor refuses Guiding and leaves the guider program's lease to refuse. The routine stops guide focus, but guide focus refuses the routine. Paused counts as guiding in one check and not in another, the live view checks nothing, and the lease timings are written twice. The hand-over between modules is wired by functions set on the Guider after construction in `Program.cs`. `PolarAlignService`, `GuideFocusService` and `GuiderLiveService` take the concrete Guider class and reach through it to the raw PHD2 client for 31 operations, so their tests run against a TCP fake with delays.

## Decision

We decided (2026-11-12, [#1809](https://github.com/open-astro/openastro-ara/issues/1809)) that custody of the Guide Camera is one module in the daemon with one rule. Starting Guiding takes the Guide Camera from anyone. The Polar Align Routine, guide focus and the live view are setup activities: they never take it from the Guider while it is guiding, calibrating, paused or has lost the star, and they are refused with a reason. Between setup activities the newer request takes it. Ara decides and the guider program's lease enforces: the custody module is the only code that takes or releases that lease. The Polar Align Routine and guide focus then reach the Guider through a narrow seam of custody and capture operations, with the PHD2 adapter and an in-process fake behind it. The live view keeps the concrete adapter, because its 25 operations are pass-through and copying them would make a shallow module. The star-lost episode becoming its own module with an injected clock is a third, optional slice.

## Rules

1. Every use of the Guide Camera asks the custody module, and only that module takes or releases the guider program's lease. Scope: `OpenAstroAra.Server/Services/PolarAlignService.cs`, `OpenAstroAra.Server/Services/GuideFocusService.cs`, `OpenAstroAra.Server/Services/GuiderLiveService.cs`, `OpenAstroAra.Server/Services/GuiderService*.cs`. Checked by: review (a lease call outside the custody module, or a capture started without asking it).
2. Starting Guiding takes the Guide Camera from any holder, and a setup activity is refused with a reason while the Guider is guiding, calibrating, paused or has lost the star. Scope: `OpenAstroAra.Server/Services/GuiderService*.cs`, `OpenAstroAra.Server/Services/PolarAlignService.cs`, `OpenAstroAra.Server/Services/GuideFocusService.cs`, `OpenAstroAra.Server/Services/GuiderLiveService.cs`. Checked by: review (a second definition of busy, or a setup activity that stops Guiding).
3. No hand-over between Guider modules is wired by a function set after construction. Scope: `OpenAstroAra.Server/Program.cs`. Checked by: review (a property assigned on the Guider in the composition root that names the Polar Align Routine or guide focus).
4. The Polar Align Routine and guide focus receive the custody and capture seam and never the raw PHD2 client. Scope: `OpenAstroAra.Server/Services/PolarAlignService.cs`, `OpenAstroAra.Server/Services/GuideFocusService.cs`. Checked by: review (a constructor that takes the concrete Guider class, or a call that reaches the PHD2 client through it).
5. The TCP fake is used only by tests of the PHD2 adapter. Scope: `OpenAstroAra.TestHarness/Guider/FakeGuider.cs`, `OpenAstroAra.Test/PolarAlignServiceTest.cs`. Checked by: review (a migrated Polar Align Routine or guide-focus test that opens a socket or waits on a delay).

## Alternatives rejected

- **Whoever holds it keeps it**: no preemption to reason about, but a forgotten Polar Align Routine would block the start of a Session.
- **The newest request always wins**: one line of policy, but a live view opened from a phone could stop Guiding mid-exposure.
- **Let the guider program's lease decide**: no module to write, but the four rule sets stay aligned only by convention, and stock PHD2 would have to carry Ara's policy.
- **A seam over all 31 operations**: no module would see the PHD2 client, but most of the seam would be pass-through.
- **Keep the TCP fake for every test**: nothing to build, but the Polar Align Routine's tests keep their socket and their delays.

## Consequences

- The Guide Camera's hand-over is tested once, against a fake, with no socket.
- Guide focus and the live view gain refusals they did not have: the live view is refused while the Guider guides, and a newer setup activity now takes the camera from guide focus.
- `Program.cs` loses the hooks that wired the hand-over.
- The seam has two adapters, the PHD2 adapter and the in-process fake; PHD2 and openastro-guider stay one adapter.

## How to reverse

Move the rule back into each caller and restore the functions set in `Program.cs`. Tests written against the in-process fake have to return to the TCP fake.

## Links

- [#1809](https://github.com/open-astro/openastro-ara/issues/1809)
- ADR 0013 (the client's Guider equipment choice)
