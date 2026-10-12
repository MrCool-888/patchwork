# Patchwork 1.1.1 icon validation

Date: 2026-10-11. Scope: app appearance icons on Windows x64.

The full test runner passed **55 scenarios with zero failures**, including **89 real WPF controller assertions**. For all eight Dark/Light and Violet/Blue/Mint/Amber combinations, tests verify the rendered palette colors and transparency, then query the actual native small and large Windows icon handles using `WM_GETICON`. Their background and logo pixels match the selected accent after live changes. Existing preview/selection/target state remains intact.

A focused startup check also loads saved Light/Blue preferences into a real controller and verifies that the initial window icon uses Blue. Dialogs inherit the owner's themed icon. The sidebar logo already follows the same palette. The version footer is now independent of the release channel.

The neutral executable/shortcut default is generated from the same logo geometry with transparent corners. Its ICO contains 16, 24, 32, 48, 64, 128 and 256-pixel frames; every frame decoded successfully with the expected colors. Windows extracted the embedded icon from the compiled app, and repeated generation was byte-identical. Use `source/build-icons.ps1` to regenerate the asset.

Runtime window icons follow app appearance; file/shortcut icons use the neutral embedded asset because Windows displays those while the app is not running. Pinned/Explorer icon-cache behavior is not controlled or cleared. Native icon handles are verified; every Windows shell/taskbar configuration and physical high-DPI display is not manually tested. Installed app files, user shortcuts and patch history were not modified during testing.
