#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Locale;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Core.Utility.Notification;
using OpenAstroAra.Image.FileFormat;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.PlateSolving.Solvers {

    internal sealed class Dc3PinPointSolver : BaseSolver {
        private readonly Dc3PoinPointCatalog catalogType;
        private readonly string catalogRootDir;
        private readonly double maxMagnitude;
        private readonly double expansion;
        private readonly string allSkyApiKey;
        private readonly string allSkyApiHost;

        public Dc3PinPointSolver(IPlateSolveSettings plateSolveSettings) {
            catalogType = plateSolveSettings.PinPointCatalogType;
            catalogRootDir = plateSolveSettings.PinPointCatalogRoot;
            maxMagnitude = plateSolveSettings.PinPointMaxMagnitude;
            expansion = plateSolveSettings.PinPointExpansion;
            allSkyApiKey = plateSolveSettings.PinPointAllSkyApiKey;
            allSkyApiHost = plateSolveSettings.PinPointAllSkyApiHost;
        }

        protected override async Task<PlateSolveResult> SolveAsyncImpl(IImageData source,
                                                                       PlateSolveParameter parameter,
                                                                       PlateSolveImageProperties imageProperties,
                                                                       IProgress<ApplicationStatus>? progress,
                                                                       CancellationToken ct) {
            string? filePath = null;
            bool attached = false;
            bool success = false;

            PlateSolveResult plateSolveResult = new() {
                Success = false,
            };

            dynamic? pinPoint = null;

            try {
                // Save the image to the solving working directory
                FileSaveInfo fileSaveInfo = new() {
                    FilePath = WORKING_DIRECTORY,
                    FilePattern = Path.GetRandomFileName(),
                    FileType = FileType.FITS
                };

                filePath = await source.SaveToDisk(fileSaveInfo, forceFileType: true, cancelToken: ct);

                await Task.Run(() => {

                    // Instantiate a new PinPoint object via its COM interface
                    try {
                        Type? PinPointType = Type.GetTypeFromProgID("PinPoint.Plate");
                        pinPoint = Activator.CreateInstance(PinPointType ?? throw new InvalidOperationException("PinPoint.Plate COM component is not registered."))!;
                    } catch (InvalidOperationException) {
                        Logger.Error($"Failed to initialize PinPoint. It or its 64bit component does not appear to be installed.");

                        if (!parameter.DisableNotifications) {
                            Notifier.ShowError(Loc.Instance["LblPinPointNotInstalled"]);
                        }

                        throw new InvalidComObjectException();
                    } catch (Exception ex) {
                        Logger.Error($"Failed to initialize PinPoint: {ex.GetType().Name}: {ex.Message}");

                        if (!parameter.DisableNotifications) {
                            Notifier.ShowError(Loc.Instance["LblPinPointFailedInitialize"]);
                        }

                        throw new InvalidComObjectException();
                    }

                    Logger.Debug($"Initialized PinPoint Astrometric Engine {pinPoint.EngineVersion}");

                    // Load the image into PinPoint and configure it for solving
                    pinPoint.AttachFITS(filePath);
                    attached = true;

                    pinPoint.ArcsecPerPixelHoriz = imageProperties.ArcSecPerPixel;
                    pinPoint.ArcsecPerPixelVert = imageProperties.ArcSecPerPixel;

                    pinPoint.Catalog = (int)catalogType;
                    pinPoint.CatalogPath = catalogRootDir;
                    pinPoint.CatalogMaximumMagnitude = maxMagnitude;
                    pinPoint.CatalogExpansion = expansion / 100;

                    if (!string.IsNullOrEmpty(allSkyApiKey)) {
                        pinPoint.AllSkyApiKey = allSkyApiKey;
                    }

                    if (!pinPoint.AllSkyDomain.Equals(allSkyApiHost)) {
                        pinPoint.AllSkyDomain = allSkyApiHost;
                    }

                    // Configure in accordance with a hinted or blind solve
                    if (parameter.Coordinates != null) {
                        pinPoint.RightAscension = parameter.Coordinates.RA;
                        pinPoint.Declination = parameter.Coordinates.Dec;

                        progress?.Report(new ApplicationStatus() { Status = Loc.Instance["LblPinPointLocalSolve"] });
                        success = pinPoint.Solve();
                    } else {
                        progress?.Report(new ApplicationStatus() { Status = Loc.Instance["LblPinPointAllSkySolve"] });
                        success = pinPoint.SolveAllSky();
                    }
                }, ct);

                // Fill out the solve results, deallocate the PinPoint object, and return
                if (success && pinPoint != null) {
                    plateSolveResult.Success = true;
                    plateSolveResult.Coordinates = new Coordinates(pinPoint!.RightAscension, pinPoint.Declination, Epoch.J2000, Coordinates.RAType.Hours);
                    plateSolveResult.PositionAngle = pinPoint.PositionAngle;
                    plateSolveResult.Pixscale = Math.Abs(pinPoint.ArcsecPerPixelHoriz);

                    if (!double.IsNaN(plateSolveResult.Pixscale)) {
                        plateSolveResult.Radius = AstroUtil.ArcsecToDegree(Math.Sqrt(Math.Pow(imageProperties.ImageWidth * plateSolveResult.Pixscale, 2) + Math.Pow(imageProperties.ImageHeight * plateSolveResult.Pixscale, 2)) / 2d);
                    }
                }
            } catch (InvalidComObjectException) {
                return plateSolveResult;
            } catch (COMException ex) {
                Notifier.ShowExternalError(ex.Message, Loc.Instance["LblPinPointErrorMessage"]);
                Logger.Error($"PinPoint failed to solve: {ex.GetType().Name}: {ex.Message}");
                return plateSolveResult;
            } finally {
                progress?.Report(new ApplicationStatus() { Status = string.Empty });

                if (pinPoint != null) {
                    if (attached) {
                        pinPoint.DetachFITS();
                    }

                    Marshal.ReleaseComObject(pinPoint);
                    pinPoint = null;
                }

                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath)) {
                    File.Delete(filePath);
                }

            }

            return plateSolveResult;
        }

        protected override void EnsureSolverValid(PlateSolveParameter parameter) {

            try {
                Type? PinPointType = Type.GetTypeFromProgID("PinPoint.Plate");
                dynamic pinPoint = Activator.CreateInstance(PinPointType ?? throw new InvalidOperationException("PinPoint.Plate COM component is not registered."))!;
                Marshal.ReleaseComObject(pinPoint);
            } catch (InvalidOperationException) {
            }
        }
    }
}