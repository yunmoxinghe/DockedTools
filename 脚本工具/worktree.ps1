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
$Ports        = 17829..17839   # 桥接服务盲扫区间，多实例时会互相挤
# 产物目录一律不写死 —— 见下方 Get-AppOutputFolder：TFM 会跟着 csproj 升级，写死的路径必然过期

# ---------------------------------------------------------------- 小工具
function Write-Head([string]$t) { Write-Host ''; Write-Host ('=== {0} ===' -f $t) -ForegroundColor Cyan }
function Write-Ok([string]$t)   { Write-Host ('  OK   {0}' -f $t) -ForegroundColor Green }
function Write-Warn([string]$t) { Write-Host ('  WARN {0}' -f $t) -ForegroundColor Yellow }
function Write-Bad([string]$t)  {
    Write-Host ('  FAIL {0}' -f $t) -ForegroundColor Red
    # 标了 FAIL 就得让调用方看出来：体检判出「后缀撞车」「csproj 没接种隔离逻辑」这类问题，
    # 光在屏幕上标红、退出码还是 0，上层（包括哥哥自己写批处理时）没法据此停下来。
    $script:BadReported = $true
}
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

<#
  构建产物目录：从 csproj 的 <TargetFramework> 现推，不写死。

  【为什么之前必须改】写死的是 net10.0-windows10.0.22621.0，而 TFM 在 60ec6f6（2026-10-03）
  升到了 net10.0-windows10.0.26100.0，本脚本最后一次改动是前一天（d999871，10-02）没跟上。
  新旧两个目录同时存在（旧目录里还躺着 10-03 的 DockedTools.exe），Test-Path 校验照样过，
  于是 -Build -Run 变成「构建进 26100、启动 22621 里的旧程序」，一声不响跑错版本 ——
  验证改动时踩这个坑，看到的全是上一个版本的行为。
#>
function Get-AppOutputFolder([string]$root) {
    $csprojPath = Join-Path $root $CsprojRel
    if (-not (Test-Path $csprojPath)) { return $null }

    $m = [regex]::Match((Get-Content $csprojPath -Raw), '<TargetFramework>([^<]+)</TargetFramework>')
    if (-not $m.Success) { return $null }

    $tfm = $m.Groups[1].Value.Trim()
    return @{ Tfm = $tfm; Folder = (Join-Path $root ('DockedTools\bin\x64\Debug\' + $tfm + '\win-x64')) }
}

# 正常收工用 return（脚本末尾不 exit，免得给成功运行也带上退出码）。
# 失败走 Fail-AndExit：早先每个失败分支都是 return，永远走不到末尾那句 exit 1，
# 实测 -Unregister 一个不存在的包 —— 打了 FAIL，$LASTEXITCODE 仍是 0，上层没法判失败。
# 【exit 会不会关掉调用者的终端】实测（PS 5.1.26100）：用 & 调用 .ps1，脚本里的 exit
# 只结束脚本自己的作用域（调用方的后续语句照常执行），同时把 $LASTEXITCODE 置成指定值。
$script:Failed = $false
$script:BadReported = $false

function Fail-AndExit([string]$msg) {
    Write-Bad $msg
    $script:Failed = $true
    exit 1
}

# 正常收尾前都要过一道：动作失败（Failed）或体检判出 FAIL（BadReported）都给非零退出码，
# 干净就 fall through 到调用方的 return。写在函数里是为了不让每个出口各抄一遍判断。
function Complete-Script {
    if ($script:Failed -or $script:BadReported) { exit 1 }
}

# ---------------------------------------------------------------- 定位仓库
$repoRoot = Get-RepoRoot
if (-not $repoRoot) {
    Fail-AndExit '没找到仓库根目录（当前目录及其上级都没有 .git）。请在 DockedTools 仓库内运行。'
}
$dirName = Split-Path $repoRoot -Leaf

# 后缀优先级：显式 -Suffix  >  环境变量 WORKTREE_SUFFIX  >  目录名派生
# 【为什么把 env 放在目录名前面】DockedTools.csproj 第 179~180 行就是这个顺序，
# 体检脚本必须和被检查的对象同源，否则「设了 WORKTREE_SUFFIX 又在带 hash 的目录里跑 -Status」
# 会报出另一个后缀，体检结论直接误导（真构建用的是 env 那个）。
$dirSuffix  = Get-DirSuffix $dirName
$envSuffix  = Format-Suffix $env:WORKTREE_SUFFIX
$explicit   = Format-Suffix $Suffix
$effSuffix  = if ($explicit) { $explicit } elseif ($envSuffix) { $envSuffix } elseif ($dirSuffix) { $dirSuffix } else { '' }

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
    if (-not ($Build -or $Run -or $New -or $Status -or $Unregister)) { Complete-Script; return }
}

# ---------------------------------------------------------------- -Unregister
if ($Unregister) {
    Write-Head ('注销包: {0}' -f $Unregister)
    $p = Get-AppxPackage | Where-Object { $_.PackageFullName -eq $Unregister } | Select-Object -First 1
    if (-not $p) { Fail-AndExit '找不到这个包，用 -ListPackages 看准确名字' }
    Write-Warn '注销后该包的安装目录会被删除。若它正在运行请先退出。'
    $p | Remove-AppxPackage
    Write-Ok '已注销'
    if (-not ($Build -or $Run -or $New -or $Status)) { Complete-Script; return }
}

# ---------------------------------------------------------------- -New
if ($New) {
    Write-Head ('新建 worktree: {0}' -f $New)

    $git = Get-Command git -ErrorAction SilentlyContinue
    if (-not $git) { Fail-AndExit '找不到 git' }

    if (-not $Root) {
        # 主仓库的上一级；若当前已在 worktree 里，就用它所在的那层目录
        $parent = Split-Path $repoRoot -Parent
        $Root = if ($parent) { $parent } else { $repoRoot }
    }

    $hash    = New-DirHash
    $wtName  = 'main-' + $hash
    $wtPath  = Join-Path $Root $wtName
    $wtSuffix = '.WT' + $hash.Substring(0, 4).ToUpperInvariant()

    if (Test-Path $wtPath) { Fail-AndExit ('目标目录已存在: {0}' -f $wtPath) }

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
        Complete-Script; return
    }

    & git @gitArgs
    if ($LASTEXITCODE -ne 0) {
        Fail-AndExit ('git worktree add 失败（exit {0}）' -f $LASTEXITCODE)
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

    if (-not ($Build -or $Run)) { Complete-Script; return }

    # 继续在当前目录构建/运行的话没意义，切进新 worktree
    Set-Location $wtPath
    $repoRoot  = $wtPath
    $dirName   = $wtName
    # 跟着换的必须是派生出来的后缀，不能只换 dirName ——
    # 之前 $dirSuffix 还是旧目录算出来的空串，体检会对一个明明带 hash 的新目录
    # 报「目录名不含 hash 段」，属于脚本自己造的假告警。
    $dirSuffix = Get-DirSuffix $wtName
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
    # Staged / 未完全安装的包 InstallLocation 是空串，而 Test-Path '' 在
    # $ErrorActionPreference='Stop' 下会直接抛 ParameterBindingValidationException（实测），
    # 体检就会在最后一步崩掉 —— 先判空再碰路径。Get-PkgAnnote 里本来就有这层保护，这里漏了。
    $hasLoc = -not [string]::IsNullOrWhiteSpace($loc)
    $mine = $hasLoc -and $repoRoot -and $loc.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)
    if ($mine) {
        Write-Ok ('本身份已安装，且归属当前 worktree: {0}' -f $installed[0].PackageFullName)
    } elseif (-not $hasLoc) {
        Write-Warn '本身份已注册但没有安装目录（Staged / 残缺状态），建议 -Unregister 掉再构建'
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
# 【为什么要先查命令存在性】-ErrorAction SilentlyContinue 只对 cmdlet 自己写的错误有效，
# 「命令根本不存在」抛的是 CommandNotFoundException，它照样会把 Stop 模式的脚本打死（实测）。
$busy = @()
if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue) {
    foreach ($p in $Ports) {
        # 同一端口 IPv4 + IPv6 各一条监听，不去重的话告警里会把同一个端口印两遍
        $c = @(Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue)
        if ($c.Count -gt 0 -and $busy -notcontains $p) { $busy += $p }
    }
    if ($busy.Count -gt 0) {
        Write-Warn ('桥接端口被占: {0}（多半是另一个实例在跑，扩展会连到它）' -f ($busy -join ', '))
    } else {
        Write-Ok ('桥接端口 {0}~{1} 全空' -f $Ports[0], $Ports[-1])
    }
} else {
    Write-Warn '这台机器没有 Get-NetTCPConnection（NetTCPIP 模块缺失），端口检查跳过'
}
# 提醒：桥接收端目前是关着的（功能/浏览器扩展/桥接配置.cs 的 BridgeConfig.Enabled = false），
# 所以这里读到「被占」更可能是别的进程，而不是本应用的另一个实例。

# 体检判出 FAIL（后缀撞车、csproj 没接种隔离逻辑等）也要给非零码 ——
# 别继续「屏幕上一片红、退出码 0」，那和这次修 Fail-AndExit 的目的是同一件事。
if (-not ($Build -or $Run)) { Complete-Script; return }

# ---------------------------------------------------------------- -Build
if ($Build) {
    Write-Head '构建 Debug'

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { Fail-AndExit '找不到 dotnet' }

    # 后缀一律走命令行 global property 传给 MSBuild：优先级高于 csproj 内的任何赋值，
    # 包身份与 WorktreeIdentity.g.cs 会一起跟着变。
    # 【为什么不再写 $env:WORKTREE_SUFFIX】环境变量会留在调用方的 shell 里，而体检现在按
    # 「显式 > env > 目录名」取后缀（与 csproj 同源）—— 同一个窗口里 cd 到下一个 worktree
    # 接着构建，就会继承上一个 worktree 的后缀，两个包身份互相顶掉，正是本脚本要抓的事故。
    # 何况 -p:WorktreeSuffix= 已经把后缀传到位了，env 那条纯属多余。
    $buildArgs = @('build', (Join-Path $repoRoot $CsprojRel), '-c', 'Debug', '-p:Platform=x64')
    if ($effSuffix) { $buildArgs += ('-p:WorktreeSuffix={0}' -f $effSuffix) }

    if ($DryRun) {
        Write-Info ('[DryRun] dotnet {0}' -f ($buildArgs -join ' '))
    } else {
        Push-Location $repoRoot
        try {
            & dotnet @buildArgs
            if ($LASTEXITCODE -ne 0) {
                Fail-AndExit ('构建失败（exit {0}）' -f $LASTEXITCODE)
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
    if (-not $winapp) { Fail-AndExit '找不到 winapp CLI（Windows App SDK CLI）' }

    # 产物目录跟着 csproj 的 TargetFramework 现推；推不出来就明说，
    # 绝不回落到某个「看起来也在」的旧 TFM 目录 —— 那正是静默跑错版本的来源
    $layout = Get-AppOutputFolder $repoRoot
    if (-not $layout) { Fail-AndExit ('读不到 {0} 里的 <TargetFramework>，无法确定产物目录' -f $CsprojRel) }

    $appFolder = $layout.Folder
    Write-Info ('TFM     : {0}' -f $layout.Tfm)
    Write-Info ('应用目录: {0}' -f $appFolder)

    $runArgs = @('run', $appFolder, '--debug-output', '--unregister-on-exit')

    if ($DryRun) {
        # 预演必须能看见命令：DryRun 的承诺是「只打印将要执行的命令，不真的跑」，
        # 那就不该因为「还没构建」被存在性检查拦在半路 —— 这里只 WARN 一句再走。
        if (-not (Test-Path $appFolder)) { Write-Warn '该目录现在还不存在，真跑之前要先 -Build' }
        Write-Info ('[DryRun] winapp {0}' -f ($runArgs -join ' '))
        Complete-Script; return
    }

    if (-not (Test-Path $appFolder)) {
        # 把 Debug 下现有哪些 TFM 列出来：写死路径的年代，旧 TFM 目录的存在恰好把
        # 「跑错版本」完全掩盖掉，现在至少让人一眼看见差在哪
        # 【注意要往上两层】$appFolder 是 ..\<tfm>\win-x64，退一层只到 <tfm>，
        # 列出来的是 ref/cs/native，不是想要的「同级其它 TFM」
        $tfmRoot  = Split-Path (Split-Path $appFolder -Parent) -Parent
        $siblings = @(Get-ChildItem $tfmRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
        $hint = if ($siblings.Count -gt 0) { ($siblings -join ', ') } else { '（一个都还没有）' }
        Fail-AndExit ('产物目录不存在，先 -Build: {0}   Debug 下现有: {1}' -f $appFolder, $hint)
    }

    # 防呆：exe 比 csproj 还旧，基本等于「构建产物没跟上源码」，提醒一句再启动
    $csprojPath = Join-Path $repoRoot $CsprojRel
    $exePath    = Join-Path $appFolder 'DockedTools.exe'
    if ((Test-Path $exePath) -and (Test-Path $csprojPath)) {
        $exeTime  = (Get-Item $exePath).LastWriteTime
        $srcTime  = (Get-Item $csprojPath).LastWriteTime
        if ($exeTime -lt $srcTime) {
            Write-Warn ('产物 exe 是 {0:MM-dd HH:mm}，csproj 改于 {1:MM-dd HH:mm} —— exe 比源码旧，确认这是想要的构建' -f $exeTime, $srcTime)
        }
    }

    Write-Info ('包身份  : {0}' -f $identity)
    Write-Info '退出后自动注销（--unregister-on-exit）'

    Push-Location $repoRoot
    try { & winapp @runArgs }
    finally { Pop-Location }
}

# 兜底：中途的失败路径都由 Fail-AndExit / Complete-Script 收走了，这里保证
# 走到最后一行时，只要出现过 Failed 或体检 FAIL 仍给出非零退出码。
Complete-Script
