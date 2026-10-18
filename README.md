# DropKit Windows

把剪贴板内容和文件临时收在一个浮窗里，支持搜索、固定与拖拽取用。

这是基于 [chenyuxiaojin/DropKit](https://github.com/chenyuxiaojin/DropKit) 的非官方 Windows 社区移植。原作采用 MIT 许可，完整许可保留在 LICENSE。

## 使用

下载公开包后运行 DropKit.exe；按 F4 打开或收起。文件暂存和剪贴板历史分开管理。用户数据保存在本机，不随公开包发布。

## 构建

在 Windows PowerShell 中执行 `./build.ps1`，输出位于 release。使用 Windows 自带 .NET Framework 4.8 编译器。

## 自检

`./release/DropKit.exe --self-test --data-dir <独立测试目录>`

删除操作使用 Windows 回收站；不把系统加密存储当作密码保险箱。
