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
using OpenAstroAra.Sequencer.SequenceItem.Dome;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Server.Services.Equipment;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38k-21 — verifies <see cref="HeadlessDomeFollower"/> sentinel behavior
    /// + that the SynchronizeDome instruction (deferred from §38k-18 on the
    /// IDomeFollower dependency) now registers and resolves via the factory.
    /// </summary>
    [TestFixture]
    public class HeadlessDomeFollowerTest {

        [Test]
        public void Reports_not_synchronized_and_not_following() {
            var f = new HeadlessDomeFollower();
            Assert.That(f.IsSynchronized, Is.False);
            Assert.That(f.IsFollowing, Is.False);
        }

        [Test]
        public async Task TriggerTelescopeSync_returns_false() {
            var f = new HeadlessDomeFollower();
            Assert.That(await f.TriggerTelescopeSync(), Is.False);
        }

        [Test]
        public async Task SyncToScopeCoordinates_returns_false() {
            var f = new HeadlessDomeFollower();
            Assert.That(
                await f.SyncToScopeCoordinates(null!, default, CancellationToken.None),
                Is.False);
        }

        [Test]
        public void GetSynchronizedDomeCoordinates_throws_not_supported() {
            var f = new HeadlessDomeFollower();
            Assert.Throws<NotSupportedException>(() => f.GetSynchronizedDomeCoordinates(null!));
        }

        [Test]
        public void WithDefaults_registers_SynchronizeDome() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Items.Select(i => i.GetType().Name), Does.Contain("SynchronizeDome"));
        }

        [Test]
        public void WithDefaults_factory_resolves_SynchronizeDome_via_prototype_lookup() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var prototype = factory.GetItem<SynchronizeDome>();
            Assert.That(prototype, Is.Not.Null);
            Assert.That(prototype, Is.InstanceOf<SynchronizeDome>());
        }
    }
}
