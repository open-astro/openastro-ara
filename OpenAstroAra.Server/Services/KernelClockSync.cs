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
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace OpenAstroAra.Server.Services;

/// <summary>#2071 — whether the kernel's clock is disciplined by NTP (or PTP), so the rig clock can be
/// trusted without a push from the app or a GPS: after a daemon restart on a rig with working NTP, the
/// mount's clock can be set and the GoTo gate opened with no client. The same rule as AlpacaBridge's
/// HostClockTrust (#2426): a kernel-synchronised clock, or one a client set.</summary>
public interface IKernelClockSyncProbe {
    /// <summary>True only when the kernel reports its clock synchronised; false when it is not, when
    /// its error bound is too large, or when it cannot tell. Cheap: one syscall, no process spawn.</summary>
    bool IsSynchronized();
}

/// <summary>The Linux probe: a read-only <c>adjtimex</c> (modes 0, no privilege needed). Synchronised when
/// the call does not report <c>TIME_ERROR</c>, <c>STA_UNSYNC</c> is clear (an NTP/PTP daemon set it clear;
/// <c>clock_settime</c>, as a client push does, sets it again) and <c>maxerror</c>, the kernel's bound on
/// the clock's error, is within <see cref="MaxErrorMicroseconds"/>. The kernel grows that bound by 500 ppm
/// while nothing disciplines the clock, so a daemon that stopped updating loses the trust on its own.</summary>
internal sealed class LinuxKernelClockSyncProbe : IKernelClockSyncProbe {

    /// <summary>2 s: under the mount clock's 5 s tolerance, and above the ~1 s the bound reaches between
    /// systemd-timesyncd's longest (2048 s) polls.</summary>
    internal const long MaxErrorMicroseconds = 2_000_000;

    private const int StaUnsync = 0x0040;
    private const int TimeError = 5;

    // struct timex on LP64 Linux (glibc and musl agree): unsigned modes, 4 bytes of padding, then the longs
    // offset, freq, maxerror, esterror, then int status. 208 bytes; the buffer is larger on purpose.
    private const int TimexBytes = 256;
    private const int MaxErrorOffset = 24;
    private const int StatusOffset = 40;
    private const int ToleranceOffset = 64;

    // Classic DllImport like LinuxSystemClockSetter's clock_settime: a blittable byte[] is pinned, not copied,
    // so no marshalling code is generated. libc, not a vendor library (ADR 0003 rule 3).
    [DllImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int adjtimex([In, Out] byte[] buf);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A probe that cannot run (no libc, an unexpected platform) answers 'not synchronised': the rig clock is then trusted only from a client push.")]
    public bool IsSynchronized() {
        try {
            return Read() is { } t && Evaluate(t.State, t.Status, t.MaxErrorMicroseconds);
        } catch (Exception) {
            return false;
        }
    }

    /// <summary>One read-only <c>adjtimex</c>; null off 64-bit little-endian Linux. <c>Tolerance</c> (the
    /// kernel's fixed 500 ppm, as ppm × 2^16) lets a test check the struct layout against the kernel.</summary>
    internal static (int State, int Status, long MaxErrorMicroseconds, long Tolerance)? Read() {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || IntPtr.Size != 8 || !BitConverter.IsLittleEndian) {
            return null;
        }
        var buf = new byte[TimexBytes]; // modes = 0: read only
        var state = adjtimex(buf);
        return (state,
            BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(StatusOffset)),
            BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(MaxErrorOffset)),
            BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(ToleranceOffset)));
    }

    /// <summary>The rule, pure: the call's return, the status word and maxerror (µs).</summary>
    internal static bool Evaluate(int state, int status, long maxErrorMicroseconds) =>
        state >= 0 && state != TimeError && (status & StaUnsync) == 0
            && maxErrorMicroseconds >= 0 && maxErrorMicroseconds <= MaxErrorMicroseconds;
}
