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
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1267 — the offline name index: every identifier HYG and OpenNGC already carry answers,
    /// whatever the case, spacing, dashes, leading zeros or Greek-letter spelling.</summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk
    public class TargetNameIndexTest {

        private string _root = null!;

        [SetUp]
        public void SetUp() {
            _root = Path.Combine(Path.GetTempPath(), "ara-nameidx-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown() {
            if (Directory.Exists(_root)) {
                Directory.Delete(_root, recursive: true);
            }
        }

        private void Write(string package, string body) {
            Directory.CreateDirectory(Path.Combine(_root, package));
            File.WriteAllText(Path.Combine(_root, package, "catalog.csv"), body);
        }

        private const string HygHeader = "\"id\",\"hip\",\"hd\",\"hr\",\"gl\",\"bf\",\"proper\",\"ra\",\"dec\",\"mag\",\"bayer\",\"flam\",\"con\",\"var\"\n";
        // AB Cas as the real HYG v4.0 row carries it (hip 12235, var AB, con Cas); Sirius with every designation.
        private const string HygRows =
            "12203,12235,,,\"\",\"\",\"\",2.625431,71.304515,10.21,\"\",\"\",Cas,AB\n"
            + "32263,32349,48915,2491,\"GJ 244\",\"9Alp CMa\",\"Sirius\",6.752477,-16.716116,-1.44,Alp,9,CMa,\"\"\n";

        private const string NgcHeader = "Name;Type;RA;Dec;V-Mag;B-Mag;M;NGC;IC;Identifiers;Common names\n";
        private const string NgcRows =
            "NGC0224;G;00:42:44.33;+41:16:07.5;3.44;4.36;031;;;C 017,UGC 00454;Andromeda Galaxy,Messier 31\n"
            + "NGC7000;Neb;20:59:17.14;+44:31:43.6;;;;;;C 020,LBN 373;North America Nebula\n"
            + "IC0434;HII;05:41:00.00;-02:27:00.0;;;;2023;;LBN 954;\n"
            + "NGC0001;G;00:07:15.84;+27:42:29.1;13.6;;;;;;\n";

        [Test]
        public void Keys_ignore_case_space_leading_zeros_and_the_variable_prefix() {
            Assert.That(TargetNameIndex.TightKey("ngc 0224"), Is.EqualTo("NGC224"));
            Assert.That(TargetNameIndex.TightKey("M031"), Is.EqualTo("M31"));
            Assert.That(TargetNameIndex.TightKey("V* AB Cas"), Is.EqualTo("ABCAS"));
            Assert.That(TargetNameIndex.TightKey("Sh2-117"), Is.EqualTo("SH2-117"), "dashes stay");
            Assert.That(TargetNameIndex.LooseKey("SH2-117"), Is.EqualTo("SH2117"));
            Assert.That(TargetNameIndex.TightKey("HIP 012235"), Is.EqualTo("HIP12235"));
            Assert.That(TargetNameIndex.TightKey("NGC 1000"), Is.EqualTo("NGC1000"), "inner zeros are digits, not padding");
        }

        [Test]
        public void Greek_letters_spelled_out_abbreviated_or_as_symbols_share_one_key() {
            Assert.That(TargetNameIndex.TightKey("Alp CMa"), Is.EqualTo("ALPCMA"));
            Assert.That(TargetNameIndex.TightKey("alpha CMa"), Is.EqualTo("ALPCMA"));
            Assert.That(TargetNameIndex.TightKey("α CMa"), Is.EqualTo("ALPCMA"));
            Assert.That(TargetNameIndex.TightKey("Omicron Cet"), Is.EqualTo("OMICET"));
            Assert.That(TargetNameIndex.TightKey("Mu Cep"), Is.EqualTo("MUCEP"));
            Assert.That(TargetNameIndex.TightKey("Etamin"), Is.EqualTo("ETAMIN"), "a proper name that starts like a Greek letter is not one");
        }

        [Test]
        public void Hyg_rows_answer_to_every_designation_column() {
            Write("hyg-stars", HygHeader + HygRows);
            var idx = new TargetNameIndex(_root);
            foreach (var q in new[] { "AB Cas", "ab cas", "V* AB Cas", "HIP 12235" }) {
                var e = idx.Find(q, CancellationToken.None);
                Assert.That(e, Is.Not.Null, q);
                Assert.That(e!.MainId, Is.EqualTo("AB Cas"));
                Assert.That(e.RaDeg, Is.EqualTo(2.625431 * 15).Within(1e-6));
                Assert.That(e.DecDeg, Is.EqualTo(71.304515).Within(1e-9));
                Assert.That(e.ObjectType, Is.EqualTo("V*"));
                Assert.That(e.Magnitude, Is.EqualTo(10.21));
            }
            foreach (var q in new[] { "Sirius", "SIRIUS", "alp CMa", "Alpha CMa", "α CMa", "9 CMa", "HIP 32349", "HD 48915", "HR 2491", "GJ 244", "Gliese 244" }) {
                var e = idx.Find(q, CancellationToken.None);
                Assert.That(e, Is.Not.Null, q);
                Assert.That(e!.MainId, Is.EqualTo("Sirius"), q);
            }
            Assert.That(idx.Find("Sirius", CancellationToken.None)!.Aliases, Does.Contain("HIP 32349").And.Contain("Alp CMa").And.Contain("9 CMa"));
            Assert.That(idx.Count, Is.EqualTo(2));
        }

        [Test]
        public void Openngc_rows_answer_to_messier_ngc_ic_identifiers_and_common_names() {
            Write("openngc-dso", NgcHeader + NgcRows);
            var idx = new TargetNameIndex(_root);
            foreach (var q in new[] { "NGC 224", "ngc0224", "M 31", "m31", "M031", "Messier 31", "andromeda galaxy", "C 17", "UGC 454" }) {
                var e = idx.Find(q, CancellationToken.None);
                Assert.That(e, Is.Not.Null, q);
                Assert.That(e!.MainId, Is.EqualTo("NGC0224"), q);
                Assert.That(e.Magnitude, Is.EqualTo(3.44));
                Assert.That(e.ObjectType, Is.EqualTo("G"));
            }
            Assert.That(idx.Find("North America Nebula", CancellationToken.None)!.MainId, Is.EqualTo("NGC7000"));
            Assert.That(idx.Find("LBN 373", CancellationToken.None)!.MainId, Is.EqualTo("NGC7000"));
            Assert.That(idx.Find("NGC 2023", CancellationToken.None)!.MainId, Is.EqualTo("IC0434"), "the NGC cross-reference on an IC row");
            Assert.That(idx.Find("NGC 1", CancellationToken.None)!.MainId, Is.EqualTo("NGC0001"));
            Assert.That(idx.Find("NGC 2", CancellationToken.None), Is.Null);
        }

        [Test]
        public void Dashes_are_tried_tight_then_loose_so_suffixed_designations_do_not_collide() {
            Write("wr-stars", NgcHeader
                + "WR 2-1;*;00:00:00;+00:00:00;;;;;;;\n"
                + "WR 21;*;01:00:00;+10:00:00;;;;;;;\n"
                + "Sh2-117;HII;20:59:17.14;+44:31:43.6;;;;;;;\n");
            var idx = new TargetNameIndex(_root);
            Assert.That(idx.Find("WR 2-1", CancellationToken.None)!.MainId, Is.EqualTo("WR 2-1"));
            Assert.That(idx.Find("WR 21", CancellationToken.None)!.MainId, Is.EqualTo("WR 21"));
            Assert.That(idx.Find("sh2 117", CancellationToken.None)!.MainId, Is.EqualTo("Sh2-117"));
            Assert.That(idx.Find("SH2117", CancellationToken.None)!.MainId, Is.EqualTo("Sh2-117"));
        }

        [Test]
        public void The_index_rebuilds_when_a_catalog_lands_and_empty_root_answers_nothing() {
            var idx = new TargetNameIndex(_root);
            Assert.That(idx.Find("Sirius", CancellationToken.None), Is.Null);
            Assert.That(idx.Count, Is.Zero);
            Write("hyg-stars", HygHeader + HygRows);
            Assert.That(idx.Find("Sirius", CancellationToken.None), Is.Not.Null, "a catalog installed mid-session is searchable");
            Assert.That(idx.Find("", CancellationToken.None), Is.Null);
            Assert.That(idx.Find("   ", CancellationToken.None), Is.Null);
        }

        [Test]
        public void A_corrupt_catalog_is_skipped_not_fatal() {
            Write("openngc-dso", "garbage\n;;;\n");
            Write("hyg-stars", HygHeader + HygRows);
            var idx = new TargetNameIndex(_root);
            Assert.That(idx.Find("Sirius", CancellationToken.None), Is.Not.Null);
        }
    }

    /// <summary>#1267 — the Sesame client: the URL, the XML parse (Simbad first, then the next resolver),
    /// a clean "nothing found", and transport failures as <see cref="SesameUnavailableException"/>.</summary>
    [TestFixture]
    public class SesameClientTest {

        private sealed class Handler : HttpMessageHandler {
            public HttpStatusCode Status = HttpStatusCode.OK;
            public string Body = "";
            public Exception? Throw;
            public Uri? LastUri;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
                LastUri = request.RequestUri;
                if (Throw is not null) {
                    throw Throw;
                }
                return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
            }
        }

        private const string AbCas = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Sesame xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
            <Target option="SNV"><name>AB Cas</name>
              <Resolver name="S=Simbad">
                <otype>EB*</otype>
                <jpos>02:37:31.51 +71:18:16.5</jpos>
                <jradeg>39.3812958</jradeg>
                <jdedeg>71.3045833</jdedeg>
                <mag band="B"><v>10.59</v><q>E</q></mag>
                <mag band="V"><v>10.32</v><q>E</q></mag>
                <oname>V* AB Cas</oname>
                <alias>HIP 12235</alias>
                <alias>TYC 4320-505-1</alias>
                <alias>V* AB Cas</alias>
              </Resolver>
              <Resolver name="N=NED"><INFO>*** Nothing found ***</INFO></Resolver>
            </Target>
            </Sesame>
            """;

        private const string NedOnly = """
            <Sesame><Target option="SNV"><name>x</name>
              <Resolver name="S=Simbad"><INFO>*** Nothing found ***</INFO></Resolver>
              <Resolver name="N=NED"><oname>NED Thing</oname><jradeg>10.5</jradeg><jdedeg>-20.25</jdedeg><mag band="G"><v>12.1</v></mag></Resolver>
            </Target></Sesame>
            """;

        private static readonly string[] AbCasAliases = { "HIP 12235", "TYC 4320-505-1" };

        [Test]
        public void The_url_escapes_the_name_and_asks_simbad_then_ned_then_vizier_in_xml() {
            Assert.That(SesameClient.UrlFor("V* AB Cas").AbsoluteUri, Is.EqualTo("https://cds.unistra.fr/cgi-bin/nph-sesame/-oxpIF/SNV?V%2A%20AB%20Cas"));
        }

        [Test]
        public void Parse_takes_simbads_answer_with_its_aliases_and_v_magnitude() {
            var r = SesameClient.Parse(AbCas);
            Assert.That(r, Is.Not.Null);
            Assert.That(r!.MainId, Is.EqualTo("V* AB Cas"));
            Assert.That(r.RaDeg, Is.EqualTo(39.3812958).Within(1e-9));
            Assert.That(r.DecDeg, Is.EqualTo(71.3045833).Within(1e-9));
            Assert.That(r.ObjectType, Is.EqualTo("EB*"));
            Assert.That(r.Magnitude, Is.EqualTo(10.32), "the V band, not B");
            Assert.That(r.Aliases, Is.EqualTo(AbCasAliases), "the main id is not repeated");
        }

        [Test]
        public void Parse_falls_through_to_the_next_resolver_and_takes_the_only_band_given() {
            var r = SesameClient.Parse(NedOnly);
            Assert.That(r, Is.Not.Null);
            Assert.That(r!.MainId, Is.EqualTo("NED Thing"));
            Assert.That(r.Magnitude, Is.EqualTo(12.1));
            Assert.That(r.DecDeg, Is.EqualTo(-20.25));
        }

        [Test]
        public void Parse_returns_null_for_nothing_found_or_a_position_out_of_range() {
            Assert.That(SesameClient.Parse("<Sesame><Target><Resolver name=\"S=Simbad\"><INFO>*** Nothing found ***</INFO></Resolver></Target></Sesame>"), Is.Null);
            Assert.That(SesameClient.Parse("<Sesame><Target><Resolver><oname>x</oname><jradeg>1</jradeg><jdedeg>95</jdedeg></Resolver></Target></Sesame>"), Is.Null);
            Assert.That(SesameClient.Parse("<Sesame/>"), Is.Null);
        }

        [Test]
        public void Parse_refuses_a_dtd() {
            Assert.Throws<System.Xml.XmlException>(() => SesameClient.Parse("<!DOCTYPE x [<!ENTITY e \"x\">]><Sesame/>"));
        }

        [Test]
        public async Task A_found_object_comes_back_and_nothing_found_is_null() {
            using var h = new Handler { Body = AbCas };
            using var client = new SesameClient(handler: h);
            var r = await client.ResolveAsync("AB Cas", CancellationToken.None);
            Assert.That(r!.MainId, Is.EqualTo("V* AB Cas"));
            Assert.That(h.LastUri!.AbsoluteUri, Does.EndWith("SNV?AB%20Cas"));
            h.Body = "<Sesame><Target><Resolver><INFO>*** Nothing found ***</INFO></Resolver></Target></Sesame>";
            Assert.That(await client.ResolveAsync("nonsense", CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task Transport_failures_a_bad_status_and_a_non_xml_body_are_unavailable() {
            using var h = new Handler { Throw = new HttpRequestException("no route") };
            using var client = new SesameClient(handler: h);
            await AssertUnavailable(client);
            h.Throw = null;
            h.Status = HttpStatusCode.ServiceUnavailable;
            await AssertUnavailable(client);
            h.Status = HttpStatusCode.OK;
            h.Body = "<html>captive portal</html>";
            await AssertUnavailable(client);
        }

        private static async Task AssertUnavailable(SesameClient client) {
            try {
                await client.ResolveAsync("x", CancellationToken.None);
                Assert.Fail("expected SesameUnavailableException");
            } catch (SesameUnavailableException) {
                Assert.Pass();
            }
        }
    }

    /// <summary>#1267 — the resolver's order (index, cache, Sesame), its statuses, and the cache that keeps a
    /// Simbad answer for the dark site.</summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk (SQLite)
    public class TargetResolverServiceTest {

        private sealed class FakeSesame : ISesameClient {
            public SesameResult? Answer;
            public bool Unreachable;
            public int Calls;
            public Task<SesameResult?> ResolveAsync(string name, CancellationToken ct) {
                Calls++;
                if (Unreachable) {
                    throw new SesameUnavailableException("down");
                }
                return Task.FromResult(Answer);
            }
        }

        private string _dir = null!;
        private SqliteAraDatabase _db = null!;

        [SetUp]
        public async Task SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), "ara-resolver-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_dir, "sky-data", "hyg-stars"));
            await File.WriteAllTextAsync(Path.Combine(_dir, "sky-data", "hyg-stars", "catalog.csv"),
                "\"id\",\"hip\",\"hd\",\"hr\",\"gl\",\"bf\",\"proper\",\"ra\",\"dec\",\"mag\",\"bayer\",\"flam\",\"con\",\"var\"\n"
                + "12203,12235,,,\"\",\"\",\"\",2.625431,71.304515,10.21,\"\",\"\",Cas,AB\n");
            _db = new SqliteAraDatabase(_dir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);
        }

        [TearDown]
        public void TearDown() {
            if (Directory.Exists(_dir)) {
                Directory.Delete(_dir, recursive: true);
            }
        }

        private TargetResolverService Svc(FakeSesame? sesame) =>
            new(new TargetNameIndex(Path.Combine(_dir, "sky-data")), sesame, _db, clock: () => new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero));

        private static readonly string[] HipOnly = { "HIP 12235" };
        private static readonly string[] WolfAliases = { "CN Leo", "GJ 406" };

        private static SesameResult Wolf359() => new("NAME Wolf 359", ["CN Leo", "GJ 406"], 164.1203, 7.0146, "Er*", 13.5);

        [Test]
        public async Task The_offline_index_answers_first_without_asking_simbad() {
            var sesame = new FakeSesame { Answer = Wolf359() };
            var r = await Svc(sesame).ResolveAsync("hip 12235", CancellationToken.None);
            Assert.That(r.Status, Is.EqualTo("resolved"));
            Assert.That(r.Source, Is.EqualTo("index"));
            Assert.That(r.MainId, Is.EqualTo("AB Cas"));
            Assert.That(r.Aliases, Is.EqualTo(HipOnly));
            Assert.That(r.RaDeg, Is.EqualTo(39.381465).Within(1e-5));
            Assert.That(sesame.Calls, Is.Zero);
        }

        [Test]
        public async Task A_simbad_answer_is_cached_and_served_offline_next_time() {
            var sesame = new FakeSesame { Answer = Wolf359() };
            var first = await Svc(sesame).ResolveAsync("Wolf 359", CancellationToken.None);
            Assert.That(first.Status, Is.EqualTo("resolved"));
            Assert.That(first.Source, Is.EqualTo("simbad"));
            Assert.That(first.MainId, Is.EqualTo("NAME Wolf 359"));
            Assert.That(first.Aliases, Is.EqualTo(WolfAliases));
            Assert.That(first.ObjectType, Is.EqualTo("Er*"));
            Assert.That(first.Magnitude, Is.EqualTo(13.5));

            var offline = new FakeSesame { Unreachable = true };
            var again = await Svc(offline).ResolveAsync("wolf359", CancellationToken.None);
            Assert.That(again.Status, Is.EqualTo("resolved"));
            Assert.That(again.Source, Is.EqualTo("cache"));
            Assert.That(again.MainId, Is.EqualTo("NAME Wolf 359"));
            Assert.That(again.Aliases, Is.EqualTo(WolfAliases));
            Assert.That(again.RaDeg, Is.EqualTo(164.1203));
            Assert.That(again.Magnitude, Is.EqualTo(13.5));
            Assert.That(offline.Calls, Is.Zero);
        }

        [Test]
        public async Task Not_found_is_reported_and_never_cached() {
            var sesame = new FakeSesame { Answer = null };
            var r = await Svc(sesame).ResolveAsync("nonsense", CancellationToken.None);
            Assert.That(r.Status, Is.EqualTo("not_found"));
            Assert.That(r.Source, Is.Empty);
            Assert.That(r.RaDeg, Is.Null);
            Assert.That(r.Message, Does.Contain("Simbad knows no object"));
            sesame.Answer = Wolf359();
            var later = await Svc(sesame).ResolveAsync("nonsense", CancellationToken.None);
            Assert.That(later.Status, Is.EqualTo("resolved"), "a miss is asked again, not remembered");
            Assert.That(sesame.Calls, Is.EqualTo(2));
        }

        [Test]
        public async Task Unreachable_simbad_reports_offline_with_the_needs_internet_message() {
            var r = await Svc(new FakeSesame { Unreachable = true }).ResolveAsync("TYC 4320-505-1", CancellationToken.None);
            Assert.That(r.Status, Is.EqualTo("offline"));
            Assert.That(r.Message, Does.Contain("needs internet"));
            var none = await Svc(null).ResolveAsync("TYC 4320-505-1", CancellationToken.None);
            Assert.That(none.Status, Is.EqualTo("offline"));
        }

        [Test]
        public async Task A_blank_query_is_not_found_and_a_resolver_without_a_database_still_works() {
            var noDb = new TargetResolverService(new TargetNameIndex(Path.Combine(_dir, "sky-data")), new FakeSesame { Answer = Wolf359() }, db: null);
            Assert.That((await noDb.ResolveAsync("  ", CancellationToken.None)).Status, Is.EqualTo("not_found"));
            Assert.That((await noDb.ResolveAsync("Wolf 359", CancellationToken.None)).Source, Is.EqualTo("simbad"));
        }
    }

    /// <summary>#1267 — the endpoint's name validation (the only 400).</summary>
    [TestFixture]
    public class TargetEndpointsTest {
        [Test]
        public void Validate_rejects_blank_long_and_control_characters() {
            Assert.That(TargetEndpoints.ValidateName(null), Is.EqualTo("name is required"));
            Assert.That(TargetEndpoints.ValidateName("   "), Is.EqualTo("name is required"));
            Assert.That(TargetEndpoints.ValidateName(new string('a', 129)), Does.Contain("128"));
            Assert.That(TargetEndpoints.ValidateName("AB\nCas"), Does.Contain("control"));
            Assert.That(TargetEndpoints.ValidateName("V* AB Cas"), Is.Null);
            Assert.That(TargetEndpoints.ValidateName(" M 31 "), Is.Null);
        }
    }
}
