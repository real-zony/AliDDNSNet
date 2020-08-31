@echo off&color e&Title 阿里云动态DNS-API 命令行控制台

cls

:Begin

echo   **********************************
echo.
echo          阿里云动态DNS-API 命令行控制台
echo.
echo   **********************************
echo. & echo   Script: %0% & echo.
echo 请输入命令：
echo.
echo     list         查询 阿里云动态DNS-API 服务
echo     config       打开 阿里云动态DNS-API 配置文件
echo.
echo     start        启动 阿里云动态DNS-API 进程
echo.
echo     restart        重启 阿里云动态DNS-API 进程
echo.
echo     kill        杀死 阿里云动态DNS-API 进程
echo.
echo     exit         退出控制台
echo     cmd          启动 cmd

echo.&echo.&set /p cmd=请输入：

if  "%cmd%"=="list"    goto List
if  "%cmd%"=="config"     goto Config
if  "%cmd%"=="start"   goto Start
if  "%cmd%"=="exit"    goto End
if  "%cmd%"=="kill"     goto Kill
if  "%cmd%"=="restart"   goto ReStart
if  "%cmd%"=="cmd"     goto Cmd

cls
goto Begin

:End
exit

:Kill
cls
echo.
echo     尝试杀死 阿里云动态DNS-API 进程
echo.
echo.&echo.
taskkill /F /IM AliCloudDynamicDNS.exe* 
set cmd=
echo.&echo     执行完成
echo.&echo.
echo.&echo.&set /p waitinput=按Enter键返回功能菜单
cls
goto Begin


:Start
cls
echo.
echo     尝试启动 阿里云动态DNS-API 进程
echo.
echo.&echo.&set /p interval=请输入监听的时间周期(单位：秒，大于等于30)：
echo.&echo.

start cmd.exe "/k title 阿里云动态DNS-API && AliCloudDynamicDNS.exe -f ./settings.json -i %interval%"

set cmd=
echo.&echo     执行完成
echo.&echo.
echo.&echo.&set /p waitinput=按Enter键返回功能菜单
cls
goto Begin


:ReStart
cls
echo.
echo     尝试杀死 阿里云动态DNS-API 进程
echo.
echo.&echo.
taskkill /F /IM AliCloudDynamicDNS.exe* 
echo.
echo     尝试启动 阿里云动态DNS-API 进程
echo.
echo.&echo.&set /p interval=请输入监听的时间周期(单位：秒，大于等于30)：
echo.&echo.

start cmd.exe "/k AliCloudDynamicDNS.exe -f ./settings.json -i %interval% && title 阿里云动态DNS-API

set cmd=
echo.&echo     执行完成
echo.&echo.
echo.&echo.&set /p waitinput=按Enter键返回功能菜单
cls
goto Begin


:List
cls
echo.
echo     查询 阿里云动态DNS-API 服务
echo.

tasklist /fi "imagename eq AliCloudDynamicDNS.exe"
set cmd=
echo.&echo.
echo.&echo.&set /p waitinput=按Enter键返回功能菜单
cls
goto Begin

:Cmd
cls
start cmd
set cmd=
goto Begin

:Config
cls
echo.
echo     打开 阿里云动态DNS-API 配置文件
echo.

start notepad %cd%\settings.json
set cmd=
echo.&echo.
goto Begin
