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
using OpenAstroAra.Server.Contracts.WsEvents;
using System.Reflection;
using System.Text.RegularExpressions;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1804 — GET /api/v1/ws/catalog (<see cref="WsEventCatalog.All"/>) must name every event the daemon emits,
    /// every declared constant must be in <c>All</c>, and every catalog entry must have an emitter.
    /// The emit scan is a SOURCE scan of OpenAstroAra.Server (outside the catalog file): it sees a token written as
    /// a string literal that is the first argument of a Publish/PublishAsync/EmitAsync call, or the value of a
    /// <c>public static readonly WsEvent XxxEvent</c> (the emitter-owned WS form, e.g. BackupService.CreateStartedEvent; a private *Event const such as a diagnostics type is not one); and it sees a use of
    /// a <c>WsEventCatalog.X</c> constant. For the "has an emitter" direction any token-shaped literal counts. It cannot
    /// see a token assembled at run time (concatenation or interpolation), nor a literal that reaches the broadcaster
    /// through a variable or ternary without being a <c>*Event</c> const.
    /// </summary>
    [TestFixture]
    public class WsEventCatalogCoverageTest {

        private static readonly Regex EventConst = new("public\\s+const\\s+string\\s+\\w*Event\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex TokenLiteral = new("\"([a-z][a-z0-9_]*(?:\\.[a-z][a-z0-9_]*)+)\"", RegexOptions.Compiled);
        private static readonly Regex EmitCallLiteral = new("(?:PublishAsync|EmitAsync|Publish)\\(\\s*\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex ConstantUse = new("WsEventCatalog\\.([A-Za-z0-9_]+)", RegexOptions.Compiled);

        private static string ServerDir() {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "OpenAstroAra.sln"))) { dir = dir.Parent; }
            Assert.That(dir, Is.Not.Null, "repo root (OpenAstroAra.sln) not found above the test directory");
            return Path.Combine(dir!.FullName, "OpenAstroAra.Server");
        }

        private static IEnumerable<(string File, int Line, string Text)> CodeLines() {
            var catalog = Path.Combine("Contracts", "WsEvents", "WsEventCatalog.cs");
            foreach (var file in Directory.EnumerateFiles(ServerDir(), "*.cs", SearchOption.AllDirectories)) {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || file.EndsWith(catalog, StringComparison.Ordinal)) {
                    continue;
                }
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++) {
                    var t = lines[i].TrimStart();
                    if (t.StartsWith("//", StringComparison.Ordinal)) { continue; }
                    yield return (file, i + 1, lines[i]);
                }
            }
        }

        private static string Token(FieldInfo f) => ((WsEvent)f.GetValue(null)!).Token;

        private static readonly HashSet<string> AllTokens = new(WsEventCatalog.All.Select(e => e.Token), StringComparer.Ordinal);

        private static Dictionary<string, FieldInfo> DeclaredConstants() =>
            typeof(WsEventCatalog).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(WsEvent))
                .ToDictionary(f => f.Name);

        [Test]
        public void EveryEmittedTokenIsInTheCatalog() {
            var missing = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (file, line, text) in CodeLines()) {
                foreach (Match m in EventConst.Matches(text).Concat(EmitCallLiteral.Matches(text))) {
                    var token = m.Groups[1].Value;
                    if (!AllTokens.Contains(token)) { missing.TryAdd(token, $"{Path.GetFileName(file)}:{line}"); }
                }
            }
            Assert.That(missing, Is.Empty,
                "emitted but not in WsEventCatalog.All: " + string.Join("; ", missing.Select(kv => $"{kv.Key} ({kv.Value})")));
        }

        [Test]
        public void EveryDeclaredConstantIsInAll() {
            var notInAll = DeclaredConstants().Values
                .Where(f => !AllTokens.Contains(Token(f)))
                .Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.That(notInAll, Is.Empty, "declared in WsEventCatalog but missing from All: " + string.Join(", ", notInAll));
        }

        [Test]
        public void EveryCatalogEntryHasAnEmitter() {
            var constants = DeclaredConstants();
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (_, _, text) in CodeLines()) {
                foreach (Match m in TokenLiteral.Matches(text)) { emitted.Add(m.Groups[1].Value); }
                foreach (Match m in ConstantUse.Matches(text)) {
                    if (constants.TryGetValue(m.Groups[1].Value, out var f)) { emitted.Add(Token(f)); }
                }
            }
            var dead = AllTokens.Where(t => !emitted.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();
            Assert.That(dead, Is.Empty, "in the catalog but nothing emits it: " + string.Join(", ", dead));
        }
    }
}