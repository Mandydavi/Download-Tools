<#
    极速下载器 · 发版脚本
    ------------------------------------------------------------
    用法：
        发版.bat 2.1.1
        发版.bat 2.1.1 -Note "修复 Civitai 解析失败"

    做了什么：
        1. 关掉正在运行的实例（否则 exe 被占用，复制会失败）
        2. 把当前 极速下载器.exe 备份到 旧版本\ 带版本号+时间戳
        3. 改写 csproj 的 <Version>
        4. dotnet build 检查错误（失败自动把版本号改回去）
        5. 发布 win-x64 自包含单文件，替换 极速下载器.exe
        6. 可选：把 -Note 追加进 更新日志.md

    只改版本号不打包： 发版.bat 2.1.1 -DryRun
    版本号没变只想重打包： 发版.bat 2.1.1 -Force
#>
param(
    [string]$Version = "",
    [string]$Note = "",
    [switch]$Force,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$root       = $PSScriptRoot
$proj       = Join-Path $root "ComfyDL.Desktop\ComfyDL.csproj"
$exeOut     = Join-Path $root "极速下载器.exe"
$backupDir  = Join-Path $root "旧版本"
$publishDir = Join-Path $env:TEMP ("comfydl-publish-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$changelog  = Join-Path $root "更新日志.md"

function Say([string]$msg, [string]$color = "Gray") {
    Write-Host $msg -ForegroundColor $color
}

# dotnet 的进度条在非交互环境会刷成几十行，收进变量里，只在失败时吐出来
function Invoke-Dotnet {
    param([string[]]$dnArgs)

    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $text = & dotnet @dnArgs 2>&1 | Out-String
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev

    return [pscustomobject]@{ Code = $code; Text = $text }
}

if (-not (Test-Path $proj)) {
    Say "找不到工程文件：$proj" "Red"
    exit 1
}

$xml = [System.IO.File]::ReadAllText($proj)
$m = [regex]::Match($xml, '<Version>([^<]+)</Version>')
$old = "0.0.0"
if ($m.Success) { $old = $m.Groups[1].Value }

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Read-Host "当前版本 $old，输入新版本号（如 2.1.1）"
}
$Version = $Version.Trim().TrimStart([char[]]"vV")

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Say "版本号格式不对，要 X.Y.Z（例：2.1.1），收到的是「$Version」" "Red"
    exit 1
}
if ($Version -eq $old -and -not $Force) {
    Say "版本号还是 $old，没变化。只想重新打包请加 -Force" "Yellow"
    exit 1
}

Say " "
Say "================ 发版  $old  ->  $Version  ================" "Cyan"
Say " "

function Restore-Version {
    [System.IO.File]::WriteAllText($proj, $xml, (New-Object System.Text.UTF8Encoding($false)))
    Say "已把 csproj 版本号改回 $old" "Yellow"
}

# ---------- 只改版本号，不打包 ----------
if ($DryRun) {
    $dryXml = $xml -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>"
    [System.IO.File]::WriteAllText($proj, $dryXml, (New-Object System.Text.UTF8Encoding($false)))
    Say "  [DryRun] csproj 版本号 -> $Version（没有编译打包）" "Yellow"
    exit 0
}

# ---------- 1. 关进程 ----------
$running = Get-Process -Name ComfyDL -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    Say "  [1/5] 已关闭正在运行的实例" "DarkGray"
}

# ---------- 2. 备份旧版 ----------
if (Test-Path $exeOut) {
    if (-not (Test-Path $backupDir)) { New-Item -ItemType Directory -Path $backupDir | Out-Null }
    $stamp = Get-Date -Format "yyyyMMdd-HHmm"
    $bak = Join-Path $backupDir ("极速下载器-v{0}-{1}.exe" -f $old, $stamp)
    Copy-Item $exeOut $bak -Force
    Say ("  [2/5] 旧版已备份 -> 旧版本\{0}" -f (Split-Path $bak -Leaf)) "DarkGray"
}
else {
    Say "  [2/5] 没有旧版可备份（首次发版）" "DarkGray"
}

# ---------- 3. 改版本号 ----------
$newXml = $xml -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>"
[System.IO.File]::WriteAllText($proj, $newXml, (New-Object System.Text.UTF8Encoding($false)))
Say "  [3/5] csproj 版本号 -> $Version" "DarkGray"

# ---------- 4. 编译 ----------
Say "  [4/5] 编译中 ..." "DarkGray"
$build = Invoke-Dotnet -dnArgs @("build", $proj, "-c", "Release", "-v", "q", "--nologo")
if ($build.Code -ne 0) {
    Say " "
    Write-Host $build.Text.Trim() -ForegroundColor DarkRed
    Say " "
    Say "编译失败。" "Red"
    Restore-Version
    exit 1
}

# ---------- 5. 发布 ----------
Say "  [5/5] 打包自包含单文件 ..." "DarkGray"
$pub = Invoke-Dotnet -dnArgs @(
    "publish", $proj,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-o", $publishDir,
    "-v", "q", "--nologo"
)

if ($pub.Code -ne 0) {
    Say " "
    Write-Host $pub.Text.Trim() -ForegroundColor DarkRed
    Say " "
    Say "打包失败。" "Red"
    Restore-Version
    exit 1
}

$built = Join-Path $publishDir "ComfyDL.exe"
if (-not (Test-Path $built)) {
    Say "打包产物不存在：$built" "Red"
    Restore-Version
    exit 1
}

# ---------- 替换 ----------
Copy-Item $built $exeOut -Force

# 临时目录能清就清，清不掉也不影响发版（系统会自己回收）
try { Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue } catch { }

$f = Get-Item $exeOut
$sizeText = "{0:N0}" -f $f.Length
$verText = $f.VersionInfo.FileVersion

# ---------- 更新日志 ----------
$logNote = ""
if (-not [string]::IsNullOrWhiteSpace($Note)) {
    if (-not (Test-Path $changelog)) {
        [System.IO.File]::WriteAllText($changelog, "# 更新日志`r`n`r`n", (New-Object System.Text.UTF8Encoding($false)))
    }
    $line = "- **v$Version**（{0}）{1}" -f (Get-Date -Format "yyyy-MM-dd"), $Note
    Add-Content -Path $changelog -Value $line -Encoding UTF8
    $logNote = "已追加到 更新日志.md"
}

Say " "
Say "================ 完成 ================" "Green"
Say ("  文件    {0}" -f $f.Name) "Green"
Say ("  大小    {0} 字节" -f $sizeText) "Green"
Say ("  版本    v{0}" -f $verText) "Green"
if ($logNote) { Say ("  " + $logNote) "DarkGray" }
Say " "
Say "下一步：双击 极速下载器.exe 自己跑一遍，确认没问题再分发。" "Cyan"
