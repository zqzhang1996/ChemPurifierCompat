<#
.SYNOPSIS
    编译当前模组项目，并把产物部署到游戏的 Mods 目录。

.DESCRIPTION
    脚本放在模组项目根目录（与 .csproj、manifest.xml 同级）。
    所有路径都相对脚本自身位置（$PSScriptRoot）解析，所以每个项目里放一份
    完全相同的副本即可，不需要写死项目名或绝对路径。

    部署内容：
        bin\<Configuration>\*\*.dll   ->  <Mods>\<Project>\
        manifest.xml                  ->  <Mods>\<Project>\
        Localization\*                ->  <Mods>\<Project>\Localization\

    目标目录默认按相对路径推导：
        <游戏根>\Modding\<Project>  ->  <游戏根>\Mods\<Project>
    也就是脚本所在的 Modding 目录的上一级，再进 Mods。

    Debug 构建会额外复制 .pdb（异常堆栈带行号）；Release 只复制 dll。

.EXAMPLE
    .\deploy.ps1
    编译 Release 并部署。

.EXAMPLE
    .\deploy.ps1 -Configuration Debug
    编译 Debug 并部署，附带 pdb。

.EXAMPLE
    .\deploy.ps1 -NoBuild
    跳过编译，直接复制已有的 bin 产物。

.EXAMPLE
    .\deploy.ps1 -DestinationRoot 'D:\Somewhere\Mods'
    部署到自定义的 Mods 根目录（仍会自动追加项目名子目录）。
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$DestinationRoot,

    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# 定位项目：脚本所在目录就是项目目录，项目名取同级的 .csproj 文件名
# ---------------------------------------------------------------------------
$projectDir = $PSScriptRoot
if (-not $projectDir) { $projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path }

$projectFile = Get-ChildItem -LiteralPath $projectDir -Filter *.csproj -File |
    Select-Object -First 1
if (-not $projectFile) {
    throw "未在 '$projectDir' 找到 .csproj 文件，请把脚本放在项目根目录。"
}

$projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectFile.Name)

# ---------------------------------------------------------------------------
# 目标目录：默认 <项目目录>\..\..\Mods\<项目名>
# ---------------------------------------------------------------------------
if (-not $DestinationRoot) {
    $DestinationRoot = Join-Path $projectDir '..\..\Mods'
}
$destination = [System.IO.Path]::GetFullPath((Join-Path $DestinationRoot $projectName))

Write-Host "项目   : $projectName"
Write-Host "配置   : $Configuration"
Write-Host "目标   : $destination"

# ---------------------------------------------------------------------------
# 编译
# ---------------------------------------------------------------------------
if ($NoBuild) {
    Write-Host '跳过编译 (-NoBuild)。'
}
else {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw '未找到 dotnet 命令，请确认已安装 .NET SDK 并在 PATH 中。'
    }

    Write-Host '正在编译...'
    & dotnet build $projectFile.FullName -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "编译失败（dotnet build 退出码 $LASTEXITCODE）。"
    }
}

# ---------------------------------------------------------------------------
# 查找 dll：不写死 netstandard2.1，按配置目录递归查找，兼容多目标框架
# ---------------------------------------------------------------------------
$binDir = Join-Path $projectDir "bin\$Configuration"
$dll = Get-ChildItem -LiteralPath $binDir -Filter "$projectName.dll" -Recurse -File `
    -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $dll) {
    throw "未找到编译产物：$binDir\$projectName.dll，请先编译或去掉 -NoBuild。"
}

# ---------------------------------------------------------------------------
# 部署
# ---------------------------------------------------------------------------
New-Item -ItemType Directory -Path $destination -Force | Out-Null

try {
    Copy-Item -LiteralPath $dll.FullName -Destination (Join-Path $destination $dll.Name) -Force
}
catch [System.IO.IOException] {
    # 游戏运行时会把已加载的 dll 映射进内存，此时无法覆盖。
    throw "无法覆盖 $($dll.Name)：目标文件被占用，请先退出游戏（Probably Stolen.exe）后重试。`n$($_.Exception.Message)"
}
Write-Host "  + $($dll.Name)"

if ($Configuration -eq 'Debug') {
    $pdb = Join-Path $dll.DirectoryName "$projectName.pdb"
    if (Test-Path -LiteralPath $pdb) {
        Copy-Item -LiteralPath $pdb -Destination (Join-Path $destination "$projectName.pdb") -Force
        Write-Host "  + $projectName.pdb"
    }
}

$manifest = Join-Path $projectDir 'manifest.xml'
if (-not (Test-Path -LiteralPath $manifest)) {
    throw "未找到 manifest.xml：$manifest"
}
Copy-Item -LiteralPath $manifest -Destination (Join-Path $destination 'manifest.xml') -Force
Write-Host '  + manifest.xml'

$localization = Join-Path $projectDir 'Localization'
if (Test-Path -LiteralPath $localization) {
    $destinationLocalization = Join-Path $destination 'Localization'
    New-Item -ItemType Directory -Path $destinationLocalization -Force | Out-Null
    Copy-Item -Path (Join-Path $localization '*') -Destination $destinationLocalization -Recurse -Force
    Write-Host '  + Localization\'
}

Write-Host '部署完成。'
