#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenAstroAra.Server.Services;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// The host-side seams every in-process daemon host in the tests replaces (<see cref="Contract.AraHost"/>
    /// and each <c>Program.BuildApp(..., TestHostSeams.Replace)</c>). The real ones shell out to
    /// <c>systemctl</c>: with no guider reachable the §63.3 recovery and the connect path call
    /// <c>systemctl start|restart openastro-guider</c>, which on a Linux desktop with a polkit agent pops a
    /// password dialog for every test run. The storage and update helpers need no fake: they reach
    /// <c>systemctl</c> only when their packaged unit template exists in <c>/etc/systemd/system</c>, and only
    /// on an explicit configure / apply request.
    /// </summary>
    internal static class TestHostSeams {

        /// <summary>Swap the systemctl-backed services for the fakes below.</summary>
        public static void Replace(IServiceCollection services) {
            services.Replace(ServiceDescriptor.Singleton<IGuiderProcessSupervisor>(new FakeGuiderProcessSupervisor()));
            services.Replace(ServiceDescriptor.Singleton<IHostPoweroffStarter>(new FakeHostPoweroffStarter()));
        }

        /// <summary>No systemd: the status is <see cref="GuiderProcessStatus.Unknown"/>, as on a host with no
        /// <c>systemctl</c>, and start/restart only count.</summary>
        internal sealed class FakeGuiderProcessSupervisor : IGuiderProcessSupervisor {
            private int starts;
            private int restarts;

            public int Starts => Volatile.Read(ref starts);

            public int Restarts => Volatile.Read(ref restarts);

            public Task<GuiderProcessStatus> QueryStatusAsync(CancellationToken ct) => Task.FromResult(GuiderProcessStatus.Unknown);

            public void RequestRestart() => Interlocked.Increment(ref restarts);

            public void RequestStart() => Interlocked.Increment(ref starts);
        }

        /// <summary>Never powers anything off; reports the refusal a host without the unit would give.</summary>
        internal sealed class FakeHostPoweroffStarter : IHostPoweroffStarter {
            private int calls;

            public int Calls => Volatile.Read(ref calls);

            public Task<HostPoweroffResult> StartAsync(CancellationToken ct) {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new HostPoweroffResult(false, "test host: poweroff is faked"));
            }
        }
    }
}