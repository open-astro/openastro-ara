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
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace OpenAstroAra.Profile {

    [Serializable()]
    [DataContract]
    public sealed class ApplicationSettings : Settings, IApplicationSettings {

        public ApplicationSettings() {
            SetDefaultValues();
        }

        [OnDeserializing]
        public void OnDeserializing(StreamingContext context) {
            SetDefaultValues();
        }

        [OnDeserialized]
        public void OnDeserialized(StreamingContext context) {
            if (Culture == "es-US") {
                Culture = "es-ES";
            }
            if (!Directory.Exists(SkySurveyCacheDirectory)) {
                SkySurveyCacheDirectory = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "FramingAssistantCache");
            }
            if (SelectedPluggableBehaviors == null) {
                SelectedPluggableBehaviors = new AsyncObservableCollection<KeyValuePair<string, string>>();
            }
            SelectedPluggableBehaviorsLookup = SelectedPluggableBehaviors.ToList().ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        protected override void SetDefaultValues() {
            language = SafeCulture("en-GB");
            logLevel = LogLevel.INFO;
            devicePollingInterval = 2;
            skyAtlasImageRepository = string.Empty;
            skySurveyCacheDirectory = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "FramingAssistantCache");
            SelectedPluggableBehaviors = new AsyncObservableCollection<KeyValuePair<string, string>>();
            SelectedPluggableBehaviorsLookup = ImmutableDictionary<string, string>.Empty;
            pageSize = 50;
        }

        [DataMember]
        public string Culture {
            get => Language.Name;
            set {
                Language = SafeCulture(value);
                RaisePropertyChanged();
            }
        }

        // Resolve a named culture when ICU is available, but fall back to the
        // invariant culture under globalization-invariant mode (the AOT server
        // container) rather than throwing CultureNotFoundException on construction.
        // The headless daemon does no server-side localization — UI localization
        // lives in the Flutter client — so the exact default is immaterial; not
        // crashing when a profile is constructed is what matters.
        private static CultureInfo SafeCulture(string name) {
            try {
                return new CultureInfo(name);
            } catch (CultureNotFoundException) {
                return CultureInfo.InvariantCulture;
            }
        }

        private CultureInfo language = SafeCulture("en-GB");

        public CultureInfo Language {
            get => language;
            set {
                if (language != value) {
                    language = value;
                    RaisePropertyChanged();
                }
            }
        }

        private LogLevel logLevel;

        [DataMember]
        public LogLevel LogLevel {
            get => logLevel;
            set {
                if (logLevel != value) {
                    logLevel = value;
                    RaisePropertyChanged();
                }
            }
        }

        private double devicePollingInterval;

        [DataMember]
        public double DevicePollingInterval {
            get => devicePollingInterval;
            set {
                if (devicePollingInterval != value) {
                    devicePollingInterval = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string skyAtlasImageRepository = string.Empty;

        [Obsolete("Sky Atlas offline image repository is no longer used in the headless build; retained for profile deserialization only.")]
        [DataMember]
        public string SkyAtlasImageRepository {
            get => skyAtlasImageRepository;
            set {
                if (skyAtlasImageRepository != value) {
                    skyAtlasImageRepository = value;
                    RaisePropertyChanged();
                }
            }
        }

        private string skySurveyCacheDirectory = string.Empty;

        [DataMember]
        public string SkySurveyCacheDirectory {
            get => skySurveyCacheDirectory;
            set {
                if (skySurveyCacheDirectory != value) {
                    skySurveyCacheDirectory = value;
                    RaisePropertyChanged();
                }
            }
        }

        public IReadOnlyDictionary<string, string> SelectedPluggableBehaviorsLookup { get; private set; } = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty;

        private void SelectedPluggableBehaviors_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) {
            SelectedPluggableBehaviorsLookup = SelectedPluggableBehaviors.ToList().ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value);
            RaisePropertyChanged(nameof(SelectedPluggableBehaviors));
            RaisePropertyChanged(nameof(SelectedPluggableBehaviorsLookup));
        }

        [NonSerialized]
        private AsyncObservableCollection<KeyValuePair<string, string>> selectedPluggableBehaviors = new();

        [DataMember]
        public AsyncObservableCollection<KeyValuePair<string, string>> SelectedPluggableBehaviors {
            get => selectedPluggableBehaviors;
            set {
                if (selectedPluggableBehaviors != value) {
                    if (selectedPluggableBehaviors != null) {
                        selectedPluggableBehaviors.CollectionChanged -= SelectedPluggableBehaviors_CollectionChanged;
                    }
                    selectedPluggableBehaviors = value;
                    if (selectedPluggableBehaviors != null) {
                        selectedPluggableBehaviors.CollectionChanged += SelectedPluggableBehaviors_CollectionChanged;
                    }
                    RaisePropertyChanged();
                }
            }
        }

        private int pageSize;

        [DataMember]
        public int PageSize {
            get => pageSize;
            set {
                if (value < 1) { value = 1; }
                if (pageSize != value) {
                    pageSize = value;
                    RaisePropertyChanged();
                }
            }
        }
    }
}