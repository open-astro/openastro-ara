#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Runtime.CompilerServices;

namespace OpenAstroAra.Core.Utility.Notification {

    // TODO(port): Phase 15 sweep — replace these static calls at all 28+ call sites
    // with DI'd INotificationService (defined in OpenAstroAra.Server/Services/IServerStateServices.cs
    // per Phase 9 / §60.9), which broadcasts notification.posted WS events to WILMA.
    // For now: informational variants are no-ops; warning/error variants forward to
    // Logger so operational issues stay visible in the daemon log instead of being
    // silently dropped (the WPF toast UI was removed in Phase 0.5p). [CallerXxx]
    // attributes are propagated so Logger captures the *original* call site (e.g.
    // ProfileService.cs:256) rather than this stub.
    public static class Notification {

        public static void ShowInformation(string message) { }

        public static void ShowInformation(string message, TimeSpan lifetime) { }

        public static void ShowSuccess(string message) { }

        public static void ShowWarning(string message,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Logger.Warning(message, memberName, sourceFilePath, lineNumber);
        }

        public static void ShowWarning(string message, TimeSpan lifetime,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Logger.Warning(message, memberName, sourceFilePath, lineNumber);
        }

        public static void ShowError(string message,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Logger.Error(message, memberName, sourceFilePath, lineNumber);
        }

        public static void ShowExternalError(string message, string header,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Logger.Error(string.IsNullOrWhiteSpace(header) ? message : $"{header}: {message}",
                memberName, sourceFilePath, lineNumber);
        }

        public static void ShowExternalWarning(string message, string header,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Logger.Warning(string.IsNullOrWhiteSpace(header) ? message : $"{header}: {message}",
                memberName, sourceFilePath, lineNumber);
        }

        public static void CloseAll() { }

        public static void Dispose() { }
    }
}
