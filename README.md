# GitHub → 百度网盘 自动备份工具

基于 **ASP.NET Core** 的服务端备份工具，服务端直接下载 GitHub 仓库 ZIP 并上传至百度网盘，无需跨域处理。

---

## 版权声明

作者保留所有版权权利。**个人学习、研究、非商业用途可自由使用**。如需用于商业项目，请联系作者获取商业授权并支付版权费用。

邮箱：1426186059@qq.com | 微信：AAA-2025-666-888

---

## ✨ 功能特性

- 🐙 **GitHub 仓库下载** - 支持公开/私有仓库，服务端直连下载
- ☁️ **百度网盘上传** - 使用百度网盘开放平台 API，服务端分片上传
- 🔐 **OAuth 2.0 授权码模式** - 回调页面自动获取 code，主页面自动换 Token
- ⚡ **服务端处理** - 文件流不经过浏览器，无内存/跨域问题
- 📊 **批量备份** - 逐个下载并上传，仓库列表展示状态
- 💾 **配置持久化** - LocalStorage 保存配置，刷新不丢失
- 🛡️ **完整性校验** - 下载后校验文件大小，防止不完整 ZIP 上传

## 🚀 快速开始

### 前置条件

- [.NET 8/10 SDK](https://dotnet.microsoft.com/download)
- 百度网盘开放平台 AppKey 和 SecretKey（需前往开放平台创建应用获取）

### 运行

```bash
cd "百度网盘自动备份github仓库"
dotnet run
```

打开浏览器访问 `http://localhost:5000`

### 使用步骤

1. **配置百度网盘** - 填写你的 AppKey/SecretKey
2. **授权百度网盘** - 点击按钮 → 跳转百度授权 → 自动返回
3. **输入 GitHub 用户名** → 点击「获取仓库列表」
4. **勾选要备份的仓库** → 点击「逐个备份选中仓库」

> ⚠️ 需在百度开放平台将 `http://localhost:5000/callback.html` 添加为回调地址

## 🏗 项目结构

```
BaiduBackup/
├── Program.cs                    # 入口，配置DI、中间件
├── BaiduBackup.csproj            # .NET 项目配置
├── Controllers/
│   ├── AuthController.cs         # 百度 OAuth Token 交换
│   ├── GitHubController.cs       # GitHub 仓库列表 API
│   └── BackupController.cs       # 备份操作（下载+上传）
├── Services/
│   ├── BaiduNetdiskService.cs    # 百度网盘 API（Token/上传）
│   └── GitHubService.cs          # GitHub API（仓库/下载）
├── Models/                       # 请求/响应 DTO
└── wwwroot/
    ├── index.html                # 前端界面
    └── callback.html             # OAuth 回调页面
```

## 📡 API 接口

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | `/api/auth/baidu` | 用授权码换取 access_token |
| GET | `/api/github/repos/{username}` | 获取 GitHub 仓库列表 |
| POST | `/api/backup/single` | 备份单个仓库 |
| POST | `/api/backup/batch` | 批量备份多个仓库 |

## 🔧 百度网盘上传流程

```
1. Precreate（预上传）
2. 分片上传（4MB/片，含重试机制）
3. Create（合并文件）
```

全部在服务端完成，文件流不上传到浏览器再下传。

## ⚠️ 注意事项

- 百度网盘 access_token 有效期约 30 天，过期需重新授权
- GitHub API 未认证限流 60次/小时，建议配置 GitHub Token
- 大仓库（>500MB）备份时间较长，请耐心等待
- 上传路径默认 `/apps/github仓库备份/`
- 下载完成后会校验文件完整性，不完整的 ZIP 不会上传

---
