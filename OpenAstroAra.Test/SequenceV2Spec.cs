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
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Reads <c>docs/specs/sequence-v2.md</c> for the tests that tie the spec to the code: the kind
    /// tables, the field cells and the Example. The spec is the source the validator is pinned to,
    /// so a drift in either direction fails a test.
    /// </summary>
    internal static class SequenceV2Spec {

        /// <summary>One row of a kind table: the kind, the engine class's simple name and the cell that lists its fields.</summary>
        internal sealed record Row(string Role, string Kind, string ClassName, string FieldsCell);

        internal static string RepoFile(string relative) {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docs", "specs", "sequence-v2.md"))) {
                dir = dir.Parent;
            }
            Assert.That(dir, Is.Not.Null, "docs/specs/sequence-v2.md is above the test directory");
            return Path.Combine(dir!.FullName, relative);
        }

        internal static string[] Lines() => File.ReadAllLines(RepoFile("docs/specs/sequence-v2.md"));

        private static readonly Regex Backticked = new("`([^`]+)`", RegexOptions.Compiled);

        /// <summary>The rows of the four tables under "Kind to engine class", keyed by the role the heading names.</summary>
        internal static List<Row> KindRows() {
            var roles = new Dictionary<string, string> {
                ["### Containers"] = "Container", ["### Instructions"] = "Instruction",
                ["### Conditions"] = "Condition", ["### Triggers"] = "Trigger",
            };
            var rows = new List<Row>();
            string? role = null;
            foreach (var line in Lines()) {
                if (line.StartsWith('#')) {
                    roles.TryGetValue(line.Trim(), out role);
                    continue;
                }
                if (role == null || !line.StartsWith("| `", StringComparison.Ordinal)) continue;
                var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                var kind = Backticked.Match(cells[0]).Groups[1].Value;
                if (kind == "kind") continue; // a table header
                rows.Add(new Row(role, kind, Backticked.Match(cells[1]).Groups[1].Value, cells[^1]));
            }
            return rows;
        }

        /// <summary>The first <c>json</c> code block under "## Example".</summary>
        internal static string Example() {
            var lines = Lines();
            var start = Array.FindIndex(lines, l => l.Trim() == "## Example");
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "the spec has an Example section");
            var open = Array.FindIndex(lines, start, l => l.Trim() == "```json");
            var close = Array.FindIndex(lines, open + 1, l => l.Trim() == "```");
            return string.Join("\n", lines.Skip(open + 1).Take(close - open - 1));
        }
    }
}