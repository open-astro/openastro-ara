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
using System.Windows.Input;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Sequencer.Container.ExecutionStrategy;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.Trigger;
using OpenAstroAra.Core.Utility;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenAstroAra.Core.Locale;
using OpenAstroAra.Sequencer.Utility;
using OpenAstroAra.Core.Utility.Extensions;

namespace OpenAstroAra.Sequencer.Container {

    [ExportMetadata("Name", "Lbl_SequenceContainer_SequenceRootContainer_Name")]
    [ExportMetadata("Description", "Lbl_SequenceContainer_SequentialContainer_Description")]
    [ExportMetadata("Icon", "BoxSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_Container")]
    [Export(typeof(ISequenceContainer))]
    [JsonObject(MemberSerialization.OptIn)]
    public class SequenceRootContainer : SequenceContainer, ISequenceRootContainer, IImmutableContainer {

        [OnDeserializing]
        public void OnDeserializing(StreamingContext context) {
            this.Items.Clear();
        }

        [ImportingConstructor]
        public SequenceRootContainer() : base(new SequentialStrategy()) {
        }

        // Reset/Detach UI commands removed — the WPF MyMessageBox confirm-dialog
        // round-trip has no headless equivalent. Triggered from REST via the
        // §38 sequence endpoints which call the corresponding domain method
        // directly.

        private void ClearContainer(ISequenceContainer container) {
            foreach (var item in container.GetItemsSnapshot()) {
                item.Detach();
            }
        }

        private object runningItemsLock = new object();
        private List<ISequenceItem> runningItems = new List<ISequenceItem>();

        public void AddRunningItem(ISequenceItem item) {
            lock (runningItemsLock) {
                runningItems.Add(item);
            }
        }

        public void RemoveRunningItem(ISequenceItem item) {
            lock (runningItemsLock) {
                runningItems.Remove(item);
            }
        }

        public void SkipCurrentRunningItems() {
            lock (runningItemsLock) {
                foreach (var item in runningItems) {
                    item.Skip();
                }
            }
        }

        public IReadOnlyCollection<ISequenceItem> GetCurrentRunningItems() {
            lock (runningItemsLock) {
                return runningItems.ToList().AsReadOnly();
            }
        }

        public event Func<object, SequenceEntityFailureEventArgs, Task> FailureEvent;

        public async Task RaiseFailureEvent(ISequenceEntity sender, Exception ex) {
            try {                
                await (FailureEvent?.InvokeAsync(sender, new SequenceEntityFailureEventArgs(sender, ex)) ?? Task.CompletedTask);
            } catch(Exception eventException) {
                Logger.Error(eventException);
            }
        }

        public override ICommand DropIntoCommand => new GalaSoft.MvvmLight.Command.RelayCommand<object>((o) => {
            (Items[1] as TargetAreaContainer).DropIntoCommand.Execute(o);
        });

        public override object Clone() {
            return new SequenceRootContainer() {
                Icon = Icon,
                Name = Name,
                Category = Category,
                Description = Description,
            };
        }

        private string sequenceTitle;

        public string SequenceTitle {
            get => string.IsNullOrEmpty(sequenceTitle) ? Name : sequenceTitle;
            set {
                if (sequenceTitle != value) {
                    sequenceTitle = value;
                    RaisePropertyChanged();
                    Name = value;
                }
            }
        }
    }
}