#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1231 — store detection after #1226: the mountinfo probe (device number, bind root,
    /// detached disk), and every consumer routed through it (lsblk's IsAraStore, the disk-space
    /// numbers, the pre-capture gate, the watcher tick).</summary>
    [TestFixture]
    public class StoreDetectionFollowUpsTest {

        // ── /proc/self/mountinfo ──
        // id parent major:minor root mountpoint options [optional…] - fstype source superopts

        private const string RootFs =
            "22 1 254:1 / / rw,relatime shared:1 - ext4 /dev/vda1 rw,errors=remount-ro\n" +
            "23 22 0:21 / /sys sysfs rw,nosuid,nodev,noexec,relatime shared:2 - sysfs sysfs rw\n" +
            "24 22 0:5 / /proc proc rw,nosuid,nodev,noexec,relatime shared:3 - proc proc rw\n" +
            "25 22 0:24 / /run tmpfs rw,nosuid,nodev,noexec,relatime shared:4 - tmpfs tmpfs rw,size=200000k,mode=755\n";

        private static readonly string[] BothStores = { "/dev/mmcblk0p2", "/dev/sda1" };
        private static readonly string[] TwoBlocks = { "mmcblk0", "sda" };
        private static readonly string[] OneBlock = { "mmcblk0" };

        private static bool Mounted(string info, Func<string, bool?>? alive = null) =>
            StoreMountProbe.IsStoreMountedFromMountInfo(info.Split('\n'), deviceExists: alive);

        [Test]
        public void Mountinfo_lines_parse_with_octal_escapes_and_the_dash_separator() {
            var e = StoreMountProbe.ParseMountInfo(
                "99 22 8:1 / /media/my\\040disk rw,relatime shared:50 master:3 - exfat /dev/sda1 rw,uid=999");
            Assert.That(e, Is.Not.Null);
            Assert.That(e!.Value.MajorMinor, Is.EqualTo("8:1"));
            Assert.That(e.Value.Root, Is.EqualTo("/"));
            Assert.That(e.Value.MountPoint, Is.EqualTo("/media/my disk"));
            Assert.That(e.Value.Source, Is.EqualTo("/dev/sda1"));
            Assert.That(StoreMountProbe.ParseMountInfo("garbage"), Is.Null);
        }

        [Test]
        public void No_entry_at_the_store_path_is_ejected() {
            Assert.That(Mounted(RootFs), Is.False);
        }

        [Test]
        public void The_systemd_self_bind_of_the_empty_directory_is_ejected() {
            // Same device as "/", root = the store path inside the root filesystem: what
            // ReadWritePaths= produces after a restart with no disk (#1207).
            var info = RootFs + "40 22 254:1 /media/openastroara /media/openastroara rw,relatime shared:1 - ext4 /dev/vda1 rw,errors=remount-ro\n";
            Assert.That(Mounted(info), Is.False);
        }

        [Test]
        public void A_self_bind_under_a_separate_media_filesystem_is_ejected() {
            var info = RootFs
                + "30 22 254:2 / /media rw,relatime shared:5 - ext4 /dev/vda2 rw\n"
                + "41 30 254:2 /openastroara /media/openastroara rw,relatime shared:5 - ext4 /dev/vda2 rw\n";
            Assert.That(Mounted(info), Is.False, "root is the store path RELATIVE to /media: still the empty directory");
        }

        [Test]
        public void A_bind_of_another_directory_on_the_root_disk_is_a_store() {
            // `mount --bind /srv/frames /media/openastroara`: same device, but root points at a
            // real directory someone chose. The /proc/self/mounts rule called this ejected (#1231).
            var info = RootFs + "42 22 254:1 /srv/frames /media/openastroara rw,relatime shared:1 - ext4 /dev/vda1 rw\n";
            Assert.That(Mounted(info), Is.True);
            // The same two mounts as /proc/self/mounts sees them: one device name at both paths.
            const string mountsView =
                "/dev/vda1 / ext4 rw,relatime,errors=remount-ro 0 0\n" +
                "/dev/vda1 /media/openastroara ext4 rw,relatime 0 0\n";
            Assert.That(StoreMountProbe.IsStoreMounted(mountsView.Split('\n')),
                Is.False, "the mounts-table fallback cannot tell this bind from the self-bind — mountinfo can");
        }

        [Test]
        public void A_usb_disk_or_a_btrfs_subvolume_is_a_store() {
            var usb = RootFs + "43 22 8:1 / /media/openastroara rw,noatime shared:60 - exfat /dev/sda1 rw,uid=999\n";
            Assert.That(Mounted(usb, _ => true), Is.True);
            // A btrfs subvolume of the root device gets an anonymous device number of its own
            // (0:nn), which /sys/dev/block never lists: unknown liveness reads as alive.
            var subvol = RootFs + "44 22 0:45 /@frames /media/openastroara rw,relatime shared:61 - btrfs /dev/vda1 rw,subvol=/@frames\n";
            Assert.That(Mounted(subvol, StoreMountProbe.DeviceExists), Is.True);
        }

        [Test]
        public void A_disk_unplugged_under_a_standing_bind_is_ejected() {
            // The namespace-private bind outlives the device: the table still says 8:1 at the
            // store path, /sys/dev/block/8:1 is gone. Writes would land on a detached filesystem.
            var info = RootFs + "43 22 8:1 / /media/openastroara rw,noatime shared:60 - exfat /dev/sda1 rw,uid=999\n";
            Assert.That(Mounted(info, mm => mm == "8:1" ? false : null), Is.False);
            Assert.That(Mounted(info, _ => null), Is.True, "unknown liveness is not a reason to refuse frames");
        }

        [Test]
        public void A_disk_replugged_over_the_stale_bind_is_a_store() {
            var info = RootFs
                + "40 22 254:1 /media/openastroara /media/openastroara rw,relatime shared:1 - ext4 /dev/vda1 rw\n"
                + "45 22 8:1 / /media/openastroara rw,noatime shared:62 - exfat /dev/sda1 rw\n";
            Assert.That(Mounted(info, _ => true), Is.True, "the last entry at the path is the one in effect");
        }

        [Test]
        public void Anonymous_devices_have_unknown_liveness() {
            Assert.That(StoreMountProbe.DeviceExists("0:45"), Is.Null);
        }

        // ── lsblk: IsAraStore through the probe ──

        private const string LsblkRootAtStorePath = """
            {"blockdevices": [
              {"name":"mmcblk0","path":"/dev/mmcblk0","type":"disk","size":31914983424,"model":"SD32G","rm":false,"children":[
                {"name":"mmcblk0p1","path":"/dev/mmcblk0p1","type":"part","size":536870912,"mountpoint":"/boot/firmware","fstype":"vfat","pkname":"mmcblk0"},
                {"name":"mmcblk0p2","path":"/dev/mmcblk0p2","type":"part","size":31378112512,"mountpoint":"/media/openastroara","fstype":"ext4","pkname":"mmcblk0"}
              ]},
              {"name":"sda","path":"/dev/sda","type":"disk","size":1000204886016,"model":"Portable SSD","rm":true,"tran":"usb","children":[
                {"name":"sda1","path":"/dev/sda1","type":"part","size":1000202043392,"mountpoint":"/media/openastroara","fstype":"exfat","pkname":"sda","label":"ARA"}
              ]}
            ]}
            """;

        [Test]
        public void Lsblk_rows_at_the_store_path_are_the_store_only_when_the_probe_says_mounted() {
            var systemDisks = new HashSet<string>(StringComparer.Ordinal) { "/dev/mmcblk0p2", "/dev/mmcblk0" };

            var ejected = StorageDeviceService.ParseDevices(LsblkRootAtStorePath, systemDisks, storeMounted: false);
            Assert.That(ejected.Select(d => d.IsAraStore), Is.All.False, "lsblk shows the self-bind at the store path; nothing is the store");
            Assert.That(ejected.Single(d => d.Path == "/dev/mmcblk0p2").IsSystemDisk, Is.True);

            var mounted = StorageDeviceService.ParseDevices(LsblkRootAtStorePath, systemDisks, storeMounted: true);
            Assert.That(mounted.Where(d => d.IsAraStore).Select(d => d.Path), Is.EquivalentTo(BothStores),
                "with a disk mounted, lsblk's mountpoint column is believed (the stale bind line only ever shows one of them live)");
        }

        [Test]
        [Platform("Linux")] // ListAsync is Linux-only by design (lsblk/findmnt); the parser is tested above on every OS
        public async Task ListAsync_routes_is_ara_store_through_the_probe() {
            // The real entry point with canned lsblk/findmnt answers: proves the seam is WIRED, not just
            // that ParseDevices honours its flag (the bot's two mutations: `true` undoes the fix,
            // `== true` makes nothing the store when the probe is unknown).
            Task<(int, string)> Run(string file, string[] args, CancellationToken ct) => Task.FromResult(
                (file, args.Length > 0 ? args[0] : "") switch {
                    ("lsblk", "-J") => (0, LsblkRootAtStorePath),
                    ("findmnt", _) => args[^1] == "/" ? (0, "/dev/mmcblk0p2\n") : (1, ""),
                    ("lsblk", "-no") => args[^1] == "/dev/mmcblk0p2" ? (0, "mmcblk0\n") : (0, ""),
                    _ => (1, ""),
                });
            var paths = new StorageHelperPaths("/nonexistent/helper", "/nonexistent/unit", Path.Combine(Path.GetTempPath(), $"oara-list-{Guid.NewGuid():N}"));
            var svc = new StorageDeviceService(Microsoft.Extensions.Logging.Abstractions.NullLogger<StorageDeviceService>.Instance, paths, Run);

            svc.StoreMounted = () => false;
            Assert.That((await svc.ListAsync(CancellationToken.None)).Select(d => d.IsAraStore), Is.All.False, "ejected: nothing is the store");

            svc.StoreMounted = () => null;
            Assert.That((await svc.ListAsync(CancellationToken.None)).Where(d => d.IsAraStore).Select(d => d.Path), Is.EquivalentTo(BothStores), "unknown: lsblk's column stands");

            svc.StoreMounted = () => true;
            Assert.That((await svc.ListAsync(CancellationToken.None)).Where(d => d.IsAraStore).Select(d => d.Path), Is.EquivalentTo(BothStores));
        }

        // ── disk space: an ejected store has no numbers ──

        [Test]
        [Platform("Linux,MacOsX")] // POSIX paths: on Windows GetFullPath turns the store path into C:\media\…
        public void Disk_space_under_an_ejected_store_is_not_resolvable() {
            Assert.That(DiskSpaceMonitor.StoreEjectedFor("/media/openastroara/frames", () => false), Is.True);
            Assert.That(DiskSpaceMonitor.StoreEjectedFor("/media/openastroara", () => false), Is.True);
            Assert.That(DiskSpaceMonitor.StoreEjectedFor("/media/openastroara-old/frames", () => false), Is.False, "a sibling path is not the store");
            Assert.That(DiskSpaceMonitor.StoreEjectedFor("/media/openastroara/frames", () => true), Is.False);
            Assert.That(DiskSpaceMonitor.StoreEjectedFor("/media/openastroara/frames", () => null), Is.False, "unknown trusts DriveInfo");
            Assert.That(DiskSpaceMonitor.StoreEjectedFor("/home/pi/frames", () => false), Is.False);

            Assert.That(DiskSpaceMonitor.TryGetFreeBytes("/media/openastroara/frames", () => false), Is.Null,
                "not the root disk's free space dressed up as the store's");
            Assert.That(DiskSpaceMonitor.TryGetSpace("/media/openastroara/frames", () => false), Is.Null);
        }

        // ── the two wiring sites ──

        [Test]
        public void Pre_capture_gate_reads_an_unknown_probe_as_not_ejected() {
            var store = new InMemoryProfileStore(); // SaveDirectory = /media/openastroara
            using var camera = new CameraService(profileStore: store) { StoreMountedProbe = () => null };
            Assert.That(camera.StoreEjected(out _), Is.False, "a probe fault must never cost a frame");
            camera.StoreMountedProbe = () => false;
            Assert.That(camera.StoreEjected(out var dir), Is.True);
            Assert.That(dir, Is.EqualTo("/media/openastroara"));
            camera.StoreMountedProbe = () => true;
            Assert.That(camera.StoreEjected(out _), Is.False);
        }

        [Test]
        public void Pre_capture_gate_ignores_a_save_directory_off_the_store() {
            var store = new InMemoryProfileStore();
            store.PutStorageSettings(store.GetStorageSettings() with { SaveDirectory = "/home/pi/frames" });
            using var camera = new CameraService(profileStore: store) { StoreMountedProbe = () => false };
            Assert.That(camera.StoreEjected(out _), Is.False);
        }

        [Test]
        public void Watcher_skips_the_tick_when_the_probe_cannot_answer() {
            Assert.That(StorageDeviceWatcher.BuildSnapshot(TwoBlocks, null), Is.Null);
            Assert.That(StorageDeviceWatcher.BuildSnapshot(TwoBlocks, true), Is.EqualTo("mmcblk0,sda|store=1"));
            Assert.That(StorageDeviceWatcher.BuildSnapshot(OneBlock, false), Is.EqualTo("mmcblk0|store=0"));
        }
    }
}
