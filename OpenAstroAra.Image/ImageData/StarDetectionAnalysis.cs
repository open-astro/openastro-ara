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
        // The detector publishes from a Task.Run threadpool thread (RenderedImage.DetectStars) while a §59
        // autofocus / Live-View consumer may read from another. _gate gives both a full memory barrier (so a
        // reader sees the published values, not a stale cache) and torn-read safety for the double fields,
        // which aren't individually atomic on 32-bit ARM (the Pi target). Reads + writes both take the lock;
        // it's uncontended in practice (one publish per frame, occasional reads). PropertyChanged is always
        // raised OUTSIDE the lock so observer callbacks can't deadlock or block the publisher.
        private readonly object _gate = new object();
        private double _hfr = double.NaN;
        private double _hfrStDev = double.NaN;
        private int _detectedStars = -1;
        private IReadOnlyList<DetectedStar> _starList = new List<DetectedStar>();

        public double HFR {
            get { lock (this._gate) { return this._hfr; } }
            set {
                lock (this._gate) { this._hfr = value; }
                this.RaisePropertyChanged();
            }
        }

        public double HFRStDev {
            get { lock (this._gate) { return this._hfrStDev; } }
            set {
                lock (this._gate) { this._hfrStDev = value; }
                this.RaisePropertyChanged();
            }
        }

        public int DetectedStars {
            get { lock (this._gate) { return this._detectedStars; } }
            set {
                lock (this._gate) { this._detectedStars = value; }
                this.RaisePropertyChanged();
            }
        }

        public IReadOnlyList<DetectedStar> StarList {
            get { lock (this._gate) { return this._starList; } }
            set {
                lock (this._gate) { this._starList = value; }
                this.RaisePropertyChanged();
            }
        }

        public StarDetectionAnalysis() {
        }

        public void SetAll(double hfr, double hfrStDev, int detectedStars, IReadOnlyList<DetectedStar> starList) {
            // Atomic publish: write every backing field under the lock (full barrier + no torn doubles for a
            // cross-thread reader), THEN raise the change notifications outside it, so any observer woken by
            // one event already sees all four properties consistent (never new HFR with a stale count).
            lock (this._gate) {
                this._hfr = hfr;
                this._hfrStDev = hfrStDev;
                this._detectedStars = detectedStars;
                this._starList = starList;
            }
            this.RaisePropertyChanged(nameof(this.HFR));
            this.RaisePropertyChanged(nameof(this.HFRStDev));
            this.RaisePropertyChanged(nameof(this.DetectedStars));
            this.RaisePropertyChanged(nameof(this.StarList));
        }
    }
}