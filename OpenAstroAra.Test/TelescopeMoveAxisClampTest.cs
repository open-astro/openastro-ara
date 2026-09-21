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

    // #1064 — the daemon, not the client's speed picker, is the guard on MoveAxis
    // rates. These pin the clamp rule the endpoint relies on.
    [TestFixture]
    public class TelescopeMoveAxisClampTest {

        [Test]
        public void Stop_passes_through_without_a_rate_read() {
            // rate 0 must never be gated — even when no maximum is known.
            Assert.That(TelescopeService.ClampMoveAxisRate(0, null), Is.EqualTo(0));
            Assert.That(TelescopeService.ClampMoveAxisRate(0, 6.0), Is.EqualTo(0));
        }

        [Test]
        public void Rate_within_max_is_unchanged() {
            Assert.That(TelescopeService.ClampMoveAxisRate(1.5, 6.016), Is.EqualTo(1.5));
            Assert.That(TelescopeService.ClampMoveAxisRate(-1.5, 6.016), Is.EqualTo(-1.5));
            Assert.That(TelescopeService.ClampMoveAxisRate(6.016, 6.016), Is.EqualTo(6.016));
        }

        [Test]
        public void Rate_over_max_is_clamped_with_sign_preserved() {
            Assert.That(TelescopeService.ClampMoveAxisRate(50, 6.016), Is.EqualTo(6.016));
            Assert.That(TelescopeService.ClampMoveAxisRate(-50, 6.016), Is.EqualTo(-6.016));
        }

        [Test]
        public void Nonzero_rate_with_no_readable_max_is_refused() {
            Assert.Throws<System.InvalidOperationException>(
                () => TelescopeService.ClampMoveAxisRate(1.0, null));
            Assert.Throws<System.InvalidOperationException>(
                () => TelescopeService.ClampMoveAxisRate(1.0, 0));
        }

        [Test]
        public void Clamp_cache_settles_on_completed_reads_CanMoveAxis_false_or_the_pass_bound() {
            Assert.That(TelescopeService.ShouldSettleAxisMax(readsCompleted: true, mountCannotMoveAxis: false, passes: 1), Is.True);
            Assert.That(TelescopeService.ShouldSettleAxisMax(readsCompleted: false, mountCannotMoveAxis: true, passes: 1), Is.True);
            Assert.That(TelescopeService.ShouldSettleAxisMax(readsCompleted: false, mountCannotMoveAxis: false, passes: 1), Is.False, "a thrown read retries");
            Assert.That(TelescopeService.ShouldSettleAxisMax(readsCompleted: false, mountCannotMoveAxis: false, passes: 3), Is.True, "bounded: never on the poll path for the session");
        }

        [Test]
        public void NaN_is_treated_as_stop() {
            Assert.That(TelescopeService.ClampMoveAxisRate(double.NaN, 6.0), Is.EqualTo(0));
        }
    }
}
