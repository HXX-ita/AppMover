<#
  把 AppMover 发布到 GitHub —— 给不熟悉命令行的使用者用。

  它会做四件事：
    1. 检查本地仓库状态（必须是干净的、有提交的）
    2. 帮你打开 GitHub 的「新建仓库」页面，让你建一个**空仓库**
    3. 问你的 GitHub 用户名，配好远程地址，推送 main 分支
       （推送时 Git Credential Manager 会自动弹浏览器让你登录）
    4. 打 v0.1.1 标签并推送 —— 这一推会触发 CI 自动构建并产出 Release

  用法：双击同目录的「发布到GitHub.cmd」，或
        powershell -ExecutionPolicy Bypass -File tools\publish-github.ps1
#>
[CmdletBinding()]
param(
    [string]$UserName,
    [string]$RepoName = 'AppMover',
    [string]$Tag = 'v0.1.1',
    [switch]$SkipOpen,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Say($m)  { Write-Host $m }
function Ok($m)   { Write-Host "  [OK]   $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  [注意] $m" -ForegroundColor Yellow }
function Die($m)  { Write-Host ""; Write-Host "  [中止] $m" -ForegroundColor Red; Write-Host ""; exit 1 }

$repoRoot = Split-Path $PSScriptRoot -Parent
$git = 'D:\git\Git\cmd\git.exe'
if (-not (Test-Path $git)) {
    $cmd = Get-Command git -ErrorAction SilentlyContinue
    if ($cmd) { $git = $cmd.Source } else { Die "找不到 git。请确认已安装 Git for Windows。" }
}

Say ""
Say "=============================================="
Say "   把 AppMover 发布到 GitHub"
Say "=============================================="
Say ""

# ---------- 1. 检查本地仓库 ----------
Say "[1/5] 检查本地仓库"
if (-not (Test-Path (Join-Path $repoRoot '.git'))) { Die "这个目录不是 git 仓库：$repoRoot" }
Push-Location $repoRoot
try {
    $branch = & $git rev-parse --abbrev-ref HEAD
    $count  = & $git rev-list --count HEAD
    if ([int]$count -lt 1) { Die "本地还没有任何提交，无法发布。" }
    Ok "当前分支 $branch，共 $count 次提交"

    $dirty = & $git status --short
    if ($dirty) {
        Warn "工作区有未提交的改动："
        $dirty | ForEach-Object { Say "         $_" }
        Die "请先提交（或撤销）这些改动再发布，避免发布出去的内容和本地不一致。"
    }
    Ok "工作区干净"

    # 隐私检查：绝不能把本机真实路径推上去
    $leak = & $git ls-files | Select-String -Pattern '^(data|build|_local)/|^journal\.jsonl$|^feedback/'
    if ($leak) {
        $leak | ForEach-Object { Warn "不该提交的文件: $_" }
        Die "版本库里混入了本机数据（含真实路径），请先移除再发布。"
    }
    Ok "版本库中没有本机数据"

    if ($DryRun) {
        Say ""
        Ok "干跑模式：本地检查全部通过，到此为止（不联网、不改远程、不推送）。"
        Say "     去掉 -DryRun 即为真实发布。"
        Say ""
        return
    }

    # ---------- 2. 建仓库 ----------
    Say ""
    Say "[2/5] 在 GitHub 上建一个**空仓库**"
    Say ""
    Say "  需要你手动建（这一步要登录你的账号，我代替不了）："
    Say "    1. 浏览器会打开 GitHub 新建仓库页面"
    Say "    2. 仓库名填：$RepoName"
    Say "    3. **不要**勾选 Add a README / Add .gitignore / Choose a license"
    Say "       （仓库里已经有了，勾了会造成推送冲突）"
    Say "    4. 点 Create repository"
    Say ""
    Say "  如果你已经有这个仓库了，直接进行下一步即可。"
    Say ""

    if (-not $SkipOpen) {
        $newUrl = "https://github.com/new?name=$RepoName"
        try { Start-Process $newUrl; Ok "已尝试打开新建仓库页面：$newUrl" }
        catch { Warn "无法自动打开浏览器，请手动访问：$newUrl" }
    }

    # ---------- 3. 用户名与远程 ----------
    Say ""
    Say "[3/5] 配置远程仓库"
    if (-not $UserName) {
        Say "  请输入你的 GitHub 用户名（就是 github.com/ 后面那一段）"
        Say "  直接回车 = 取消发布"
        $UserName = (Read-Host "  用户名").Trim()
        if (-not $UserName) { Die "已取消。" }
    }

    $remoteUrl = "https://github.com/$UserName/$RepoName.git"
    $existing = & $git remote
    if ($existing -contains 'origin') {
        & $git remote set-url origin $remoteUrl
        Ok "已更新远程地址为 $remoteUrl"
    } else {
        & $git remote add origin $remoteUrl
        Ok "已设置远程地址为 $remoteUrl"
    }

    # 远程仓库必须为空，否则推送会冲突
    Say "  检查远程仓库是否为空 ..."
    $heads = & $git ls-remote --heads origin 2>&1
    if ($LASTEXITCODE -ne 0) {
        Warn "无法访问远程仓库：$heads"
        Die "请确认：用户名/仓库名是否写对、仓库是否已创建、以及是否有访问权限。"
    }
    if ($heads) {
        Warn "远程仓库里已经有内容了："
        $heads | ForEach-Object { Say "         $_" }
        Say ""
        Say "  这说明建仓库时勾选了 README / .gitignore / license。"
        Say "  两个办法："
        Say "    A. 删掉那个仓库，重新建一个**完全空白**的"
        Say "    B. 在命令行里执行下面两条，把远程内容合并进来后再跑本脚本："
        Say "         git pull --rebase origin main"
        Die "远程仓库非空，为避免覆盖或冲突，已停止。"
    }
    Ok "远程仓库是空的，可以推送"

    # ---------- 4. 推送 ----------
    Say ""
    Say "[4/5] 推送代码"
    Say "  推送时 Git Credential Manager 会自动弹出浏览器让你登录 GitHub，"
    Say "  在浏览器里点授权即可（不需要在这里输密码或 Token）。"
    Say ""
    & $git push -u origin $branch
    if ($LASTEXITCODE -ne 0) {
        Die "推送失败。常见原因：网络不通、没登录 GitHub、仓库名写错、或没有权限。"
    }
    Ok "代码已推送"

    # ---------- 5. 标签 ----------
    Say ""
    Say "[5/5] 打标签并推送（触发自动发布）"
    $hasTag = & $git tag -l $Tag
    if ($hasTag) {
        Warn "标签 $Tag 已存在，跳过创建"
    } else {
        & $git tag $Tag
        Ok "已创建标签 $Tag"
    }
    & $git push origin $Tag
    if ($LASTEXITCODE -ne 0) { Die "标签推送失败。" }
    Ok "标签已推送"

    Say ""
    Say "=============================================="
    Write-Host "   发布完成！" -ForegroundColor Cyan
    Say "=============================================="
    Say ""
    $repoUrl    = "https://github.com/$UserName/$RepoName"
    $actionsUrl = "$repoUrl/actions"
    $releaseUrl = "$repoUrl/releases"
    Say "  仓库地址  : $repoUrl"
    Say "  构建状态  : $actionsUrl"
    Say "  发布版本  : $releaseUrl"
    Say ""
    Say "  接下来 CI 会自动跑（大约两三分钟）："
    Say "    构建 → 内核回归测试 → 界面冒烟测试 → 打包 → 创建 Release"
    Say "  去「构建状态」那个页面看是否变绿；等它跑完，"
    Say "  「发布版本」页面就会出现可以下载的 zip。"
    Say ""
    Say "  如果 Actions 页面是红的，把报错信息发给我，我来查。"
    Say ""
    if (-not $SkipOpen) {
        try { Start-Process $actionsUrl; Ok "已打开构建状态页面" } catch { }
    }
}
finally {
    Pop-Location
}
