#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Sequencer {

    // Phase 0.5p2 net10.0 conversion: original SequenceHasChanged ran a WPF
    // MyMessageBox YesNo prompt before detaching a modified sub-tree. In the
    // headless server changes are committed via REST; the §38 sequence
    // endpoints don't need a confirm-dialog round trip — they just apply.
    // Kept as a static delegate consumers can replace if a future
    // command-level confirm gate is needed.
    public class SequenceHasChanged : OpenAstroAra.Core.Utility.BaseINPC, ISequenceHasChanged {
        protected bool hasChanged;
        public virtual bool HasChanged { get => hasChanged; set => hasChanged = value; }
        public virtual void ClearHasChanged() => hasChanged = false;
        public virtual bool AskHasChanged(string name) => false;
    }
}
