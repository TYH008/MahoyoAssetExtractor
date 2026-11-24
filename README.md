# MahoyoAssetExtractor
Steam 版《魔法使之夜》资源提取工具

A free Windows tool for extracting supported images, audio, and videos from the Steam edition of *WITCH ON THE HOLY NIGHT*.

1. Open [Releases](https://github.com/TYH008/MahoyoAssetExtractor/releases/tag/v1.0.0) and download `MahoyoAssetExtractor-win-x64.exe` under **Assets**.
2. Double-click the EXE. Select your Steam game folder, choose an output folder, and tick the categories you want.
3. If you mainly want CGs and backgrounds, choose **Gallery images**. Extracting **All images** can take about 16 GB of disk space and took about 40 minutes on the project owner's PC. Your time will vary.

The interface starts in English and supports English, Japanese, Simplified Chinese, and Traditional Chinese. The download is a single EXE for 64-bit Windows. It includes the .NET runtime and needs no installer. The tool reads game files locally and does not include game assets.

## 中文说明

Mahoyo Asset Extractor 是免费 Windows 工具，可从你拥有的 Steam 版《魔法使之夜》中批量提取支持的图片、音频和视频。在 [Releases](https://github.com/TYH008/MahoyoAssetExtractor/releases/tag/v1.0.0) 的 **Assets** 下载 `MahoyoAssetExtractor-win-x64.exe`，双击打开后选择游戏目录、输出目录和提取类别。想找 CG 和背景时，建议选择「画廊图片」。全部图片约占 16 GB，作者的电脑上提取约用 40 分钟，实际时间因配置而异。

## Runtime licenses / 运行时许可

The bundled .NET 8.0.29 license and third-party notices are in [licenses](licenses/).

## Build from source / 从源码构建

On Windows with the .NET 8 SDK installed, run:

```powershell
dotnet build src/VNAssetExplorer.App/VNAssetExplorer.App.csproj -c Release
```

## Project license / 项目许可

The [project license](LICENSE) uses PolyForm Noncommercial 1.0.0 with an additional limit on commercial distribution. See the [distribution summary](DISTRIBUTION-TERMS.md).

[项目许可](LICENSE) 以 PolyForm 非商业许可证 1.0.0 为基础，另有禁止商业分发的条款。欢迎为非商业目的使用、修改和分享。详见[分发说明](DISTRIBUTION-TERMS.md)。
