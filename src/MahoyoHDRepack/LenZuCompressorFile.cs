using System;
using System.Linq;
using System.Numerics;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.FsSrv;
using SixLabors.ImageSharp.Processing;
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
            public uint _35; // [2..8]
            public int Max_31_32_DictMaskBitCount; // max of _31 and _32
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
                public ulong Unk;
            }

#pragma warning disable IDE1006 // Naming Styles
            public static int lz_decompress(NativeSpan* outSpan, NativeSpan* compressedSpan)
            {
                outSpan->Data = null;

                LzHeaderData lz_data = default;
                int _7 = 7;
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
                    var dictMaskBitCount = baseIdx;
                    offset = readBytes + lenBytesRead + offset;
                    lenBytesRead = lz_read_int(&baseIdx, compressedSpan, offset, 7, 0x20);
                    var local_EBX_213 = offset + lenBytesRead;
                    var local_80 = CONCAT44(dictMaskBitCount, baseIdx);
                    int iVar3 = lz_read_int(&baseIdx, compressedSpan, local_EBX_213, 7, 0x20);
                    var int2_2 = local_EBX_213 + iVar3;
                    lenBytesRead = lz_read_early_data(&lz_data, compressedSpan, int2_2, 7, &_7);
                    baseIdx = (uint)(lenBytesRead + int2_2);
                    if ((int)baseIdx < 1)
                    {
                        return -2;
                    }
                    dictMaskBitCount = (uint)lz_data.Max_31_32_DictMaskBitCount;
                    byte _max_31_32_b = (byte)lz_data.Max_31_32_DictMaskBitCount;
                    var dictSize = GetDictionarySizeFromBitCount(lz_data.Max_31_32_DictMaskBitCount);
                    var allocateSize = (nuint)((nint)sizeof(DictionaryEntry) * dictSize);
                    var dictionary = (DictionaryEntry*)NativeMemory.Alloc(allocateSize);
                    if (dictionary is null)
                    {
                        return -9;
                    }
                    if (dictSize != 0)
                    {
                        var curHead = dictionary;
                        for (var i = 0; i < dictSize; i++)
                        {
                            dictionary[i]._0 = 0;
                            dictionary[i].Next2 = uint.MaxValue;
                            dictionary[i].Next1 = uint.MaxValue;
                            dictionary[i].Key = 0xff;
                        }
                        if (dictSize != 0)
                        {
                            lenBytesRead = GetMaskFromBitCount((int)dictMaskBitCount);
                            uint readBytes2 = (uint)lenBytesRead + 1;
                            uint _1 = 1;
                            uint value = 0;
                            int read_int_arg5 = (int)((((int)(dictMaskBitCount + 7) >> 0x1f) & 7u) + dictMaskBitCount + 7) >> 3;
                            var local_ac = read_int_arg5;
                            lenBytesRead = lz_read_int(&value, compressedData, (int)baseIdx, _7, read_int_arg5);
                            var read_int_arg5_2 = local_ac;
                            if (value == 0)
                            {
                                value = readBytes2;
                            }
                            if (readBytes2 * 4 < (read_int_arg5 + 4) * value)
                            {
                                _1 = uint.MaxValue;
                                value = readBytes2;
                            }
                            var x = _1;
                            var y = _0;
                            if (value != 0)
                            {
                                uint incr;
                                do
                                {
                                    dictMaskBitCount = (uint)y;
                                    if (0 < (int)x)
                                    {
                                        _1 = 0;
                                        offset = lz_read_int(&_1, compressedData, (int)baseIdx + lenBytesRead, _7, read_int_arg5_2);
                                        lenBytesRead += offset;
                                        dictMaskBitCount = _1;
                                    }
                                    _1 = 0;
                                    offset = lz_read_int(&_1, compressedSpan, (int)baseIdx + lenBytesRead, _7, 0x20);
                                    lenBytesRead += offset;
                                    incr = (uint)y + 1;
                                    dictionary[dictMaskBitCount]._0 = _1;
                                    y = incr;
                                }
                                while (incr < value);
                                dictMaskBitCount = lz_data.Max_31_32_DictMaskBitCount;
                            }
                            baseIdx += (uint)lenBytesRead;
                            if (0 < (int)dictMaskBitCount)
                            {
                                if (dictMaskBitCount < 0x20)
                                {
                                    _zero = (1 << ((byte)dictMaskBitCount & 0x1f)) + (-1);
                                }
                                else
                                {
                                    _zero = -1;
                                }
                            }
                            dictMaskBitCount = (uint)_zero + 1u;
                            uint a7, a8, a6, u1, u5, u3, u2;
                            while (true)
                            {
                                a7 = uint.MaxValue;
                                a8 = uint.MaxValue >> 4;
                                a6 = uint.MaxValue >> 4;
                                var incr = uint.MaxValue;
                                var l3 = _0;
                                var curAlloc = dictionary;
                                ulong uVar3 = (uint)_0;
                                if (dictMaskBitCount == 0) break;
                                do
                                {
                                    u1 = curAlloc->_0;
                                    u5 = (uint)l3;
                                    if ((u1 != 0) && (curAlloc->Key == 0xff))
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
                                while (u5 + 1 < dictMaskBitCount);
                                if ((uint)uVar3 < 2) break;
                                l3 = (ulong)dictMaskBitCount;
                                dictionary[dictMaskBitCount]._0 = dictionary[incr]._0 + dictionary[a7]._0;
                                dictionary[dictMaskBitCount].Next2 = a7;
                                dictionary[dictMaskBitCount].Next1 = incr;
                                dictionary[a7].Key = 1;
                                dictionary[incr].Key = 0;

                                dictMaskBitCount += 1;
                            }
                            if (dictMaskBitCount <= _zero + 1)
                            {
                                dictionary[a7].Key = 1;
                            }
                            if (dictMaskBitCount != 0)
                            {
                                var pSVar2 = decompressedData;
                                var dataCopy = lz_data;
                                dictMaskBitCount = FUN_5730(&dataCopy, decompressedData, compressedData, baseIdx, _7, fileLen, dictionary, dictMaskBitCount);
                                if (0 < (int)dictMaskBitCount)
                                {
                                    NativeMemory.Free(dictionary);
                                    var pbVar3 = pSVar2->Data;
                                    ReadOnlySpan<int> lut = [0xe9, 0x11f, 0x137, 0x1b1];
                                    ulong l3 = 0;
                                    if (pSVar2->Length != 0)
                                    {
                                        do
                                        {
                                            var incr = (uint)_0;
                                            a6 = incr + 1;
                                            _0 = (ulong)a6;
                                            l3 = (ulong)((long)(l3 + *pbVar3) * (long)lut[(int)(incr & 3)]);
                                            pbVar3 = pbVar3 + 1;
                                        }
                                        while (a6 < (uint)pSVar2->Length);
                                    }
                                    pSVar2->Unk = l3;
                                    if (l3 != local_80)
                                    {
                                        return -10;
                                    }
                                    return (int)dictMaskBitCount;
                                }
                            }
                        }
                    }
                }
                return -3;
            }

            private static ulong CONCAT44(uint int2, uint baseIdx) => ((ulong)int2 << 32) | baseIdx;

            private static int lz_read_int(uint* result, NativeSpan* dataSpan, int baseIdx, int rbi, int emr)
            {
                byte maskedByte, resultVal, baseBitNo;
                int reads, maxReads, idx, moveAmt, len;
                byte* pCur;

                pCur = dataSpan->Data + baseIdx;
                idx = 0;
                reads = 0;
                *result = 0;
                resultVal = 0;
                maxReads = ((int)((((emr + (-1)) >> 0x1f) & 7) + emr + (-1)) >> 3) + 1;
                if (0 < maxReads)
                {
                    len = dataSpan->Length;
                    baseBitNo = 0;
                    do
                    {
                        if (len <= idx + baseIdx) break;
                        maskedByte = (byte)((byte)(0xff >> ((7 - rbi) & 0x1f)) & *pCur);
                        if (rbi < 8)
                        {
                            moveAmt = 0;
                            resultVal = (byte)(rbi - 7);
                            if ((int)(rbi - 7) < 1)
                            {
                                moveAmt = 1;
                                resultVal = (byte)((pCur[1] >> (((byte)rbi + 1) & 0x1f)) | (maskedByte << (-resultVal & 0x1f)));
                            }
                            else
                            {
                                resultVal = (byte)(maskedByte >> (resultVal & 0x1f));
                            }
                        }
                        else
                        {
                            moveAmt = -1;
                        }
                        idx += moveAmt;
                        *result = *result | (uint)resultVal << (baseBitNo & 0x1f);
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

            private static int lz_read_early_data(LzHeaderData* lz_data, NativeSpan* compressedSpan, int offset, int _7, int* out_2)
            {
                bool result;
                byte diffFrom7Byte;
                int readBytes;
                int modify, modify2, modify3, modify4, modify5, modify6;
                byte bVar1, read1_result, read2_result;

                byte* pData = compressedSpan->Data + offset;
                readBytes = _7 - 7;
                read1_result = 0;
                modify5 = 0;
                modify6 = 0;
                bVar1 = (byte)(0xff >> ((7 - _7) & 0x1f));
                diffFrom7Byte = (byte)readBytes;

                lz_data->_30 = pData[0];
                lz_data->_31 = pData[1];
                lz_data->_32 = pData[2];
                lz_data->Max_32_33 = pData[3];
                lz_data->_34_BitShift = pData[4];
                lz_data->_35 = pData[5];

                *out_2 = _7;

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

                data->Max_31_32_DictMaskBitCount = _31;
                if ((int)_31 < (int)_32)
                {
                    data->Max_31_32_DictMaskBitCount = _31 = _32;
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

                if ((int)data->_35 < 2)
                {
                    data->_35 = 2;
                    return false;
                }
                if (8 < (int)data->_35)
                {
                    data->_35 = 8;
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

            private static uint FUN_5730(LzHeaderData* headerData, NativeSpan* decompressedData, NativeSpan* compressedData, uint baseIdx, int _7, uint fileLen, DictionaryEntry* dictionary, uint int2)
            {
                uint readDictSeqResult, _34_bitShift, uVar4, sgn7, _7_2, u_zero;
                ulong dictMaskBitCount, uVar5;
                int iVar5, iVar6, bitOffs, cidx, nextBitOffs, nextIndex;
                byte readMaskedByte, bVar8, curByte, maskedByte;
                byte* pCompressed, pDecompressed;

                pDecompressed = decompressedData->Data;
                pCompressed = compressedData->Data + baseIdx;
                _7_2 = (uint)_7;
                readMaskedByte = 0;
                cidx = 0;
                u_zero = 0;
                _7 = 0;
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
                        dictMaskBitCount = headerData->Max_31_32_DictMaskBitCount;
                        bitOffs = (int)(_7_2 - 1);
                        nextBitOffs = bitOffs;
                        if (bitOffs < 0)
                        {
                            nextBitOffs = 7;
                        }
                        nextIndex = cidx + 1;
                        if (-1 < bitOffs)
                        {
                            nextIndex = cidx;
                        }
                        readDictSeqResult = LenZu_ReadFromDictSequence(pCompressed, nextIndex, nextBitOffs, dictMaskBitCount, (DictionaryEntry*)dictionary, int2, &_7);
                        if (((curByte >> ((byte)_7_2 & 0x1f)) & 1) == 0)
                        {
                            break;
                        }
                        if ((int)readDictSeqResult < 0) return (uint)-nextIndex;
                        sgn7 = (uint)(((int)_7 >> 0x1f) & 7);
                        _34_bitShift = (uint)(_7 + sgn7);
                        cidx = (int)_34_bitShift >> 3;
                        nextBitOffs -= (int)((_34_bitShift & 7) - sgn7);
                        if (7 < nextBitOffs)
                        {
                            cidx += -1;
                            nextBitOffs += -8;
                        }
                        if (nextBitOffs < 0)
                        {
                            nextBitOffs += 8;
                            cidx += 1;
                        }
                        nextIndex += cidx;
                        readDictSeqResult += headerData->_35;
                        dictMaskBitCount = LenZu_ReadFromDictSequence(pCompressed, nextIndex, nextBitOffs, dictMaskBitCount, (DictionaryEntry*)dictionary, int2, &_7);
                        if ((int)dictMaskBitCount < 0)
                        {
                            return (uint)-nextIndex;
                        }
                        sgn7 = (uint)((int)_7 >> 0x1f) & 7;
                        _34_bitShift = (uint)(_7 + sgn7);
                        cidx = (int)_34_bitShift >> 3;
                        _7_2 = (uint)(nextBitOffs - ((_34_bitShift & 7) - sgn7));
                        if (7 < (int)_7_2)
                        {
                            _7_2 -= 8;
                            cidx += -1;
                        }
                        if ((int)_7_2 < 0)
                        {
                            _7_2 += 8;
                            cidx += 1;
                        }
                        cidx = nextIndex + cidx;
                        sgn7 = 0;
                        _34_bitShift = headerData->_34_BitShift;
                        curByte = (byte)_34_bitShift;
                        if (8 < (int)_34_bitShift)
                        {
                            maskedByte = (byte)((0xffu >> (int)((-(int)_7_2 + 7u) & 0x1f)) & pCompressed[cidx]);
                            if (_7_2 < 8)
                            {
                                nextBitOffs = 0;
                                var bitShift = (byte)(_7_2 - 7);
                                if ((int)(_7_2 - 7) < 1)
                                {
                                    nextBitOffs = 1;
                                    readMaskedByte = (byte)(pCompressed[(long)cidx + 1] >> (bitShift + 8 & 0x1f)
                                        | maskedByte << (-bitShift & 0x1f));
                                }
                                else
                                {
                                    readMaskedByte = (byte)(maskedByte >> (bitShift & 0x1f));
                                }
                            }
                            else
                            {
                                nextBitOffs = -1;
                            }
                            _34_bitShift -= 8;
                            cidx += nextBitOffs;
                            sgn7 = (uint)readMaskedByte << ((byte)_34_bitShift & 0x1f);
                        }
                        if (0 < (int)_34_bitShift)
                        {
                            maskedByte = (byte)((byte)(0xff >> (-(int)_7_2 + 7 & 0x1f)) & pCompressed[cidx]);
                            if ((_7_2 < 8) && (_34_bitShift - 1 < 8))
                            {
                                nextBitOffs = 0;
                                bitOffs = (int)((_7_2 - _34_bitShift) + 1);
                                readMaskedByte = (byte)bitOffs;
                                if (bitOffs < 1)
                                {
                                    nextBitOffs = 1;
                                    readMaskedByte = (byte)(pCompressed[(long)cidx + 1] >> ((sbyte)(_7_2 - _34_bitShift) + 9 & 0x1f)
                                        | maskedByte << (-readMaskedByte & 0x1f));
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
                            _34_bitShift = _7_2 - _34_bitShift;
                            uVar4 = _34_bitShift - 8;
                            if ((int)_34_bitShift < 8)
                            {
                                uVar4 = _34_bitShift;
                            }
                            _7_2 = uVar4 + 8;
                            if (-1 < (int)uVar4)
                            {
                                _7_2 = uVar4;
                            }
                            cidx += nextBitOffs;
                            sgn7 |= readMaskedByte;
                        }
                        if (0 < (int)readDictSeqResult)
                        {
                            _34_bitShift = headerData->_35;
                            uVar5 = (ulong)readDictSeqResult;
                            do
                            {
                                if ((int)u_zero < fileLen)
                                {
                                    *pDecompressed = pDecompressed[-(long)(int)(sgn7 + (dictMaskBitCount << (int)(headerData->_34_BitShift & 0x1f)) + _34_bitShift)];
                                    pDecompressed += 1;
                                    u_zero += 1;
                                }
                                uVar5 -= 1;
                            }
                            while (uVar5 != 0);
                        }
                    }
                    if ((int)readDictSeqResult < 0) break;
                    _34_bitShift = (uint)((int)_7 >> 0x1f & 7);
                    dictMaskBitCount = (ulong)(_7 + _34_bitShift);
                    cidx = (int)dictMaskBitCount >> 3;
                    _7_2 = (uint)(nextBitOffs - (int)((dictMaskBitCount & 7) - _34_bitShift));
                    if (7 < (int)_7_2)
                    {
                        _7_2 -= 8;
                        cidx += -1;
                    }
                    if ((int)_7_2 < 0)
                    {
                        _7_2 += 8;
                        cidx += 1;
                    }
                    cidx = nextIndex + cidx;
                    if (0 < (int)(readDictSeqResult + 1))
                    {
                        uVar5 = (ulong)(readDictSeqResult + 1);
                        do
                        {
                            if ((int)u_zero < fileLen)
                            {
                                curByte = (byte)((byte)(0xff >> (7 - (int)_7_2 & 0x1f)) & pCompressed[cidx]);
                                if (_7_2 < 8)
                                {
                                    nextBitOffs = 0;
                                    readMaskedByte = (byte)(_7_2 - 7);
                                    if ((int)(_7_2 - 7) < 1)
                                    {
                                        nextBitOffs = 1;
                                        readMaskedByte = (byte)(pCompressed[(long)cidx + 1] >> (readMaskedByte + 8 & 0x1f)
                                            | curByte << (-readMaskedByte & 0x1f));
                                    }
                                    else
                                    {
                                        readMaskedByte = (byte)(curByte >> (readMaskedByte & 0x1f));
                                    }
                                }
                                else
                                {
                                    nextBitOffs = -1;
                                }
                                cidx += nextBitOffs;
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
            public struct DictionaryEntry
            {
                [FieldOffset(0x0)] public uint _0;
                [FieldOffset(0x4)] public uint Next2;
                [FieldOffset(0x8)] public uint Next1;
                [FieldOffset(0xc)] public byte Key;
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

            private static int GetDictionarySizeFromBitCount(int bitCount)
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

            private static uint LenZu_ReadFromDictSequence(byte* pCompressed, int startIndex, int readShift, ulong numBits, DictionaryEntry* dictionary, uint int2, int* sequenceLength)
            {
                uint pow2, bytesRead, uVar10, uVar11;
                long lVar3, readIndex;
                ulong dictEntry, nextDictEntry;
                int mask;
                byte readValue;

                bytesRead = 0;
                if ((int)numBits < 1)
                {
                    mask = 0;
                }
                else if (numBits < 0x20)
                {
                    mask = (1 << ((byte)numBits & 0x1f)) + (-1);
                }
                else
                {
                    mask = -1;
                }
                pow2 = (uint)(mask + 1);
                var dictionarySize = GetDictionarySize(mask);
                uVar11 = 0xffffffff;
                uVar10 = 0xffffffff;
                if (pow2 < int2)
                {
                    if ((pow2 != 0) && (dictionarySize != 0))
                    {
                        readIndex = (long)startIndex;
                        dictEntry = (ulong)(int2 - 1);
                        while (true)
                        {
                            if ((uint)dictEntry < pow2)
                            {
                                *sequenceLength = (int)bytesRead;
                                return (uint)bytesRead;
                            }
                            readValue = (byte)(pCompressed[readIndex] >> ((byte)readShift & 0x1f) & 1);
                            nextDictEntry = dictionary[dictEntry].Next1;
                            if (dictionary[nextDictEntry].Key != readValue)
                            {
                                nextDictEntry = dictionary[dictEntry].Next2;
                                if (dictionary[nextDictEntry].Key != readValue) break;
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
                            bytesRead += 1;
                            readIndex = nextIndex;
                            dictEntry = nextDictEntry;
                        }
                        *sequenceLength = (int)bytesRead;
                        return uint.MaxValue;
                    }
                    return uint.MaxValue;
                }
                if (int2 != 0)
                {
                    do
                    {
                        uVar10 = uVar11;
                        if (dictionary->_0 != 0)
                        {
                            *sequenceLength = 1;
                            uVar10 = bytesRead;
                        }
                        bytesRead += 1;
                        dictionary = dictionary + 1;
                        uVar11 = uVar10;
                    }
                    while (bytesRead < int2);
                }
                return uVar10;
            }
        }

#pragma warning restore IDE1006 // Naming Styles
    }
}
