# DeepFind V3 — QA matrix and release gate

Specification: [V3](https://www.figma.com/spec/qG3hLoXVf4EKKzBIjr2T4G). [Product map](https://www.figma.com/board/QNVKnusn2kovV6QJuhWbyV).

This branch is an implementation candidate, not a release. Use disposable sources and indexes. Do not ask the owner to be the first runtime tester. Record the tested SHA, Windows version/build, MSI version, resolution, scaling and theme, actual outcome, screenshots/logs, and defects. A test that has not run is NOT passed.

| Scenario | Spec | Evidence required |
|---|---|---|
| Install, Start Menu target, launch, uninstall | AC-01/02 | Windows CI MSI smoke (shortcut checks added), plus interactive Desktop option, upgrade/repair and data retention still required |
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

Desktop checkbox on final installer page remains open. Do not mark DF-02 resolved until actual installation is verified. No release approval until the complete matrix passes.
