@echo off
setlocal
pushd "%~dp0"
python -X utf8 tools\Build.py %*
set "BUILD_RESULT=%ERRORLEVEL%"
popd
exit /b %BUILD_RESULT%
