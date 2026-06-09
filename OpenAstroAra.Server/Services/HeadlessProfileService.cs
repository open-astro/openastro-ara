// Headless server stub: the change-notification events in this file satisfy the
// IProfileService contract but are never raised server-side (WILMA edits settings
// over REST against IProfileStore/profile.json), so CS0067 "event is never used"
// is expected here and intentionally suppressed for the whole file.
#pragma warning disable CS0067

#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Utility;
using OpenAstroAra.Profile;
using OpenAstroAra.Profile.Interfaces;
using System.Globalization;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §38k-22 — headless no-op stub for <see cref="IProfileService"/>.
///
/// Purpose is narrow and deliberate: the §38k sequence-instruction prototypes
/// (<c>Dither</c>, <c>SwitchFilter</c>, <c>ConnectAllEquipment</c>, …) take an
/// <see cref="IProfileService"/> in their constructors, and the
/// <see cref="HeadlessSequencerFactory"/> needs to *construct* them so they
/// resolve from a saved JSON sequence body. Prototypes are only ever cloned /
/// registered / validated — never executed — so a non-null service with a
/// default <see cref="ActiveProfile"/> and no-op mutators/events is sufficient.
///
/// This intentionally does NOT solve the profile source-of-truth question. The
/// daemon persists user-edited settings via <c>IProfileStore</c> /
/// <c>profile.json</c> (the WILMA REST surface); the inherited NINA
/// <c>ProfileService</c> has its own XML store. Reconciling those — so that an
/// *executing* instruction reads the settings the user actually edited — is an
/// execution-engine concern (there is no execution engine yet) and is decided
/// then. At that point the DI registration swaps this stub for the real service,
/// exactly as the equipment-mediator stubs swap for real Alpaca drivers.
/// </summary>
public sealed class HeadlessProfileService : IProfileService {

    public bool ProfileWasSpecifiedFromCommandLineArgs => false;

    public AsyncObservableCollection<ProfileMeta> Profiles { get; } = new();

    // A single default in-memory profile. Instructions read settings off this at
    // execution time (none execute today); the real, user-edited profile wires in
    // with the execution engine.
    public IProfile ActiveProfile { get; } = new OpenAstroAra.Profile.Profile();

    public bool Clone(ProfileMeta profileInfo) => false;
    public void Add() { }
    public bool SelectProfile(ProfileMeta profileInfo) => false;
    public bool RemoveProfile(ProfileMeta profileInfo) => false;
    public void ChangeLocale(CultureInfo language) { }
    public void ChangeLatitude(double latitude) { }
    public void ChangeLongitude(double longitude) { }
    public void ChangeElevation(double elevation) { }
    public void ChangeHorizon(string horizonFilePath) { }
    public void Release() { }

    public event EventHandler? LocaleChanged;
    public event EventHandler? LocationChanged;
    public event EventHandler? ProfileChanging;
    public event EventHandler? ProfileChanged;
    public event EventHandler? HorizonChanged;
}
