using System;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LibHac;
using LibHac.Common;
using LibHac.Fs;
using Buffer = System.Buffer;

namespace MahoyoHDRepack
{
    internal static class LenZuCompressorFile
    {
        private const int HeaderSize = 0x20;

        public static ReadOnlySpan<byte> ExpectHeader
            => "LenZuCompressor\0"u8 +
               "1\0\0\00\0\0\0\0\0\0\0\0\0\0\0"u8;

        public static IStorage ReadCompressed(IStorage compressed)
        {
            using UniqueRef<IStorage> result = default;
            ReadCompressed(ref result.Ref, compressed).ThrowIfFailure();
            return result.Release();
        }

        private struct LzHeaderData
        {
            public int _31; // [3..15]
            public int _32; // [3..15]
            public int Max_32_33; // max of _32 and byte at 0x33 in file
            public int LookbehindBaseBitCount; // [0..above field] + some other constraints
            public int _35_DictEntryOffset; // [2..8]
            public int Max_31_32_HuffTableBitCount; // max of _31 and _32
            public uint _30;
        }

        public static unsafe Result ReadCompressed(ref UniqueRef<IStorage> uncompressed, IStorage compressedStorage)
        {
            var result = compressedStorage.GetSize(out var size);
            if (result.IsFailure()) return result.Miss();

            if (size < 0x36) return ResultFs.InvalidFileSize.Value;

            Span<byte> headerData = stackalloc byte[HeaderSize];
            result = compressedStorage.Read(0, headerData);
            if (result.IsFailure()) return result.Miss();

            if (!headerData.SequenceEqual(ExpectHeader)) return ResultFs.InvalidFileSize.Value;

            // now we read the rest of the file into memory
            var compressedData = new byte[size].AsSpan();
            result = compressedStorage.Read(0, compressedData);
            if (result.IsFailure()) return result.Miss();

            Attempt2.NativeSpan outSpan = default, compressedSpan = default;

            byte[] decompressedData;
            try
            {
                int finalLen;
                fixed (byte* pCompressed = compressedData)
                {
                    compressedSpan.Data = pCompressed;
                    compressedSpan.Length = compressedData.Length;

                    finalLen = Attempt2.lz_decompress(&outSpan, &compressedSpan);
                }

                if (finalLen < 0)
                {
                    throw new InvalidOperationException($"Result code: {finalLen} (decompression failed?)");
                }

                decompressedData = new byte[finalLen];
                new Span<byte>(outSpan.Data, outSpan.Length).Slice(0, finalLen).CopyTo(decompressedData);
            }
            finally
            {
                if (outSpan.Data != null)
                {
                    NativeMemory.Free(outSpan.Data);
                }
            }

            uncompressed.Reset(MemoryStorage.Adopt(decompressedData));
            return Result.Success;
        }

        static unsafe class Attempt2
        {
            public struct NativeSpan
            {
                public byte* Data;
                public int Length;
                public int Padding;
                public ulong Checksum;
            }

#pragma warning disable IDE1006 // Naming Styles
            public static int lz_decompress(NativeSpan* outSpan, NativeSpan* compressedSpan)
            {
                outSpan->Data = null;

                LzHeaderData lz_data = default;
                var subByteAlignment = 7;
                var _zero = 0;
                ulong _0 = 0;
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

                    var tableEntryCount = GetTableSizeFromBitCount(lz_data.Max_31_32_HuffTableBitCount);
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
                            var firstRealTableEntry = (uint)GetMaskFromBitCount(lz_data.Max_31_32_HuffTableBitCount) + 1;
                            var huffEntryByteCountRoundedUp = (lz_data.Max_31_32_HuffTableBitCount + 7) / 8;

                            lenBytesRead = lz_read_int(out var numEntriesToFill, compressedData, baseIdx, subByteAlignment, huffEntryByteCountRoundedUp);
                            if (numEntriesToFill == 0)
                            {
                                numEntriesToFill = firstRealTableEntry;
                            }

                            // read some table entries
                            int x = 1;
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
                            var currentEntry = firstRealTableEntry;
                            uint idxOfSmallest, smallest, sndSmallest, entry_0, u3, u2;
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
                                if (0 < (int)finalLength)
                                {
                                    NativeMemory.Free(table);
                                    var l3 = ComputeChecksum(pSVar2);
                                    if (l3 != checksum)
                                    {
                                        return -10;
                                    }
                                    return (int)finalLength;
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
                        data = data + 1;
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
            /// causes this to consume all of the bits in the second byte read. In this case, the byte-advance will be 1, which is incorrect; it should be 2. The original code does not correctly
            /// handle this case.
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

                lz_data->_30 = pData[0];
                lz_data->_31 = pData[1];
                lz_data->_32 = pData[2];
                lz_data->Max_32_33 = pData[3];
                lz_data->LookbehindBaseBitCount = pData[4];
                lz_data->_35_DictEntryOffset = pData[5];

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
                var _31 = data->_31;
                var _31gt2 = 2 < _31;
                if (!_31gt2)
                {
                    data->_31 = _31 = 3;
                }
                var _31lt16 = _31 < 0x10;
                if (!_31lt16)
                {
                    data->_31 = _31 = 0xf;
                }

                var _32 = data->_32;
                var _32gt2 = 2 < _32;
                if (!_32gt2)
                {
                    data->_32 = _32 = 3;
                }
                var _32lt16 = _32 < 0x10;
                if (!_32lt16)
                {
                    data->_32 = _32 = 0xf;
                }

                data->Max_31_32_HuffTableBitCount = _31;
                if (_31 < _32)
                {
                    data->Max_31_32_HuffTableBitCount = _31 = _32;
                }

                var _33 = data->Max_32_33;
                var _max_32_33 = _33;
                if (_32 > _33)
                {
                    data->Max_32_33 = _max_32_33 = _32;
                }

                var _max_32_33lt16 = _max_32_33 < 0x10;
                if (!_max_32_33lt16)
                {
                    data->Max_32_33 = _max_32_33 = 0xf;
                }

                var _34 = data->LookbehindBaseBitCount;
                var _34gtm1 = -1 < _32;
                if (!_34gtm1)
                {
                    data->LookbehindBaseBitCount = _34 = 0;
                }
                var _34lt_max = _34 < _max_32_33;
                if (!_34lt_max)
                {
                    data->LookbehindBaseBitCount = _34 = _max_32_33 - 1;
                }
                var _mdiffltemax = _max_32_33 - _34 <= _31;
                if (!_mdiffltemax)
                {
                    data->LookbehindBaseBitCount = _max_32_33 - _31;
                }

                if (data->_35_DictEntryOffset < 2)
                {
                    data->_35_DictEntryOffset = 2;
                    return false;
                }
                if (8 < data->_35_DictEntryOffset)
                {
                    data->_35_DictEntryOffset = 8;
                    return false;
                }
                return (_mdiffltemax &&
                    _34lt_max &&
                    _34gtm1 &&
                    (_max_32_33lt16 && (_32 <= _33 && (_32lt16 && (_32gt2 && (_31lt16 && _31gt2))))));
            }

            private static int lz_read_header(NativeSpan* pDataPtr)
            {
                ulong uVar10, uVar4, uVar5, uVar9, uVar13;
                long lVar2, lVar6, lVar11, lVar7;
                int iVar3;
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

            private static uint DecompressData(LzHeaderData* headerData, NativeSpan* decompressedData, NativeSpan* compressedData, int baseIdx, int prevBitPos, uint fileLen, HuffmanTableEntry* huffmanTable, uint startEntry)
            {
                uint resultDictEntry, lookbehindBitCount2, uVar4, sgn7, u_zero;
                ulong uVar5;
                int iVar5, iVar6, bitOffs, cidx, nextIndex;
                byte bVar8, curByte, maskedByte;
                byte* pCompressed, pDecompressed;

                pDecompressed = decompressedData->Data;
                pCompressed = compressedData->Data + baseIdx;
                cidx = 0;
                u_zero = 0;
                var dictBitLength = 0;
                sgn7 = 0;

                while (true)
                {
                    int nextBitOffs;
                    while (true)
                    {
                        if ((fileLen <= u_zero) || (compressedData->Length <= cidx + baseIdx))
                        {
                            compressedData->Length = (int)u_zero;
                            return u_zero;
                        }
                        curByte = pCompressed[cidx];
                        var tableBitCount = headerData->Max_31_32_HuffTableBitCount;
                        nextBitOffs = prevBitPos - 1;
                        if (nextBitOffs < 0) nextBitOffs = 7;

                        // we only increment cidx when we wrapped into the next byte
                        nextIndex = prevBitPos - 1 >= 0 ? cidx : cidx + 1;

                        resultDictEntry = LenZu_DecodeHuffmanSequence(pCompressed, nextIndex, nextBitOffs, tableBitCount, huffmanTable, startEntry, &dictBitLength);

                        if (((curByte >> ((byte)prevBitPos)) & 1) == 0)
                        {
                            // break if the previous bit in the current byte was 0
                            break;
                        }

                        // ReadFromDictSequenceFailed
                        if ((int)resultDictEntry < 0) return (uint)-nextIndex;

                        var offsAmt = dictBitLength / 8;
                        nextBitOffs -= dictBitLength % 8;

                        if (7 < nextBitOffs)
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
                        resultDictEntry += (uint)headerData->_35_DictEntryOffset;

                        var sndDictResul = LenZu_DecodeHuffmanSequence(pCompressed, nextIndex, nextBitOffs, tableBitCount, huffmanTable, startEntry, &dictBitLength);
                        // ReadFromDictSequence failed
                        if ((int)sndDictResul < 0) return (uint)-nextIndex;

                        var offsAmt2 = dictBitLength / 8;
                        prevBitPos = nextBitOffs - (dictBitLength % 8);
                        if (7 < prevBitPos)
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
                        uint lookbehindBaseAmount = 0;

                        var lookbehindBitCount = headerData->LookbehindBaseBitCount;
                        if (8 < lookbehindBitCount)
                        {
                            (var readMaskedByte, var nextAdjustB) = ReadUnalignedBitsStartingAtIndex(&pCompressed[cidx], prevBitPos);
                            nextBitOffs = nextAdjustB;

                            lookbehindBitCount -= 8;
                            cidx += nextBitOffs;
                            lookbehindBaseAmount = (uint)readMaskedByte << ((byte)lookbehindBitCount & 0x1f);
                        }
                        if (0 < lookbehindBitCount)
                        {
                            (var readMaskedByte, var nextBitOffsB) = ReadUnalignedBitsStartingAtIndex(&pCompressed[cidx], prevBitPos, lookbehindBitCount);
                            nextBitOffs = nextBitOffsB;

                            lookbehindBitCount = prevBitPos - (lookbehindBitCount % 8);
                            var tmp1 = lookbehindBitCount - 8;
                            if (lookbehindBitCount < 8)
                            {
                                tmp1 = lookbehindBitCount;
                            }
                            prevBitPos = tmp1 + 8;
                            if (-1 < tmp1)
                            {
                                prevBitPos = tmp1;
                            }
                            cidx += nextBitOffs;
                            lookbehindBaseAmount |= readMaskedByte;
                        }
                        if (0 < (int)resultDictEntry)
                        {
                            lookbehindBitCount = headerData->_35_DictEntryOffset;
                            uVar5 = resultDictEntry;
                            do
                            {
                                if ((int)u_zero < fileLen)
                                {
                                    *pDecompressed = pDecompressed[-(long)(int)(lookbehindBaseAmount + (sndDictResul << (headerData->LookbehindBaseBitCount & 0x1f)) + lookbehindBitCount)];
                                    pDecompressed += 1;
                                    u_zero += 1;
                                }
                                uVar5 -= 1;
                            }
                            while (uVar5 != 0);
                        }
                    }
                    if ((int)resultDictEntry < 0) break;

                    cidx = dictBitLength / 8;
                    prevBitPos = nextBitOffs - (dictBitLength % 8);
                    if (7 < prevBitPos)
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
                    if (0 < (int)(resultDictEntry + 1))
                    {
                        uVar5 = resultDictEntry + 1;
                        do
                        {
                            if ((int)u_zero < fileLen)
                            {
                                (var readMaskedByte, var nextBitOffsB) = ReadUnalignedBitsStartingAtIndex(&pCompressed[cidx], prevBitPos);
                                cidx += nextBitOffsB;
                                *pDecompressed = (byte)readMaskedByte;
                                pDecompressed = pDecompressed + 1;
                                u_zero += 1;
                            }
                            uVar5 -= 1;
                        }
                        while (uVar5 != 0);
                    }
                }
                return (uint)-nextIndex;
            }


            [StructLayout(LayoutKind.Explicit)]
            public struct HuffmanTableEntry
            {
                [FieldOffset(0x0)] public uint TableConstructOrderVal;
                [FieldOffset(0x4)] public uint Child2;
                [FieldOffset(0x8)] public uint Child1;
                [FieldOffset(0xc)] public byte BitValue;
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
            private static uint LenZu_DecodeHuffmanSequence(byte* pCompressed, int startIndex, int readShift, int numBits, HuffmanTableEntry* table, uint startEntry, int* bitSequenceLength)
            {
                uint firstRealHuffEntry, bitsRead, resultVal, persistResultVar;
                long lVar3, readIndex;
                ulong tableEntry, nextDictEntry;
                int mask;
                byte readValue;

                bitsRead = 0;
                mask = GetMaskFromBitCount(numBits);
                firstRealHuffEntry = (uint)(mask + 1);
                var tblSize = GetDictionarySize(mask);
                persistResultVar = 0xffffffff;
                resultVal = 0xffffffff;
                if (firstRealHuffEntry < startEntry)
                {
                    if ((firstRealHuffEntry != 0) && (tblSize != 0))
                    {
                        readIndex = startIndex;
                        tableEntry = startEntry - 1;
                        while (true)
                        {
                            if ((uint)tableEntry < firstRealHuffEntry)
                            {
                                *bitSequenceLength = (int)bitsRead;
                                return (uint)tableEntry;
                            }
                            readValue = (byte)(pCompressed[readIndex] >> ((byte)readShift & 0x1f) & 1);
                            nextDictEntry = table[tableEntry].Child1;
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
                        *bitSequenceLength = (int)bitsRead;
                        return uint.MaxValue;
                    }
                    return uint.MaxValue;
                }
                if (startEntry != 0)
                {
                    var iterVar = 0u;
                    do
                    {
                        resultVal = persistResultVar;
                        if (table->TableConstructOrderVal != 0)
                        {
                            *bitSequenceLength = 1;
                            resultVal = iterVar;
                        }
                        iterVar += 1;
                        table = table + 1;
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
