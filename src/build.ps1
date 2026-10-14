<#
  用 Windows 自带的 csc.exe 编译 AppMover。
  不需要安装 .NET SDK —— 这是本项目刻意选择的零安装分发路线。

  产物两个（共用同一份 Core）：
    AppMover.exe       图形界面（WinExe，双击即用）
    AppMover-cli.exe   命令行（控制台程序，含回归自测）

  为什么要分成两个可执行文件，而不是一个带 --gui 开关的程序：
  WinExe 子系统不会附加到父进程的控制台，命令行输出会全部丢失；
  要在一个二进制里兼顾两者，得手工 AttachConsole + 重开 stdout，既脆弱又难测。
  两个小而清晰的可执行文件共享同一个内核，是这个场景更稳妥的做法。

  用法:
    powershell -ExecutionPolicy Bypass -File src\build.ps1
    powershell -ExecutionPolicy Bypass -File src\build.ps1 -Run selftest
    powershell -ExecutionPolicy Bypass -File src\build.ps1 -Version 0.2.0
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Config = 'Release',
    [string]$Version = '0.1.3',
    [string]$Run,
    [switch]$SmokeUi
)

$ErrorActionPreference = 'Stop'

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "找不到系统自带的 C# 编译器 csc.exe" }

$srcDir   = Join-Path $PSScriptRoot 'AppMover'
$coreDir  = Join-Path $srcDir 'Core'
$cliDir   = Join-Path $srcDir 'Cli'
$guiDir   = Join-Path $srcDir 'Gui'
$manifest = Join-Path $srcDir 'app.manifest'
$outDir   = Join-Path $PSScriptRoot '..\build'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

function Get-Sources([string]$dir) {
    if (-not (Test-Path $dir)) { return @() }
    return @(Get-ChildItem $dir -Recurse -Filter *.cs | ForEach-Object { $_.FullName })
}

$coreSources = Get-Sources $coreDir
if ($coreSources.Count -eq 0) { throw "在 $coreDir 下没找到任何 .cs 源文件" }

# 程序集版本通过生成的 AssemblyInfo 提供：
# 老版 csc.exe 没有 /version: 开关（它是 C# 5 编译器），只能走程序集特性。
# 生成到 build 目录而不是源码目录，避免污染工作树。
$genDir = Join-Path $outDir 'gen'
if (-not (Test-Path $genDir)) { New-Item -ItemType Directory -Path $genDir -Force | Out-Null }
$asmInfo = Join-Path $genDir 'AssemblyInfo.g.cs'
$asmText = @"
using System.Reflection;
[assembly: AssemblyVersion("$($Version).0")]
[assembly: AssemblyFileVersion("$($Version).0")]
[assembly: AssemblyProduct("AppMover")]
[assembly: AssemblyTitle("AppMover")]
[assembly: AssemblyCompany("AppMover contributors")]
[assembly: AssemblyCopyright("MIT License")]
"@
[System.IO.File]::WriteAllText($asmInfo, $asmText, (New-Object System.Text.UTF8Encoding($true)))

function Build-Target {
    param(
        [string]$Name,
        [string]$TargetKind,          # exe | winexe
        [string[]]$Sources,
        [string[]]$ExtraRefs = @()
    )

    $exe = Join-Path $outDir $Name
    # 注意：变量名不能叫 $args —— 那是 PowerShell 的自动变量
    $cscArgs = @(
        '/nologo',
        ("/target:" + $TargetKind),
        # 源文件是 UTF-8，必须显式指定代码页，否则中文注释会按 GBK 解码出错
        '/codepage:65001',
        # 应用清单：声明 Windows 10 兼容性、长路径感知、DPI 感知
        ('/win32manifest:' + $manifest),
        '/warn:4',
        ("/out:" + $exe)
    )
    $cscArgs += $ExtraRefs
    if ($Config -eq 'Release') { $cscArgs += '/optimize+' } else { $cscArgs += '/debug+' }
    $cscArgs += $Sources

    Write-Host ("编译 {0} ({1}，{2} 个源文件) ..." -f $Name, $TargetKind, $Sources.Count)
    # 必须把编译器输出收进变量：直接 & $csc 的话，任何 stdout（例如一条警告）
    # 都会被 PowerShell 当成函数返回值混进来，调用方拿到的就不是单个路径而是一个数组，
    # 后续拼接 "$exe.config" 会得到非法路径（曾因此报 "path's format is not supported"）。
    $cscOutput = & $csc @cscArgs 2>&1
    $cscCode = $LASTEXITCODE
    if ($cscOutput) { $cscOutput | ForEach-Object { Write-Host "  $_" } }
    if ($cscCode -ne 0) { throw "编译 $Name 失败，退出码 $cscCode" }

    $fi = Get-Item $exe
    Write-Host ("  -> {0}  ({1:N1} KB)" -f $fi.Name, ($fi.Length / 1KB))
    return $exe
}

Write-Host "编译器 : $csc"
Write-Host "版本   : $Version"
Write-Host "输出   : $outDir"
Write-Host ""

# 随产物生成 app.config。
# 必须打开长路径支持：.NET Framework 默认的 legacy path handling 会拒绝 \\?\ 前缀，
# 而深层目录（node_modules、组件缓存等）很容易超过 260 字符。
$appConfig = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup useLegacyV2RuntimeActivationPolicy="true">
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" />
  </startup>
  <runtime>
    <AppContextSwitchOverrides value="Switch.System.IO.UseLegacyPathHandling=false;Switch.System.IO.BlockLongPaths=false" />
  </runtime>
</configuration>
'@

# 1) 命令行版
$cliSources = @($coreSources) + @(Get-Sources $cliDir) + @($asmInfo)
$cliExe = Build-Target -Name 'AppMover-cli.exe' -TargetKind 'exe' -Sources $cliSources
[System.IO.File]::WriteAllText("$cliExe.config", $appConfig, (New-Object System.Text.UTF8Encoding($false)))

# 2) 图形界面版
$guiSrc = Get-Sources $guiDir
if ($guiSrc.Count -gt 0) {
    $guiSources = @($coreSources) + @($guiSrc) + @($asmInfo)
    $guiExe = Build-Target -Name 'AppMover.exe' -TargetKind 'winexe' -Sources $guiSources `
        -ExtraRefs @('/r:System.Windows.Forms.dll', '/r:System.Drawing.dll')
    [System.IO.File]::WriteAllText("$guiExe.config", $appConfig, (New-Object System.Text.UTF8Encoding($false)))
} else {
    Write-Host "跳过图形界面版（Gui 目录下没有源文件）"
}

Write-Host ""
Write-Host "构建完成。"

if ($SmokeUi) {
    $smoke = Join-Path $outDir 'ui-smoke.txt'
    if (Test-Path $smoke) { Remove-Item $smoke -Force }
    Write-Host ""
    Write-Host "=== 界面冒烟测试 ==="
    Start-Process -FilePath $guiExe -ArgumentList '--smoke', "`"$smoke`"" -Wait | Out-Null
    if (-not (Test-Path $smoke)) { throw "界面冒烟测试没有产出报告，说明程序未能启动" }
    Get-Content $smoke -Encoding UTF8 | ForEach-Object { Write-Host $_ }
    $last = (Get-Content $smoke -Encoding UTF8 | Select-Object -Last 1)
    if ($last -notmatch '失败 0') { throw "界面冒烟测试未全部通过: $last" }
}

if ($Run) {
    Write-Host ""
    Write-Host "=== 运行: AppMover-cli.exe $Run ==="
    & $cliExe $Run
    exit $LASTEXITCODE
}
