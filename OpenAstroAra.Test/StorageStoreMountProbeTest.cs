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
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §29 / #1207 — "is the frames store mounted?" decided from
    /// <c>/proc/self/mounts</c> text. A line at the mount point is NOT enough:
    /// after a restart with the disk ejected, systemd's optional
    /// <c>ReadWritePaths=-/media/openastroara</c> bind-mounts the empty
    /// directory into the service namespace, so the daemon sees a line there
    /// backed by the ROOT filesystem's device. The probe must call that
    /// ejected, and a real USB disk mounted.
    /// </summary>
    [TestFixture]
    public class StorageStoreMountProbeTest {

        private const string Root =
            "/dev/vda1 / ext4 rw,relatime,errors=remount-ro 0 0\n" +
            "sysfs /sys sysfs rw,nosuid,nodev,noexec,relatime 0 0\n" +
            "proc /proc proc rw,nosuid,nodev,noexec,relatime 0 0\n" +
            "tmpfs /run tmpfs rw,nosuid,nodev,noexec,relatime,size=200000k,mode=755 0 0\n";

        [Test]
        public void No_line_at_the_mount_point_is_ejected() {
            Assert.That(StoreMountProbe.IsStoreMounted(Root.Split('\n')), Is.False);
        }

        [Test]
        public void The_bind_of_the_empty_directory_from_the_root_disk_is_ejected() {
            // Exactly what the VM showed after a daemon restart with no disk:
            // the same device as "/" mounted at the store path.
            var mounts = Root + "/dev/vda1 /media/openastroara ext4 rw,relatime,errors=remount-ro 0 0\n";
            Assert.That(StoreMountProbe.IsStoreMounted(mounts.Split('\n')), Is.False,
                "a bind of the root filesystem is the empty directory, not a store");
        }

        [Test]
        public void A_real_exfat_usb_disk_is_mounted() {
            var mounts = Root +
                "/dev/sda1 /media/openastroara exfat rw,noatime,uid=999,gid=999,fmask=0113,dmask=0002,iocharset=utf8,errors=remount-ro 0 0\n";
            Assert.That(StoreMountProbe.IsStoreMounted(mounts.Split('\n')), Is.True);
        }

        [Test]
        public void A_real_ext4_usb_disk_is_mounted_even_though_root_is_ext4_too() {
            var mounts = Root + "/dev/sda1 /media/openastroara ext4 rw,noatime,data=ordered 0 0\n";
            Assert.That(StoreMountProbe.IsStoreMounted(mounts.Split('\n')), Is.True,
                "fstype alone cannot tell a store from the root bind — the device must");
        }

        [Test]
        public void A_bind_from_a_separate_media_filesystem_is_still_ejected() {
            // /media on its own partition: the bind's source matches the
            // NEAREST ancestor mount, not "/". Still no disk of its own.
            var mounts = Root +
                "/dev/vda2 /media ext4 rw,relatime 0 0\n" +
                "/dev/vda2 /media/openastroara ext4 rw,relatime 0 0\n";
            Assert.That(StoreMountProbe.IsStoreMounted(mounts.Split('\n')), Is.False);
        }

        [Test]
        public void A_disk_mounted_over_the_stale_bind_is_mounted() {
            // Replug after the restart: the real mount stacks on top of the
            // bind, and the LAST line at the path is the one in effect.
            var mounts = Root +
                "/dev/vda1 /media/openastroara ext4 rw,relatime,errors=remount-ro 0 0\n" +
                "/dev/sda1 /media/openastroara exfat rw,noatime,uid=999,gid=999 0 0\n";
            Assert.That(StoreMountProbe.IsStoreMounted(mounts.Split('\n')), Is.True);
        }

        [Test]
        public void A_sibling_mount_under_media_does_not_count() {
            var mounts = Root + "/dev/sdb1 /media/openastroara-old exfat rw 0 0\n";
            Assert.That(StoreMountProbe.IsStoreMounted(mounts.Split('\n')), Is.False);
        }
    }
}
