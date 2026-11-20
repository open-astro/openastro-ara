#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>#2044 — red stub: the watch that stops tracking before the meridian with the flip off.</summary>
public sealed partial class MeridianStopWatch : BackgroundService {

    public MeridianStopWatch(IProfileStore profiles, ITelescopeMediator telescope, Func<ISequencerService?> sequencer,
            IWsBroadcaster? ws, ILogger<MeridianStopWatch> logger, TimeSpan? interval = null) {
        _profiles = profiles;
        _ = (telescope, sequencer, ws, logger, interval);
    }

    private readonly IProfileStore _profiles;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    internal Task TickAsync(CancellationToken ct) => _profiles is null ? Task.CompletedTask : Task.CompletedTask;
}
