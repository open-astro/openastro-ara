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
using NUnit.Framework;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>Direct Alpaca ImageBytes decode: metadata parsing, the [x,y]→raster transpose, and refusal paths.</summary>
    [TestFixture]
    public class AlpacaImageBytesTest {

        private static byte[] Body(int width, int height, int transmissionType, byte[] payload,
                int metadataVersion = 1, int errorNumber = 0, int rank = 2, int dataStart = 44) {
            var body = new byte[44 + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(0), metadataVersion);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), errorNumber);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(16), dataStart);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(20), 2 /* image element type Int32 */);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(24), transmissionType);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(28), rank);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(32), width);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(36), height);
            payload.CopyTo(body.AsSpan(44));
            return body;
        }

        [Test]
        public void Uint16_payload_transposes_xy_into_raster_order() {
            // 3 wide × 2 tall; payload is Alpaca [x,y] order (y contiguous per column):
            // column x=0: (0,0)=1 (0,1)=4 · x=1: 2,5 · x=2: 3,6
            var payload = new byte[12];
            ushort[] wire = [1, 4, 2, 5, 3, 6];
            for (var i = 0; i < wire.Length; i++) {
                BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i * 2), wire[i]);
            }
            var (pixels, w, h) = AlpacaImageBytes.Decode(Body(3, 2, 8, payload));
            Assert.That((w, h), Is.EqualTo((3, 2)));
            Assert.That(pixels, Is.EqualTo(new ushort[] { 1, 2, 3, 4, 5, 6 }),
                "row-major raster: row 0 = (0,0)(1,0)(2,0)");
        }

        [Test]
        public void Int32_payload_clamps_into_ushort() {
            var payload = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0), -5);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), 70000);
            var (pixels, _, _) = AlpacaImageBytes.Decode(Body(1, 2, 2, payload));
            Assert.That(pixels, Is.EqualTo(new ushort[] { 0, ushort.MaxValue }));
        }

        [Test]
        public void Refusals_are_loud() {
            Assert.Throws<InvalidOperationException>(() => AlpacaImageBytes.Decode(new byte[10]),
                "truncated metadata");
            Assert.Throws<InvalidOperationException>(
                () => AlpacaImageBytes.Decode(Body(1, 1, 8, new byte[2], metadataVersion: 2)),
                "unknown metadata version");
            Assert.Throws<InvalidOperationException>(
                () => AlpacaImageBytes.Decode(Body(1, 1, 8, new byte[2], errorNumber: 1024)),
                "device error must not decode as pixels");
            Assert.Throws<InvalidOperationException>(
                () => AlpacaImageBytes.Decode(Body(1, 1, 8, new byte[2], rank: 3)),
                "rank 3 unsupported in v1");
            Assert.Throws<InvalidOperationException>(
                () => AlpacaImageBytes.Decode(Body(2, 2, 8, new byte[2])),
                "payload shorter than geometry");
        }
    }
}
