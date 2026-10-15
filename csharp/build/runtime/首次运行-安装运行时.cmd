@echo off
chcp 65001 >nul
setlocal

echo.
echo  iOpenPod 管理器 —— 首次运行准备
echo  ================================
echo.
echo  正在安装 Windows App Runtime 1.8 运行时组件…
echo  （这是微软官方组件，只需安装一次；如果已经安装过会自动跳过。）
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0安装运行时.ps1"

if errorlevel 1 (
  echo.
  echo  运行时安装失败。请查看上方提示。
  echo  也可以手动双击 Runtime 目录里的四个 .msix 文件逐个安装。
  echo.
) else (
  echo.
  echo  准备完成，现在可以双击 iOpenPod.exe 启动了。
  echo.
)

pause
endlocal
