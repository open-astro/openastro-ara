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

    // §63.9 fork identification. The connect handshake (PHD2Guider.Connect) resolves which guider it's
    // talking to from three sources, in order of authority: the synchronous get_version RPC result, the
    // catch-up "Version" event, then — for a pre-#57 daemon that exposes neither fork key — the legacy
    // version/subver substring. Kept as a pure static so the precedence is unit-testable without a socket.
    public sealed partial class PHD2Guider {

        // The fork's canonical id string (get_version.fork / Version.Fork). "openastro-phd2" is the
        // pre-rename value and is still accepted for back-compat with an older daemon build.
        internal const string OpenAstroGuiderFork = "openastro-guider";
        internal const string LegacyOpenAstroFork = "openastro-phd2";

        private string? _guiderFork;

        /// <summary>The connected guider's fork id ("openastro-guider" on the fork, the daemon's own
        /// fork string or "PHD2" upstream), resolved at connect. Null until the first connect.</summary>
        public string? GuiderFork {
            get => _guiderFork;
            private set {
                _guiderFork = value;
                RaisePropertyChanged();
            }
        }

        private bool _overlapSupport;

        /// <summary>Whether the connected daemon advertised overlapped/pipelined-RPC support — a fork-only
        /// capability (§63.9), the gate later slices use before issuing concurrent RPCs.</summary>
        public bool OverlapSupport {
            get => _overlapSupport;
            private set {
                _overlapSupport = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Resolve the guider fork from the get_version RPC result, the "Version" event, and the legacy
        /// version/subver strings — in that order of authority. An explicit non-OpenAstro fork string is
        /// authoritative (it does NOT fall through to the legacy substring); the legacy path only runs when
        /// no fork key is present at all (a pre-#57 daemon). Every source has both an RPC and an event
        /// slot; the RPC value is preferred and the event is the fallback — including for the legacy
        /// version/subver, so a fork marker the synchronous RPC carried in <c>phd_subver</c> isn't lost
        /// when the catch-up event is slow or missed. OverlapSupport prefers the RPC value, then the event,
        /// then false.
        /// </summary>
        public static GuiderForkIdentity IdentifyGuiderFork(
                string? rpcFork, bool? rpcOverlapSupport, string? rpcPhdVersion, string? rpcPhdSubver,
                string? eventFork, bool? eventOverlapSupport, string? eventPhdVersion, string? eventPhdSubver) {

            string? fork = !string.IsNullOrEmpty(rpcFork) ? rpcFork
                : (!string.IsNullOrEmpty(eventFork) ? eventFork : null);
            string? phdVersion = !string.IsNullOrEmpty(rpcPhdVersion) ? rpcPhdVersion : eventPhdVersion;
            string? phdSubver = !string.IsNullOrEmpty(rpcPhdSubver) ? rpcPhdSubver : eventPhdSubver;

            bool identified = fork is not null
                ? ForkStringIsOpenAstro(fork)
                : LegacyVersionLooksOpenAstro(phdVersion, phdSubver);

            string forkName = fork ?? (identified ? OpenAstroGuiderFork : "PHD2");
            bool overlap = rpcOverlapSupport ?? eventOverlapSupport ?? false;
            return new GuiderForkIdentity(identified, forkName, overlap);
        }

        private static bool ForkStringIsOpenAstro(string? fork) =>
            !string.IsNullOrEmpty(fork) &&
            (fork.Contains(OpenAstroGuiderFork, StringComparison.OrdinalIgnoreCase) ||
             fork.Contains(LegacyOpenAstroFork, StringComparison.OrdinalIgnoreCase));

        // Pre-#57 daemons carry no fork key — the fork used to announce itself through PHDSubver
        // ("openastroara"/"openastro-*" markers) or a dev-version suffix on PHDVersion.
        private static bool LegacyVersionLooksOpenAstro(string? phdVersion, string? phdSubver) =>
            SubstringMatchesOpenAstro(phdSubver) || SubstringMatchesOpenAstro(phdVersion);

        private static bool SubstringMatchesOpenAstro(string? s) =>
            !string.IsNullOrEmpty(s) &&
            (s.Contains("openastroara", StringComparison.OrdinalIgnoreCase) ||
             s.Contains(OpenAstroGuiderFork, StringComparison.OrdinalIgnoreCase) ||
             s.Contains(LegacyOpenAstroFork, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The resolved identity of a connected guider (§63.9): whether it's the openastro-guider
    /// fork, the reported fork string, and whether it advertised overlapped-RPC support.</summary>
    public readonly record struct GuiderForkIdentity(bool IsOpenAstroGuider, string ForkName, bool OverlapSupport);
}
