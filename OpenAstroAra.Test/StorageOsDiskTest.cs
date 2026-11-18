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
using NUnit.Framework;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1769 — the OS disk in two levels (os_partition, os_disk_data), the media kind, and
    /// the erase guard that refuses either level before the helper is reached.</summary>
    [TestFixture]
    public class StorageOsDiskTest {

        // ASIAIR-shaped: eMMC with a boot, a root and a data partition, plus a USB disk.
        private const string EmmcWithData = """
            {"blockdevices": [
              {"name":"mmcblk0","path":"/dev/mmcblk0","type":"disk","size":64000000000,"model":"eMMC","children":[
                {"name":"mmcblk0p1","path":"/dev/mmcblk0p1","type":"part","size":536870912,"mountpoint":"/boot/firmware","fstype":"vfat","pkname":"mmcblk0"},
                {"name":"mmcblk0p2","path":"/dev/mmcblk0p2","type":"part","size":16000000000,"mountpoint":"/","fstype":"ext4","pkname":"mmcblk0"},
                {"name":"mmcblk0p3","path":"/dev/mmcblk0p3","type":"part","size":40000000000,"uuid":"DA7A-0001","fstype":"exfat","label":"DATA","pkname":"mmcblk0"}
              ]},
              {"name":"sda","path":"/dev/sda","type":"disk","size":1000204886016,"rm":true,"tran":"usb","children":[
                {"name":"sda1","path":"/dev/sda1","type":"part","size":1000202043392,"uuid":"USB1-0001","fstype":"exfat","pkname":"sda"}
              ]}
            ]}
            """;

        private static readonly HashSet<string> EmmcSystem = new(StringComparer.Ordinal) { "/dev/mmcblk0p1", "/dev/mmcblk0p2", "/dev/mmcblk0" };

        private static string? Sysfs(string path) => path switch {
            "/sys/block/mmcblk0/device/type" => "MMC\n",
            "/sys/block/mmcblk1/device/type" => "SD\n",
            _ => null,
        };

        [Test]
        public void An_os_disk_data_partition_is_its_own_level_and_still_a_system_disk() {
            var list = StorageDeviceService.ParseDevices(EmmcWithData, EmmcSystem, storeMounted: true, Sysfs);

            Assert.That(list.Single(d => d.Path == "/dev/mmcblk0p2").SystemDiskLevel, Is.EqualTo("os_partition"));
            var data = list.Single(d => d.Path == "/dev/mmcblk0p3");
            Assert.That(data.SystemDiskLevel, Is.EqualTo("os_disk_data"));
            Assert.That(data.IsSystemDisk, Is.True, "an older client hides exactly what it hides today");
            var usb = list.Single(d => d.Path == "/dev/sda1");
            Assert.That(usb.SystemDiskLevel, Is.EqualTo("none"));
            Assert.That(usb.IsSystemDisk, Is.False);
        }

        [Test]
        public void Root_on_dm_crypt_makes_the_other_partition_of_the_physical_disk_os_disk_data() {
            const string json = """
                {"blockdevices": [
                  {"name":"nvme0n1","path":"/dev/nvme0n1","type":"disk","size":512000000000,"tran":"nvme","children":[
                    {"name":"nvme0n1p1","path":"/dev/nvme0n1p1","type":"part","size":536870912,"mountpoint":"/boot","fstype":"vfat","pkname":"nvme0n1","tran":"nvme"},
                    {"name":"nvme0n1p2","path":"/dev/nvme0n1p2","type":"part","size":100000000000,"fstype":"crypto_LUKS","pkname":"nvme0n1","tran":"nvme","children":[
                      {"name":"root","path":"/dev/mapper/root","type":"crypt","size":100000000000,"mountpoint":"/","fstype":"ext4","pkname":"nvme0n1p2"}]},
                    {"name":"nvme0n1p3","path":"/dev/nvme0n1p3","type":"part","size":300000000000,"uuid":"DA7A-0002","fstype":"ext4","pkname":"nvme0n1","tran":"nvme"}
                  ]}
                ]}
                """;
            // SystemDisksAsync's walk: the mapper node, its partition, the physical disk, plus /boot.
            var system = new HashSet<string>(StringComparer.Ordinal) { "/dev/mapper/root", "/dev/nvme0n1p2", "/dev/nvme0n1", "/dev/nvme0n1p1" };
            var list = StorageDeviceService.ParseDevices(json, system, storeMounted: true, Sysfs);

            Assert.That(list.Single(d => d.Path == "/dev/nvme0n1p2").SystemDiskLevel, Is.EqualTo("os_partition"));
            Assert.That(list.Single(d => d.Path == "/dev/nvme0n1p3").SystemDiskLevel, Is.EqualTo("os_disk_data"));
            Assert.That(list.Select(d => d.MediaKind), Is.All.EqualTo("nvme"));
        }

        [Test]
        public void Media_kind_comes_from_the_disk_not_the_partition() {
            var list = StorageDeviceService.ParseDevices(EmmcWithData, EmmcSystem, storeMounted: true, Sysfs);
            Assert.That(list.Where(d => d.Path.StartsWith("/dev/mmcblk0", StringComparison.Ordinal)).Select(d => d.MediaKind), Is.All.EqualTo("emmc"));
            Assert.That(list.Single(d => d.Path == "/dev/sda1").MediaKind, Is.EqualTo("unknown"));
        }

        [TestCase(null, "mmcblk0", "MMC", "emmc")]
        [TestCase(null, "mmcblk1", "SD", "sd")]
        [TestCase("nvme", "nvme0n1", null, "nvme")]
        [TestCase("usb", "sda", "Direct-Access", "unknown")]
        [TestCase(null, "sdb", null, "unknown")]
        [TestCase(null, "../etc", "SD", "unknown")]
        [TestCase(null, null, "SD", "unknown")]
        public void ProbeMedia_reads_sysfs_type_and_the_nvme_transport(string? tran, string? disk, string? sysfsType, string expected) {
            string? Read(string path) => sysfsType is not null && disk is not null && path == $"/sys/block/{disk}/device/type" ? sysfsType : null;
            Assert.That(StorageDeviceService.ProbeMedia(tran, disk, Read), Is.EqualTo(expected));
        }

        // Root on mmcblk0p2; every runner call is recorded so a test can show no helper or sudo ran.
        private static StorageDeviceService OsDiskService(string lsblkJson, List<string> calls) {
            Task<(int, string)> Run(string file, string[] args, CancellationToken ct) {
                calls.Add(file);
                return Task.FromResult(
                    (file, args.Length > 0 ? args[0] : "") switch {
                        ("lsblk", "-J") => (0, lsblkJson),
                        ("findmnt", _) => args[^1] == "/" ? (0, "/dev/mmcblk0p2\n") : (1, ""),
                        ("lsblk", "-no") => args[^1] == "/dev/mmcblk0p2" ? (0, "mmcblk0\n") : (0, ""),
                        _ => (1, ""),
                    });
            }
            var paths = new StorageHelperPaths("/nonexistent/helper", "/nonexistent/unit", Path.Combine(Path.GetTempPath(), $"oara-osdisk-{Guid.NewGuid():N}"));
            return new StorageDeviceService(NullLogger<StorageDeviceService>.Instance, paths, Run) { StoreMounted = () => true };
        }

        [Test]
        [Platform("Linux")]
        public async Task Erasing_a_partition_on_the_os_disk_is_refused_without_calling_the_helper() {
            var calls = new List<string>();
            var svc = OsDiskService(EmmcWithData, calls);

            var onOsDisk = await svc.RefuseEraseOnOsDiskAsync("DA7A-0001", CancellationToken.None);
            var root = await svc.RefuseEraseOnOsDiskAsync("/dev/mmcblk0p2", CancellationToken.None);
            var usb = await svc.RefuseEraseOnOsDiskAsync("USB1-0001", CancellationToken.None);

            Assert.That(onOsDisk, Is.EqualTo(new StorageConfigureResult(false, "refused", "system_disk", null)));
            Assert.That(root, Is.EqualTo(new StorageConfigureResult(false, "refused", "system_disk", null)));
            Assert.That(usb, Is.Null, "a drive on another disk may be erased");
            Assert.That(calls, Is.All.Matches<string>(f => f is "lsblk" or "findmnt"), "only reads; no helper or sudo");
        }

        [Test]
        [Platform("Linux")]
        public async Task A_usb_clone_listed_first_does_not_unlock_an_erase_of_the_os_disk_data_partition() {
            // The eMMC data partition cloned to a USB disk: both carry DA7A-0001, and the clone is listed first.
            const string clone = """
                {"name":"sdb","path":"/dev/sdb","type":"disk","size":64000000000,"rm":true,"tran":"usb","children":[
                  {"name":"sdb1","path":"/dev/sdb1","type":"part","size":40000000000,"uuid":"DA7A-0001","fstype":"exfat","label":"DATA","pkname":"sdb"}
                ]},
                """;
            var svc = OsDiskService(EmmcWithData.Replace("{\"blockdevices\": [", "{\"blockdevices\": [" + clone, StringComparison.Ordinal), []);

            var refusal = await svc.RefuseEraseOnOsDiskAsync("DA7A-0001", CancellationToken.None);

            Assert.That(refusal, Is.EqualTo(new StorageConfigureResult(false, "refused", "system_disk", null)));
        }
    }
}
