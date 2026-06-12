// The IProfileService event members satisfy the interface but are never raised server-side (ARA
// v0.0.1 is single-profile; locale/location/horizon changes flow through the store hydration, not
// NINA's event surface), so CS0067 "event is never used" is expected and suppressed for the file —
// same as HeadlessProfileService.
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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Profile;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §14e profile source-of-truth — the daemon's real <see cref="IProfileService"/>, replacing the
/// <see cref="HeadlessProfileService"/> stub at the DI registration point.
///
/// The daemon persists user-edited settings via <see cref="IProfileStore"/> (<c>profile.json</c>,
/// the WILMA REST surface), while NINA's sequence instructions read
/// <see cref="IProfileService.ActiveProfile"/>. This service is the bridge: it holds one live
/// <see cref="Profile"/> and hydrates the NINA settings that have a clean store counterpart from
/// the store — at construction and again on every store <c>Put</c> (via
/// <see cref="IProfileStore.Changed"/>) — so an <em>executing</em> instruction reads the values the
/// user actually edited, not compile-time defaults.
///
/// Mapped sections (see <see cref="ProfileStoreMapper"/>): site → astrometry (lat/long/elevation),
/// PHD2 → guider (host/port/dither/settle), autofocus → focuser (step/initial-offset/exposure/binning),
/// storage+filenames → image file (path/pattern/format), plate-solve numerics, safety-policy
/// meridian fields. Sections with no NINA-profile counterpart (notifications, diagnostics mode,
/// stretch defaults, equipment auto-connect) are daemon-side concerns and stay store-only.
/// Multi-profile management (<see cref="Profiles"/>/<see cref="SelectProfile"/>) remains inert —
/// ARA is single-profile in v0.0.1 (§37); the mutator surface mirrors the headless stub.
/// </summary>
public sealed partial class StoreBackedProfileService : IProfileService, IDisposable {

    private readonly IProfileStore _store;
    private readonly ILogger<StoreBackedProfileService> _logger;
    // Serializes hydrations: two concurrent REST PUTs both raise Changed, and NINA's settings
    // objects are MVVM property bags with no thread-safety of their own — Apply must not run
    // twice into the same Profile concurrently.
    private readonly object _hydrateLock = new();
    private volatile bool _disposed;

    public StoreBackedProfileService(IProfileStore store, ILogger<StoreBackedProfileService>? logger = null) {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _logger = logger ?? NullLogger<StoreBackedProfileService>.Instance;
        // Subscribe BEFORE the initial hydration so a PUT that lands mid-construction can't be
        // missed; _hydrateLock serializes its re-hydration against this one.
        _store.Changed += OnStoreChanged;
        Hydrate();
    }

    public bool ProfileWasSpecifiedFromCommandLineArgs => false;

    public AsyncObservableCollection<ProfileMeta> Profiles { get; } = new();

    /// <summary>
    /// The single live profile instructions read at execution time. The same instance for the
    /// daemon's lifetime — hydration mutates its settings in place (NINA settings raise
    /// PropertyChanged per field; <see cref="ProfileChanged"/> stays reserved for an actual
    /// profile *switch*, which ARA v0.0.1 doesn't do).
    /// </summary>
    public IProfile ActiveProfile { get; } = new OpenAstroAra.Profile.Profile();

    private void OnStoreChanged(object? sender, EventArgs e) => Hydrate();

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Store-changed event boundary: a hydration failure (e.g. an unparseable enum token in one section) must degrade to keeping the previous values and logging, never throw back into the REST PUT that triggered the event. CA1031's log-and-recover boundary applies.")]
    private void Hydrate() {
        try {
            lock (_hydrateLock) {
                // Guard INSIDE the lock: checked outside, a handler that passed the check could
                // still apply after Dispose flipped the flag (check and apply must be atomic).
                if (_disposed) {
                    return;
                }
                ProfileStoreMapper.Apply(_store, ActiveProfile);
            }
        } catch (Exception ex) {
            LogHydrateFailed(ex);
        }
    }

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

    public void Dispose() {
        lock (_hydrateLock) {
            if (_disposed) {
                return;
            }
            _disposed = true;
        }
        _store.Changed -= OnStoreChanged;
    }

    public event EventHandler? LocaleChanged;
    public event EventHandler? LocationChanged;
    public event EventHandler? ProfileChanging;
    public event EventHandler? ProfileChanged;
    public event EventHandler? HorizonChanged;

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "profile hydration from the store failed; ActiveProfile keeps its previous values")]
    private partial void LogHydrateFailed(Exception ex);
}

/// <summary>
/// Pure store→profile section mapper (internal static so every mapping is sim-free unit-testable).
/// Only fields with a clean semantic counterpart are mapped; everything else keeps the NINA default
/// (documented per method). Mapping direction is one-way store→profile: the store is the
/// source-of-truth the WILMA client edits, and nothing in the headless daemon mutates the NINA
/// profile settings back.
/// </summary>
internal static class ProfileStoreMapper {

    // Two accepted consistency windows, both self-converging and both inherent to NINA's mutable
    // profile model (instructions read ActiveProfile with no lock, exactly as NINA's own UI-edits-
    // while-sequencing does):
    // 1. Cross-section: each Get takes the store lock independently, so a PUT landing mid-pass can
    //    yield pre-PUT section A + post-PUT section B. The PUT fires its own Changed, the follow-up
    //    hydration runs, and the steady state is consistent.
    // 2. Within-section: an instruction reading while ApplySite writes Latitude→Longitude→Elevation
    //    sequentially can observe new Latitude + old Longitude for one read. Individual doubles
    //    don't tear (atomic loads), the window is a user-save during a running instruction, and the
    //    next read is consistent.
    internal static void Apply(IProfileStore store, IProfile profile) {
        ApplySite(store.GetSiteSettings(), profile);
        ApplyPhd2(store.GetPhd2Settings(), profile);
        ApplyAutofocus(store.GetAutofocusSettings(), profile);
        ApplyImageFile(store.GetStorageSettings(), profile);
        ApplyPlateSolve(store.GetPlateSolveSettings(), profile);
        ApplyMeridian(store.GetSafetyPolicies(), profile);
    }

    internal static void ApplySite(SiteSettingsDto site, IProfile profile) {
        profile.AstrometrySettings.Latitude = site.LatitudeDeg;
        profile.AstrometrySettings.Longitude = site.LongitudeDeg;
        profile.AstrometrySettings.Elevation = site.ElevationM;
        // Horizon file / Bortle / seeing / twilight have no NINA-profile counterpart consumed by
        // instructions; they stay store-only.
    }

    internal static void ApplyPhd2(Phd2SettingsDto phd2, IProfile profile) {
        var guider = profile.GuiderSettings;
        guider.PHD2ServerHost = phd2.Host;
        guider.PHD2ServerPort = phd2.Port;
        guider.DitherPixels = phd2.DitherPixels;
        guider.SettlePixels = phd2.SettlePixels;
        guider.SettleTime = phd2.SettleTimeSec;
        guider.SettleTimeout = phd2.SettleTimeoutSec;
        // §63.5 guider-engine config — owned by ARA's profile, pushed to the guider daemon on connect
        // (guider-e-2 maps these onto set_profile_setup / set_algo_param / set_dec_guide_mode). Normalized
        // here (not thrown — this runs on profile hydration) so the value guider-e-2 reads is always one PHD2
        // accepts: aggressiveness ∈ [0,1], minimum-move ≥ 0, dec-guide-mode in the known set.
        guider.GuideFocalLength = phd2.GuideFocalLength;
        guider.GuidePixelSize = phd2.GuidePixelSize;
        guider.RAAggressiveness = Math.Clamp(phd2.RaAggressiveness, 0.0, 1.0);
        guider.DecAggressiveness = Math.Clamp(phd2.DecAggressiveness, 0.0, 1.0);
        guider.MinimumMove = Math.Max(0.0, phd2.MinimumMove);
        guider.DecGuideMode = NormalizeDecGuideMode(phd2.DecGuideMode);
        // DitherEnabled / DitherEveryNFrames are per-sequence concerns in NINA (the Dither
        // instruction + trigger carry them), and Phd2Profile selection is a §63 connect-time
        // concern — store-only here.
    }

    internal static void ApplyAutofocus(AutofocusSettingsDto autofocus, IProfile profile) {
        var focuser = profile.FocuserSettings;
        focuser.AutoFocusStepSize = autofocus.StepSize;
        focuser.AutoFocusInitialOffsetSteps = autofocus.Steps;
        focuser.AutoFocusExposureTime = autofocus.ExposureSeconds;
        focuser.AutoFocusBinning = ClampToShort(autofocus.Binning);
        // Method / filter / trigger policies map onto the autofocus orchestration (not ported yet)
        // rather than FocuserSettings; revisit when the AF pipeline lands.
    }

    internal static void ApplyImageFile(StorageSettingsDto storage, IProfile profile) {
        var imageFile = profile.ImageFileSettings;
        imageFile.FilePath = storage.SaveDirectory;
        // The store template uses `\\` separators verbatim (matches the WILMA client); NINA's
        // FilePattern consumes the same token syntax.
        imageFile.FilePattern = storage.FilenameTemplate;
        imageFile.FileType = MapFileType(storage.FileFormat);
        // Compression rides on the §72 FITS writer (fits_rice/fits_gzip), not on FileType.
    }

    internal static void ApplyPlateSolve(PlateSolveSettingsDto plateSolve, IProfile profile) {
        var settings = profile.PlateSolveSettings;
        settings.SearchRadius = plateSolve.SearchRadiusDeg;
        settings.DownSampleFactor = plateSolve.DownsampleFactor;
        settings.NumberOfAttempts = plateSolve.MaxIterations;
        settings.Sync = plateSolve.SyncToCoordinates;
        settings.SlewToTarget = plateSolve.CenterAfterSlew;
        // NINA's centering Threshold is in arcMINUTES; the store stores arcseconds.
        settings.Threshold = plateSolve.ConvergenceToleranceArcsec / 60.0;
        // Engine/path selection maps onto the §31 solver wiring (not ported yet) — store-only here.
    }

    internal static void ApplyMeridian(SafetyPoliciesDto policies, IProfile profile) {
        var meridian = profile.MeridianFlipSettings;
        meridian.PauseTimeBeforeMeridian = policies.MeridianPauseMin;
        meridian.Recenter = policies.MeridianRecenter;
        // MeridianFlipAuto gates the flip *trigger* (sequence-level, not ported yet); the guider
        // re-calibration flag rides on the §63 PHD2 wiring — store-only here.
    }

    // FileFormat tokens per the §29 contract: fits / xisf / fits_rice / fits_gzip. The compressed
    // FITS variants are still FITS container-wise.
    internal static FileType MapFileType(string fileFormat) =>
        string.Equals(fileFormat, "xisf", StringComparison.OrdinalIgnoreCase)
            ? FileType.XISF
            : FileType.FITS;

    internal static short ClampToShort(int value) =>
        (short)Math.Clamp(value, short.MinValue, short.MaxValue);

    // §63.5 — coerce an unrecognized dec-guide-mode to "auto" so guider-e-2's set_dec_guide_mode never sends
    // PHD2 a value it would reject. Case-insensitive; the openapi enum documents the accepted set.
    private static readonly System.Collections.Generic.HashSet<string> DecGuideModes =
        new(System.StringComparer.OrdinalIgnoreCase) { "auto", "north", "south", "off" };

    internal static string NormalizeDecGuideMode(string? mode) {
        var m = mode?.Trim().ToLowerInvariant();
        return m != null && DecGuideModes.Contains(m) ? m : "auto";
    }
}
