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

        // §59.3/§59.4 Smart Focus shape metrics — the per-star half of the feature vector that a
        // defocus→offset inverse map is trained on. Derived from the same flux-weighted blob moments as
        // HFR (see StarDetector.Measure), so they cost one extra pass, no re-detection.

        /// <summary>Full-Width-at-Half-Maximum in pixels, from the flux-weighted second moment
        /// (FWHM = 2√(2 ln 2)·σ for the equivalent Gaussian). Grows monotonically with defocus.</summary>
        public double FWHM { get; set; }

        /// <summary>Minor/major principal-axis ratio of the flux distribution, in (0, 1]: 1 ≈ a round
        /// star, → 0 an elongated one. The §59.4 asymmetry/tilt signal (guiding drift, tilt, coma).</summary>
        public double Roundness { get; set; }

        /// <summary>Background-corrected peak-to-background ratio, (peak − background) / max(1, background)
        /// — the scale-invariant central brightness that collapses as a refractor defocuses. The 1-ADU
        /// denominator floor keeps it bounded on an all-dark/bias-free frame.</summary>
        public double PeakToBackground { get; set; }

        /// <summary>§59.3/§59.10 — outer diameter (pixels) of the defocused donut: the full extent of the
        /// star's flux ring, from the half-max outer edge of its radial surface-brightness profile. On an
        /// obstructed scope (SCT/RC/Newtonian) this is the donut's outer ring; on a refractor / in-focus star
        /// it degrades to roughly the FWHM extent. 0 for a sub-pixel blob.</summary>
        public double DonutOuterDiameter { get; set; }

        /// <summary>§59.3/§59.10 — inner diameter (pixels) of the donut hole: the central-obstruction shadow,
        /// from the half-max inner edge of the radial profile. &gt; 0 only when the star images as a ring with a
        /// dark centre (a defocused obstructed scope); 0 for a refractor or an in-focus star whose profile
        /// peaks at the centre.</summary>
        public double DonutInnerDiameter { get; set; }

        /// <summary>§59.3 — donut ring thickness (pixels) = <see cref="DonutOuterDiameter"/> −
        /// <see cref="DonutInnerDiameter"/>. Derived, not stored: the per-star value is the difference, while
        /// the frame-level median of it is a distinct statistic (the median of differences ≠ the difference of
        /// medians) that <see cref="FocusFeatureVector"/> carries separately.</summary>
        public double RingThickness => DonutOuterDiameter - DonutInnerDiameter;
    }

    public interface IStarDetection {
        Task<StarDetectionResult> Detect(IRenderedImage image, string format,
            StarDetectionParams parameters, IProgress<ApplicationStatus> progress, CancellationToken token);
    }

    public interface IStarAnnotator {
        Task<byte[]> GetAnnotatedImage(StarDetectionParams parameters, StarDetectionResult result, byte[] source);
    }
}