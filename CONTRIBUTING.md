# 参与贡献

感谢你愿意改进 ADHD Focus Guard。当前项目仍处于早期阶段，优先接受能够提高稳定性、隐私保护、可测试性和 Windows 使用体验的改动。

## 开发环境

需要 Windows 11 x64 和 .NET 10 SDK。克隆仓库后运行：

```powershell
dotnet restore Vigil.Windows.slnx
dotnet build Vigil.Windows.slnx --configuration Debug
dotnet test Vigil.Windows.slnx --configuration Debug
```

涉及 WPF、截屏、托盘、全局快捷键或 DPI 的改动需要在真实 Windows 桌面环境中人工验证。

## 代码边界

`Vigil.Core` 维护产品规则，不应依赖 WPF 或具体云服务；`Vigil.Infrastructure` 封装 Windows API、网络、加密和存储；`Vigil.App` 负责界面和交互。新增规则应优先放入 Core 并提供单元测试。

不得提交 API Key、真实截图、用户数据库、日志、证书或个人配置。AI 请求日志不得包含 Authorization、请求正文、图片 Base64 或完整模型响应。

## 提交改动

请让每个提交只解决一个清晰问题，并在 Pull Request 中说明改动原因、用户影响、验证方法以及隐私或费用方面的变化。行为变更需要同步更新 README 或 `docs/产品功能与实现逻辑.md`。

提交贡献即表示你同意将改动按仓库的 [MIT License](LICENSE) 授权。请勿提交来源不明、许可证不兼容或从原 Vigil Swift 项目直接复制的代码。
