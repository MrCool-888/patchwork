using System;
using System.Text;

namespace Patchwork
{
    internal static class PreviewPagingTests
    {
        internal static void Run()
        {
            Check(null, "", 1);
            Check("before", "after", 1);
            string boundary = new string('a', PreviewPaging.PageSize - 1) + "\U0001F642" + "tail";
            Check(boundary, boundary, 2);
            string repeated = new string('a', PreviewPaging.PageSize - 1) + String.Concat(System.Linq.Enumerable.Repeat("\U0001F642", 20));
            Check(repeated, repeated, 2);
            Check(new string('b', PreviewPaging.PageSize + 5), "short", 2);
            Check("short", new string('a', PreviewPaging.PageSize * 2 + 7), 3);
            Check(new string('a', PreviewPaging.PageSize - 1) + "\U0001F642" + new string('b', PreviewPaging.PageSize * 2), new string('x', PreviewPaging.PageSize * 2 + 1), 4);
            string huge = new string('x', 8700000);
            Check(huge, huge, PreviewPaging.PageCount(huge, huge));
        }

        static void Check(string before, string after, int expectedPages)
        {
            int count = PreviewPaging.PageCount(before, after);
            Assert(count == expectedPages, "Unexpected page count.");
            var left = new StringBuilder(); var right = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                var page = PreviewPaging.GetPage(before, after, i);
                Assert(page.Index == i && page.Count == count, "Page metadata mismatch.");
                Assert(page.Before.Length <= PreviewPaging.PageSize && page.After.Length <= PreviewPaging.PageSize, "Page exceeded its UTF-16 unit limit.");
                Assert(StartsWithLowSurrogate(page.Before) == false && StartsWithLowSurrogate(page.After) == false, "Page started with a low surrogate.");
                Assert(!EndsWithHighSurrogate(page.Before) && !EndsWithHighSurrogate(page.After), "Page ended with a high surrogate.");
                left.Append(page.Before); right.Append(page.After);
            }
            Assert(left.ToString() == (before ?? String.Empty), "Before pages did not reconstruct original.");
            Assert(right.ToString() == (after ?? String.Empty), "After pages did not reconstruct original.");
        }

        static bool EndsWithHighSurrogate(string value) { return value.Length > 0 && Char.IsHighSurrogate(value[value.Length - 1]); }
        static bool StartsWithLowSurrogate(string value) { return value.Length > 0 && Char.IsLowSurrogate(value[0]); }
        static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
