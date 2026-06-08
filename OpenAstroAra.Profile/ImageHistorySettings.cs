#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Profile.Interfaces;
using System;
using System.IO;
using System.Runtime.Serialization;

namespace OpenAstroAra.Profile {

    [Serializable()]
    [DataContract]
    public class ImageHistorySettings : Settings, IImageHistorySettings {

        [OnDeserializing]
        public void OnDeserializing(StreamingContext context) {
            SetDefaultValues();
        }

        protected override void SetDefaultValues() {
            _imageHistoryLeftSelected = ImageHistory.HFR;
            _imageHistoryRightSelected = ImageHistory.Stars;
        }

        private ImageHistory _imageHistoryLeftSelected;

        [DataMember]
        public ImageHistory ImageHistoryLeftSelected {
            get => _imageHistoryLeftSelected;
            set {
                if (_imageHistoryLeftSelected != value) {
                    _imageHistoryLeftSelected = value;
                    RaisePropertyChanged();
                }
            }
        }

        private ImageHistory _imageHistoryRightSelected;

        [DataMember]
        public ImageHistory ImageHistoryRightSelected {
            get => _imageHistoryRightSelected;
            set {
                if (_imageHistoryRightSelected != value) {
                    _imageHistoryRightSelected = value;
                    RaisePropertyChanged();
                }
            }
        }

    }
}