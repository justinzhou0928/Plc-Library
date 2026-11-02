# 发布与打标签规范

适用对象：维护者。目标：**tag 本身就是一份可读的发布说明**，而不是只有版本号；提交、CHANGELOG 与 tag 内容保持一致。

## 1. 版本号

- 遵循 SemVer：`MAJOR.MINOR.PATCH`；修复/CI/文档用 PATCH，兼容新增用 MINOR，不兼容变更用 MAJOR。
- 唯一版本源：`Directory.Build.props` 的 `<Version>`。
- 每次发布必须在 `CHANGELOG.md` 增加 `## [X.Y.Z] - YYYY-MM-DD` 章节，按 `修复 / 变更（不兼容） / 性能与精简 / 测试 / 构建 / 文档` 分类。
- 版本号、CHANGELOG、tag 名称三者必须一致。

## 2. 发布提交

发布提交只包含版本号、CHANGELOG 与本版本必要的文档/CI 修正，不混入无关重构。

提交标题：

```text
release: vX.Y.Z <一句话摘要>
```

提交正文建议包含：

```text
- 主要修复
- 主要变更/不兼容点
- 测试与验证方式
```

示例：

```text
release: v1.3.4 CI annotation cleanup

- Linux CI BACnet simulator uses shared+exclusive UDP ports
- CI reports failed test names from trx
- Optional NuGet.org publish no longer fails the tag build
```

不要再用模糊的 `chore: 版本号提升至 x.y.z` 作为唯一发布信息。

## 3. Tag 必须 annotated

禁止 lightweight tag：

```bash
# 错误：没有 tag message，GitHub 上只能看到版本号
git tag v1.3.4

# 正确
git tag -a v1.3.4 -F release-notes.txt
```

tag 名称固定为 `vX.Y.Z`（带 `v`）。tag 必须指向发布提交，且工作区干净、CI 通过。

## 4. Tag message 模板

tag message 至少包含：版本、日期、一句话摘要、分类变更、验证结果。内容与 CHANGELOG 对应章节一致，可直接复制。

```text
PlcLibrary vX.Y.Z — YYYY-MM-DD

<一句话摘要：本版本最重要的 1–2 件事>

### 修复
- ...

### 变更（不兼容）
- ...

### 性能与精简
- ...

### 测试
- ...

### 构建
- ...
```

要求：

- 不能只写 `PlcLibrary v1.3.4`；
- 不兼容变更必须单独列 `### 变更（不兼容）`；
- 测试写“验证了什么”，不要只写数量；数量以当次 `dotnet test` 输出为准；
- 外部依赖/CI 限制（如 NuGet secret、GitHub Actions 警告）写在 `### 构建`，不要伪装成已发布。

示例（v1.3.4）：

```text
PlcLibrary v1.3.4 — 2026-11-02

Linux CI BACnet 模拟器端口修复，tag 发布流水线加固。

### 修复
- BACnet 测试模拟器改用 SDK shared + exclusive UDP 端口模式，修复 Ubuntu 上 Address already in use
- 11 个 BACnet 模拟器用例在 Ubuntu 通过

### 测试
- 全量 426 个用例通过（Windows 本地 + Ubuntu CI）
- CI 失败时上传 TRX 并输出失败测试 annotation

### 构建
- GitHub Packages 发布成功
- NuGet.org 可选发布失败时只输出 warning，不再让 tag CI 失败
```

## 5. 发布步骤

```bash
# 1. 干净工作区，全量验证
git status --short
dotnet build PlcLibrary.slnx -c Debug
dotnet test PlcLibrary.Tests/PlcLibrary.Tests.csproj -c Debug

# 2. 更新 Directory.Build.props 与 CHANGELOG，提交
git add Directory.Build.props CHANGELOG.md
git commit -m "release: vX.Y.Z <摘要>"

# 3. 推送 master，等 CI 绿
git push github master

# 4. 用模板写 tag message，创建 annotated tag
git tag -a vX.Y.Z -F release-notes.txt

# 5. 推送 tag（tag CI 会跑 build/test/pack/publish）
git push github vX.Y.Z

# 6. 确认 tag CI 与 annotation 无 error
git ls-remote --tags github vX.Y.Z
```

## 6. Tag CI 失败时

- **不要移动或强推已有 tag**；tag 一旦推送视为不可变。
- 修复后发一个 **PATCH** 版本（如 1.3.4 → 1.3.5），用新 tag 重新走流程。
- 如果只是 tag message 写错、尚未推送，可重新 `git tag -a -f`；**已推送后不允许**覆盖。

## 7. 检查清单

- [ ] `Directory.Build.props`、`CHANGELOG.md`、tag 名称一致
- [ ] 发布提交只包含本版本内容
- [ ] tag 是 annotated（`git cat-file -t vX.Y.Z` 返回 `tag`）
- [ ] tag message 含摘要、分类变更、验证结果，不是只有版本号
- [ ] tag CI 的 build/test/pack 全绿，annotation 无 error
- [ ] 失败/限制（如 NuGet secret）在 tag message 和 CHANGELOG 中如实说明
