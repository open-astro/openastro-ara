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
using OpenAstroAra.Core.Interfaces;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §63 guider-a — the first real guider service. Replaces <c>PlaceholderGuiderService</c> by driving
/// the re-ported <see cref="PHD2Guider"/> (PHD2's JSON-RPC client) over the §6 REST surface.
///
/// Unlike the Alpaca device services, PHD2 is its own protocol: there is no discovery handshake —
/// the client connects to a host/port (default <c>localhost:4400</c>) carried by the profile
/// (§63.5). <see cref="ConnectAsync"/> pushes the request's host/port into the active profile's
/// guider settings, then constructs a fresh <see cref="PHD2Guider"/> and connects on a background
/// task per the §60.5 202-Accepted contract; callers poll <see cref="GetAsync"/> for the outcome.
///
/// No §32.4 cache loop is needed: <see cref="PHD2Guider"/> maintains its <c>State</c> live from its
/// own background JSON-RPC listener, so <see cref="GetAsync"/> reads it directly.
///
/// RMS (<c>RmsTotal/Ra/Dec</c>) is left null for now — TODO(§63): accumulate it from the guider's
/// <c>GuideEvent</c> stream (guider-b). Mediator unification (<c>IGuiderMediator</c> so the sequencer
/// drives the live guider) is a separate follow-up that replaces <c>HeadlessGuiderMediator</c>.
/// </summary>
public sealed partial class GuiderService : IGuiderService, IDisposable {

    private readonly ILogger<GuiderService> _logger;
    private readonly IProfileService _profileService;
    // §63.6: WS sink for guider.dark_library.* events. Optional so the service composes in tests without a hub.
    private readonly IWsBroadcaster? _ws;
    private readonly object _gate = new();
    // A no-op progress sink: the headless daemon reports guider progress to clients over the §60.9 WS
    // stream, not through NINA's in-process IProgress reporter.
    private readonly IProgress<ApplicationStatus> _noProgress = new Progress<ApplicationStatus>();

    // §63.2 RMS window: the most recent guide-step errors (raw pixels), bounded so the RMS reflects
    // recent guiding rather than the whole session. Fed by the guider's GuideEvent stream.
    private const int MaxGuideStepWindow = 200;
    private readonly Queue<(double Ra, double Dec)> _guideSteps = new();

    private PHD2Guider? _guider;
    private EquipmentConnectionState _state = EquipmentConnectionState.Disconnected;
    private long _connectGeneration; // this attempt's id; later attempts/disconnects bump it to supersede
    // Cancels the in-flight background connect (incl. its systemd-start/reachability wait) when a
    // newer connect or a disconnect supersedes it — so a rapid connect→disconnect can't leave the
    // ensure-reachable loop polling for up to its full deadline.
    private CancellationTokenSource? _connectCts;
    // Service-lifetime token for the fire-and-forget calibration builds (dark library / defect map).
    // PHD2's build RPC is a long blocking call that isn't itself cancellable, but linking the
    // background Task.Run to this token + passing it into the build gives those builds a shutdown
    // signal and avoids leaving an orphaned, unobservable task behind a CancellationToken.None on
    // Dispose. Cancelled + disposed in Dispose.
    private readonly CancellationTokenSource _shutdownCts = new();
    private bool _disposed;

    // §63.1: ask systemd to start the guider service if it isn't already up when we connect
    // (the §63 deployment runs openastro-guider as a systemd unit, not as an ARA child process).
    private readonly IGuiderProcessSupervisor _supervisor;

    public GuiderService(IProfileService profileService, GuiderRecoveryCoordinator recovery, ILogger<GuiderService> logger,
            IGuiderProcessSupervisor supervisor, IWsBroadcaster? ws = null,
            IProfileStore? profileStore = null, Func<ISequencerService?>? sequencerResolver = null,
            INotificationService? notifications = null, IFaultLogService? faultLog = null,
            Func<Guid?>? activeProfileIdResolver = null) {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _ws = ws;
        _profileStore = profileStore;
        _sequencerResolver = sequencerResolver;
        _notifications = notifications;
        _faultLog = faultLog;
        _activeProfileIdResolver = activeProfileIdResolver;
    }

    // §30.7.4 — resolves the multi-profile repository's ActiveId so the calibration-state stamp can
    // detect a profile switch during a long build (POST /profiles/{id}/select swaps the live store
    // without touching the guider). Null in benches/tests without a repository → stamp unconditionally.
    private readonly Func<Guid?>? _activeProfileIdResolver;

    public Task<GuiderDto?> GetAsync(CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_guider is null) {
                return Task.FromResult<GuiderDto?>(null);
            }
            var (rmsTotal, rmsRa, rmsDec) = ComputeRms(_guideSteps);
            var pixelScale = _guider.PixelScale;
            var runtime = new GuiderStateDto(
                State: MapGuidingState(_guider.State),
                RmsTotal: rmsTotal, // raw pixels over the recent guide-step window
                RmsRa: rmsRa,
                RmsDec: rmsDec,
                CurrentProfile: _guider.SelectedProfile?.Name,
                RmsTotalArcsec: RmsArcsec(rmsTotal, pixelScale),
                RmsRaArcsec: RmsArcsec(rmsRa, pixelScale),
                RmsDecArcsec: RmsArcsec(rmsDec, pixelScale));
            return Task.FromResult<GuiderDto?>(new GuiderDto("PHD2_Single", "PHD2", _state, runtime));
        }
    }

    public Task<OperationAcceptedDto> ConnectAsync(GuiderConnectRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        ConnectCoreAsync(request, idempotencyKey, supersedeRecovery: true);

    // §63.3 — the auto-reconnect path must NOT supersede the recovery pass that
    // issued it (CancelRecoveryLocked would cancel the reconnect loop's own token
    // mid-flight); every user/REST connect does.
    internal Task<OperationAcceptedDto> ConnectCoreAsync(GuiderConnectRequestDto request, string? idempotencyKey, bool supersedeRecovery) {
        ArgumentNullException.ThrowIfNull(request);
        long generation;
        PHD2Guider guider;
        CancellationToken connectToken;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Idempotent: connecting while already connecting/connected is a no-op accept (§60.5).
            if (_state is EquipmentConnectionState.Connecting or EquipmentConnectionState.Connected) {
                return Task.FromResult(Accepted("guider.connect", idempotencyKey));
            }
            if (supersedeRecovery) {
                // A fresh user connect supersedes any §63.3 recovery still polling
                // for the dropped session.
                CancelRecoveryLocked();
            }
            // PHD2Guider reads host/port from the profile (§63.5), so honor the request by writing
            // them in before the connect.
            var settings = _profileService.ActiveProfile.GuiderSettings;
            settings.PHD2ServerHost = request.Host;
            settings.PHD2ServerPort = request.Port;
            DisposeGuiderLocked();
            guider = new PHD2Guider(_profileService);
            // Observe a mid-session socket drop: PHD2Guider raises PHD2ConnectionLost from its
            // listener's finally when the link dies. Without this, _state would stay Connected
            // forever and RequireConnectedGuider would keep dispatching to a dead guider.
            guider.PHD2ConnectionLost += OnConnectionLost;
            guider.GuideEvent += OnGuideStep;
            // §42.2: a structured device fault (guide camera dropped) — the link stays up, so this runs
            // the on_guider_lost policy without dropping to Error or starting §63.3 recovery.
            guider.EquipmentFault += OnEquipmentFault;
            _guideSteps.Clear(); // fresh session — drop the prior connection's RMS window
            _guider = guider;
            generation = ++_connectGeneration;
            CancelConnectLocked();
            _connectCts = new CancellationTokenSource();
            connectToken = _connectCts.Token;
            SetStateLocked(EquipmentConnectionState.Connecting);
        }
        // 202 contract: do the blocking connect off-thread; GetAsync reports the outcome.
        _ = Task.Run(() => ConnectInBackground(guider, generation, connectToken), CancellationToken.None);
        return Task.FromResult(Accepted("guider.connect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ++_connectGeneration; // supersede any in-flight connect
            CancelConnectLocked(); // and cancel its ensure-reachable/start wait immediately
            CancelRecoveryLocked(); // the user gave up on the guider — stop recovering it
            DisposeGuiderLocked();
            SetStateLocked(EquipmentConnectionState.Disconnected);
        }
        return Task.FromResult(Accepted("guider.disconnect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> StartGuidingAsync(string? idempotencyKey, CancellationToken ct) {
        var guider = RequireConnectedGuider();
        _ = Task.Run(() => guider.StartGuiding(forceCalibration: false, _noProgress, CancellationToken.None), CancellationToken.None);
        return Task.FromResult(Accepted("guider.start", idempotencyKey));
    }

    public Task<OperationAcceptedDto> StopGuidingAsync(string? idempotencyKey, CancellationToken ct) {
        var guider = RequireConnectedGuider();
        _ = Task.Run(() => guider.StopGuiding(CancellationToken.None), CancellationToken.None);
        return Task.FromResult(Accepted("guider.stop", idempotencyKey));
    }

    public Task<OperationAcceptedDto> DitherAsync(double pixels, string? idempotencyKey, CancellationToken ct) {
        var guider = RequireConnectedGuider();
        // PHD2Guider.Dither reads the amplitude from the profile; honor the request's pixels.
        _profileService.ActiveProfile.GuiderSettings.DitherPixels = pixels;
        _ = Task.Run(() => guider.Dither(_noProgress, CancellationToken.None), CancellationToken.None);
        return Task.FromResult(Accepted("guider.dither", idempotencyKey));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background connect boundary: PHD2Guider.Connect can throw arbitrary socket/IO/protocol exceptions; any escape must surface as the Error state and be contained. Log-and-recover.")]
    private async Task ConnectInBackground(PHD2Guider guider, long generation, CancellationToken ct) {
        try {
            // §63.1: the guider runs as a systemd service. If it isn't reachable yet (e.g. it
            // hasn't finished booting, or is stopped), ask systemd to start it and wait briefly —
            // ARA never spawns the guider itself. Off-systemd (dev/CI) RequestStart is a no-op, so
            // this just probes; the bench's FakeGuider is already listening and passes immediately.
            // ct is cancelled if a newer connect or a disconnect supersedes this attempt.
            await EnsureGuiderReachableAsync(ct).ConfigureAwait(false);
            // Bail before the (potentially long, uncancellable) socket connect if a disconnect or a
            // newer connect already superseded us — otherwise this task lingers on the OS connect
            // timeout for an unreachable guider even though its result will be discarded.
            ct.ThrowIfCancellationRequested();
            var ok = await guider.Connect(CancellationToken.None).ConfigureAwait(false);
            lock (_gate) {
                if (generation != _connectGeneration) {
                    return; // superseded by a newer connect/disconnect
                }
                SetStateLocked(ok ? EquipmentConnectionState.Connected : EquipmentConnectionState.Error);
            }
        } catch (OperationCanceledException) {
            // Superseded by a newer connect/disconnect — that operation already owns the state;
            // this attempt just stops quietly (not a connect failure).
        } catch (Exception ex) {
            LogConnectFailed(ex);
            lock (_gate) {
                if (generation == _connectGeneration) {
                    SetStateLocked(EquipmentConnectionState.Error);
                }
            }
        }
    }

    // Ensure the guider service is up before connecting. If it's already reachable (the normal
    // case — it booted with the Pi), return at once; otherwise ask systemd to start it and poll
    // for it to come up, bounded by a timeout. Off-systemd the start is a no-op, so this degrades
    // to a probe — fine for dev/CI/bench where the guider (or FakeGuider) is started externally.
    private async Task EnsureGuiderReachableAsync(CancellationToken ct) {
        var settings = _profileService.ActiveProfile.GuiderSettings;
        string host = settings.PHD2ServerHost;
        int port = settings.PHD2ServerPort;
        if (await IsReachableAsync(host, port, ct).ConfigureAwait(false)) {
            return;
        }
        // IsReachableAsync swallows cancellation as "not reachable", so bail explicitly if the
        // attempt was already superseded — otherwise we'd log + systemctl-start spuriously for a
        // connect that's being torn down. The OCE is handled cleanly in ConnectInBackground.
        ct.ThrowIfCancellationRequested();
        LogGuiderNotReachableStarting(host, port);
        _supervisor.RequestStart();
        // Re-probe until the unit comes up, bounded by a ~10s wall-clock deadline (so the worst
        // case is the deadline, not iterations × per-probe-timeout) and cancelled the moment a
        // disconnect/reconnect supersedes this attempt.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (!deadline.IsCancellationRequested) {
            try {
                await Task.Delay(TimeSpan.FromMilliseconds(500), deadline.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // The linked token covers both the supersede (ct) and the 10s deadline. A supersede
                // stops quietly; a deadline-during-delay should still reach the timeout log below.
                if (ct.IsCancellationRequested) {
                    return;
                }
                break;
            }
            if (await IsReachableAsync(host, port, deadline.Token).ConfigureAwait(false)) {
                return;
            }
        }
        // Reaching here means the 10s deadline expired (a supersede would have returned from the
        // loop's OCE catch). Log it for ops diagnosis, then fall through and let guider.Connect()
        // fail into the Error state — the §63.3 recovery path handles it from there.
        if (!ct.IsCancellationRequested) {
            LogGuiderStartTimeout(host, port);
        }
    }

    private static async Task<bool> IsReachableAsync(string host, int port, CancellationToken ct) {
        using var probe = new TcpClient();
        try {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(750));
            await probe.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            // ConnectAsync returned without throwing ⇒ connected (reachable). Shut down gracefully
            // (FIN, not the RST a bare Dispose sends) to be polite to a remote guider; a failure to
            // do so doesn't change the reachable verdict.
            try {
                probe.Client?.Shutdown(SocketShutdown.Both);
            } catch (Exception e) when (e is SocketException or ObjectDisposedException) {
                // already closed / disposed by the peer — still reachable
            }
            return true;
        } catch (SocketException) {
            return false; // nothing listening / host unresolved
        } catch (OperationCanceledException) {
            return false; // probe timed out / superseded
        }
    }

    // PHD2 dropped the socket mid-session: surface it as Error so GetAsync stops reporting Connected
    // and RequireConnectedGuider rejects further guide ops until the client reconnects. Guarded on
    // the current guider so a torn-down stale instance's late event can't clobber a newer connect.
    private void OnConnectionLost(object? sender, EventArgs e) {
        lock (_gate) {
            if (ReferenceEquals(sender, _guider) && _state == EquipmentConnectionState.Connected) {
                HandleLinkDownLocked();
            }
        }
    }

    // §42.2: the daemon reported a structured device fault (the guide camera dropped). Unlike a socket
    // drop this leaves the guider LINK up and the daemon may be self-reconnecting, so we do NOT go to
    // Error or start §63.3 process recovery — guiding is simply degraded now, so we run the
    // on_guider_lost policy and stay Connected. We react only to the guide CAMERA: the daemon emits only
    // camera today, and a future non-guiding device (rotator/aux) must not pause the sequence as "guiding
    // lost". (Pairing reconnecting=false / the daemon's silent reconnect-abandonment — guider#66 — with an
    // ARA-side watchdog is a tracked follow-up.)
    private void OnEquipmentFault(object? sender, EquipmentFaultEventArgs e) {
        lock (_gate) {
            if (ReferenceEquals(sender, _guider) && _state == EquipmentConnectionState.Connected
                    && AffectsGuiding(e.DeviceType)) {
                BeginFaultReactionLocked(GuiderFaultKind.EquipmentDisconnected);
            }
        }
    }

    // The daemon's device-type enum is effectively "camera" today. Treat a missing/blank type as the
    // guide camera (fail toward alerting on the one device we know it emits) and ignore any other named
    // device until per-device guiding impact is modelled.
    private static bool AffectsGuiding(string? deviceType) =>
        string.IsNullOrWhiteSpace(deviceType)
        || deviceType.Equals("camera", StringComparison.OrdinalIgnoreCase);

    // Shared §63.3/§42.2 link-down path: a dead socket (PHD2ConnectionLost) and a
    // wedged-but-connected daemon (the active-poll ping streak, GuiderService.ActivePoll.cs)
    // converge here. Caller holds _gate and has verified the loss belongs to the
    // current guider/session.
    private void HandleLinkDownLocked() {
        SetStateLocked(EquipmentConnectionState.Error);
        // §63.3 guider-d: the daemon dropped mid-session — try to recover the process
        // (and, on success, auto-reconnect within the §63.3 grace window).
        BeginRecoveryLocked();
        // §42.2: guiding is gone NOW — any running sequence is shooting unguided,
        // so react per the profile's on_guider_lost policy (GuiderService.FaultReaction.cs).
        BeginFaultReactionLocked(GuiderFaultKind.LinkDown);
    }

    // Append each guide step's raw RA/Dec error to the bounded RMS window. Guarded on the current
    // guider so a torn-down stale instance's late event can't pollute a newer session's window.
    private void OnGuideStep(object? sender, IGuideStep step) {
        if (step is null) {
            return;
        }
        lock (_gate) {
            if (!ReferenceEquals(sender, _guider)) {
                return;
            }
            _guideSteps.Enqueue((step.RADistanceRaw, step.DECDistanceRaw));
            while (_guideSteps.Count > MaxGuideStepWindow) {
                _guideSteps.Dequeue();
            }
        }
    }

    // RMS (root-mean-square) of the windowed guide errors, in raw pixels. Total is the combined
    // RA/Dec magnitude RMS = sqrt(mean(ra^2 + dec^2)). Null when no steps have arrived yet.
    internal static (double? Total, double? Ra, double? Dec) ComputeRms(IReadOnlyCollection<(double Ra, double Dec)> steps) {
        if (steps.Count == 0) {
            return (null, null, null);
        }
        double sumRa2 = 0, sumDec2 = 0;
        foreach (var (ra, dec) in steps) {
            sumRa2 += ra * ra;
            sumDec2 += dec * dec;
        }
        var n = steps.Count;
        return (Math.Sqrt((sumRa2 + sumDec2) / n), Math.Sqrt(sumRa2 / n), Math.Sqrt(sumDec2 / n));
    }

    // §63 guider-a follow-up — the arcsec twin of a pixel RMS: scaled by the guider's reported
    // pixel scale (arcsec/px), the unit guiding accuracy is conventionally quoted in. A scale of
    // 0 means PHD2 hasn't reported one yet → arcsec unknown (null), never a fake 0″.
    internal static double? RmsArcsec(double? rmsPixels, double pixelScaleArcsecPerPx) =>
        rmsPixels is { } px && pixelScaleArcsecPerPx > 0 ? px * pixelScaleArcsecPerPx : null;

    // internal (not private): the §45 PolarAlignService reuses this to reach the connected guider for the
    // PA-session lease + solver captures, with the same connected-or-throw contract the guide ops use.
    internal PHD2Guider RequireConnectedGuider() {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_guider is null || _state != EquipmentConnectionState.Connected) {
                throw new InvalidOperationException("guider is not connected");
            }
            return _guider;
        }
    }

    // NINA PhdAppState → §63.2 GuiderStateDto.State token. Selected/Looping/unknown collapse to
    // "stopped"; "dithering" arrives via the GuideEvent settling path (not modeled until RMS lands).
    private static string MapGuidingState(string? phdState) => phdState switch {
        "Calibrating" => "calibrating",
        "Guiding" => "guiding",
        "Paused" => "paused",
        "LostLock" => "star_lost",
        _ => "stopped",
    };

    private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
        new(OperationId: Guid.NewGuid(),
            OperationType: operationType,
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);

    private void SetStateLocked(EquipmentConnectionState state) {
        _state = state;
        if (state == EquipmentConnectionState.Connected) {
            // A fresh successful connection starts a new fault episode: the §42.2
            // reaction may fire again on the next mid-session loss.
            _latchedFaultKind = null;
            // §42.5 — the reconnect resolves this guider's open disconnect fault rows (the
            // same observed-reconnect semantics the Alpaca devices get from the publisher
            // hook); best-effort and off-lock.
            ResolveGuiderFaultsQuietly();
            StartPingLoopLocked();
        } else {
            StopPingLoopLocked();
        }
    }

    private void DisposeGuiderLocked() {
        if (_guider is not null) {
            _guider.PHD2ConnectionLost -= OnConnectionLost;
            _guider.GuideEvent -= OnGuideStep;
            _guider.EquipmentFault -= OnEquipmentFault;
            _guider.Disconnect();
            _guider.Dispose();
            _guider = null;
        }
    }

    public void Dispose() {
        lock (_gate) {
            if (_disposed) {
                return;
            }
            _disposed = true;
            // Cancel + dispose the in-flight recovery pass (if any). RunRecoveryAsync's finally also
            // disposes under the gate; the null-check there makes either order safe, and disposing
            // here satisfies CA2213. No new pass can start — BeginRecoveryLocked checks _disposed.
            CancelRecoveryLocked();
            _recoveryPassCts?.Dispose();
            _recoveryPassCts = null;
            CancelConnectLocked();
            // §63.3 — stop + dispose the liveness-ping timer (CA2213).
            StopPingLoopLocked();
            // Signal any in-flight calibration build to wind down. Its background task observes the
            // token where it can and its finally releases the gate; no new build can start
            // (BuildDarkLibraryAsync/BuildDefectMapDarksAsync check _disposed at entry).
            _shutdownCts.Cancel();
            _shutdownCts.Dispose();
            DisposeGuiderLocked();
        }
    }

    // Cancel + dispose the in-flight background connect's token (its ensure-reachable/systemd-start
    // wait). Void so the synchronous Cancel() doesn't trip CA1849 in the Task-returning callers.
    private void CancelConnectLocked() {
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = null;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "PHD2 guider connect failed")]
    partial void LogConnectFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Guider not reachable at {Host}:{Port} — requesting a systemd start before connecting")]
    partial void LogGuiderNotReachableStarting(string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guider still not reachable at {Host}:{Port} after the start wait — proceeding to connect (it will fail into Error)")]
    partial void LogGuiderStartTimeout(string host, int port);
}
