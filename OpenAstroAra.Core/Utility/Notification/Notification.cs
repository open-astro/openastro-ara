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
using System.Threading;

namespace OpenAstroAra.Core.Utility.Notification {

    /// <summary>
    /// Where the ported code's warning and error toasts go (#1174). The Equipment, Sequencer,
    /// PlateSolving and Profile layers still call the static <see cref="Notifier"/> (it was
    /// NINA's toast UI; the WPF toasts went in Phase 0.5p) and cannot take a DI'd service
    /// without threading one through every device class, so the daemon plugs its §46
    /// notification inbox in here at startup instead. Informational toasts are not
    /// forwarded: they were transient UI chatter, never an inbox entry.
    /// </summary>
    public interface INotifierSink {
        /// <param name="isError">true for the error variants, false for the warnings.</param>
        /// <param name="header">The external-source header (PHD2, a driver), or null.</param>
        void Notify(bool isError, string? header, string message);
    }

    // The ported toast surface. Warning/error variants log through Logger with the
    // ORIGINAL call site ([CallerXxx] attributes propagate it) and then hand the text to
    // the installed INotifierSink, so an operational problem lands in the daemon log AND
    // in WILMA's inbox. Informational variants are no-ops (see INotifierSink).
    public static class Notifier {

        private static INotifierSink? _sink;

        /// <summary>Install (or, with null, remove) the inbox bridge. The daemon sets it once
        /// at boot; tests set and clear their own.</summary>
        public static void Use(INotifierSink? sink) => Volatile.Write(ref _sink, sink);

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A sink that throws must never turn a logged warning into an exception at the device call site; the log line already carries the message.")]
        private static void Forward(bool isError, string? header, string message) {
            var sink = Volatile.Read(ref _sink);
            if (sink is null) {
                return;
            }
            try {
                sink.Notify(isError, header, message);
            } catch (Exception ex) {
                Logger.Warning($"Notifier sink failed: {ex.Message}");
            }
        }

        public static void ShowInformation(string message) { }

        public static void ShowInformation(string message, TimeSpan lifetime) { }

        public static void ShowSuccess(string message) { }

        public static void ShowWarning(string message,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Logger.Warning(message, memberName, sourceFilePath, lineNumber);
            Forward(isError: false, header: null, message);
        }

        public static void ShowWarning(string message, TimeSpan lifetime,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Logger.Warning(message, memberName, sourceFilePath, lineNumber);
            Forward(isError: false, header: null, message);
        }

        public static void ShowError(string message,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Logger.Error(message, memberName, sourceFilePath, lineNumber);
            Forward(isError: true, header: null, message);
        }

        public static void ShowExternalError(string message, string header,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Logger.Error(string.IsNullOrWhiteSpace(header) ? message : $"{header}: {message}",
                memberName, sourceFilePath, lineNumber);
            Forward(isError: true, string.IsNullOrWhiteSpace(header) ? null : header, message);
        }

        public static void ShowExternalWarning(string message, string header,
                [CallerMemberName] string memberName = "",
                [CallerFilePath] string sourceFilePath = "",
                [CallerLineNumber] int lineNumber = 0) {
            Logger.Warning(string.IsNullOrWhiteSpace(header) ? message : $"{header}: {message}",
                memberName, sourceFilePath, lineNumber);
            Forward(isError: false, string.IsNullOrWhiteSpace(header) ? null : header, message);
        }

        public static void CloseAll() { }

        public static void Dispose() { }
    }
}