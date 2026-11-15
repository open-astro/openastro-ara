#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Services;

/// <summary>
/// How the daemon restarts itself. The packaged unit has no polkit rule for
/// <c>systemctl restart</c> (ADR 0017), so the daemon stops itself with a failure exit code and
/// <c>Restart=on-failure</c> brings it back (design/PORT_PLAYBOOK.md §34.7).
/// </summary>
public interface IDaemonRestarter {
    void Restart();
}

/// <summary>Stops the host gracefully with exit code 75 so systemd treats the exit as a failure and restarts the unit.</summary>
public sealed class ExitDaemonRestarter(Microsoft.Extensions.Hosting.IHostApplicationLifetime lifetime) : IDaemonRestarter {
    // 75 = EX_TEMPFAIL (sysexits.h): "try again later", any non-zero code satisfies Restart=on-failure.
    // StopApplication (not Environment.Exit) lets open files and the database close cleanly.
    internal const int RestartExitCode = 75;

    public void Restart() {
        Environment.ExitCode = RestartExitCode;
        lifetime.StopApplication();
    }
}