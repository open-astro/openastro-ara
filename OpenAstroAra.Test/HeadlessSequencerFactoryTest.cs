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
using OpenAstroAra.Server.Services;
using System.Text.Json;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §38k-2 — verifies <see cref="HeadlessSequencerFactory"/> constructs
    /// with empty backing lists, exposes the four <c>ICollectionView</c>
    /// properties without throwing, and feeds through
    /// <see cref="SequenceBodyDeserializer"/> for the unknown-type
    /// graceful-degradation path end-to-end (i.e. with the real factory
    /// instead of the §38k-1 Moq stand-in).
    /// </summary>
    [TestFixture]
    public class HeadlessSequencerFactoryTest {

        [Test]
        public void Constructs_with_no_arguments_yielding_empty_lists() {
            var factory = new HeadlessSequencerFactory();
            Assert.That(factory.Items, Is.Empty);
            Assert.That(factory.Conditions, Is.Empty);
            Assert.That(factory.Container, Is.Empty);
            Assert.That(factory.Triggers, Is.Empty);
            Assert.That(factory.DateTimeProviders, Is.Empty);
        }

        [Test]
        public void Exposes_non_null_ICollectionView_properties() {
            var factory = new HeadlessSequencerFactory();
            Assert.That(factory.ItemsView, Is.Not.Null);
            Assert.That(factory.InstructionsView, Is.Not.Null);
            Assert.That(factory.ConditionsView, Is.Not.Null);
            Assert.That(factory.TriggersView, Is.Not.Null);
        }

        [Test]
        public void ViewFilter_default_is_empty_string_and_round_trips() {
            var factory = new HeadlessSequencerFactory();
            Assert.That(factory.ViewFilter, Is.EqualTo(string.Empty));
            factory.ViewFilter = "imaging";
            Assert.That(factory.ViewFilter, Is.EqualTo("imaging"));
        }

        // The GetXxx<T>() default-fallback path needs a generic call site
        // with a real T : ISequenceX constraint; that's exercised via the
        // SequenceBodyDeserializer integration test below rather than
        // explicitly enumerating each generic method here.

        [Test]
        public void Pairs_with_SequenceBodyDeserializer_for_unknown_type_path() {
            // End-to-end: with the real HeadlessSequencerFactory in place
            // (rather than the §38k-1 Moq), the deserializer still
            // gracefully degrades on an unknown $type.
            var factory = new HeadlessSequencerFactory();
            var deserializer = new SequenceBodyDeserializer(factory, logger: null);
            var body = JsonDocument.Parse("""
                {
                    "schemaVersion": "openastroara-sequence-v1",
                    "$type": "Fictional.Namespace.NoSuchContainer, Fictional.Assembly"
                }
                """).RootElement.Clone();

            var ok = deserializer.TryDeserialize(body, out var container, out var error);

            Assert.That(ok, Is.True, $"unexpected error: {error}");
            Assert.That(container, Is.Not.Null);
            Assert.That(container!.GetType().Name, Is.EqualTo("UnknownSequenceContainer"));
        }
    }
}
