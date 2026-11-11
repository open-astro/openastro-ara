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

/// <summary>#1580 — one camera from AlpacaBridge's <c>attachedcameras</c> list (<c>Index</c>, <c>Name</c>, <c>Serial</c>).</summary>
public sealed record AlpacaAttachedCamera(int Index, string Name, string Serial);

/// <summary>How an add ended: <see cref="Added"/> (ErrorNumber 0), <see cref="Refused"/> (AlpacaBridge answered with an
/// error and its message), <see cref="Unconfirmed"/> (no usable answer: the device may or may not exist now).</summary>
public enum AlpacaAddOutcome { Added, Refused, Unconfirmed }

/// <summary>The result of <see cref="IAlpacaManagementClient.AddDeviceAsync"/>. <paramref name="Message"/> is AlpacaBridge's
/// own text; <paramref name="Warning"/> is the part of a success message that follows its "Warning: " lead.</summary>
public sealed record AlpacaAddResult(AlpacaAddOutcome Outcome, string Message, string? Warning);
