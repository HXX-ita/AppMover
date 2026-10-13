# 贡献指南

## 这个项目最看重什么

**安全性 > 功能数量。** 这个工具会移动用户已安装软件的文件位置，一次误判或一个回滚漏洞
就可能让人丢数据。所以评审代码时，排序是这样的：

1. 会不会损坏或丢失用户数据
2. 判定规则是否有据可查、会不会误判
3. 失败时能不能回到操作前的状态
4. 功能有没有用、界面好不好看

如果一个改动让某类目录"能被搬了"，但说不出它为什么安全，那它不会被合并。

---

## 环境

不需要安装任何东西：

- Windows 10 / 11
- .NET Framework 4.8（系统自带）
- `csc.exe`（随 .NET Framework 提供，构建脚本会自动找到）
- PowerShell 5.1+

## 构建与测试

```powershell
powershell -ExecutionPolicy Bypass -File src\build.ps1                 # 构建
powershell -ExecutionPolicy Bypass -File src\build.ps1 -Run selftest  # 内核回归（必须全绿）
powershell -ExecutionPolicy Bypass -File src\build.ps1 -SmokeUi       # 界面冒烟（必须全绿）
```

两个测试套件都必须全绿才算改完。CI 会把内核回归作为硬性门禁，界面冒烟作为参考项。

## 代码约定

- **语法上限 C# 5**：系统自带的 `csc.exe` 是老版编译器，不支持字符串插值（`$"..."`）、
  空条件运算符（`?.`）、`nameof`。这是"零安装分发"这个目标带来的约束，不是疏忽。
- **零依赖**：不引第三方库。JSON 处理用 `Core/JsonLite.cs`，进程调用用 `Core/Proc.cs`。
- **`.ps1` 文件必须带 UTF-8 BOM 且用 CRLF 换行**。否则 PowerShell 5.1 会按 GBK 解码中文，
  乱码可能吃掉 here-string 的起始标记导致整脚本解析失败 —— 这个坑已经踩过两次。
  仓库里的 `.ps1` 都已规范化，新增脚本请保持一致。

## 改判定规则时

这是最容易改错、也最需要证据的部分。

1. **先看真实数据**。规则不是想出来的，是拿真实机器扫出来、再逐条人工审计出来的：
   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\scan-inventory.ps1
   powershell -ExecutionPolicy Bypass -File tools\make-report.ps1
   ```
   然后用 `AppMover-cli.exe classify <路径>` 抽查。

2. **改完必须加回归用例**，而且**必须用真实存在的目录名**，不能用与正则同构的构造字符串。
   这个教训很贵：曾经用一个自己编的 `Windows PowerShell`（带空格）去"验证"正则，
   测试通过了，但真实目录名是 `WindowsPowerShell`（无空格），结果 32 位系统目录一直漏网。

3. **在 `SelfTest.cs` 对应分组里加断言**，并在注释里写清这条用例是从哪次误判来的。

4. 跑 `selftest`，全绿才算完成。

## 提交信息

说清「为什么」，而不只是「改了什么」。例如：

```
修正 32 位系统目录因路径假设错误而漏网的问题

真实目录名是 WindowsPowerShell（无空格），而规则里写的是
Windows PowerShell。之前用构造字符串自测所以假通过，
换成真实路径才暴露。
```

## 界面改动

界面改动必须让冒烟测试覆盖到关键几何关系。如果新增了会重叠的控件，
在 `Gui/UiSmoke.cs` 里补一条断言。建议顺手做一次**变异测试**：
故意把改动改错，确认测试真的会失败 —— 不会失败的断言没有价值。

## 反馈闭环

用户通过 GitHub Issue 反馈误判 → 补规则 → 加回归用例 → 标注对应的 issue 号。
这是这个项目改进规则的主要途径，见 [docs/FEEDBACK.md](docs/FEEDBACK.md)。
