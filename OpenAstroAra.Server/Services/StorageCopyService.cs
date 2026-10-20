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
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>Why a copy could not start; the endpoint maps it to a Problem.</summary>
public sealed record StorageCopyRefusal(int StatusCode, string Code, string Detail);

public interface IStorageCopyService {
    /// <summary>True while a copy is preparing or copying: the store and the target
    /// must not be reconfigured, checked or ejected underneath it.</summary>
    bool IsRunning { get; }

    StorageCopyStatusDto Status();

    /// <summary>Start copying the store (or <paramref name="folders"/> on it) onto the
    /// drive <paramref name="targetUuid"/>. Returns null once the job is running in the
    /// background (progress via <see cref="Status"/>), or why it was refused.</summary>
    StorageCopyRefusal? Start(string targetUuid, IReadOnlyList<string>? folders);

    /// <summary>Cancel the running copy; false when nothing is running.</summary>
    bool Cancel();
}

/// <summary>
/// Disk-to-disk copy from the store (<c>/media/openastroara</c>) onto a second drive,
/// mounted for the duration at <c>/media/openastroara-copy</c> by the root helper.
/// The layout is mirrored at the target's top level, so the copy can itself serve as
/// a store later. Copy only: nothing is ever deleted or overwritten. A file already
/// there with the same size is skipped (re-running a copy only adds what is new); a
/// different file under the same name is reported as a conflict and left alone.
/// Each file is written under a <c>.ara-copy.tmp</c> name, flushed, then renamed, so
/// a pulled cable or a cancel never leaves a truncated frame under its real name.
/// Hidden entries (OS metadata such as <c>.Spotlight-V100</c>), in-flight <c>*.tmp</c>
/// writes and the §65.4 preview-cache sidecars (regenerated on demand) are not copied.
/// </summary>
public sealed partial class StorageCopyService : IStorageCopyService, IDisposable, IAsyncDisposable {

    internal const string TempSuffix = ".ara-copy.tmp";

    /// <summary>Per-file allowance on top of the bytes themselves when checking free
    /// space: the target rounds every file up to its cluster size (exFAT on large
    /// drives uses 128 KiB clusters).</summary>
    internal const long PerFileSlackBytes = 128 * 1024;

    private static readonly string[] SkippedDirectories = ["lost+found", "System Volume Information", "$RECYCLE.BIN"];

    private readonly IStorageDeviceService devices;
    private readonly ILogger logger;
    private readonly string sourceRoot;
    private readonly Func<bool?> storeMounted;
    private readonly Func<string, long?> freeBytes;
    private readonly object gate = new();

    private Job? job;
    private CancellationTokenSource? cts;

    public StorageCopyService(IStorageDeviceService devices, ILogger<StorageCopyService> logger)
        : this(devices, logger, StoreMountProbe.MountPoint, StoreMountProbe.IsStoreMounted, FreeBytes) {
    }

    internal StorageCopyService(IStorageDeviceService devices, ILogger logger, string sourceRoot,
            Func<bool?> storeMounted, Func<string, long?> freeBytes) {
        this.devices = devices;
        this.logger = logger;
        this.sourceRoot = Path.GetFullPath(sourceRoot);
        this.storeMounted = storeMounted;
        this.freeBytes = freeBytes;
    }

    /// <summary>The background task of the current job; tests await it.</summary>
    internal Task? Running { get; private set; }

    public bool IsRunning {
        get {
            lock (gate) {
                return job is { State: "preparing" or "copying" };
            }
        }
    }

    public StorageCopyStatusDto Status() {
        lock (gate) {
            return job?.Snapshot() ?? new StorageCopyStatusDto("idle", null, [], 0, 0, 0, 0, 0, 0, 0,
                null, null, null, null, false, null, null);
        }
    }

    public StorageCopyRefusal? Start(string targetUuid, IReadOnlyList<string>? folders) {
        if (string.IsNullOrWhiteSpace(targetUuid)) {
            return new StorageCopyRefusal(400, "bad_uuid", "target_uuid is required");
        }
        if (storeMounted() == false || !Directory.Exists(sourceRoot)) {
            return new StorageCopyRefusal(409, "no_store",
                "No storage drive is connected, so there is nothing to copy from.");
        }
        var relative = new List<string>();
        foreach (var folder in folders ?? []) {
            var rel = ToRelative(folder);
            if (rel is null) {
                return new StorageCopyRefusal(400, "bad_folder", $"'{folder}' is not a folder on the storage drive.");
            }
            if (rel.Length == 0) {
                relative.Clear(); // the store root itself: the whole store
                break;
            }
            relative.Add(rel);
        }
        lock (gate) {
            if (job is { State: "preparing" or "copying" }) {
                return new StorageCopyRefusal(409, "copy_running", "A copy is already running.");
            }
            job = new Job(targetUuid, relative);
            cts?.Dispose();
            cts = new CancellationTokenSource();
            var token = cts.Token;
            var started = job;
            Running = Task.Run(() => RunAsync(started, token), CancellationToken.None);
        }
        LogStarted(logger, targetUuid, relative.Count == 0 ? "the whole store" : string.Join(", ", relative));
        return null;
    }

    public bool Cancel() {
        lock (gate) {
            if (job is not { State: "preparing" or "copying" } || cts is null) {
                return false;
            }
            cts.Cancel();
            return true;
        }
    }

    /// <summary>Daemon shutdown cancels a running copy; its finally still releases the target.</summary>
    public void Dispose() {
        lock (gate) {
            cts?.Cancel();
            cts?.Dispose();
            cts = null;
        }
    }

    /// <summary>Host shutdown: cancel a running copy and wait (bounded) for its finally to
    /// release the target, so the daemon never stops with a drive left mounted.</summary>
    public async ValueTask DisposeAsync() {
        Task? running;
        CancellationTokenSource? source;
        lock (gate) {
            source = cts;
            running = Running;
        }
        if (source is not null) {
            await source.CancelAsync().ConfigureAwait(false);
        }
        if (running is not null) {
            try {
                await running.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            } catch (TimeoutException) {
                // The release is a helper round trip; past this the host is going down anyway.
            }
        }
        Dispose();
    }

    /// <summary>A store path as the client may send it → path relative to the store
    /// root ("" for the root itself), or null when it is not a directory on the store.</summary>
    private string? ToRelative(string folder) {
        if (string.IsNullOrWhiteSpace(folder)) {
            return null;
        }
        var full = Path.GetFullPath(Path.IsPathRooted(folder) ? folder : Path.Combine(sourceRoot, folder));
        if (full == sourceRoot) {
            return string.Empty;
        }
        if (!full.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !Directory.Exists(full)) {
            return null;
        }
        // The walk never follows a link below a picked folder; a picked folder (or one of its
        // parents) that is itself a link would lead off the store all the same.
        for (var dir = full; dir != sourceRoot; dir = Path.GetDirectoryName(dir)!) {
            if (new DirectoryInfo(dir).LinkTarget is not null) {
                return null;
            }
        }
        return Path.GetRelativePath(sourceRoot, full);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background job boundary: any failure must land in the job status (and the target must still be released), never as an unobserved task exception.")]
    private async Task RunAsync(Job current, CancellationToken ct) {
        var mounted = false;
        // The outcome is published only after the target is released, so no status a client
        // reads as finished ever precedes "safe to unplug".
        (string State, string? Code, string? Detail) outcome = ("failed", "copy_failed", null);
        try {
            // Not cancellable: a token would only stop the WAIT, while the root helper goes on
            // and mounts the drive anyway. Wait it out, then honour a cancel, so a mounted
            // drive always reaches the release in the finally.
            var mount = await devices.MountCopyTargetAsync(current.TargetUuid, CancellationToken.None).ConfigureAwait(false);
            if (!mount.Success || mount.MountPoint is null) {
                outcome = ct.IsCancellationRequested ? ("cancelled", null, null) : ("failed", mount.Code, mount.Detail);
                return;
            }
            mounted = true;
            ct.ThrowIfCancellationRequested();
            var targetRoot = mount.MountPoint;
            var plan = Plan(current, targetRoot, ct);
            var needed = plan.Sum(f => f.Length + PerFileSlackBytes);
            var free = freeBytes(targetRoot);
            if (free is { } available && available < needed) {
                outcome = ("failed", "not_enough_space", $"needs {needed} bytes, the drive has {available} free");
                return;
            }
            lock (gate) {
                current.State = "copying";
            }
            foreach (var file in plan) {
                ct.ThrowIfCancellationRequested();
                await CopyOneAsync(current, file, ct).ConfigureAwait(false);
            }
            outcome = ("done", null, null);
        } catch (OperationCanceledException) {
            outcome = ("cancelled", null, null);
        } catch (Exception ex) {
            LogFailed(logger, ex);
            outcome = ("failed", "copy_failed", ex.Message);
        } finally {
            var released = false;
            string? releaseError = null;
            if (mounted) {
                var release = await devices.ReleaseCopyTargetAsync(CancellationToken.None).ConfigureAwait(false);
                released = release.Success;
                releaseError = release.Success ? null : release.Detail ?? release.Code;
            }
            lock (gate) {
                current.TargetReleased = released;
                current.ErrorCode = outcome.Code ?? (releaseError is null ? null : "release_failed");
                current.ErrorDetail = outcome.Code is null ? releaseError : outcome.Detail;
                current.CurrentFile = null;
                current.FinishedUtc = DateTimeOffset.UtcNow;
                current.State = outcome.State;
            }
            LogFinished(logger, current.State, current.FilesCopied, current.FilesSkipped,
                current.FilesConflicting, current.FilesFailed);
        }
    }

    internal sealed record PlannedFile(string Source, string Destination, string Relative, long Length);

    /// <summary>Walks the chosen folders (or the whole store), classifies every file
    /// against the target and returns the ones to copy. Overlapping picks count once.</summary>
    private List<PlannedFile> Plan(Job current, string targetRoot, CancellationToken ct) {
        IEnumerable<string> roots = current.Folders.Count == 0
            ? [sourceRoot]
            : current.Folders.Select(f => Path.Combine(sourceRoot, f));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var plan = new List<PlannedFile>();
        int skipped = 0, conflicting = 0;
        foreach (var root in roots) {
            foreach (var file in Walk(new DirectoryInfo(root), ct)) {
                var rel = Path.GetRelativePath(sourceRoot, file.FullName);
                if (!seen.Add(rel)) {
                    continue;
                }
                var dest = Path.Combine(targetRoot, rel);
                var existing = new FileInfo(dest);
                if (existing.Exists) {
                    if (existing.Length == file.Length) {
                        skipped++;
                    } else {
                        conflicting++;
                    }
                    continue;
                }
                plan.Add(new PlannedFile(file.FullName, dest, rel, file.Length));
            }
        }
        lock (gate) {
            current.FilesToCopy = plan.Count;
            current.BytesToCopy = plan.Sum(f => f.Length);
            current.FilesSkipped = skipped;
            current.FilesConflicting = conflicting;
        }
        return plan;
    }

    /// <summary>Files under <paramref name="dir"/>, depth first. Symlinks are never
    /// followed (a link loop or a link off the drive is not part of the store).</summary>
    private static IEnumerable<FileInfo> Walk(DirectoryInfo dir, CancellationToken ct) {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        var pending = new Stack<DirectoryInfo>();
        pending.Push(dir);
        while (pending.Count > 0) {
            ct.ThrowIfCancellationRequested();
            var current = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try {
                entries = current.EnumerateFileSystemInfos("*", options).ToList();
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                continue;
            }
            foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.Ordinal)) {
                if (entry.Name.StartsWith('.') || entry.LinkTarget is not null) {
                    continue;
                }
                if (entry is DirectoryInfo sub) {
                    if (!SkippedDirectories.Contains(sub.Name, StringComparer.OrdinalIgnoreCase)) {
                        pending.Push(sub);
                    }
                } else if (entry is FileInfo file && IsCopied(file.Name)) {
                    yield return file;
                }
            }
        }
    }

    /// <summary>In-flight writes and regenerable preview caches stay behind.</summary>
    internal static bool IsCopied(string name) =>
        !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        && !PreviewCacheMaintenance.IsCacheSidecar(name);

    private async Task CopyOneAsync(Job current, PlannedFile file, CancellationToken ct) {
        lock (gate) {
            current.CurrentFile = file.Relative;
        }
        var temp = file.Destination + TempSuffix;
        long written = 0;
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Destination)!);
            var source = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (source.ConfigureAwait(false)) {
                var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                    1 << 20, FileOptions.Asynchronous);
                await using (target.ConfigureAwait(false)) {
                    var buffer = new byte[1 << 20];
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) {
                        await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        written += read;
                        lock (gate) {
                            current.BytesCopied += read;
                        }
                    }
                    // fsync before the rename: FlushAsync only drains the stream's buffer,
                    // so the frame could still sit in the page cache when the drive is pulled.
                    await Task.Run(() => target.Flush(flushToDisk: true), ct).ConfigureAwait(false);
                }
            }
            // No overwrite: a file that appeared under this name meanwhile is not ours.
            File.Move(temp, file.Destination, overwrite: false);
            File.SetLastWriteTimeUtc(file.Destination, File.GetLastWriteTimeUtc(file.Source));
            lock (gate) {
                current.FilesCopied++;
            }
        } catch (OperationCanceledException) {
            TryDelete(temp);
            throw;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            TryDelete(temp);
            LogFileFailed(logger, file.Relative, ex.Message);
            lock (gate) {
                // Progress counts what landed: a file that failed partway is not on the drive.
                current.BytesCopied -= written;
                current.FilesFailed++;
                current.FirstFailure ??= $"{file.Relative}: {ex.Message}";
            }
        }
    }

    private static void TryDelete(string path) {
        try {
            File.Delete(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // A temp file left behind is skipped by the next copy's walk (*.tmp).
        }
    }

    private static long? FreeBytes(string path) {
        try {
            return new DriveInfo(path).AvailableFreeSpace;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
            return null;
        }
    }

    private sealed class Job(string targetUuid, IReadOnlyList<string> folders) {
        public string TargetUuid { get; } = targetUuid;
        public IReadOnlyList<string> Folders { get; } = folders;
        public string State { get; set; } = "preparing";
        public int FilesToCopy { get; set; }
        public int FilesCopied { get; set; }
        public int FilesSkipped { get; set; }
        public int FilesConflicting { get; set; }
        public int FilesFailed { get; set; }
        public long BytesToCopy { get; set; }
        public long BytesCopied { get; set; }
        public string? CurrentFile { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorDetail { get; set; }
        public string? FirstFailure { get; set; }
        public bool TargetReleased { get; set; }
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedUtc { get; set; }

        public StorageCopyStatusDto Snapshot() => new(State, TargetUuid, Folders, FilesToCopy, FilesCopied,
            FilesSkipped, FilesConflicting, FilesFailed, BytesToCopy, BytesCopied, CurrentFile, ErrorCode,
            ErrorDetail, FirstFailure, TargetReleased, StartedUtc, FinishedUtc);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Disk-to-disk copy to {Uuid} started: {What}.")]
    private static partial void LogStarted(ILogger logger, string uuid, string what);

    [LoggerMessage(Level = LogLevel.Information, Message = "Disk-to-disk copy {State}: {Copied} copied, {Skipped} already there, {Conflicting} conflicting, {Failed} failed.")]
    private static partial void LogFinished(ILogger logger, string state, int copied, int skipped, int conflicting, int failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Disk-to-disk copy: {File} failed: {Reason}.")]
    private static partial void LogFileFailed(ILogger logger, string file, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Disk-to-disk copy failed.")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
