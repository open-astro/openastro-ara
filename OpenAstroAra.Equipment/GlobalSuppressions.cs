// Assembly-level analyzer suppressions for OpenAstroAra.Equipment.
//
// Each entry documents an MS-sanctioned "when to suppress" rationale. Prefer an
// in-code fix; only add here when the rule does not fit the construct.

using System.Diagnostics.CodeAnalysis;

// CA1003 — the equipment mediator interfaces expose ASYNCHRONOUS events whose handler
// delegate returns Task (Func<object, TEventArgs, Task>) so the raiser can await every
// subscriber (DelegateExtension.InvokeAsync). CA1003's documented Cause is "a delegate
// that returns void"; these return Task and are therefore outside the rule's stated
// scope, and the recommended System.EventHandler<T> (which returns void) cannot express
// awaitable handlers. IGuiderMediator.GuideEvent additionally carries the polymorphic
// IGuideStep domain interface, which cannot derive from EventArgs.
[assembly: SuppressMessage("Design", "CA1003:Use generic event handler instances",
    Justification = "Awaitable async events (handler returns Task) are outside CA1003's documented void-delegate cause; EventHandler<T> cannot express awaitable handlers. GuideEvent carries the polymorphic IGuideStep interface, which cannot be an EventArgs.",
    Scope = "namespaceanddescendants",
    Target = "~N:OpenAstroAra.Equipment.Interfaces.Mediator")]

// CA1003 — IGuider.GuideEvent is the canonical guide-step event carrying the polymorphic
// IGuideStep domain interface (implemented by PhdEvent/SkyGuardEvent). IGuideStep is an
// interface and cannot derive from System.EventArgs, so CA1003's recommended
// EventHandler<TEventArgs> fix is inapplicable without replacing the established
// guide-step payload contract across the entire guider subsystem.
[assembly: SuppressMessage("Design", "CA1003:Use generic event handler instances",
    Justification = "GuideEvent carries the polymorphic IGuideStep interface payload, which cannot derive from System.EventArgs; the EventHandler<TEventArgs> fix is inapplicable.",
    Scope = "type",
    Target = "~T:OpenAstroAra.Equipment.Interfaces.IGuider")]
