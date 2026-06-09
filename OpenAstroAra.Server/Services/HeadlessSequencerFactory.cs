#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Sequencer;
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.Camera;
using OpenAstroAra.Sequencer.SequenceItem.Dome;
using OpenAstroAra.Sequencer.SequenceItem.Focuser;
using OpenAstroAra.Sequencer.SequenceItem.Guider;
using OpenAstroAra.Sequencer.SequenceItem.Rotator;
using OpenAstroAra.Sequencer.SequenceItem.SafetyMonitor;
using OpenAstroAra.Sequencer.SequenceItem.Switch;
using OpenAstroAra.Sequencer.SequenceItem.Telescope;
using OpenAstroAra.Sequencer.SequenceItem.Utility;
using OpenAstroAra.Sequencer.Trigger;
using OpenAstroAra.Sequencer.Utility.DateTimeProvider;
using OpenAstroAra.Server.Services.Equipment;
using System.Windows.Data;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Headless implementation of <see cref="ISequencerFactory"/> for the
/// OpenAstroAra daemon. The inherited <c>SequencerFactory</c> needs an
/// <c>IProfileService</c> + a <c>PluginOptionsAccessor</c> + builds WPF
/// <c>CollectionViewSource</c> wrappers for sidebar UI — none of which
/// the daemon needs since Flutter drives the sidebar via REST + WS.
/// This implementation provides just enough surface to satisfy the
/// JSON converters: prototype lookup via <see cref="GetItem{T}"/>
/// /etc., plus the <c>DateTimeProviders</c> list the
/// <c>SequenceDateTimeProviderCreationConverter</c> reads.
///
/// Items / Conditions / Triggers / Container start empty; per-type
/// registrations land in subsequent §38k sub-PRs as we wire equipment-
/// bound instruction prototypes (TakeExposure, SetFilter, Slew, etc.)
/// into the DI tree.
/// </summary>
public sealed class HeadlessSequencerFactory : ISequencerFactory {

    public IList<ISequenceItem> Items { get; }
    public IList<ISequenceCondition> Conditions { get; }
    public IList<ISequenceContainer> Containers { get; }
    public IList<ISequenceTrigger> Triggers { get; }
    public IList<IDateTimeProvider> DateTimeProviders { get; }

    // The four View properties are WPF state for the legacy sidebar.
    // Headless callers don't read them, but the interface requires them.
    // CollectionViewSource is shimmed in OpenAstroAra.Sequencer/Compat;
    // GetDefaultView returns a working CollectionViewSource over the
    // backing list.
    public ICollectionView ItemsView { get; }
    public ICollectionView InstructionsView { get; }
    public ICollectionView ConditionsView { get; }
    public ICollectionView TriggersView { get; }

    public string ViewFilter { get; set; } = string.Empty;

    public HeadlessSequencerFactory(
            IList<ISequenceItem>? items = null,
            IList<ISequenceCondition>? conditions = null,
            IList<ISequenceContainer>? container = null,
            IList<ISequenceTrigger>? triggers = null,
            IList<IDateTimeProvider>? dateTimeProviders = null) {
        Items = items ?? new List<ISequenceItem>();
        Conditions = conditions ?? new List<ISequenceCondition>();
        Containers = container ?? new List<ISequenceContainer>();
        Triggers = triggers ?? new List<ISequenceTrigger>();
        DateTimeProviders = dateTimeProviders ?? new List<IDateTimeProvider>();

        // Headless views are best-effort over the backing lists. Sidebar
        // grouping / sorting / filtering are client-side concerns.
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        InstructionsView = CollectionViewSource.GetDefaultView(Items);
        ConditionsView = CollectionViewSource.GetDefaultView(Conditions);
        TriggersView = CollectionViewSource.GetDefaultView(Triggers);
    }

    // Same Clone-prototype-or-default pattern as the inherited
    // SequencerFactory (see SequencerFactory.cs lines 144-158). Returning
    // null when no prototype matches lets the JSON converters fall back
    // to UnknownSequenceContainer / UnknownSequenceItem.

    public T GetContainer<T>() where T : ISequenceContainer =>
        (T)(Containers.FirstOrDefault(x => x.GetType() == typeof(T))?.Clone() ?? default(T)!);

    public T GetItem<T>() where T : ISequenceItem =>
        (T)(Items.FirstOrDefault(x => x.GetType() == typeof(T))?.Clone() ?? default(T)!);

    public T GetCondition<T>() where T : ISequenceCondition =>
        (T)(Conditions.FirstOrDefault(x => x.GetType() == typeof(T))?.Clone() ?? default(T)!);

    public T GetTrigger<T>() where T : ISequenceTrigger =>
        (T)(Triggers.FirstOrDefault(x => x.GetType() == typeof(T))?.Clone() ?? default(T)!);

    /// <summary>
    /// Build a factory pre-populated with the equipment-independent
    /// container prototypes — <see cref="SequenceRootContainer"/>,
    /// <see cref="SequentialContainer"/>, <see cref="ParallelContainer"/>.
    /// These are the structural containers a NINA sequence root needs to
    /// resolve before any equipment-bound instructions land; registering
    /// them unblocks the JSON converter on the most common $type values
    /// at the top of a saved sequence body.
    ///
    /// Per-instruction prototypes (TakeExposure, SetFilter, etc.) wire in
    /// subsequent §38k sub-PRs as each instruction's equipment-service
    /// dependency tree gets DI-wired.
    /// </summary>
    public static HeadlessSequencerFactory WithDefaults(
            ISafetyMonitorMediator? safetyMonitorMediator = null,
            ITelescopeMediator? telescopeMediator = null,
            IGuiderMediator? guiderMediator = null,
            IFocuserMediator? focuserMediator = null,
            ICameraMediator? cameraMediator = null,
            IRotatorMediator? rotatorMediator = null,
            ISwitchMediator? switchMediator = null,
            IDomeMediator? domeMediator = null,
            IDomeFollower? domeFollower = null) {
        // §38k-9 … §38k-21 — equipment-mediator stubs default to no-op headless
        // impls so call sites that don't yet have real Alpaca-backed mediators
        // still get a usable prototype set. As real drivers land (§14e Alpaca
        // simulator pinning gates this), Program.cs's DI can hand in real
        // mediators here instead.
        //
        // §38k-15 note: HeadlessFilterWheelMediator is DI-registered in Program.cs
        // to complete the mediator surface, but is NOT a parameter here — its only
        // instruction (SwitchFilter) also needs IProfileService and lands in a
        // follow-up §38k sub-PR, so the factory has no use for it yet.
        safetyMonitorMediator ??= new HeadlessSafetyMonitorMediator();
        telescopeMediator ??= new HeadlessTelescopeMediator();
        guiderMediator ??= new HeadlessGuiderMediator();
        focuserMediator ??= new HeadlessFocuserMediator();
        cameraMediator ??= new HeadlessCameraMediator();
        rotatorMediator ??= new HeadlessRotatorMediator();
        switchMediator ??= new HeadlessSwitchMediator();
        domeMediator ??= new HeadlessDomeMediator();
        // §38k-21 — the one non-mediator dependency a dome instruction needs.
        domeFollower ??= new HeadlessDomeFollower();

        return new HeadlessSequencerFactory(
            items: new List<ISequenceItem> {
                // §38k-4 — utility instructions with no equipment deps.
                new Annotation(),
                new WaitForTimeSpan(),
                // §38k-9 — first equipment-bound instruction prototype.
                // WaitUntilSafe polls ISafetyMonitorMediator.GetInfo() until
                // IsSafe transitions true; with the headless stub it reports
                // "not connected" so the JSON path still works for
                // serialization round-trip + validation while the real
                // Alpaca-backed wiring is pending.
                new WaitUntilSafe(safetyMonitorMediator),
                // §38k-10 — second equipment-mediator + telescope-bound
                // instruction. SetTracking calls telescopeMediator.SetTrackingMode
                // at Execute time; the headless stub no-ops.
                new SetTracking(telescopeMediator),
                // §38k-11 — telescope+guider-bound instructions. ParkScope
                // stops guiding before parking; FindHome + SlewScopeToRaDec
                // also coordinate the guider. UnparkScope is telescope-only
                // but registered alongside for completeness of the telescope
                // instruction surface.
                new UnparkScope(telescopeMediator),
                new ParkScope(telescopeMediator, guiderMediator),
                new FindHome(telescopeMediator, guiderMediator),
                new SlewScopeToRaDec(telescopeMediator, guiderMediator),
                // §38k-12 — focuser-bound instructions. All three take only
                // IFocuserMediator. The headless stub no-ops the move ops
                // and returns 0 for the resulting position.
                new MoveFocuserAbsolute(focuserMediator),
                new MoveFocuserRelative(focuserMediator),
                new MoveFocuserByTemperature(focuserMediator),
                // §38k-13 — camera-control instructions. All five take only
                // ICameraMediator (no IImagingMediator / image pipeline), so
                // they register against the headless camera stub: Cool/Warm
                // report "didn't succeed" off the not-connected sentinel, and
                // SetUSBLimit / SetReadoutMode / DewHeater no-op.
                new CoolCamera(cameraMediator),
                new WarmCamera(cameraMediator),
                new SetUSBLimit(cameraMediator),
                new SetReadoutMode(cameraMediator),
                new DewHeater(cameraMediator),
                // §38k-14 — guider instructions on the existing guider stub.
                // StartGuiding / StopGuiding depend only on IGuiderMediator.
                // (Dither also needs IProfileService → deferred to a follow-up.)
                new StartGuiding(guiderMediator),
                new StopGuiding(guiderMediator),
                // §38k-16 — rotator instruction. MoveRotatorMechanical depends
                // only on IRotatorMediator.
                new MoveRotatorMechanical(rotatorMediator),
                // §38k-17 — switch instruction. SetSwitchValue depends only on
                // ISwitchMediator.
                new SetSwitchValue(switchMediator),
                // §38k-18 — dome instructions. Five depend only on IDomeMediator;
                // Enable/DisableDomeSynchronization also take the telescope stub.
                // (SynchronizeDome also needs IDomeFollower → deferred.)
                new OpenDomeShutter(domeMediator),
                new CloseDomeShutter(domeMediator),
                new ParkDome(domeMediator),
                new FindHomeDome(domeMediator),
                new SlewDomeAzimuth(domeMediator),
                new EnableDomeSynchronization(domeMediator, telescopeMediator),
                new DisableDomeSynchronization(domeMediator, telescopeMediator),
                // §38k-21 — SynchronizeDome was the one dome instruction deferred
                // from §38k-18 (it also needs IDomeFollower). With the headless
                // dome-follower stub it now registers, completing the dome set.
                new SynchronizeDome(domeMediator, domeFollower, telescopeMediator),
            },
            conditions: new List<ISequenceCondition> {
                // §38k-7 — no-equipment conditions. LoopCondition bounds a
                // container by iteration count; TimeSpanCondition bounds it
                // by elapsed wall-clock time. Both are parameterless +
                // self-contained.
                new LoopCondition(),
                new TimeSpanCondition(),
            },
            container: new List<ISequenceContainer> {
                new SequenceRootContainer(),
                new SequentialContainer(),
                new ParallelContainer(),
            });
    }
}