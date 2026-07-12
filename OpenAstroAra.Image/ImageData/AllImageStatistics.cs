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
using OpenAstroAra.Image.Interfaces;
using System;
using System.Threading.Tasks;

namespace OpenAstroAra.Image.ImageData {

    public class AllImageStatistics : BaseINPC, IDisposable {
        public ImageProperties ImageProperties { get; private set; }
        public Task<IImageStatistics> ImageStatistics { get; private set; }
        public IStarDetectionAnalysis StarDetectionAnalysis { get; private set; }

        private AllImageStatistics(
            ImageProperties imageProperties,
            Task<IImageStatistics> imageStatistics,
            IStarDetectionAnalysis starDetectionAnalysis) {
            this.ImageProperties = imageProperties;
            this.ImageStatistics = imageStatistics;
            this.StarDetectionAnalysis = starDetectionAnalysis;

            this.StarDetectionAnalysis.PropertyChanged += Child_PropertyChanged;
        }

        private void Child_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) {
            this.ChildChanged(sender, e);
        }

        public static AllImageStatistics Create(IImageData imageData) {
            return new AllImageStatistics(imageData.Properties, imageData.Statistics.Task, imageData.StarDetectionAnalysis);
        }

        private bool _disposed;

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) {
            if (_disposed) { return; }
            if (disposing) {
                // Unsubscribe to avoid the child keeping this instance alive via the event handler.
                this.StarDetectionAnalysis.PropertyChanged -= Child_PropertyChanged;
            }
            _disposed = true;
        }
    }
}