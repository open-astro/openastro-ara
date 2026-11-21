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
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>Result of asking systemd to power the host off. <see cref="Message"/> is the helper's
/// own output, for the caller to report when <see cref="Started"/> is false.</summary>
public readonly record struct HostPoweroffResult(bool Started, string Message);

/// <summary>
/// Powers the host off through the root-owned <c>openastroara-poweroff.service</c> unit (ADR 0017):
/// the daemon holds no root privilege, so it asks systemd to start the unit and polkit allows
/// that one unit and the <c>start</c> verb.
/// </summary>
public interface IHostPoweroffStarter {
    Task<HostPoweroffResult> StartAsync(CancellationToken ct);
}

/// <summary><c>systemctl start</c>-backed <see cref="IHostPoweroffStarter"/>. No sudo, no pkexec.</summary>
public sealed class SystemctlHostPoweroffStarter(
    Func<string, string[], CancellationToken, Task<(int ExitCode, string Output)>> run) : IHostPoweroffStarter {

    internal const string Unit = "openastroara-poweroff.service";

    public async Task<HostPoweroffResult> StartAsync(CancellationToken ct) {
        // --no-block: the host goes down while the job runs, so a blocking start may never return.
        var (exitCode, output) = await run("systemctl", [Systemctl.NoAskPassword, "start", "--no-block", Unit], ct).ConfigureAwait(false);
        return exitCode == 0
            ? new HostPoweroffResult(true, string.Empty)
            : new HostPoweroffResult(false,
                string.IsNullOrWhiteSpace(output) ? "systemd refused to start the poweroff helper." : output.Trim());
    }
}