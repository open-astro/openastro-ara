#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

public interface IServerUpdateService {

    /// <summary>Stage an uploaded <c>.deb</c> and inspect it. Throws
    /// <see cref="ServerUpdateRejectedException"/> when the file is not a newer
    /// <c>openastroara-server</c> package for this machine, or when the daemon is not a
    /// packaged install.</summary>
    Task<ServerUpdateStagedDto> StageAsync(Stream body, long? declaredLength, string? expectedSha256, CancellationToken ct);

    /// <summary>Hand a staged package to the root helper. Returns once the helper unit has
    /// been asked to start; the daemon is restarted by the helper shortly after.</summary>
    Task<ServerUpdateStatusDto> ApplyAsync(string id, CancellationToken ct);

    /// <summary>Outcome of an apply, read from the helper's result file; <c>pending</c> while
    /// the helper runs. Null when the id is unknown.</summary>
    Task<ServerUpdateStatusDto?> GetStatusAsync(string id, CancellationToken ct);
}

/// <summary>Typed refusal: <see cref="Reason"/> is the wire token, <see cref="Message"/> the detail.</summary>
public sealed class ServerUpdateRejectedException : Exception {
    public string Reason { get; }

    public ServerUpdateRejectedException(string reason, string message) : base(message) {
        Reason = reason;
    }

    public ServerUpdateRejectedException() : this("rejected", "The update was rejected.") {
    }

    public ServerUpdateRejectedException(string message) : this("rejected", message) {
    }

    public ServerUpdateRejectedException(string message, Exception innerException) : base(message, innerException) {
        Reason = "rejected";
    }
}

/// <summary>Paths and tool names the service uses; swapped by tests.</summary>
/// <param name="StageDirectory">Daemon-owned; uploads land here as <c>&lt;id&gt;.deb</c>.</param>
/// <param name="RequestDirectory">Daemon-owned tmpfs exchange: <c>&lt;id&gt;.request</c> in, <c>&lt;id&gt;.result</c> out.</param>
/// <param name="HelperUnitTemplate">The root-side oneshot; its presence is the "packaged install" tell.</param>
internal sealed record ServerUpdatePaths(string StageDirectory, string RequestDirectory, string HelperUnitTemplate) {
    public static ServerUpdatePaths Default { get; } = new(
        "/var/lib/openastroara/updates",
        "/run/openastroara/update",
        "/etc/systemd/system/openastroara-update@.service");
}

/// <summary>
/// §33 client-pushed update (#1122). The online path is apt (§34.4); at a dark site with no
/// internet the client pushes a <c>.deb</c> it downloaded earlier. The daemon only stages and
/// inspects (<c>dpkg-deb -f</c>, <c>dpkg-query</c>, <c>dpkg --compare-versions</c>: all
/// unprivileged); installing is <c>openastroara-update@&lt;id&gt;.service</c>, a root oneshot the
/// daemon asks systemd to start over D-Bus with a polkit rule scoped to exactly that
/// (<c>50-openastroara-update.rules</c>) — the same escalation shape as the §29.1.4 storage
/// helper, because the daemon's unit runs with <c>NoNewPrivileges=true</c> and sudo is not an
/// option. The helper keeps the previously installed package and reinstalls it when the new
/// daemon does not answer <c>/healthz</c>.
/// </summary>
public sealed partial class ServerUpdateService : IServerUpdateService {

    /// <summary>Hard cap on an upload; the arm64 self-contained .deb is ~40 MB.</summary>
    internal const long MaxUploadBytes = 256L * 1024 * 1024;

    internal const string PackageName = "openastroara-server";

    private readonly ILogger logger;
    private readonly ServerUpdatePaths paths;
    private readonly IWsBroadcaster? ws;
    private readonly int listenPort;
    private readonly Func<string, string[], CancellationToken, Task<(int ExitCode, string Output)>> run;

    public ServerUpdateService(ILogger<ServerUpdateService> logger, IWsBroadcaster ws, ServerListenPort port)
        : this(logger, ServerUpdatePaths.Default, ws, port.Port, null) {
    }

    internal ServerUpdateService(ILogger logger, ServerUpdatePaths paths, IWsBroadcaster? ws, int listenPort,
            Func<string, string[], CancellationToken, Task<(int ExitCode, string Output)>>? run) {
        this.logger = logger;
        this.paths = paths;
        this.ws = ws;
        this.listenPort = listenPort;
        this.run = run ?? RunAsync;
    }

    public async Task<ServerUpdateStagedDto> StageAsync(Stream body, long? declaredLength, string? expectedSha256, CancellationToken ct) {
        if (declaredLength is > MaxUploadBytes) {
            throw new ServerUpdateRejectedException("too_large", $"Upload exceeds {MaxUploadBytes / (1024 * 1024)} MiB.");
        }
        if (!File.Exists(paths.HelperUnitTemplate)) {
            throw new ServerUpdateRejectedException("not_packaged",
                "This daemon was not installed from the openastroara-server package, so there is nothing to update in place.");
        }
        var installed = await InstalledVersionAsync(ct).ConfigureAwait(false)
            ?? throw new ServerUpdateRejectedException("not_packaged", "dpkg does not report openastroara-server as installed.");

        Directory.CreateDirectory(paths.StageDirectory);
        var id = Guid.NewGuid().ToString("N");
        var file = Path.Combine(paths.StageDirectory, id + ".deb");
        long size;
        try {
            string sha256;
            (size, sha256) = await CopyCappedAsync(body, file, ct).ConfigureAwait(false);
            // §33.4 — optional, but when the client sends it a Wi-Fi drop that truncated the
            // upload is caught here rather than by dpkg-deb's less helpful error.
            if (!string.IsNullOrWhiteSpace(expectedSha256)
                    && !string.Equals(expectedSha256.Trim(), sha256, StringComparison.OrdinalIgnoreCase)) {
                throw new ServerUpdateRejectedException("checksum_mismatch",
                    $"Upload SHA-256 {sha256} does not match the declared {expectedSha256.Trim()} (incomplete transfer?).");
            }
            var (package, version, arch) = await InspectAsync(file, ct).ConfigureAwait(false);
            if (!string.Equals(package, PackageName, StringComparison.Ordinal)) {
                throw new ServerUpdateRejectedException("wrong_package", $"The file is '{package}', not {PackageName}.");
            }
            var hostArch = await HostArchitectureAsync(ct).ConfigureAwait(false);
            if (hostArch is not null && !string.Equals(arch, hostArch, StringComparison.Ordinal)) {
                throw new ServerUpdateRejectedException("wrong_architecture", $"The package is built for {arch}; this machine is {hostArch}.");
            }
            if (!await IsNewerAsync(version, installed, ct).ConfigureAwait(false)) {
                throw new ServerUpdateRejectedException("not_newer", $"{version} is not newer than the installed {installed}.");
            }
            LogStaged(id, version, installed, size);
            return new ServerUpdateStagedDto(id, package, version, installed, size);
        } catch {
            TryDelete(file);
            throw;
        }
    }

    public async Task<ServerUpdateStatusDto> ApplyAsync(string id, CancellationToken ct) {
        if (!IsRequestId(id)) {
            throw new ServerUpdateRejectedException("unknown_id", "Unknown update id.");
        }
        var file = Path.Combine(paths.StageDirectory, id + ".deb");
        if (!File.Exists(file)) {
            throw new ServerUpdateRejectedException("unknown_id", "Unknown update id (not staged, or already applied).");
        }
        var (_, version, _) = await InspectAsync(file, ct).ConfigureAwait(false);
        Directory.CreateDirectory(paths.RequestDirectory);
        var requestPath = Path.Combine(paths.RequestDirectory, id + ".request");
        // One argument per line, like the storage request: the staged file and the port the
        // helper should probe for /healthz once the new daemon is up.
        await File.WriteAllTextAsync(requestPath,
            file + "\n" + listenPort.ToString(CultureInfo.InvariantCulture) + "\n", ct).ConfigureAwait(false);

        PublishRestartImminent(id, version);
        // --no-block: the oneshot restarts THIS process part-way through, so a blocking start
        // would never return. The result file is what reports the outcome (GetStatusAsync).
        var (exitCode, output) = await run("systemctl", ["start", "--no-block", $"openastroara-update@{id}.service"], ct)
            .ConfigureAwait(false);
        if (exitCode != 0) {
            TryDelete(requestPath);
            throw new ServerUpdateRejectedException("helper_unavailable",
                string.IsNullOrWhiteSpace(output) ? "systemd refused to start the update helper." : output.Trim());
        }
        LogApplyStarted(id, version);
        return new ServerUpdateStatusDto(id, "pending", null, version, false, string.Empty);
    }

    public async Task<ServerUpdateStatusDto?> GetStatusAsync(string id, CancellationToken ct) {
        if (!IsRequestId(id)) {
            return null;
        }
        var resultPath = Path.Combine(paths.RequestDirectory, id + ".result");
        if (File.Exists(resultPath)) {
            return ParseResult(id, await File.ReadAllTextAsync(resultPath, ct).ConfigureAwait(false));
        }
        if (File.Exists(Path.Combine(paths.RequestDirectory, id + ".request"))) {
            return new ServerUpdateStatusDto(id, "pending", null, null, false, string.Empty);
        }
        return null;
    }

    /// <summary>Result file: line 1 the helper's exit code (storage shape), then
    /// <c>key=value</c> lines (<c>status</c>, <c>from</c>, <c>to</c>, <c>rollback</c>) mixed with log
    /// output. A missing status line is a helper that died before reporting: <c>failed</c>.</summary>
    internal static ServerUpdateStatusDto ParseResult(string id, string text) {
        var (exitCode, rest) = StorageDeviceService.ParseHelperResult(text);
        string? status = null, from = null, to = null;
        var rollback = false;
        foreach (var raw in rest.Split('\n')) {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("status=", StringComparison.Ordinal)) status = line[7..].Trim();
            else if (line.StartsWith("from=", StringComparison.Ordinal)) from = line[5..].Trim();
            else if (line.StartsWith("to=", StringComparison.Ordinal)) to = line[3..].Trim();
            else if (line.StartsWith("rollback=", StringComparison.Ordinal)) rollback = line[9..].Trim() == "available";
        }
        status = status switch {
            "applied" or "rolled_back" or "failed" => status,
            _ => exitCode == 0 ? "applied" : "failed",
        };
        return new ServerUpdateStatusDto(id, status, from, to, rollback, rest.Trim());
    }

    private static async Task<(long Size, string Sha256)> CopyCappedAsync(Stream body, string file, CancellationToken ct) {
        await using var target = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16];
        long total = 0;
        int n;
        while ((n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) {
            total += n;
            if (total > MaxUploadBytes) {
                // Chunked bodies carry no Content-Length; stop at the cap instead of filling the disk.
                throw new ServerUpdateRejectedException("too_large", $"Upload exceeds {MaxUploadBytes / (1024 * 1024)} MiB.");
            }
            hash.AppendData(buffer, 0, n);
            await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
        }
        if (total == 0) {
            throw new ServerUpdateRejectedException("empty", "The upload was empty.");
        }
        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private async Task<(string Package, string Version, string Architecture)> InspectAsync(string file, CancellationToken ct) {
        var (code, output) = await run("dpkg-deb", ["-f", file, "Package", "Version", "Architecture"], ct).ConfigureAwait(false);
        if (code != 0) {
            throw new ServerUpdateRejectedException("not_a_package", "The file is not a Debian package.");
        }
        string? package = null, version = null, arch = null;
        foreach (var raw in output.Split('\n')) {
            var line = raw.Trim();
            if (line.StartsWith("Package:", StringComparison.Ordinal)) package = line[8..].Trim();
            else if (line.StartsWith("Version:", StringComparison.Ordinal)) version = line[8..].Trim();
            else if (line.StartsWith("Architecture:", StringComparison.Ordinal)) arch = line[13..].Trim();
        }
        if (package is null || version is null || arch is null) {
            throw new ServerUpdateRejectedException("not_a_package", "The package control file lacks Package/Version/Architecture.");
        }
        return (package, version, arch);
    }

    private async Task<string?> InstalledVersionAsync(CancellationToken ct) {
        var (code, output) = await run("dpkg-query", ["-W", "-f=${Version}", PackageName], ct).ConfigureAwait(false);
        return code == 0 && !string.IsNullOrWhiteSpace(output) ? output.Trim() : null;
    }

    private async Task<string?> HostArchitectureAsync(CancellationToken ct) {
        var (code, output) = await run("dpkg", ["--print-architecture"], ct).ConfigureAwait(false);
        return code == 0 && !string.IsNullOrWhiteSpace(output) ? output.Trim() : null;
    }

    private async Task<bool> IsNewerAsync(string candidate, string installed, CancellationToken ct) {
        // dpkg's own ordering (epochs, ~, -ara.N suffixes), not a home-grown parser.
        var (code, _) = await run("dpkg", ["--compare-versions", candidate, "gt", installed], ct).ConfigureAwait(false);
        return code == 0;
    }

    private void PublishRestartImminent(string id, string version) {
        if (ws is null) {
            return;
        }
        var payload = new JsonObject {
            ["reason"] = "update",
            ["update_id"] = id,
            ["version"] = version,
            // The helper waits this long for the old daemon to drain before dpkg runs.
            ["in_seconds"] = 5,
        };
        using var doc = JsonDocument.Parse(payload.ToJsonString());
        _ = ws.PublishAsync(WsEventCatalog.ServerRestartImminent, doc.RootElement.Clone(), CancellationToken.None);
    }

    internal static bool IsRequestId(string id) => StorageDeviceService.UuidShape().IsMatch(id);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort cleanup of a staged upload or exchange file; a leftover is harmless (unique id) and must never mask the real refusal.")]
    private static void TryDelete(string path) {
        try {
            File.Delete(path);
        } catch (Exception) {
            // Intentionally swallowed — see justification.
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Probing dpkg/systemctl is best-effort: a missing binary must become a typed refusal, never crash the request.")]
    private static async Task<(int ExitCode, string Output)> RunAsync(string file, string[] args, CancellationToken ct) {
        try {
            var info = new ProcessStartInfo(file) {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) {
                info.ArgumentList.Add(a);
            }
            using var process = Process.Start(info);
            if (process is null) {
                return (-1, string.Empty);
            }
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return (process.ExitCode, process.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout) ? stderr : stdout);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return (-1, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Update {Id} staged: {Version} over installed {Installed} ({Bytes} bytes)")]
    private partial void LogStaged(string id, string version, string installed, long bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update {Id} handed to the root helper: installing {Version}; this daemon restarts shortly")]
    private partial void LogApplyStarted(string id, string version);
}

/// <summary>The port Kestrel listens on, registered by Program so services that hand it to
/// out-of-process helpers (the update helper probes <c>/healthz</c> on it) read one value.</summary>
public sealed record ServerListenPort(int Port);
