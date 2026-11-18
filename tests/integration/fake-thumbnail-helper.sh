#!/bin/sh
# Stand-in for the Skia helper used ONLY when the real one has not been built (a machine without NuGet access). It follows the
# same protocol (image bytes on stdin, a WebP on stdout) but decodes nothing; CI always runs the real helper instead.
cat > /dev/null
[ -n "$FAKE_HELPER_LOG" ] && echo run >> "$FAKE_HELPER_LOG"
printf 'RIFF\034\000\000\000WEBPVP8X\012\000\000\000\000\000\000\000\000\000\000\000'
