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
using System;

namespace OpenAstroAra.PlateSolving {

    public class PlateSolveParameter {
        private double pixelSize;

        public double FocalLength { get; set; }

        public double PixelSize {
            get => Math.Max(Binning, 1) * pixelSize;
            set => pixelSize = value;
        }

        public int Binning { get; set; }
        public double SearchRadius { get; set; }
        public double Regions { get; set; }

        /// <summary>Solver downsample (binning) factor; 0 = pick it from the pixel scale per solve
        /// (<see cref="SolveTuning.DownsampleFor"/>) instead of a fixed profile number (#1136).</summary>
        public int DownSampleFactor { get; set; }
        public int MaxObjects { get; set; }

        /// <summary>Wall-clock budget for one solver run at <see cref="SearchRadius"/>; null keeps the
        /// legacy 10-minute cap. Wider stages scale it (<see cref="SolveTuning.TimeoutFor"/>).</summary>
        public TimeSpan? Timeout { get; set; }

        /// <summary>Let the solver search the image scale itself (ASTAP <c>-fov 0</c>) instead of trusting
        /// the focal length / pixel size — the staged search's second stage, for a profile whose optics
        /// are wrong (#1136). Off for a normal solve: it is slower than a known scale.</summary>
        public bool SearchScale { get; set; }

        /// <summary>Second search centre when the hint fails: the last good solve of this process
        /// (<see cref="SolveMemory"/>) or a caller-supplied position. Null = none.</summary>
        public Coordinates? FallbackCoordinates { get; set; }

        /// <summary>Whether the staged search (scale search, fallback centre, widened radii) runs between
        /// the hinted solve and the blind failover. Off = the legacy hint → blind behaviour, for loops
        /// that must fail fast (polar alignment).</summary>
        public bool StagedSearchEnabled { get; set; } = true;

        public bool DisableNotifications { get; set; }

        public bool BlindFailoverEnabled { get; set; } = true;

        private Coordinates? coordinates;

        public Coordinates? Coordinates {
            get => coordinates;
            set => coordinates = value?.Transform(Epoch.J2000);
        }

        public override string ToString() {
            var j2000 = Coordinates?.Transform(Epoch.J2000);
            var formatCoordinates = j2000 != null ? $"Reference Coordinates RA: {j2000.RAString} Dec: {j2000.DecString} Epoch: {j2000.Epoch}" : "";
            return $"FocalLength: {FocalLength} " +
                $"PixelSize: {PixelSize} " +
                $"SearchRadius: {SearchRadius} " +
                $"BlindFailoverEnabled: {BlindFailoverEnabled} " +
                $"Regions: {Regions} " +
                $"DownSampleFactor: {DownSampleFactor} " +
                $"MaxObjects: {MaxObjects} " +
                $"Timeout: {(Timeout is TimeSpan t ? t.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "s" : "default")} " +
                $"SearchScale: {SearchScale} " +
                $"StagedSearch: {StagedSearchEnabled} " +
                $"{formatCoordinates}";
        }

        public PlateSolveParameter Clone() {
            var clone = (PlateSolveParameter)this.MemberwiseClone();
            clone.Coordinates = clone.Coordinates?.Transform(clone.Coordinates.Epoch);
            clone.FallbackCoordinates = clone.FallbackCoordinates?.Transform(clone.FallbackCoordinates.Epoch);
            return clone;
        }
    }
}