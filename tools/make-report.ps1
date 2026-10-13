<#
  判定引擎雏形 + 可视化报告生成器。

  输入: data\inventory-*.json （由 scan-inventory.ps1 产出）
  输出: data\inventory-report.html （双击即可在浏览器打开查看）

  这一步的目的是：把 SPEC.md §4 的判定规则在真实数据上跑一遍，
  用结果检验规则是否站得住，再据此写 C# 正式实现。
#>
[CmdletBinding()]
param(
    [string]$DataDir = (Join-Path $PSScriptRoot '..\data')
)

$ErrorActionPreference = 'Continue'

$file = Get-ChildItem (Join-Path $DataDir 'inventory-*.json') | Sort-Object Name -Descending | Select-Object -First 1
if (-not $file) { Write-Host "找不到 inventory-*.json，请先运行 scan-inventory.ps1"; exit 1 }
Write-Host "读取 $($file.Name)"
$inv = Get-Content $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json

# ---------- 路径规范化 ----------
function Normalize-Path([string]$p) {
    if ([string]::IsNullOrWhiteSpace($p)) { return '' }
    $s = $p.Trim().TrimEnd('\')
    $s = $s -replace '/', '\'
    return $s.ToLowerInvariant()
}

# ---------- 硬黑名单（SPEC §4.1）----------
$forbiddenPatterns = @(
    @{ Re = '^c:\\windows(\\|$)';                       Why = 'Windows 操作系统本体' }
    @{ Re = '^c:\\windows\\winsxs';                     Why = '组件存储 WinSxS（含硬链接，动了会坏）' }
    @{ Re = '^c:\\program files\\windowsapps';          Why = '商店应用(MSIX)，ACL 受保护' }
    @{ Re = '^c:\\program files\\windows defender';     Why = 'Windows Defender 自身' }
    @{ Re = '^c:\\programdata\\microsoft\\windows defender'; Why = 'Windows Defender 数据' }
    @{ Re = '^c:\\program files\\(x86\\)\\windows defender'; Why = 'Windows Defender 自身' }
    @{ Re = '^c:\\program files\\windows nt';           Why = '系统组件' }
    @{ Re = '^c:\\program files\\common files\\microsoft shared'; Why = 'COM/Office 基础设施' }
    @{ Re = '^c:\\program files\\dotnet';               Why = '.NET 运行时，被其他软件依赖' }
    @{ Re = '^c:\\program files\\(x86\\)\\microsoft\.net'; Why = '.NET 运行时组件' }
    @{ Re = '^c:\\program files\\windowspowershell';    Why = '系统 PowerShell' }
    @{ Re = '^c:\\program files\\windows kits';         Why = 'Windows SDK 组件' }
    @{ Re = '^c:\\programdata\\microsoft\\windows';     Why = '系统外壳数据' }
    @{ Re = '^c:\\programdata\\microsoft\\edge';        Why = 'Edge 更新器目录，更新会重建' }
    @{ Re = '^c:\\programdata\\package cache';          Why = '安装器包缓存，被卸载程序引用' }
    @{ Re = '^c:\\users\\[^\\]+\\appdata\\local\\packages'; Why = 'UWP 应用数据，ACL 受保护' }
    @{ Re = '^c:\\users\\[^\\]+\\appdata\\local\\microsoft\\windows($|\\)'; Why = '系统外壳数据' }
    @{ Re = '^c:\\users\\[^\\]+\\appdata\\local\\temp'; Why = '系统临时目录' }
    @{ Re = '^c:\\users\\[^\\]+\\appdata\\local\\microsoft\\windowsapps'; Why = '商店应用别名' }
    @{ Re = '^c:\\\$recycle\.bin';                      Why = '回收站' }
    @{ Re = '^c:\\system volume information';           Why = '系统卷信息' }
    @{ Re = '^c:\\recovery';                            Why = '恢复分区数据' }
    @{ Re = '^c:\\perflogs';                            Why = '性能日志' }
    @{ Re = '^c:\\msocache';                            Why = 'Office 安装缓存' }
    @{ Re = '^c:\\config\.msi';                         Why = '安装事务目录' }
    @{ Re = '^c:\\\$winreagent';                        Why = 'Windows 恢复代理' }
    @{ Re = '^c:\\\$sysreset';                          Why = '系统重置数据' }
)

# ---------- 共享组件 / 厂商目录（SPEC §4.1 补充）----------
# 审计发现的误判来源：这类目录没有卸载登记项，朴素的残留判定会把它们当成垃圾。
# 它们不是垃圾，但同样不该搬 —— 被多个软件或驱动引用。
$sharedNeverMove = @(
    @{ Re = '\\common files$';            Why = '共享组件目录（Common Files），被多个软件引用' }
    @{ Re = '\\nvidia( corporation)?$';   Why = 'NVIDIA 驱动组件，搬动会影响驱动更新与卸载' }
    @{ Re = '\\amd$';                     Why = 'AMD 驱动组件' }
    @{ Re = '\\intel( corporation)?$';    Why = 'Intel 驱动组件' }
    @{ Re = '\\realtek';                  Why = 'Realtek 驱动组件' }
    @{ Re = '\\modifiablewindowsapps$';   Why = '系统可修改应用目录' }
    @{ Re = '\\reference assemblies$';    Why = '.NET 引用程序集，被编译工具依赖' }
    @{ Re = '\\uninstall information$';   Why = '安装/卸载信息' }
    @{ Re = '\\internet explorer$';       Why = '系统组件 IE' }
    @{ Re = '^c:\\users\\[^\\]+\\appdata\\(local|roaming|locallow)\\microsoft$'; Why = 'Microsoft 共享数据，含系统组件与多软件运行时' }
    @{ Re = '^c:\\programdata\\microsoft$'; Why = 'Microsoft 共享数据（系统外壳/组件）' }
    @{ Re = '\\windows mail$|\\windows media player$|\\windows multimedia platform$|\\windows portable devices$|\\windows photo viewer$|\\windows sidebar$|\\windows security$'; Why = 'Windows 自带组件' }
    # 审计补充：初版只覆盖了 64 位路径，下面这些 32 位变体与系统目录曾漏网被误判为残留
    @{ Re = '^c:\\program files( \(x86\))?\\windows ?(nt|powershell|defender|mail|sidebar|photo viewer|portable devices|multimedia platform)$'; Why = 'Windows 自带组件（32 位变体）' }
    @{ Re = '^c:\\program files( \(x86\))?\\microsoft$'; Why = 'Microsoft 共享组件（含 Edge / WebView2）' }
    @{ Re = '^c:\\program files( \(x86\))?\\msbuild$'; Why = 'MSBuild 构建组件' }
    @{ Re = '^c:\\program files( \(x86\))?\\microsoft\.net$'; Why = '.NET Framework 组件' }
    @{ Re = '\\installshield installation information$'; Why = 'InstallShield 卸载信息，卸载程序依赖它，删除会导致无法卸载' }
    @{ Re = '^c:\\programdata\\(usoshared|usoprivate|microsoft|huorong|windows security health)$'; Why = '系统/安全软件数据目录' }
)

# ---------- 已安装软件索引 ----------
$uninstall = @($inv.Uninstall)
$instLoc = New-Object System.Collections.Generic.List[string]
$instName = New-Object System.Collections.Generic.List[string]
foreach ($u in $uninstall) {
    if ($u.InstallLocation) { $instLoc.Add((Normalize-Path $u.InstallLocation)) }
    if ($u.DisplayName)     { $instName.Add($u.DisplayName.ToLowerInvariant()) }
}

# ---------- 服务索引（判断文件夹里是否住着服务）----------
$svcPaths = @()
try {
    $svcRoot = 'HKLM:\SYSTEM\CurrentControlSet\Services'
    foreach ($s in (Get-ChildItem $svcRoot -ErrorAction SilentlyContinue)) {
        $p = Get-ItemProperty $s.PSPath -ErrorAction SilentlyContinue
        if ($p.ImagePath) {
            $ip = $p.ImagePath -replace '^\\?"', '' -replace '\\?"$', ''
            $ip = ($ip -split '\s+-')[0]
            $svcPaths += (Normalize-Path $ip)
        }
    }
} catch {}

# ---------- 逐条判定 ----------
$results = New-Object System.Collections.Generic.List[object]

foreach ($f in $inv.Folders) {
    $norm = Normalize-Path $f.Path
    $name = $f.Name
    $nameLower = $name.ToLowerInvariant()
    $cat = 'safe'; $why = @(); $conf = 'medium'

    # 1) 硬黑名单 + 共享组件/厂商目录
    $hit = $null
    foreach ($p in $forbiddenPatterns) { if ($norm -match $p.Re) { $hit = $p; break } }
    if (-not $hit) { foreach ($p in $sharedNeverMove) { if ($norm -match $p.Re) { $hit = $p; break } } }
    if ($hit) {
        $cat = 'forbidden'; $why += $hit.Why; $conf = 'high'
    }

    # 2) 已是重解析点
    if (-not $hit -and $f.IsReparsePoint) {
        $cat = 'forbidden'; $why += '该目录本身已经是联接/符号链接，禁止二次搬运'; $conf = 'high'
    }

    # 3) 体内含重解析点（部分文件是链接）
    if (-not $hit -and $f.Reparse -gt 0 -and $cat -ne 'forbidden') {
        $why += ('目录内含 ' + $f.Reparse + ' 个链接项，复制时会打断')
        if ($f.Reparse -gt 20) { $cat = 'caution'; $conf = 'medium' }
    }

    if ($cat -ne 'forbidden') {
        # 4) 是否对应一个已登记软件
        $byLoc = $false
        foreach ($l in $instLoc) {
            if ($l -and ($l -eq $norm -or $l.StartsWith($norm + '\') -or $norm.StartsWith($l + '\'))) { $byLoc = $true; break }
        }
        $byName = $false
        foreach ($n in $instName) {
            if ($n -eq $nameLower -or $n.StartsWith($nameLower) -or $nameLower.StartsWith($n)) { $byName = $true; break }
        }

        # 5) 是否住着服务
        $hasSvc = $false
        foreach ($sp in $svcPaths) { if ($sp -and $sp.StartsWith($norm + '\')) { $hasSvc = $true; break } }

        if ($hasSvc) { $cat = 'caution'; $why += '该目录内含 Windows 服务程序，搬移前需停服务' }

        # 缓存类目录名：删掉软件会自己重建，属于真正「可清理」的
        $cachePattern = '^(pip|npm|npm-cache|yarn|pnpm|nuget|\.gradle|\.m2|\.cargo|go-build|temp|tmp|cache|caches|code cache|gpucache|shadercache|d3dscache|grshadercache|cachestorage|logs?|dxcache|glcache)$'
        $orphanPattern = '\((\d+)\)$|_old$|\.old$|\.bak$|^backup|^temp$|新建文件夹'

        if ($f.Kind -eq 'ProgramFiles' -or $f.Kind -eq 'ProgramFilesX86') {
            if ($byLoc -or $byName) {
                if ($cat -ne 'caution') { $cat = 'caution' }
                $why += 'Program Files 下已安装软件（需管理员；自动更新器可能破坏联接）'
            } elseif ($f.Files -eq 0) {
                $cat = 'junk'; $conf = 'high'; $why += '空目录（不含任何文件）'
            } elseif ($nameLower -match $orphanPattern) {
                $cat = 'junk'; $conf = 'medium'; $why += '无卸载登记且命名符合卸载残留特征'
            } else {
                # 关键修正：没有卸载登记 ≠ 是垃圾。共享组件、绿色软件都可能没有登记项。
                $cat = 'caution'; $conf = 'low'
                $why += 'Program Files 下但注册表无对应卸载项：可能是共享组件或绿色软件，也可能是卸载残留，需人工判断'
            }
        }
        elseif ($f.Kind -eq 'AppDataLocal' -or $f.Kind -eq 'AppDataRoaming' -or $f.Kind -eq 'AppDataLocalLow') {
            # 审计结论：AppData 目录绝大多数不登记到卸载表，
            # 所以「无卸载项」绝不能当作残留证据（初版曾因此把剪映、微信、游戏存档误判成垃圾）。
            if ($nameLower -match $cachePattern) {
                $cat = 'junk'; $conf = 'medium'
                $why += '缓存类目录，软件会自动重建：可安全清理，也可搬移到其他盘'
            } elseif ($byLoc -or $byName) {
                if ($cat -eq 'safe') { $why += '已登记软件的应用数据目录（搬移前请先退出该软件）' }
            } else {
                if ($cat -eq 'safe') { $why += '应用数据目录（多数软件的数据目录不登记到卸载表，无登记不代表是残留）' }
            }
        }
        elseif ($f.Kind -eq 'ProgramData') {
            if ($nameLower -match 'package cache|installer cache' -or $nameLower -match $cachePattern) {
                $cat = 'junk'; $conf = 'medium'; $why += '安装器包缓存，卸载程序可能引用，清理前建议确认'
            } elseif ($byLoc -or $byName) {
                if ($cat -eq 'safe') { $why += '已登记软件的数据目录' }
            } else {
                $cat = 'caution'; $conf = 'low'
                $why += 'ProgramData 下无卸载登记：可能是驱动/共享组件或服务数据，搬移前需确认'
            }
        }
        elseif ($f.Kind -eq 'UserProfile') {
            $why += '用户文档类目录，建议优先使用 Windows 自带「属性→位置」迁移功能'
            $cat = 'caution'
        }
    }

    if ($why.Count -eq 0) { $why += '位于可搬位置，未发现禁止特征' }

    $results.Add([pscustomobject]@{
        Category = $cat
        Confidence = $conf
        Name = $name
        Path = $f.Path
        Kind = $f.Kind
        Bytes = $f.Bytes
        GB = [math]::Round($f.Bytes / 1GB, 3)
        Files = $f.Files
        Denied = $f.Denied
        Reparse = $f.Reparse
        LastWrite = $f.LastWrite
        Why = ($why -join '；')
    })
}

$results = $results | Sort-Object Bytes -Descending

# ---------- 统计 ----------
$catName = @{ safe='可安全搬移'; caution='可搬但需小心'; forbidden='禁止搬移'; junk='疑似残留可清理' }
$catColor = @{ safe='#12805c'; caution='#b26a00'; forbidden='#b3261e'; junk='#6b4bb8' }

Write-Host ""
Write-Host "=== 判定结果 ==="
foreach ($g in ($results | Group-Object Category | Sort-Object { -($_.Group | Measure-Object Bytes -Sum).Sum })) {
    $s = ($g.Group | Measure-Object Bytes -Sum).Sum
    Write-Host ("  {0,-14} {1,4} 项   {2,8} GB" -f $catName[$g.Name], $g.Count, [math]::Round($s/1GB,2))
}

# ---------- 生成 HTML ----------
function HtmlEnc([string]$s) {
    if ($null -eq $s) { return '' }
    return $s.Replace('&','&amp;').Replace('<','&lt;').Replace('>','&gt;').Replace('"','&quot;')
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8">')
[void]$sb.AppendLine('<title>AppMover 扫描报告</title>')
[void]$sb.AppendLine(@'
<style>
 body{font-family:"Microsoft YaHei",system-ui,sans-serif;margin:0;background:#f5f6f8;color:#1a1a1a}
 header{background:#1f2937;color:#fff;padding:18px 26px}
 header h1{margin:0;font-size:19px}
 header p{margin:6px 0 0;font-size:13px;opacity:.8}
 .stat{display:flex;gap:14px;padding:16px 26px;flex-wrap:wrap}
 .card{background:#fff;border-radius:10px;padding:12px 18px;box-shadow:0 1px 3px rgba(0,0,0,.09);min-width:140px}
 .card b{display:block;font-size:22px;margin-top:4px}
 .controls{padding:0 26px 12px;display:flex;gap:10px;flex-wrap:wrap;align-items:center}
 input,select{padding:7px 11px;border:1px solid #cbd5e1;border-radius:7px;font-size:13px}
 table{width:100%;border-collapse:collapse;background:#fff;font-size:13px}
 th,td{padding:9px 12px;text-align:left;border-bottom:1px solid #eef0f3;vertical-align:top}
 th{background:#eef1f5;position:sticky;top:0;font-weight:600}
 tr:hover td{background:#fafbfc}
 .badge{display:inline-block;padding:2px 9px;border-radius:11px;color:#fff;font-size:11px;white-space:nowrap}
 .num{text-align:right;font-variant-numeric:tabular-nums;white-space:nowrap}
 .path{font-family:Consolas,monospace;font-size:11.5px;color:#4b5563;word-break:break-all}
 .why{font-size:12px;color:#4b5563}
 .low{opacity:.55;font-size:11px}
</style></head><body>
'@)
[void]$sb.AppendLine('<header><h1>软件搬家 · AppMover — C 盘扫描报告</h1>')
[void]$sb.AppendLine(('<p>计算机 {0} ｜ {1} ｜ 扫描于 {2}</p></header>' -f (HtmlEnc $inv.Computer), (HtmlEnc $inv.Windows), (HtmlEnc $inv.ScannedAt)))
[void]$sb.AppendLine('<div class="stat">')
foreach ($g in ($results | Group-Object Category | Sort-Object { -($_.Group | Measure-Object Bytes -Sum).Sum })) {
    $s = ($g.Group | Measure-Object Bytes -Sum).Sum
    [void]$sb.AppendLine(('<div class="card"><span style="font-size:12px;color:#6b7280">{0}</span><b style="color:{1}">{2} GB</b><span style="font-size:12px;color:#6b7280">{3} 项</span></div>' -f $catName[$g.Name], $catColor[$g.Name], [math]::Round($s/1GB,2), $g.Count))
}
[void]$sb.AppendLine('</div>')
[void]$sb.AppendLine('<div class="controls">')
[void]$sb.AppendLine('<input id="q" placeholder="搜索名称或路径..." style="width:260px">')
[void]$sb.AppendLine('<select id="c"><option value="">全部分类</option><option value="safe">可安全搬移</option><option value="caution">可搬但需小心</option><option value="forbidden">禁止搬移</option><option value="junk">疑似残留可清理</option></select>')
[void]$sb.AppendLine('<span id="cnt" style="font-size:13px;color:#6b7280"></span>')
[void]$sb.AppendLine('</div>')
[void]$sb.AppendLine('<table id="t"><thead><tr><th>分类</th><th>名称</th><th>路径</th><th class="num">大小(GB)</th><th class="num">文件数</th><th>判定依据</th></tr></thead><tbody>')

foreach ($r in $results) {
    [void]$sb.AppendLine(('<tr data-c="{0}" data-s="{1}"><td><span class="badge" style="background:{2}">{3}</span>{4}</td><td>{5}</td><td class="path">{6}</td><td class="num">{7}</td><td class="num">{8}</td><td class="why">{9}</td></tr>' -f `
        $r.Category, (HtmlEnc ($r.Name + ' ' + $r.Path)).ToLower(), $catColor[$r.Category], $catName[$r.Category], `
        $(if ($r.Confidence -eq 'low') { '<div class="low">低置信</div>' } else { '' }), `
        (HtmlEnc $r.Name), (HtmlEnc $r.Path), $r.GB, $r.Files, (HtmlEnc $r.Why)))
}
[void]$sb.AppendLine('</tbody></table>')
[void]$sb.AppendLine(@'
<script>
var q=document.getElementById('q'),c=document.getElementById('c'),rows=[].slice.call(document.querySelectorAll('#t tbody tr')),cnt=document.getElementById('cnt');
function upd(){var s=q.value.toLowerCase(),v=c.value,n=0,b=0;
 rows.forEach(function(r){var ok=(!v||r.dataset.c===v)&&(!s||r.dataset.s.indexOf(s)>=0);
  r.style.display=ok?'':'none'; if(ok){n++; b+=parseFloat(r.children[3].textContent)||0;}});
 cnt.textContent='显示 '+n+' 项，合计 '+b.toFixed(2)+' GB';}
q.oninput=upd;c.onchange=upd;upd();
</script></body></html>
'@)

$out = Join-Path $DataDir 'inventory-report.html'
[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($true)))

# 同时输出机器可读的判定结果，便于回归审计（每次改规则都要重跑并 diff）
$jsonOut = Join-Path $DataDir 'classification-latest.json'
@($results) | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $jsonOut -Encoding UTF8

Write-Host ""
Write-Host "报告已生成: $out"
Write-Host "判定数据:   $jsonOut"
