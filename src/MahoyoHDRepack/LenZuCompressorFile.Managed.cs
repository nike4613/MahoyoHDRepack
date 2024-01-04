using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using CommunityToolkit.Diagnostics;

namespace MahoyoHDRepack
{
    internal partial class LenZuCompressorFile
    {
        /// <summary>
        /// Reads an unaligned (big-endian) set of bits from the byte-aligned bitstream <paramref name="span"/>, with the bit with index <paramref name="bitIndex"/>
        /// being the highest bit in the result.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Consider the following example bytes (where each letter represents a single bit in the bitstream) (all examples assume <c><paramref name="bitCount"/> = 8</c>):
        /// 
        /// <code>
        /// 7......0 7......0
        /// abcdefgh ijklmnop
        /// </code>
        /// 
        /// If we chose <c><paramref name="bitIndex"/> = 7</c>, that would mean that bit 7 in the first byte is the first bit to include in the result. As such,
        /// this selects the whole first byte as its output:
        /// 
        /// <code>
        /// 7......0 7......0
        /// abcdefgh ijklmnop
        /// --------
        /// 
        /// Output: abcdefgh
        /// </code>
        /// 
        /// If we chose <c><paramref name="bitIndex"/> = 0</c>, that would mean that bit 0 in the first byte is the first bit to include in the result, and we have
        /// this:
        /// 
        /// <code>
        /// 7......0 7......0
        /// abcdefgh ijklmnop
        ///        --------
        /// 
        /// Output: hijklmno
        /// </code>
        /// 
        /// Note how no matter what, the next byte at that alignment starts in the following aligned byte, hence the pointer-adjust always being 1, at least when <paramref name="bitCount"/> is 8.
        /// 
        /// </para>
        /// <para>I find the choice of 7 to mean byte-aligned to be odd, but this is what the original code does. ¯\_(ツ)_/¯</para>
        /// <para>
        /// The original implementation does not seem to be correct in the face of <c><paramref name="bitCount"/> = 16</c> and <c><paramref name="bitIndex"/> = 7</c>, or any other combination which
        /// causes this to consume all of the bits in the second byte read. In this case, the byte-advance will be 1, which is incorrect; it should be 2. This implementation does handle this case 
        /// correctly.
        /// </para>
        /// </remarks>
        /// <param name="span">The data to read from.</param>
        /// <param name="highestBit">The index of the highest bit to read from in the bitstream.</param>
        /// <param name="bitCount">The number of bits to read.</param>
        /// <returns>A tuple containing both the result and the pointer-adjust.</returns>
        private static (int Value, int ByteOffset) ReadUnalignedBitsFromSpan(ReadOnlySpan<byte> span, int highestBit, int bitCount = 8)
        {
            Guard.IsInRange(highestBit, 0, 8);
            Guard.IsInRange(bitCount, 0, 17);
            Guard.IsLessThanOrEqualTo(7 - highestBit + bitCount, 16, nameof(bitCount));
            Guard.HasSizeGreaterThanOrEqualTo(span, 1);

            var maskedFirst = (0xff >> (7 - highestBit)) & span[0];

            var offset = highestBit - bitCount + 1;
            if (offset < 1)
            {
                if (offset + 8 >= 8)
                {
                    // don't need to read the next byte
                    return (maskedFirst << -offset, 1);
                }
                else
                {
                    Guard.HasSizeGreaterThanOrEqualTo(span, 2);
                    // do need to read the next byte
                    var result = (span[1] >> (offset + 8)) | (maskedFirst << -offset);
                    return (result, offset == -8 ? 2 : 1);
                }
            }
            else
            {
                return (maskedFirst >> offset, 0);
            }
        }

        private ref struct Bitstream
        {
            private readonly ReadOnlySpan<byte> bytes;
            private int byteOffset;
            private int bitIndex; // 7->0

            public bool IsAtEnd => byteOffset >= bytes.Length;

            public Bitstream(ReadOnlySpan<byte> bytes)
            {
                this.bytes = bytes;
                byteOffset = 0;
                bitIndex = 7;
            }

            // TODO: this implementation can be significantly optimized

            private void NormalizeOffsets()
            {
                while (bitIndex < 0)
                {
                    bitIndex += 8;
                    byteOffset++;
                }
                while (bitIndex >= 8)
                {
                    bitIndex -= 8;
                    byteOffset--;
                }
            }

            public bool ReadBit()
            {
                var result = (bytes[byteOffset] & (1 << bitIndex)) != 0;
                bitIndex--; // decrementing bit index advances forward
                NormalizeOffsets();
                return result;
            }

            public int ReadBigEndian(int bits)
            {
                var (result, _) = ReadUnalignedBitsFromSpan(bytes.Slice(byteOffset), bitIndex, bits);
                bitIndex -= bits;
                NormalizeOffsets();
                return result;
            }

            public int ReadLittleEndianWithRoundedBits(int bits)
            {
                var noBytes = ((bits - 1) / 8) + 1;
                Guard.IsInRangeFor(byteOffset + noBytes, bytes, nameof(bits));
                var result = 0;
                for (var i = 0; i < noBytes; i++)
                {
                    result |= ReadBigEndian(8) << (i * 8);
                }
                return result;
            }
        }

        private static byte[] DecompressFile(ReadOnlySpan<byte> compressedFile, bool assertChecksum)
        {
            Helpers.DAssert(compressedFile.Slice(0, HeaderSize).SequenceEqual(ExpectHeader));

            var bodyData = compressedFile.Slice(HeaderSize);

            var decompressedLength = MemoryMarshal.Read<LEUInt32>(bodyData).Value;
            var checksumHi = MemoryMarshal.Read<LEUInt32>(bodyData[4..]).Value;
            var checksumLo = MemoryMarshal.Read<LEUInt32>(bodyData[8..]).Value;
            // last 4 here is unused....

            var checksum = ((ulong)checksumHi << 32) | checksumLo;

            bodyData = bodyData[16..];

            // next, we read compressor options
            var options = ReadCompressorOptions(bodyData);
            bodyData = bodyData[6..]; // compressor options are 6 bytes;

            var bitstream = new Bitstream(bodyData);

            var table = ReadHuffmanTable(options, ref bitstream);

            var resultData = new byte[decompressedLength];
            var finalSize = DecompressCore(options, resultData, ref bitstream, table);
            if (finalSize != decompressedLength)
            {
                resultData = resultData.AsSpan(0, finalSize).ToArray();
            }

            if (assertChecksum)
            {
                var computedChecksum = ComputeChecksum(resultData);
                if (checksum != computedChecksum)
                {
                    ThrowHelper.ThrowInvalidDataException($"Checksum did not match (expected {checksum:x16}, got {computedChecksum:x16})");
                }
            }

            return resultData;
        }

        private static ulong ComputeChecksum(ReadOnlySpan<byte> data)
            => ComputeChecksumWithSeed(0, 0, data);

        private static ulong ComputeChecksumWithSeed(ulong seed, int iOffset, ReadOnlySpan<byte> data)
        {
            ReadOnlySpan<int> ChecksumLut = [0xe9, 0x115, 0x137, 0x1b1];
            var checksum = seed;
            for (var i = 0; i < data.Length; i++)
            {
                checksum = (checksum + data[i]) * (ulong)ChecksumLut[(i + iOffset) & 3];
            }
            return checksum;
        }

        private struct CompressorOptions
        {
            public int HuffTableBitCount;
            public int BackrefLowBitCount;
            public int BackrefBaseDistance;

            public readonly int HuffTableMaxEntryCount
            {
                get
                {
                    var value = 1 << HuffTableBitCount;
                    return (value + 1) * value / 2;
                }
            }

            public readonly int HuffTableBitMask => (1 << HuffTableBitCount) - 1;
        }

        private static CompressorOptions ReadCompressorOptions(ReadOnlySpan<byte> data)
        {
            Guard.HasSizeGreaterThanOrEqualTo(data, 6);

            // byte 0 is unused
            var huffTableBitCountRaw = data[1];
            var huffTableBitCountMin = data[2];
            var backrefLowBitCountXUpper = data[3];
            var backrefLowBitCount = data[4];
            var backrefBaseDistance = data[5];

            Guard.IsInRange(huffTableBitCountRaw, 3u, 16u);
            Guard.IsInRange(huffTableBitCountMin, 3u, 16u);
            var huffTableBitCount = int.Max(huffTableBitCountRaw, huffTableBitCountMin);
            Guard.IsGreaterThanOrEqualTo(backrefLowBitCountXUpper, huffTableBitCountMin);
            Guard.IsLessThan(backrefLowBitCountXUpper, 16u);
            Guard.IsInRange(backrefLowBitCount, 0u, backrefLowBitCountXUpper);
            Guard.IsGreaterThanOrEqualTo(huffTableBitCount, backrefLowBitCountXUpper - backrefLowBitCount);
            Guard.IsInRange(backrefBaseDistance, 2u, 9u);

            return new()
            {
                HuffTableBitCount = huffTableBitCount,
                BackrefLowBitCount = backrefLowBitCount,
                BackrefBaseDistance = backrefBaseDistance,
            };
        }

        private struct HuffmanTableEntry
        {
            public int ConstructorValue;
            public int Child1;
            public int Child2;
            public sbyte BitValue;
        }

        private ref struct HuffmanTable(ReadOnlySpan<HuffmanTableEntry> table, int startEntry, int firstReal)
        {
            public readonly ReadOnlySpan<HuffmanTableEntry> Table = table;
            public readonly int StartEntry = startEntry;
            public readonly int FirstRealEntry = firstReal;
        }

        private static HuffmanTable ReadHuffmanTable(in CompressorOptions options, ref Bitstream bitstream)
        {
            var table = new HuffmanTableEntry[options.HuffTableMaxEntryCount];
            table.AsSpan().Fill(new()
            {
                BitValue = -1,
                Child1 = -1,
                Child2 = -1,
                ConstructorValue = 0
            });

            var firstRealEntry = options.HuffTableBitMask + 1;
            var maxBytesForHuffTableBits = (options.HuffTableBitCount + 7) / 8;

            var entriesToFill = bitstream.ReadLittleEndianWithRoundedBits(maxBytesForHuffTableBits);
            if (entriesToFill is 0)
            {
                entriesToFill = firstRealEntry;
            }

            var readTableIndex = true;
            if (firstRealEntry * 4 < (maxBytesForHuffTableBits + 4) * entriesToFill)
            {
                readTableIndex = false;
                entriesToFill = firstRealEntry;
            }

            // now we can actually read the initial table entries
            for (var i = 0; i < entriesToFill; i++)
            {
                var tableIdx = i;
                if (readTableIndex)
                {
                    tableIdx = bitstream.ReadLittleEndianWithRoundedBits(maxBytesForHuffTableBits);
                }
                table[tableIdx].ConstructorValue = bitstream.ReadLittleEndianWithRoundedBits(32);
            }

            // now, we build up the rest of the table
            for (var current = firstRealEntry; current < table.Length; current++)
            {
                var idxOfSmallest = -1;
                var idxOfSecondSmallest = -1;
                var smallestValue = int.MaxValue;
                var sndSmallestValue = int.MaxValue;

                for (var i = 0; i < current; i++)
                {
                    var value = table[i].ConstructorValue;
                    if (value is not 0 && table[i].BitValue == -1)
                    {
                        if (sndSmallestValue > value)
                        {
                            sndSmallestValue = value;
                            idxOfSecondSmallest = i;
                        }

                        if (smallestValue > value)
                        {
                            idxOfSecondSmallest = idxOfSmallest;
                            sndSmallestValue = smallestValue;
                            smallestValue = value;
                            idxOfSmallest = i;
                        }
                    }
                }

                if (idxOfSmallest < 0 || idxOfSecondSmallest < 0)
                {
                    if (idxOfSmallest >= 0 && current <= firstRealEntry)
                    {
                        table[idxOfSmallest].BitValue = 1;
                    }
                    Helpers.Assert(current is not 0);
                    return new(table, current, firstRealEntry);
                }

                table[current].ConstructorValue = table[idxOfSecondSmallest].ConstructorValue + table[idxOfSmallest].ConstructorValue;
                table[current].Child2 = idxOfSmallest;
                table[current].Child1 = idxOfSecondSmallest;
                table[idxOfSmallest].BitValue = 1;
                table[idxOfSecondSmallest].BitValue = 0;
            }

            Debugger.Break();
            return new(table, table.Length - 1, firstRealEntry);
        }

        private static int DecompressCore(in CompressorOptions options, Span<byte> decompressed, ref Bitstream bitstream, scoped HuffmanTable table)
        {
            var decompressedOffset = 0;
            while (decompressedOffset < decompressed.Length && !bitstream.IsAtEnd)
            {
                var isBackref = bitstream.ReadBit();
                var length = DecodeHuffmanSequence(ref bitstream, table);
                Helpers.Assert(length >= 0);

                if (isBackref)
                {
                    // backreference, read a distance
                    length += options.BackrefBaseDistance;

                    var distanceHighBits = DecodeHuffmanSequence(ref bitstream, table);
                    Helpers.Assert(distanceHighBits >= 0);

                    var distanceLowBits = 0;
                    if (options.BackrefLowBitCount > 0)
                    {
                        distanceLowBits = bitstream.ReadBigEndian(options.BackrefLowBitCount);
                    }

                    var distance = distanceLowBits | (distanceHighBits << options.BackrefLowBitCount);
                    distance += options.BackrefBaseDistance;
                    for (var i = 0; i < length && decompressedOffset < decompressed.Length; i++)
                    {
                        decompressed[decompressedOffset] = decompressed[decompressedOffset - distance];
                        decompressedOffset++;
                    }
                }
                else
                {
                    // literal, read and write out that many bytes + 1
                    for (var i = 0; i < length + 1 && decompressedOffset < decompressed.Length; i++)
                    {
                        decompressed[decompressedOffset++] = (byte)bitstream.ReadBigEndian(8);
                    }
                }
            }

            return decompressedOffset;
        }

        private static int DecodeHuffmanSequence(ref Bitstream bitstream, scoped HuffmanTable table)
        {
            if (table.FirstRealEntry < table.StartEntry)
            {
                Helpers.Assert(table.FirstRealEntry > 0 && table.Table.Length > 0);
                var tableEntry = table.StartEntry - 1;
                while (tableEntry >= table.FirstRealEntry)
                {
                    var bit = bitstream.ReadBit() ? 1 : 0;

                    var c1 = table.Table[tableEntry].Child1;
                    var c2 = table.Table[tableEntry].Child2;

                    tableEntry =
                        table.Table[c1].BitValue == bit ? c1 :
                        table.Table[c2].BitValue == bit ? c2 :
                        ThrowHelper.ThrowInvalidOperationException<int>();
                }
                return tableEntry;
            }

            for (var i = table.StartEntry - 1; i >= 0; i--)
            {
                if (table.Table[i].ConstructorValue != 0)
                {
                    // this is the entry we care about
                    // always consume a bit
                    _ = bitstream.ReadBit();
                    return i;
                }
            }

            return ThrowHelper.ThrowInvalidOperationException<int>();
        }
    }
}
