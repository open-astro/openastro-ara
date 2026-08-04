#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Interfaces;
using OpenAstroAra.Image.FileFormat.RAW;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Image.ImageData;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Test.TestData;

namespace OpenAstroAra.Test;

[TestFixture]
public sealed class LibRawDecoderTest {
    private string _root = null!;

    [SetUp]
    public void SetUp() {
        _root = Path.Combine(Path.GetTempPath(), $"oara-libraw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Test]
    public void Native_runtime_is_available_and_meets_supported_abi() {
        var decoder = new LibRawDecoder();
        var parsed = Version.TryParse(decoder.Version?.Split('-', StringSplitOptions.RemoveEmptyEntries)[0],
            out var version);
        Assert.Multiple(() => {
            Assert.That(decoder.IsAvailable, Is.True,
                "Install LibRaw 0.21+ (libraw23t64/libraw-dev on Debian or Ubuntu).");
            Assert.That(parsed, Is.True);
            Assert.That(version, Is.GreaterThanOrEqualTo(new Version(0, 21)));
        });
    }

    [Test]
    public async Task Synthetic_dng_decodes_from_file_and_buffer_to_linear_rgb() {
        var bytes = SyntheticDng.Create();
        var originalBytes = bytes.ToArray();
        var path = Path.Combine(_root, "fixture.dng");
        await File.WriteAllBytesAsync(path, bytes);
        var decoder = new LibRawDecoder();

        var fromFile = await decoder.DecodeFileAsync(path, ImageLoadLimits.Default,
            CancellationToken.None);
        var fromBuffer = await decoder.DecodeBufferAsync(bytes, ImageLoadLimits.Default,
            CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(fromFile.Width, Is.EqualTo(64));
            Assert.That(fromFile.Height, Is.EqualTo(48));
            Assert.That(fromFile.SourceBitDepth, Is.EqualTo(16));
            Assert.That(fromFile.DebayerMethod, Is.EqualTo("libraw_ahd"));
            Assert.That(fromFile.CameraMake, Is.EqualTo("OpenAstro"));
            Assert.That(fromFile.CameraModel, Is.EqualTo("Synthetic RGGB"));
            Assert.That(fromFile.OriginalCfaPattern, Is.EqualTo("RGGB"));
            Assert.That(fromFile.BorrowRedPlane(), Has.Length.EqualTo(64 * 48));
            Assert.That(fromFile.BorrowGreenPlane(), Has.Length.EqualTo(64 * 48));
            Assert.That(fromFile.BorrowBluePlane(), Has.Length.EqualTo(64 * 48));
            Assert.That(fromFile.BorrowRedPlane().Average(static value => value),
                Is.GreaterThan(fromFile.BorrowGreenPlane().Average(static value => value)));
            Assert.That(fromFile.BorrowGreenPlane().Average(static value => value),
                Is.GreaterThan(fromFile.BorrowBluePlane().Average(static value => value)));
            Assert.That(fromBuffer.BorrowRedPlane(), Is.EqualTo(fromFile.BorrowRedPlane()));
            Assert.That(fromBuffer.BorrowGreenPlane(), Is.EqualTo(fromFile.BorrowGreenPlane()));
            Assert.That(fromBuffer.BorrowBluePlane(), Is.EqualTo(fromFile.BorrowBluePlane()));
            Assert.That(bytes, Is.EqualTo(originalBytes), "LibRaw must not mutate camera source bytes.");
        });
    }

    [Test]
    public void Geometry_limit_rejects_identified_dng_before_pixel_decode() {
        var path = Path.Combine(_root, "bounded.dng");
        File.WriteAllBytes(path, SyntheticDng.Create());
        var limits = new ImageLoadLimits(MaxDimension: 32);
        var ex = Assert.ThrowsAsync<InvalidDataException>(() =>
            new LibRawDecoder().DecodeFileAsync(path, limits, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("64x48"));
    }

    [Test]
    public void Malformed_raw_has_safe_stage_and_native_error() {
        var path = Path.Combine(_root, "broken.dng");
        File.WriteAllBytes(path, "II*\0not-a-dng"u8.ToArray());
        var ex = Assert.ThrowsAsync<RawImageDecodeException>(() =>
            new LibRawDecoder().DecodeFileAsync(path, ImageLoadLimits.Default,
                CancellationToken.None));
        Assert.Multiple(() => {
            Assert.That(ex!.Message, Does.StartWith("LibRaw failed to identify camera RAW data:"));
            Assert.That(ex.NativeErrorCode, Is.Not.Null);
            Assert.That(ex.Message, Does.Not.Contain(path));
        });
    }

    [Test]
    public void Pre_cancelled_file_and_buffer_loads_do_no_native_work() {
        var path = Path.Combine(_root, "cancelled.dng");
        var bytes = SyntheticDng.Create();
        File.WriteAllBytes(path, bytes);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var decoder = new LibRawDecoder();
        Assert.Multiple(() => {
            Assert.That(async () => await decoder.DecodeFileAsync(path, ImageLoadLimits.Default,
                cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(async () => await decoder.DecodeBufferAsync(bytes, ImageLoadLimits.Default,
                cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        });
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Cancellation_interrupts_an_in_flight_native_decode() {
        var bytes = SyntheticDng.Create(width: 3072, height: 2048);
        using var cancellation = new CancellationTokenSource();
        var decode = new LibRawDecoder().DecodeBufferAsync(bytes, ImageLoadLimits.Default,
            cancellation.Token);
        await Task.Delay(10);
        Assert.That(decode.IsCompleted, Is.False,
            "Fixture must still be decoding when cancellation is issued.");

        await cancellation.CancelAsync();

        Assert.That(async () => await decode, Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task Concurrent_decodes_keep_native_contexts_isolated() {
        var bytes = SyntheticDng.Create();
        var decoder = new LibRawDecoder();
        var decodes = Enumerable.Range(0, 8).Select(_ =>
            decoder.DecodeBufferAsync(bytes, ImageLoadLimits.Default, CancellationToken.None));

        var results = await Task.WhenAll(decodes);

        Assert.Multiple(() => {
            Assert.That(results, Has.Length.EqualTo(8));
            Assert.That(results.Select(static result => result.BorrowRedPlane()[100]), Is.All.EqualTo(
                results[0].BorrowRedPlane()[100]));
            Assert.That(results.Select(static result => result.CameraModel),
                Is.All.EqualTo("Synthetic RGGB"));
        });
    }

    [Test]
    public void Empty_and_oversized_buffers_reject_before_native_work() {
        var decoder = new LibRawDecoder();
        Assert.Multiple(() => {
            Assert.That(async () => await decoder.DecodeBufferAsync(ReadOnlyMemory<byte>.Empty,
                ImageLoadLimits.Default, CancellationToken.None), Throws.InstanceOf<InvalidDataException>());
            Assert.That(async () => await decoder.DecodeBufferAsync(new byte[5],
                new ImageLoadLimits(MaxFileBytes: 4), CancellationToken.None),
                Throws.InstanceOf<InvalidDataException>());
        });
    }

    [Test]
    public async Task Camera_exposure_factory_decodes_raw_without_legacy_not_implemented_path() {
        var bytes = SyntheticDng.Create();
        var profile = new HeadlessProfileService();
        var imageFactory = new SourceImageDataFactory(profile);
        var factory = new ExposureDataFactory(imageFactory, profile,
            Mock.Of<IPluggableBehaviorSelector<IStarDetection>>(),
            Mock.Of<IPluggableBehaviorSelector<IStarAnnotator>>(),
            new LibRawDecoder());
        var metadata = new ImageMetaData();

        var exposure = factory.CreateRAWExposureData(RawConverter.FREEIMAGE,
            bytes, "DNG", bitDepth: 14, metadata);
        var image = await exposure.ToImageData(cancelToken: CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(image.Properties.Width, Is.EqualTo(64));
            Assert.That(image.Properties.Height, Is.EqualTo(48));
            Assert.That(image.Properties.BitDepth, Is.EqualTo(16));
            Assert.That(image.Properties.IsBayered, Is.False);
            Assert.That(image.Data.RAWType, Is.EqualTo("dng"));
            Assert.That(image.Data.RAWData, Is.EqualTo(bytes));
            Assert.That(image.Data.FlatArray, Has.Length.EqualTo(64 * 48));
            Assert.That(image.MetaData.Camera.SensorType, Is.EqualTo(SensorType.Color));
            Assert.That(image.MetaData.GenericHeaders.OfType<IGenericMetaDataHeader<string>>()
                .Any(static header => header.Key == "DEBAYER" && header.Value == "libraw_ahd"), Is.True);
            Assert.That(image.RenderBitmapSource(), Has.Length.EqualTo(64 * 48));
        });
    }

    [Test]
    public async Task Inherited_image_factory_loads_supported_raw_file_without_legacy_exception() {
        var path = Path.Combine(_root, "legacy-loader.DNG");
        await File.WriteAllBytesAsync(path, SyntheticDng.Create());
        var detectionSelector = new Mock<IPluggableBehaviorSelector<IStarDetection>>();
        detectionSelector.Setup(static selector => selector.GetBehavior()).Returns(Mock.Of<IStarDetection>());
        var annotationSelector = new Mock<IPluggableBehaviorSelector<IStarAnnotator>>();
        annotationSelector.Setup(static selector => selector.GetBehavior()).Returns(Mock.Of<IStarAnnotator>());
        var factory = new ImageDataFactory(new HeadlessProfileService(), detectionSelector.Object,
            annotationSelector.Object);

        var image = await factory.CreateFromFile(path, bitDepth: 14, isBayered: true,
            RawConverter.DCRAW, CancellationToken.None);
        var originalBytes = await File.ReadAllBytesAsync(path);

        Assert.Multiple(() => {
            Assert.That(BaseImageData.FileIsSupported(path), Is.True);
            Assert.That(image.Properties.Width, Is.EqualTo(64));
            Assert.That(image.Properties.Height, Is.EqualTo(48));
            Assert.That(image.Properties.BitDepth, Is.EqualTo(16));
            Assert.That(image.Data.RAWType, Is.EqualTo("dng"));
            Assert.That(image.Data.RAWData, Is.EqualTo(originalBytes));
        });
    }
}