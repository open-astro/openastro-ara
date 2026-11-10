#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test;

/// <summary>Disk-to-disk copy from the store onto a second drive: what is copied, what
/// is skipped, and that the target is always released. The helper is faked; the store
/// and the target are temp directories.</summary>
[Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
public class StorageCopyServiceTest {
    private string store = null!;
    private string target = null!;
    private Mock<IStorageDeviceService> devices = null!;
    private Func<string, long?> freeBytes = _ => null;
    private bool? storeMounted = true;
    private StorageCopyService? service;
    private static readonly string[] PickedFolders = ["manual/M31", "manual/M31/flats"];

    [SetUp]
    public void SetUp() {
        var root = Path.Combine(Path.GetTempPath(), "ara-copy-" + Path.GetRandomFileName());
        store = Path.Combine(root, "store");
        target = Path.Combine(root, "target");
        Directory.CreateDirectory(store);
        Directory.CreateDirectory(target);
        devices = new Mock<IStorageDeviceService>();
        devices.Setup(d => d.MountCopyTargetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageConfigureResult(true, "ok", null, target));
        devices.Setup(d => d.ReleaseCopyTargetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageConfigureResult(true, "released", null, null));
        freeBytes = _ => null;
        storeMounted = true;
    }

    [TearDown]
    public void TearDown() {
        service?.Dispose();
        service = null;
        try { Directory.Delete(Path.GetDirectoryName(store)!, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private StorageCopyService Svc =>
        service ??= new(devices.Object, NullLogger.Instance, store, () => storeMounted, p => freeBytes(p));

    private void Put(string relative, string content = "frame") {
        var path = Path.Combine(store, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static async Task<Server.Contracts.StorageCopyStatusDto> RunToEnd(StorageCopyService svc,
            string uuid = "6EF6-FD21", params string[] folders) {
        Assert.That(svc.Start(uuid, folders), Is.Null, "the copy starts");
        await svc.Running!.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        return svc.Status();
    }

    [Test]
    public async Task The_whole_store_is_mirrored_onto_the_target_and_the_target_is_released() {
        Put("manual/M31/2026-10-16/Light/a.fits", "aaaa");
        Put("manual/M31/2026-10-16/Light/a.hist.v2.json", "{}");
        Put("manual/M31/2026-10-16/Light/a.thumb.jpg");
        Put("manual/M31/2026-10-16/Light/a.preview.auto_stf.4096.jpg");
        Put("manual/M31/2026-10-16/Light/b.fits.tmp");
        Put(".Spotlight-V100/store.db");
        Put("notes.txt", "mine");

        var status = await RunToEnd(Svc).ConfigureAwait(false);

        Assert.That(status.State, Is.EqualTo("done"), status.ErrorDetail);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(target, "manual/M31/2026-10-16/Light/a.fits")).ConfigureAwait(false),
            Is.EqualTo("aaaa"));
        Assert.That(File.Exists(Path.Combine(target, "manual/M31/2026-10-16/Light/a.hist.v2.json")), Is.True);
        Assert.That(File.Exists(Path.Combine(target, "notes.txt")), Is.True, "everything on the store, not just frames");
        Assert.That(File.Exists(Path.Combine(target, "manual/M31/2026-10-16/Light/a.thumb.jpg")), Is.False, "preview caches regenerate");
        Assert.That(File.Exists(Path.Combine(target, "manual/M31/2026-10-16/Light/a.preview.auto_stf.4096.jpg")), Is.False);
        Assert.That(File.Exists(Path.Combine(target, "manual/M31/2026-10-16/Light/b.fits.tmp")), Is.False, "in-flight writes stay behind");
        Assert.That(Directory.Exists(Path.Combine(target, ".Spotlight-V100")), Is.False, "OS metadata stays behind");
        Assert.That(Directory.EnumerateFiles(target, "*" + StorageCopyService.TempSuffix, SearchOption.AllDirectories), Is.Empty);
        Assert.That(status.FilesCopied, Is.EqualTo(3));
        Assert.That(status.FilesToCopy, Is.EqualTo(3));
        Assert.That(status.BytesCopied, Is.EqualTo(status.BytesToCopy));
        Assert.That(status.TargetReleased, Is.True, "safe to unplug");
        devices.Verify(d => d.ReleaseCopyTargetAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private void PutAt(string relative, DateTime modifiedUtc) {
        Put(relative);
        File.SetLastWriteTimeUtc(Path.Combine(store, relative), modifiedUtc);
    }

    [Test]
    public void Contents_count_the_frames_and_date_the_newest() {
        PutAt("night/Light/a.fits", new DateTime(2026, 10, 1, 22, 0, 0, DateTimeKind.Utc));
        PutAt("night/Light/b.xisf", new DateTime(2026, 10, 3, 23, 30, 0, DateTimeKind.Utc));
        PutAt("night/Light/c.fits.tmp", new DateTime(2026, 10, 9, 1, 0, 0, DateTimeKind.Utc));
        PutAt(".Trashes/d.fits", new DateTime(2026, 10, 9, 1, 0, 0, DateTimeKind.Utc));
        Put("notes.txt", "mine");

        var contents = StorageCopyService.Summarize(store);

        Assert.That(contents.Readable, Is.True);
        Assert.That(contents.Frames, Is.EqualTo(2), "frames only: no in-flight writes, OS metadata or other files");
        Assert.That(contents.NewestFrameUtc, Is.EqualTo(new DateTimeOffset(2026, 10, 3, 23, 30, 0, TimeSpan.Zero)));
        Assert.That(contents.FramesNotCopied, Is.EqualTo(2), "no copy has run from this drive");
    }

    [Test]
    public async Task A_clean_copy_of_the_whole_store_counts_its_frames_as_copied() {
        PutAt("night/a.fits", DateTime.UtcNow.AddHours(-2));

        var status = await RunToEnd(Svc).ConfigureAwait(false);
        Assert.That(status.State, Is.EqualTo("done"), status.ErrorDetail);
        Assert.That(StorageCopyService.Summarize(store).FramesNotCopied, Is.Zero);

        PutAt("night/b.fits", DateTime.UtcNow.AddHours(1));
        Assert.That(StorageCopyService.Summarize(store).FramesNotCopied, Is.EqualTo(1), "a frame saved after the copy");
    }

    [Test]
    public async Task A_copy_to_a_drive_erased_since_counts_its_frames_as_not_copied_again() {
        PutAt("night/a.fits", DateTime.UtcNow.AddHours(-2));
        var erased = new StorageEraseLog(Path.GetDirectoryName(store)!);

        var status = await RunToEnd(Svc, "6EF6-FD21").ConfigureAwait(false);
        Assert.That(status.State, Is.EqualTo("done"), status.ErrorDetail);
        Assert.That(StorageCopyService.Summarize(store, erased: erased).FramesNotCopied, Is.Zero, "drive A untouched");

        erased.Record("6ef6-fd21", DateTime.UtcNow);

        Assert.That(StorageCopyService.Summarize(store, erased: erased).FramesNotCopied, Is.EqualTo(1),
            "the only copy was on the drive that was erased");
    }

    [Test]
    public async Task An_erase_log_that_cannot_be_read_counts_every_frame_as_not_copied() {
        PutAt("night/a.fits", DateTime.UtcNow.AddHours(-2));
        var stateDir = Path.GetDirectoryName(store)!;
        var status = await RunToEnd(Svc, "6EF6-FD21").ConfigureAwait(false);
        Assert.That(status.State, Is.EqualTo("done"), status.ErrorDetail);
        // A directory where the log file should be: File.ReadAllLines throws.
        Directory.CreateDirectory(Path.Combine(stateDir, StorageEraseLog.FileName));

        Assert.That(StorageCopyService.Summarize(store, erased: new StorageEraseLog(stateDir)).FramesNotCopied, Is.EqualTo(1));
    }

    [Test]
    public void A_marker_from_before_it_named_its_drive_counts_nothing_as_copied() {
        PutAt("night/a.fits", DateTime.UtcNow.AddHours(-2));
        var marker = Path.Combine(store, StorageCopyService.LastCopyMarker);
        File.WriteAllBytes(marker, []);
        File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);

        Assert.That(StorageCopyService.Summarize(store).FramesNotCopied, Is.EqualTo(1));
    }

    [Test]
    public async Task A_frame_saved_while_the_copy_runs_still_counts_as_not_copied() {
        PutAt("night/a.fits", DateTime.UtcNow.AddHours(-2));
        // freeBytes runs after the walk and before the files go across: a plan saving
        // a frame then is what the copy never sees.
        freeBytes = _ => {
            PutAt("night/late.fits", DateTime.UtcNow);
            return null;
        };

        var status = await RunToEnd(Svc).ConfigureAwait(false);

        Assert.That(status.State, Is.EqualTo("done"), status.ErrorDetail);
        Assert.That(File.Exists(Path.Combine(target, "night/late.fits")), Is.False, "not in the plan");
        Assert.That(StorageCopyService.Summarize(store).FramesNotCopied, Is.EqualTo(1), "late.fits is on no other drive");
    }

    [Test]
    public async Task A_copy_of_chosen_folders_leaves_the_store_not_copied() {
        PutAt("manual/M31/a.fits", DateTime.UtcNow.AddHours(-2));
        PutAt("manual/M42/b.fits", DateTime.UtcNow.AddHours(-2));

        var status = await RunToEnd(Svc, "6EF6-FD21", "manual/M31").ConfigureAwait(false);

        Assert.That(status.State, Is.EqualTo("done"), status.ErrorDetail);
        Assert.That(StorageCopyService.Summarize(store).FramesNotCopied, Is.EqualTo(2));
    }

    [Test]
    public void A_drive_that_is_not_mounted_cannot_be_read() {
        var unmounted = new Server.Contracts.StorageDeviceDto("/dev/sdb1", "6EF6-FD21", "ARA-DISK", null, "exfat",
            1L << 30, null, true, "usb", false, false);

        Assert.That(StorageCopyService.ContentsOf(unmounted, new StorageEraseLog(Path.GetDirectoryName(store)!)).Readable, Is.False);
        Assert.That(StorageCopyService.ContentsOf(null, new StorageEraseLog(Path.GetDirectoryName(store)!)).Readable, Is.False);
        Assert.That(StorageCopyService.ContentsOf(unmounted with { MountPoint = store }, new StorageEraseLog(Path.GetDirectoryName(store)!)).Readable, Is.True);
    }

    [Test]
    public async Task A_second_copy_skips_what_is_there_and_never_overwrites_a_different_file() {
        Put("night/a.fits", "aaaa");
        Put("night/b.fits", "bbbb");
        Directory.CreateDirectory(Path.Combine(target, "night"));
        await File.WriteAllTextAsync(Path.Combine(target, "night/a.fits"), "aaaa").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(target, "night/b.fits"), "someone else's").ConfigureAwait(false);

        var status = await RunToEnd(Svc).ConfigureAwait(false);

        Assert.That(status.State, Is.EqualTo("done"));
        Assert.That(status.FilesSkipped, Is.EqualTo(1));
        Assert.That(status.FilesConflicting, Is.EqualTo(1));
        Assert.That(status.FilesCopied, Is.Zero);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(target, "night/b.fits")).ConfigureAwait(false),
            Is.EqualTo("someone else's"));
    }

    [Test]
    public async Task Picked_folders_copy_only_those_and_an_overlap_counts_once() {
        Put("manual/M31/a.fits");
        Put("manual/M31/flats/f.fits");
        Put("manual/M42/b.fits");

        var status = await RunToEnd(Svc, "6EF6-FD21", "manual/M31", Path.Combine(store, "manual/M31/flats"))
            .ConfigureAwait(false);

        Assert.That(status.State, Is.EqualTo("done"));
        Assert.That(status.FilesCopied, Is.EqualTo(2), "flats sits inside M31: copied once");
        Assert.That(status.Folders, Is.EqualTo(PickedFolders), "absolute picks become store-relative");
        Assert.That(File.Exists(Path.Combine(target, "manual/M42/b.fits")), Is.False);
    }

    [Test]
    public async Task The_store_root_with_a_trailing_slash_is_the_whole_store() {
        Put("night/a.fits");
        var status = await RunToEnd(Svc, "6EF6-FD21", store + Path.DirectorySeparatorChar).ConfigureAwait(false);
        Assert.That(status.State, Is.EqualTo("done"));
        Assert.That(status.Folders, Is.Empty, "reads as the whole store, not \".\"");
        Assert.That(status.FilesCopied, Is.EqualTo(1));
    }

    [Test]
    public void A_folder_off_the_store_is_refused() {
        Put("night/a.fits");
        var svc = Svc;
        foreach (var bad in new[] { "../etc", "/etc", "no-such-folder", " " }) {
            var refused = svc.Start("6EF6-FD21", [bad]);
            Assert.That(refused?.Code, Is.EqualTo("bad_folder"), bad);
        }
        devices.Verify(d => d.MountCopyTargetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public void No_store_is_refused() {
        storeMounted = false;
        Assert.That(Svc.Start("6EF6-FD21", null)?.Code, Is.EqualTo("no_store"));
    }

    [Test]
    public async Task A_target_without_room_fails_before_copying_and_is_released() {
        Put("night/a.fits", new string('x', 4096));
        freeBytes = _ => 1024;

        var status = await RunToEnd(Svc).ConfigureAwait(false);

        Assert.That(status.State, Is.EqualTo("failed"));
        Assert.That(status.ErrorCode, Is.EqualTo("not_enough_space"));
        Assert.That(Directory.EnumerateFileSystemEntries(target), Is.Empty);
        Assert.That(status.TargetReleased, Is.True);
    }

    [Test]
    public async Task A_target_that_will_not_mount_reports_the_helper_code_and_releases_nothing() {
        devices.Setup(d => d.MountCopyTargetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageConfigureResult(false, "refused", "is_store", null));
        Put("night/a.fits");

        var status = await RunToEnd(Svc).ConfigureAwait(false);

        Assert.That(status.State, Is.EqualTo("failed"));
        Assert.That(status.ErrorCode, Is.EqualTo("refused"));
        Assert.That(status.ErrorDetail, Is.EqualTo("is_store"));
        devices.Verify(d => d.ReleaseCopyTargetAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task A_second_start_while_one_runs_is_refused_and_cancel_ends_it() {
        var mountStarted = new TaskCompletionSource();
        var mountDone = new TaskCompletionSource<StorageConfigureResult>();
        devices.Setup(d => d.MountCopyTargetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => { mountStarted.TrySetResult(); return mountDone.Task; });
        Put("night/a.fits");
        var svc = Svc;

        Assert.That(svc.Start("6EF6-FD21", null), Is.Null);
        await mountStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.That(svc.IsRunning, Is.True);
        Assert.That(svc.Start("6EF6-FD21", null)?.Code, Is.EqualTo("copy_running"));
        Assert.That(svc.Cancel(), Is.True);
        mountDone.SetResult(new StorageConfigureResult(false, "mount_failed", null, null));
        await svc.Running!.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        Assert.That(svc.Status().State, Is.EqualTo("cancelled"));
        Assert.That(svc.IsRunning, Is.False);
        Assert.That(svc.Cancel(), Is.False, "nothing left to cancel");
    }

    [Test]
    public async Task A_cancel_while_the_target_mounts_still_releases_it() {
        // The root helper finishes its mount whatever the daemon's token says: a cancel that
        // only stops the wait would leave the drive mounted with nothing reporting it.
        var mountStarted = new TaskCompletionSource();
        var mountDone = new TaskCompletionSource<StorageConfigureResult>();
        // Like StorageDeviceService: the token cancels the WAIT for the helper, not the helper.
        devices.Setup(d => d.MountCopyTargetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) => {
                mountStarted.TrySetResult();
                return await mountDone.Task.WaitAsync(ct).ConfigureAwait(false);
            });
        Put("night/a.fits");

        Assert.That(Svc.Start("6EF6-FD21", null), Is.Null);
        await mountStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.That(Svc.Cancel(), Is.True);
        await Task.Delay(100).ConfigureAwait(false); // the cancelled wait unwinds before the helper finishes
        mountDone.SetResult(new StorageConfigureResult(true, "ok", null, target));
        await Svc.Running!.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        var status = Svc.Status();
        Assert.That(status.State, Is.EqualTo("cancelled"));
        Assert.That(status.FilesCopied, Is.Zero);
        Assert.That(status.TargetReleased, Is.True, "safe to unplug");
        devices.Verify(d => d.ReleaseCopyTargetAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    [Platform(Exclude = "Win")]
    public void A_picked_folder_that_is_a_symlink_off_the_store_is_refused() {
        var outside = Path.Combine(Path.GetDirectoryName(store)!, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(store, "night"), outside);
        Put("real/a.fits");
        Directory.CreateSymbolicLink(Path.Combine(store, "real", "nested-link"), outside);

        Assert.That(Svc.Start("6EF6-FD21", ["night"])?.Code, Is.EqualTo("bad_folder"));
        Assert.That(Svc.Start("6EF6-FD21", ["real/nested-link"])?.Code, Is.EqualTo("bad_folder"));
    }

    [Test]
    public async Task Disposing_waits_for_the_target_to_be_released() {
        var mountStarted = new TaskCompletionSource();
        devices.Setup(d => d.MountCopyTargetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () => {
                mountStarted.TrySetResult();
                await Task.Delay(200).ConfigureAwait(false);
                return new StorageConfigureResult(true, "ok", null, target);
            });
        Put("night/a.fits");

        Assert.That(Svc.Start("6EF6-FD21", null), Is.Null);
        await mountStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await Svc.DisposeAsync().ConfigureAwait(false);
        service = null;

        devices.Verify(d => d.ReleaseCopyTargetAsync(It.IsAny<CancellationToken>()), Times.Once,
            "daemon shutdown leaves no drive mounted");
    }

    [Test]
    public async Task A_cancel_after_the_target_is_mounted_still_releases_it() {
        Put("night/a.fits");
        freeBytes = _ => { Svc.Cancel(); return null; };

        var status = await RunToEnd(Svc).ConfigureAwait(false);

        Assert.That(status.State, Is.EqualTo("cancelled"));
        Assert.That(status.FilesCopied, Is.Zero);
        Assert.That(status.TargetReleased, Is.True);
    }

    [Test]
    public async Task A_target_that_cannot_be_released_says_so() {
        devices.Setup(d => d.ReleaseCopyTargetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageConfigureResult(false, "device_busy", "copy_target", null));
        Put("night/a.fits");

        var status = await RunToEnd(Svc).ConfigureAwait(false);

        Assert.That(status.State, Is.EqualTo("done"), "the files are copied");
        Assert.That(status.TargetReleased, Is.False, "but the drive is not safe to unplug yet");
        Assert.That(status.ErrorCode, Is.EqualTo("release_failed"));
    }

    [Test]
    public async Task The_copy_reads_as_running_until_the_target_is_released() {
        // A client stops polling on the first finished state: "done" before the unmount
        // completes would leave it showing a summary without "safe to unplug".
        var releasing = new TaskCompletionSource();
        var release = new TaskCompletionSource<StorageConfigureResult>();
        devices.Setup(d => d.ReleaseCopyTargetAsync(It.IsAny<CancellationToken>()))
            .Returns(() => { releasing.TrySetResult(); return release.Task; });
        Put("night/a.fits");

        Assert.That(Svc.Start("6EF6-FD21", null), Is.Null);
        await releasing.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var during = Svc.Status();
        Assert.That(Svc.IsRunning, Is.True, during.State);
        Assert.That(during.FinishedUtc, Is.Null);

        release.SetResult(new StorageConfigureResult(true, "released", null, null));
        await Svc.Running!.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var after = Svc.Status();
        Assert.That(after.State, Is.EqualTo("done"));
        Assert.That(after.TargetReleased, Is.True);
        Assert.That(after.FinishedUtc, Is.Not.Null);
        Assert.That(after.CurrentFile, Is.Null);
    }

    [Test]
    [Platform(Exclude = "Win")]
    public async Task Symlinks_are_not_followed() {
        Put("night/a.fits");
        var outside = Path.Combine(Path.GetDirectoryName(store)!, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "x").ConfigureAwait(false);
        Directory.CreateSymbolicLink(Path.Combine(store, "link"), outside);

        var status = await RunToEnd(Svc).ConfigureAwait(false);

        Assert.That(status.FilesCopied, Is.EqualTo(1));
        Assert.That(Directory.Exists(Path.Combine(target, "link")), Is.False);
    }

    [Test]
    public void Copied_names_leave_temp_writes_and_preview_caches_behind() {
        Assert.That(StorageCopyService.IsCopied("a.fits"), Is.True);
        Assert.That(StorageCopyService.IsCopied("a.hist.v2.json"), Is.True);
        Assert.That(StorageCopyService.IsCopied("my-own.jpg"), Is.True, "a user's JPEG is theirs");
        Assert.That(StorageCopyService.IsCopied("a.fits.tmp"), Is.False);
        Assert.That(StorageCopyService.IsCopied("a.thumb.jpg"), Is.False);
        Assert.That(StorageCopyService.IsCopied("a.preview.auto_stf.4096.jpg"), Is.False);
    }
}
