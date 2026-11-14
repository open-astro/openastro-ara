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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>What a device declares about its commands (ADR 0011, #1808): who a fault is blamed on, the
/// hard wall-clock bound on the blocking call, the settle poll, and whether the motion flag is optional.
/// <see cref="MotionFlagOptional"/> lets <see cref="UnreadableReadsBeforeSettled"/> unreadable reads in a
/// row end the wait; otherwise an unreadable read never counts as done (rule 6).
/// <see cref="PublishFaults"/> false is the opt-out for a command whose failure is not a device fault.</summary>
internal sealed record DeviceCommandPolicy(
    DeviceType Device,
    TimeSpan HardTimeout,
    TimeSpan PollInterval,
    int MaxPolls,
    bool MotionFlagOptional = false,
    int UnreadableReadsBeforeSettled = 0,
    bool PublishFaults = true);

/// <summary>One command. <see cref="Description"/> is the sentence start of every message ("rotator move
/// to 90°"). <see cref="Terminal"/> reads the terminal condition (true done, false not yet, null unreadable);
/// null means nothing to wait for. <see cref="Dispatched"/> runs once the call has returned,
/// <see cref="Polled"/> after each settle read. <see cref="IsLive"/> and <see cref="CurrentDevice"/> come
/// from the device's lifecycle: the module never calls the lifecycle itself (rule 7).</summary>
internal sealed record DeviceCommandRequest(
    string Description,
    Action Call,
    Func<bool> IsLive,
    Func<DiscoveredDeviceDto?> CurrentDevice,
    Func<bool?>? Terminal = null,
    Action? Dispatched = null,
    Action? Polled = null);

/// <summary>ADR 0011 (#1808) — the command module: the off-thread call, the race against the sequencer
/// token and a hard timeout, the settle poll, the verdict, the fault publish and the abandoned call.
/// A device holds one, built with its <see cref="DeviceCommandPolicy"/>.</summary>
internal sealed partial class DeviceCommand {

    private readonly DeviceCommandPolicy _policy;
    private readonly IEquipmentFaultSink? _faults;
    private readonly ILogger _logger;

    public DeviceCommand(DeviceCommandPolicy policy, IEquipmentFaultSink? faults, ILogger logger) {
        _policy = policy;
        _faults = faults;
        _logger = logger;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Sequencer command boundary: the blocking device call can throw arbitrary driver/HTTP exceptions, and a concurrent Disconnect/Dispose can dispose the device mid-call; genuine sequencer cancellation is rethrown, and any other escape (including a device/HTTP timeout OCE) is logged, published as a §42.4 fault, and rethrown as SequenceEntityFailedException so the instruction's retry/failure machinery engages (§42.2). CA1031's catch-classify-rethrow boundary applies.")]
    public async Task RunAsync(DeviceCommandRequest request, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        try {
            // Race the blocking call against BOTH the sequencer token and a wall-clock bound so a hung
            // HTTP call can't pin the sequence thread. The abandoned call is observed so it can't
            // surface as an unobserved task exception.
            var callTask = Task.Run(request.Call, CancellationToken.None);
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                linked.CancelAfter(_policy.HardTimeout);
                var completed = await Task.WhenAny(callTask, Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);
                if (completed != callTask) {
                    ObserveQuietly(callTask, request.Description);
                    ct.ThrowIfCancellationRequested();          // sequencer cancel → propagate
                    throw new TimeoutException(                 // wall-clock bound → device fault
                        $"{request.Description} did not complete within {_policy.HardTimeout.TotalSeconds:0}s");
                }
                await linked.CancelAsync().ConfigureAwait(false); // call won the race: cancel the timer so it can't leak
            }
            await callTask.ConfigureAwait(false); // observe the call's result / surface its exception
            request.Dispatched?.Invoke();
            if (request.Terminal is not null && !await WaitSettledAsync(request, ct).ConfigureAwait(false)) {
                // The call returned but the command was never confirmed done (still going after the
                // full bound, or the device dropped/was superseded mid-wait — the publish gate
                // suppresses the fault for the latter, the §42.3 probe owns it).
                var msg = $"{request.Description} was not confirmed settled within the {_policy.MaxPolls * _policy.PollInterval.TotalSeconds:0}s bound";
                Publish(request, EquipmentFaultKind.StallTimeout, msg);
                throw new SequenceEntityFailedException(msg);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw; // genuine sequencer cancellation — propagate so the run aborts
        } catch (SequenceEntityFailedException) {
            throw; // already classified + published above
        } catch (TimeoutException ex) {
            LogCommandFailed(ex, request.Description);
            Publish(request, EquipmentFaultKind.StallTimeout, ex.Message);
            throw new SequenceEntityFailedException(ex.Message, ex);
        } catch (Exception ex) {
            LogCommandFailed(ex, request.Description);
            var msg = $"{request.Description} failed: {ex.Message}";
            Publish(request, EquipmentFaultKind.OpError, msg);
            throw new SequenceEntityFailedException(msg, ex);
        }
    }

    // Polls the terminal condition until done or the bound elapses. Returns false when the command
    // could not be CONFIRMED done: bound exhausted, or the device is gone (disconnected / superseded /
    // disposed — a dropped connection must NOT read as done). An unreadable read never counts as done
    // (rule 6) unless the policy declares the flag optional and N come in a row; a real read resets it.
    private async Task<bool> WaitSettledAsync(DeviceCommandRequest request, CancellationToken ct) {
        var unreadable = 0;
        for (var i = 0; i < _policy.MaxPolls; i++) {
            await Task.Delay(_policy.PollInterval, ct).ConfigureAwait(false);
            if (!request.IsLive()) {
                return false;
            }
            var done = request.Terminal!();
            request.Polled?.Invoke();
            if (done == true) {
                return true;
            }
            if (done is null) {
                if (_policy.MotionFlagOptional && ++unreadable >= _policy.UnreadableReadsBeforeSettled) {
                    return true;
                }
            } else {
                unreadable = 0;
            }
        }
        return false;
    }

    // §42.4 — a fault may only be blamed on the LIVE device: a command whose device was superseded or
    // disposed mid-call stays a log line, so CurrentDevice supplies both the check and the snapshot.
    private void Publish(DeviceCommandRequest request, EquipmentFaultKind kind, string details) {
        if (!_policy.PublishFaults || _faults is null || request.CurrentDevice() is not { } device) {
            return;
        }
        _faults.Publish(new EquipmentFaultEvent(_policy.Device, device.UniqueId, device.Name, kind, details, DateTimeOffset.UtcNow));
    }

    // Observes an abandoned (cancelled/timed-out) call so a later fault can't surface as an
    // UnobservedTaskException, and logs it at Debug for post-mortem.
    private void ObserveQuietly(Task task, string description) {
        _ = task.ContinueWith(
            t => LogAbandonedFaulted(t.Exception!, description),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "abandoned {Description} (cancelled/timed-out) later faulted")]
    private partial void LogAbandonedFaulted(Exception ex, string description);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Description} failed")]
    private partial void LogCommandFailed(Exception ex, string description);
}
