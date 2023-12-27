using System;
using System.Linq;
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
            public uint _34; // [0..above field] + some other constraints
            public uint _35; // [2..8]
            public uint Max_31_32; // max of _31 and _32
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
                    var int2 = baseIdx;
                    offset = readBytes + lenBytesRead + offset;
                    lenBytesRead = lz_read_int(&baseIdx, compressedSpan, offset, 7, 0x20);
                    var local_EBX_213 = offset + lenBytesRead;
                    var local_80 = CONCAT44(int2, baseIdx);
                    int iVar3 = lz_read_int(&baseIdx, compressedSpan, local_EBX_213, 7, 0x20);
                    var int2_2 = local_EBX_213 + iVar3;
                    lenBytesRead = lz_read_early_data(&lz_data, compressedSpan, int2_2, 7, &_7);
                    baseIdx = (uint)(lenBytesRead + int2_2);
                    if ((int)baseIdx < 1)
                    {
                        return -2;
                    }
                    int2 = lz_data.Max_31_32;
                    byte _max_31_32_b = (byte)lz_data.Max_31_32;
                    lenBytesRead = _zero;
                    if (0 < lz_data.Max_31_32)
                    {
                        if ((uint)lz_data.Max_31_32 < 0x20)
                        {
                            lenBytesRead = (1 << (_max_31_32_b & 0x1f)) + (-1);
                        }
                        else
                        {
                            lenBytesRead = -1;
                        }
                    }
                    uint incr;
                    if (lenBytesRead + 1 < 0x16a0a)
                    {
                        incr = (uint)((lenBytesRead + 2) * (lenBytesRead + 1)) >> 1;
                    }
                    else
                    {
                        incr = uint.MaxValue;
                    }
                    ulong l3 = CONCAT44(0, incr);
                    var allocateSize = ulong.CreateSaturating(0x10 * new Int128(0, l3));
                    var allocated = (AllocatedStruct*)NativeMemory.Alloc((nuint)allocateSize);
                    if (allocated is null)
                    {
                        return -9;
                    }
                    if (incr != 0)
                    {
                        var curHead = allocated;
                        do
                        {
                            curHead->_0 = 0;
                            curHead->_4 = uint.MaxValue;
                            curHead->_8 = uint.MaxValue;
                            curHead->_c = 0xff;
                            curHead = curHead + 1;
                            l3 -= 1;
                        }
                        while (l3 != 0);
                        if (incr != 0)
                        {
                            lenBytesRead = _zero;
                            if (0 < (int)int2)
                            {
                                if (int2 < 0x20)
                                {
                                    lenBytesRead = (1 << (_max_31_32_b & 0x1f)) + (-1);
                                }
                                else
                                {
                                    lenBytesRead = -1;
                                }
                            }
                            uint readBytes2 = (uint)lenBytesRead + 1;
                            uint _1 = 1;
                            uint value = 0;
                            int read_int_arg5 = (int)((((int)(int2 + 7) >> 0x1f) & 7u) + int2 + 7) >> 3;
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
                                do
                                {
                                    int2 = (uint)y;
                                    if (0 < (int)x)
                                    {
                                        _1 = 0;
                                        offset = lz_read_int(&_1, compressedData, (int)baseIdx + lenBytesRead, _7, read_int_arg5_2);
                                        lenBytesRead += offset;
                                        int2 = _1;
                                    }
                                    _1 = 0;
                                    offset = lz_read_int(&_1, compressedSpan, (int)baseIdx + lenBytesRead, _7, 0x20);
                                    lenBytesRead += offset;
                                    incr = (uint)y + 1;
                                    allocated[int2]._0 = _1;
                                    y = incr;
                                }
                                while (incr < value);
                                int2 = lz_data.Max_31_32;
                            }
                            baseIdx += (uint)lenBytesRead;
                            if (0 < (int)int2)
                            {
                                if (int2 < 0x20)
                                {
                                    _zero = (1 << ((byte)int2 & 0x1f)) + (-1);
                                }
                                else
                                {
                                    _zero = -1;
                                }
                            }
                            int2 = (uint)_zero + 1u;
                            uint a7, a8, a6, u1, u5, u3, u2;
                            while (true)
                            {
                                a7 = uint.MaxValue;
                                a8 = uint.MaxValue >> 4;
                                a6 = uint.MaxValue >> 4;
                                incr = uint.MaxValue;
                                l3 = _0;
                                var curAlloc = allocated;
                                ulong uVar3 = (uint)_0;
                                if (int2 == 0) break;
                                do
                                {
                                    u1 = curAlloc->_0;
                                    u5 = (uint)l3;
                                    if ((u1 != 0) && (curAlloc->_c == 0xff))
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
                                while (u5 + 1 < int2);
                                if ((uint)uVar3 < 2) break;
                                l3 = (ulong)int2;
                                allocated[int2]._0 = allocated[incr]._0 + allocated[a7]._0;
                                allocated[int2]._4 = a7;
                                allocated[int2]._8 = incr;
                                allocated[a7]._c = 1;
                                allocated[incr]._c = 0;

                                int2 += 1;
                            }
                            if (int2 <= _zero + 1)
                            {
                                allocated[a7]._c = 1;
                            }
                            if (int2 != 0)
                            {
                                var pSVar2 = decompressedData;
                                var dataCopy = lz_data;
                                int2 = FUN_5730(&dataCopy, decompressedData, compressedData, baseIdx, _7, fileLen, allocated, int2);
                                if (0 < (int)int2)
                                {
                                    NativeMemory.Free(allocated);
                                    var pbVar3 = pSVar2->Data;
                                    ReadOnlySpan<int> lut = [0xe9, 0x11f, 0x137, 0x1b1];
                                    l3 = 0;
                                    if (pSVar2->Length != 0)
                                    {
                                        do
                                        {
                                            incr = (uint)_0;
                                            a6 = incr + 1;
                                            _0 = (ulong)a6;
                                            l3 = (ulong)((long)(l3 + *pbVar3) * (long)lut[(int)(incr & 3)]);
                                            pbVar3 = pbVar3 + 1;
                                        }
                                        while(a6 < (uint)pSVar2->Length);
                                    }
                                    pSVar2->Unk = l3;
                                    if (l3 != local_80)
                                    {
                                        return -10;
                                    }
                                    return (int)int2;
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
                lz_data->_34 = pData[4];
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

                data->Max_31_32 = _31;
                if ((int)_31 < (int)_32)
                {
                    data->Max_31_32 = _31 = _32;
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

                _34 = data->_34;
                var _34gtm1 = -1 < (int)_32;
                if (!_34gtm1)
                {
                    data->_34 = _34 = 0;
                }
                var _34lt_max = (int)_34 < (int)_max_32_33;
                if (!_34lt_max)
                {
                    data->_34 = _34 = _max_32_33 - 1;
                }
                var _mdiffltemax = (int)(_max_32_33 - _34) <= (int)_31;
                if (!_mdiffltemax)
                {
                    data->_34 = _max_32_33 - _31;
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

            private static uint FUN_5730(LzHeaderData* headerData, NativeSpan* decompressedData, NativeSpan* compressedData, uint baseIdx, int _7, uint fileLen, AllocatedStruct* allocated, uint int2)
            {
                uint uVar1, uVar2, uVar4, uVar6, _7_2, u_zero;
                ulong uVar3, uVar5;
                int iVar5, iVar6, iVar7, cidx, iVar8, iVar9;
                byte b_zero, bVar8, @byte, bVar10;
                byte* pCompressed, pDecompressed;

                pDecompressed = decompressedData->Data;
                pCompressed = compressedData->Data + baseIdx;
                _7_2 = (uint)_7;
                b_zero = 0;
                cidx = 0;
                u_zero = 0;
                _7 = 0;
                uVar6 = 0;

                while (true)
                {
                    while (true)
                    {
                        if ((fileLen <= u_zero) || (compressedData->Length <= cidx + baseIdx))
                        {
                            compressedData->Length = (int)u_zero;
                            return u_zero;
                        }
                        @byte = pCompressed[cidx];
                        uVar3 = headerData->Max_31_32;
                        iVar7 = (int)(_7_2 - 1);
                        iVar8 = iVar7;
                        if (iVar7 < 0)
                        {
                            iVar8 = 7;
                        }
                        iVar9 = cidx + 1;
                        if (-1 < iVar7)
                        {
                            iVar9 = cidx;
                        }
                        uVar1 = FUN_5ba0(pCompressed, iVar9, iVar8, uVar3, (AllocatedStruct*)allocated, int2, &_7);
                        if (((@byte >> ((byte)_7_2 & 0x1f)) & 1) == 0)
                        {
                            break;
                        }
                        if ((int)uVar1 < 0) return (uint)-iVar9;
                        uVar6 = (uint)(((int)_7 >> 0x1f) & 7);
                        uVar2 = (uint)(_7 + uVar6);
                        cidx = (int)uVar2 >> 3;
                        iVar8 -= (int)((uVar2 & 7) - uVar6);
                        if (7 < iVar8)
                        {
                            cidx += -1;
                            iVar8 += -8;
                        }
                        if (iVar8 < 0)
                        {
                            iVar8 += 8;
                            cidx += 1;
                        }
                        iVar9 += cidx;
                        uVar1 += headerData->_35;
                        uVar3 = FUN_5ba0(pCompressed, iVar9, iVar8, uVar3, (AllocatedStruct*)allocated, int2, &_7);
                        if ((int)uVar3 < 0)
                        {
                            return (uint)-iVar9;
                        }
                        uVar6 = (uint)((int)_7 >> 0x1f) & 7;
                        uVar2 = (uint)(_7 + uVar6);
                        cidx = (int)uVar2 >> 3;
                        _7_2 = (uint)(iVar8 - ((uVar2 & 7) - uVar6));
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
                        cidx = iVar9 + cidx;
                        uVar6 = 0;
                        uVar2 = headerData->_34;
                        @byte = (byte)uVar2;
                        if (8 < (int)uVar2)
                        {
                            bVar10 = (byte)((0xffu >> (int)((-(int)_7_2 + 7u) & 0x1f)) & pCompressed[cidx]);
                            if (_7_2 < 8)
                            {
                                iVar8 = 0;
                                b_zero = (byte)(_7_2 - 7);
                                if ((int)(_7_2 - 7) < 1)
                                {
                                    iVar8 = 1;
                                    b_zero = (byte)(pCompressed[(long)cidx + 1] >> (b_zero + 8 & 0x1f)
                                        | bVar10 << (-b_zero & 0x1f));
                                }
                                else
                                {
                                    b_zero = (byte)(bVar10 >> (b_zero & 0x1f));
                                }
                            }
                            else
                            {
                                iVar8 = -1;
                            }
                            uVar2 -= 8;
                            cidx += iVar8;
                            uVar6 = (uint)b_zero << ((byte)uVar2 & 0x1f);
                        }
                        if (0 < (int)uVar2)
                        {
                            bVar10 = (byte)((byte)(0xff >> (-(int)_7_2 + 7 & 0x1f)) & pCompressed[cidx]);
                            if ((_7_2 < 8) && (uVar2 - 1 < 8))
                            {
                                iVar8 = 0;
                                iVar7 = (int)((_7_2 - uVar2) + 1);
                                b_zero = (byte)iVar7;
                                if (iVar7 < 1)
                                {
                                    iVar8 = 1;
                                    b_zero = (byte)(pCompressed[(long)cidx + 1] >> ((sbyte)(_7_2 - uVar2) + 9 & 0x1f)
                                        | bVar10 << (-b_zero & 0x1f));
                                }
                                else
                                {
                                    b_zero = (byte)(bVar10 >> (b_zero & 0x1f));
                                }
                            }
                            else
                            {
                                iVar8 = -1;
                            }
                            uVar2 &= 0x80000007;
                            if ((int)uVar2 < 0)
                            {
                                uVar2 = (uVar2 - 1 | 0xfffffff8) + 1;
                            }
                            uVar2 = _7_2 - uVar2;
                            uVar4 = uVar2 - 8;
                            if ((int)uVar2 < 8)
                            {
                                uVar4 = uVar2;
                            }
                            _7_2 = uVar4 + 8;
                            if (-1 < (int)uVar4)
                            {
                                _7_2 = uVar4;
                            }
                            cidx += iVar8;
                            uVar6 |= b_zero;
                        }
                        if (0 < (int)uVar1)
                        {
                            uVar2 = headerData->_35;
                            uVar5 = (ulong)uVar1;
                            do
                            {
                                if ((int)u_zero < fileLen)
                                {
                                    *pDecompressed = pDecompressed[-(long)(int)(uVar6 + (uVar3 << (@byte & 0x1f)) + uVar2)];
                                    pDecompressed += 1;
                                    u_zero += 1;
                                }
                                uVar5 -= 1;
                            }
                            while (uVar5 != 0);
                        }
                    }
                    if ((int)uVar1 < 0) break;
                    uVar2 = (uint)((int)_7 >> 0x1f & 7);
                    uVar3 = (ulong)(_7 + uVar2);
                    cidx = (int)uVar3 >> 3;
                    _7_2 = (uint)(iVar8 - (int)((uVar3 & 7) - uVar2));
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
                    cidx = iVar9 + cidx;
                    if (0 < (int)(uVar1 + 1))
                    {
                        uVar5 = (ulong)(uVar1 + 1);
                        do
                        {
                            if ((int)u_zero < fileLen)
                            {
                                @byte = (byte)((byte)(0xff >> (7 - (int)_7_2 & 0x1f)) & pCompressed[cidx]);
                                if (_7_2 < 8)
                                {
                                    iVar8 = 0;
                                    b_zero = (byte)(_7_2 - 7);
                                    if ((int)(_7_2 - 7) < 1)
                                    {
                                        iVar8 = 1;
                                        b_zero = (byte)(pCompressed[(long)cidx + 1] >> (b_zero + 8 & 0x1f)
                                            | @byte << (-b_zero & 0x1f));
                                    }
                                    else
                                    {
                                        b_zero = (byte)(@byte >> (b_zero & 0x1f));
                                    }
                                }
                                else
                                {
                                    iVar8 = -1;
                                }
                                cidx += iVar8;
                                *pDecompressed = b_zero;
                                pDecompressed = pDecompressed + 1;
                                u_zero += 1;
                            }
                            uVar5 -= 1;
                        }
                        while (uVar5 != 0);
                    }
                }
                return (uint)-iVar9;
            }

            [StructLayout(LayoutKind.Explicit)]
            public struct AllocatedStruct
            {
                [FieldOffset(0x0)] public uint _0;
                [FieldOffset(0x4)] public uint _4;
                [FieldOffset(0x8)] public uint _8;
                [FieldOffset(0xc)] public byte _c;
            }

            private static uint FUN_5ba0(byte* pCompressed, int param_2, int param_3, ulong param_4, AllocatedStruct* allocated, uint int2, int* _7)
            {
                uint uVar1, uVar2, uVar5, uVar10, uVar11;
                long lVar3, lVar6;
                ulong uVar7, uVar8;
                int iVar9;
                byte bVar4;

                uVar2 = 0;
                uVar5 = 0xfffffff;
                if ((int)param_4 < 1)
                {
                    iVar9 = 0;
                }
                else if (param_4 < 0x20)
                {
                    iVar9 = (1 << ((byte)param_4 & 0x1f)) + (-1);
                }
                else
                {
                    iVar9 = -1;
                }
                uVar1 = (uint)(iVar9 + 1);
                if (uVar1 < 0x16a0a)
                {
                    uVar5 = (uint)((iVar9 + 2) * uVar1 >> 1);
                }
                uVar11 = 0xffffffff;
                uVar10 = 0xffffffff;
                if (uVar1 < int2)
                {
                    if ((uVar1 != 0) && (uVar5 != 0))
                    {
                        lVar6 = (long)param_2;
                        uVar7 = (ulong)(int2 - 1);
                        while (true)
                        {
                            if ((uint)uVar7 < uVar1)
                            {
                                *_7 = (int)uVar2;
                                return (uint)uVar2;
                            }
                            bVar4 = (byte)(pCompressed[lVar6] >> ((byte)param_3 & 0x1f) & 1);
                            uVar8 = allocated[uVar7]._8;
                            if (allocated[uVar8]._c != bVar4)
                            {
                                uVar8 = allocated[uVar7]._4;
                                if (allocated[uVar8]._c != bVar4) break;
                            }
                            iVar9 = param_3 + (-1);
                            param_3 = iVar9;
                            if (iVar9 < 0)
                            {
                                param_3 = 7;
                            }
                            lVar3 = lVar6 + 1;
                            if (-1 < iVar9)
                            {
                                lVar3 = lVar6;
                            }
                            uVar2 += 1;
                            lVar6 = lVar3;
                            uVar7 = uVar8;
                        }
                        *_7 = (int)uVar2;
                        return uint.MaxValue;
                    }
                    return uint.MaxValue;
                }
                if (int2 != 0)
                {
                    do
                    {
                        uVar10 = uVar11;
                        if (allocated->_0 != 0)
                        {
                            *_7 = 1;
                            uVar10 = uVar2;
                        }
                        uVar2 += 1;
                        allocated = allocated + 1;
                        uVar11 = uVar10;
                    }
                    while (uVar2 < int2);
                }
                return uVar10;
            }
        }

#pragma warning restore IDE1006 // Naming Styles
    }
}
