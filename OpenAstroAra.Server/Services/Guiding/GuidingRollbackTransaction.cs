#region "copyright"

/* Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors. */

#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services.Guiding;

/// <summary>
/// Runs every independent restore operation and reports all failures together.
/// Rollback must continue after one driver property rejects a write.
/// </summary>
public static class GuidingRollbackTransaction {
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Rollback must continue after arbitrary hardware and protocol failures.")]
    public static async Task ExecuteAllAsync(IEnumerable<Func<Task>> operations) {
        ArgumentNullException.ThrowIfNull(operations);
        var errors = new List<Exception>();
        foreach (var operation in operations) {
            ArgumentNullException.ThrowIfNull(operation);
            try {
                await operation().ConfigureAwait(false);
            } catch (Exception error) {
                errors.Add(error);
            }
        }
        if (errors.Count > 0)
            throw new AggregateException("one or more guiding snapshot restore operations failed", errors);
    }
}
