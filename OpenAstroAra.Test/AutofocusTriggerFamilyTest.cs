#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Equipment.Equipment.MyFilterWheel;
using OpenAstroAra.Equipment.Equipment.MyFocuser;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Sequencer.Interfaces;
using OpenAstroAra.Sequencer.SequenceItem;
using OpenAstroAra.Sequencer.Trigger.Autofocus;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §59.5 — the autofocus trigger family (time interval / focuser-temp delta / HFR drift /
    /// first-use-of-a-filter) against a fake <see cref="IImageHistory"/>, plus the daemon-side
    /// <see cref="ImageHistoryService"/> the triggers read in production.
    /// </summary>
    [TestFixture]
    public class AutofocusTriggerFamilyTest {

        private sealed class FakeHistory : IImageHistory {
            public List<ImageHistoryEntry> Images { get; } = [];
            public List<AutofocusHistoryEntry> Afs { get; } = [];
            public IReadOnlyList<ImageHistoryEntry> ImagePoints => Images;
            public IReadOnlyList<AutofocusHistoryEntry> AutofocusPoints => Afs;
        }

        private static ISequenceItem LightExposure() {
            var item = new Mock<IExposureItem>();
            item.SetupGet(x => x.ImageType).Returns("LIGHT");
            return item.As<ISequenceItem>().Object;
        }

        private static ISequenceItem FlatExposure() {
            var item = new Mock<IExposureItem>();
            item.SetupGet(x => x.ImageType).Returns("FLAT");
            return item.As<ISequenceItem>().Object;
        }

        private static Mock<IFocuserMediator> Focuser(double temperature, bool connected = true) {
            var focuser = new Mock<IFocuserMediator>();
            focuser.Setup(f => f.GetInfo()).Returns(new FocuserInfo { Connected = connected, Temperature = temperature });
            return focuser;
        }

        private static Mock<IFilterWheelMediator> FilterWheel(string? filterName, bool connected = true) {
            var wheel = new Mock<IFilterWheelMediator>();
            wheel.Setup(w => w.GetInfo()).Returns(new FilterWheelInfo {
                Connected = connected,
                SelectedFilter = filterName is null ? null! : new FilterInfo(filterName, 0, 0),
            });
            return wheel;
        }

        // ---- ImageHistoryService (the daemon-side IImageHistory) ----

        [Test]
        public void ImageHistoryService_AssignsMonotonicIdsAndNormalizesType() {
            var sut = new ImageHistoryService();
            sut.RecordImage("light", 2.1, "Ha");
            sut.RecordImage("LIGHT", 2.2, "Ha");

            Assert.That(sut.ImagePoints.Select(p => p.Id), Is.EqualTo(new long[] { 1, 2 }));
            Assert.That(sut.ImagePoints.Select(p => p.Type), Is.All.EqualTo("LIGHT"));
        }

        [Test]
        public void ImageHistoryService_AutofocusWatermarkIsTheImageOrdinal() {
            var sut = new ImageHistoryService();
            sut.RecordImage("light", 2.1, "Ha");
            sut.RecordImage("light", 2.2, "Ha");
            sut.RecordAutofocus(5.5, "Ha");
            sut.RecordImage("light", 1.9, "Ha");

            var af = sut.AutofocusPoints.Single();
            Assert.That(af.Id, Is.EqualTo(2));
            Assert.That(af.Temperature, Is.EqualTo(5.5));
            Assert.That(sut.ImagePoints.Count(p => p.Id > af.Id), Is.EqualTo(1),
                "exactly the post-autofocus frame is 'since the last AF'");
        }

        [Test]
        public void ImageHistoryService_TrimsTheFrontWithoutBreakingIds() {
            var sut = new ImageHistoryService();
            for (var i = 0; i < ImageHistoryService.MaxImagePoints + 5; i++) {
                sut.RecordImage("light", 2.0, null);
            }
            Assert.That(sut.ImagePoints, Has.Count.EqualTo(ImageHistoryService.MaxImagePoints));
            Assert.That(sut.ImagePoints[0].Id, Is.EqualTo(6), "oldest points age out; ids stay monotonic");
        }

        // ---- AutofocusAfterTimeTrigger ----

        [Test]
        public void TimeTrigger_FiresWhenTheLastAutofocusIsOlderThanAmount() {
            var history = new FakeHistory();
            history.Afs.Add(new AutofocusHistoryEntry(0, DateTimeOffset.UtcNow.AddMinutes(-40), 5.0, "Ha"));
            var sut = new AutofocusAfterTimeTrigger(history) { Amount = 30 };

            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.True);
            Assert.That(sut.Elapsed, Is.GreaterThanOrEqualTo(39.9));
        }

        [Test]
        public void TimeTrigger_StaysQuietWhileTheLastAutofocusIsFresh() {
            var history = new FakeHistory();
            history.Afs.Add(new AutofocusHistoryEntry(0, DateTimeOffset.UtcNow.AddMinutes(-10), 5.0, "Ha"));
            var sut = new AutofocusAfterTimeTrigger(history) { Amount = 30 };

            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.False);
        }

        [Test]
        public void TimeTrigger_AnchorsOnBlockStartWhenNoAutofocusExists() {
            var sut = new AutofocusAfterTimeTrigger(new FakeHistory()) { Amount = 0 };

            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.False,
                "no autofocus and no block start yet — nothing to measure from");

            sut.SequenceBlockInitialize();
            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.True,
                "Amount=0 fires immediately once the block-start anchor exists");
        }

        [Test]
        public void TimeTrigger_OnlyGatesLightExposures() {
            var history = new FakeHistory();
            history.Afs.Add(new AutofocusHistoryEntry(0, DateTimeOffset.UtcNow.AddMinutes(-40), 5.0, "Ha"));
            var sut = new AutofocusAfterTimeTrigger(history) { Amount = 30 };

            Assert.That(sut.ShouldTrigger(null, FlatExposure()), Is.False, "flats never need a refocus trigger");
            Assert.That(sut.ShouldTrigger(null, new Mock<ISequenceItem>().Object), Is.False);
            Assert.That(sut.ShouldTrigger(null, null), Is.False);
        }

        [Test]
        public void TimeTrigger_CloneCarriesAmount() {
            var sut = new AutofocusAfterTimeTrigger(new FakeHistory()) { Amount = 45 };
            var clone = (AutofocusAfterTimeTrigger)sut.Clone();
            Assert.That(clone, Is.Not.SameAs(sut));
            Assert.That(clone.Amount, Is.EqualTo(45));
        }

        // ---- AutofocusAfterTemperatureChangeTrigger ----

        [Test]
        public void TempTrigger_FiresOnDriftFromTheLastAutofocusTemperature() {
            var history = new FakeHistory();
            history.Afs.Add(new AutofocusHistoryEntry(0, DateTimeOffset.UtcNow, 10.0, "Ha"));
            var sut = new AutofocusAfterTemperatureChangeTrigger(Focuser(4.0).Object, history) { Amount = 5 };

            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.True);
            Assert.That(sut.DeltaT, Is.EqualTo(6.0));
        }

        [Test]
        public void TempTrigger_StaysQuietWithinTheBand() {
            var history = new FakeHistory();
            history.Afs.Add(new AutofocusHistoryEntry(0, DateTimeOffset.UtcNow, 10.0, "Ha"));
            var sut = new AutofocusAfterTemperatureChangeTrigger(Focuser(8.0).Object, history) { Amount = 5 };

            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.False);
            Assert.That(sut.DeltaT, Is.EqualTo(2.0));
        }

        [Test]
        public void TempTrigger_IgnoresAutofocusRecordsWithoutATemperature() {
            var history = new FakeHistory();
            history.Afs.Add(new AutofocusHistoryEntry(0, DateTimeOffset.UtcNow, 10.0, "Ha"));
            history.Afs.Add(new AutofocusHistoryEntry(1, DateTimeOffset.UtcNow, double.NaN, "Ha"));
            var sut = new AutofocusAfterTemperatureChangeTrigger(Focuser(4.0).Object, history) { Amount = 5 };

            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.True,
                "the NaN record (focuser lost its sensor mid-session) must not mask the real reference");
        }

        [Test]
        public void TempTrigger_NoFocuserOrNoSensorMeansNoTrigger() {
            var noFocuser = new AutofocusAfterTemperatureChangeTrigger(null, new FakeHistory()) { Amount = 0 };
            Assert.That(noFocuser.ShouldTrigger(null, LightExposure()), Is.False);

            var noSensor = new AutofocusAfterTemperatureChangeTrigger(Focuser(double.NaN).Object, new FakeHistory()) { Amount = 0 };
            Assert.That(noSensor.ShouldTrigger(null, LightExposure()), Is.False);
        }

        [Test]
        public void TempTrigger_AnchorsOnInitializeWhenNoAutofocusExists() {
            var focuser = Focuser(10.0);
            var sut = new AutofocusAfterTemperatureChangeTrigger(focuser.Object, new FakeHistory()) { Amount = 5 };
            sut.Initialize();

            focuser.Setup(f => f.GetInfo()).Returns(new FocuserInfo { Connected = true, Temperature = 3.0 });
            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.True);
            Assert.That(sut.DeltaT, Is.EqualTo(7.0));
        }

        [Test]
        public void TempTrigger_ValidateFlagsAMissingFocuser() {
            var sut = new AutofocusAfterTemperatureChangeTrigger(Focuser(5.0, connected: false).Object, null);
            Assert.That(sut.Validate(), Is.False);
            Assert.That(sut.Issues, Has.Count.EqualTo(1));
        }

        // ---- AutofocusAfterHFRIncreaseTrigger ----

        private static FakeHistory HfrHistory(params double[] hfrs) {
            var history = new FakeHistory();
            for (var i = 0; i < hfrs.Length; i++) {
                history.Images.Add(new ImageHistoryEntry(i + 1, "LIGHT", hfrs[i], "Ha"));
            }
            return history;
        }

        [Test]
        public void HfrTrigger_FiresOnARisingTrend() {
            // Least squares over 2.0..2.4 predicts 2.4 at the newest sample — 20% over the 2.0 low.
            var sut = new AutofocusAfterHFRIncreaseTrigger(HfrHistory(2.0, 2.1, 2.2, 2.3, 2.4), FilterWheel("Ha").Object) {
                Amount = 5,
                SampleSize = 5,
            };

            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.True);
            Assert.That(sut.HFRTrendPercentage, Is.EqualTo(20.0).Within(0.01));
            Assert.That(sut.OriginalHFR, Is.EqualTo(2.0));
        }

        [Test]
        public void HfrTrigger_StaysQuietOnAFlatTrend() {
            var sut = new AutofocusAfterHFRIncreaseTrigger(HfrHistory(2.0, 2.0, 2.0, 2.0, 2.0), FilterWheel("Ha").Object) {
                Amount = 5,
                SampleSize = 5,
            };

            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.False);
            Assert.That(sut.HFRTrendPercentage, Is.EqualTo(0.0).Within(0.01));
        }

        [Test]
        public void HfrTrigger_OnlyReadsFramesAfterTheLastAutofocus() {
            var history = HfrHistory(3.0, 3.5, 4.0, 4.5, 5.0); // terrible pre-AF drift
            history.Afs.Add(new AutofocusHistoryEntry(5, DateTimeOffset.UtcNow, 5.0, "Ha"));
            history.Images.Add(new ImageHistoryEntry(6, "LIGHT", 2.0, "Ha"));
            history.Images.Add(new ImageHistoryEntry(7, "LIGHT", 2.0, "Ha"));
            history.Images.Add(new ImageHistoryEntry(8, "LIGHT", 2.0, "Ha"));
            var sut = new AutofocusAfterHFRIncreaseTrigger(history, FilterWheel("Ha").Object) { Amount = 5, SampleSize = 5 };

            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.False,
                "the drift the autofocus already fixed must not re-fire the trigger");
            Assert.That(sut.OriginalHFR, Is.EqualTo(2.0));
        }

        [Test]
        public void HfrTrigger_FiltersByTheSelectedFilter() {
            var history = new FakeHistory();
            // A rising Ha trend interleaved with pristine OIII frames — only Ha may count.
            var id = 0;
            foreach (var hfr in new[] { 2.0, 2.1, 2.2, 2.3, 2.4 }) {
                history.Images.Add(new ImageHistoryEntry(++id, "LIGHT", hfr, "Ha"));
                history.Images.Add(new ImageHistoryEntry(++id, "LIGHT", 1.5, "OIII"));
            }
            var sut = new AutofocusAfterHFRIncreaseTrigger(history, FilterWheel("Ha").Object) { Amount = 5, SampleSize = 5 };

            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.True);
            Assert.That(sut.Filter, Is.EqualTo("Ha"));
            Assert.That(sut.OriginalHFR, Is.EqualTo(2.0), "the OIII 1.5 low must not be the baseline");
        }

        [Test]
        public void HfrTrigger_NeedsAtLeastThreeSamplesAndAHistory() {
            Assert.That(new AutofocusAfterHFRIncreaseTrigger(null, null).ShouldTrigger(null, LightExposure()), Is.False);
            Assert.That(new AutofocusAfterHFRIncreaseTrigger(HfrHistory(2.0, 4.0), FilterWheel("Ha").Object) { Amount = 5 }
                .ShouldTrigger(null, LightExposure()), Is.False);
        }

        [Test]
        public void HfrTrigger_SampleSizeBelowThreeIsRejected() {
            var sut = new AutofocusAfterHFRIncreaseTrigger(null, null) { SampleSize = 7 };
            sut.SampleSize = 2;
            Assert.That(sut.SampleSize, Is.EqualTo(7), "a bad import value keeps the previous size instead of throwing");
        }

        // ---- AutofocusAfterFilterChange ----

        [Test]
        public void FilterTrigger_FiresWhenTheWheelLeftTheLastAutofocusFilter() {
            var history = new FakeHistory();
            history.Afs.Add(new AutofocusHistoryEntry(0, DateTimeOffset.UtcNow, 5.0, "Ha"));
            var sut = new AutofocusAfterFilterChange(FilterWheel("OIII").Object, history, null);

            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.True);
            Assert.That(sut.LastAutoFocusFilter, Is.EqualTo("OIII"), "the new filter becomes the reference");
        }

        [Test]
        public void FilterTrigger_StaysQuietOnTheSameFilter() {
            var history = new FakeHistory();
            history.Afs.Add(new AutofocusHistoryEntry(0, DateTimeOffset.UtcNow, 5.0, "Ha"));
            var sut = new AutofocusAfterFilterChange(FilterWheel("Ha").Object, history, null);

            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.False);
        }

        [Test]
        public void FilterTrigger_AdoptsTheCurrentFilterOnFirstSight() {
            var wheel = FilterWheel("Ha");
            var sut = new AutofocusAfterFilterChange(wheel.Object, new FakeHistory(), null);

            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.False, "first sight only anchors");

            wheel.Setup(w => w.GetInfo()).Returns(new FilterWheelInfo {
                Connected = true,
                SelectedFilter = new FilterInfo("OIII", 0, 0),
            });
            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.True);
        }

        [Test]
        public void FilterTrigger_DesignatedAutofocusFilterKeepsLocalTracking() {
            var profile = new Mock<IProfileService>();
            var filters = new ObserveAllCollection<FilterInfo> { new("L", 0, 0) { AutoFocusFilter = true } };
            profile.SetupGet(p => p.ActiveProfile.FilterWheelSettings.FilterWheelFilters).Returns(filters);
            var history = new FakeHistory();
            history.Afs.Add(new AutofocusHistoryEntry(0, DateTimeOffset.UtcNow, 5.0, "Ha"));

            var sut = new AutofocusAfterFilterChange(FilterWheel("OIII").Object, history, profile.Object);
            sut.Initialize(); // wheel already on OIII

            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.False,
                "with a designated AF filter the history record must not override local tracking");
        }

        [Test]
        public void FilterTrigger_ATransientNullFilterNeitherFiresNorClobbersTheReference() {
            var wheel = FilterWheel("Ha");
            var sut = new AutofocusAfterFilterChange(wheel.Object, new FakeHistory(), null);
            sut.Initialize();

            // Mid-move / reconnect blip: the wheel momentarily reports no selected filter.
            wheel.Setup(w => w.GetInfo()).Returns(new FilterWheelInfo { Connected = true, SelectedFilter = null! });
            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.False,
                "there is no filter to focus for — a spurious AF here wastes sky time");
            Assert.That(sut.LastAutoFocusFilter, Is.EqualTo("Ha"), "the reference survives the blip");

            // The same filter comes back — nothing changed, still quiet.
            wheel.Setup(w => w.GetInfo()).Returns(new FilterWheelInfo {
                Connected = true,
                SelectedFilter = new FilterInfo("Ha", 0, 0),
            });
            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.False);

            // A genuinely different filter still fires.
            wheel.Setup(w => w.GetInfo()).Returns(new FilterWheelInfo {
                Connected = true,
                SelectedFilter = new FilterInfo("OIII", 0, 0),
            });
            Assert.That(sut.ShouldTrigger(null, LightExposure()), Is.True);
        }

        [Test]
        public void FilterTrigger_ValidateFlagsAMissingWheel() {
            var sut = new AutofocusAfterFilterChange(FilterWheel("Ha", connected: false).Object, null, null);
            Assert.That(sut.Validate(), Is.False);
            Assert.That(sut.Issues, Has.Count.EqualTo(1));
        }
    }
}
