using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace MahoyoHDRepack.ScriptText.DeepLuna;

internal sealed class DeepLunaTextProcessor
{
    [Flags]
    private enum CurrentAtLineState
    {
        None = 0,
        Italics = 1 << 0, // @i
        Backwards = 1 << 1, // @b
        Antiqua = 1 << 2, // @g
    }

    [Flags]
    private enum StringFormatState
    {
        None = 0,
        Italics = 1 << 0,
        Backwards = 1 << 1,
        Antiqua = 1 << 2,
        BackwardsItalics = 1 << 3,
        Flipped = 1 << 4,
        VerticalFlipped = 1 << 5,

        SimultaneousMark = 1 << 16,
        InsertAtK = 1 << 17,

        AtStateMask = Italics | Backwards | Antiqua,
        OneSegmentOnlyMask = SimultaneousMark | InsertAtK,
        NonstandardFormatMask = ~AtStateMask & ~OneSegmentOnlyMask,
    }

    private static StringFormatState MergeFormatStates(CurrentAtLineState atState, StringFormatState formatState)
        => (StringFormatState)atState | formatState;

    public string ConvertDeepLunaText(string text)
    {
        var textSegments = new List<(StringFormatState Format, string Text)>();
        var sb = new StringBuilder();

        // formatState only contains deepLuna control codes
        var formatState = StringFormatState.None;
        // atState contains only the @-directive states
        var atState = CurrentAtLineState.None;

        var shouldCenter = false;
        var shouldRightAlign = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            switch (c)
            {
                case '@':
                    // append the segment
                    textSegments.Add((MergeFormatStates(atState, formatState), sb.ToString()));
                    _ = sb.Clear();
                    formatState &= ~StringFormatState.OneSegmentOnlyMask;

                    // handle an @-code
                    switch (text[++i])
                    {
                        case 'i':
                            atState |= CurrentAtLineState.Italics;
                            break;
                        case 'g':
                            atState |= CurrentAtLineState.Antiqua;
                            break;
                        case 'b':
                            atState |= CurrentAtLineState.Backwards;
                            break;
                        case 'r':
                            // reset
                            atState = CurrentAtLineState.None;
                            break;
                        case 'k':
                            // what does this do? Don't know, but we should preserve it
                            formatState |= StringFormatState.InsertAtK;
                            break;
                        case 't':
                            // marks simultaneous segments
                            formatState |= StringFormatState.SimultaneousMark;
                            break;
                        case var x:
                            Debug.Fail($"Unrecognized control code '@{x}'");
                            break;
                    }
                    break;

                case '%' when text.AsSpan(i) is ['%', '{', ..]:
                    // append the segment
                    textSegments.Add((MergeFormatStates(atState, formatState), sb.ToString()));
                    _ = sb.Clear();
                    formatState &= ~StringFormatState.OneSegmentOnlyMask;

                    {
                        var nameSpan = text.AsSpan(i + 2);

                        var endBlock = nameSpan.IndexOf('}');
                        Debug.Assert(endBlock > 0);

                        i += 2 + endBlock;

                        nameSpan = nameSpan.Slice(0, endBlock);

                        var close = false;
                        if (nameSpan is ['/', .. var rest])
                        {
                            nameSpan = rest;
                            close = true;
                        }

                        // now do the right thing according to the name
                        StringFormatState formatMask;
                        switch (nameSpan)
                        {
                            case "n":
                                // force newline
                                _ = sb.Append('^');
                                continue;
                            case "s":
                                // force space
                                _ = sb.Append(' ');
                                continue;
                            case "center":
                                // center the text in the line
                                shouldCenter = true;
                                continue;
                            case "align_right":
                                shouldRightAlign = true;
                                continue;
                            case "no_break":
                                // we don't do manual breaking
                                continue;
                            case "nothing":
                                // documented no-op
                                continue;
                            case "force_glue":
                                // we don't do explicit gluing
                                continue;
                            case "e_35":
                                _ = sb.Append('\uE200');
                                continue;
                            case "i":
                                // italics
                                formatMask = StringFormatState.Italics;
                                break;
                            case "r":
                                // flipped (reverso)
                                formatMask = StringFormatState.Flipped;
                                break;
                            case "ri":
                                // reverso italics (but they're actually also backwards italics)
                                formatMask = StringFormatState.BackwardsItalics | StringFormatState.Flipped;
                                break;
                            case "g":
                                // antiqua
                                formatMask = StringFormatState.Antiqua;
                                break;
                            case "flip_vertical":
                                // vertical flip
                                formatMask = StringFormatState.VerticalFlipped;
                                break;
                            default:
                                Debug.Fail($"Unrecognized deepLuna formatting '${{{nameSpan}}}'");
                                continue;
                        }

                        if (!close)
                        {
                            formatState |= formatMask;
                        }
                        else
                        {
                            formatState &= ~formatMask;
                        }
                    }
                    break;

                default:
                    _ = sb.Append(c);
                    break;
            }
        }

        if (sb.Length > 0)
        {
            textSegments.Add((MergeFormatStates(atState, formatState), sb.ToString()));
        }

        return BuildFinalLine(sb, textSegments, shouldCenter, shouldRightAlign);
    }

    private const int PrivateUseOffset = 0xE000;
    private const int FirstModifiedCodepoint = 0x21;
    private const int LastModifiedCodepoint = 0x7E;
    private const int RangeSize = 0x80;

    private const int ForwardItalicAmount = -13;

    public record struct SpecialFormatOpts(
        int ItalicAmt,
        bool HorizFlip,
        bool VertFlip
        );

    private static SpecialFormatOpts GetFormatOptsForFormat(StringFormatState formatState)
    {
        return new(
            (formatState & (StringFormatState.Italics | StringFormatState.BackwardsItalics)) switch
            {
                StringFormatState.None => 0,
                StringFormatState.Italics => ForwardItalicAmount,
                StringFormatState.BackwardsItalics => -ForwardItalicAmount,
                _ => 0
            },
            formatState.Has(StringFormatState.Flipped),
            formatState.Has(StringFormatState.VerticalFlipped));
    }

    private readonly Dictionary<SpecialFormatOpts, int> specialFormatOffsetIndex = new();
    private readonly List<Rune> extraFormatCodepoints = new();

    private string BuildFinalLine(StringBuilder sb, List<(StringFormatState Format, string Text)> segments, bool shouldCenter, bool shouldRightAlign)
    {
        _ = sb.Clear();

        var atState = StringFormatState.None;

        static void SetAtFormatState(StringBuilder sb, ref StringFormatState atState, StringFormatState newState)
        {
            newState &= StringFormatState.AtStateMask;

            // if we need to remove ANY, we need to full-reset
            if (((atState ^ newState) & atState) != 0)
            {
                _ = sb.Append("@r");
                atState = StringFormatState.None;
            }

            // if we have some to add, add them
            var toAddFlags = (atState ^ newState) & newState;
            if (toAddFlags != 0)
            {
                // note: @b should be first, because I *think* the engine cares
                if (toAddFlags.Has(StringFormatState.Backwards))
                {
                    _ = sb.Append("@b");
                    atState |= StringFormatState.Backwards;
                }
                if (toAddFlags.Has(StringFormatState.Italics))
                {
                    _ = sb.Append("@i");
                    atState |= StringFormatState.Italics;
                }
                if (toAddFlags.Has(StringFormatState.Antiqua))
                {
                    _ = sb.Append("@g");
                    atState |= StringFormatState.Antiqua;
                }
            }
        }

        string? nextPreTag = null;

        // TODO: figure out something for shouldCenter
        // For shouldRightAlign, our strategy is to @b (which puts the text on the right edge of the screen), and just emit the text fully backwards.

        var iterDir = shouldRightAlign ? -1 : 1;
        for (var i = shouldRightAlign ? segments.Count - 1 : 0;
            i >= 0 && i < segments.Count;
            i += iterDir)
        {
            var (format, text) = segments[i];

            if (text.Length == 0) continue;

            if (shouldRightAlign)
            {
                format ^= StringFormatState.Backwards;
            }

            if (nextPreTag is { } tag)
            {
                _ = sb.Append(tag);
                nextPreTag = null;
            }

            if (format.Has(StringFormatState.SimultaneousMark))
            {
                format &= ~StringFormatState.SimultaneousMark;
                // this segment was marked InsertAtT, emit the marker
                if (!shouldRightAlign)
                {
                    _ = sb.Append("@t");
                }
                else
                {
                    // if we're right-aligning, it should actually be at the *end* of this segment though
                    nextPreTag += "@t";
                }
            }
            if (format.Has(StringFormatState.InsertAtK))
            {
                format &= ~StringFormatState.InsertAtK;
                // this segment was marked InsertAtK, emit the marker
                if (!shouldRightAlign)
                {
                    _ = sb.Append("@k");
                }
                else
                {
                    // if we're right-aligning, it should actually be at the *end* of this segment though
                    nextPreTag += "@k";
                }
            }

            // lets apply @-states, if we can
            var fmtAtStates = format & StringFormatState.AtStateMask;

            if (format.Has(StringFormatState.Flipped) || format.Has(StringFormatState.VerticalFlipped))
            {
                // if we're requesting flipped or vertical-flipped, clear italics, because we need to handle that ourselves
                fmtAtStates &= ~StringFormatState.Italics;
            }

            if (format.Has(StringFormatState.Flipped | StringFormatState.BackwardsItalics) && !format.Has(StringFormatState.VerticalFlipped))
            {
                // flipped + backwards italics is just flipped + engine italics
                // but only when not also vertical-flipped, ofc
                fmtAtStates |= StringFormatState.Italics;
                format &= ~StringFormatState.BackwardsItalics;
            }

            SetAtFormatState(sb, ref atState, fmtAtStates);

            var offsetIndex = -1;

            // if we have other flags, determine the correct codepoint offset index
            var otherFlags = format & StringFormatState.NonstandardFormatMask;
            var specialFormattingKind = format & ~(StringFormatState.Antiqua | StringFormatState.Backwards);
            if (otherFlags != 0)
            {
                var stateOpts = GetFormatOptsForFormat(specialFormattingKind);
                if (!specialFormatOffsetIndex.TryGetValue(stateOpts, out var idx))
                {
                    specialFormatOffsetIndex.Add(stateOpts, idx = specialFormatOffsetIndex.Count);
                }
                offsetIndex = idx;
            }

            if (offsetIndex == -1)
            {
                if (!shouldRightAlign)
                {
                    // no offseting is necessary, we can just append the string to the string builder
                    _ = sb.Append(text);
                }
                else
                {
                    // we need to do a unicode-correct backwards enumeration
                    for (var j = text.Length - 1; j >= 0; j--)
                    {
                        var c = text[j];
                        // if c is the low surrogate, the immediately prior char must be a high surrogate, which always needs to be appended first
                        if (char.IsLowSurrogate(c))
                        {
                            _ = sb.Append(text[--j]);
                        }
                        _ = sb.Append(c);
                    }
                }
            }
            else
            {
                // we need to offset, do that
                var segmentBase = PrivateUseOffset + (RangeSize * offsetIndex);
                var asciiOfset = segmentBase - FirstModifiedCodepoint;

                for (var j = shouldRightAlign ? text.Length - 1 : 0; j >= 0 && j < text.Length; j += iterDir)
                {
                    var c = text[j];
                    var cp = new Rune(c);

                    if (!shouldRightAlign)
                    {
                        if (char.IsHighSurrogate(c))
                        {
                            cp = new Rune(c, text[++j]);
                        }
                    }
                    else
                    {
                        if (char.IsLowSurrogate(c))
                        {
                            cp = new Rune(text[--j], c);
                        }
                    }

                    if (cp.Value is >= FirstModifiedCodepoint and <= LastModifiedCodepoint)
                    {
                        //SetAtFormatState(sb, ref atState, fmtAtStates);
                        _ = sb.Append(null, $"{new Rune(cp.Value + asciiOfset)}"); // note: this codepath hits AppendSpanFormattable
                    }
                    else
                    {
                        if (!Rune.IsWhiteSpace(cp)
                            && cp.Value is not 0x25A0 and not 0x2015 // note: engine specially recognizes a few chars, so don't replace those
                            && Rune.GetUnicodeCategory(cp)
                                is not System.Globalization.UnicodeCategory.DashPunctuation)
                        {
                            var extraIndex = extraFormatCodepoints.IndexOf(cp);
                            if (extraIndex < 0)
                            {
                                extraIndex = extraFormatCodepoints.Count;
                                extraFormatCodepoints.Add(cp);
                            }

                            if (extraIndex >= RangeSize - (LastModifiedCodepoint - FirstModifiedCodepoint + 1))
                            {
                                throw new InvalidOperationException("Too many extra codepoints!");
                            }
                            extraIndex += LastModifiedCodepoint - FirstModifiedCodepoint + 1;

                            cp = new Rune(segmentBase + extraIndex);
                        }
                        _ = sb.Append(null, $"{cp}"); // note: this codepath hits AppendSpanFormattable
                    }
                }
            }
        }

        if (atState.Has(StringFormatState.Backwards))
        {
            // if the last segment was backwards, we want to add a char to fix some engine funkiness
            _ = sb.Append(' ');
        }

        return sb.ToString();
    }

    public sealed class FontInfo
    {
        public required int RangeSize { get; init; }
        public required int AutoMinCodepoint { get; init; }
        public required int AutoMaxCodepoint { get; init; }
        public required IEnumerable<SpecialFormatOpts> FormatOptions { get; init; }
        public required IEnumerable<int> ExtraCodepoints { get; init; }
    }

    public FontInfo GetFontInfoModel()
    {
        return new FontInfo
        {
            RangeSize = RangeSize,
            AutoMinCodepoint = FirstModifiedCodepoint,
            AutoMaxCodepoint = LastModifiedCodepoint,
            FormatOptions = specialFormatOffsetIndex.OrderBy(kvp => kvp.Value).Select(kvp => kvp.Key),
            ExtraCodepoints = extraFormatCodepoints.Select(rune => rune.Value)
        };
    }
}

[JsonSourceGenerationOptions(MaxDepth = 5)]
[JsonSerializable(typeof(DeepLunaTextProcessor.FontInfo))]
internal sealed partial class FontInfoJsonContext : JsonSerializerContext
{

}
