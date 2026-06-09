#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Utility;
using System;
using System.Runtime.Serialization;

namespace OpenAstroAra.Profile {

    public class ProfileMeta : BaseINPC {
        public Guid Id { get; set; }
        public DateTime LastUsed { get; set; }
        private string name = string.Empty;

        public string Name {
            get => name;
            set {
                name = value;
                RaisePropertyChanged();
            }
        }
        private string description = string.Empty;

        public string Description {
            get => description;
            set {
                description = value;
                RaisePropertyChanged();
            }
        }

        public string Location { get; set; } = string.Empty;
        private bool isActive;

        public bool IsActive {
            get => isActive;
            set {
                isActive = value;
                RaisePropertyChanged();
            }
        }
    }

    [DataContract]
    public class ProfileMetaProxy {
        [DataMember]
        public Guid Id { get; set; }

        [DataMember]
        public string Name { get; set; } = string.Empty;

        [DataMember]
        public string Description { get; set; } = string.Empty;

        [DataMember]
        public DateTime LastUsed { get; set; }
    }
}