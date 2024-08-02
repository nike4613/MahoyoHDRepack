using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using CommunityToolkit.Diagnostics;

namespace MahoyoHDRepack.ScriptText.DeepLuna;

internal static class DeepLunaParser
{
    private enum ParseState
    {
        ExpectBlock,
        ParseBlockPrefix,
        ParseBlockId,
        ExpectOpenBlock,
        InBlock,
    }

    public static void Parse(DeepLunaDatabase targetDatabase, DeepLunaTextProcessor processor, string filename, string text)
    {
        var lineno = 1;
        var braceCount = 0;
        var blockStartLine = 0;

        var translatedBuilder = new StringBuilder();
        var tlSpaces = 0;
        var commentBuilder = new StringBuilder();
        var contentHashBuilder = new StringBuilder();
        var thisBlockIsOffset = false;

        var state = ParseState.ExpectBlock;

        for (var span = text.AsSpan(); span.Length > 0; span = span.Length > 0 ? span[1..] : [])
        {
            if (span[0] is '\n') lineno++;

            switch (state)
            {
                default: ThrowHelper.ThrowInvalidOperationException(); break;

                case ParseState.ExpectBlock:
                    // waiting for the start of a block, skip over (non-newline) whitespace
                    switch (span)
                    {
                        case [] or [' ' or '\r' or '\n', ..]: continue; // read newlines correctly
                        case ['[', ..]:
                            state = ParseState.ParseBlockPrefix;
                            _ = contentHashBuilder.Clear();
                            continue;
                        default:
                            // error
                            throw new ArgumentException($"{filename}: Unexpected char '{span[0]}' on line {lineno}");
                    }

                case ParseState.ParseBlockPrefix:
                    // look for the appropriate block prefix
                    // note: we're assuming a slightly better formed syntax than deepLuna actually does; it allows whitespace ANYWHERE here
                    // we only allow it at the beginning and after the colon
                    switch (span)
                    {
                        case [] or [' ' or '\r' or '\n', ..]: continue; // read newlines correctly
                        case ['s', 'h', 'a', ':', ..]:
                            thisBlockIsOffset = false;
                            // skip past everything up to the colon
                            span = span[3..];
                            goto SwitchToParseBlock;
                        case ['o', 'f', 'f', 's', 'e', 't', ':', ..]:
                            thisBlockIsOffset = true;
                            // skip past everything up to the colon
                            span = span[6..];
                            goto SwitchToParseBlock;

                        default:
                            var colonIdx = span.IndexOf(':');
                            var badTag = colonIdx >= 0 ? span.Slice(0, colonIdx) : span;
                            throw new ArgumentException($"{filename}: Bad block prefix tag '{badTag}' on line {lineno}");

                        SwitchToParseBlock:
                            state = ParseState.ParseBlockId;
                            _ = contentHashBuilder.Clear();
                            continue;
                    }

                case ParseState.ParseBlockId:
                    switch (span)
                    {
                        case [] or [' ' or '\r' or '\n', ..]: continue; // read newlines correctly
                        case [>= '0' and <= '9', ..]:
                            break;
                        case [>= 'a', <= 'f', ..] when !thisBlockIsOffset:
                            break;
                        case [']', ..]:
                            // ended block, transition
                            state = ParseState.ExpectOpenBlock;
                            continue;
                        default:
                            throw new ArgumentException($"{filename}: Invalid character '{span[0]}' in offset/hash on line {lineno}");
                    }
                    _ = contentHashBuilder.Append(span[0]);
                    break;

                case ParseState.ExpectOpenBlock:
                    switch (span)
                    {
                        case [] or [' ' or '\r' or '\n', ..]: continue; // read newlines correctly
                        case ['{', ..]:
                            braceCount++;
                            state = ParseState.InBlock;
                            blockStartLine = lineno;
                            _ = translatedBuilder.Clear();
                            _ = commentBuilder.Clear();
                            continue;
                        default:
                            throw new ArgumentException($"{filename}: Expected open-block after block specifier, but found '{span[0]}' on line {lineno}");
                    }

                case ParseState.InBlock:
                    {
                        // this is the important part of the parser
                        var c = span[0];
                        switch (c)
                        {
                            case '\n':
                                // note that whitespace at the end of a line is IGNORED by deepLuna, including the linebreak
                                // (though strictly speaking, it only ignores the line break for injection, which is all we care about here)

                                // by this point, we've already accumulated the text correctly, and just need to reset the whitespace counter
                                tlSpaces = 0;
                                continue;
                            case '\r':
                                // we ALWAYS ignore \r
                                continue;

                            case ' ':
                                // keep track of how many spaces we've seen
                                tlSpaces++;
                                break;

                            case '-' when span is ['-', '-', ..]:
                                {
                                    // this is the beginning of a machine comment, skip to the end of the line
                                    var eolIndex = span.IndexOf('\n');
                                    if (eolIndex < 0) eolIndex = span.Length;
                                    span = span[eolIndex..];
                                    lineno++;
                                    continue;
                                }

                            case '/' when span is ['/', '/', ..]:
                                {
                                    // this is a human comment, stash it and skip
                                    var eolIndex = span.IndexOf('\n');
                                    if (eolIndex < 0) eolIndex = span.Length;
                                    _ = commentBuilder.Append(span[0..eolIndex].Trim()).AppendLine();
                                    span = span[eolIndex..];
                                    lineno++;
                                    continue;
                                }

                            default:
                                // before adding text, add the appropriate number of spaces
                                if (tlSpaces > 0)
                                {
                                    _ = translatedBuilder.Append(' ', tlSpaces);
                                    tlSpaces = 0;
                                }
                                _ = translatedBuilder.Append(c);
                                break;

                            case '{':
                                braceCount++;
                                goto default;

                            case '}':
                                braceCount--;
                                if (braceCount > 0)
                                {
                                    goto default;
                                }

                                // ok, we're done with the TL, lets bank it
                                state = ParseState.ExpectBlock; // always transition out to ExpectBlock
                                if (translatedBuilder.Length == 0 && commentBuilder.Length == 0)
                                {
                                    // utterly useless, empty line, ignore entirely
                                    continue;
                                }

                                // materialize the offset and hash members
                                int? offset = null;
                                ImmutableArray<byte> hash = default;
                                if (thisBlockIsOffset)
                                {
                                    offset = int.Parse(contentHashBuilder.ToString(), CultureInfo.InvariantCulture);
                                }
                                else
                                {
                                    hash = [.. Convert.FromHexString(contentHashBuilder.ToString())];
                                }

                                var line = new DeepLunaLine(filename, blockStartLine, lineno, hash, offset,
                                    translatedBuilder.Length > 0 ? processor.ConvertDeepLunaText(translatedBuilder.ToString()) : null,
                                    commentBuilder.Length > 0 ? commentBuilder.ToString() : null);
                                _ = translatedBuilder.Clear();
                                _ = commentBuilder.Clear();
                                _ = contentHashBuilder.Clear();

                                targetDatabase.InsertLine(line);
                                break;
                        }
                    }
                    break;
            }
        }

        if (state != ParseState.ExpectBlock)
        {
            throw new ArgumentException($"{filename}: Unexpected EOF in state {state}");
        }
    }
}
