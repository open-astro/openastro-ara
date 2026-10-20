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
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test;

/// <summary>The render gate behind previews and thumbnails: a semaphore whose freed
/// slots go to a waiting preview before any thumbnail. Seen on the CM4 after a drive
/// switch: ~50 uncached thumbnails queued, and an opened frame's preview waited 95 s
/// behind them, past the client's 60 s timeout.</summary>
public class RenderGateTest {
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    [Test]
    public async Task A_preview_queued_after_thumbnails_gets_the_next_slot() {
        var gate = new RenderGate(2);
        await gate.WaitAsync(preview: false, CancellationToken.None).ConfigureAwait(false);
        await gate.WaitAsync(preview: false, CancellationToken.None).ConfigureAwait(false);
        var thumb1 = gate.WaitAsync(preview: false, CancellationToken.None);
        var thumb2 = gate.WaitAsync(preview: false, CancellationToken.None);
        var preview = gate.WaitAsync(preview: true, CancellationToken.None);

        gate.Release();
        await preview.WaitAsync(Soon).ConfigureAwait(false);
        Assert.That(thumb1.IsCompleted, Is.False, "the preview jumped the thumbnail queue");

        gate.Release();
        await thumb1.WaitAsync(Soon).ConfigureAwait(false);
        Assert.That(thumb2.IsCompleted, Is.False, "thumbnails stay first-come first-served");
        gate.Release();
        await thumb2.WaitAsync(Soon).ConfigureAwait(false);
    }

    [Test]
    public async Task Thumbnails_use_every_slot_while_no_preview_waits() {
        var gate = new RenderGate(2);
        await gate.WaitAsync(preview: false, CancellationToken.None).WaitAsync(Soon).ConfigureAwait(false);
        await gate.WaitAsync(preview: false, CancellationToken.None).WaitAsync(Soon).ConfigureAwait(false);
        Assert.That(gate.Free, Is.Zero);
        gate.Release();
        gate.Release();
        Assert.That(gate.Free, Is.EqualTo(2), "released slots return to the pool when nobody waits");
    }

    [Test]
    public async Task A_cancelled_wait_leaves_the_queue_and_never_holds_a_slot() {
        var gate = new RenderGate(1);
        await gate.WaitAsync(preview: false, CancellationToken.None).ConfigureAwait(false);
        using var cts = new CancellationTokenSource();
        var cancelled = gate.WaitAsync(preview: false, cts.Token);
        var next = gate.WaitAsync(preview: false, CancellationToken.None);

        await cts.CancelAsync().ConfigureAwait(false);
        await Assert.ThatAsync(async () => await cancelled.ConfigureAwait(false), Throws.InstanceOf<OperationCanceledException>()).ConfigureAwait(false);

        gate.Release();
        await next.WaitAsync(Soon).ConfigureAwait(false);
        gate.Release();
        Assert.That(gate.Free, Is.EqualTo(1), "the cancelled waiter took no slot with it");
    }

    [Test]
    public async Task An_already_cancelled_wait_throws_without_queueing() {
        var gate = new RenderGate(1);
        await gate.WaitAsync(preview: true, CancellationToken.None).ConfigureAwait(false);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync().ConfigureAwait(false);
        await Assert.ThatAsync(async () => await gate.WaitAsync(preview: false, cts.Token).ConfigureAwait(false),
            Throws.InstanceOf<OperationCanceledException>()).ConfigureAwait(false);
        gate.Release();
        Assert.That(gate.Free, Is.EqualTo(1));
    }

    [Test]
    public void A_gate_needs_at_least_one_slot() {
        Assert.That(() => new RenderGate(0), Throws.InstanceOf<ArgumentOutOfRangeException>());
    }
}
