using System;

namespace Patchwork
{
    internal sealed class PreviewPage
    {
        internal readonly string Before, After;
        internal readonly int Index, Count;
        internal PreviewPage(string before, string after, int index, int count)
        { Before = before; After = after; Index = index; Count = count; }
    }

    internal static class PreviewPaging
    {
        internal const int PageSize = 32768;

        internal static int PageCount(string before, string after)
        {
            return Math.Max(1, Math.Max(CountPages(before), CountPages(after)));
        }

        internal static PreviewPage GetPage(string before, string after, int pageIndex)
        {
            before = before ?? String.Empty; after = after ?? String.Empty;
            int count = PageCount(before, after);
            if (pageIndex < 0 || pageIndex >= count) throw new ArgumentOutOfRangeException("pageIndex");
            return new PreviewPage(Slice(before, pageIndex), Slice(after, pageIndex), pageIndex, count);
        }

        static int CountPages(string text)
        {
            if (String.IsNullOrEmpty(text)) return 0;
            int count = 0;
            for (int start = 0; start < text.Length; count++) start = NextBoundary(text, start);
            return count;
        }

        static string Slice(string text, int pageIndex)
        {
            int start = 0;
            for (int page = 0; page < pageIndex && start < text.Length; page++) start = NextBoundary(text, start);
            if (start >= text.Length) return String.Empty;
            return text.Substring(start, NextBoundary(text, start) - start);
        }

        static int NextBoundary(string text, int start)
        {
            int end = Math.Min(start + PageSize, text.Length);
            return end < text.Length && end > start && Char.IsLowSurrogate(text[end]) && Char.IsHighSurrogate(text[end - 1]) ? end - 1 : end;
        }
    }
}
