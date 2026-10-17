param([string]$Root)
$ErrorActionPreference='Stop'
$workspace=(Resolve-Path -LiteralPath $Root).Path
$testRoot=Join-Path $workspace 'artifacts/setup-058-smoke'
$installDir=Join-Path $testRoot 'installed'
$data=Join-Path $testRoot 'profile'
New-Item -ItemType Directory -Force -Path $testRoot,$data | Out-Null
$sentinel=Join-Path $data 'preserve.txt'
[IO.File]::WriteAllText($sentinel,'keep-user-downloads-and-settings')
$setup=Join-Path $workspace 'release/setup/AAA-Download-Manager-0.5.8-Isolated-Test.exe'
$result=@{result='FAIL';isolatedAppId=$true;realPowerActions=0;personalBrowserModified=$false}
function RunHidden([string]$Exe,[string[]]$Arguments){
 $process=Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru -Wait -WindowStyle Hidden
 if($process.ExitCode -ne 0){throw "Process failed ($($process.ExitCode)): $Exe"}
 return $process.ExitCode
}
try {
 $rejectedDir=Join-Path $testRoot 'unaccepted-install'
 $rejected=Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/DIR=`"$rejectedDir`"","/LOG=`"$(Join-Path $testRoot 'unaccepted.log')`"") -PassThru -Wait -WindowStyle Hidden
 if($rejected.ExitCode -eq 0 -or (Test-Path -LiteralPath (Join-Path $rejectedDir 'DownloadManager.exe'))){throw 'Installer accepted missing terms consent'}
 $result.unacceptedSilentInstallBlocked=$true
 $result.install=RunHidden $setup @('/ACCEPTTERMS=1','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/TASKS=',"/DIR=`"$installDir`"","/LOG=`"$(Join-Path $testRoot 'install.log')`"")
 $exe=Join-Path $installDir 'DownloadManager.exe'
 if((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -notlike '0.5.8*'){throw 'Wrong application version'}
 if((Get-Item -LiteralPath $exe).VersionInfo.ProductName -ne 'AAA Download Manager'){throw 'Wrong application brand'}
 $manifest=Get-Content -LiteralPath (Join-Path $installDir 'browser-extension/chromium/manifest.json') -Raw | ConvertFrom-Json
 if($manifest.name -ne '__MSG_appName__' -or (Get-Content -LiteralPath (Join-Path $installDir 'browser-extension/chromium/_locales/en/messages.json') -Raw | ConvertFrom-Json).appName.message -ne 'AAA Download Manager'){throw 'Wrong extension brand'}
 if($manifest.version -ne '0.5.3'){throw 'Wrong Chrome extension version'}
 foreach($relative in @('legal/TERMS-OF-USE.txt','DownloadManager.dll','DownloadManager.Core.dll','browser-extension/chromium/icons/on-16.png','browser-extension/chromium/icons/off-16.png','browser-extension/firefox/icons/on-16.png','browser-extension/firefox/icons/off-16.png','browser-extension/chromium/background.js','browser-extension/chromium/intercept.js','browser-extension/firefox/background.js','browser-extension/firefox/intercept.js','media-tools/bin/ffmpeg.exe','media-tools/LICENSE.txt')) {
  $expected=(Get-FileHash -LiteralPath (Join-Path $workspace ('release/DownloadManager-0.5.8-preview/'+$relative))).Hash
  if((Get-FileHash -LiteralPath (Join-Path $installDir $relative)).Hash -ne $expected){throw "Installed payload mismatch: $relative"}
 }
 if(!(Test-Path -LiteralPath (Join-Path $installDir 'Chrome-Extension.fa.txt'))){throw 'Chrome guide missing'}
 $result.installedUiRender=RunHidden $exe @('--data-dir',"`"$data`"",'--render-ui',"`"$(Join-Path $testRoot 'installed-ui.png')`"",'--report',"`"$(Join-Path $testRoot 'render-error.json')`"")
 if(Test-Path -LiteralPath (Join-Path $testRoot 'render-error.json')){throw 'UI render error report exists'}
 foreach($target in 'chromium','firefox'){foreach($name in 'capture.html','capture.js','popup.html','popup.js','popup.css'){[IO.File]::WriteAllText((Join-Path $installDir ("browser-extension/"+$target+"/"+$name)),"obsolete application page fixture")}}
 $result.reinstall=RunHidden $setup @('/ACCEPTTERMS=1','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/TASKS=',"/DIR=`"$installDir`"","/LOG=`"$(Join-Path $testRoot 'reinstall.log')`"")
 foreach($target in 'chromium','firefox'){foreach($name in 'capture.html','capture.js','popup.html','popup.js','popup.css'){if(Test-Path -LiteralPath (Join-Path $installDir ("browser-extension/"+$target+"/"+$name))){throw "Obsolete page survived upgrade: $target/$name"}}}
 if(!(Test-Path -LiteralPath (Join-Path $testRoot 'installed-ui.png.about.png')) -or !(Test-Path -LiteralPath (Join-Path $testRoot 'installed-ui.png.terms.png'))){throw 'Installed about/legal rendering missing'}
 $result.legalDocumentsVerified=$true
 $result.retiredExtensionPagesRemoved=$true
 $result.chromePayloadVerified=$true
 $result.version='0.5.8'
 $result.result='PASS'
} finally {
 $uninstaller=Join-Path $installDir 'unins000.exe'
 if(Test-Path -LiteralPath $uninstaller){$result.uninstall=RunHidden $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/LOG=`"$(Join-Path $testRoot 'uninstall.log')`"")}
 $result.userDataPreserved=([IO.File]::ReadAllText($sentinel) -eq 'keep-user-downloads-and-settings')
 $result.uninstallRegistrationRemoved=!(Test-Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{83018A7C-D1CA-4E54-9510-B464A2540420}_is1')
 $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'result.json') -Encoding utf8
 if(!$result.userDataPreserved -or !$result.uninstallRegistrationRemoved){throw 'Cleanup/preservation failed'}
}
$result | ConvertTo-Json
