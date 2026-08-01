using Moq;
using NUnit.Framework;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PhdEvents;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using System;
using System.Linq;

namespace OpenAstroAra.Test;

[TestFixture]
public sealed class GuidingTelemetryCollectorTest {
    [Test]
    public void Collector_copies_optional_fields_context_and_rejects_out_of_order_frames() {
        var mediator = new Mock<IGuiderMediator>();
        using var collector = new OpenAstroAra.Server.Services.Guiding.GuidingTelemetryCollector(mediator.Object);
        collector.SetContext(exposureMilliseconds: 500, guidePixelScaleArcsecPerPixel: 1.2,
            guideOutputEnabled: false, mountRightAscensionHours: 2.5,
            mountDeclinationDegrees: -10, mountAzimuthDegrees: 180,
            parameterSnapshotHash: "hash");

        mediator.Raise(m => m.GuideEvent += null, this, Step(100, 8, 1));
        mediator.Raise(m => m.GuideEvent += null, this, Step(99, 9, 2));

        var sample = collector.GetWindow().Samples.Single();
        Assert.That(sample.MultiStarCount, Is.EqualTo(8));
        Assert.That(sample.RejectedStarCount, Is.EqualTo(1));
        Assert.That(sample.ExposureMilliseconds, Is.EqualTo(500));
        Assert.That(sample.GuidePixelScaleArcsecPerPixel, Is.EqualTo(1.2));
        Assert.That(sample.MountDeclinationDegrees, Is.EqualTo(-10));
        Assert.That(sample.ParameterSnapshotHash, Is.EqualTo("hash"));
        Assert.That(sample.GuideOutputEnabled, Is.False);
    }

    private static PhdEventGuideStep Step(double time, int stars, int rejected) => new() {
        Frame = time,
        Time = time,
        RADistanceRaw = .1,
        DECDistanceRaw = -.2,
        RADistanceGuide = .1,
        DECDistanceGuide = -.2,
        SNR = 20,
        HFD = 3,
        StarMass = 1000,
        MultiStarCount = stars,
        RejectedStarCount = rejected,
    };
}
