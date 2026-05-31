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
using System.Linq;
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

        // §38k-3 — verify WithDefaults() registers the three structural
        // containers and that the JSON converter can now resolve them
        // (instead of falling back to UnknownSequenceContainer).

        [Test]
        public void WithDefaults_registers_the_three_structural_containers() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            Assert.That(factory.Container, Has.Count.EqualTo(3));
            var typeNames = factory.Container.Select(c => c.GetType().Name).ToList();
            Assert.That(typeNames, Does.Contain("SequenceRootContainer"));
            Assert.That(typeNames, Does.Contain("SequentialContainer"));
            Assert.That(typeNames, Does.Contain("ParallelContainer"));
        }

        [Test]
        public void WithDefaults_factory_resolves_SequentialContainer_via_JSON() {
            // End-to-end: the JSON converter's GetContainer<T> reflection
            // lookup hits the registered prototype, clones it, returns a
            // real SequentialContainer rather than UnknownSequenceContainer.
            var factory = HeadlessSequencerFactory.WithDefaults();
            var deserializer = new SequenceBodyDeserializer(factory, logger: null);
            var body = JsonDocument.Parse("""
                {
                    "schemaVersion": "openastroara-sequence-v1",
                    "$type": "OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer"
                }
                """).RootElement.Clone();

            var ok = deserializer.TryDeserialize(body, out var container, out var error);

            Assert.That(ok, Is.True, $"unexpected error: {error}");
            Assert.That(container, Is.Not.Null);
            Assert.That(container!.GetType().Name, Is.EqualTo("SequentialContainer"));
        }

        [Test]
        public void WithDefaults_factory_resolves_SequenceRootContainer_via_JSON() {
            var factory = HeadlessSequencerFactory.WithDefaults();
            var deserializer = new SequenceBodyDeserializer(factory, logger: null);
            var body = JsonDocument.Parse("""
                {
                    "schemaVersion": "openastroara-sequence-v1",
                    "$type": "OpenAstroAra.Sequencer.Container.SequenceRootContainer, OpenAstroAra.Sequencer"
                }
                """).RootElement.Clone();

            var ok = deserializer.TryDeserialize(body, out var container, out _);

            Assert.That(ok, Is.True);
            Assert.That(container!.GetType().Name, Is.EqualTo("SequenceRootContainer"));
        }

        [Test]
        public void WithDefaults_factory_still_degrades_for_unknown_type() {
            // Even with structural containers registered, an unknown $type
            // (e.g. an equipment-bound instruction we haven't wired yet)
            // still falls back to UnknownSequenceContainer rather than
            // throwing — preserves the §38k-1 graceful-degradation invariant.
            var factory = HeadlessSequencerFactory.WithDefaults();
            var deserializer = new SequenceBodyDeserializer(factory, logger: null);
            var body = JsonDocument.Parse("""
                {
                    "schemaVersion": "openastroara-sequence-v1",
                    "$type": "Fictional.Namespace.NoSuchContainer, Fictional.Assembly"
                }
                """).RootElement.Clone();

            var ok = deserializer.TryDeserialize(body, out var container, out _);

            Assert.That(ok, Is.True);
            Assert.That(container!.GetType().Name, Is.EqualTo("UnknownSequenceContainer"));
        }

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
