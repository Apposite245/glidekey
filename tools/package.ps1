# Builds the two release zips into dist\ after the unit and smoke tests pass.
#   GlideKey-win-x64.zip               framework-dependent single exe (needs .NET 9 Desktop Runtime)
#   GlideKey-win-x64-selfcontained.zip self-contained single exe
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."
$proj = "$root\cs\GlideKey.csproj"
$dist = "$root\dist"
Get-Process GlideKey -ErrorAction SilentlyContinue | Stop-Process
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $dist | Out-Null

$fd = "$dist\fd"; $sc = "$dist\sc"
dotnet publish $proj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -nologo -v q -o $fd
if ($LASTEXITCODE) { throw "publish (framework-dependent) failed" }
dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -nologo -v q -o $sc
if ($LASTEXITCODE) { throw "publish (self-contained) failed" }

foreach ($mode in '--unittest', '--smoke') {
    foreach ($exe in "$fd\GlideKey.exe", "$sc\GlideKey.exe") {
        $out = New-TemporaryFile
        $p = Start-Process $exe -ArgumentList $mode -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out
        $last = (Get-Content $out | Select-Object -Last 1)
        Remove-Item $out
        if ($p.ExitCode -ne 0) { throw "$mode failed for $exe : $last" }
        "$mode ok ($(Split-Path (Split-Path $exe) -Leaf)): $last"
    }
}

foreach ($pair in @(@($fd, 'GlideKey-win-x64.zip'), @($sc, 'GlideKey-win-x64-selfcontained.zip'))) {
    Copy-Item "$root\README.md", "$root\LICENSE" $pair[0]
    Compress-Archive -Path "$($pair[0])\*" -DestinationPath "$dist\$($pair[1])"
}
Remove-Item $fd, $sc -Recurse -Force
Get-ChildItem $dist | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
