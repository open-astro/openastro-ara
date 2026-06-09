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
using OpenAstroAra.Core.Locale;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Equipment.Equipment.MySwitch;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Sequencer.Validations;
using System;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Switch {

    [ExportMetadata("Name", "Lbl_SequenceItem_Switch_SetSwitchValue_Name")]
    [ExportMetadata("Description", "Lbl_SequenceItem_Switch_SetSwitchValue_Description")]
    [ExportMetadata("Icon", "ButtonSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Switch")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SetSwitchValue : SequenceItem, IValidatable {
        private ISwitchMediator switchMediator;

        [ImportingConstructor]
        public SetSwitchValue(ISwitchMediator switchMediator) {
            this.switchMediator = switchMediator;

            writableSwitches = new ReadOnlyCollection<IWritableSwitch>(CreateDummyList());
            SelectedSwitch = WritableSwitches.First();
        }

        private SetSwitchValue(SetSwitchValue cloneMe) : this(cloneMe.switchMediator) {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new SetSwitchValue(this) {
                SwitchIndex = SwitchIndex,
                Value = Value
            };
        }

        private IList<string> issues = new List<string>();

        public IList<string> Issues {
            get => issues;
            set {
                issues = value;
                RaisePropertyChanged();
            }
        }

        private double value;

        [JsonProperty]
        public double Value {
            get => value;
            set {
                this.value = value;
                Validate();
                RaisePropertyChanged();
            }
        }

        private short switchIndex;

        [JsonProperty]
        public short SwitchIndex {
            get => switchIndex;
            set {
                if (value > -1) {
                    switchIndex = value;
                    RaisePropertyChanged();
                }
            }
        }

        private IWritableSwitch? selectedSwitch;

        [JsonIgnore]
        public IWritableSwitch? SelectedSwitch {
            get => selectedSwitch;
            set {
                selectedSwitch = value;
                SwitchIndex = (short)(selectedSwitch != null ? (WritableSwitches?.IndexOf(selectedSwitch) ?? -1) : -1);
                RaisePropertyChanged();
            }
        }

        private ReadOnlyCollection<IWritableSwitch> writableSwitches;

        public ReadOnlyCollection<IWritableSwitch> WritableSwitches {
            get => writableSwitches;
            set {
                writableSwitches = value;
                RaisePropertyChanged();
            }
        }

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            return switchMediator.SetSwitchValue(switchIndex, Value, progress, token);
        }

        private static List<IWritableSwitch> CreateDummyList() {
            var dummySwitches = new List<IWritableSwitch>();
            for (short i = 0; i < 20; i++) {
                dummySwitches.Add(new DummySwitch((short)(i + 1)));
            }
            return dummySwitches;
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Validation boundary: querying switch-hub state may throw any exception; the failure is logged and reported through Issues as a generic validation error so a transient equipment fault cannot crash validation. CA1031 sanctions general catches at such recover-and-report boundaries.")]
        public bool Validate() {
            try {
                var i = new List<string>();
                var info = switchMediator.GetInfo();
                if (info?.Connected != true) {
                    //When switch gets disconnected the real list will be changed to the dummy list
                    if (!(WritableSwitches.FirstOrDefault() is DummySwitch)) {
                        WritableSwitches = new ReadOnlyCollection<IWritableSwitch>(CreateDummyList());
                    }

                    i.Add(Loc.Instance["LblSwitchNotConnected"]);
                } else {
                    if (WritableSwitches.Count > 0) {
                        //When switch gets connected the dummy list will be changed to the real list
                        if (WritableSwitches.FirstOrDefault() is DummySwitch) {
                            WritableSwitches = info.WritableSwitches ?? new ReadOnlyCollection<IWritableSwitch>(CreateDummyList());

                            if (switchIndex >= 0 && WritableSwitches.Count > switchIndex) {
                                SelectedSwitch = WritableSwitches[switchIndex];
                            } else {
                                SelectedSwitch = null;
                            }
                        }
                    } else {
                        SelectedSwitch = null;
                        i.Add(Loc.Instance["Lbl_SequenceItem_Validation_NoWritableSwitch"]);
                    }
                }

                if (switchIndex >= 0 && WritableSwitches.Count > switchIndex) {
                    if (WritableSwitches[switchIndex] != SelectedSwitch) {
                        SelectedSwitch = WritableSwitches[switchIndex];
                    }
                }

                var s = SelectedSwitch;

                if (s == null) {
                    i.Add(Loc.Instance["Lbl_SequenceItem_Validation_NoSwitchSelected"]);
                } else {
                    if (Value < s.Minimum || Value > s.Maximum)
                        i.Add(string.Format(CultureInfo.CurrentCulture, Loc.Instance["Lbl_SequenceItem_Validation_InvalidSwitchValue"], s.Minimum, s.Maximum, s.StepSize));
                }

                Issues = i;
                return Issues.Count == 0;
            } catch (Exception ex) {
                Issues = new List<string>() { "An unexpected error occurred" };
                Logger.Error(ex);
                return false;
            }
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(SetSwitchValue)}, SwitchIndex {SwitchIndex}, Value: {Value}";
        }
    }
}