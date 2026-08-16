using NUnit.Framework;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test;

/// <summary>
/// §25.5.6 — the fan/cooler safety interlock (the "never cool without the fan"
/// rule). These test the pure decision function directly; the HTTP write paths
/// (PutFanAsync + ProbeFanAsync) are exercised against the Alpaca simulator in
/// the live-rig verification.
/// </summary>
[TestFixture]
public class FanInterlockTest {
    [Test]
    public void AutoStartFan_when_fan_exists_and_is_off() {
        var verdict = CameraService.FanInterlock(
            fanSpeed: 0, fanMax: 3, fanSupportKnown: true);
        Assert.That(verdict, Is.EqualTo(CameraService.FanInterlockVerdict.AutoStartFan));
    }

    [Test]
    public void FanAlreadyOn_when_fan_exists_and_is_running() {
        var verdict = CameraService.FanInterlock(
            fanSpeed: 1, fanMax: 3, fanSupportKnown: true);
        Assert.That(verdict, Is.EqualTo(CameraService.FanInterlockVerdict.FanAlreadyOn));
    }

    [Test]
    public void NoFanNeeded_when_the_route_confirmed_no_fan() {
        // A clean 404/1025 (or a settled null) means the camera has no
        // controllable fan — passive heat sinking, cooling is fine.
        var verdict = CameraService.FanInterlock(
            fanSpeed: null, fanMax: null, fanSupportKnown: true);
        Assert.That(verdict, Is.EqualTo(CameraService.FanInterlockVerdict.NoFanNeeded));
    }

    [Test]
    public void FailClosed_when_fan_state_is_unknown() {
        // Initial connect, or every probe so far hit a transient failure —
        // the interlock must REFUSE, not silently skip and let the TEC run
        // without a fan. This is the transient-read gap from the review.
        var verdict = CameraService.FanInterlock(
            fanSpeed: null, fanMax: null, fanSupportKnown: false);
        Assert.That(verdict, Is.EqualTo(CameraService.FanInterlockVerdict.UnknownFailClosed));
    }

    [Test]
    public void FailClosed_even_when_speed_looks_off() {
        // A stale speed with no established support must not bypass the gate.
        var verdict = CameraService.FanInterlock(
            fanSpeed: 0, fanMax: null, fanSupportKnown: false);
        Assert.That(verdict, Is.EqualTo(CameraService.FanInterlockVerdict.UnknownFailClosed));
    }
}
