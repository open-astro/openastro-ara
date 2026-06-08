#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.Trigger;
using OpenAstroAra.Sequencer.Utility;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Container {

    public interface ISequenceRootContainer : ISequenceContainer, ITriggerable {

        void AddRunningItem(ISequenceItem item);

        void RemoveRunningItem(ISequenceItem item);

        void SkipCurrentRunningItems();

        IReadOnlyCollection<ISequenceItem> GetCurrentRunningItems();

        string SequenceTitle { get; set; }

        [SuppressMessage("Design", "CA1030:Use events where appropriate",
            Justification = "RaiseFailureEvent IS the raise mechanism for the FailureEvent below; it is async (Task) so that subscriber dispatch can be awaited before the run continues, which a plain event cannot express. CA1030 sanctions suppression for the event-raising member of an event-based design.")]
        Task RaiseFailureEvent(ISequenceEntity sender, Exception ex);
        [SuppressMessage("Design", "CA1003:Use generic event handler instances",
            Justification = "Intentional asynchronous event: handlers return Task so the raiser can await them (Func<object, SequenceEntityFailureEventArgs, Task>). The void-returning EventHandler<T> cannot express awaitable handlers, so the generic-EventHandler form CA1003 recommends is not applicable.")]
        event Func<object, SequenceEntityFailureEventArgs, Task>? FailureEvent;
    }
}