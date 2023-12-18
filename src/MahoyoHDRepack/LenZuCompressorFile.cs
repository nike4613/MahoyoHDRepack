using System;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using LibHac;
using LibHac.Common;
using LibHac.Fs;
using Microsoft.IdentityModel.Tokens;

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

        public static Result ReadCompressed(ref UniqueRef<IStorage> uncompressed, IStorage compressedStorage)
        {
            var result = compressedStorage.GetSize(out var size);
            if (result.IsFailure()) return result.Miss();

            if (size < 0x36) return ResultFs.InvalidFileSize.Value;

            Span<byte> headerData = stackalloc byte[HeaderSize];
            result = compressedStorage.Read(0, headerData);
            if (result.IsFailure()) return result.Miss();

            if (!headerData.SequenceEqual(ExpectHeader)) return ResultFs.InvalidFileSize.Value;

            // now we read the rest of the file into memory
            var compressedData = new byte[size - 0x20].AsSpan();
            result = compressedStorage.Read(0x20, compressedData);
            if (result.IsFailure()) return result.Miss();

            // the next u32 is the decompressed length
            var decompressedLength = MemoryMarshal.Read<LEUInt32>(compressedData.Slice(0)).Value;
            var decompressedArr = new byte[decompressedLength];
            var decompressed = decompressedArr.AsSpan();

            // there are 3 more u32s
            var int2 = MemoryMarshal.Read<LEUInt32>(compressedData.Slice(4)).Value;
            var int3 = MemoryMarshal.Read<LEUInt32>(compressedData.Slice(8)).Value;
            var int4 = MemoryMarshal.Read<LEUInt32>(compressedData.Slice(12)).Value; // immediately overwritten in decomp

            var local_80 = ((ulong)int2 << 32) | int3;

            compressedData = compressedData.Slice(16);

            var read = LzReadHeaderData(out var data, compressedData);
            if (read < 0)
            {
                return ResultFs.DataCorrupted.Value;
            }

            compressedData = compressedData.Slice(read);

            int2 = data.Max_31_32;
            var _max_31_32_b = (byte)(data.Max_31_32 & 0xff);
            var readBytes = (1u << _max_31_32_b) - 1;

            uint incr;
            if (readBytes + 1 < 0x16a0a)
            {
                incr = ((readBytes + 2) * (readBytes + 1)) >> 1;
            }
            else
            {
                incr = uint.MaxValue;
            }

            var fillIters = incr;
            var longFillIters = (ulong)fillIters;
            var allocateSize = ulong.CreateSaturating(16 * new UInt128(0, longFillIters));
            var allocatedArr = new byte[(int)allocateSize];
            var allocated = allocatedArr.AsSpan();

            if (incr == 0) return ResultFs.DatabaseCorrupted.Value;

            for (var offs = 8; longFillIters > 0; longFillIters--, offs += 0x10)
            {
                allocated.Slice(offs - 8, 4).Clear();
                allocated.Slice(offs - 4, 8).Fill(0xff);
                allocated[offs + 4] = 0xff;
                offs += 0x10;
                longFillIters--;
            }

            readBytes = (1u << _max_31_32_b) - 1;
            var readBytes2 = readBytes + 1;

            var readArg5 = (int)((((int2 + 7) >> 0x1f) & 7) + int2 + 7) >> 3;

            var one = 1;
            var zero = 0uL;

            read = LzReadInt(out var value, compressedData, 7, readArg5);
            compressedData = compressedData.Slice(read);
            Helpers.Assert(read >= 0);
            if (value == 0)
            {
                value = readBytes2;
            }

            if (readBytes2 * 4 < (readArg5 + 4) * value)
            {
                one = -1;
                value = readBytes2;
            }

            var x = one;
            var y = zero;
            int amtRead;
            if (value != 0)
            {
                do
                {
                    int2 = (uint)y;
                    if (x > 0)
                    {
                        amtRead = LzReadInt(out int2, compressedData, 7, readArg5);
                        compressedData = compressedData.Slice(amtRead);
                    }

                    amtRead = LzReadInt(out var valu, compressedData, 7, 0x20);
                    compressedData = compressedData.Slice(amtRead);
                    one = (int)valu;
                    incr = (uint)y + 1;
                    MemoryMarshal.Write(allocated.Slice((int)(int2 * 0x10)), one);
                    y = incr;
                }
                while (incr < value);

                int2 = data.Max_31_32;
            }

            if ((int)int2 > 0)
            {
                if (int2 < 0x20)
                {
                    zero = (1u << ((byte)int2)) - 1;
                }
                else
                {
                    zero = ulong.MaxValue;
                }
            }
            int2 = (uint)(zero + 1);

            uint a6, a7, a8;
            while (true)
            {
                a7 = 0xffffffff;
                a8 = 0xfffffffu;
                a6 = 0xfffffffu;
                incr = 0xffffffff;
                longFillIters = zero;
                var curSpan = allocated;
                var incr2 = zero;
                if (int2 == 0) break;
                uint u1, u2, u3, u5;
                var offs = 0;
                do
                {
                    u1 = MemoryMarshal.Read<uint>(allocated.Slice(offs));
                    u5 = (uint)longFillIters;

                    if (u1 != 0 && allocated[offs + 0xc] == 0xff)
                    {
                        incr2++;
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

                    offs += 0x10;
                    longFillIters = u5 + 1;
                }
                while (u5 + 1 < int2);

                if ((uint)incr2 < 2) break;

                longFillIters = int2;

                MemoryMarshal.Write(allocated.Slice((int)((longFillIters * 0x10) + 4)),
                    MemoryMarshal.Read<int>(allocated.Slice((int)incr * 0x10)) +
                    MemoryMarshal.Read<int>(allocated.Slice((int)a7 * 0x10)));
                int2 += 1;
                MemoryMarshal.Write(allocated.Slice((int)(longFillIters * 0x10) + 4), a7);
                MemoryMarshal.Write(allocated.Slice((int)(longFillIters * 0x10) + 8), incr);
                allocated[(int)(a7 * 0x10) + 0xc] = 1;
                allocated[(int)(incr * 0x10) + 0xc] = 0;
            }

            if (int2 <= zero + 1)
            {
                allocated[(int)(a7 * 0x10) + 0xc] = 1;
            }

            if (int2 != 0)
            {
                var lzHeaderData = data;
                int2 = (uint)FUN_5730(in lzHeaderData, decompressed, compressedData, 7, decompressed.Length, allocated, int2);

                if ((int)int2 > 0)
                {
                    // delete allocated
                    ReadOnlySpan<uint> tbl = [
                        0xe9,
                        0x115,
                        0x137,
                        0x1b1
                    ];

                    var offs = 0;
                    if (decompressed.Length != 0)
                    {
                        do
                        {
                            incr = (uint)zero;
                            a6 = incr + 1;
                            zero = a6;
                            longFillIters = (longFillIters + decompressed[offs]) * tbl[(int)(incr & 3)];
                            offs++;
                        }
                        while (a6 < decompressed.Length);
                    }

                    // ???
                    // *(ulonglong*)((longlong) & decompressedDataSpan[1].data + 4) = longFillIters;

                    if (longFillIters != local_80)
                    {
                        return ResultFs.DataCorrupted.Value;
                    }
                }

                Helpers.Assert(int2 == decompressed.Length);
            }

            uncompressed.Reset(MemoryStorage.Adopt(decompressedArr));
            return Result.Success;
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

        private static int LzReadHeaderData(out LzHeaderData data, ReadOnlySpan<byte> compressed)
        {
            data = default;
            data._30 = compressed[0];
            data._31 = compressed[1];
            data._32 = compressed[2];
            data.Max_32_33 = compressed[3];
            data._34 = compressed[4];
            data._35 = compressed[5];

            var readAmt = 6;
            if (data._30 > 6)
            {
                readAmt += (int)data._30 - 6;
            }

            // lz_adjust_data
            var success = true;

            var _31 = data._31;
            data._31 = uint.Clamp(_31, 3, 15);
            success &= _31 == data._31;
            _31 = data._31;

            var _32 = data._32;
            data._32 = uint.Clamp(_32, 3, 15);
            success &= _32 == data._32;
            _32 = data._32;

            data.Max_31_32 = _31;
            if (_31 < _32)
            {
                data.Max_31_32 = _32;
                _31 = _32;
            }
            var _33 = data.Max_32_33 = uint.Max(data.Max_32_33, _32);
            data.Max_32_33 = _33 = uint.Min(_33, 15);

            var _34 = data._34;
            data._34 = uint.Clamp(_34, 0, data.Max_32_33 - 1);
            success &= _34 == data._34;
            _34 = data._34;

            if (_33 - _34 > _31)
            {
                success = false;
                data._34 = _34 = _33 - _31;
            }

            var _35 = data._35;
            data._35 = uint.Clamp(_35, 2, 8);
            success &= _35 == data._35;
            _35 = data._35;

            success &= _32 <= _33;

            if (!success)
            {
                readAmt = -readAmt;
            }

            return readAmt;
        }

        private static int LzReadInt(out uint value, ReadOnlySpan<byte> data, int maskBitIdx, int encNbytes)
        {
            value = 0;

            var maxReads = (((((encNbytes - 1) >> 0x1f) & 7) + encNbytes - 1) >> 3) + 1;
            var reads = 0;
            var idx = 0;
            if (maxReads > 0)
            {
                var len = data.Length;
                var baseBitNo = 0;
                do
                {
                    if (len <= idx) break;
                    var maskedByte = (byte)(0xff >> (7 - maskBitIdx)) & data[idx];
                    Helpers.Assert(maskBitIdx < 8);

                    var resultVal = (byte)(maskBitIdx - 7);
                    if (maskBitIdx - 7 < 1)
                    {
                        resultVal = (byte)((data[idx + 1] >> (maskBitIdx + 1)) | (maskedByte << (-resultVal)));
                    }
                    else
                    {
                        resultVal = (byte)(maskedByte >> resultVal);
                    }

                    idx++;
                    value |= (uint)resultVal << baseBitNo;
                    reads++;
                    baseBitNo += 8;
                }
                while (reads < maxReads);
            }

            if (reads != maxReads)
            {
                idx = -idx;
            }
            return idx;
        }


        private static int FUN_5730(in LzHeaderData headerData, Span<byte> decompressed, Span<byte> compressedData, uint _7, int fileLen, Span<byte> allocated, uint int2)
        {
            var _7_2 = _7;
            byte bzero = 0;
            var cidx = 0;
            var uzero = 0u;
            int iVar5;
            uint uVar4;
            int iVar6;
            int iVar7;
            byte @byte;
            var didx = 0;

            while (true)
            {
                while (true)
                {
                    if (fileLen <= uzero || compressedData.Length <= cidx)
                    {
                        return (int)uzero;
                    }

                    @byte = compressedData[cidx];
                    uVar4 = headerData.Max_31_32;
                    iVar5 = (int)_7_2 - 1;
                    iVar6 = iVar5;
                    if (iVar5 < 0)
                    {
                        iVar6 = 7;
                    }
                    iVar7 = cidx + 1;
                    if (iVar5 >= 0)
                    {
                        iVar7 = cidx;
                    }
                    var uVar1 = FUN_5ba0(compressedData, iVar7, iVar6, uVar4, allocated, int2, ref _7);
                    iVar5 = (int)uVar1;

                    if (((@byte >> ((byte)_7_2)) & 1) == 0) break;
                    if (iVar5 < 0) return -iVar7;

                    var uVar3 = (_7 >> 0x1f) & 7;
                    _7_2 = _7 + uVar3;
                    cidx = (int)(_7_2 >> 3);
                    iVar6 -= (int)((_7_2 & 7) - uVar3);
                    if (iVar6 > 7)
                    {
                        cidx--;
                        iVar6 -= 8;
                    }
                    if (iVar6 < 0)
                    {
                        iVar6 += 8;
                        cidx++;
                    }
                    iVar7 += cidx;
                    uVar3 = (uint)iVar5 + headerData._35;
                    uVar1 = FUN_5ba0(compressedData, iVar7, iVar6, uVar4, allocated, int2, ref _7);
                    if ((int)uVar1 < 0) return -iVar7;

                    uVar4 = (_7 >> 0x1f) & 7;
                    _7_2 = _7 + uVar4;
                    cidx = (int)(_7_2 >> 3);
                    _7_2 = (uint)(iVar6 - ((_7_2 & 7) - uVar4));

                    if (_7_2 > 7)
                    {
                        _7_2 -= 8;
                        cidx--;
                    }
                    if (_7_2 < 0)
                    {
                        _7_2 += 8;
                        cidx++;
                    }
                    cidx += iVar7;
                    var uVar9 = 0u;
                    uVar4 = headerData._34;
                    @byte = (byte)uVar4;

                    if ((int)uVar4 > 8)
                    {
                        var bVar8 = (byte)((0xff >> (-(sbyte)_7_2 + 7)) & compressedData[cidx]);
                        if ((int)_7_2 < 8)
                        {
                            iVar6 = 0;
                            bzero = (byte)((int)_7_2 - 7);
                            if ((int)_7_2 - 7 < 1)
                            {
                                iVar6 = 1;
                                bzero = (byte)((compressedData[cidx + 1] >> (bzero + 8)) | (bVar8 << -bzero));
                            }
                            else
                            {
                                bzero = (byte)(bVar8 >> bzero);
                            }
                        }
                        else
                        {
                            iVar6 = -1;
                        }

                        uVar4 -= 8;
                        cidx += iVar6;
                        uVar9 = (uint)bzero << (byte)uVar4;
                    }

                    if ((int)uVar4 > 0)
                    {
                        var bVar8 = (byte)((0xff >> (-(sbyte)_7_2 + 7)) & compressedData[cidx]);
                        if ((int)_7_2 < 8 && (int)uVar4 - 1 < 8)
                        {
                            iVar6 = 0;
                            iVar5 = (int)(_7_2 - uVar4 + 1);
                            bzero = (byte)iVar5;
                            if (iVar5 < 1)
                            {
                                iVar6 = 1;
                                bzero = (byte)((compressedData[cidx + 1] >> ((byte)(_7_2 - uVar4 + 9))) | (bVar8 << -bzero));
                            }
                            else
                            {
                                bzero = (byte)(bVar8 >> bzero);
                            }
                        }
                        else
                        {
                            iVar6 = -1;
                        }

                        uVar4 &= 0x80000007;
                        if ((int)uVar4 < 0)
                        {
                            uVar4 = ((uVar4 - 1) | 0xfffffff8) + 1;
                        }
                        _7_2 -= uVar4;
                        uVar4 = _7_2 - 8;
                        if ((int)_7_2 < 8)
                        {
                            uVar4 = _7_2;
                        }
                        _7_2 = uVar4 + 8;
                        if ((int)uVar4 >= 0)
                        {
                            _7_2 = uVar4;
                        }
                        cidx += iVar6;
                        uVar9 |= bzero;
                    }

                    if ((int)uVar3 > 0)
                    {
                        uVar4 = headerData._35;
                        var uVar2 = (ulong)uVar3;
                        do
                        {
                            if ((int)uzero < fileLen)
                            {
                                decompressed[didx] = decompressed[(int)(didx - (uVar9 + ((int)uVar1 << @byte) + uVar4))];
                                didx++;
                                uzero++;
                            }

                            uVar2--;
                        }
                        while (uVar2 != 0);
                    }
                }

                if (iVar5 < 0) break;
                uVar4 = (uint)(((int)_7 >> 0x1f) & 7);
                _7_2 = _7 + uVar4;
                cidx = (int)_7_2 >> 3;
                _7_2 = (uint)(iVar6 - ((_7_2 & 7) - uVar4));
                if ((int)_7_2 > 7)
                {
                    _7_2 -= 8;
                    cidx--;
                }
                if ((int)_7_2 < 0)
                {
                    _7_2 += 8;
                    cidx++;
                }
                cidx = iVar7 + cidx;

                if ((iVar5 + 1) > 0)
                {
                    var uVar1 = (ulong)(iVar5 + 1);
                    do
                    {
                        if ((int)uzero < fileLen)
                        {
                            @byte = (byte)((0xff >> (7 - (sbyte)_7_2)) & compressedData[cidx]);
                            if (_7_2 < 8)
                            {
                                iVar6 = 0;
                                bzero = (byte)(_7_2 - 7);
                                if ((int)(_7_2 - 7) < 1)
                                {
                                    bzero = (byte)((compressedData[cidx + 1] >> (bzero + 8)) | (@byte << (-bzero)));
                                }
                                else
                                {
                                    bzero = (byte)(@byte >> bzero);
                                }
                            }
                            else
                            {
                                iVar6 = -1;
                            }
                            cidx += iVar6;
                            decompressed[didx] = bzero;
                            didx++;
                            uzero++;
                        }
                        uVar1--;
                    }
                    while (uVar1 != 0);
                }
            }

            return -iVar7;
        }

        private static uint FUN_5ba0(Span<byte> compressedData, int param_2, int param_3, uint param_4, Span<byte> allocated, uint int2, ref uint _7)
        {
            uint uVar1, uVar2, uVar5, uVar10, uVar11;
            long lVar3, lVar6;
            ulong uVar7, uVar8;
            int iVar9;
            byte bVar4;

            uVar2 = 0;
            uVar5 = 0xffffffff;
            if ((int)param_4 < 1)
            {
                iVar9 = 0;
            }
            else if (param_4 < 0x20)
            {
                iVar9 = (1 << ((byte)param_4)) - 1;
            }
            else
            {
                iVar9 = -1;
            }
            uVar1 = (uint)(iVar9 + 1);
            if (uVar1 < 0x16a0a)
            {
                uVar5 = (uint)(((iVar9 + 2) * uVar1) >> 1);
            }
            uVar11 = 0xffffffff;
            uVar10 = 0xffffffff;
            if (uVar1 < int2)
            {
                if (uVar1 != 0 && uVar5 != 0)
                {
                    lVar6 = param_2;
                    uVar7 = int2 - 1;
                    while (true)
                    {
                        if ((uint)uVar7 < uVar1)
                        {
                            _7 = uVar2;
                            return (uint)uVar7;
                        }
                        bVar4 = (byte)((compressedData[(int)lVar6] >> param_3) & 1);
                        uVar8 = MemoryMarshal.Read<uint>(allocated.Slice((int)((uVar7 * 0x10) + 8)));
                        if (allocated[(int)((uVar8 * 0x10) + 0xc)] != bVar4)
                        {
                            uVar8 = MemoryMarshal.Read<uint>(allocated.Slice((int)((uVar7 * 0x10) + 4)));
                            if (allocated[(int)((uVar8 * 0x10) + 0xc)] != bVar4)
                            {
                                break;
                            }
                        }
                        iVar9 = param_3 - 1;
                        param_3 = iVar9;
                        if (iVar9 < 0)
                        {
                            param_3 = 7;
                        }
                        lVar3 = lVar6 + 1;
                        if (iVar9 >= 0)
                        {
                            lVar3 = lVar6;
                        }
                        uVar2 += 1;
                        lVar6 = lVar3;
                        uVar7 = uVar8;
                    }
                    _7 = uVar2;
                    return uint.MaxValue;
                }
                return uint.MaxValue;
            }
            if (int2 != 0)
            {
                do
                {
                    uVar10 = uVar11;
                    if (MemoryMarshal.Read<int>(allocated) != 0)
                    {
                        _7 = 1;
                        uVar10 = uVar2;
                    }
                    uVar2++;
                    allocated = allocated.Slice(0x10);
                    uVar11 = uVar10;
                }
                while (uVar2 < int2);
            }
            return uVar10;
        }
    }
}
