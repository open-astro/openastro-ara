#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2 {

    // §42.2 structured equipment-fault signal from openastro-guider's EquipmentDisconnected event (#57).
    // Distinct from PHD2ConnectionLost: the guide LINK is still up (the daemon is alive and self-
    // reconnecting), so this is a guiding-degraded condition, not a link drop — the session reacts per
    // the on_guider_lost policy but stays Connected (no §63.3 recovery). The paired EquipmentReconnected
    // event is informational (logged in ProcessEvent); it drives no reaction.
    public sealed partial class PHD2Guider {

        /// <summary>Raised when the daemon reports a device disconnect (e.g. the guide camera dropped).
        /// A guiding-degraded fault — the guide link itself is still up.</summary>
        public event EventHandler<EquipmentFaultEventArgs>? EquipmentFault;

        private void RaiseEquipmentFault(string deviceType, string reason, bool reconnecting) =>
            EquipmentFault?.Invoke(this, new EquipmentFaultEventArgs(deviceType, reason, reconnecting));
    }

    /// <summary>A daemon-reported device disconnect (§42.2). <see cref="Reconnecting"/> means the daemon
    /// is attempting a throttled auto-reconnect (which it may silently abandon — guider#66).</summary>
    public sealed class EquipmentFaultEventArgs(string deviceType, string reason, bool reconnecting) : EventArgs {
        public string DeviceType { get; } = deviceType;
        public string Reason { get; } = reason;
        public bool Reconnecting { get; } = reconnecting;
    }
}
