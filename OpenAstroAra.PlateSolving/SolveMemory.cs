using OpenAstroAra.Astrometry;
using System;

namespace OpenAstroAra.PlateSolving {

    /// <summary>
    /// #1136 — the last successful solve of this daemon process, used as the staged search's fallback
    /// centre when the mount's hint fails (a mount that lost sync is usually still within a few degrees
    /// of where it last solved). Process-wide on purpose: every solve path (manual solve, centering,
    /// rotation) feeds and reads it without plumbing. A remembered position older than
    /// <see cref="MaxAge"/> is ignored — the scope has moved on.
    /// </summary>
    public static class SolveMemory {
        private static readonly object Gate = new();
        private static Coordinates? last;
        private static DateTime lastUtc;

        public static readonly TimeSpan MaxAge = TimeSpan.FromHours(2);

        /// <summary>Clock seam for tests.</summary>
        internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public static void Remember(Coordinates? solved) {
            if (solved is null) {
                return;
            }
            lock (Gate) {
                last = solved.Transform(Epoch.J2000);
                lastUtc = UtcNow();
            }
        }

        /// <summary>The remembered position, or null when none or too old.</summary>
        public static Coordinates? Recall() {
            lock (Gate) {
                if (last is null || UtcNow() - lastUtc > MaxAge) {
                    return null;
                }
                return last.Transform(Epoch.J2000);
            }
        }

        public static void Clear() {
            lock (Gate) {
                last = null;
            }
        }
    }
}
