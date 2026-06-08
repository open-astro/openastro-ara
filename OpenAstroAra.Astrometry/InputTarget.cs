#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Astrometry.Interfaces;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using System;
using System.Runtime.Serialization;

namespace OpenAstroAra.Astrometry {

    [JsonObject(MemberSerialization.OptIn)]
    public class InputTarget : BaseINPC {
        private bool deserializing;
        [OnDeserializing]
        public void OnDeserializing(StreamingContext context) {
            deserializing = true;
        }

        [OnDeserialized]
        public void OnDeserialized(StreamingContext context) {
            deserializing = false;
            RaiseCoordinatesChanged();
        }

        public InputTarget(Angle latitude, Angle longitude, CustomHorizon horizon) {
            this.latitude = latitude;
            this.longitude = longitude;
            DeepSkyObject = new DeepSkyObject(string.Empty, new Coordinates(Angle.Zero, Angle.Zero, Epoch.J2000), string.Empty, horizon);
            DeepSkyObject.SetDateAndPosition(NighttimeCalculator.GetReferenceDate(DateTime.Now), latitude.Degree, longitude.Degree);
            InputCoordinates = new InputCoordinates();
        }

        private void InputCoordinates_OnCoordinatesChanged(object? sender, EventArgs e) {
            RaiseCoordinatesChanged();
        }

        private bool expanded = true;

        [JsonProperty]
        public bool Expanded {
            get => expanded;
            set {
                expanded = value;
                RaisePropertyChanged();
            }
        }

        private IDeepSkyObject deepSkyObject = null!;  // set via DeepSkyObject in the constructor

        public IDeepSkyObject DeepSkyObject {
            get => deepSkyObject;
            set {
                deepSkyObject = value;
                RaisePropertyChanged();
            }
        }

        private string targetName = string.Empty;

        [JsonProperty]
        public string TargetName {
            get => targetName;
            set {
                if (value != targetName) {
                    targetName = value;
                    RaisePropertyChanged();
                    RaiseCoordinatesChanged();
                }
            }
        }

        /// <summary>
        /// Backwards compatibility property that will migrate to position angle
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1044:Properties should not be write only",
            Justification = "Deserialization-only back-compat shim: maps the legacy JSON 'Rotation' field onto PositionAngle. Adding a getter would re-emit the deprecated field on serialization.")]
        [JsonProperty(propertyName: "Rotation")]
        public double DeprecatedRotation {
            set => PositionAngle = 360 - value;
        }

        private double positionAngle;
        [JsonProperty]
        public double PositionAngle {
            get => positionAngle;
            set {
                if (value != positionAngle) {
                    positionAngle = AstroUtil.EuclidianModulus(value, 360);
                    RaisePropertyChanged();
                    RaiseCoordinatesChanged();
                }
            }
        }

        private InputCoordinates inputCoordinates = null!;  // set via InputCoordinates in the constructor
        private Angle latitude;
        private Angle longitude;

        [JsonProperty]
        public InputCoordinates InputCoordinates {
            get => inputCoordinates;
            set {
                if (inputCoordinates != null) {
                    InputCoordinates.CoordinatesChanged -= InputCoordinates_OnCoordinatesChanged;
                }
                inputCoordinates = value;
                if (inputCoordinates != null) {
                    InputCoordinates.CoordinatesChanged += InputCoordinates_OnCoordinatesChanged; ;
                }
                RaiseCoordinatesChanged();
            }
        }

        public void SetPosition(Angle latitude, Angle longitude) {
            this.latitude = latitude;
            this.longitude = longitude;
            if (this.DeepSkyObject != null) {
                this.DeepSkyObject.SetDateAndPosition(NighttimeCalculator.GetReferenceDate(DateTime.Now), latitude.Degree, longitude.Degree);
                RaiseCoordinatesChanged();
            }
        }

        private void RaiseCoordinatesChanged() {
            if (!deserializing) {
                RaisePropertyChanged(nameof(PositionAngle));
                RaisePropertyChanged(nameof(InputCoordinates));

                DeepSkyObject.Name = TargetName;
                DeepSkyObject.Coordinates = InputCoordinates.Coordinates;
                DeepSkyObject.RotationPositionAngle = PositionAngle;

                this.CoordinatesChanged?.Invoke(this, new EventArgs());
            }
        }

        public event EventHandler? CoordinatesChanged;

        public override string ToString() {
            return $"{InputCoordinates}; Position Angle: {PositionAngle}";
        }
    }
}