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
using OpenAstroAra.Core.Locale;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Sequencer.Utility;
using OpenAstroAra.Sequencer.Utility.DateTimeProvider;
using OpenAstroAra.Sequencer.Validations;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.SequenceItem.Utility {

    [JsonObject(MemberSerialization.OptIn)]
    public class WaitForTime : SequenceItem, IValidatable, IEstimatesOwnDuration {
        private IList<IDateTimeProvider> dateTimeProviders;
        private int hours;
        private int minutes;
        private int minutesOffset;
        private int seconds;
        private IDateTimeProvider? selectedProvider;

        public WaitForTime(IList<IDateTimeProvider> dateTimeProviders) {
            DateTime = new SystemDateTime();
            this.dateTimeProviders = dateTimeProviders;
            this.SelectedProvider = DateTimeProviders?.FirstOrDefault();
        }

        public WaitForTime(IList<IDateTimeProvider> dateTimeProviders, IDateTimeProvider? selectedProvider) {
            DateTime = new SystemDateTime();
            this.dateTimeProviders = dateTimeProviders;
            this.SelectedProvider = selectedProvider;
        }

        private WaitForTime(WaitForTime cloneMe) : this(cloneMe.DateTimeProviders, cloneMe.SelectedProvider) {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new WaitForTime(this) {
                Hours = Hours,
                Minutes = Minutes,
                Seconds = Seconds,
                MinutesOffset = MinutesOffset
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

        public bool Validate() {
            var i = new List<string>();
            if (HasFixedTimeProvider) {
                var referenceDate = NighttimeCalculator.GetReferenceDate(DateTime.Now);
                if (lastReferenceDate != referenceDate) {
                    UpdateTime();
                }
            }
            if (!timeDeterminedSuccessfully) {
                i.Add($"{Loc.Instance["LblSelectedTimeSourceInvalid"]} {failureReason}");
            }

            Issues = i;
            return i.Count == 0;
        }

        public IList<IDateTimeProvider> DateTimeProviders => dateTimeProviders;

        public bool HasFixedTimeProvider => selectedProvider != null && !(selectedProvider is OpenAstroAra.Sequencer.Utility.DateTimeProvider.TimeProvider);

        [JsonProperty]
        public int Hours {
            get => hours;
            set {
                hours = value;
                RaisePropertyChanged();
            }
        }

        [JsonProperty]
        public int Minutes {
            get => minutes;
            set {
                minutes = value;
                RaisePropertyChanged();
            }
        }

        [JsonProperty]
        public int MinutesOffset {
            get => minutesOffset;
            set {
                minutesOffset = value;
                UpdateTime();
                RaisePropertyChanged();
            }
        }

        [JsonProperty]
        public int Seconds {
            get => seconds;
            set {
                seconds = value;
                RaisePropertyChanged();
            }
        }

        [JsonProperty]
        public IDateTimeProvider? SelectedProvider {
            get => selectedProvider;
            set {
                selectedProvider = value;
                if (selectedProvider != null) {
                    UpdateTime();
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(HasFixedTimeProvider));
                }
            }
        }

        private TimeOnly rolloverTime;

        public TimeOnly RolloverTime {
            get => rolloverTime;
            set => SetProperty(ref rolloverTime, value);
        }

        private bool timeDeterminedSuccessfully;
        private string failureReason = string.Empty;
        private DateTime lastReferenceDate;
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Time-determination boundary: the selected IDateTimeProvider and nighttime calculation may throw a provider-specific TimeProviderException or any other exception; the failure reason is captured and surfaced through Validate() rather than aborting. CA1031 sanctions general catches at such recover-and-report boundaries.")]
        private void UpdateTime() {
            try {
                RolloverTime = SelectedProvider?.GetRolloverTime(this) ?? RolloverTime;
                lastReferenceDate = NighttimeCalculator.GetReferenceDate(DateTime.Now);
                if (HasFixedTimeProvider) {
                    var t = selectedProvider!.GetDateTime(this) + TimeSpan.FromMinutes(MinutesOffset);
                    Hours = t.Hour;
                    Minutes = t.Minute;
                    Seconds = t.Second;

                }
                timeDeterminedSuccessfully = true;
            } catch (Exception ex) {
                timeDeterminedSuccessfully = false;
                failureReason = ex is TimeProviderException tpe ? tpe.LocalizedMessage : ex.Message;
                Validate();
            }
        }

        public override void AfterParentChanged() {
            UpdateTime();
        }

        public ICustomDateTime DateTime { get; set; }

        // #1576: the plain-time target this wait resolved when it started. Held so a finished or
        // ending wait estimates 0, not the next day's 24 h. Overwritten by each Execute, cleared by
        // ResetProgress. Ticks, 0 = unset, Volatile: the run-state readers are on other threads.
        private long heldTargetTicks;

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            Volatile.Write(ref heldTargetTicks, HasFixedTimeProvider ? 0 : ResolveTarget(DateTime.Now).Ticks);
            var duration = GetEstimatedDuration();
            var gate = ItemUtility.GetPauseGate(Parent);
            if (gate is null) {
                await OpenAstroAra.Core.Utility.CoreUtil.Wait(duration, true, progress, "", token);
                return;
            }
            // This wait can run for hours (until dark), so a pause must not queue behind it: suspend
            // here, then wait out what is left of the same target.
            var end = DateTime.UtcNow + duration;
            while (end > DateTime.UtcNow) {
                await gate.WaitWhilePausedAsync(token);
                var left = end - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) {
                    return;
                }
                using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(token);
                var wait = OpenAstroAra.Core.Utility.CoreUtil.Wait(left, true, progress, "", interrupt.Token);
                var requested = gate.WhenPauseRequestedAsync(interrupt.Token);
                await Task.WhenAny(wait, requested);
                await interrupt.CancelAsync();
                token.ThrowIfCancellationRequested();
                await Task.WhenAll(wait, requested).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }

        public override void ResetProgress() {
            Volatile.Write(ref heldTargetTicks, 0);
            base.ResetProgress();
        }

        /// <summary>The estimate below is target minus now: it already shrinks while running, so the
        /// ETA must not subtract the elapsed time a second time (#1155).</summary>
        public bool EstimateIsRemaining => true;

        private DateTime ResolveTarget(DateTime now) {
            // #1080 — an estimate is read from the run-state / WS publish / checkpoint paths as well
            // as by the engine, so it must not write the live tree: the provider's rollover is used
            // locally here; UpdateTime() is where RolloverTime (an observable property) is assigned.
            var rollover = SelectedProvider?.GetRolloverTime(this) ?? RolloverTime;
            return WallClockTarget.Resolve(now, Hours, Minutes, Seconds, rollover, HasFixedTimeProvider);
        }

        public override TimeSpan GetEstimatedDuration() {
            var held = HasFixedTimeProvider ? 0 : Volatile.Read(ref heldTargetTicks);
            var then = held != 0 ? new DateTime(held) : ResolveTarget(DateTime.Now);

            var diff = then - DateTime.Now;
            if (diff < TimeSpan.Zero) {
                return TimeSpan.Zero;
            } else {
                return diff;
            }
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(WaitForTime)}, Time: {Hours}:{Minutes}:{Seconds}h, Offset: {MinutesOffset}";
        }
    }
}