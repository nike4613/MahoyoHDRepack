using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace MahoyoHDRepack.ScriptText.DeepLuna;

internal static class DeepLunaTextProcessor
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

    public static string ConvertDeepLunaText(string text)
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

    private static string BuildFinalLine(StringBuilder sb, List<(StringFormatState Format, string Text)> segments, bool shouldCenter, bool shouldRightAlign)
    {
        _ = sb.Clear();

        // TODO: handle shouldCenter and shouldRightAlign

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
                if (toAddFlags.Has(StringFormatState.Italics))
                {
                    _ = sb.Append("@i");
                    atState |= StringFormatState.Italics;
                }
                if (toAddFlags.Has(StringFormatState.Backwards))
                {
                    _ = sb.Append("@b");
                    atState |= StringFormatState.Backwards;
                }
                if (toAddFlags.Has(StringFormatState.Antiqua))
                {
                    _ = sb.Append("@g");
                    atState |= StringFormatState.Antiqua;
                }
            }
        }

        foreach (var (fformat, text) in segments)
        {
            if (text.Length == 0) continue;

            var format = fformat;
            if (format.Has(StringFormatState.SimultaneousMark))
            {
                format &= ~StringFormatState.SimultaneousMark;
                // this segment was marked InsertAtT, emit the marker
                _ = sb.Append("@t");
            }
            if (format.Has(StringFormatState.InsertAtK))
            {
                format &= ~StringFormatState.InsertAtK;
                // this segment was marked InsertAtK, emit the marker
                _ = sb.Append("@k");
            }

            // lets apply @-states, if we can
            var fmtAtStates = format & StringFormatState.AtStateMask;

            if (format.Has(StringFormatState.Flipped) || format.Has(StringFormatState.VerticalFlipped))
            {
                // if we're requesting flipped or vertical-flipped, clear italics, because we need to handle that ourselves
                fmtAtStates &= ~StringFormatState.Italics;
            }

            SetAtFormatState(sb, ref atState, fmtAtStates);

            var offsetIndex = -1;

            // if we have other flags, determine the correct codepoint offset index
            var otherFlags = format & StringFormatState.NonstandardFormatMask;
            var specialFormattingKind = format & ~(StringFormatState.Antiqua | StringFormatState.Backwards);
            if (otherFlags != 0)
            {
                switch (specialFormattingKind)
                {
                    case StringFormatState.BackwardsItalics:
                        offsetIndex = 1;
                        break;
                    case StringFormatState.Flipped:
                        offsetIndex = 2;
                        break;
                    case StringFormatState.Italics | StringFormatState.Flipped:
                        offsetIndex = 3;
                        break;
                    case StringFormatState.BackwardsItalics | StringFormatState.Flipped:
                        offsetIndex = 4;
                        break;
                    case StringFormatState.VerticalFlipped:
                        offsetIndex = 5;
                        break;

                    case var x:
                        Debug.Fail($"Unsupported special formatting {x}");
                        break;
                }
            }

            if (offsetIndex == -1)
            {
                // no offseting is necessary, we can just append the string to the string builder
                _ = sb.Append(text);
            }
            else
            {
                // we need to offset, do that
                var offset = PrivateUseOffset + (RangeSize * offsetIndex) - FirstModifiedCodepoint;

                foreach (var c in text)
                {
                    if ((int)c is >= FirstModifiedCodepoint and <= LastModifiedCodepoint)
                    {
                        SetAtFormatState(sb, ref atState, fmtAtStates);
                        _ = sb.Append((char)(c + offset));
                    }
                    else
                    {
                        if (!char.IsWhiteSpace(c))
                        {
                            // note: we need to ensure we use all formats for parts of the text which aren't affected by our manual remapping
                            // TODO: generate a list of non-ASCII chars that also need processing

                            var fixedFormat = format;
                            if (fixedFormat.Has(StringFormatState.BackwardsItalics | StringFormatState.Backwards))
                            {
                                // backwards backwards-italics turn into normal italics
                                // (we don't want to suppress the @b though, because it physically reverses the order of the characters still)
                                fixedFormat &= ~StringFormatState.BackwardsItalics;
                                fixedFormat |= StringFormatState.Italics;
                            }

                            SetAtFormatState(sb, ref atState, fixedFormat);
                        }
                        _ = sb.Append(c);
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
}
