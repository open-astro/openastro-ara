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
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§57.4 — the panic-stop ordering contract behind POST /telescope/abort (#836 r5):
    /// device abort first, sequence pause UNCONDITIONALLY after — including when the abort
    /// throws.</summary>
    [TestFixture]
    public class MountStopHandlerTest {

        private static readonly string[] AbortThenPause = ["abort", "pause"];

        [Test]
        public async Task A_successful_abort_runs_abort_then_pause() {
            var calls = new List<string>();
            await MountStopHandler.ExecuteAsync(
                () => { calls.Add("abort"); return Task.CompletedTask; },
                () => { calls.Add("pause"); return Task.CompletedTask; });
            Assert.That(calls, Is.EqualTo(AbortThenPause),
                "the mount stops before the sequence is told to pause");
        }

        [Test]
        public void A_throwing_device_abort_still_pauses_the_sequence() {
            // §57.4 step 2's whole point: a driver error must not leave the run firing exposures
            // at a mount that may still be moving.
            var paused = false;
            var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
                MountStopHandler.ExecuteAsync(
                    () => throw new InvalidOperationException("driver went away"),
                    () => { paused = true; return Task.CompletedTask; }));
            Assert.That(paused, Is.True, "the pause is unconditional");
            Assert.That(ex!.Message, Does.Contain("driver went away"),
                "the abort failure still surfaces to the caller");
        }
    }
}
