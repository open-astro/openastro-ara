#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;

namespace OpenAstroAra.Test {

    /// <summary>
    /// openastro-guider 2.1.0 answers <c>guiding_assistant_apply</c> with bare names in the array; Ara's
    /// parse failed and reported "Unable to get response from phd2" although every value had been applied
    /// (rig night 2026-10-18, four Accept presses). The repair quotes bare array elements and nothing else.
    /// </summary>
    [TestFixture]
    public class Phd2ReplyRepairTest {
        private static readonly string[] FourApplied = ["exposure", "min_move_ra", "min_move_dec", "dec_guide_mode"];

        [Test]
        public void The_guiders_bare_name_array_parses_after_the_repair() {
            const string line = "{\"jsonrpc\":\"2.0\",\"result\":{\"applied\":[exposure,min_move_ra,min_move_dec,dec_guide_mode]},\"id\":\"42\"}";
            Assert.That(() => JObject.Parse(line), Throws.InstanceOf<Newtonsoft.Json.JsonReaderException>(), "the raw line is invalid JSON");

            var repaired = PHD2Guider.QuoteBareNames(line);
            var o = JObject.Parse(repaired);
            Assert.That(o["result"]!["applied"]!.ToObject<string[]>(), Is.EqualTo(FourApplied));
            Assert.That(o["id"]!.Value<string>(), Is.EqualTo("42"));
        }

        [Test]
        public void Valid_json_is_left_alone() {
            foreach (var line in new[] {
                "{\"result\":{\"applied\":[\"exposure\",\"min_move_ra\"]},\"id\":\"1\"}",
                "{\"result\":[true,false,null,1,2.5,\"x\"],\"id\":\"2\"}",
                "{\"error\":{\"code\":1,\"message\":\"no star selected - start looping and select a star first (loop + find_star), or start guiding\"},\"id\":\"3\"}",
                "{\"result\":{\"state\":\"measuring\",\"messages\":[]},\"id\":\"4\"}",
            }) {
                Assert.That(PHD2Guider.QuoteBareNames(line), Is.EqualTo(line), line);
            }
        }

        [Test]
        public void A_single_bare_element_and_spaces_are_handled() {
            Assert.That(PHD2Guider.QuoteBareNames("{\"applied\":[exposure]}"), Is.EqualTo("{\"applied\":[\"exposure\"]}"));
            Assert.That(PHD2Guider.QuoteBareNames("{\"applied\":[ exposure , dec_guide_mode ]}"), Is.EqualTo("{\"applied\":[ \"exposure\" , \"dec_guide_mode\" ]}"));
        }

        [Test]
        public void Assistant_states_that_mean_a_run_is_in_progress() {
            Assert.That(PHD2Guider.IsGuidingAssistantRunning("measuring"), Is.True);
            Assert.That(PHD2Guider.IsGuidingAssistantRunning("measuring_backlash"), Is.True);
            foreach (var idle in new[] { "idle", "done", "failed", "", null }) {
                Assert.That(PHD2Guider.IsGuidingAssistantRunning(idle), Is.False, idle ?? "null");
            }
        }
    }
}
