using System;
using System.Windows.Controls;

namespace Patchwork
{
    public partial class MainController
    {
        int previewPageIndex;

        void ResetPreviewPage()
        {
            previewPageIndex = 0;
            RenderPreviewPage();
        }

        void MovePreviewPage(int delta)
        {
            if (preview == null || Control<ComboBox>("PreviewFilePicker").SelectedIndex < 0 || Control<ComboBox>("PreviewFilePicker").SelectedIndex >= preview.Files.Count) return;
            int count = PreviewPaging.PageCount(CurrentPreviewFile().BeforeText, CurrentPreviewFile().AfterText);
            previewPageIndex = Math.Max(0, Math.Min(count - 1, previewPageIndex + delta));
            RenderPreviewPage();
        }

        void RenderPreviewPage()
        {
            var before = Control<TextBox>("BeforeText"); var after = Control<TextBox>("AfterText");
            var previous = Control<Button>("PreviewPreviousButton"); var next = Control<Button>("PreviewNextButton");
            if (preview == null || Control<ComboBox>("PreviewFilePicker").SelectedIndex < 0 || Control<ComboBox>("PreviewFilePicker").SelectedIndex >= preview.Files.Count)
            {
                before.Text = after.Text = String.Empty;
                Control<TextBlock>("PreviewPageLabel").Text = "Page 1 of 1";
                previous.IsEnabled = next.IsEnabled = false;
                return;
            }
            var file = CurrentPreviewFile(); var page = PreviewPaging.GetPage(file.BeforeText, file.AfterText, previewPageIndex);
            Control<TextBlock>("BeforeLabel").Text = !file.BeforeExists ? "Before · file absent" : preview.PreviousJournalId != null ? "Before · currently applied" : "Before · original";
            Control<TextBlock>("AfterLabel").Text = file.AfterExists ? "After applying" : "After · file removed";
            before.Text = page.Before; after.Text = page.After;
            Control<TextBlock>("PreviewPageLabel").Text = "Page " + (page.Index + 1) + " of " + page.Count;
            previous.IsEnabled = page.Index > 0; next.IsEnabled = page.Index + 1 < page.Count;
        }

        FileChange CurrentPreviewFile() { return preview.Files[Control<ComboBox>("PreviewFilePicker").SelectedIndex]; }
    }
}
