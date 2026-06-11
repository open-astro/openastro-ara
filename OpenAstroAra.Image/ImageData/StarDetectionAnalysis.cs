#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Utility;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Image.Interfaces;
using System.Collections.Generic;

namespace OpenAstroAra.Image.ImageData {

    public class StarDetectionAnalysis : BaseINPC, IStarDetectionAnalysis {
        private double _hfr = double.NaN;
        private double _hfrStDev = double.NaN;
        private int _detectedStars = -1;
        private IReadOnlyList<DetectedStar> _starList = new List<DetectedStar>();

        public double HFR {
            get => this._hfr;
            set {
                this._hfr = value;
                this.RaisePropertyChanged();
            }
        }

        public double HFRStDev {
            get => this._hfrStDev;
            set {
                this._hfrStDev = value;
                this.RaisePropertyChanged();
            }
        }

        public int DetectedStars {
            get => this._detectedStars;
            set {
                this._detectedStars = value;
                this.RaisePropertyChanged();
            }
        }

        public IReadOnlyList<DetectedStar> StarList {
            get => this._starList;
            set {
                this._starList = value;
                this.RaisePropertyChanged();
            }
        }

        public StarDetectionAnalysis() {
        }

        public void SetAll(double hfr, double hfrStDev, int detectedStars, IReadOnlyList<DetectedStar> starList) {
            // Atomic publish: write every backing field first, THEN raise the change notifications, so any
            // observer woken by one event already sees all four properties consistent (never new HFR with a
            // stale DetectedStars/StarList). The per-property setters remain for callers that set one field.
            this._hfr = hfr;
            this._hfrStDev = hfrStDev;
            this._detectedStars = detectedStars;
            this._starList = starList;
            this.RaisePropertyChanged(nameof(this.HFR));
            this.RaisePropertyChanged(nameof(this.HFRStDev));
            this.RaisePropertyChanged(nameof(this.DetectedStars));
            this.RaisePropertyChanged(nameof(this.StarList));
        }
    }
}