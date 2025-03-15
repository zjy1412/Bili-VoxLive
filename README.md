# Bili-VoxLive

## 项目概述

Bili-VoxLive 是一个B站第三方客户端，专注于提供纯语音直播体验。该应用允许用户登录B站账号，收听直播间的语音流，观看弹幕流，并且支持发送弹幕。

## 主要功能

- 登录B站账号
- 显示关注列表中的直播间
- 接收纯语音的直播流
- 显示直播弹幕
- 发送弹幕

## 界面特点

竖直状的方型界面，依次是：
- 状态栏和标签页（打开的直播间）
- 弹幕流显示区域
- 语音波形图显示区域，可调整语音大小
- 弹幕发送窗口

## 技术栈

- .NET 8.0
- WPF (Windows Presentation Foundation)
- LibVLCSharp (用于音频流处理)
- NAudio

## 如何构建

1. 克隆仓库
```bash
git clone https://github.com/你的用户名/Bili-VoxLive.git
```

2. 使用Visual Studio打开 `Bili-VoxLive.sln` 解决方案文件

3. 构建解决方案
   - 在Visual Studio中点击"构建" > "构建解决方案"
   - 或使用命令行：`dotnet build`

4. 运行应用
   - 在Visual Studio中点击"调试" > "开始调试"
   - 或使用命令行：`dotnet run`

## 隐私说明

应用需要存储B站的登录凭证以便于自动登录，这些信息存储在本地的 `bilibili_cookies.txt` 文件中。请勿分享此文件，以保护账号安全。

## 贡献

欢迎提交问题和功能请求。如果您想为项目做出贡献，请提交拉取请求。

## 许可证

[待定] 