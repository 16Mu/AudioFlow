param([switch]$Verify)
$ErrorActionPreference = 'Stop'
$audioProjectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$audioBuildProperties = [xml](Get-Content (Join-Path $audioProjectRoot 'Directory.Build.props') -Raw)
$audioReleaseVersion = [string]$audioBuildProperties.Project.PropertyGroup.Version
$audioDisplayVersion = [string]$audioBuildProperties.Project.PropertyGroup.ProductDisplayVersion
if ([string]::IsNullOrWhiteSpace($audioDisplayVersion)) { $audioDisplayVersion = $audioReleaseVersion }
$audioTargetFramework = [string]$audioBuildProperties.Project.PropertyGroup.TargetFramework
Push-Location -LiteralPath $audioProjectRoot
try {
    vsm current
    dotnet tool restore
    dotnet build src/AudioFlow.App/AudioFlow.App.csproj -c Release -r win-x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
    if ($Verify) {
        dotnet build tests/AudioFlow.Verification/AudioFlow.Verification.csproj -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Verification build failed.' }
        & (Join-Path $audioProjectRoot ('tests\AudioFlow.Verification\bin\Release\' + $audioTargetFramework + '\AudioFlow.Verification.exe')) --unit
        if ($LASTEXITCODE -ne 0) { throw 'Verification failed.' }
    }
    $audioReleaseDirectory = Join-Path $audioProjectRoot ('dist\AudioFlow-' + $audioDisplayVersion)
    New-Item -ItemType Directory -Path $audioReleaseDirectory -Force | Out-Null
    $audioBuildId = [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $audioPublishDirectory = Join-Path $audioProjectRoot ('.cache\publish-' + $audioBuildId + '\portable')
    New-Item -ItemType Directory -Path $audioPublishDirectory -Force | Out-Null
    dotnet publish src/AudioFlow.App/AudioFlow.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $audioPublishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath 'docs\使用说明.md' -Destination $audioPublishDirectory -Force
    Copy-Item -LiteralPath 'THIRD_PARTY_NOTICES.md' -Destination $audioPublishDirectory -Force
    dotnet wix build installer/AudioFlow.wxs -arch x64 -d "ProjectRoot=$audioProjectRoot" -d "PublishDir=$audioPublishDirectory" -d "ReleaseVersion=$audioReleaseVersion" -pdbtype none -out (Join-Path $audioReleaseDirectory ('AudioFlow-' + $audioDisplayVersion + '-x64.msi'))
    if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }
    Compress-Archive -LiteralPath $audioPublishDirectory -DestinationPath (Join-Path $audioReleaseDirectory ('AudioFlow-' + $audioDisplayVersion + '-portable.zip')) -Force
    Copy-Item -LiteralPath 'docs\使用说明.md' -Destination $audioReleaseDirectory -Force
    Copy-Item -LiteralPath 'docs\发布说明.md' -Destination $audioReleaseDirectory -Force
    Copy-Item -LiteralPath 'docs\验证报告.md' -Destination $audioReleaseDirectory -Force
    $audioUnpackedDirectory = Join-Path $audioReleaseDirectory ('便携版-' + $audioBuildId)
    New-Item -ItemType Directory -Path $audioUnpackedDirectory -Force | Out-Null
    Copy-Item -LiteralPath $audioPublishDirectory -Destination $audioUnpackedDirectory -Recurse
    Set-Content -LiteralPath (Join-Path $audioReleaseDirectory '运行最新版.txt') -Value (Join-Path $audioUnpackedDirectory 'portable\AudioFlow.exe') -Encoding utf8
    $audioSourceFiles = @(rg --files --hidden src tests docs scripts installer -g '!**/bin/**' -g '!**/obj/**')
    $audioSourceFiles += @('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'Directory.Build.props', 'global.json', 'dotnet-tools.json', '.tool-versions', '.gitignore')
    $audioSourceArchive = Join-Path $audioReleaseDirectory ('AudioFlow-' + $audioDisplayVersion + '-source.zip')
    $audioSourceStream = [System.IO.File]::Open($audioSourceArchive, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    $audioSourceZip = [System.IO.Compression.ZipArchive]::new($audioSourceStream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($audioSourceFile in ($audioSourceFiles | Sort-Object -Unique)) {
            $audioSourcePath = [System.IO.Path]::GetFullPath((Join-Path $audioProjectRoot $audioSourceFile))
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($audioSourceZip, $audioSourcePath, $audioSourceFile.Replace('\','/'), [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $audioSourceZip.Dispose(); $audioSourceStream.Dispose() }
    Get-ChildItem -LiteralPath $audioReleaseDirectory -File | Where-Object Name -ne 'checksums.json' | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $audioReleaseDirectory 'checksums.json') -Encoding utf8
    Write-Output $audioReleaseDirectory
}
finally { Pop-Location }
