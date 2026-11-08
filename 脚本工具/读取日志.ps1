<#
.SYNOPSIS
    命令行读取 / 过滤 / 跟随 / 切换 DockedTools 的应用日志。

.DESCRIPTION
    日志由 LogService 写在 MSIX 包内的 LocalState\logs 下：
      app.log    —— Debug / Info / Warning（滚动档 app.log.1 … app.log.4）
      error.log  —— Error（逐条刷盘，崩溃现场优先看这个）
      level.txt  —— 运行期级别开关，写 Debug / Info / Warning / Error 之一
      buffer.txt —— 运行期 Debug 缓冲开关，写 on / off 之一；不存在 = on（默认缓冲）
                     Debug 档先进内存环形缓冲，只有出现 Error 时才整段吐进 app.log；
                     写 off 退回「Debug 逐条落盘」，-Follow 实时 tail 就用它。

    为什么要这个脚本：MSIX 容器路径不好记、Release 包没有控制台、
    而且出问题的时候应用可能根本打不开设置页 —— 一条命令能看日志、
    能把详细级别打开再复现，比重新打包快得多。

    脚本用 FileShare.ReadWrite + Delete 打开文件，因此可以边写边读，
    并且不会挡住应用自己的日志滚动（应用改名滚动文件时要求没有其它句柄）。

    默认级别是 Info，Debug 档不落盘。要看细节先 -SetLevel Debug，复现完 -SetLevel Info 关掉。

.PARAMETER Tail
    输出最后 N 条日志（默认 50）。

.PARAMETER Follow
    持续跟随新日志，Ctrl+C 退出。

.PARAMETER Level
    只要某个级别：Error / Warning / Info / Debug（Debug 隐含「需要先 -SetLevel Debug」）。

.PARAMETER Module
    按模块名过滤，例如 -Module 网页应用。

.PARAMETER Pattern
    按正则过滤整条日志（含堆栈）。

.PARAMETER Since
    只要这个时间之后的：30m / 2h / 1d / "2026-10-16 09:30"。

.PARAMETER File
    只看某个文件：app / error / all（默认 all，按滚动档从旧到新合并）。

.PARAMETER List
    只列出日志文件、大小、当前级别，不打印内容。

.PARAMETER Stats
    按「级别 + 模块」统计条数，用于快速定位谁在刷屏。

.PARAMETER Json
    以 JSON 输出（每行一个对象），便于管道给别的工具。

.PARAMETER Export
    把整个日志目录打包成 zip，用于贴给别人排查。

.PARAMETER SetLevel
    写 level.txt 切换运行期级别，最多 5 秒后应用自己生效，无需重启。

.PARAMETER ShowLevel
    显示当前落盘级别与 level.txt 路径。

.PARAMETER SetBuffer
    写 buffer.txt 切换 Debug 档的落盘方式：on = 进内存缓冲、出错才吐盘（默认）；
    off = 逐条落盘，配合 -Follow 实时看细节。最多 5 秒后生效，不用重启应用。

.PARAMETER Package
    读哪个包的日志：auto（默认，取最近写入的那个）/ release / debug / all。

.PARAMETER PathOverride
    直接指定日志目录（未打包运行、或想在别的机器上读时用）。

.PARAMETER BuildLog
    读项目根目录下的 winapp 构建日志（构建失败时排查用）。

.EXAMPLE
    .\读取日志.ps1 -Tail 200 -Level Error

.EXAMPLE
    .\读取日志.ps1 -Follow -Module 网页应用

.EXAMPLE
    .\读取日志.ps1 -SetLevel Debug; # 复现问题
    .\读取日志.ps1 -Since 10m -Pattern 'WebView2'
    .\读取日志.ps1 -SetLevel Info

.EXAMPLE
    .\读取日志.ps1 -Export $env:USERPROFILE\Desktop\日志.zip
#>

[CmdletBinding()]
param(
    [int]$Tail = 50,
    [switch]$Follow,
    [ValidateSet('Debug', 'Info', 'Warning', 'Error')]$Level,
    [string]$Module,
    [string]$Pattern,
    [string]$Since,
    [ValidateSet('app', 'error', 'all')]$File = 'all',
    [switch]$List,
    [switch]$Stats,
    [switch]$Json,
    [string]$Export,
    [ValidateSet('Debug', 'Info', 'Warning', 'Error')]$SetLevel,
    [switch]$ShowLevel,
    [ValidateSet('on', 'off')]$SetBuffer,
    [ValidateSet('auto', 'release', 'debug', 'all')]$Package = 'auto',
    [string]$PathOverride,
    [switch]$BuildLog
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 一条日志的开头：[2026-10-16 09:00:00.123] [Info] [模块] 正文
$script:HeaderRegex = '^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\] \[(Debug|Info|Warning|Error)\] \[([^\]]*)\] ?(.*)$'
# 结尾标志：位置行永远是 FormatMessage 的最后一行
$script:LocationPrefix = '位置: '

function Open-SharedTextReader {
    param([string]$Path)

    # ReadWrite + Delete：既能读到正在被追加的文件，又不妨碍应用把它改名滚动
    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
    $reader = New-Object System.IO.StreamReader($stream, (New-Object System.Text.UTF8Encoding($false)))
    return $reader
}

function Resolve-LogDirectory {
    param([switch]$Quiet)

    if ($PathOverride) {
        if (-not (Test-Path -LiteralPath $PathOverride)) {
            throw "指定的日志目录不存在：$PathOverride"
        }
        return @((Resolve-Path -LiteralPath $PathOverride).Path)
    }

    $packagesRoot = Join-Path $env:LOCALAPPDATA 'Packages'
    $candidates = @()

    # 包名：Release = 8B8CC4F4.482486777ECD9，Debug 带 .Debug 后缀，两个 LocalState 是分开的
    if (Test-Path -LiteralPath $packagesRoot) {
        $folders = Get-ChildItem -LiteralPath $packagesRoot -Directory -Filter '8B8CC4F4.482486777ECD9*' -ErrorAction SilentlyContinue
        foreach ($folder in $folders) {
            $logsDir = Join-Path $folder.FullName 'LocalState\logs'
            if (-not (Test-Path -LiteralPath $logsDir)) { continue }

            $isDebug = $folder.Name -like '*.Debug*'
            if ($Package -eq 'release' -and $isDebug) { continue }
            if ($Package -eq 'debug' -and -not $isDebug) { continue }

            $candidates += [pscustomobject]@{
                Path          = $logsDir
                IsDebug       = $isDebug
                LastWriteTime = (Get-Item -LiteralPath $logsDir).LastWriteTime
            }
        }
    }

    if ($candidates.Count -eq 0) {
        # 兜底：LogService 拿不到 LocalFolder 时会退到 %TEMP%\DockedAI_Logs
        $tempDir = Join-Path $env:TEMP 'DockedAI_Logs'
        if (Test-Path -LiteralPath $tempDir) {
            if (-not $Quiet) { Write-Host "（没找到已安装的包，改用兜底目录）" -ForegroundColor DarkYellow }
            return @($tempDir)
        }
        throw "找不到日志目录。已安装的话请确认包名 8B8CC4F4.482486777ECD9* 是否存在，或用 -PathOverride 指定。"
    }

    if ($Package -eq 'all') {
        return @($candidates | Sort-Object IsDebug | ForEach-Object { $_.Path })
    }

    # auto：取最近有写入的那个（Debug 包通常才是刚刚跑过的那份）
    $newest = $candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    return @($newest.Path)
}

function Get-OrderedLogFiles {
    param([string]$Directory)

    # 滚动档从旧到新，活文件在最后 —— 这样合并出来就是时间顺序。
    # 用正则自己判，不用 -Filter 'app.log.*'：Win32 通配会连短文件名一起匹配，
    # 同一个文件能被列两次，日志平白多一倍。
    $files = @(Get-ChildItem -LiteralPath $Directory -File -ErrorAction SilentlyContinue)
    $bases = @()
    if ($File -eq 'app' -or $File -eq 'all') { $bases += 'app.log' }
    if ($File -eq 'error' -or $File -eq 'all') { $bases += 'error.log' }

    $ordered = @()
    foreach ($base in $bases) {
        $escaped = [regex]::Escape($base)
        $rolled = @()
        foreach ($item in $files) {
            if ($item.Name -match ('^' + $escaped + '\.(\d+)$')) {
                $rolled += [pscustomobject]@{ Name = $item.Name; Index = [int]$Matches[1] }
            }
        }
        foreach ($roll in ($rolled | Sort-Object Index -Descending)) { $ordered += $roll.Name }
        $ordered += $base
    }

    $paths = @()
    foreach ($name in $ordered) {
        $full = Join-Path $Directory $name
        if (Test-Path -LiteralPath $full) { $paths += $full }
    }

    # 旧版本 AotDebugLogger 留下的 debug.log 不参与条目解析：
    # 它的时间头是 [HH:mm:ss.fff]，套不进现在的按条目过滤 / 统计逻辑，
    # 混进来只会让「解析不到条目」看起来像日志丢了。
    # 它仍然出现在 -List / -Export 里，需要看内容就直接开文件（或用 -PathOverride 指到别处）。
    return $paths
}

function ConvertTo-LogEntry {
    param([string[]]$Lines, [string]$Source)

    $entries = @()
    $current = $null

    foreach ($line in $Lines) {
        $match = [regex]::Match($line, $script:HeaderRegex)
        if ($match.Success) {
            if ($null -ne $current) { $entries += $current }
            $current = [pscustomobject]@{
                Timestamp = [datetime]::ParseExact($match.Groups[1].Value, 'yyyy-MM-dd HH:mm:ss.fff', $null)
                Level     = $match.Groups[2].Value
                Module    = $match.Groups[3].Value
                Head      = $match.Groups[4].Value
                Lines     = New-Object System.Collections.Generic.List[string]
                Source    = (Split-Path $Source -Leaf)
            }
            $current.Lines.Add($line)
        }
        elseif ($null -ne $current) {
            $current.Lines.Add($line)
        }
        # 头部之前的散行（多半是进程被截断的半条）直接忽略，不编造成数据
    }

    if ($null -ne $current) { $entries += $current }

    # 去掉尾部空行，显示更紧凑；正文本身还是原样，别篡改现场
    foreach ($entry in $entries) {
        while ($entry.Lines.Count -gt 1 -and [string]::IsNullOrWhiteSpace($entry.Lines[$entry.Lines.Count - 1])) {
            $entry.Lines.RemoveAt($entry.Lines.Count - 1)
        }
    }
    return $entries
}

function Read-LogEntries {
    param([string]$Directory)

    $all = @()
    foreach ($path in (Get-OrderedLogFiles -Directory $Directory)) {
        $reader = Open-SharedTextReader -Path $path
        try {
            # 整块读进来再切行：$lines += ReadLine() 是 O(n^2)，
            # 单个文件上限 5MB、十几万行时会慢到没法用
            $text = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        if ([string]::IsNullOrWhiteSpace($text)) { continue }
        $lines = @($text -split "`r?`n")
        $all += @(ConvertTo-LogEntry -Lines $lines -Source $path)
    }
    return $all
}

function Test-EntryMatches {
    param($Entry)

    if ($Level -and $Entry.Level -ne $Level) { return $false }

    if ($Module) {
        if ($Entry.Module -notlike "*$Module*") { return $false }
    }

    if ($Since) {
        if ($Entry.Timestamp -lt (ConvertTo-SinceTime $Since)) { return $false }
    }

    if ($Pattern) {
        if ($Entry.Lines -join "`n" -notmatch $Pattern) { return $false }
    }
    return $true
}

function ConvertTo-SinceTime {
    param([string]$Text)

    $trimmed = $Text.Trim().ToLower()
    if ($trimmed -match '^(\d+)([mhd])$') {
        $amount = [int]$Matches[1]
        switch ($Matches[2]) {
            'm' { return (Get-Date).AddMinutes(-$amount) }
            'h' { return (Get-Date).AddHours(-$amount) }
            'd' { return (Get-Date).AddDays(-$amount) }
        }
    }
    $parsed = [datetime]::MinValue
    if ([datetime]::TryParse($Text, [ref]$parsed)) { return $parsed }
    throw "-Since 看不懂：$Text（支持 30m / 2h / 1d / 2026-10-16 09:30）"
}

function Write-Entry {
    param($Entry)

    $color = 'Gray'
    switch ($Entry.Level) {
        'Error' { $color = 'Red' }
        'Warning' { $color = 'Yellow' }
        'Info' { $color = 'White' }
        'Debug' { $color = 'DarkGray' }
    }

    foreach ($line in $Entry.Lines) {
        if ($line -eq $Entry.Lines[0]) {
            Write-Host $line -ForegroundColor $color
        }
        else {
            Write-Host $line
        }
    }
    Write-Host ''
}

function Output-LogEntry {
    param($Entry)

    # 让 -Json 和 -Follow 能一起用：跟随模式下也能吐 JSON 给别的工具消费
    if ($Json) {
        [pscustomobject]@{
            time    = $Entry.Timestamp.ToString('yyyy-MM-dd HH:mm:ss.fff')
            level   = $Entry.Level
            module  = $Entry.Module
            file    = $Entry.Source
            message = ($Entry.Lines -join "`n")
        } | ConvertTo-Json -Depth 3
        return
    }

    Write-Entry -Entry $Entry
}

function Write-CurrentLevel {
    param([string]$Directory)

    $levelPath = Join-Path $Directory 'level.txt'
    $level = 'Info'
    if (Test-Path -LiteralPath $levelPath) {
        $reader = Open-SharedTextReader -Path $levelPath
        try { $text = $reader.ReadToEnd().Trim() } finally { $reader.Dispose() }
        if ($text) { $level = $text }
    }
    Write-Host ("当前落盘级别: {0}   ({1})" -f $level, $levelPath) -ForegroundColor Cyan

    # Debug 档现在默认走内存缓冲（LogService 里的 buffer.txt），不写出来会让人以为
    # 「开了 Debug 却看不到 Debug 行」是应用坏了 —— 其实是它在等一条 Error 才吐盘。
    $bufferPath = Join-Path $Directory 'buffer.txt'
    $buffer = 'on'
    if (Test-Path -LiteralPath $bufferPath) {
        $reader = Open-SharedTextReader -Path $bufferPath
        try { $bufferText = $reader.ReadToEnd().Trim() } finally { $reader.Dispose() }
        if ($bufferText) { $buffer = $bufferText }
    }
    Write-Host ("Debug 缓冲: {0}   ({1})" -f $buffer, $bufferPath) -ForegroundColor Cyan

    if ($buffer -eq 'off') {
        Write-Host 'Debug 正在逐条落盘（实时 tail 状态）；排查完 -SetBuffer on 关回缓冲。' -ForegroundColor DarkGray
    }
    else {
        Write-Host 'Debug 先进内存缓冲，只有出现 Error 时才整段吐到 app.log。' -ForegroundColor DarkGray
        Write-Host '要实时逐条看 Debug：读取日志.ps1 -SetBuffer off；只要细节别刷屏：-SetLevel Debug 并保持缓冲开着。' -ForegroundColor DarkGray
    }
}

# ── 构建日志分支（和运行时日志是两回事，单独走）──────────────────────
if ($BuildLog) {
    $repoRoot = Split-Path $PSScriptRoot -Parent
    $buildLogPath = Join-Path $repoRoot 'DockedTools\debug_log.txt'
    if (-not (Test-Path -LiteralPath $buildLogPath)) {
        $buildLogPath = Join-Path $repoRoot 'debug_log.txt'
    }
    if (-not (Test-Path -LiteralPath $buildLogPath)) {
        throw "没找到构建日志，先确认 winapp run 有没有输出到 DockedTools\debug_log.txt"
    }
    Get-Content -LiteralPath $buildLogPath -Tail $Tail -Encoding UTF8
    return
}

# ── 定位日志目录 ─────────────────────────────────────────────────────
# 必须 @() 包一层：函数只返回一个目录时 PowerShell 会把数组摊平成字符串，
# 于是 $directories[0] 会变成「路径的第一个字符 C」，来源显示就成笑了。
$directories = @(Resolve-LogDirectory)
$firstDirectory = $directories[0]

if ($SetLevel) {
    $levelPath = Join-Path $firstDirectory 'level.txt'
    # UTF8 无 BOM：应用侧会 trim BOM，但保持干净省得别处踩坑
    [System.IO.File]::WriteAllText($levelPath, $SetLevel, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ("已写入 {0} -> {1}" -f $levelPath, $SetLevel) -ForegroundColor Green
    Write-Host '写线程最多 5 秒后生效，不用重启应用；排查完记得 -SetLevel Info 关回去。' -ForegroundColor DarkGray
    return
}

if ($SetBuffer) {
    $bufferPath = Join-Path $firstDirectory 'buffer.txt'
    # UTF8 无 BOM：和 level.txt 同一条纪律（应用侧虽然会 trim BOM，但别处不一定）
    [System.IO.File]::WriteAllText($bufferPath, $SetBuffer, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ("已写入 {0} -> {1}" -f $bufferPath, $SetBuffer) -ForegroundColor Green

    if ($SetBuffer -eq 'off') {
        Write-Host 'Debug 改为逐条落盘，可以 -Follow 实时看了；排查完记得 -SetBuffer on 关回缓冲。' -ForegroundColor DarkGray
        Write-Host '否则 app.log 会被设置读取的流水账刷满 —— 本机实测那种噪音占过整份日志的 98%。' -ForegroundColor DarkGray
    }
    else {
        Write-Host 'Debug 回到内存缓冲：平时不落盘，出现 Error 时把出错前那段现场整段吐进 app.log。' -ForegroundColor DarkGray
    }
    Write-Host '写线程最多 5 秒后生效，不用重启应用。' -ForegroundColor DarkGray
    return
}

if ($List -or $ShowLevel) {
    foreach ($directory in $directories) {
        Write-Host "日志目录: $directory" -ForegroundColor Cyan
        Get-ChildItem -LiteralPath $directory -File |
            Sort-Object LastWriteTime -Descending |
            Select-Object Name,
            @{ Name = 'Size'; Expression = { '{0:N1} KB' -f ($_.Length / 1KB) } },
            LastWriteTime |
            Format-Table -AutoSize
        Write-CurrentLevel -Directory $directory
    }
    if ($List -or $ShowLevel) { return }
}

if ($Export) {
    $exportPath = if ([System.IO.Path]::IsPathRooted($Export)) {
        $Export
    }
    else {
        Join-Path (Get-Location).Path $Export
    }
    Compress-Archive -Path (Join-Path $firstDirectory '*') -DestinationPath $exportPath -Force
    Write-Host "已导出: $exportPath" -ForegroundColor Green
    return
}

# ── 读取 + 过滤 ──────────────────────────────────────────────────────
$entries = @()
foreach ($directory in $directories) {
    $entries += Read-LogEntries -Directory $directory
}

if ($entries.Count -eq 0) {
    Write-Host '日志目录里没有可解析的条目（应用还没写过日志，或文件是旧的）。' -ForegroundColor DarkYellow
    Write-CurrentLevel -Directory $firstDirectory
    return
}

$filtered = @($entries | Where-Object { Test-EntryMatches -Entry $_ })

if ($Stats) {
    if ($filtered.Count -eq 0) { Write-Host '没有匹配的日志。' -ForegroundColor DarkYellow; return }
    $filtered |
        Group-Object Level, Module |
        Sort-Object Count -Descending |
        Select-Object Count, Name |
        Format-Table -AutoSize
    return
}

if ($Follow) {
    if ($filtered.Count -gt 0) {
        foreach ($entry in @($filtered | Select-Object -Last $Tail)) { Output-LogEntry -Entry $entry }
    }

    # 同时跟 app.log 和 error.log：Error 现在单独进 error.log，
    # 只跟 app.log 会变成「崩溃前最要紧的那几行看不见」
    $watchNames = @('app.log', 'error.log')
    Write-Host ("—— 跟随中（{0}），Ctrl+C 退出 —— 来源: {1}" -f ($watchNames -join ' / '), $firstDirectory) -ForegroundColor Cyan

    $watchers = @{}
    try {
        foreach ($name in $watchNames) {
            $path = Join-Path $firstDirectory $name
            if (-not (Test-Path -LiteralPath $path)) { continue }
            $reader = Open-SharedTextReader -Path $path
            # 从文件末尾开始跟，不然一上来把历史全刷一遍
            $fs = $reader.BaseStream
            $fs.Position = $fs.Length
            $reader.DiscardBufferedData()
            $watchers[$name] = [pscustomobject]@{
                Reader     = $reader
                Pending    = New-Object System.Collections.Generic.List[string]
                Path       = $path
                LastLength = $fs.Length
            }
        }

        if ($watchers.Count -eq 0) {
            Write-Host "还没人能跟：$firstDirectory 里既没有 app.log 也没有 error.log。" -ForegroundColor DarkYellow
            return
        }

        while ($true) {
            $advanced = $false

            foreach ($name in @($watchers.Keys)) {
                $watcher = $watchers[$name]

                # 文件变短 = 应用把它滚成 .1 了，同名文件是新建的空文件 ⇒ 关掉重开。
                # 用「上次读到的长度」判，不用 BaseStream.Position：
                # StreamReader 有内部缓冲，句柄位置会滞后于真正读到的行。
                if (Test-Path -LiteralPath $watcher.Path) {
                    $length = (Get-Item -LiteralPath $watcher.Path).Length
                    if ($length -lt $watcher.LastLength) {
                        $watcher.Reader.Dispose()
                        $watcher.Reader = Open-SharedTextReader -Path $watcher.Path
                        $watcher.LastLength = 0
                        Write-Host "（$name 已滚动，继续跟新文件）" -ForegroundColor DarkGray
                    }
                }

                while (-not $watcher.Reader.EndOfStream) {
                    $advanced = $true
                    $line = $watcher.Reader.ReadLine()
                    if ($null -eq $line) { break }

                    $isHeader = [regex]::IsMatch($line, $script:HeaderRegex)
                    if ($isHeader -and $watcher.Pending.Count -gt 0) {
                        $entry = @(ConvertTo-LogEntry -Lines ($watcher.Pending.ToArray()) -Source $name)
                        $watcher.Pending.Clear()
                        if ($entry.Count -gt 0 -and (Test-EntryMatches -Entry $entry[0])) { Output-LogEntry -Entry $entry[0] }
                    }

                    $watcher.Pending.Add($line)

                    # 位置行永远是 FormatMessage 的最后一行。
                    # 靠它收尾，否则「跟随」会一直慢一条（要等下一条日志进来才显示上一条）。
                    if ($line.StartsWith($script:LocationPrefix)) {
                        $entry = @(ConvertTo-LogEntry -Lines ($watcher.Pending.ToArray()) -Source $name)
                        $watcher.Pending.Clear()
                        if ($entry.Count -gt 0 -and (Test-EntryMatches -Entry $entry[0])) { Output-LogEntry -Entry $entry[0] }
                    }

                    $watcher.LastLength = $watcher.Reader.BaseStream.Position
                }
            }

            if (-not $advanced) { Start-Sleep -Milliseconds 500 }
        }
    }
    finally {
        foreach ($watcher in $watchers.Values) {
            if ($watcher.Reader) { $watcher.Reader.Dispose() }
        }
    }
    return
}

if ($filtered.Count -eq 0) {
    Write-Host '没有匹配的日志。检查一下过滤条件，或者 -SetLevel Debug 再复现一次。' -ForegroundColor DarkYellow
    Write-CurrentLevel -Directory $firstDirectory
    return
}

$selected = @($filtered | Select-Object -Last $Tail)

if ($Json) {
    $payload = $selected | ForEach-Object {
        [pscustomobject]@{
            time    = $_.Timestamp.ToString('yyyy-MM-dd HH:mm:ss.fff')
            level   = $_.Level
            module  = $_.Module
            file    = $_.Source
            message = ($_.Lines -join "`n")
        }
    }
    $payload | ConvertTo-Json -Depth 3 -Compress:$false
    return
}

Write-Host ("来源: {0}   匹配 {1} 条，显示最后 {2} 条" -f $firstDirectory, $filtered.Count, $selected.Count) -ForegroundColor DarkCyan
foreach ($entry in $selected) { Write-Entry -Entry $entry }
