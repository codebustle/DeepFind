# DeepFind

DeepFind searches the names, paths, and text of files in folders you choose. It is a native WPF desktop application with an offline Lucene index and SQLite tracking. Windows 11 x64 is the primary target. Windows 10 compatibility depends on the edition and build and requires validation.

## Build and run

Install the .NET 10 SDK version specified in `global.json`. Run these commands from the repository root on Windows.

```powershell
dotnet build DeepFind.slnx -c Release
dotnet test DeepFind.slnx -c Release --no-build
dotnet run --project src/DeepFind.App -c Release
```

The test project can also run on Linux with `dotnet test tests/DeepFind.Tests -c Release`. It covers all supported document formats, restart persistence, query syntax, recovery, reconciliation, and a synthetic corpus of over 10,000 files.

## Search your files

1. Open **Index settings**, add one or more folders, and choose **Apply changes**. Indexing runs while DeepFind is open.
2. Enter a word such as `apple`, quote an exact phrase such as `"hellow world"`, or use `AND`, `OR`, `filename:`, and `ext:`. Adjacent terms require both matches. The extension restriction applies to the whole search.
3. Filter or sort the results. Press Enter on a selected result to open the file, Ctrl+P to preview indexed text, or Ctrl+C to copy its path. The result menu also opens Explorer. Ctrl+F or Ctrl+L focuses search.

PDF, DOCX, XLSX, PPTX, TXT, Markdown, CSV, and HTML are supported. Image-only PDFs need OCR. Other formats can optionally appear in filename and path results. PDF excerpts show the page, and spreadsheet excerpts show the worksheet and cell when known. Extraction is bounded to the first million characters or 20,000 text segments. Large or unreadable files can still appear by name. The **Status** tab shows document notices and allows retries, pause, resume, clear, and rebuild.

## Local data

The index stores copies of extracted text under `%LOCALAPPDATA%\DeepFind` by default. Removing a source folder from **Index settings** removes its indexed files unless another selected folder covers them. DeepFind preserves indexed text from temporarily unavailable folders, even if source permissions have changed. **Clear index** deletes the current searchable content and pauses indexing. Changing the index location creates or reuses an index after restart. Clear the old index first if you want to remove its stored text. Diagnostic logs omit filenames, search terms, and extracted text.

## Build the installer

Run on Windows x64. The MSI includes a self-contained .NET deployment and installs per user.

```powershell
dotnet publish src/DeepFind.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
dotnet build installer/DeepFind.Installer.wixproj -c Release
```

Open `DeepFind-Setup-x64.msi` from the installer build output and follow the setup wizard. The installer is unsigned. Close DeepFind before upgrading or uninstalling. To keep settings and indexed data, uninstall through Windows Settings. To remove the default data directory as part of uninstall, run `msiexec /x DeepFind-Setup-x64.msi REMOVEINDEX=1` with the installer used for the installed version. Clear any custom index location from the application before uninstalling.

Before release, verify install, upgrade, and both uninstall paths on Windows. Validate large-index performance on the target hardware, Windows keyboard and Contrast themes, and interruption during an active scan.
