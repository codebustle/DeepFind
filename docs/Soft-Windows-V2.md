# DeepFind — Soft Windows V2 (Option A)

Approved Figma design: https://www.figma.com/design/hmCi6qih6TmBMM6CHon438?node-id=13-946

- 8 DIP control corners; 10 DIP panel and popup corners. Top-level window shape/caption remain native Windows behavior.
- Original Segoe UI font and result text sizes: 16 DIP filename, 14 DIP path and excerpt. Results retain their readable spacing; long excerpts may grow.
- Separate 36 DIP search input and Search action; clear button clears only the query, keeps filter and sort, and schedules search immediately.
- Compact filters and sort control; first-launch Add folder action; contextual no-results clear action.
- Shared dynamic palette for app chrome, panels, controls, lists, menus, preview and index confirmation. Fluent handles native scrollbars and other platform components. Windows 11 DWM colors follow app appearance; older Windows builds may not support explicit title-bar color. Contrast themes use Windows system brushes.
- Settings separate normal operations from index maintenance, preserve confirmations, keep Appearance accessible, and reflow the check intervals at narrow widths.

## Manual acceptance (Windows)

Use disposable test data. Check both Light and Dark, then System and Windows Contrast themes.

1. Launch on a fresh profile; Add folder opens Folders. Select a test folder, Apply changes, wait for indexing and search for a known term.
2. Type a query, confirm the clear icon appears, invoke it and verify the text becomes empty while the selected sort and type filter remain unchanged. The result list refreshes. Confirm keyboard focus returns to search.
3. With a result selected, check right-click, Ctrl+P and Preview, Enter/open, Show in Explorer and Copy path. Check the menu and preview in both themes.
4. Open Index settings; verify Folders, Status, Preferences, the Appearance menu, pause/resume and the two maintenance confirmations. Choose No first for both destructive actions.
5. Resize the search window to 440×410 and Index settings to 440×405. Verify wrapped controls and stacked preference intervals, usable scrollbars, readable text, and keyboard focus. Repeat at 100%, 125%, and 150% display scaling.
6. Verify the native window title bar and controls on the intended Windows 11 and Windows 10 editions; optional frame colors may depend on OS support.

The search, storage, extraction and indexing engine are unchanged by this visual change. Passing build/test/installer smoke checks do not establish pixel-perfect native rendering or every interaction above.
