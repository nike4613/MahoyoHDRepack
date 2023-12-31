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
            public uint _31; // [3..15]
            public uint _32; // [3..15]
            public uint Max_32_33; // max of _32 and byte at 0x33 in file
            public uint _34_BitShift; // [0..above field] + some other constraints
            public uint _35_DictEntryOffset; // [2..8]
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
                int subByteAlignment = 7;
                int _zero = 0;
                ulong _0 = 0;
                var compressedData = compressedSpan;
                var decompressedData = outSpan;
                int readBytes = lz_read_header(compressedSpan);
                if (readBytes < 0)
                {
                    return -1;
                }

                uint baseIdx = 0;
                var lenBytesRead = lz_read_int(&baseIdx, compressedSpan, readBytes, 7, 0x20);
                var fileLen = baseIdx;
                if (outSpan is not null)
                {
                    // NOTE: this is different from the original, we alloc here to avoid re-reading everything
                    outSpan->Data = (byte*)NativeMemory.Alloc(fileLen);
                    outSpan->Length = (int)fileLen;

                    var offset = lz_read_int(&baseIdx, compressedSpan, readBytes + lenBytesRead, 7, 0x20);
                    var huffTableBitCount = baseIdx;
                    offset = readBytes + lenBytesRead + offset;
                    lenBytesRead = lz_read_int(&baseIdx, compressedSpan, offset, 7, 0x20);
                    var local_EBX_213 = offset + lenBytesRead;
                    var checksum = CONCAT44(huffTableBitCount, baseIdx);
                    int iVar3 = lz_read_int(&baseIdx, compressedSpan, local_EBX_213, 7, 0x20);
                    var int2_2 = local_EBX_213 + iVar3;
                    lenBytesRead = lz_read_early_data(&lz_data, compressedSpan, int2_2, 7, &subByteAlignment);
                    baseIdx = (uint)(lenBytesRead + int2_2);
                    if ((int)baseIdx < 1)
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
                        var curHead = table;
                        for (var i = 0; i < tableEntryCount; i++)
                        {
                            table[i]._0 = 0;
                            table[i].Child2 = uint.MaxValue;
                            table[i].Child1 = uint.MaxValue;
                            table[i].BitValue = 0xff;
                        }
                        if (tableEntryCount != 0)
                        {
                            uint firstRealTableEntry = (uint)GetMaskFromBitCount((int)lz_data.Max_31_32_HuffTableBitCount) + 1;
                            uint _1 = 1;
                            uint value = 0;
                            int huffEntryByteCountRoundedUp = (lz_data.Max_31_32_HuffTableBitCount + 7) / 8;
                            lenBytesRead = lz_read_int(&value, compressedData, (int)baseIdx, subByteAlignment, huffEntryByteCountRoundedUp);
                            if (value == 0)
                            {
                                value = firstRealTableEntry;
                            }
                            if (firstRealTableEntry * 4 < (huffEntryByteCountRoundedUp + 4) * value)
                            {
                                _1 = uint.MaxValue;
                                value = firstRealTableEntry;
                            }
                            var x = _1;
                            var y = _0;
                            if (value != 0)
                            {
                                uint incr;
                                do
                                {
                                    huffTableBitCount = (uint)y;
                                    if (0 < (int)x)
                                    {
                                        _1 = 0;
                                        offset = lz_read_int(&_1, compressedData, (int)baseIdx + lenBytesRead, subByteAlignment, huffEntryByteCountRoundedUp);
                                        lenBytesRead += offset;
                                        huffTableBitCount = _1;
                                    }
                                    _1 = 0;
                                    offset = lz_read_int(&_1, compressedSpan, (int)baseIdx + lenBytesRead, subByteAlignment, 0x20);
                                    lenBytesRead += offset;
                                    incr = (uint)y + 1;
                                    table[huffTableBitCount]._0 = _1;
                                    y = incr;
                                }
                                while (incr < value);
                            }
                            baseIdx += (uint)lenBytesRead;
                            _zero = GetMaskFromBitCount(lz_data.Max_31_32_HuffTableBitCount);
                            var initialTableEntryPlusOne = (uint)_zero + 1u;
                            uint a7, a8, a6, u1, u5, u3, u2;
                            while (true)
                            {
                                a7 = uint.MaxValue;
                                a8 = uint.MaxValue >> 4;
                                a6 = uint.MaxValue >> 4;
                                var incr = uint.MaxValue;
                                var l3 = _0;
                                var curAlloc = table;
                                ulong uVar3 = (uint)_0;
                                if (initialTableEntryPlusOne == 0) break;
                                do
                                {
                                    u1 = curAlloc->_0;
                                    u5 = (uint)l3;
                                    if ((u1 != 0) && (curAlloc->BitValue == 0xff))
                                    {
                                        uVar3 = (ulong)((int)uVar3 + 1);
                                        u3 = u1;
                                        u2 = u5;
                                        if (a6 <= u1)
                                        {
                                            u3 = a6;
                                            u2 = incr;
                                        }
                                        incr = u2;
                                        a6 = u3;
                                        if (u1 < a8)
                                        {
                                            incr = a7;
                                            a6 = a8;
                                            a8 = u1;
                                            a7 = u5;
                                        }
                                    }
                                    else
                                    {
                                        //Debugger.Break();
                                    }
                                    curAlloc = curAlloc + 1;
                                    l3 = (ulong)(u5 + 1);
                                }
                                while (u5 + 1 < initialTableEntryPlusOne);
                                if ((uint)uVar3 < 2) break;
                                l3 = (ulong)initialTableEntryPlusOne;
                                table[initialTableEntryPlusOne]._0 = table[incr]._0 + table[a7]._0;
                                table[initialTableEntryPlusOne].Child2 = a7;
                                table[initialTableEntryPlusOne].Child1 = incr;
                                table[a7].BitValue = 1;
                                table[incr].BitValue = 0;

                                initialTableEntryPlusOne += 1;
                            }
                            if (initialTableEntryPlusOne <= _zero + 1)
                            {
                                table[a7].BitValue = 1;
                            }
                            if (initialTableEntryPlusOne != 0)
                            {
                                var pSVar2 = decompressedData;
                                var dataCopy = lz_data;
                                var finalLength = DecompressData(&dataCopy, decompressedData, compressedData, baseIdx, subByteAlignment, fileLen, table, initialTableEntryPlusOne);
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
                        _0 = (ulong)a6;
                        l3 = (ulong)((long)(l3 + *data) * (long)lut[(int)(incr & 3)]);
                        data = data + 1;
                    }
                    while (a6 < (uint)dataSpan->Length);
                }
                dataSpan->Checksum = l3;
                return l3;
            }

            private static ulong CONCAT44(uint hi, uint lo) => ((ulong)hi << 32) | lo;

            public readonly struct ReadByteFromBitOffsetResult(byte result, sbyte adjust)
            {
                public readonly byte Result = result;
                public readonly sbyte Adjust = adjust;

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public void Deconstruct(out byte result, out sbyte adjust)
                {
                    result = Result;
                    adjust = Adjust;
                }
            }

            /// <summary>
            /// Reads an unaligned (big-endian) byte from the byte-aligned bitstream <paramref name="data"/>, with the bit with index <paramref name="bitIndex"/>
            /// being the highest bit in the result.
            /// </summary>
            /// <remarks>
            /// <para>Note: this is NOT safe in the face of byte 2 at <paramref name="data"/> being an invalid page. This implementation is compied basically
            /// verbatim from the decomp.</para>
            /// <para>
            /// 
            /// Consider the following example bytes (where each letter represents a single bit in the bitstream):
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
            /// Note how no matter what, the next byte at that alignment starts in the following aligned byte, hence the pointer-adjust always being 1.
            /// 
            /// </para>
            /// <para>I find the choice of 7 to mean byte-aligned to be odd, but this is what the original code does. ¯\_(ツ)_/¯</para>
            /// </remarks>
            /// <param name="data">A pointer to the data to read from.</param>
            /// <param name="bitIndex">The bit index to read at.</param>
            /// <returns>A <see cref="ReadByteFromBitOffsetResult"/> containing both the result and the pointer-adjust. (The pointer-adjust is always 1 though.)</returns>
            private static ReadByteFromBitOffsetResult ReadUnalignedByteByFirstBitIndex(byte* data, int bitIndex)
            {
                Helpers.Assert(bitIndex is >= 0 and < 8);

                // NOTE: if this needs to be revisited, look at lz_read_int in decomp for its purest form
                var maskedFirst = (0xff >> (7 - bitIndex)) & *data;
                var result = (data[1] >> (bitIndex + 1)) | (maskedFirst << (7 - bitIndex));
                return new((byte)result, 1);
            }

            private static int lz_read_int(uint* result, NativeSpan* dataSpan, int baseIdx, int subByteAlignment, int emr)
            {
                byte maskedByte, resultVal, baseBitNo;
                int reads, maxReads, idx, len;
                byte* pCur;

                pCur = dataSpan->Data + baseIdx;
                idx = 0;
                reads = 0;
                *result = 0;
                resultVal = 0;
                maxReads = ((emr - 1) / 8) + 1;
                if (0 < maxReads)
                {
                    len = dataSpan->Length;
                    baseBitNo = 0;
                    do
                    {
                        if (len <= idx + baseIdx) break;
                        (resultVal, var moveAmt) = ReadUnalignedByteByFirstBitIndex(pCur, subByteAlignment);
                        idx += moveAmt;
                        *result = *result | (uint)resultVal << baseBitNo;
                        reads += 1;
                        baseBitNo += 8;
                        pCur = pCur + moveAmt;
                    }
                    while (reads < maxReads);
                }

                if (reads != maxReads)
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
                lz_data->_34_BitShift = pData[4];
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
                uint _31, _32, _33, _34, _max_32_33;

                _31 = data->_31;
                var _31gt2 = 2 < (int)_31;
                if (!_31gt2)
                {
                    data->_31 = _31 = 3;
                }
                var _31lt16 = (int)_31 < 0x10;
                if (!_31lt16)
                {
                    data->_31 = _31 = 0xf;
                }

                _32 = data->_32;
                var _32gt2 = 2 < (int)_32;
                if (!_32gt2)
                {
                    data->_32 = _32 = 3;
                }
                var _32lt16 = (int)_32 < 0x10;
                if (!_32lt16)
                {
                    data->_32 = _32 = 0xf;
                }

                data->Max_31_32_HuffTableBitCount = (int)_31;
                if ((int)_31 < (int)_32)
                {
                    data->Max_31_32_HuffTableBitCount = (int)(_31 = _32);
                }

                _33 = data->Max_32_33;
                _max_32_33 = _33;
                if ((int)_32 > (int)_33)
                {
                    data->Max_32_33 = _max_32_33 = _32;
                }

                var _max_32_33lt16 = (int)_max_32_33 < 0x10;
                if (!_max_32_33lt16)
                {
                    data->Max_32_33 = _max_32_33 = 0xf;
                }

                _34 = data->_34_BitShift;
                var _34gtm1 = -1 < (int)_32;
                if (!_34gtm1)
                {
                    data->_34_BitShift = _34 = 0;
                }
                var _34lt_max = (int)_34 < (int)_max_32_33;
                if (!_34lt_max)
                {
                    data->_34_BitShift = _34 = _max_32_33 - 1;
                }
                var _mdiffltemax = (int)(_max_32_33 - _34) <= (int)_31;
                if (!_mdiffltemax)
                {
                    data->_34_BitShift = _max_32_33 - _31;
                }

                if ((int)data->_35_DictEntryOffset < 2)
                {
                    data->_35_DictEntryOffset = 2;
                    return false;
                }
                if (8 < (int)data->_35_DictEntryOffset)
                {
                    data->_35_DictEntryOffset = 8;
                    return false;
                }
                return (_mdiffltemax &&
                    _34lt_max &&
                    _34gtm1 &&
                    (_max_32_33lt16 && ((int)_32 <= (int)_33 && (_32lt16 && (_32gt2 && (_31lt16 && _31gt2))))));
            }

            private static int lz_read_header(NativeSpan* pDataPtr)
            {
                ulong uVar10, uVar4, uVar5, uVar9, uVar13;
                long lVar2, lVar6, lVar11, lVar7;
                int iVar3;
                uint uVar12;
                byte* auStack_la8 = stackalloc byte[32];
                ReadOnlySpan<byte> local_188 = "LenZuCompressor\0"u8;
                byte* local_f8 = stackalloc byte[16];
                byte* acStack_e8 = stackalloc byte[128];
                uint* local_68 = stackalloc uint[4];
                byte* fst16ofData = stackalloc byte[16];

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
                    uVar13 = (ulong)uVar12;
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
                    uVar9 = (ulong)uVar12;
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

            private static uint DecompressData(LzHeaderData* headerData, NativeSpan* decompressedData, NativeSpan* compressedData, uint baseIdx, int prevBitPos, uint fileLen, HuffmanTableEntry* huffmanTable, uint firstHuffTableEntry)
            {
                uint resultDictEntry, _34_bitShift, uVar4, sgn7, u_zero;
                ulong uVar5;
                int iVar5, iVar6, bitOffs, cidx, nextBitOffs, nextIndex;
                byte readMaskedByte, bVar8, curByte, maskedByte;
                byte* pCompressed, pDecompressed;

                pDecompressed = decompressedData->Data;
                pCompressed = compressedData->Data + baseIdx;
                readMaskedByte = 0;
                cidx = 0;
                u_zero = 0;
                var dictBitLength = 0;
                sgn7 = 0;

                while (true)
                {
                    while (true)
                    {
                        if ((fileLen <= u_zero) || (compressedData->Length <= cidx + baseIdx))
                        {
                            compressedData->Length = (int)u_zero;
                            return u_zero;
                        }
                        curByte = pCompressed[cidx];
                        var tableBitCount = headerData->Max_31_32_HuffTableBitCount;
                        nextBitOffs = (int)(prevBitPos - 1);
                        if (nextBitOffs < 0) nextBitOffs = 7;

                        // we only increment cidx when we wrapped into the next byte
                        nextIndex = prevBitPos - 1 >= 0 ? cidx : cidx + 1;

                        resultDictEntry = LenZu_DecodeHuffmanSequence(pCompressed, nextIndex, nextBitOffs, tableBitCount, huffmanTable, firstHuffTableEntry, &dictBitLength);

                        if (((curByte >> ((byte)prevBitPos)) & 1) == 0)
                        {
                            // break if the previous bit in the current byte was 0
                            break;
                        }

                        // ReadFromDictSequenceFailed
                        if ((int)resultDictEntry < 0) return (uint)-nextIndex;

                        // load the sign bit of _7 into the low 3 bits of sgn7
                        sgn7 = (uint)(((int)dictBitLength >> 0x1f) & 7);
                        // effectively: sgn7 = _7 < 0 ? 7 : 0;
                        // sgn6 = ..0sss (s = sign bit, S = inverse sign bit

                        var tmp1 = (int)(dictBitLength + sgn7);
                        var offsAmt = (int)tmp1 >> 3;
                        nextBitOffs -= (int)((tmp1 & 7) - sgn7);

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
                        resultDictEntry += headerData->_35_DictEntryOffset;

                        var sndDictResul = LenZu_DecodeHuffmanSequence(pCompressed, nextIndex, nextBitOffs, tableBitCount, huffmanTable, firstHuffTableEntry, &dictBitLength);
                        // ReadFromDictSequence failed
                        if ((int)sndDictResul < 0) return (uint)-nextIndex;

                        sgn7 = (uint)((int)dictBitLength >> 0x1f) & 7;
                        var tmp2 = (int)(dictBitLength + sgn7);
                        var offsAmt2 = (int)tmp2 >> 3;
                        prevBitPos = (int)(nextBitOffs - ((tmp2 & 7) - sgn7));
                        if (7 < (int)prevBitPos)
                        {
                            prevBitPos -= 8;
                            offsAmt2 += -1;
                        }
                        if ((int)prevBitPos < 0)
                        {
                            prevBitPos += 8;
                            offsAmt2 += 1;
                        }

                        cidx = nextIndex + offsAmt2;
                        uint lookbehindBaseAmount = 0;

                        _34_bitShift = headerData->_34_BitShift;
                        curByte = (byte)_34_bitShift;
                        if (8 < (int)_34_bitShift)
                        {
                            (readMaskedByte, var nextBitOffsB) = ReadUnalignedByteByFirstBitIndex(&pCompressed[cidx], prevBitPos);
                            nextBitOffs = nextBitOffsB;

                            _34_bitShift -= 8;
                            cidx += nextBitOffs;
                            lookbehindBaseAmount = (uint)readMaskedByte << ((byte)_34_bitShift & 0x1f);
                        }
                        if (0 < (int)_34_bitShift)
                        {
                            // note: this looks VERY SIMILAR to ReadUnalignedByteByFirstBitIndex, except that it has the extra _34_bitShift terms inserted
                            // this is likely actually another case of that logic being inlined (maybe manually, hopefully by the compiler), and all the others
                            // just don't use that bitshift as a meaningful parameter (they always set it to 0, maybe?)
                            maskedByte = (byte)((byte)(0xff >> (7 - prevBitPos)) & pCompressed[cidx]);
                            if ((prevBitPos < 8) && (_34_bitShift - 1 < 8))
                            {
                                nextBitOffs = 0;
                                bitOffs = (int)((prevBitPos - _34_bitShift) + 1);
                                readMaskedByte = (byte)bitOffs;
                                if (bitOffs < 1)
                                {
                                    nextBitOffs = 1;
                                    readMaskedByte = (byte)(pCompressed[(long)cidx + 1] >> ((sbyte)(prevBitPos - _34_bitShift) + 9 & 0x1f)
                                        | maskedByte << (-(sbyte)readMaskedByte & 0x1f));
                                }
                                else
                                {
                                    readMaskedByte = (byte)(maskedByte >> (readMaskedByte & 0x1f));
                                }
                            }
                            else
                            {
                                nextBitOffs = -1;
                            }
                            _34_bitShift &= 0x80000007;
                            if ((int)_34_bitShift < 0)
                            {
                                _34_bitShift = (_34_bitShift - 1 | 0xfffffff8) + 1;
                            }
                            _34_bitShift = (uint)(prevBitPos - _34_bitShift);
                            uVar4 = _34_bitShift - 8;
                            if ((int)_34_bitShift < 8)
                            {
                                uVar4 = _34_bitShift;
                            }
                            prevBitPos = (int)uVar4 + 8;
                            if (-1 < (int)uVar4)
                            {
                                prevBitPos = (int)uVar4;
                            }
                            cidx += nextBitOffs;
                            lookbehindBaseAmount |= readMaskedByte;
                        }
                        if (0 < (int)resultDictEntry)
                        {
                            _34_bitShift = headerData->_35_DictEntryOffset;
                            uVar5 = (ulong)resultDictEntry;
                            do
                            {
                                if ((int)u_zero < fileLen)
                                {
                                    *pDecompressed = pDecompressed[-(long)(int)(lookbehindBaseAmount + (sndDictResul << (int)(headerData->_34_BitShift & 0x1f)) + _34_bitShift)];
                                    pDecompressed += 1;
                                    u_zero += 1;
                                }
                                uVar5 -= 1;
                            }
                            while (uVar5 != 0);
                        }
                    }
                    if ((int)resultDictEntry < 0) break;
                    _34_bitShift = (uint)((int)dictBitLength >> 0x1f & 7);
                    var dictBitLengthPlusBitShift = dictBitLength + _34_bitShift;
                    cidx = (int)dictBitLengthPlusBitShift >> 3;
                    prevBitPos = (int)(nextBitOffs - (int)((dictBitLengthPlusBitShift & 7) - _34_bitShift));
                    if (7 < (int)prevBitPos)
                    {
                        prevBitPos -= 8;
                        cidx += -1;
                    }
                    if ((int)prevBitPos < 0)
                    {
                        prevBitPos += 8;
                        cidx += 1;
                    }
                    cidx = nextIndex + cidx;
                    if (0 < (int)(resultDictEntry + 1))
                    {
                        uVar5 = (ulong)(resultDictEntry + 1);
                        do
                        {
                            if ((int)u_zero < fileLen)
                            {
                                (readMaskedByte, var nextBitOffsB) = ReadUnalignedByteByFirstBitIndex(&pCompressed[cidx], prevBitPos);
                                cidx += nextBitOffsB;
                                *pDecompressed = readMaskedByte;
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
                [FieldOffset(0x0)] public uint _0;
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
            private static uint LenZu_DecodeHuffmanSequence(byte* pCompressed, int startIndex, int readShift, int numBits, HuffmanTableEntry* table, uint firstTableEntryPLus1, int* bitSequenceLength)
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
                if (firstRealHuffEntry < firstTableEntryPLus1)
                {
                    if ((firstRealHuffEntry != 0) && (tblSize != 0))
                    {
                        readIndex = (long)startIndex;
                        tableEntry = (ulong)(firstTableEntryPLus1 - 1);
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
                if (firstTableEntryPLus1 != 0)
                {
                    var iterVar = 0u;
                    do
                    {
                        resultVal = persistResultVar;
                        if (table->_0 != 0)
                        {
                            *bitSequenceLength = 1;
                            resultVal = iterVar;
                        }
                        iterVar += 1;
                        table = table + 1;
                        persistResultVar = resultVal;
                    }
                    while (iterVar < firstTableEntryPLus1);
                }
                return resultVal;
            }
        }

#pragma warning restore IDE1006 // Naming Styles
    }
}
