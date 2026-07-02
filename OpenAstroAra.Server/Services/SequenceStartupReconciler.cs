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
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using System;
using System.IO;

namespace OpenAstroAra.Server.Services;

/// <summary>Result of a §28.2 startup reconciliation pass.</summary>
public enum SequenceReconcileOutcome {
    /// <summary>No checkpoint file existed — clean shutdown.</summary>
    Clean,
    /// <summary>Checkpoint found + parsed; daemon crashed mid-sequence.</summary>
    Interrupted,
    /// <summary>Checkpoint found but JSON malformed — quarantined per §28.1.</summary>
    Corrupt,
}

/// <summary>Outcome details from <see cref="SequenceStartupReconciler.Reconcile"/>.</summary>
public sealed record SequenceReconcileResult(
    SequenceReconcileOutcome Outcome,
    SequenceRunStateDto? PreviousState,
    string? QuarantinedPath);

/// <summary>
/// §28.2 daemon-startup reconciler. Inspects
/// <c>{profileDir}/sequences/active/current.json</c>; if it exists the
/// daemon was killed mid-sequence. The §28.2 policy is "do not
/// auto-resume; require an explicit user action" so this reconciler:
///
/// <list type="number">
///   <item>Reads the checkpoint (or quarantines it via the §28.1
///     <c>.corrupt.&lt;unix-ts&gt;</c> rename when JSON parse fails).</item>
///   <item>Removes the file so subsequent startups don't re-trigger.</item>
///   <item>Returns a <see cref="Result"/> the caller can use to emit a
///     §46 notification ("the previous sequence ended unexpectedly" or
///     "checkpoint file was damaged") + log a CHECKPOINT_INTERRUPTED /
///     CHECKPOINT_CORRUPT entry.</item>
/// </list>
///
/// Notification emission lives in the caller (Program.cs startup) so this
/// service stays a pure file-IO helper testable without the
/// INotificationService injection chain.
/// </summary>
public sealed partial class SequenceStartupReconciler {

    private readonly ActiveSequenceCheckpoint _checkpoint;
    private readonly ILogger<SequenceStartupReconciler> _logger;

    public SequenceStartupReconciler(ActiveSequenceCheckpoint checkpoint, ILogger<SequenceStartupReconciler>? logger = null) {
        _checkpoint = checkpoint;
        _logger = logger ?? NullLogger<SequenceStartupReconciler>.Instance;
    }

    /// <summary>
    /// Run the §28.2 reconciliation once. Idempotent: subsequent calls on
    /// a clean state return <see cref="Outcome.Clean"/>.
    /// </summary>
    public SequenceReconcileResult Reconcile() {
        if (!_checkpoint.Exists()) {
            return new SequenceReconcileResult(SequenceReconcileOutcome.Clean, PreviousState: null, QuarantinedPath: null);
        }

        var previous = _checkpoint.TryRead();
        if (previous is not null) {
            LogCheckpointInterrupted(previous.SequenceId, previous.State, previous.InstructionsCompleted, previous.InstructionsTotal);
            // Clear the file — §28.2 policy is no auto-resume, the user
            // explicitly re-starts via REST. Keeping the file would
            // re-trigger on every subsequent startup.
            _checkpoint.Clear();
            return new SequenceReconcileResult(SequenceReconcileOutcome.Interrupted, previous, QuarantinedPath: null);
        }

        // File exists but failed to parse — §28.1 corruption quarantine.
        // Rename to <file>.corrupt.<unix-ts> and clear the canonical path.
        var quarantinePath = $"{_checkpoint.FilePath}.corrupt.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        try {
            File.Move(_checkpoint.FilePath, quarantinePath);
            LogCheckpointQuarantined(quarantinePath);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogQuarantineFailed(ex, _checkpoint.FilePath);
            // Last-resort: just delete it so startup proceeds. Diagnostics
            // lost but the alternative is the file blocking every future
            // startup.
            try { File.Delete(_checkpoint.FilePath); } catch (Exception delEx) when (delEx is IOException or UnauthorizedAccessException) { }
            return new SequenceReconcileResult(SequenceReconcileOutcome.Corrupt, PreviousState: null, QuarantinedPath: null);
        }
        return new SequenceReconcileResult(SequenceReconcileOutcome.Corrupt, PreviousState: null, QuarantinedPath: quarantinePath);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "CHECKPOINT_INTERRUPTED: previous sequence {SeqId} was {State} with frame {Frame}/{Total} at startup")]
    private partial void LogCheckpointInterrupted(Guid seqId, SequenceRunState state, int frame, int total);

    [LoggerMessage(Level = LogLevel.Error, Message = "CHECKPOINT_CORRUPT: active sequence checkpoint failed to parse; quarantined to {QuarantinePath}")]
    private partial void LogCheckpointQuarantined(string quarantinePath);

    [LoggerMessage(Level = LogLevel.Error, Message = "CHECKPOINT_CORRUPT: failed to quarantine unreadable checkpoint at {Path}")]
    private partial void LogQuarantineFailed(Exception ex, string path);
}