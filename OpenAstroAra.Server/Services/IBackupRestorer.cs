#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Collections.Generic;
using System.Threading;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// §43-2b seam over the static <see cref="BackupRestorer"/> so the restore background worker is testable:
    /// a fake can block (to observe the <c>running</c> clone-status) or throw (to observe <c>failed</c>) without a
    /// real archive. The production impl (<see cref="DefaultBackupRestorer"/>) just delegates to the static.
    /// </summary>
    public interface IBackupRestorer {
        /// <summary>Extract + atomically swap the requested areas from <paramref name="zipPath"/> into
        /// <paramref name="profileDir"/>; returns the areas actually restored.</summary>
        IReadOnlyList<string> Restore(
            string zipPath, string profileDir, bool restoreProfile, bool restoreSequences, CancellationToken ct);
    }

    /// <summary>Production <see cref="IBackupRestorer"/> — delegates to the real staged-swap restorer.</summary>
    internal sealed class DefaultBackupRestorer : IBackupRestorer {
        public IReadOnlyList<string> Restore(
            string zipPath, string profileDir, bool restoreProfile, bool restoreSequences, CancellationToken ct) =>
            BackupRestorer.Restore(zipPath, profileDir, restoreProfile, restoreSequences, ct);
    }
}
