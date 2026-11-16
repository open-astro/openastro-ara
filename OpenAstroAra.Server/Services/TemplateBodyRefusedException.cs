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

namespace OpenAstroAra.Server.Services;

/// <summary>An instantiated template body failed the sequence schema check (ADR 0018). The
/// instantiate endpoint maps this type, and only this type, to 422.</summary>
public sealed class TemplateBodyRefusedException : Exception {
    public TemplateBodyRefusedException() { }
    public TemplateBodyRefusedException(string reason) : base(reason) { }
    public TemplateBodyRefusedException(string reason, Exception innerException) : base(reason, innerException) { }
}