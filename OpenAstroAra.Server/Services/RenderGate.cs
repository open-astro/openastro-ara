#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Bounds concurrent full-FITS renders (previews + thumbnails) like a semaphore, with
/// two queues: a freed slot goes to the oldest <em>preview</em> waiter before any
/// thumbnail waiter. The library grid queues dozens of thumbnail renders at once (a
/// drive whose cache sidecars are missing renders every tile); without the priority a
/// frame the user opens waited behind all of them, past the client's 60 s timeout.
/// Thumbnails still use every slot while nobody is waiting for a preview.
/// </summary>
internal sealed class RenderGate {
    private readonly object sync = new();
    private readonly LinkedList<TaskCompletionSource> previews = new();
    private readonly LinkedList<TaskCompletionSource> thumbnails = new();
    private int free;

    public RenderGate(int slots) {
        ArgumentOutOfRangeException.ThrowIfLessThan(slots, 1);
        free = slots;
    }

    /// <summary>Slots not held right now (tests).</summary>
    internal int Free {
        get {
            lock (sync) {
                return free;
            }
        }
    }

    /// <summary>Wait for a slot. A cancelled wait leaves the queue; one that was granted
    /// its slot in the same instant completes normally, and its caller releases it.</summary>
    public async Task WaitAsync(bool preview, CancellationToken ct) {
        TaskCompletionSource waiter;
        LinkedListNode<TaskCompletionSource> node;
        lock (sync) {
            // A thumbnail never takes a free slot past a waiting preview.
            if (free > 0 && (preview || previews.Count == 0)) {
                free--;
                return;
            }
            ct.ThrowIfCancellationRequested();
            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = (preview ? previews : thumbnails).AddLast(waiter);
        }
        using (ct.Register(() => {
            lock (sync) {
                if (node.List is null) {
                    return; // already granted
                }
                node.List.Remove(node);
            }
            waiter.TrySetCanceled(ct);
        })) {
            await waiter.Task.ConfigureAwait(false);
        }
    }

    /// <summary>Blocking wait for the synchronous render path (runs on a pool thread).</summary>
    public void Wait(bool preview) => WaitAsync(preview, CancellationToken.None).GetAwaiter().GetResult();

    public void Release() {
        TaskCompletionSource? next = null;
        lock (sync) {
            var queue = previews.Count > 0 ? previews : thumbnails;
            if (queue.First is { } first) {
                queue.RemoveFirst();
                next = first.Value;
            } else {
                free++;
            }
        }
        next?.TrySetResult();
    }
}
