# Bili-VoxLive

## 项目概述

Bili-VoxLive 是一个B站第三方客户端，专注于提供纯语音直播体验。该应用允许用户登录B站账号，收听直播间的语音流，观看弹幕流，并且支持发送弹幕。

## 主要功能

- 登录B站账号（实现方案是云视听小电视）
- 显示关注列表中的直播间（暂时限制10个）
- 接收直播流
- 显示直播弹幕
- 发送弹幕
- 搜索在线直播间
- 可以选择打开直播画面，但默认是关闭的

## 你为什么需要这个项目

因为B站没有提供纯语音直播流，所以只播放声音并不能节省流量，所以这不应该是你使用这个项目的初衷。

更真实的场景是，你想上班摸鱼，干活有背景音，或者只是单纯不想自己看的直播被人看到，这时候这个项目就派上用场了。

目前的实现是B站客户端直播间的一个相当小的子集，这意味着你不能进行送礼物，点关注等行为。所以这不应该是观看b站直播的唯一方式，可以的话请使用b站官方网页端或者客户端。

## 技术栈

- .NET 8.0
- WPF (Windows Presentation Foundation)
- LibVLCSharp (用于音频流处理)
- NAudio

## 如何构建

1. 克隆仓库
```bash
git clone https://github.com/zjy1412/Bili-VoxLive.git
```

1. 构建解决方案
   - 使用命令行：`dotnet build`

2. 运行应用
   - 使用命令行：`dotnet run`

3. 发布应用
   - 使用命令行：`dotnet publish`

## 隐私说明

应用需要存储B站的登录凭证以便于自动登录，这些信息存储在本地的 `bilibili_cookies.txt` 文件中。请勿分享此文件，以保护账号安全。

## 贡献

欢迎提交问题和功能请求。如果您想为项目做出贡献，请提交拉取请求。

