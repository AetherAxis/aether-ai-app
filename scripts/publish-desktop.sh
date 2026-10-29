#!/usr/bin/env bash
# ⑮ aether-ai-app 桌面端 macOS 发布脚本
#
# 产出：publish/AetherAI.app（Release 自包含 + ad-hoc 签名）——Finder 双击运行 / 拷进 /Applications / 拖进 Dock。
# 说明：
#   - 自包含（--self-contained）：不依赖本机 dotnet，机器上的 SDK 升级/卸载不影响已发布 app；
#   - 数据目录 ~/.aether/aether-ai-app（llm-configs / agents / 会话库 / browser-profile）与调试态**同一份**，
#     发布版和 dotnet run 版可互换使用；
#   - 依赖「兄弟仓并排于 AetherAxis 根下」的本地布局（跨仓 ProjectReference）——这是本机脚本，不是 CI 产物；
#   - 发布版验证：Contents/MacOS/Aether.AI.App.Desktop --browser-smoke（五断言，会闪浏览器窗口）。
set -euo pipefail
cd "$(dirname "$0")/.."   # 仓根

case "$(uname -m)" in
  arm64)  RID="osx-arm64" ;;
  x86_64) RID="osx-x64" ;;
  *) echo "❌ 未知架构 $(uname -m)"; exit 1 ;;
esac

APP_NAME="AetherAI"
VERSION="0.1.0"
EXE="Aether.AI.App.Desktop"
OUT="publish"

echo "==> dotnet publish（Release / $RID / 自包含）"
dotnet publish "src/$EXE/$EXE.csproj" -c Release -r "$RID" --self-contained true -o "$OUT/payload"

echo "==> 组装 .app bundle"
BUNDLE="$OUT/$APP_NAME.app"
rm -rf "$BUNDLE"
mkdir -p "$BUNDLE/Contents/MacOS" "$BUNDLE/Contents/Resources"
cp -R "$OUT/payload/." "$BUNDLE/Contents/MacOS/"

cat > "$BUNDLE/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>$APP_NAME</string>
    <key>CFBundleDisplayName</key><string>Aether AI</string>
    <key>CFBundleIdentifier</key><string>com.aetheraxis.aiapp</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleExecutable</key><string>$EXE</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>LSMinimumSystemVersion</key><string>10.15</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>
PLIST

echo "==> ad-hoc 签名（本地构建无 quarantine 属性；arm64 上是硬要求，x86 上保险）"
codesign --force --deep -s - "$BUNDLE" 2>/dev/null \
  || echo "  ⚠ codesign 跳过（未装 Xcode CLT；x86 无碍，arm64 上无法启动时需手动执行）"

echo ""
echo "✅ 发布完成：$PWD/$BUNDLE"
echo "   双击运行，或拷进 /Applications：cp -R \"$PWD/$BUNDLE\" /Applications/"
echo "   命令行验证（可选）：\"$BUNDLE/Contents/MacOS/$EXE\" --browser-smoke"
