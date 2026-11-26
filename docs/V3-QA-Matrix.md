# DeepFind V3 — QA matrix and release gate

Specification: [V3](https://www.figma.com/spec/qG3hLoXVf4EKKzBIjr2T4G). [Product map](https://www.figma.com/board/QNVKnusn2kovV6QJuhWbyV).

This branch is an implementation candidate, not a release. Use disposable sources and indexes. Do not ask the owner to be the first runtime tester. Record the tested SHA, Windows version/build, MSI version, resolution, scaling and theme, actual outcome, screenshots/logs, and defects. A test that has not run is NOT passed.

| Scenario | Spec | Evidence required |
|---|---|---|
| Install, Start Menu target, launch, uninstall | AC-01/02 | Windows CI MSI smoke, interactive Desktop option, upgrade/repair, retention and explicit removal |
| First run, folder picker cancel/add/apply | AC-03 | Windows UI interaction |
| Paging, 1,001 and 10,000 matches, sizes and clamp | AC-04/05 | Automated search tests; Windows UI navigation, responsiveness and index-refresh behavior |
| Live search, cancellation, clear, Enter, keyboard | AC-06/07 | Query tests and Windows UI interaction |
| Modification/name sort and ties | AC-08 | Automated sort tests and Windows UI date formatting |
| Result View controls persisted | AC-09 | Settings roundtrip and Windows UI toggle/restart |
| All format badges and type labels | AC-10 | Fixture search tests, Windows UI inspection |
| Open/reveal/copy/preview, unavailable | AC-11 | Windows UI tests including stale original |
| Clean restart, no unchanged extraction | AC-12 | Integration test, Windows run status and logs |
| Changes/rename/delete/offline | AC-13 | Integration test and real watched/network folders |
| Pending journal and subset scan | AC-14 | Integration tests, interruption/restart on Windows |
| Pause/resume/update/retry | AC-15 | Windows UI and cancellation verification |
| Clear/rebuild confirmation | AC-16 | Windows UI with disposable index |
| Settings validation, storage and persistence | AC-17 | Integration plus Windows UI |
| Light/Dark/System/Contrast; default/min window sizes and 100–200% scaling | AC-18 | Windows visual and keyboard checks |
| Corruption, permissions, storage and recovery | AC-19 | Windows controlled failure cases |
| Build, tests, artifact and all above cases | AC-20 | Exact SHA CI, complete recorded scenario results |

Desktop checkbox and silent opt-in were verified, including actual interactive checked and unchecked Finish and the resulting shortcut/absence (see execution below). No release approval until the complete matrix passes.

## Verified execution (Windows Server 2025, build 26100)

[Run 45758993679](https://github.com/codebustle/DeepFind/actions/runs/45758993679) (f134d8f) and [run 45820915198](https://github.com/codebustle/DeepFind/actions/runs/45820915198) (5b9c2ee) independently passed build, 15 automated tests, installed WPF search/paging, 50/100 page sizes, descending names, all View flags/defaults, settings tab navigation, invalid size, Light theme, pause/resume, declined Clear, silent shortcut and Start Menu install/uninstall; both also passed real V2→V3 upgrade, single product registration, repair, default data retention and explicit removal. [UI screenshots](https://github.com/codebustle/DeepFind/actions/runs/45758993679/artifacts/15088904783) show the default Dark/Light main window and Light Preferences.

Interactive installer test on 5b9c2ee passed Welcome navigation via a scoped actual pointer click, then failed because the native license checkbox exposes no managed TogglePattern. The [next run 45821979028](https://github.com/codebustle/DeepFind/actions/runs/45821979028) (d248300) passed every configured CI step. All 15 tests, real installer Finish both checked and unchecked, exact Desktop target/absence and cleanup, installed keyboard/copy/preview, real local watcher create/modify/rename/delete, confirmed Clear with 51 originals preserved, Resume, real V2 upgrade/repair and data retention/removal PASS. [Native Windows screenshots](https://github.com/codebustle/DeepFind/actions/runs/45821979028/artifacts/15118273673).

## Next runtime coverage

Added installed UI assertions for the remaining three page sizes and last/previous navigation, eight directional sorts using three distinct file sizes/timestamps, visible controls at minimum main/settings dimensions, unavailable source feedback with indexing paused, confirmed and cancelled Rebuild preserving original test files, and restart with saved page size, hidden path, Light theme and index-ready status. [Run 45826388139](https://github.com/codebustle/DeepFind/actions/runs/45826388139) (7160cdf) verified all five sizes, all four page navigation controls and eight directional sorts with real metadata. [440×410 actual screenshot](https://github.com/codebustle/DeepFind/actions/runs/45826388139/artifacts/15119394341) shows the wrapped UI with every command visible. The immediate UIA bounds test ran before WPF layout settled; the SQLite probe failed due legacy PowerShell native string quoting. Both tests now retain their assertions and use stable bounds and a standalone read-only probe. Later scenarios are pending.

The next CI uses separate clean Windows VMs for interface, native MSI, formats/first run/preferences, 10,001-file interruption and corruption recovery, loopback SMB disconnect/reconnect/overlap/retry and paused restart, plus V2 upgrade. A single candidate MSI and eight validated fixtures are built and shared via artifacts. Every job has independently named evidence and a time limit; no preceding run can contaminate its settings or index. Release remains blocked until all AC-01–20 are completely evidenced, including target Windows 11 x64 client, actual DPI/Contrast and remaining edge cases.

## Independent VM results — 55fac9a

[Run 45828551578](https://github.com/codebustle/DeepFind/actions/runs/45828551578) passed build, 15/15 tests, dependency audit, independent real MSI Finish both ways and V2 upgrade/repair/retention. The recovery run committed 3,008 of 10,001 sources, stopped the real process with journal pending (1/0), then restarted and processed exactly 6,993 remaining. It verified real UI 1,000/1,001 match page ranges, page 41 of 41 for 10,001, final rapid-query result and a clean restart record of 10,001 checked, 0 new or changed, 0 notices. [Recovery UI evidence](https://github.com/codebustle/DeepFind/actions/runs/45828551578/artifacts/15122266111). An actual truncated segment manifest raised EndOfStreamException at startup rather than the intended explicit corruption prompt; new targeted exception translation and regression test correct this path, pending runtime verification. Independent interface failed a transient UI Automation PageSize lookup although the control was visibly rendered; poll lookup before selecting. SMB and document-format jobs still pending final logs. Release remains blocked.

## Further verified results — 96c48f1

[Run 45829831123](https://github.com/codebustle/DeepFind/actions/runs/45829831123) passed 16/16 automated tests including truncated manifest regression, native MSI and V2 upgrade. Actual 440×410 main / 440×405 settings all tabs and bounds passed; [interface screenshots](https://github.com/codebustle/DeepFind/actions/runs/45829831123/artifacts/15122611744) show the correct missing-file message despite UIA Text type not returned. Real loopback SMB with overlapping roots, disconnect/cached results/notice, reconnect and Retry all passed; [network screenshots](https://github.com/codebustle/DeepFind/actions/runs/45829831123/artifacts/15122995436). Pausing failed with visible Access to the path is denied; protect atomic settings replacement against brief file-sharing locks, and make the CI reader allow delete sharing. New unit test deliberately holds the old settings file briefly. Corruption re-test repeated 10,001 recovery, deep range and zero unchanged work; log no longer shows Startup failed, but native confirmation lookup failed. Next test scopes actual native controls by verified process/class/label and uses a real hit-tested click. Native decline/accept, paused restart and remaining format/picker scenarios still require execution. Release remains blocked.
