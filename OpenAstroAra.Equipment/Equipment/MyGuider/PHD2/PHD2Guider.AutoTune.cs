#region "copyright"

/* Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors. */

#endregion

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;

/// <summary>Small typed RPC surface used by ARA's transactional guiding tuner.</summary>
public sealed partial class PHD2Guider {
    public async Task<IReadOnlyList<int>> GetSupportedExposureDurationsAsync(CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        var response = await SendMessage<AutoTuneExposureDurationsResponse>(new Phd2GetExposureDurations()).ConfigureAwait(false);
        ThrowIfRpcError(response, "get_exposure_durations");
        return response.result ?? Array.Empty<int>();
    }

    public async Task<int> GetGuideExposureMillisecondsAsync(CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        var response = await SendMessage<GetExposureResponse>(new Phd2GetExposure()).ConfigureAwait(false);
        ThrowIfRpcError(response, "get_exposure");
        return response.result ?? 0;
    }

    public async Task SetGuideExposureMillisecondsAsync(int milliseconds, CancellationToken ct) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(milliseconds);
        ct.ThrowIfCancellationRequested();
        var response = await SendMessage(new Phd2SetExposure { Parameters = new object[] { milliseconds } }).ConfigureAwait(false);
        ThrowIfRpcError(response, "set_exposure");
    }

    public async Task<bool> GetGuideOutputEnabledAsync(CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        var response = await SendMessage<BooleanPhdMethodResponse>(new Phd2GetGuideOutputEnabled()).ConfigureAwait(false);
        ThrowIfRpcError(response, "get_guide_output_enabled");
        return response.result ?? false;
    }

    public async Task SetGuideOutputEnabledAsync(bool enabled, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        var response = await SendMessage(new Phd2SetGuideOutputEnabled { Parameters = new object[] { enabled } }).ConfigureAwait(false);
        ThrowIfRpcError(response, "set_guide_output_enabled");
    }

    public async Task<string> GetDecGuideModeAsync(CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        var response = await SendMessage(new Phd2GetDecGuideMode()).ConfigureAwait(false);
        ThrowIfRpcError(response, "get_dec_guide_mode");
        return response.result?.ToString() ?? "Auto";
    }

    public async Task SetDecGuideModeAsync(string mode, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(mode)) throw new ArgumentException("DEC guide mode is required.", nameof(mode));
        ct.ThrowIfCancellationRequested();
        var response = await SendMessage(new Phd2SetDecGuideMode {
            Parameters = new Phd2SetDecGuideModeParameter { Mode = mode },
        }).ConfigureAwait(false);
        ThrowIfRpcError(response, "set_dec_guide_mode");
    }

    public async Task<IReadOnlyDictionary<string, double>> GetAlgorithmParametersAsync(string axis, CancellationToken ct) {
        ValidateAxis(axis);
        ct.ThrowIfCancellationRequested();
        var namesResponse = await SendMessage<AutoTuneAlgorithmNamesResponse>(new Phd2GetAlgoParamNames {
            Parameters = new object[] { axis },
        }).ConfigureAwait(false);
        ThrowIfRpcError(namesResponse, "get_algo_param_names");
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in namesResponse.result ?? Array.Empty<string>()) {
            if (name.Equals("algorithmName", StringComparison.OrdinalIgnoreCase)) continue;
            var response = await SendMessage(new AutoTuneGetAlgorithmParameter {
                Parameters = new AutoTuneAlgorithmParameter { Axis = axis, Name = name },
            }).ConfigureAwait(false);
            ThrowIfRpcError(response, $"get_algo_param:{name}");
            if (double.TryParse(response.result?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
                values[name] = value;
            }
        }
        return values;
    }

    public async Task<string> GetAlgorithmAsync(string axis, CancellationToken ct) {
        ValidateAxis(axis);
        ct.ThrowIfCancellationRequested();
        var response = await SendMessage(new AutoTuneGetAlgorithm {
            Parameters = new AutoTuneAlgorithmAxis { Axis = axis },
        }).ConfigureAwait(false);
        ThrowIfRpcError(response, "get_algo");
        return response.result?.ToString() ?? "Hysteresis";
    }

    public async Task SetAlgorithmAsync(string axis, string algorithm, CancellationToken ct) {
        ValidateAxis(axis);
        if (string.IsNullOrWhiteSpace(algorithm)) throw new ArgumentException("Algorithm is required.", nameof(algorithm));
        ct.ThrowIfCancellationRequested();
        var response = await SendMessage(new AutoTuneSetAlgorithm {
            Parameters = new AutoTuneSetAlgorithmParameter { Axis = axis, Algorithm = algorithm },
        }).ConfigureAwait(false);
        ThrowIfRpcError(response, "set_algo");
    }

    public async Task SetAlgorithmParameterAsync(string axis, string name, double value, CancellationToken ct) {
        ValidateAxis(axis);
        if (string.IsNullOrWhiteSpace(name) || !double.IsFinite(value)) throw new ArgumentException("Algorithm parameter is invalid.");
        ct.ThrowIfCancellationRequested();
        var response = await SendMessage(new Phd2SetAlgoParam {
            Parameters = new Phd2SetAlgoParamParameter { Axis = axis, Name = name, Value = value },
        }).ConfigureAwait(false);
        ThrowIfRpcError(response, $"set_algo_param:{name}");
    }

    public async Task<string> GetCalibrationJsonAsync(CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        var response = await SendMessage(new Phd2GetCalibrationData()).ConfigureAwait(false);
        ThrowIfRpcError(response, "get_calibration_data");
        return response.result is null ? "null" : JsonConvert.SerializeObject(response.result);
    }

    public async Task<(double RaMaximumPulseMilliseconds, double DecMaximumPulseMilliseconds)> GetGuideLimitsAsync(CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        var response = await SendMessage(new AutoTuneGetGuideLimits()).ConfigureAwait(false);
        ThrowIfRpcError(response, "get_guide_limits");
        if (response.result is not JObject result)
            return (500, 500);
        return (ReadNumber(result, "MaxRaDuration", 500), ReadNumber(result, "MaxDecDuration", 500));
    }

    public async Task SetGuideLimitsAsync(double raMaximumPulseMilliseconds, double decMaximumPulseMilliseconds, CancellationToken ct) {
        if (!double.IsFinite(raMaximumPulseMilliseconds) || !double.IsFinite(decMaximumPulseMilliseconds)
            || raMaximumPulseMilliseconds < 1 || decMaximumPulseMilliseconds < 1)
            throw new ArgumentOutOfRangeException(nameof(raMaximumPulseMilliseconds));
        ct.ThrowIfCancellationRequested();
        var response = await SendMessage(new AutoTuneSetGuideLimits {
            Parameters = new AutoTuneGuideLimitsParameter {
                RaMaximumPulseMilliseconds = raMaximumPulseMilliseconds,
                DecMaximumPulseMilliseconds = decMaximumPulseMilliseconds,
            },
        }).ConfigureAwait(false);
        ThrowIfRpcError(response, "set_guide_limits");
    }

    private static void ValidateAxis(string axis) {
        if (!string.Equals(axis, "ra", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(axis, "dec", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Axis must be ra or dec.", nameof(axis));
    }

    private static void ThrowIfRpcError(PhdMethodResponse response, string method) {
        if (response.error is not null) throw new InvalidOperationException($"PHD2 RPC {method} failed: {response.error.message ?? response.error.code.ToString(CultureInfo.InvariantCulture)}");
    }

    private static double ReadNumber(JObject result, string property, double fallback) =>
        result.TryGetValue(property, StringComparison.OrdinalIgnoreCase, out var token)
        && token is not null && token.ToObject<double>() is var value && double.IsFinite(value) && value > 0
            ? value : fallback;
}

public sealed class AutoTuneExposureDurationsResponse : PhdMethodResponse {
    public IReadOnlyList<int>? result { get; set; }
}

public sealed class AutoTuneAlgorithmNamesResponse : PhdMethodResponse {
    public IReadOnlyList<string>? result { get; set; }
}

internal sealed class AutoTuneGetAlgorithmParameter : Phd2Method<AutoTuneAlgorithmParameter> {
    public override string Method => "get_algo_param";
}

internal sealed class AutoTuneGetAlgorithm : Phd2Method<AutoTuneAlgorithmAxis> {
    public override string Method => "get_algo";
}

internal sealed class AutoTuneSetAlgorithm : Phd2Method<AutoTuneSetAlgorithmParameter> {
    public override string Method => "set_algo";
}

internal sealed class AutoTuneGetGuideLimits : Phd2Method {
    public override string Method => "get_guide_limits";
}

internal sealed class AutoTuneSetGuideLimits : Phd2Method<AutoTuneGuideLimitsParameter> {
    public override string Method => "set_guide_limits";
}

internal sealed class AutoTuneGuideLimitsParameter {
    [JsonProperty(PropertyName = "MaxRaDuration")]
    public double RaMaximumPulseMilliseconds { get; set; }

    [JsonProperty(PropertyName = "MaxDecDuration")]
    public double DecMaximumPulseMilliseconds { get; set; }
}

internal class AutoTuneAlgorithmAxis {
    [JsonProperty(PropertyName = "axis")]
    public string Axis { get; set; } = string.Empty;
}

internal sealed class AutoTuneAlgorithmParameter : AutoTuneAlgorithmAxis {
    [JsonProperty(PropertyName = "name")]
    public string Name { get; set; } = string.Empty;
}

internal sealed class AutoTuneSetAlgorithmParameter : AutoTuneAlgorithmAxis {
    [JsonProperty(PropertyName = "name")]
    public string Algorithm { get; set; } = string.Empty;
}
