﻿using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MahoyoHDRepack
{
    internal partial class LenZuCompressorFile
    {
        private struct LzHeaderData
        {
            public int HuffTableBitCountRaw; // [3..15]
            public int HuffTableBitCountMin; // [3..15]
            public int BackrefLowBitCountExclusiveUpperBound; // [HuffTableBitCountMin..15]
            public int BackrefLowBitCount; // [0..above field], BackrefLowBitCountExclusiveUpperBound - BackrefLowBitCount <= HuffTableBitCount
            public int BackrefBaseDistance; // [2..8]
            public int HuffTableBitCount; // max(HuffTableBitCountRaw, HuffTableBitCountMin)
            public uint Unused; // unused
        }

#pragma warning disable IDE1006 // Naming Styles
        private static unsafe class Attempt2
        {
            public struct NativeSpan
            {
                public byte* Data;
                public int Length;
                public int Padding;
                public ulong Checksum;
            }

            public static int lz_decompress(NativeSpan* outSpan, NativeSpan* compressedSpan)
            {
                outSpan->Data = null;

                LzHeaderData lz_data = default;
                var subByteAlignment = 7;
                var compressedData = compressedSpan;
                var decompressedData = outSpan;
                var readBytes = lz_read_header(compressedSpan);
                if (readBytes < 0)
                {
                    return -1;
                }

                var lenBytesRead = lz_read_int(out var fileLen, compressedSpan, readBytes, 7, 0x20);
                if (outSpan is not null)
                {
                    // NOTE: this is different from the original, we alloc here to avoid re-reading everything
                    outSpan->Data = (byte*)NativeMemory.Alloc(fileLen);
                    outSpan->Length = (int)fileLen;

                    lenBytesRead += readBytes;
                    var offset = lenBytesRead + lz_read_int(out var checksumHi, compressedSpan, lenBytesRead, 7, 0x20);

                    offset += lz_read_int(out var checksumLo, compressedSpan, offset, 7, 0x20);

                    var checksum = CONCAT44(checksumHi, checksumLo);
                    offset += lz_read_int(out _, compressedSpan, offset, 7, 0x20);

                    lenBytesRead = lz_read_early_data(&lz_data, compressedSpan, offset, 7, &subByteAlignment);
                    var baseIdx = lenBytesRead + offset;
                    if (baseIdx < 1)
                    {
                        return -2;
                    }

                    var tableEntryCount = GetTableSizeFromBitCount(lz_data.HuffTableBitCount);
                    var tableSize = (nuint)((nint)sizeof(HuffmanTableEntry) * tableEntryCount);
                    var table = (HuffmanTableEntry*)NativeMemory.Alloc(tableSize);
                    if (table is null)
                    {
                        return -9;
                    }

                    if (tableEntryCount != 0)
                    {
                        // fill the table with the default values
                        for (var i = 0; i < tableEntryCount; i++)
                        {
                            table[i].TableConstructOrderVal = 0;
                            table[i].Child2 = uint.MaxValue;
                            table[i].Child1 = uint.MaxValue;
                            table[i].BitValue = 0xff;
                        }

                        if (tableEntryCount != 0)
                        {
                            // now we want to read the initial table
                            var firstRealTableEntry = (uint)GetMaskFromBitCount(lz_data.HuffTableBitCount) + 1;
                            var huffEntryByteCountRoundedUp = (lz_data.HuffTableBitCount + 7) / 8;

                            lenBytesRead = lz_read_int(out var numEntriesToFill, compressedData, baseIdx, subByteAlignment, huffEntryByteCountRoundedUp);
                            if (numEntriesToFill == 0)
                            {
                                numEntriesToFill = firstRealTableEntry;
                            }

                            // read some table entries
                            var x = 1;
                            if (firstRealTableEntry * 4 < (huffEntryByteCountRoundedUp + 4) * numEntriesToFill)
                            {
                                x = -1;
                                numEntriesToFill = firstRealTableEntry;
                            }

                            for (var index = 0u; index < numEntriesToFill; index++)
                            {
                                var tableIndex = index;
                                if (x > 0)
                                {
                                    lenBytesRead += lz_read_int(out tableIndex, compressedData, baseIdx + lenBytesRead, subByteAlignment, huffEntryByteCountRoundedUp);
                                }
                                lenBytesRead += lz_read_int(out table[tableIndex].TableConstructOrderVal, compressedSpan, baseIdx + lenBytesRead, subByteAlignment, 0x20);
                            }

                            baseIdx += lenBytesRead;

                            // build up the rest of the table
                            // the algorithm is kinda strange: in the file, is a list of 32-bit integers. This list populates the lowest index entries 'order' fields.
                            // In each step of the algorithm, one more element is populated, above those specified in the file. It's children are the 2 smallest-valued
                            // (but nonzero) entries which are not already children of another entry. The smallest-valued entry corresponds to the 1 bit, and the second
                            // smallest corresponds with the 0 bit. Each new entry has a value which is the sum This process stops when there is only one unassigned element left.
                            var currentEntry = firstRealTableEntry;
                            uint idxOfSmallest, smallest, sndSmallest, entry_0;
                            while (true)
                            {
                                idxOfSmallest = uint.MaxValue;
                                smallest = uint.MaxValue >> 4;
                                sndSmallest = uint.MaxValue >> 4;

                                var idxOfSndSmallest = uint.MaxValue;
                                var curEntry = table;
                                var counter = 0u;
                                var iterVal = 0u;

                                if (currentEntry == 0) break;
                                do
                                {
                                    entry_0 = curEntry->TableConstructOrderVal;
                                    if ((entry_0 != 0) && (curEntry->BitValue == 0xff))
                                    {
                                        // the entry has not had its bitvalue initialized, but is important
                                        counter++;

                                        if (sndSmallest > entry_0)
                                        {
                                            sndSmallest = entry_0;
                                            idxOfSndSmallest = iterVal;
                                        }

                                        if (smallest > entry_0)
                                        {
                                            idxOfSndSmallest = idxOfSmallest;
                                            sndSmallest = smallest;
                                            smallest = entry_0;
                                            idxOfSmallest = iterVal;
                                        }
                                    }

                                    curEntry++;
                                    iterVal++;
                                }
                                while (iterVal < currentEntry);
                                if (counter < 2) break;

                                table[currentEntry].TableConstructOrderVal = table[idxOfSndSmallest].TableConstructOrderVal + table[idxOfSmallest].TableConstructOrderVal;
                                table[currentEntry].Child2 = idxOfSmallest;
                                table[currentEntry].Child1 = idxOfSndSmallest;
                                table[idxOfSmallest].BitValue = 1;
                                table[idxOfSndSmallest].BitValue = 0;

                                currentEntry += 1;
                            }
                            if (currentEntry <= firstRealTableEntry)
                            {
                                table[idxOfSmallest].BitValue = 1;
                            }
                            if (currentEntry != 0)
                            {
                                var pSVar2 = decompressedData;
                                var dataCopy = lz_data;
                                var finalLength = DecompressData(&dataCopy, decompressedData, compressedData, baseIdx, subByteAlignment, fileLen, table, currentEntry);
                                if (0 < finalLength)
                                {
                                    NativeMemory.Free(table);
                                    var l3 = ComputeChecksum(pSVar2);
                                    if (l3 != checksum)
                                    {
                                        return -10;
                                    }
                                    return finalLength;
                                }
                            }
                        }
                    }
                }
                return -3;
            }

            private static ulong ComputeChecksum(NativeSpan* dataSpan)
            {
                ReadOnlySpan<int> lut = [0xe9, 0x115, 0x137, 0x1b1];
                ulong l3 = 0;
                ulong a6;
                ulong _0 = 0;
                var data = dataSpan->Data;
                if (dataSpan->Length != 0)
                {
                    do
                    {
                        var incr = (uint)_0;
                        a6 = incr + 1;
                        _0 = a6;
                        l3 = (ulong)((long)(l3 + *data) * lut[(int)(incr & 3)]);
                        data++;
                    }
                    while (a6 < (uint)dataSpan->Length);
                }
                dataSpan->Checksum = l3;
                return l3;
            }

            private static ulong CONCAT44(uint hi, uint lo) => ((ulong)hi << 32) | lo;

            public readonly struct ReadByteFromBitOffsetResult(ushort result, short adjust)
            {
                public readonly ushort Result = result;
                public readonly short Adjust = adjust;

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public void Deconstruct(out ushort result, out short adjust)
                {
                    result = Result;
                    adjust = Adjust;
                }
            }

            /// <summary>
            /// Reads an unaligned (big-endian) set of bits from the byte-aligned bitstream <paramref name="data"/>, with the bit with index <paramref name="bitIndex"/>
            /// being the highest bit in the result.
            /// </summary>
            /// <remarks>
            /// <para>Note: this is NOT safe in the face <c><paramref name="data"/> + 1</c> being an invalid page. This implementation is compied basically
            /// verbatim from the decomp.</para>
            /// <para>
            /// 
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
            /// <param name="data">A pointer to the data to read from.</param>
            /// <param name="bitIndex">The bit index to read at.</param>
            /// <param name="bitCount">The number of bits to read.</param>
            /// <returns>A <see cref="ReadByteFromBitOffsetResult"/> containing both the result and the pointer-adjust.</returns>
            private static ReadByteFromBitOffsetResult ReadUnalignedBitsStartingAtIndex(byte* data, int bitIndex, int bitCount = 8)
            {
                Helpers.Assert(bitIndex is >= 0 and < 8);
                Helpers.Assert(bitCount is >= 0 and <= 16);
                Helpers.Assert(7 - bitIndex + bitCount <= 16);

                var maskedFirst = (byte)((byte)(0xff >> (7 - bitIndex)) & *data);

                var bitOffs = bitIndex - bitCount + 1;

                if (bitOffs < 1)
                {
                    var result = (data[1] >> (bitOffs + 8)) | (maskedFirst << (-bitOffs));
                    return new((ushort)result, (short)(bitOffs == -8 ? 2 : 1));
                }
                else
                {
                    var result = maskedFirst >> bitOffs;
                    return new((ushort)result, 0);
                }
            }

            private static int lz_read_int(out uint result, NativeSpan* dataSpan, int baseIdx, int subByteAlignment, int bitsToRead)
            {
                var pCur = dataSpan->Data + baseIdx;
                var idx = 0;
                var reads = 0;
                result = 0;
                var bytesToRead = ((bitsToRead - 1) / 8) + 1;
                if (0 < bytesToRead)
                {
                    var len = dataSpan->Length;
                    byte baseBitNo = 0;
                    do
                    {
                        if (len <= idx + baseIdx) break;
                        (var resultVal, var moveAmt) = ReadUnalignedBitsStartingAtIndex(pCur, subByteAlignment);
                        idx += moveAmt;
                        result |= (uint)resultVal << baseBitNo;
                        reads += 1;
                        baseBitNo += 8;
                        pCur += moveAmt;
                    }
                    while (reads < bytesToRead);
                }

                if (reads != bytesToRead)
                {
                    idx = -idx;
                }
                return idx;
            }

            private static int lz_read_early_data(LzHeaderData* lz_data, NativeSpan* compressedSpan, int offset, int subByteAlignmentUnused, int* reusltSubByteAlignment)
            {
                var pData = compressedSpan->Data + offset;

                lz_data->Unused = pData[0];
                lz_data->HuffTableBitCountRaw = pData[1];
                lz_data->HuffTableBitCountMin = pData[2];
                lz_data->BackrefLowBitCountExclusiveUpperBound = pData[3];
                lz_data->BackrefLowBitCount = pData[4];
                lz_data->BackrefBaseDistance = pData[5];

                *reusltSubByteAlignment = subByteAlignmentUnused;

                if (!lz_adjust_data(lz_data))
                {
                    return -6;
                }
                else
                {
                    return 6;
                }
            }

            private static bool lz_adjust_data(LzHeaderData* data)
            {
                var huffTableBitCount = data->HuffTableBitCountRaw;
                if (huffTableBitCount <= 2)
                {
                    data->HuffTableBitCountRaw = huffTableBitCount = 3;
                    return false;
                }
                if (huffTableBitCount >= 16)
                {
                    data->HuffTableBitCountRaw = huffTableBitCount = 0xf;
                    return false;
                }

                var huffTableBitCountMin = data->HuffTableBitCountMin;
                if (huffTableBitCountMin <= 2)
                {
                    data->HuffTableBitCountMin = huffTableBitCountMin = 3;
                    return false;
                }
                if (huffTableBitCountMin >= 16)
                {
                    data->HuffTableBitCountMin = huffTableBitCountMin = 0xf;
                    return false;
                }

                data->HuffTableBitCount = huffTableBitCount = int.Max(huffTableBitCount, huffTableBitCountMin);

                var backrefLowXUpperRaw = data->BackrefLowBitCountExclusiveUpperBound;
                var backrefLowBitCountExclusiveUpperBound = int.Max(huffTableBitCountMin, backrefLowXUpperRaw);
                data->BackrefLowBitCountExclusiveUpperBound = backrefLowBitCountExclusiveUpperBound;

                if (backrefLowBitCountExclusiveUpperBound >= 16)
                {
                    data->BackrefLowBitCountExclusiveUpperBound = backrefLowBitCountExclusiveUpperBound = 0xf;
                    return false;
                }

                var backrefLowBitCount = data->BackrefLowBitCount;
                if (backrefLowBitCount < 0)
                {
                    data->BackrefLowBitCount = backrefLowBitCount = 0;
                    return false;
                }
                if (backrefLowBitCount >= backrefLowBitCountExclusiveUpperBound)
                {
                    data->BackrefLowBitCount = backrefLowBitCount = backrefLowBitCountExclusiveUpperBound - 1;
                    return false;
                }

                if (backrefLowBitCountExclusiveUpperBound - backrefLowBitCount > huffTableBitCount)
                {
                    data->BackrefLowBitCount = backrefLowBitCountExclusiveUpperBound - huffTableBitCount;
                    return false;
                }

                if (data->BackrefBaseDistance < 2)
                {
                    data->BackrefBaseDistance = 2;
                    return false;
                }
                if (8 < data->BackrefBaseDistance)
                {
                    data->BackrefBaseDistance = 8;
                    return false;
                }

                return huffTableBitCountMin <= backrefLowXUpperRaw;
            }

            private static int lz_read_header(NativeSpan* pDataPtr)
            {
                ulong uVar10, uVar5, uVar9, uVar13;
                long lVar2, lVar6, lVar11, lVar7;
                uint uVar12;
                var auStack_la8 = stackalloc byte[32];
                var local_188 = "LenZuCompressor\0"u8;
                var local_f8 = stackalloc byte[16];
                var acStack_e8 = stackalloc byte[128];
                var local_68 = stackalloc uint[4];
                var fst16ofData = stackalloc byte[16];

                var pData = pDataPtr->Data;
                uVar10 = 0;
                Buffer.MemoryCopy(pData, fst16ofData, 16, 16);
                uVar9 = uVar10;
                uVar13 = uVar10;
                do
                {
                    uVar5 = uVar10;
                    if (fst16ofData[uVar9] != local_188[(int)uVar9])
                    {
                        if ((int)uVar13 < 0x10)
                        {
                            return -1;
                        }
                        break;
                    }
                    uVar9 += 1;
                    uVar12 = (uint)((int)uVar13 + 1);
                    uVar13 = uVar12;
                }
                while ((int)uVar12 < 0x10);
                do
                {
                    Helpers.Assert(!(0x1f < uVar5));
                    uVar5 += 1;
                }
                while ((long)uVar5 < 0x20);
                Buffer.MemoryCopy(pData, local_f8, 16, 16);
                uVar9 = uVar10;
                do
                {
                    uVar12 = (uint)uVar9;
                    if (local_f8[uVar10] != local_188[(int)uVar10])
                    {
                        if ((int)uVar12 < 0x10) return 0x20;
                        break;
                    }
                    uVar10 += 1;
                    uVar12 += 1;
                    uVar9 = uVar12;
                }
                while ((int)uVar12 < 0x10);
                if (-1 < (int)uVar12)
                {
                    local_68[2] = *(uint*)(pData + 0x10);
                    local_68[0] = *(uint*)(pData + 0x14);
                    local_68[3] = 0;
                    local_68[1] = 0;
                    lVar7 = -1;
                    do
                    {
                        lVar11 = lVar7 + 1;
                        lVar2 = lVar7 + 1;
                        lVar7 = lVar11;
                    }
                    while (*(byte*)((long)local_68 + lVar2) != 0);
                    lVar7 = -1;
                    do
                    {
                        lVar6 = lVar7 + 1;
                        lVar2 = lVar7 + 9;
                        lVar7 = lVar6;
                    }
                    while (*(byte*)((long)local_68 + lVar2) != 0);
                    Buffer.MemoryCopy(local_68, fst16ofData + ((int)lVar6 + 1), lVar11 + 1, lVar11 + 1);
                }
                return 0x20;
            }

            private static int WrapAt8(int i) => ((i % 8) + 8) % 8;

            /// <summary>
            /// Decodes the compressed data.
            /// </summary>
            /// <remarks>
            /// <para>
            /// The compressed data consists of a sequence of 'instructions', where each 'instruction' encodes BOTH a backreference AND a literal. All instructions
            /// are compactly encoded, using as few bits as possible in the bitstream.
            /// </para>
            /// <para>
            /// The compressed datastream is a stream of <em>bits</em>, not a stream of bytes. As such, the decompression code has to be very careful
            /// to always keep track of which bit in the current byte is being referred to. (Note that for some reason the bits in each byte are treated as
            /// big-endian order, so the first bit in each byte is bit 7 (the high bit), and the last is bit 0 (the low bit).)
            /// </para>
            /// <para>
            /// The first bit in each instruction indicates whether or nor this instruction is a backreference. If the bit is 1, it does, and if the bit is 0, it
            /// doesn't. The following bits are a Huffman-coded sequence representing the a value we'll call X. X serves two purposes: it is the number of bytes
            /// to copy from earlier in the output stream, AND the 1-based number of literal bytes in the compressed stream. Note that is a backreference is used,
            /// a literal is NOT used.
            /// </para>
            /// <para>
            /// If this instruction encodes a backreference, X is incremented by the header value <see cref="LzHeaderData.BackrefBaseDistance"/> (which is the 6th byte after
            /// the main header). The next bits are another Huffman sequence (from the same code!) which encode the high bits of the backreference distance offset. The next
            /// <see cref="LzHeaderData.BackrefLowBitCount"/> bits are a normally-encoded big-endian integer (no more than 16 bits!) which are the low bits of the backref
            /// distance offset. These values are concatenated, then added to <see cref="LzHeaderData.BackrefBaseDistance"/> to compute the actual distance. Each byte is
            /// copied one-by-one, so the new data can overlap the backreferenced data (as is common in LZ77 algorithms) Then, the next X + 1 octets (8 bit bytes, at whatever
            /// alignment in the bitstream the encoder happens to be in at this point) are copied verbatim to the output, as a literal. The instruction is now completed.
            /// </para>
            /// <para>
            /// The parameters to this function mostly describe where in the bitstream and Huffman table to start. While there are a lot of them, they're mostly uninteresting.
            /// </para>
            /// </remarks>
            /// <param name="headerData"></param>
            /// <param name="decompressedData"></param>
            /// <param name="compressedData"></param>
            /// <param name="baseIdx"></param>
            /// <param name="prevBitPos"></param>
            /// <param name="fileLen"></param>
            /// <param name="huffmanTable"></param>
            /// <param name="startEntry"></param>
            /// <returns></returns>
            private static int DecompressData(LzHeaderData* headerData, NativeSpan* decompressedData, NativeSpan* compressedData, int baseIdx, int prevBitPos, uint fileLen, HuffmanTableEntry* huffmanTable, uint startEntry)
            {
                int cidx, nextIndex;
                byte curByte;
                byte* pCompressed, pDecompressed;

                pDecompressed = decompressedData->Data;
                pCompressed = compressedData->Data + baseIdx;
                cidx = 0;
                var currentFileOffset = 0;
                var dictBitLength = 0;

                while (true)
                {
                    int nextBitOffs;
                    int backrefCount;
                    while (true)
                    {
                        if ((fileLen <= currentFileOffset) || (compressedData->Length <= cidx + baseIdx))
                        {
                            compressedData->Length = currentFileOffset;
                            return currentFileOffset;
                        }
                        curByte = pCompressed[cidx];
                        var tableBitCount = headerData->HuffTableBitCount;
                        nextBitOffs = prevBitPos - 1;
                        if (nextBitOffs < 0) nextBitOffs = 7;

                        // we only increment cidx when we wrapped into the next byte
                        nextIndex = prevBitPos - 1 >= 0 ? cidx : cidx + 1;

                        backrefCount = LenZu_DecodeHuffmanSequence(pCompressed, nextIndex, nextBitOffs, tableBitCount, huffmanTable, startEntry, &dictBitLength);

                        if (((curByte >> ((byte)prevBitPos)) & 1) == 0)
                        {
                            // break if the previous bit in the current byte was 0
                            // 0 bit indicates literal, 1 bit indicates backref
                            break;
                        }

                        // ReadFromDictSequenceFailed
                        if (backrefCount < 0) return -nextIndex;

                        var offsAmt = dictBitLength / 8;
                        nextBitOffs -= dictBitLength % 8;

                        if (nextBitOffs >= 8)
                        {
                            offsAmt += -1;
                            nextBitOffs += -8;
                        }
                        if (nextBitOffs < 0)
                        {
                            nextBitOffs += 8;
                            offsAmt += 1;
                        }

                        nextIndex += offsAmt;
                        backrefCount += headerData->BackrefBaseDistance;

                        var backrefDistHighBits = LenZu_DecodeHuffmanSequence(pCompressed, nextIndex, nextBitOffs, tableBitCount, huffmanTable, startEntry, &dictBitLength);
                        // ReadFromDictSequence failed
                        if (backrefDistHighBits < 0) return -nextIndex;

                        var offsAmt2 = dictBitLength / 8;
                        prevBitPos = nextBitOffs - (dictBitLength % 8);

                        if (prevBitPos >= 8)
                        {
                            prevBitPos -= 8;
                            offsAmt2 += -1;
                        }
                        if (prevBitPos < 0)
                        {
                            prevBitPos += 8;
                            offsAmt2 += 1;
                        }

                        cidx = nextIndex + offsAmt2;
                        uint backrefDistLowBits = 0;

                        var backrefBitCount = headerData->BackrefLowBitCount;
                        // interestingly, even though the implementation of ReadUnalignedBitsStartingAtIndex (mostly) supports up to 16-bit reads,
                        // the implementation does reads first in a block of 8, then in the remainder. 
                        if (8 < backrefBitCount)
                        {
                            (var readMaskedByte, var nextAdjustB) = ReadUnalignedBitsStartingAtIndex(&pCompressed[cidx], prevBitPos);
                            nextBitOffs = nextAdjustB;

                            backrefBitCount -= 8;
                            cidx += nextBitOffs;
                            backrefDistLowBits = (uint)readMaskedByte << backrefBitCount;
                        }
                        if (0 < backrefBitCount)
                        {
                            (var readMaskedByte, var nextBitOffsB) = ReadUnalignedBitsStartingAtIndex(&pCompressed[cidx], prevBitPos, backrefBitCount);
                            nextBitOffs = nextBitOffsB;

                            backrefBitCount = prevBitPos - (backrefBitCount % 8);

                            prevBitPos = WrapAt8(backrefBitCount);

                            cidx += nextBitOffs;
                            backrefDistLowBits |= readMaskedByte;
                        }

                        // decode a lookbehind
                        if (0 < backrefCount)
                        {
                            var iterVar = backrefCount;
                            do
                            {
                                if (currentFileOffset < fileLen)
                                {
                                    *pDecompressed = pDecompressed[-(long)(int)(backrefDistLowBits + (backrefDistHighBits << headerData->BackrefLowBitCount) + headerData->BackrefBaseDistance)];
                                    pDecompressed += 1;
                                    currentFileOffset += 1;
                                }
                                iterVar -= 1;
                            }
                            while (iterVar != 0);
                        }
                    }
                    if (backrefCount < 0) break;

                    cidx = dictBitLength / 8;
                    prevBitPos = nextBitOffs - (dictBitLength % 8);
                    if (prevBitPos >= 8)
                    {
                        prevBitPos -= 8;
                        cidx += -1;
                    }
                    if (prevBitPos < 0)
                    {
                        prevBitPos += 8;
                        cidx += 1;
                    }
                    cidx = nextIndex + cidx;

                    // decode a possibly-unaligned literal
                    if (0 < backrefCount + 1)
                    {
                        var iterVar = backrefCount + 1;
                        do
                        {
                            if (currentFileOffset < fileLen)
                            {
                                (var readMaskedByte, var nextBitOffsB) = ReadUnalignedBitsStartingAtIndex(&pCompressed[cidx], prevBitPos);
                                cidx += nextBitOffsB;
                                *pDecompressed = (byte)readMaskedByte;
                                pDecompressed++;
                                currentFileOffset += 1;
                            }
                            iterVar -= 1;
                        }
                        while (iterVar != 0);
                    }
                }
                return -nextIndex;
            }

            /// <summary>
            /// Each entry represents a bit. The lowest index entries are terminals, and their index is the final value.
            /// </summary>
            /// <remarks>
            /// <see cref="Child1"/> and <see cref="Child2"/> are checked sequentially for the one with the correct <see cref="BitValue"/>
            /// for the bit in the sequence.
            /// </remarks>
            public struct HuffmanTableEntry
            {
                public uint TableConstructOrderVal;
                public uint Child2;
                public uint Child1;
                public byte BitValue;
            }

            private static int GetMaskFromBitCount(int bitCount)
            {
                if (bitCount > 0)
                {
                    if (bitCount < 0x20)
                    {
                        return (1 << bitCount) - 1;
                    }
                    else
                    {
                        return -1;
                    }
                }
                else
                {
                    return 0;
                }
            }

            private static int GetTableSizeFromBitCount(int bitCount)
            {
                return GetDictionarySize(GetMaskFromBitCount(bitCount));
            }

            private static int GetDictionarySize(int lowMask)
            {
                Helpers.DAssert(BitOperations.PopCount((uint)lowMask + 1) == 1);
                if (lowMask + 1 < 0x16a0a)
                {
                    return (int)((uint)((lowMask + 2) * (lowMask + 1)) >> 1);
                }
                else
                {
                    return -1;
                }
            }

            // this appears to be a huffman decoder?
            // returns the dictionary entry index after the read
            private static int LenZu_DecodeHuffmanSequence(byte* pCompressed, int startIndex, int readShift, int numBits, HuffmanTableEntry* table, uint startEntry, int* bitSequenceLength)
            {
                long readIndex;
                uint tableEntry;

                var bitsRead = 0;
                var mask = GetMaskFromBitCount(numBits);
                var firstRealHuffEntry = (uint)(mask + 1);
                var tblSize = GetDictionarySize(mask);
                var persistResultVar = -1;
                var resultVal = -1;

                // read a Huffman sequence, using startEntry as the start of the tree
                if (firstRealHuffEntry < startEntry)
                {
                    if ((firstRealHuffEntry != 0) && (tblSize != 0))
                    {
                        readIndex = startIndex;
                        tableEntry = startEntry - 1;
                        while (true)
                        {
                            if (tableEntry < firstRealHuffEntry)
                            {
                                *bitSequenceLength = bitsRead;
                                return (int)tableEntry;
                            }
                            var readValue = (byte)((pCompressed[readIndex] >> ((byte)readShift & 0x1f)) & 1);
                            var nextDictEntry = table[tableEntry].Child1;
                            if (table[nextDictEntry].BitValue != readValue)
                            {
                                nextDictEntry = table[tableEntry].Child2;
                                if (table[nextDictEntry].BitValue != readValue) break;
                            }
                            var nextShift = readShift + (-1);
                            readShift = nextShift;
                            if (nextShift < 0)
                            {
                                readShift = 7;
                            }
                            var nextIndex = readIndex + 1;
                            if (-1 < nextShift)
                            {
                                nextIndex = readIndex;
                            }
                            bitsRead += 1;
                            readIndex = nextIndex;
                            tableEntry = nextDictEntry;
                        }
                        *bitSequenceLength = bitsRead;
                        return -1;
                    }
                    return -1;
                }

                // if, somehow, the startEntry is within the numeric value range, we instead select the highest-index LESS than startEntry with a nonzero TableConstructOrderVal.
                // Why this forward-iterates instead of backward iterating from startEntry and breaking out, one can only guess.
                if (startEntry != 0)
                {
                    var iterVar = 0;
                    do
                    {
                        resultVal = persistResultVar;
                        if (table->TableConstructOrderVal != 0)
                        {
                            *bitSequenceLength = 1;
                            resultVal = iterVar;
                        }
                        iterVar += 1;
                        table++;
                        persistResultVar = resultVal;
                    }
                    while (iterVar < startEntry);
                }
                return resultVal;
            }
        }

#pragma warning restore IDE1006 // Naming Styles
    }
}
