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
using OpenAstroAra.Fits;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Stretch;
using SkiaSharp;

namespace OpenAstroAra.Test;

[TestFixture]
public sealed class PreviewImageServiceTest {
    private string _root = null!;
    private string _sourceRoot = null!;
    private string _cacheRoot = null!;
    private HeadlessProfileService _profile = null!;
    private SourceImageDataFactory _sourceFactory = null!;

    [SetUp]
    public void SetUp() {
        _root = Path.Combine(Path.GetTempPath(), $"oara-preview-{Guid.NewGuid():N}");
        _sourceRoot = Path.Combine(_root, "sources");
        _cacheRoot = Path.Combine(_root, "cache");
        Directory.CreateDirectory(_sourceRoot);
        _profile = new HeadlessProfileService();
        _sourceFactory = new SourceImageDataFactory(_profile);
    }

    [TearDown]
    public void TearDown() {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Test]
    public async Task Render_is_cached_outside_source_and_returns_applied_metadata() {
        var path = WriteFits("mono.fits", 20, 10);
        using var service = Service();
        var request = Request(path, maxDimension: 8);

        var first = await service.RenderAsync(request, CancellationToken.None);
        var second = await service.RenderAsync(request, CancellationToken.None);
        using var decoded = SKBitmap.Decode(first.Bytes);

        Assert.Multiple(() => {
            Assert.That(first.CacheHit, Is.False);
            Assert.That(second.CacheHit, Is.True);
            Assert.That(second.Bytes, Is.EqualTo(first.Bytes));
            Assert.That(first.Metadata.Width, Is.EqualTo(8));
            Assert.That(first.Metadata.Height, Is.EqualTo(4));
            Assert.That(decoded.Width, Is.EqualTo(8));
            Assert.That(decoded.Height, Is.EqualTo(4));
            Assert.That(first.Metadata.AppliedParameters.Whitepoint,
                Is.GreaterThan(first.Metadata.AppliedParameters.Blackpoint));
            Assert.That(Directory.EnumerateFiles(_sourceRoot, "*.preview*", SearchOption.AllDirectories), Is.Empty);
            Assert.That(Directory.EnumerateFiles(_cacheRoot, "*.jpg", SearchOption.AllDirectories).Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Source_change_invalidates_checksum_fingerprint_key() {
        var path = WriteFits("mutable.fits", 12, 8, seed: 1);
        using var service = Service();
        var request = Request(path);
        var first = await service.RenderAsync(request, CancellationToken.None);

        WriteFits("mutable.fits", 12, 8, seed: 2000);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        var second = await service.RenderAsync(request, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(second.CacheHit, Is.False);
            Assert.That(second.Metadata.CacheKey, Is.Not.EqualTo(first.Metadata.CacheKey));
            Assert.That(second.Metadata.SourceChecksumSha256,
                Is.Not.EqualTo(first.Metadata.SourceChecksumSha256));
        });
    }

    [Test]
    public async Task Decoder_identity_change_invalidates_cached_pixels() {
        var path = WriteFits("decoder-version.fits", 12, 8, seed: 7);
        var request = Request(path);
        using var firstService = new PreviewImageService(_cacheRoot,
            new IdentitySourceFactory(_sourceFactory, "decoder-a"));
        var first = await firstService.RenderAsync(request, CancellationToken.None);
        using var secondService = new PreviewImageService(_cacheRoot,
            new IdentitySourceFactory(_sourceFactory, "decoder-b"));

        var second = await secondService.RenderAsync(request, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(first.CacheHit, Is.False);
            Assert.That(second.CacheHit, Is.False);
            Assert.That(second.Metadata.CacheKey, Is.Not.EqualTo(first.Metadata.CacheKey));
        });
    }

    [Test]
    public async Task Every_render_control_changes_the_cache_key() {
        var path = WriteFits("controls.fits", 12, 8, "RGGB");
        using var service = Service();
        var baseline = await service.RenderAsync(Request(path, applyDebayer: true), CancellationToken.None);
        var inverted = await service.RenderAsync(Request(path, applyDebayer: true) with { Invert = true }, CancellationToken.None);
        var smaller = await service.RenderAsync(Request(path, maxDimension: 5, applyDebayer: true), CancellationToken.None);
        var mono = await service.RenderAsync(Request(path, applyDebayer: true) with {
            ChannelMode = PreviewChannelMode.Luminance,
        }, CancellationToken.None);
        var saturated = await service.RenderAsync(Request(path, applyDebayer: true) with { Saturation = 1.5 }, CancellationToken.None);
        var annotated = await service.RenderAsync(Request(path, applyDebayer: true) with {
            AnnotateStars = true,
        }, CancellationToken.None);
        var styled = await service.RenderAsync(Request(path, applyDebayer: true) with {
            AnnotateStars = true,
            AnnotationOptions = new StarAnnotationOptions(Red: 255, Green: 0, Blue: 0),
        }, CancellationToken.None);

        Assert.That(new[] {
            baseline.Metadata.CacheKey,
            inverted.Metadata.CacheKey,
            smaller.Metadata.CacheKey,
            mono.Metadata.CacheKey,
            saturated.Metadata.CacheKey,
            annotated.Metadata.CacheKey,
            styled.Metadata.CacheKey,
        }.Distinct().Count(), Is.EqualTo(7));
    }

    [Test]
    public async Task Annotation_is_optional_cached_capped_and_never_changes_source() {
        var path = WriteStarFieldFits("stars.fits", 120, 90);
        var sourceBefore = await File.ReadAllBytesAsync(path);
        using var service = Service();
        var plain = await service.RenderAsync(Request(path), CancellationToken.None);
        var request = Request(path) with {
            AnnotateStars = true,
            AnnotationOptions = new StarAnnotationOptions(
                Red: 255, Green: 0, Blue: 0, MaxAnnotations: 2),
            StarSensitivity = 5,
        };

        var annotated = await service.RenderAsync(request, CancellationToken.None);
        var cached = await service.RenderAsync(request, CancellationToken.None);
        var sourceAfter = await File.ReadAllBytesAsync(path);
        using var decoded = SKBitmap.Decode(annotated.Bytes);
        var redPixels = 0;
        for (var y = 0; y < decoded.Height; y++) {
            for (var x = 0; x < decoded.Width; x++) {
                var color = decoded.GetPixel(x, y);
                if (color.Red > color.Green + 40 && color.Red > color.Blue + 40) redPixels++;
            }
        }

        Assert.Multiple(() => {
            Assert.That(annotated.Metadata.Annotated, Is.True);
            Assert.That(annotated.Metadata.AnnotationCount, Is.EqualTo(2));
            Assert.That(annotated.Metadata.RejectedAnnotationCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(annotated.Metadata.AnnotationColor, Is.EqualTo("#ff0000"));
            Assert.That(plain.Metadata.Annotated, Is.False);
            Assert.That(annotated.Metadata.CacheKey, Is.Not.EqualTo(plain.Metadata.CacheKey));
            Assert.That(cached.CacheHit, Is.True);
            Assert.That(cached.Metadata.AnnotationCount, Is.EqualTo(2));
            Assert.That(sourceAfter, Is.EqualTo(sourceBefore));
            Assert.That(redPixels, Is.GreaterThan(10));
        });
    }

    [Test]
    public async Task Debayer_crop_channel_and_raw_cfa_modes_report_correct_dimensions() {
        var path = WriteFits("osc.fits", 8, 6, "RGGB");
        using var service = Service();
        var cropped = await service.RenderAsync(Request(path, applyDebayer: true) with {
            ChannelMode = PreviewChannelMode.Red,
            CropX = 1,
            CropY = 1,
            CropWidth = 6,
            CropHeight = 4,
        }, CancellationToken.None);
        var raw = await service.RenderAsync(Request(path, applyDebayer: false), CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(cropped.Metadata.Width, Is.EqualTo(3));
            Assert.That(cropped.Metadata.Height, Is.EqualTo(2));
            Assert.That(cropped.Metadata.DebayerMode, Is.EqualTo("super_pixel"));
            Assert.That(cropped.Metadata.ChannelMode, Is.EqualTo("red"));
            Assert.That(raw.Metadata.Width, Is.EqualTo(8));
            Assert.That(raw.Metadata.Height, Is.EqualTo(6));
            Assert.That(raw.Metadata.DebayerMode, Is.EqualTo("none"));
            Assert.That(raw.Metadata.ChannelMode, Is.EqualTo("raw_cfa"));
        });
    }

    [Test]
    public void Color_channel_without_debayer_is_rejected() {
        var path = WriteFits("channel.fits", 8, 6, "RGGB");
        using var service = Service();
        var request = Request(path) with { ChannelMode = PreviewChannelMode.Blue };
        var ex = Assert.ThrowsAsync<ArgumentException>(() =>
            service.RenderAsync(request, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("require debayering"));
    }

    [Test]
    public void Crop_outside_source_is_rejected() {
        var path = WriteFits("crop.fits", 8, 6);
        using var service = Service();
        var request = Request(path) with {
            CropX = 7,
            CropY = 0,
            CropWidth = 2,
            CropHeight = 2,
        };
        var ex = Assert.ThrowsAsync<ArgumentException>(() =>
            service.RenderAsync(request, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("exceeds source dimensions"));
    }

    [Test]
    public async Task Per_frame_lru_cap_keeps_newest_variants_only() {
        var path = WriteFits("lru.fits", 20, 10);
        using var service = Service(new PreviewCacheOptions(
            MaxBytes: 10 * 1024 * 1024, MaxEntries: 20, MaxVariantsPerFrame: 2));
        var request = Request(path);
        var first = await service.RenderAsync(request, CancellationToken.None);
        await Task.Delay(20);
        var second = await service.RenderAsync(request with { Invert = true }, CancellationToken.None);
        await Task.Delay(20);
        await service.RenderAsync(request, CancellationToken.None); // touch first: second becomes LRU
        await Task.Delay(20);
        await service.RenderAsync(request with { MaxDimension = 7 }, CancellationToken.None);

        var cacheFiles = Directory.EnumerateFiles(_cacheRoot, "*.jpg", SearchOption.AllDirectories).ToArray();
        Assert.Multiple(() => {
            Assert.That(cacheFiles, Has.Length.EqualTo(2));
            Assert.That(cacheFiles.Any(file => file.Contains(first.Metadata.CacheKey, StringComparison.Ordinal)), Is.True);
            Assert.That(cacheFiles.Any(file => file.Contains(second.Metadata.CacheKey, StringComparison.Ordinal)), Is.False);
        });
    }

    [Test]
    public async Task Global_entry_cap_spans_multiple_frames() {
        var path = WriteFits("global-lru.fits", 20, 10);
        using var service = Service(new PreviewCacheOptions(
            MaxBytes: 10 * 1024 * 1024, MaxEntries: 2, MaxVariantsPerFrame: 20));
        var request = Request(path);
        await service.RenderAsync(request, CancellationToken.None);
        await Task.Delay(20);
        await service.RenderAsync(request with { FrameId = Guid.NewGuid() }, CancellationToken.None);
        await Task.Delay(20);
        await service.RenderAsync(request with { FrameId = Guid.NewGuid() }, CancellationToken.None);

        Assert.That(Directory.EnumerateFiles(_cacheRoot, "*.jpg", SearchOption.AllDirectories),
            Has.Exactly(2).Items);
    }

    [Test]
    public async Task Total_byte_cap_can_evict_an_oversized_entry_without_failing_response() {
        var path = WriteFits("tiny-budget.fits", 40, 30);
        using var service = Service(new PreviewCacheOptions(
            MaxBytes: 32, MaxEntries: 20, MaxVariantsPerFrame: 20));
        var result = await service.RenderAsync(Request(path), CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(result.Bytes, Is.Not.Empty);
            Assert.That(Directory.EnumerateFiles(_cacheRoot, "*.jpg", SearchOption.AllDirectories), Is.Empty);
        });
    }

    [Test]
    public async Task Concurrent_same_key_requests_decode_once() {
        var path = WriteFits("single-flight.fits", 30, 20);
        var counting = new CountingSourceFactory(_sourceFactory);
        using var service = new PreviewImageService(_cacheRoot, counting);
        var request = Request(path);

        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => service.RenderAsync(request, CancellationToken.None)));

        Assert.Multiple(() => {
            Assert.That(counting.LoadCount, Is.EqualTo(1));
            Assert.That(results.Count(result => !result.CacheHit), Is.EqualTo(1));
            Assert.That(results.Count(result => result.CacheHit), Is.EqualTo(11));
        });
    }

    [Test]
    public async Task Corrupt_cache_entry_is_deleted_and_rebuilt() {
        var path = WriteFits("corrupt-cache.fits", 20, 10);
        using var service = Service();
        var request = Request(path);
        await service.RenderAsync(request, CancellationToken.None);
        var jpegPath = Directory.EnumerateFiles(_cacheRoot, "*.jpg", SearchOption.AllDirectories).Single();
        await File.WriteAllTextAsync(jpegPath, "broken");

        var rebuilt = await service.RenderAsync(request, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(rebuilt.CacheHit, Is.False);
            Assert.That(rebuilt.Bytes[0], Is.EqualTo(0xff));
            Assert.That(rebuilt.Bytes[1], Is.EqualTo(0xd8));
        });
    }

    [Test]
    public async Task Delete_frame_entries_removes_only_target_frame_cache() {
        var firstPath = WriteFits("delete-a.fits", 20, 10, seed: 1);
        var secondPath = WriteFits("delete-b.fits", 20, 10, seed: 2);
        using var service = Service();
        var first = Request(firstPath);
        var second = Request(secondPath) with { FrameId = Guid.NewGuid() };
        await service.RenderAsync(first, CancellationToken.None);
        await service.RenderAsync(second, CancellationToken.None);

        await service.DeleteFrameEntriesAsync(first.FrameId, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(Directory.Exists(Path.Combine(_cacheRoot, first.FrameId.ToString("N"))), Is.False);
            Assert.That(Directory.EnumerateFiles(Path.Combine(_cacheRoot, second.FrameId.ToString("N")),
                "*.jpg", SearchOption.AllDirectories), Is.Not.Empty);
        });
    }

    private PreviewImageService Service(PreviewCacheOptions? options = null) =>
        new(_cacheRoot, _sourceFactory, options);

    private static PreviewRenderRequest Request(string path, int maxDimension = 2048,
            bool applyDebayer = false) => new(
        FrameId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        SourcePath: path,
        SourceChecksumSha256: null,
        Algorithm: StretchAlgorithm.AutoStf,
        Parameters: new StretchParams(),
        MaxDimension: maxDimension,
        ApplyDebayer: applyDebayer,
        ChannelMode: PreviewChannelMode.Rgb,
        Invert: false,
        Saturation: 1,
        CropX: null,
        CropY: null,
        CropWidth: null,
        CropHeight: null);

    private string WriteFits(string fileName, int width, int height,
            string? cfaPattern = null, int seed = 0) {
        var path = Path.Combine(_sourceRoot, fileName);
        var pixels = Enumerable.Range(0, width * height)
            .Select(index => (ushort)((seed + index * 977) & ushort.MaxValue))
            .ToArray();
        using var fits = FitsImage.Create(path, width, height, FitsBitDepth.UnsignedShort);
        fits.WriteImageData(pixels);
        if (cfaPattern is not null) fits.SetHeader("BAYERPAT", cfaPattern);
        fits.Complete();
        return path;
    }

    private string WriteStarFieldFits(string fileName, int width, int height) {
        var path = Path.Combine(_sourceRoot, fileName);
        var pixels = Enumerable.Repeat((ushort)1000, width * height).ToArray();
        foreach (var (cx, cy) in new[] { (20, 20), (60, 25), (95, 40), (35, 65), (80, 70) }) {
            for (var y = cy - 2; y <= cy + 2; y++) {
                for (var x = cx - 2; x <= cx + 2; x++) {
                    var distance = Math.Abs(x - cx) + Math.Abs(y - cy);
                    pixels[y * width + x] = (ushort)(30000 - distance * 3500);
                }
            }
        }
        using var fits = FitsImage.Create(path, width, height, FitsBitDepth.UnsignedShort);
        fits.WriteImageData(pixels);
        fits.Complete();
        return path;
    }

    private sealed class CountingSourceFactory : ISourceImageDataFactory {
        private readonly ISourceImageDataFactory _inner;
        private int _loadCount;

        public CountingSourceFactory(ISourceImageDataFactory inner) => _inner = inner;

        public int LoadCount => Volatile.Read(ref _loadCount);
        public string DecoderCacheIdentity => _inner.DecoderCacheIdentity;

        public async Task<SourceImageData> LoadAsync(string path, CancellationToken ct) {
            Interlocked.Increment(ref _loadCount);
            await Task.Delay(30, ct);
            return await _inner.LoadAsync(path, ct);
        }

        public OpenAstroAra.Image.Interfaces.IImageData CreateImageData(
                SourceImageData source, IProfileService? profileService = null) =>
            _inner.CreateImageData(source, profileService);
    }

    private sealed class IdentitySourceFactory(
            ISourceImageDataFactory inner, string identity) : ISourceImageDataFactory {
        public string DecoderCacheIdentity => identity;

        public Task<SourceImageData> LoadAsync(string path, CancellationToken ct) =>
            inner.LoadAsync(path, ct);

        public OpenAstroAra.Image.Interfaces.IImageData CreateImageData(
                SourceImageData source, IProfileService? profileService = null) =>
            inner.CreateImageData(source, profileService);
    }
}