#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

// Phase 0.5p2 net10.0 conversion: the NINA-inherited ImageAnalysis algorithms
// (StarDetection, StarAnnotator, BahtinovAnalysis, ContrastDetection,
// DebayeredImage, ImageUtility) were deeply WPF/BitmapSource-coupled and
// have been deleted. Per playbook §line-2105 the replacement is OpenCvSharp4-
// based, which is a substantial follow-up (lands when autofocus / star
// analysis features are wired into the headless server).
//
// These minimal stub interfaces + types preserve the call surface so the
// rest of the Image project (ImageData/, Interfaces/) compiles. Real impls
// drop in later behind these names.

using OpenAstroAra.Core.Model;
using OpenAstroAra.Image.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Image.ImageAnalysis {

    public class StarDetectionParams {
        public double Sensitivity { get; set; }
        public int NoiseReduction { get; set; }
        public int InnerCropRatio { get; set; }
        public int OuterCropRatio { get; set; }
        public bool UseROI { get; set; }
        public bool IsAutoFocus { get; set; }
        public int MaxNumberOfStars { get; set; }
        public string? PixelSampling { get; set; }
    }

    public class StarDetectionResult {
        public int DetectedStars { get; set; }
        public double AverageHFR { get; set; }
        public double HFRStdDev { get; set; }
        public IReadOnlyList<DetectedStar> StarList { get; set; } = new List<DetectedStar>();
    }

    public class DetectedStar {
        public double Position { get; set; }
        public double HFR { get; set; }
        public double AverageBrightness { get; set; }
        public double MaxBrightness { get; set; }
        public double Background { get; set; }
    }

    public interface IStarDetection {
        Task<StarDetectionResult> Detect(IRenderedImage image, string format,
            StarDetectionParams parameters, IProgress<ApplicationStatus> progress, CancellationToken token);
    }

    public interface IStarAnnotator {
        Task<byte[]> GetAnnotatedImage(StarDetectionParams parameters, StarDetectionResult result, byte[] source);
    }
}