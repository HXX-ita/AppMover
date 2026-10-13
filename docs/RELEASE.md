# 发布流程

> 目标：任何人拿到仓库都能复现构建，维护者打一个标签就能产出 Release。
> 全程不需要安装 .NET SDK，不需要自建服务器。

---

## 一、首次发布前的准备（维护者做一次）

### 1. 在 GitHub 建一个空仓库

不要勾选 "Add a README" / "Add .gitignore" / "Choose a license" —— 这些文件仓库里已经有了，
勾选会造成首次推送冲突。

### 2. 关联远程并推送

```powershell
cd <项目目录>
git remote add origin https://github.com/<你的用户名>/AppMover.git
git branch -M main
git push -u origin main
```

推送后 GitHub 会自动执行 `.github/workflows/build.yml`：构建 → 跑内核回归 →
跑界面冒烟 → 上传产物。**第一次跑完请去 Actions 页面确认是绿的。**

### 3. 建两个标签（用于分类反馈）

仓库 → Issues → Labels，新建：

| 标签 | 用途 |
|---|---|
| `feedback` | 所有反馈入口自动带上 |
| `rule-gap` | 判定规则缺失或误判 |

程序生成的提交链接里已经写死了 `labels=feedback`，所以这个标签名要保持一致。

### 4. 反馈仓库地址不需要手工配置

CI 在打包时会执行：

```powershell
'{"repo":"<owner>/<repo>"}' → build/feedback.json
```

这个文件会随 Release 一起发布，放在 `AppMover.exe` 旁边。
程序启动后读取它来拼反馈链接，**因此源码里不需要硬编码任何人的用户名**。

本地自己构建的程序没有这个文件，会退回源码里的占位符 `REPLACE-ME/AppMover`，
并在生成链接时明确提示"反馈仓库尚未配置"。这是有意为之：宁可提示，不生成无效链接。

---

## 二、发一个版本

```powershell
git tag v0.1.0
git push origin v0.1.0
```

标签推送会触发 Release 任务：

1. 从标签名解析版本号（`v0.1.0` → `0.1.0`），注入程序集版本
2. 构建两个 exe
3. **跑内核回归测试，不通过就直接失败**（不会产出坏版本）
4. 跑界面冒烟测试（参考项，见下方说明）
5. 写入 `feedback.json`
6. 打包 `AppMover-<版本>-win-x64.zip`
7. 用 `gh release create` 创建 Release 并附上 zip

### 关于界面冒烟测试为什么是"参考项"

它需要真正 `Show()` 一个窗口来测量控件几何，而 CI 运行器的桌面会话不一定稳定。
在本地它经过**变异测试**验证有效（把停靠顺序改错，它会立刻报
`结果区域顶部 0` 并失败），所以它是有价值的检查；
但让它去卡住整条发布流水线，会把环境问题误判成代码问题。
因此它标了 `continue-on-error: true`，失败不阻断，但**日志里看得到**。

---

## 三、本地复现 CI 的全部动作

```powershell
# 1. 防回归：所有 .ps1 必须带 UTF-8 BOM
#    （PowerShell 5.1 用系统 ANSI 代码页解码无 BOM 文件，中文乱码会破坏脚本解析）
Get-ChildItem -Recurse -Filter *.ps1 | ForEach-Object {
  $b = [System.IO.File]::ReadAllBytes($_.FullName)
  if ($b[0] -ne 0xEF -or $b[1] -ne 0xBB -or $b[2] -ne 0xBF) { Write-Host "缺 BOM: $_" }
}

# 2. 从零构建并测试
Remove-Item build -Recurse -Force -ErrorAction SilentlyContinue
powershell -ExecutionPolicy Bypass -File src\build.ps1 -Version 0.1.0
build\AppMover-cli.exe selftest                 # 必须全绿（86 项）
powershell -ExecutionPolicy Bypass -File src\build.ps1 -SmokeUi   # 界面冒烟（37 项）

# 3. 校验 YAML（一个语法错误的 workflow 不会报错，它只是永远不运行）
#    需要 PyYAML，可用 pip install pyyaml --target <临时目录>
python _local\validate_yaml.py .
```

---

## 四、发布前检查清单

- [ ] `git status` 干净，没有未提交的改动
- [ ] 从未构建过的干净目录能构建成功（删掉 `build/` 重来）
- [ ] `selftest` 全绿
- [ ] `-SmokeUi` 全绿
- [ ] YAML 校验通过
- [ ] **确认没有把本机数据提交上去**：`git ls-files | Select-String '^(data|build|_local)/'` 应当没有输出
- [ ] `README.md` 与 `README.en.md` 的状态描述与实际一致
- [ ] 打标签的版本号与 `README` 里写的一致

---

## 五、绝对不要提交的东西

| 路径 | 为什么 |
|---|---|
| `data/` | 含本机真实扫描结果（目录名、路径、大小）——这是用户隐私 |
| `build/` | 构建产物，由 CI 生成 |
| `_local/` | 本机专用脚本（例如把 DSH 家目录从 C 盘搬到 D 盘的工具） |
| `journal.jsonl` | 搬移记录，含真实路径 |
| `feedback/` | 反馈报告，含真实路径 |

这些都已写进 `.gitignore`。CI 里可以加一道检查：

```powershell
$leak = git ls-files | Select-String -Pattern '^(data|build|_local)/'
if ($leak) { throw "有本机数据被提交: $leak" }
```
