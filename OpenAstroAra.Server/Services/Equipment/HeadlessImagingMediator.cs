// The event member satisfies the mediator interface but is never raised by the headless stub, so
// CS0067 "event is never used" is expected and intentionally suppressed for the whole file.
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

using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Equipment.Model;
using OpenAstroAra.Image.Interfaces;

namespace OpenAstroAra.Server.Services.Equipment;

/// <summary>
/// §14e PRb — headless no-op stub for <see cref="IImagingMediator"/>, the default the
/// <see cref="HeadlessSequencerFactory"/> uses to construct the <c>TakeExposure</c> prototype when
/// no real imaging wiring is supplied (tests / body round-trips). Every capture member surfaces
/// <see cref="NotSupportedException"/> — prototypes are cloned/validated, never executed, and a
/// stub that fabricated an <see cref="IExposureData"/> would hide real wiring mistakes.
/// </summary>
public sealed class HeadlessImagingMediator : IImagingMediator {

    public Task<IExposureData> CaptureImage(CaptureSequence sequence, CancellationToken token, IProgress<ApplicationStatus>? progress, string targetName = "") =>
        Task.FromException<IExposureData>(new NotSupportedException(
            "Headless imaging stub cannot capture; the real CameraService-backed IImagingMediator swaps in at the DI registration point."));

    public Task<IRenderedImage> CaptureAndPrepareImage(CaptureSequence sequence, PrepareImageParameters parameters, CancellationToken token, IProgress<ApplicationStatus>? progress) =>
        Task.FromException<IRenderedImage>(new NotSupportedException("Headless imaging stub cannot render."));

    public Task<IRenderedImage> PrepareImage(IImageData imageData, PrepareImageParameters parameters, CancellationToken token) =>
        Task.FromException<IRenderedImage>(new NotSupportedException("Headless imaging stub cannot render."));

    public Task<IRenderedImage> PrepareImage(IExposureData imageData, PrepareImageParameters parameters, CancellationToken token) =>
        Task.FromException<IRenderedImage>(new NotSupportedException("Headless imaging stub cannot render."));

    public Task<bool> StartLiveView(CaptureSequence sequence, CancellationToken ct) =>
        Task.FromException<bool>(new NotSupportedException("Headless imaging stub cannot stream a live view."));

    public void DestroyImage() { }
    public int GetImageRotation() => 0;
    public void SetImageRotation(int rotation) { }
    public void SetSubSambleRectangle(ObservableRectangle observableRectangle) { }

    public event EventHandler<ImagePreparedEventArgs>? ImagePrepared;
}
