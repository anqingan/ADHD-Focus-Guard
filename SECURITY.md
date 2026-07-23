# 安全策略

## 报告安全问题

请不要在公开 Issue 中提交 API Key、截图、数据库、日志原文或能够识别个人的信息。优先使用 GitHub 仓库的 Private vulnerability reporting；如果该功能尚未启用，请仅提交不含敏感细节的 Issue，请求维护者提供私密沟通渠道。

报告应尽量包含受影响版本、复现条件、潜在影响和建议修复方向，但不要附带真实用户数据。

## 当前安全边界

API Key 使用 Windows DPAPI CurrentUser 加密，并与 Base URL 绑定。截图只在内存中处理并发送给用户配置的云端模型，不写入磁盘。目标、统计和最终复盘保存在当前用户的 `%LocalAppData%\Vigil\Vigil.db`，目前没有数据库级加密。

本项目仍处于早期开发阶段，尚未提供安装签名、自动更新、安全响应时限或长期支持承诺。
