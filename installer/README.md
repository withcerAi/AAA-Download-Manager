# Windows installer — 0.5.6 preview

Build on Windows using the .NET 10 SDK and Inno Setup 6.7.3:

```powershell
./installer/build.ps1 -Dotnet <dotnet.exe> -Compiler <ISCC.exe>
```

Obtain Inno Setup from https://jrsoftware.org/isdl.php and review its current licensing terms. The bundled Farsi translation comes from the official jrsoftware/issrc repository, tag is-6_7_3, Files/Languages/Unofficial/Farsi.isl; its attribution is retained.

The source repository excludes binaries. Full media-enabled packaging requires the optional LGPL runtime described in ../media-tools/README.md. The build script publishes self-contained Windows x64 desktop and native-host output before compiling the installer. It does not sign the resulting package.

The stable application ID supports upgrades. Installation defaults to %LOCALAPPDATA%\Programs\DownloadManager and application data remains in %LOCALAPPDATA%\DownloadManager. Interactive installation requires reviewing the English terms. Authorized unattended installs require /ACCEPTTERMS=1; silent installs never launch the application.

The installer offers desktop shortcuts and current-user native messaging registration for Chrome, Edge and Firefox. The browser bridge task is selected by default and can be deselected. Extensions still require browser-side installation; the installer does not silently load them or change firewall rules. Uninstall removes only owned native-host registrations and application binaries, preserving settings and downloaded files.

The included extensions are version 0.5.3 with a toolbar ON/OFF action. Standard Firefox requires Mozilla signing for permanent extension installation. The desktop and installer remain unsigned previews; see ../CODE_SIGNING.md.

For isolated installation tests, compile with:

```powershell
ISCC /DSetupAppId=83018A7C-D1CA-4E54-9510-B464A2540420 /DSetupFileName=AAA-Download-Manager-0.5.6-Isolated-Test /DIsolatedTest installer/DownloadManager.iss
./installer/smoke-test.ps1 -Root <repository>
```

The alternate test identity, workspace destination and disabled application closing protect the regular installation. Production keeps application ID 167AB087-C613-423C-9984-84448870E972.