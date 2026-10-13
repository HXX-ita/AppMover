<#
  侦察脚本：盘点本机各软件/数据目录的占用与归属。

  这是产品的「扫描器」原型：产品里那套严谨判定规则，要先在真实数据上跑出来。
  为了精确，这里用逐目录容错的 .NET 遍历，而不是 Get-ChildItem -Recurse
  （后者遇到一个无权限目录就整棵树中断，而且拿不到「重解析点」这类关键信息）。

  输出: data\inventory-<时间戳>.json
#>
[CmdletBinding()]
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\data'),
    [string]$User = $env:USERNAME
)

$ErrorActionPreference = 'Continue'

# ---------- 逐目录容错的树统计 ----------
function Get-TreeStats {
    param([string]$Path)

    $files = 0; $dirs = 0; $bytes = 0; $denied = 0; $reparse = 0
    $stack = [System.Collections.Generic.Stack[string]]::new()
    $stack.Push($Path)

    while ($stack.Count -gt 0) {
        $cur = $stack.Pop()
        $entries = $null
        try   { $entries = [System.IO.Directory]::EnumerateFileSystemEntries($cur) }
        catch { $denied++; continue }

        try {
            foreach ($e in $entries) {
                try {
                    $attr = [System.IO.File]::GetAttributes($e)
                    if ($attr -band [System.IO.FileAttributes]::ReparsePoint) { $reparse++; continue }
                    if ($attr -band [System.IO.FileAttributes]::Directory) { $dirs++; $stack.Push($e) }
                    else { $files++; $bytes += ([System.IO.FileInfo]::new($e)).Length }
                } catch { $denied++ }
            }
        } catch { $denied++ }
    }

    [pscustomobject]@{
        Files      = $files
        Dirs       = $dirs
        Bytes      = $bytes
        Denied     = $denied
        Reparse    = $reparse
    }
}

function Get-ChildReport {
    param([string]$Root, [string]$Kind, [int]$MaxChildren = 400)

    if (-not (Test-Path -LiteralPath $Root)) { return @() }
    $out = New-Object System.Collections.Generic.List[object]

    $children = $null
    try { $children = [System.IO.Directory]::EnumerateDirectories($Root) } catch { return @() }

    $n = 0
    foreach ($c in $children) {
        if ($n -ge $MaxChildren) { break }
        $n++
        $isLink = $false
        try { $isLink = (([System.IO.File]::GetAttributes($c)) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 } catch {}

        if ($isLink) {
            $out.Add([pscustomobject]@{
                Kind = $Kind; Root = $Root; Name = [System.IO.Path]::GetFileName($c); Path = $c
                IsReparsePoint = $true; Files = 0; Dirs = 0; Bytes = 0; Denied = 0; Reparse = 0
                LastWrite = $null
            })
            continue
        }

        $st = Get-TreeStats -Path $c
        $lw = $null
        try { $lw = ([System.IO.DirectoryInfo]::new($c)).LastWriteTimeUtc.ToString('o') } catch {}
        $out.Add([pscustomobject]@{
            Kind = $Kind; Root = $Root; Name = [System.IO.Path]::GetFileName($c); Path = $c
            IsReparsePoint = $false
            Files = $st.Files; Dirs = $st.Dirs; Bytes = $st.Bytes; Denied = $st.Denied; Reparse = $st.Reparse
            LastWrite = $lw
        })
    }
    $out
}

# ---------- 卷清单（多盘机器必须处理）----------
$volumes = @()
foreach ($v in (Get-Volume -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter })) {
    $volumes += [pscustomobject]@{
        Letter = "$($v.DriveLetter):"
        Label = $v.FileSystemLabel
        FileSystem = $v.FileSystem
        DriveType = "$($v.DriveType)"
        SizeGB = [math]::Round($v.Size / 1GB, 2)
        FreeGB = [math]::Round($v.SizeRemaining / 1GB, 2)
    }
}

# ---------- 已安装软件登记表（判断「文件夹是否对应一个真实已安装软件」的依据）----------
$uninstall = @()
foreach ($k in @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
)) {
    $uninstall += Get-ItemProperty -Path $k -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName } |
        Select-Object DisplayName, DisplayVersion, Publisher, InstallLocation,
                      @{n='EstSizeKB';e={$_.EstimatedSize}}, InstallDate, UninstallString, PSPath
}

# ---------- 扫描目标 ----------
$userProfile = "C:\Users\$User"
$roots = @(
    @{ Path = 'C:\Program Files';                   Kind = 'ProgramFiles' }
    @{ Path = 'C:\Program Files (x86)';             Kind = 'ProgramFilesX86' }
    @{ Path = 'C:\ProgramData';                     Kind = 'ProgramData' }
    @{ Path = "$userProfile\AppData\Local";         Kind = 'AppDataLocal' }
    @{ Path = "$userProfile\AppData\Roaming";       Kind = 'AppDataRoaming' }
    @{ Path = "$userProfile\AppData\LocalLow";      Kind = 'AppDataLocalLow' }
    @{ Path = $userProfile;                         Kind = 'UserProfile' }
)
# C 盘根目录下的自定义文件夹（跳过 Windows 自身和系统文件）
$roots += @{ Path = 'C:\'; Kind = 'DriveRoot' }

$report = @()
foreach ($r in $roots) {
    Write-Host "扫描 $($r.Path) ..."
    $items = Get-ChildReport -Root $r.Path -Kind $r.Kind
    if ($r.Kind -eq 'UserProfile') {
        # 用户目录里只保留真正的文件夹，排除 AppData 与系统隐藏项
        $items = $items | Where-Object { $_.Name -notin @('AppData','.dsh') }
    }
    if ($r.Kind -eq 'DriveRoot') {
        $skip = @('Windows','Program Files','Program Files (x86)','ProgramData','Users','$Recycle.Bin',
                  'System Volume Information','Recovery','PerfLogs','$WinREAgent','MSOCache',
                  'Documents and Settings','Config.Msi','$SysReset','Intel','AMD','NVIDIA')
        $items = $items | Where-Object { $_.Name -notin $skip -and $_.Name -notlike '*.tmp' }
    }
    $report += $items
}

$doc = [pscustomobject]@{
    ScannedAt   = (Get-Date).ToString('o')
    Computer    = $env:COMPUTERNAME
    Windows     = (Get-CimInstance Win32_OperatingSystem).Caption
    Volumes     = $volumes
    Uninstall   = $uninstall
    Folders     = ($report | Sort-Object Bytes -Descending)
}

if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null }
$file = Join-Path $OutputDir ("inventory-{0}.json" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
$doc | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $file -Encoding UTF8

Write-Host ""
Write-Host "完成: $file"
Write-Host ("文件夹条目: {0}   已登记软件: {1}   卷: {2}" -f $report.Count, $uninstall.Count, $volumes.Count)
$tot = ($report | Measure-Object Bytes -Sum).Sum
Write-Host ("合计占用: {0} GB" -f [math]::Round($tot / 1GB, 2))
