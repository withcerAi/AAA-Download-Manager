# Windows installer — 0.4.4 preview

Build with `build.ps1 -Dotnet <dotnet.exe> -Compiler <ISCC.exe>` using .NET SDK 10 and Inno Setup 6.7.3. The compiler source is available from https://jrsoftware.org/isdl.php. Commercial distribution requires reviewing the compiler's commercial license terms. The bundled Farsi installer translation comes from the official `jrsoftware/issrc` repository, tag `is-6_7_3`, `Files/Languages/Unofficial/Farsi.isl`; author attribution is retained in that file.

The stable AppId supports upgrades in place. Default destination is `%LOCALAPPDATA%\Programs\DownloadManager`; application data remains in `%LOCALAPPDATA%\DownloadManager`. Files are self-contained for Windows x64, without a separate .NET installation or network bootstrapper. The installer supports English and Persian, system light/dark appearance, optional desktop shortcut, Start menu shortcuts, standard Windows uninstall registration and an unchecked launch option. Silent installs never launch the app.

No browser registration, startup registration or firewall changes occur during installation. Browser integration is explicitly connected in the application after manually installing the extension. Uninstall runs the browser host's ownership-checked unregister commands before removing binaries. It preserves application data and downloaded files; it does not recursively delete arbitrary folders.

The application and generated installer are unsigned development previews. Do not present them as a complete IDM replacement or a signed commercial release.


Version 0.4.4 bundles the Chrome/Edge extension 0.4.4, its toolbar-only ON/OFF toggle, and local FFmpeg shared media tools. Chrome still requires manual Load unpacked installation; see the installed Chrome-Extension.fa.txt guide. This installer does not silently modify the user's browser.

Installer regression uses an alternate AppId (83018A7C-D1CA-4E54-9510-B464A2540420), a workspace destination, no shortcuts, and CloseApplications=no, so it cannot replace the user's normal installation registration. Compile with /DSetupAppId=83018A7C-D1CA-4E54-9510-B464A2540420 /DSetupFileName=DownloadManager-0.4.4-Isolated-Test /DIsolatedTest, then run smoke-test.ps1 -Root <repository>. The production AppId is unchanged.
