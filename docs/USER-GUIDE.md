# Using Patchwork 1.1

Patchwork changes files in a Windows app using a patch pack. You choose the changes, review them and apply them. Original files are backed up so you can restore them later.

## Get a patch pack

On the welcome screen, choose **Find community patches**. This opens **Patch sources** with the community repository filled in. Choose **Add & download packs** to download its published packs. You can also paste another GitHub repository or release link from an author you trust.

If you already downloaded a `.json` or `.patchwork` file, choose **Open patch file** instead. Importing a pack does not change an app.

## Choose, review and apply

1. In **Patches**, choose the app and pack from the menu at the top. The app version must match the pack. A pack appearing in the library does not guarantee that it matches your installed app.
2. Choose the app folder on the right. Some packs find it automatically. The hint tells you which file or folder the pack expects. **Change folder…** lets you correct an automatic selection.
3. Select the patches you want on the left. You can search the list. Some patches have their own options, and some require or conflict with other patches.
4. Choose **Review changes**. Patchwork checks the files and shows a before/after preview. Choose a file to review; use the page buttons for longer previews. No files have changed yet.
5. Close the target app, then choose **Apply patches**. Protected folders may need a Windows administrator prompt. Keep Patchwork open until the operation finishes.

If preview is blocked, read the message at the bottom. A different app version needs a matching pack; a folder containing files changed outside Patchwork may need its original files recovered. Do not bypass these checks by changing a pack's version label.

## Update or remove patches

**Patch sources** checks for newer packs. These checks update the library; they do not automatically change an app. Keep your desired patches selected, review again and choose **Update patches**. Deselected patches are removed as part of that update.

Open **Backups & restore** to restore the original files. Close the target app first. If an operation was interrupted, the history card offers recovery. Patchwork checks current files before restoring and refuses to overwrite unrelated changes.

Keep the Patchwork data folder and its backups. Updating or uninstalling Patchwork does not automatically undo patches in other apps. Restore those apps before discarding backups or uninstalling if you want their original files back.

## Appearance and app updates

Open **Settings → Appearance**. Choose **Dark** or **Light**, then **Violet**, **Blue**, **Mint** or **Amber**. Changes are immediate and remembered. These colors affect Patchwork; color options in a patch pack affect the target app separately.

**Settings → Patchwork app updates** updates the patcher itself. **Patch sources** updates patch packs. Enable **Include prerelease app versions** to discover 1.1.0; stable latest remains 0.9.3. The installer verifies its download and keeps your separate data folder.

For making packs, use [the authoring guide](AUTHORING.md). For technical details and build instructions, see [the README](../README.md).
