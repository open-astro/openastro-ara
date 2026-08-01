#region "copyright"

/* Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors. */

#endregion

using NUnit.Framework;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Guider;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test;

[TestFixture]
public sealed class PHD2AutoTuneRpcTest {
    private static readonly int[] SupportedExposures = [250, 500, 1000];

    [Test]
    public async Task AutoTune_control_surface_uses_typed_JSON_RPC_and_runtime_algorithm_names() {
        await using var fake = FakeGuider.Start();
        fake.OnRpc("get_profile", _ => new JsonObject { ["name"] = "Default", ["id"] = 1 });
        fake.OnRpc("get_profiles", _ => new JsonArray(new JsonObject { ["name"] = "Default", ["id"] = 1 }));
        fake.OnRpc("get_version", _ => new JsonObject {
            ["version"] = "2.6.11",
            ["phd_version"] = "2.6.11",
            ["phd_subver"] = "ara-test",
            ["msg_version"] = 1,
            ["overlap_support"] = true,
            ["fork"] = "openastro-guider",
        });
        fake.OnRpc("get_connected", JsonValue.Create(true));
        fake.OnRpc("get_pixel_scale", JsonValue.Create(1.5));
        fake.OnRpc("get_exposure_durations", new JsonArray(250, 500, 1000));
        fake.OnRpc("get_exposure", JsonValue.Create(500));
        fake.OnRpc("get_guide_output_enabled", JsonValue.Create(true));
        fake.OnRpc("get_dec_guide_mode", JsonValue.Create("Auto"));
        fake.OnRpc("get_algo", _ => JsonValue.Create("Hysteresis"));
        fake.OnRpc("get_algo_param_names", _ => new JsonArray("algorithmName", "aggression", "minMove"));
        fake.OnRpc("get_algo_param", request => {
            var parameters = request["params"]?.AsObject();
            return JsonValue.Create((string?)parameters?["name"] switch {
                "aggression" => 0.7,
                "minMove" => 0.15,
                _ => 0.0,
            });
        });
        fake.OnRpc("get_calibration_data", _ => new JsonObject());
        fake.OnRpc("get_guide_limits", _ => new JsonObject {
            ["MaxRaDuration"] = 500,
            ["MaxDecDuration"] = 600,
        });

        var profiles = new HeadlessProfileService();
        profiles.ActiveProfile.GuiderSettings.PHD2ServerHost = "127.0.0.1";
        profiles.ActiveProfile.GuiderSettings.PHD2ServerPort = fake.Port;
        using var guider = new PHD2Guider(profiles);
        Assert.That(await guider.Connect(CancellationToken.None).ConfigureAwait(false), Is.True);
        Assert.That(await guider.GetSupportedExposureDurationsAsync(CancellationToken.None), Is.EqualTo(SupportedExposures));
        Assert.That(await guider.GetGuideExposureMillisecondsAsync(CancellationToken.None), Is.EqualTo(500));
        Assert.That(await guider.GetGuideOutputEnabledAsync(CancellationToken.None), Is.True);
        Assert.That(await guider.GetDecGuideModeAsync(CancellationToken.None), Is.EqualTo("Auto"));
        Assert.That(await guider.GetAlgorithmAsync("ra", CancellationToken.None), Is.EqualTo("Hysteresis"));
        var parameters = await guider.GetAlgorithmParametersAsync("ra", CancellationToken.None);
        Assert.That(parameters["aggression"], Is.EqualTo(0.7));
        Assert.That(parameters["minMove"], Is.EqualTo(0.15));
        Assert.That(await guider.GetGuideLimitsAsync(CancellationToken.None), Is.EqualTo((500d, 600d)));
        Assert.That(fake.ReceivedMethods, Does.Contain("get_algo_param_names"));
        Assert.That(fake.ReceivedMethods.Count(method => method == "get_algo_param"), Is.GreaterThanOrEqualTo(2));
    }

}
