# YokoWave Minecraft

Java 25 客户端 Mod。F8 打开连接与输出设置，单人和多人游戏均无需服务端安装。连接后手动启用；初始强度上限为 0。

| 加载器 | 构建目标 |
| --- | --- |
| Fabric | Minecraft 1.20.1、1.21.1；Loader 0.20.1+、对应版本 Fabric API |
| Forge | Minecraft 1.20.1；Forge 47.4.29+ |
| NeoForge | Minecraft 1.21.1；NeoForge 21.1.265+ |

分别构建，不能混用 JAR，也不声明支持所有 1.20 之后的版本。游戏启动器也必须使用 **Java 25**；较早加载器可能不识别其字节码。

## 连接

- **YokoWave 事件**：桌面端启动事件服务端，填写完整 `/ws/<节点 ID>` 地址与令牌。Mod 声明受伤、死亡、复活、跳跃与状态数值，由节点图决定输出。
- **YokoWave 控制**：桌面端启动 control 服务端，填写地址与令牌，连接后选择设备。在「输出」页设置实际通道、强度上限、频率、时长和每点伤害的强度，再启用。受伤时发送一次有限波形。
- **DG-LAB V4**：填写中继地址（可用 YokoWave 中继模板，如 `ws://电脑局域网地址:9998/v4`），连接后打开配对二维码，用 DG-LAB 4 App 扫描，再选择上报的设备。App 通过中继接入，不需要 control 服务端。手机和电脑都须能访问该地址，`127.0.0.1` 仅适合同机测试。

有效设置自动保存。修改输出参数会停止并需要重新启用；选择设备不会重连中继。令牌仅在内存。退出世界、暂停或连接故障禁用输出；重新连接不会自动启用。配对 ID 只在当前连接内有效。事件与限制见[实现说明](../../docs/minecraft.md)。

## 构建

在本目录运行，Windows 用 `./gradlew.bat` 代替 `bash gradlew`：

```sh
bash gradlew build -Ploader=fabric -Pminecraft_version=1.20.1
bash gradlew build -Ploader=fabric -Pminecraft_version=1.21.1
bash gradlew build -Ploader=forge -Pminecraft_version=1.20.1
bash gradlew build -Ploader=neoforge -Pminecraft_version=1.21.1
```

安装对应加载器 `build/libs/` 中的 JAR；`build/devlibs/` 的开发包不能安装。依赖的 [Java SDK](../java-sdk/) 通过 Gradle composite build 自动构建并合入，Gson 使用游戏自带版本，ZXing 随包提供。

独立 [Minecraft CI](../../.github/workflows/minecraft.yml) 使用 `minecraft-v<版本>` 标签验证 `mod_version` 后发布四个 JAR，不替换桌面版 latest release。本模块不进入 .NET solution 或 Pages 静态包。
