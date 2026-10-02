<#
.SYNOPSIS
    DockedTools 的 git worktree 初始化 / 包身份体检脚本。

.DESCRIPTION
    为什么需要它：
        MSIX 包的 Identity Name 决定「能不能并行安装」，运行时的单实例标识（Mutex /
        事件 / 命名管道）决定「能不能同时运行」。两者必须同源，缺一个就会出现
        「装得上但跑不起来」——第二个实例会去激活第一个然后自己退出。

        本仓库的做法是从 worktree 目录名（如 main-ffb0958c）取 hash 前 4 位派生后缀
        .WTFFB0，由 DockedTools.csproj 在构建时同时写进：
            1. MSIX Identity Name
            2. 功能\应用入口\WorktreeIdentity.g.cs（单实例标识读它）
        这套逻辑是构建期自动的，但**前提是目录名里带 hash**。目录名不带 hash
        （比如手动 git worktree add ../my-fix）时后缀为空，所有 worktree 又撞回一起。

    我们的 worktree 一律由 WorkBuddy 生成，目录名形如 main-ffb0958c，自带 8 位 hex，
    所以后缀派生总是成立的 —— 日常用不到 -New，进新 worktree 跑一次 -Status 确认
    身份、再 -Build -Run 就行。

    后缀只取 hash 前 4 位（16^4 = 65536 种），理论上会撞车；worktree 删掉后包也可能
    残留在系统里变成指向不存在目录的孤儿包。这两类情况 -Status 都会明确报出来。

    常用姿势：
        .\脚本工具\worktree.ps1 -Status          # 体检当前 worktree（默认，只读）
        .\脚本工具\worktree.ps1 -Build -Run      # 构建并启动
        .\脚本工具\worktree.ps1 -ListPackages    # 列已装的包，标出归属/孤儿包
        .\脚本工具\worktree.ps1 -New feat/xxx    # 手动建 worktree 时才用（目录名自动带 hash）
        .\脚本工具\worktree.ps1 -ListPackages    # 列出机器上已装的 DockedTools 包
        .\脚本工具\worktree.ps1 -Unregister <PackageFullName>   # 注销指定包

.NOTES
    只在 Debug 下生效：Release 的 WorktreeSuffix 恒为空串，行为与未加隔离时一致。
#>

[CmdletBinding()]
param(
    # 新建 worktree：给定新分支名。目录名会自动形如 main-<8位hex>，保证身份隔离生效
    [string]$New,

    # worktree 放在哪个目录下（默认与主仓库同级）
    [string]$Root,

    # 显式身份后缀，优先于目录名派生。传 "WTABCD" 或 ".WTABCD" 都行。
    # 用于目录名不含 hash 的既有 worktree，构建时会以 -p:WorktreeSuffix= 传给 MSBuild，
    # 包身份与单实例标识会一起跟着变，无需改代码。
    [string]$Suffix,

    # 构建 Debug
    [switch]$Build,

    # 用 winapp run 启动
    [switch]$Run,

    # 体检（默认行为，只读，不改动任何东西）
    [switch]$Status,

    # 只打印将要执行的命令，不真的跑（git / dotnet / winapp 都不动）
    [switch]$DryRun,

    # 列出机器上已装的 DockedTools 包
    [switch]$ListPackages,

    # 注销指定的包（传 PackageFullName，先用 -ListPackages 查）
    [string]$Unregister
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- 常量
$BaseIdentity = '8B8CC4F4.482486777ECD9'
$CsprojRel    = 'DockedTools\DockedTools.csproj'
$ManifestRel  = 'DockedTools\Package.Debug.appxmanifest'
$IdentityRel  = 'DockedTools\功能\应用入口\WorktreeIdentity.g.cs'
$AppFolderRel = 'DockedTools\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64'
$Ports        = 17829..17839   # 桥接服务盲扫区间，多实例时会互相挤

# ---------------------------------------------------------------- 小工具
function Write-Head([string]$t) { Write-Host ''; Write-Host ('=== {0} ===' -f $t) -ForegroundColor Cyan }
function Write-Ok([string]$t)   { Write-Host ('  OK   {0}' -f $t) -ForegroundColor Green }
function Write-Warn([string]$t) { Write-Host ('  WARN {0}' -f $t) -ForegroundColor Yellow }
function Write-Bad([string]$t)  { Write-Host ('  FAIL {0}' -f $t) -ForegroundColor Red }
function Write-Info([string]$t) { Write-Host ('       {0}' -f $t) -ForegroundColor DarkGray }

<# 从目录名派生后缀：main-ffb0958c -> .WTFFB0 #>
function Get-DirSuffix([string]$dirName) {
    $m = [regex]::Match($dirName, '-([0-9a-fA-F]{4,})')
    if ($m.Success) { return '.WT' + $m.Groups[1].Value.Substring(0, 4).ToUpperInvariant() }
    return ''
}

<# 统一成 .WTXXXX 形态；空输入返回空 #>
function Format-Suffix([string]$s) {
    if ([string]::IsNullOrWhiteSpace($s)) { return '' }
    $s = $s.Trim()
    if (-not $s.StartsWith('.')) { $s = '.' + $s }
    return $s.ToUpperInvariant()
}

<# 找到仓库根目录（当前目录往上找，直到含 .git 的那一层） #>
function Get-RepoRoot {
    $d = (Get-Location).Path
    while ($d) {
        if (Test-Path (Join-Path $d '.git')) { return $d }
        $parent = Split-Path $d -Parent
        if (-not $parent -or $parent -eq $d) { break }
        $d = $parent
    }
    return $null
}

<# 生成 8 位 hex，用于新 worktree 的目录名（只有 -New 手动建时才用得上） #>
function New-DirHash { return ([guid]::NewGuid().ToString('N')).Substring(0, 8) }

<#
  给已装的包打标注：它归哪个 worktree、是不是孤儿包（安装目录已被删掉）。
  worktree 删掉以后包仍留在系统里，指向一个不存在的目录——这种包跑不了也占地方。
#>
function Get-PkgAnnote($p, [string]$root) {
    $loc = $p.InstallLocation
    if ([string]::IsNullOrWhiteSpace($loc)) { return '(无安装目录)' }
    if (-not (Test-Path $loc)) { return '孤儿包：安装目录已不存在' }
    if ($root -and $loc.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) { return '本 worktree' }
    if ($loc -match '\\Worktrees\\DockedTools\\([^\\]+)') { return ('属于 worktree: ' + $Matches[1]) }
    return ''
}

# 中途结束一律用 return：脚本里直接 exit 会把调用者的终端一起关掉。
# 只有真正失败时才在脚本末尾 exit 1，给调用方一个非零退出码。
$script:Failed = $false

# ---------------------------------------------------------------- 定位仓库
$repoRoot = Get-RepoRoot
if (-not $repoRoot) {
    Write-Bad '没找到仓库根目录（当前目录及其上级都没有 .git）。请在 DockedTools 仓库内运行。'
    $script:Failed = $true
    return
}
$dirName = Split-Path $repoRoot -Leaf

# 后缀优先级：显式 -Suffix  >  目录名派生  >  环境变量 WORKTREE_SUFFIX
$dirSuffix  = Get-DirSuffix $dirName
$envSuffix  = Format-Suffix $env:WORKTREE_SUFFIX
$explicit   = Format-Suffix $Suffix
$effSuffix  = if ($explicit) { $explicit } elseif ($dirSuffix) { $dirSuffix } elseif ($envSuffix) { $envSuffix } else { '' }

Write-Host ''
Write-Host 'DockedTools · worktree 工具' -ForegroundColor Cyan
Write-Host ('  仓库目录 : {0}' -f $repoRoot)

# ---------------------------------------------------------------- -ListPackages
if ($ListPackages) {
    Write-Head '机器上已装的 DockedTools 包'
    $pkgs = @(Get-AppxPackage | Where-Object { $_.Name -like "$BaseIdentity*" })
    if ($pkgs.Count -eq 0) { Write-Info '（一个都没有）' }
    foreach ($p in $pkgs) {
        $note = Get-PkgAnnote $p $repoRoot
        Write-Host ('  {0}' -f $p.PackageFullName) -ForegroundColor White
        Write-Info ('Status={0}{1}' -f $p.Status, $(if ($note) { '  ·  ' + $note } else { '' }))
        Write-Info ('Location={0}' -f $p.InstallLocation)
        if ($note -like '孤儿包*') { Write-Warn '可以清理：-Unregister 上面那串 PackageFullName' }
    }
    Write-Info ('共 {0} 个。注销用：-Unregister <PackageFullName>' -f $pkgs.Count)
    # 只有「光列表」这一个意图时才到此为止；同时带 -Status 就继续往下体检
    if (-not ($Build -or $Run -or $New -or $Status -or $Unregister)) { return }
}

# ---------------------------------------------------------------- -Unregister
if ($Unregister) {
    Write-Head ('注销包: {0}' -f $Unregister)
    $p = Get-AppxPackage | Where-Object { $_.PackageFullName -eq $Unregister } | Select-Object -First 1
    if (-not $p) { Write-Bad '找不到这个包，用 -ListPackages 看准确名字'; $script:Failed = $true; return }
    Write-Warn '注销后该包的安装目录会被删除。若它正在运行请先退出。'
    $p | Remove-AppxPackage
    Write-Ok '已注销'
    if (-not ($Build -or $Run -or $New -or $Status)) { return }
}

# ---------------------------------------------------------------- -New
if ($New) {
    Write-Head ('新建 worktree: {0}' -f $New)

    $git = Get-Command git -ErrorAction SilentlyContinue
    if (-not $git) { Write-Bad '找不到 git'; $script:Failed = $true; return }

    if (-not $Root) {
        # 主仓库的上一级；若当前已在 worktree 里，就用它所在的那层目录
        $parent = Split-Path $repoRoot -Parent
        $Root = if ($parent) { $parent } else { $repoRoot }
    }

    $hash    = New-DirHash
    $wtName  = 'main-' + $hash
    $wtPath  = Join-Path $Root $wtName
    $wtSuffix = '.WT' + $hash.Substring(0, 4).ToUpperInvariant()

    if (Test-Path $wtPath) { Write-Bad ('目标目录已存在: {0}' -f $wtPath); $script:Failed = $true; return }

    Write-Info ('目录名   : {0}   （带 hash 才会触发身份隔离，别手改成别的名字）' -f $wtName)
    Write-Info ('包身份   : {0}.Debug{1}' -f $BaseIdentity, $wtSuffix)

    # 分支已存在就不带 -b（DryRun 下不查，纯预演不碰仓库）
    $branchExists = ''
    if (-not $DryRun) { $branchExists = (& git branch --list $New 2>$null) -join '' }
    $gitArgs = if ($branchExists.Trim()) { @('worktree', 'add', $wtPath, $New) }
               else { @('worktree', 'add', '-b', $New, $wtPath) }

    if ($DryRun) {
        Write-Info ('[DryRun] git {0}' -f ($gitArgs -join ' '))
        Write-Host ''
        Write-Host '  接下来（DryRun 未真正创建）：' -ForegroundColor Cyan
        Write-Host ('    cd "{0}"' -f $wtPath)
        Write-Host '    .\脚本工具\worktree.ps1 -Build -Run'
        return
    }

    & git @gitArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Bad ('git worktree add 失败（exit {0}）' -f $LASTEXITCODE)
        $script:Failed = $true
        return
    }

    Write-Ok ('已创建: {0}' -f $wtPath)
    Write-Host ''
    Write-Host '  接下来：' -ForegroundColor Cyan
    Write-Host ('    cd "{0}"' -f $wtPath)
    if ($explicit) {
        Write-Host ('    .\脚本工具\worktree.ps1 -Build -Run -Suffix {0}' -f $explicit)
    } else {
        Write-Host '    .\脚本工具\worktree.ps1 -Build -Run'
    }

    if (-not ($Build -or $Run)) { return }

    # 继续在当前目录构建/运行的话没意义，切进新 worktree
    Set-Location $wtPath
    $repoRoot  = $wtPath
    $dirName   = $wtName
    $effSuffix = $wtSuffix
}

# ---------------------------------------------------------------- 体检
Write-Head 'worktree 身份体检'

# 1) 隔离机制是否已接种（老 worktree 可能是从加机制之前的 commit 切出来的）
$csproj = Join-Path $repoRoot $CsprojRel
$seeded = $false
if (Test-Path $csproj) {
    $seeded = (Select-String -Path $csproj -SimpleMatch '_WriteWorktreeIdentity' -Quiet) -as [bool]
}
if ($seeded) { Write-Ok ('DockedTools.csproj 已含隔离逻辑（_WriteWorktreeIdentity）') }
else         { Write-Bad 'DockedTools.csproj 里没有隔离逻辑 —— 这个 worktree 是从旧 commit 切出来的，构建不会隔离身份' }

# 2) 目录名
if ($dirSuffix) { Write-Ok ('目录名可派生后缀 : {0}  ->  {1}' -f $dirName, $dirSuffix) }
else {
    Write-Warn ('目录名不含 hash 段: "{0}"  ->  派生的后缀为空，多 worktree 会互相顶掉' -f $dirName)
    Write-Info '两条路（选一）：'
    Write-Info ('  a) 重命名目录让它带 hash：git worktree move "{0}" "{1}\main-{2}"' -f $repoRoot, (Split-Path $repoRoot -Parent), (New-DirHash))
    Write-Info '  b) 每次构建/运行显式带 -Suffix WTXXXX（本脚本支持，会传给 MSBuild）'
}

if ($explicit) { Write-Info ('显式后缀（本次生效）: {0}' -f $explicit) }

# 3) 派生的完整身份
$identity = $BaseIdentity + '.Debug' + $effSuffix
Write-Host ''
Write-Host ('  Identity Name   : {0}' -f $identity) -ForegroundColor White
Write-Host ('  DisplayName     : 边栏助手 (Debug{0})' -f $effSuffix) -ForegroundColor White
Write-Host ('  单实例 Mutex    : Local\DockedAI_SingleInstance_Mutex_DEBUG{0}' -f $effSuffix) -ForegroundColor White
Write-Host ('  单实例管道      : DockedAI_SingleInstance_Pipe{0}' -f $effSuffix) -ForegroundColor White

# 4) 身份合法性（MSIX 硬约束：3~50 字符、只允许 [A-Za-z0-9.-]、不以点结尾）
$lenOk  = $identity.Length -ge 3 -and $identity.Length -le 50
$charOk = $identity -match '^[A-Za-z0-9.-]+$'
$dotOk  = -not $identity.EndsWith('.')
if ($lenOk)  { Write-Ok ('身份长度 {0}（限制 3~50）' -f $identity.Length) }  else { Write-Bad ('身份长度 {0} 越界' -f $identity.Length) }
if ($charOk) { Write-Ok '字符集合法 [A-Za-z0-9.-]' }                          else { Write-Bad '字符集非法' }
if ($dotOk)  { Write-Ok '不以点结尾' }                                        else { Write-Bad '以点结尾，MSIX 拒绝' }

# 5) Publisher 不能动（必须与签名证书 Subject 一致）
$manifestPath = Join-Path $repoRoot $ManifestRel
if (Test-Path $manifestPath) {
    [xml]$x = Get-Content $manifestPath -Raw
    $ns = New-Object System.Xml.XmlNamespaceManager($x.NameTable)
    $ns.AddNamespace('p', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $pubNode = $x.SelectSingleNode('/p:Package/p:Identity/@Publisher', $ns)
    $oldName = $x.SelectSingleNode('/p:Package/p:Identity/@Name', $ns)
    Write-Ok ('Publisher 保持原样: {0}' -f $pubNode.Value)
    Write-Info ('manifest 里写死的 Name = {0}（构建时会被 XmlPoke 改写成 {1}）' -f $oldName.Value, $identity)
} else {
    Write-Warn ('找不到 {0}' -f $ManifestRel)
}

# 6) WorktreeIdentity.g.cs 当前内容（上次构建留下的）
$g = Join-Path $repoRoot $IdentityRel
if (Test-Path $g) {
    $m = [regex]::Match((Get-Content $g -Raw), 'Suffix\s*=\s*"([^"]*)"')
    if ($m.Success) {
        $cur = $m.Groups[1].Value
        if ($cur -eq $effSuffix) { Write-Ok ('身份文件已是最新: Suffix = "{0}"' -f $cur) }
        else { Write-Warn ('身份文件还是旧的: Suffix = "{0}"，构建后会变成 "{1}"' -f $cur, $effSuffix) }
    } else { Write-Warn '身份文件格式异常，读不到 Suffix' }
}

# 7) 本身份是否已安装 —— 且必须是「本 worktree 装的」才算数。
#    目录名由 WorkBuddy 生成，总是 main-<8位hex>，只取前 4 位做后缀，理论上存在撞车可能：
#    若已有一个同名包指向别的目录，说明另一个 worktree 抢到了同一个后缀。
$installed = @(Get-AppxPackage | Where-Object { $_.Name -eq $identity })
if ($installed.Count -gt 0) {
    $loc = $installed[0].InstallLocation
    $mine = $loc -and $repoRoot -and $loc.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)
    if ($mine) {
        Write-Ok ('本身份已安装，且归属当前 worktree: {0}' -f $installed[0].PackageFullName)
    } elseif (-not (Test-Path $loc)) {
        Write-Warn ('本身份已注册但目录已不存在（孤儿包）: {0}' -f $loc)
        Write-Info '先 -Unregister 再构建，否则可能注册不上'
    } else {
        Write-Bad ('后缀撞车：{0} 已被另一个目录占用' -f $identity)
        Write-Info ('它指向: {0}' -f $loc)
        Write-Info '换一个后缀：.\脚本工具\worktree.ps1 -Build -Run -Suffix WTXXXX'
    }
    Write-Info ('AUMID: {0}!App' -f $installed[0].PackageFamilyName)
} else {
    Write-Info ('本身份尚未安装，构建并运行后会自动注册')
}

# 7b) 本 worktree 目录下是否残留别的身份（之前用别的后缀装过，worktree 复用时会撞）
$stale = @(Get-AppxPackage | Where-Object {
        $_.Name -like "$BaseIdentity*" -and $_.Name -ne $identity -and $_.InstallLocation -and
        $_.InstallLocation.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)
    })
if ($stale.Count -gt 0) {
    Write-Warn ('当前 worktree 目录下还残留 {0} 个旧身份的包：' -f $stale.Count)
    foreach ($p in $stale) { Write-Info ('{0}' -f $p.PackageFullName) }
    Write-Info '它们不会被顶掉，但会一直占着系统里的包名，建议 -Unregister 掉'
}

# 8) 端口占用（桥接服务盲扫区间，多实例会抢）
$busy = @()
foreach ($p in $Ports) {
    $c = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue
    if ($c) { $busy += $p }
}
if ($busy.Count -gt 0) {
    Write-Warn ('桥接端口被占: {0}（多半是另一个实例在跑，扩展会连到它）' -f ($busy -join ', '))
} else {
    Write-Ok ('桥接端口 {0}~{1} 全空' -f $Ports[0], $Ports[-1])
}

if (-not ($Build -or $Run)) { return }

# ---------------------------------------------------------------- -Build
if ($Build) {
    Write-Head '构建 Debug'

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { Write-Bad '找不到 dotnet'; $script:Failed = $true; return }

    # 显式后缀走命令行传给 MSBuild，global property 优先级高于 csproj 内的赋值，
    # 包身份与 WorktreeIdentity.g.cs 会一起跟着变
    $env:WORKTREE_SUFFIX = $effSuffix
    $buildArgs = @('build', (Join-Path $repoRoot $CsprojRel), '-c', 'Debug', '-p:Platform=x64')
    if ($effSuffix) { $buildArgs += ('-p:WorktreeSuffix={0}' -f $effSuffix) }

    if ($DryRun) {
        Write-Info ('[DryRun] $env:WORKTREE_SUFFIX = "{0}"' -f $effSuffix)
        Write-Info ('[DryRun] dotnet {0}' -f ($buildArgs -join ' '))
    } else {
        Push-Location $repoRoot
        try {
            & dotnet @buildArgs
            if ($LASTEXITCODE -ne 0) {
                Write-Bad ('构建失败（exit {0}）' -f $LASTEXITCODE)
                $script:Failed = $true
                return
            }
        } finally { Pop-Location }
    }

    if ($DryRun) {
        Write-Info '[DryRun] 未真正构建'
    } else {
        Write-Ok '构建完成'
    }
    if ($effSuffix) { Write-Ok ('包身份已隔离为 {0}' -f $identity) }
    else            { Write-Warn '后缀为空 —— 这份构建会顶掉其它 worktree 的 .Debug 包' }
}

# ---------------------------------------------------------------- -Run
if ($Run) {
    Write-Head '启动'

    $winapp = Get-Command winapp -ErrorAction SilentlyContinue
    if (-not $winapp) { Write-Bad '找不到 winapp CLI（Windows App SDK CLI）'; $script:Failed = $true; return }

    $appFolder = Join-Path $repoRoot $AppFolderRel
    if (-not (Test-Path $appFolder)) { Write-Bad ('产物目录不存在，先构建: {0}' -f $appFolder); $script:Failed = $true; return }

    Write-Info ('应用目录: {0}' -f $appFolder)
    Write-Info ('包身份  : {0}' -f $identity)
    Write-Info '退出后自动注销（--unregister-on-exit）'

    $runArgs = @('run', $appFolder, '--debug-output', '--unregister-on-exit')
    if ($DryRun) {
        Write-Info ('[DryRun] winapp {0}' -f ($runArgs -join ' '))
        return
    }

    Push-Location $repoRoot
    try { & winapp @runArgs }
    finally { Pop-Location }
}

# 只有失败才在这里结束进程，给调用方非零退出码；成功时不 exit，免得关掉调用者的终端
if ($script:Failed) { exit 1 }
