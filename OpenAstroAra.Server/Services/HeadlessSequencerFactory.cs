#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Astrometry;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Sequencer;
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Connections;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.Format;
using OpenAstroAra.Sequencer.Interfaces;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.Autofocus;
using OpenAstroAra.Sequencer.SequenceItem.Camera;
using OpenAstroAra.Sequencer.SequenceItem.Connect;
using OpenAstroAra.Sequencer.SequenceItem.Dome;
using OpenAstroAra.Sequencer.SequenceItem.FilterWheel;
using OpenAstroAra.Sequencer.SequenceItem.FlatDevice;
using OpenAstroAra.Sequencer.SequenceItem.Focuser;
using OpenAstroAra.Sequencer.SequenceItem.Guider;
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Sequencer.SequenceItem.Platesolving;
using OpenAstroAra.Sequencer.SequenceItem.Rotator;
using OpenAstroAra.Sequencer.SequenceItem.SafetyMonitor;
using OpenAstroAra.Sequencer.SequenceItem.Switch;
using OpenAstroAra.Sequencer.SequenceItem.Telescope;
using OpenAstroAra.Sequencer.SequenceItem.Utility;
using OpenAstroAra.Sequencer.Trigger;
using OpenAstroAra.Sequencer.Trigger.Autofocus;
using OpenAstroAra.Sequencer.Trigger.Connect;
using OpenAstroAra.Sequencer.Trigger.Guider;
using OpenAstroAra.Sequencer.Trigger.MeridianFlip;
using OpenAstroAra.Sequencer.Utility.DateTimeProvider;
using OpenAstroAra.Server.Services.Equipment;
using System;
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
    /// Builds one prototype per entry, in entry order, with the constructor registered for the entry's engine
    /// class. §38k-7 / §38.10a: every client-generated target block carries an AboveHorizonCondition and the
    /// editor offers AltitudeCondition; unregistered, both degraded to UnknownSequenceCondition, whose failed
    /// check skipped the whole target block. AboveHorizonCondition's constructor seeds its offset, which runs a
    /// NOVAS transform (WaitLoopData.Offset → SetTargetAltitudeWithHorizon). On a daemon without the
    /// astrometry natives that throws, and the prototype is built at DI time, so the whole daemon failed to
    /// start (CI's runtime smoke, Docker). An entry flagged <see cref="SequenceKindEntry.NeedsAstrometryNatives"/>
    /// whose constructor faults that way is skipped instead: Program.cs already warns at boot that altitude
    /// conditions will fail until the natives are installed, and a sequence carrying one degrades to
    /// UnknownSequenceCondition as it did before §38.10a. Any other fault, the same fault on an unflagged
    /// entry, and an entry with no constructor propagate.
    /// </summary>
    internal static List<object> Instantiate(IEnumerable<SequenceKindEntry> entries, IReadOnlyDictionary<Type, Func<object>> constructors) {
        var built = new List<object>();
        foreach (var entry in entries) {
            if (!constructors.TryGetValue(entry.EngineClass, out var make)) {
                throw new InvalidOperationException("The kind table lists '" + entry.Kind + "' but the factory has no constructor for " + entry.EngineClass.Name + ".");
            }
            try {
                built.Add(make());
            } catch (Exception ex) when (entry.NeedsAstrometryNatives && IsMissingNativeLibrary(ex)) {
                // skipped, see above
            }
        }
        return built;
    }

    /// <summary>True for a fault from constructing a prototype that needs a native library that is missing or
    /// mismatched (absent, wrong architecture, or missing an entry point).</summary>
    internal static bool IsMissingNativeLibrary(Exception ex) =>
        ex is TypeInitializationException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

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
            IDomeFollower? domeFollower = null,
            IFilterWheelMediator? filterWheelMediator = null,
            IFlatDeviceMediator? flatDeviceMediator = null,
            IWeatherDataMediator? weatherDataMediator = null,
            IProfileService? profileService = null,
            IImagingMediator? imagingMediator = null,
            IMeridianFlipExecutor? meridianFlipExecutor = null,
            ICenteringExecutor? centeringExecutor = null,
            IAutofocusExecutor? autofocusExecutor = null,
            IImageHistory? imageHistory = null,
            IAutofocusConditionGate? autofocusConditionGate = null,
            IFlatCaptureExecutor? flatCaptureExecutor = null,
            OpenAstroAra.Sequencer.SequenceItem.Telescope.ITrackingProbeExecutor? trackingProbeExecutor = null,
            OpenAstroAra.Sequencer.Interfaces.IFrameQualityFeed? frameQualityFeed = null,
            OpenAstroAra.Sequencer.Interfaces.IFrameSolveFeed? frameSolveFeed = null,
            OpenAstroAra.Sequencer.Interfaces.IGuidingRestartRecenter? guidingRestartRecenter = null,
            IEquipmentConnections? equipmentConnections = null) =>
        Build(safetyMonitorMediator, telescopeMediator, guiderMediator, focuserMediator, cameraMediator, rotatorMediator,
            switchMediator, domeMediator, domeFollower, filterWheelMediator, flatDeviceMediator, weatherDataMediator,
            profileService, imagingMediator, meridianFlipExecutor, centeringExecutor, autofocusExecutor, imageHistory,
            autofocusConditionGate, flatCaptureExecutor, trackingProbeExecutor, frameQualityFeed, frameSolveFeed,
            guidingRestartRecenter, equipmentConnections, constructorOverrides: null);

    /// <summary>The same build with some constructors replaced, for a test that makes one prototype fault. Not for production code.</summary>
    internal static HeadlessSequencerFactory WithDefaults(IReadOnlyDictionary<Type, Func<object>> constructorOverrides) =>
        Build(constructorOverrides: constructorOverrides);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The NighttimeCalculator behind the time sources lives as long as the factory, a daemon singleton; its only resource is a 10-minute reference-date timer.")]
    private static HeadlessSequencerFactory Build(
            ISafetyMonitorMediator? safetyMonitorMediator = null,
            ITelescopeMediator? telescopeMediator = null,
            IGuiderMediator? guiderMediator = null,
            IFocuserMediator? focuserMediator = null,
            ICameraMediator? cameraMediator = null,
            IRotatorMediator? rotatorMediator = null,
            ISwitchMediator? switchMediator = null,
            IDomeMediator? domeMediator = null,
            IDomeFollower? domeFollower = null,
            IFilterWheelMediator? filterWheelMediator = null,
            IFlatDeviceMediator? flatDeviceMediator = null,
            IWeatherDataMediator? weatherDataMediator = null,
            IProfileService? profileService = null,
            IImagingMediator? imagingMediator = null,
            IMeridianFlipExecutor? meridianFlipExecutor = null,
            ICenteringExecutor? centeringExecutor = null,
            IAutofocusExecutor? autofocusExecutor = null,
            IImageHistory? imageHistory = null,
            IAutofocusConditionGate? autofocusConditionGate = null,
            IFlatCaptureExecutor? flatCaptureExecutor = null,
            OpenAstroAra.Sequencer.SequenceItem.Telescope.ITrackingProbeExecutor? trackingProbeExecutor = null,
            OpenAstroAra.Sequencer.Interfaces.IFrameQualityFeed? frameQualityFeed = null,
            OpenAstroAra.Sequencer.Interfaces.IFrameSolveFeed? frameSolveFeed = null,
            OpenAstroAra.Sequencer.Interfaces.IGuidingRestartRecenter? guidingRestartRecenter = null,
            IEquipmentConnections? equipmentConnections = null,
            IReadOnlyDictionary<Type, Func<object>>? constructorOverrides = null) {
        // §38k-9 … §38k-22 — equipment-mediator stubs default to no-op headless
        // impls so call sites that don't yet have real Alpaca-backed mediators
        // still get a usable prototype set. As real drivers land (§14e Alpaca
        // simulator pinning gates this), Program.cs's DI can hand in real
        // mediators here instead.
        //
        // §38k-22 — profileService is a HeadlessProfileService stub: it exists
        // only so the profile-bound instructions (Dither / SwitchFilter / the
        // Connect dir except ConnectEquipment) can be *constructed* as JSON-resolvable prototypes.
        // Prototypes never execute, so a default ActiveProfile + no-op events
        // suffice; reconciling the real user-edited profile (IProfileStore /
        // profile.json) is an execution-engine concern decided later.
        safetyMonitorMediator ??= new HeadlessSafetyMonitorMediator();
        telescopeMediator ??= new HeadlessTelescopeMediator();
        guiderMediator ??= new HeadlessGuiderMediator();
        focuserMediator ??= new HeadlessFocuserMediator();
        cameraMediator ??= new HeadlessCameraMediator();
        // §14e PRb — TakeExposure's capture dependency; the real CameraService-backed
        // IImagingMediator swaps in via Program.cs DI, this stub only enables prototype
        // construction (clone/validate — never executed headless without real wiring).
        imagingMediator ??= new HeadlessImagingMediator();
        rotatorMediator ??= new HeadlessRotatorMediator();
        switchMediator ??= new HeadlessSwitchMediator();
        domeMediator ??= new HeadlessDomeMediator();
        // §38k-21 — the one non-mediator dependency a dome instruction needs.
        domeFollower ??= new HeadlessDomeFollower();
        // §38k-19/20 + §38k-22 — the remaining device mediators + the profile stub
        // that the Connect dir (except ConnectEquipment) + Dither/SwitchFilter need.
        filterWheelMediator ??= new HeadlessFilterWheelMediator();
        flatDeviceMediator ??= new HeadlessFlatDeviceMediator();
        weatherDataMediator ??= new HeadlessWeatherDataMediator();
        profileService ??= new HeadlessProfileService();
        // §58 — the meridian-flip trigger needs an IMeridianFlipExecutor to construct. The throwing
        // placeholder keeps the prototype JSON-resolvable for clone/validate; Program.cs hands in the real
        // §58.4 MeridianFlipExecutor so an executed flip actually runs the recovery sequence.
        meridianFlipExecutor ??= new PlaceholderMeridianFlipExecutor();
        // The Connect Equipment item and the Reconnect trigger connect through the rig's connections; Program.cs
        // hands in the EquipmentReconnector. This placeholder keeps the prototypes constructible and refuses a run.
        equipmentConnections ??= new UnwiredEquipmentConnections();
        // Wait (until time) and Until a time need their time sources: the clock (the default, which
        // rolls over at the site's dawn, so an evening plan's 01:30 means tonight's 01:30) and the
        // sky events the full UI's editor offers.
        var night = new NighttimeCalculator(profileService);
        var clockTime = new OpenAstroAra.Sequencer.Utility.DateTimeProvider.TimeProvider(night);
        var timeSources = new List<IDateTimeProvider> {
            clockTime,
            new SunsetProvider(night), new SunriseProvider(night),
            new CivilDuskProvider(night), new CivilDawnProvider(night),
            new NauticalDuskProvider(night), new NauticalDawnProvider(night),
            new DuskProvider(night), new DawnProvider(night),
            new MeridianProvider(profileService),
        };

        // One constructor per engine class; the kind table (SequenceKinds.All) says which are built and in what order.
        var constructors = new Dictionary<Type, Func<object>> {
            // §38k-4 — utility instructions with no equipment deps.
            [typeof(Annotation)] = () => new Annotation(),
            // §58.12-b — pause-for-a-human step (manual filter swap on an
            // OSC rig, etc.); arms the root's pause gate as Prompt.
            [typeof(WaitForUser)] = () => new WaitForUser(),
            [typeof(WaitForTimeSpan)] = () => new WaitForTimeSpan(),
            [typeof(WaitForTime)] = () => new WaitForTime(timeSources, clockTime),
            // §48.4 — WaitForSunAltitude gates the sky-flat generator's body (waits for the sun
            // to drop through the twilight altitude); profile-only dep for the observer site.
            [typeof(WaitForSunAltitude)] = () => new WaitForSunAltitude(profileService),
            // §38k-9 — first equipment-bound instruction prototype.
            // WaitUntilSafe polls ISafetyMonitorMediator.GetInfo() until
            // IsSafe transitions true; with the headless stub it reports
            // "not connected" so the JSON path still works for
            // serialization round-trip + validation while the real
            // Alpaca-backed wiring is pending.
            [typeof(WaitUntilSafe)] = () => new WaitUntilSafe(safetyMonitorMediator),
            // §38k-10 — second equipment-mediator + telescope-bound
            // instruction. SetTracking calls telescopeMediator.SetTrackingMode
            // at Execute time; the headless stub no-ops.
            [typeof(SetTracking)] = () => new SetTracking(telescopeMediator),
            // §38k-11 — telescope+guider-bound instructions. ParkScope
            // stops guiding before parking; FindHome + SlewScopeToRaDec
            // also coordinate the guider. UnparkScope is telescope-only
            // but registered alongside for completeness of the telescope
            // instruction surface.
            [typeof(UnparkScope)] = () => new UnparkScope(telescopeMediator),
            [typeof(ParkScope)] = () => new ParkScope(telescopeMediator, guiderMediator),
            [typeof(FindHome)] = () => new FindHome(telescopeMediator, guiderMediator),
            // #1446 — with "Center target after slew" on, the slew centres through the §28
            // CenteringService (the same executor as Center and Rotate below).
            [typeof(SlewScopeToRaDec)] = () => new SlewScopeToRaDec(telescopeMediator, guiderMediator, profileService, centeringExecutor),
            // §48.4 — the §39.5 sky-flat generator emits SlewScopeToAltAz (point the mount at
            // the twilight sky patch) wrapped by a WaitForSunAltitude gate; both must resolve
            // through the factory or those generated bodies fail to load.
            [typeof(SlewScopeToAltAz)] = () => new SlewScopeToAltAz(profileService, telescopeMediator, guiderMediator),
            // §38k-12 — focuser-bound instructions. All three take only
            // IFocuserMediator. The headless stub no-ops the move ops
            // and returns 0 for the resulting position.
            [typeof(MoveFocuserAbsolute)] = () => new MoveFocuserAbsolute(focuserMediator),
            [typeof(MoveFocuserRelative)] = () => new MoveFocuserRelative(focuserMediator),
            [typeof(MoveFocuserByTemperature)] = () => new MoveFocuserByTemperature(focuserMediator),
            // §38k-13 — camera-control instructions. All five take only
            // ICameraMediator (no IImagingMediator / image pipeline), so
            // they register against the headless camera stub: Cool/Warm
            // report "didn't succeed" off the not-connected sentinel, and
            // SetUSBLimit / SetReadoutMode / DewHeater no-op.
            // §14e PRb — the capture instruction itself; NINA-imported bodies + the §38.7
            // starter templates resolve to it via the §38k-6 namespace remap.
            [typeof(OpenAstroAra.Sequencer.SequenceItem.Imaging.TakeExposure)] = () => new OpenAstroAra.Sequencer.SequenceItem.Imaging.TakeExposure(cameraMediator, imagingMediator),
            [typeof(CoolCamera)] = () => new CoolCamera(cameraMediator),
            [typeof(WarmCamera)] = () => new WarmCamera(cameraMediator),
            [typeof(SetUSBLimit)] = () => new SetUSBLimit(cameraMediator),
            [typeof(SetReadoutMode)] = () => new SetReadoutMode(cameraMediator),
            [typeof(DewHeater)] = () => new DewHeater(cameraMediator),
            // §38k-14 — guider instructions on the existing guider stub.
            // StartGuiding / StopGuiding depend only on IGuiderMediator.
            [typeof(StartGuiding)] = () => new StartGuiding(guiderMediator),
            [typeof(StopGuiding)] = () => new StopGuiding(guiderMediator),
            // §38k-22 — Dither additionally needs IProfileService (settings);
            // satisfied by the HeadlessProfileService stub.
            // §42.2 — a restart after a long guiding gap re-centers the target first.
            [typeof(Dither)] = () => new Dither(guiderMediator, profileService, guidingRestartRecenter),
            // §38k-16 — rotator instruction. MoveRotatorMechanical depends
            // only on IRotatorMediator.
            [typeof(MoveRotatorMechanical)] = () => new MoveRotatorMechanical(rotatorMediator),
            // §38k-17 — switch instruction. SetSwitchValue depends only on
            // ISwitchMediator.
            [typeof(SetSwitchValue)] = () => new SetSwitchValue(switchMediator),
            // §38k-18 — dome instructions. Five depend only on IDomeMediator;
            // Enable/DisableDomeSynchronization also take the telescope stub.
            // (SynchronizeDome follows below: it also needs an IDomeFollower.)
            [typeof(OpenDomeShutter)] = () => new OpenDomeShutter(domeMediator),
            [typeof(CloseDomeShutter)] = () => new CloseDomeShutter(domeMediator),
            [typeof(ParkDome)] = () => new ParkDome(domeMediator),
            [typeof(FindHomeDome)] = () => new FindHomeDome(domeMediator),
            [typeof(SlewDomeAzimuth)] = () => new SlewDomeAzimuth(domeMediator),
            [typeof(EnableDomeSynchronization)] = () => new EnableDomeSynchronization(domeMediator, telescopeMediator),
            [typeof(DisableDomeSynchronization)] = () => new DisableDomeSynchronization(domeMediator, telescopeMediator),
            // §38k-21 — SynchronizeDome needs an IDomeFollower; the daemon passes the real
            // DomeFollower (#1689), the factory default is the headless stub.
            [typeof(SynchronizeDome)] = () => new SynchronizeDome(domeMediator, domeFollower, telescopeMediator),
            // §38k-22 — filter-wheel instruction. SwitchFilter needs
            // IProfileService (filter list) + IFilterWheelMediator.
            [typeof(SwitchFilter)] = () => new SwitchFilter(profileService, filterWheelMediator),
            // §38 NINA import fidelity — autofocus + center-and-rotate steps (one per target in
            // real plans). Execution goes through the §59 AutofocusSweepService and the §28
            // CenteringService (Program.cs wires both); a null executor keeps the prototype
            // JSON-resolvable and its Execute fails loudly rather than skipping focus/centering.
            [typeof(RunAutofocus)] = () => new RunAutofocus(autofocusExecutor),
            [typeof(CenterAndRotate)] = () => new CenterAndRotate(centeringExecutor, rotatorMediator),
            // #1264 — the unguided tracking probe (run builder emits it only without a guider).
            // Executes through ITrackingProbeExecutor (TrackingProbeService in Program.cs DI); a
            // null executor keeps the prototype JSON-resolvable and its Execute fails loudly.
            [typeof(OpenAstroAra.Sequencer.SequenceItem.Telescope.TrackingProbe)] = () => new OpenAstroAra.Sequencer.SequenceItem.Telescope.TrackingProbe(trackingProbeExecutor),
            // §48.3 — the auto-exposure flat set. Executes through IFlatCaptureExecutor
            // (FlatCaptureService in Program.cs DI); a null executor keeps the prototype
            // JSON-resolvable and its Execute fails loudly rather than skipping flats.
            [typeof(FlatPanelFlats)] = () => new FlatPanelFlats(flatCaptureExecutor),
            // §48.4 — the twilight sky-flat set. Reuses the same IFlatCaptureExecutor seam
            // (CaptureSkyFlatSetAsync); the generated sky sequence carries its own
            // WaitForSunAltitude + SlewScopeToAltAz around this leaf, so the leaf only owns
            // the drift-reprobe capture. Null executor keeps the prototype JSON-resolvable.
            [typeof(SkyFlats)] = () => new SkyFlats(flatCaptureExecutor),
            // Opens the panel's cover before a lens-cap pause (bias/darks after the flats).
            [typeof(OpenFlatPanelCover)] = () => new OpenFlatPanelCover(flatCaptureExecutor),
            // #1757 — closes the cover for unattended bias/darks; fails (never skips) without a motorised cover.
            [typeof(CloseFlatPanelCover)] = () => new CloseFlatPanelCover(flatCaptureExecutor),
            // §38k-22 — Connect dir. ConnectEquipment, DisconnectEquipment, DisconnectAllEquipment (and the
            // ReconnectTrigger below) take only the connections port (#1545); ConnectAll/SwitchProfile still
            // take the full 11 device mediators and IProfileService (profile-first). Real connect/disconnect
            // orchestration runs at execution time; these register so saved bodies resolve.
            [typeof(ConnectAllEquipment)] = () => new ConnectAllEquipment(
                profileService, cameraMediator, filterWheelMediator, focuserMediator,
                rotatorMediator, telescopeMediator, guiderMediator, switchMediator,
                flatDeviceMediator, weatherDataMediator, domeMediator, safetyMonitorMediator),
            [typeof(ConnectEquipment)] = () => new ConnectEquipment(equipmentConnections),
            [typeof(DisconnectAllEquipment)] = () => new DisconnectAllEquipment(equipmentConnections),
            [typeof(DisconnectEquipment)] = () => new DisconnectEquipment(equipmentConnections),
            [typeof(SwitchProfile)] = () => new SwitchProfile(
                profileService, cameraMediator, filterWheelMediator, focuserMediator,
                rotatorMediator, telescopeMediator, guiderMediator, switchMediator,
                flatDeviceMediator, weatherDataMediator, domeMediator, safetyMonitorMediator),

            // §38k-7 / §38.10a — the condition prototypes. LoopCondition and TimeSpanCondition are
            // parameterless; the two astrometry conditions read the site (and custom horizon) from the
            // store-backed profile and are skipped without the natives (Instantiate). TimeCondition needs the time sources.
            [typeof(LoopCondition)] = () => new LoopCondition(),
            [typeof(TimeSpanCondition)] = () => new TimeSpanCondition(),
            [typeof(AboveHorizonCondition)] = () => new AboveHorizonCondition(profileService),
            [typeof(AltitudeCondition)] = () => new AltitudeCondition(profileService),
            [typeof(TimeCondition)] = () => new TimeCondition(timeSources, clockTime),
            [typeof(SequenceRootContainer)] = () => new SequenceRootContainer(),
            [typeof(SequentialContainer)] = () => new SequentialContainer(),
            [typeof(ParallelContainer)] = () => new ParallelContainer(),
            // §38 NINA import fidelity — the per-target block and the dominant imaging
            // block in real NINA exports. Both are SequentialContainer subclasses (they
            // carry a Strategy.$type, so the item converter routes them here). Registering
            // the prototypes lets DeepSkyObjectContainer / SmartExposure resolve to the
            // real types instead of degrading to UnknownSequenceContainer (which gutted
            // the bulk of every imported plan).
            [typeof(DeepSkyObjectContainer)] = () => new DeepSkyObjectContainer(profileService),
            [typeof(SmartExposure)] = () => new SmartExposure(),
            // §58 — the meridian-flip trigger. Decision logic (#362) depends only on the telescope mediator
            // + profile; the §58.4 orchestration runs through the injected executor. Registering the
            // prototype lets a saved sequence body with a MeridianFlipTrigger resolve to the real trigger
            // (rather than falling back to UnknownSequenceTrigger).
            [typeof(MeridianFlipTrigger)] = () => new MeridianFlipTrigger(profileService, telescopeMediator, meridianFlipExecutor),
            // §38 NINA import fidelity — the most common trigger in real plans (one per
            // Smart Exposure). Carries AfterExposures + a TriggerRunner holding a Dither.
            [typeof(DitherAfterExposures)] = () => new DitherAfterExposures(),
            // §38 NINA import fidelity — the autofocus sibling: run autofocus every N exposures.
            [typeof(AutofocusAfterExposures)] = () => new AutofocusAfterExposures(autofocusConditionGate),
            // #1264 — the unguided exposure guard: steps the imaging loop's exposure down/up a rung
            // from the §59.5 analysis of each light (through IFrameQualityFeed).
            [typeof(OpenAstroAra.Sequencer.Trigger.Telescope.UnguidedExposureGuard)] = () => new OpenAstroAra.Sequencer.Trigger.Telescope.UnguidedExposureGuard(frameQualityFeed),
            // #1195 — NINA's Center After Drift: solve every Nth saved light from disk, re-centre through the
            // §28 centering seam when the field has drifted past the threshold. Removed in #242, re-ported
            // so imported plans no longer load it as UnknownSequenceTrigger.
            [typeof(OpenAstroAra.Sequencer.Trigger.Platesolving.CenterAfterDriftTrigger)] = () => new OpenAstroAra.Sequencer.Trigger.Platesolving.CenterAfterDriftTrigger(
                frameSolveFeed, centeringExecutor, telescopeMediator, cameraMediator),
            // §59.5 — the remaining autofocus trigger family: time interval, focuser-temp
            // delta, HFR drift, first-use-of-a-filter. Each reads the daemon's session
            // image history (plus the equipment it observes) and consults the §59.9
            // conditions gate before firing; the RunAutofocus inside a deserialized
            // TriggerRunner resolves through the item prototype above, which carries the
            // §59 sweep executor.
            [typeof(AutofocusAfterTimeTrigger)] = () => new AutofocusAfterTimeTrigger(imageHistory, autofocusConditionGate),
            [typeof(AutofocusAfterTemperatureChangeTrigger)] = () => new AutofocusAfterTemperatureChangeTrigger(focuserMediator, imageHistory, autofocusConditionGate),
            [typeof(AutofocusAfterHFRIncreaseTrigger)] = () => new AutofocusAfterHFRIncreaseTrigger(imageHistory, filterWheelMediator, autofocusConditionGate),
            [typeof(AutofocusAfterFilterChange)] = () => new AutofocusAfterFilterChange(filterWheelMediator, imageHistory, profileService, autofocusConditionGate),
            // Previously unregistered: a WILMA-added or NINA-imported ReconnectTrigger
            // degraded to UnknownSequenceTrigger (never fired). Its ConnectEquipment item
            // prototype was already registered above; the trigger itself was missed.
            [typeof(ReconnectTrigger)] = () => new ReconnectTrigger(equipmentConnections),
        };
        if (constructorOverrides != null) {
            foreach (var (type, make) in constructorOverrides) constructors[type] = make;
        }

        return new HeadlessSequencerFactory(
            items: Instantiate(SequenceKinds.All.Where(e => e.Role == SequenceNodeRole.Instruction), constructors).Cast<ISequenceItem>().ToList(),
            conditions: Instantiate(SequenceKinds.All.Where(e => e.Role == SequenceNodeRole.Condition), constructors).Cast<ISequenceCondition>().ToList(),
            container: Instantiate(SequenceKinds.All.Where(e => e.Role == SequenceNodeRole.Container), constructors).Cast<ISequenceContainer>().ToList(),
            triggers: Instantiate(SequenceKinds.All.Where(e => e.Role == SequenceNodeRole.Trigger), constructors).Cast<ISequenceTrigger>().ToList(),
            // A body naming a provider (the full UI's editor does) resolves through this list.
            dateTimeProviders: timeSources);
    }

    // The default when no connections are handed in: the prototypes construct, and a run refuses.
    private sealed class UnwiredEquipmentConnections : IEquipmentConnections {
        public bool IsConnected(EquipmentRole role) => false;
        public bool IsDisconnectedByUser(EquipmentRole role) => false;
        public Task ConnectAsync(EquipmentRole role, CancellationToken ct) => throw Unwired();
        public Task RecoverAsync(EquipmentRole role, CancellationToken ct) => throw Unwired();
        public Task DisconnectAsync(EquipmentRole role, CancellationToken ct) => throw Unwired();
        private static EquipmentRefusedException Unwired() =>
            new(EquipmentRefusalCause.NotSupported, "This sequencer was built without equipment connections");
    }
}