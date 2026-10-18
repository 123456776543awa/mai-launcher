# Maimai Cabinet Launcher

给家用/自建 maimai 机台用的 Windows 启动器：原生外壳（C# / .NET 8 WinForms）+ WebView2 界面，
在 1080×1920 竖屏上提供游戏风格的圆屏菜单，并把 io4 按键、触摸屏、灯板统一接起来。

> 非官方项目，与 SEGA / maimai 官方无关，仅供自用机台。

## 功能

- **圆屏菜单**：分类页签（MCM 风格分类）、卡片列表、详情页、右侧只读选项列表 + 高亮框；
- **输入**：
  - io4 按键（VID_0CA3&PID_0021）通过 Raw Input 读取，A4=OK、A5=BACK、A3/A6=切换条目、A1/A8=音量；
  - 触摸屏（STM32 Virtual ComPort，VID_0483&PID_5740，9600）按 `{RSET}`→`{HALT}`→`{STAT}` 初始化，解析 34 区；
  - 输入框使用物理键盘（Enter 保存、Esc 取消）；
- **输出**：灯板（默认 COM21，115200）按键颜色、开机扫灯；
- **AquaMai**：按当前 `AquaMai.toml` 实时解析生成设置页，支持布尔/数字写入（A4 保存、A5 丢弃）、字符输入、更新 AquaMai（保留 toml）；
- **多版本**：版本列表、新建版本（选 .bat）、重命名；
- **其他**：MajPlay / MCM 启动、状态看板（io4/触摸/LED/配置/关于）、托盘图标、断线重连（触摸 1s、LED 3s、io4 3s）、单实例。

## 硬件前提

| 设备 | 说明 |
|---|---|
| 显示器 | 竖屏 1080×1920（副屏） |
| io4 | VID_0CA3&PID_0021，64 字节 Raw Input，按键在 offset 29/30 |
| 触摸屏 | STM32 VCP，VID_0483&PID_5740，9600 8N1 |
| 灯板 | Mai2 LED 串口，默认 COM21，115200 |

端口都会自动解析（触摸按 VID/PID，LED 优先读 AquaMai 的 `PortName_1PLED`），COM 号变化不影响。

## 构建

```powershell
dotnet publish .\src\MaimaiLauncher -c Release -r win-x64 --self-contained true -o ..\publish
```

自包含发布，不需要目标机器安装 .NET。发布后把 `publish` 整个目录拷到目标机器即可运行。

网页部分由模板生成（已提交生成结果，改模板后再跑）：

```powershell
python tools\build_menu_v2.py
```

## 配置

首次运行会生成 `config.json`，可参考 `config.example.json`：

- `Games[]`：版本列表（`Name` + `Bat`），`ActiveGame` 为当前版本；
- `MajPlay.Exe` / `Mcm`：外部程序路径；
- `AquaMai.Toml`：`AquaMai.toml` 的绝对路径，`Writable` 控制是否允许写入；
- `Touch.Port`：`auto` 表示按 VID/PID 自动找口，`Baud` 默认 9600；
- `Ui`：`v2`（新菜单）或 `v1`（旧界面）。

## 按键映射（外键）

| 键 | 菜单 | 输入框 |
|---|---|---|
| A1/A8 | 音量 −/+ | — |
| A3/A6 | 上一条目 / 下一条目 | — |
| A4 | 进入 / 保存 | 保存 |
| A5 | 返回 | 取消 |
| A2/A7 | — | — |

触摸：E8/E2 翻分类、A3/A6 切条目、B4/B5 调值、A4 确认（列表页触摸不会进入，只能外键）。

## 已知问题

- MajPlay 由启动器拉起时可能卡在 Unity 初始化（直接双击正常），目前是释放设备后独立启动；
- 菜单分类是按关键词的启发式映射，个别 section 可能归类不准；
- 触摸/LED 串口被游戏占用时启动器会显示“未连接”，退出游戏后自动恢复。

## 致谢

- [MajdataPlay](https://github.com/TeamMajdata/MajdataPlay)（GPL-3.0）：触摸/LED 协议参考；
- [AquaMai](https://github.com/MuNET-OSS/AquaMai)：游戏侧插件与配置；
- [MaiChartManager](https://github.com/MewoLab/MaiChartManager)：配置编辑器的分类与交互参考。

本项目为独立实现，未包含上述项目的代码或二进制。

## 许可

MIT，见 `LICENSE`。
