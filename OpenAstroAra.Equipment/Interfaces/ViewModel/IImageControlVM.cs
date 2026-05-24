#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Image.ImageData;
using OpenAstroAra.Equipment.Equipment.MyCamera;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Core.Utility.WindowService;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using System;

namespace OpenAstroAra.Equipment.Interfaces.ViewModel {

    public interface IImageControlVM : IDockableVM {
        bool AutoStretch { get; set; }
        BahtinovImage BahtinovImage { get; }
        ObservableRectangle BahtinovRectangle { get; set; }
        ICommand CancelPlateSolveImageCommand { get; }
        bool DetectStars { get; set; }
        ICommand DragMoveCommand { get; }
        double DragResizeBoundary { get; }
        ICommand DragStartCommand { get; }
        ICommand DragStopCommand { get; }
        BitmapSource Image { get; set; }
        IAsyncCommand InspectAberrationCommand { get; }
        bool IsLiveViewEnabled { get; }
        IAsyncCommand PlateSolveImageCommand { get; }
        AsyncCommand<bool> PrepareImageCommand { get; }
        IRenderedImage RenderedImage { get; set; }
        bool ShowBahtinovAnalyzer { get; set; }
        bool ShowCrossHair { get; set; }
        ApplicationStatus Status { get; set; }
        IWindowServiceFactory WindowServiceFactory { get; set; }
        int ImageRotation { get; set; }

        void Dispose();

        Task<IRenderedImage> PrepareImage(IImageData data, PrepareImageParameters parameters, CancellationToken cancelToken);

        void UpdateDeviceInfo(CameraInfo cameraInfo);

        event EventHandler<ImagePreparedEventArgs> ImagePrepared;
    }
}