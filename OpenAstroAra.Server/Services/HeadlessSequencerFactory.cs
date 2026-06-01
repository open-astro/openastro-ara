#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Windows.Data;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Sequencer;
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.SequenceItem.SafetyMonitor;
using OpenAstroAra.Sequencer.SequenceItem.Utility;
using OpenAstroAra.Sequencer.Trigger;
using OpenAstroAra.Sequencer.Utility.DateTimeProvider;
using OpenAstroAra.Server.Services.Equipment;

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
    public IList<ISequenceContainer> Container { get; }
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
        Container = container ?? new List<ISequenceContainer>();
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
        (T)(Container.FirstOrDefault(x => x.GetType() == typeof(T))?.Clone() ?? default(T)!);

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
            ISafetyMonitorMediator? safetyMonitorMediator = null) {
        // §38k-9 — equipment-mediator stubs default to no-op headless impls
        // so call sites that don't yet have real Alpaca-backed mediators
        // still get a usable prototype set. As real drivers land (§14e
        // Alpaca simulator pinning gates this), Program.cs's DI can hand
        // in real mediators here instead.
        safetyMonitorMediator ??= new HeadlessSafetyMonitorMediator();

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
