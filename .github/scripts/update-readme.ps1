<#
.SYNOPSIS
    扫描仓库各子目录下的 plugin.json 与根目录 .lwp 安装包，自动更新根目录 README 中的插件列表表格。

.DESCRIPTION
    规则说明：
      1. 扫描根目录下所有包含 plugin.json 的一级子目录（跳过以 . 或 _ 开头的目录）。
      2. 读取 plugin.json 中的 id、name、version、description，以及可选的 name_en。
      3. 「插件名（English）」优先读取 plugin.json 的 name_en；未设置时保留原表格中对应插件 ID 的英文名；
         若为全新插件，则回退使用大小写不同的目录名（如 MediaDeck）或由插件 ID 派生的 Title Case 名称。
      4. 检查子目录下的 logo.png（兼容常见图片扩展名）与 README.md（大小写不敏感，保留磁盘真实文件名以兼容 Linux）。
      5. 检查仓库根目录下是否存在 <id>.lwp，若存在则在最后一列生成 Badge 下载按钮（指向 GitHub Raw 直链），否则显示 -。
      6. 更新 README 中 <!-- PLUGINS:START --> 与 <!-- PLUGINS:END --> 之间的表格（若无标记则自动定位「## 插件列表」下的表格并补齐标记）。

.EXAMPLE
    pwsh -NoProfile -File .github/scripts/update-readme.ps1
    pwsh -NoProfile -File .github/scripts/update-readme.ps1 -CheckOnly
#>
param(
    [string]$Root = "",
    [string]$Repo = "",
    [string]$Branch = "",
    [switch]$CheckOnly
)

$ErrorActionPreference = "Stop"

if (-not $Root) {
    # 脚本默认位于 .github/scripts/update-readme.ps1，向上两级即为仓库根目录
    $Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}
$Root = (Resolve-Path -LiteralPath $Root).Path

# 解析 GitHub 仓库路径（owner/repo）
if (-not $Repo) {
    if ($env:GITHUB_REPOSITORY) {
        $Repo = $env:GITHUB_REPOSITORY.Trim()
    } else {
        $gitConfigPath = Join-Path $Root ".git/config"
        if (Test-Path -LiteralPath $gitConfigPath) {
            $gitConfig = [System.IO.File]::ReadAllText($gitConfigPath, [System.Text.Encoding]::UTF8)
            foreach ($line in ($gitConfig -split "\r?\n")) {
                if ($line -match 'url\s*=\s*.*github\.com[:/]([^/\s]+/[^/\s]+?)(?:\.git)?\s*$') {
                    $Repo = $Matches[1]
                    break
                }
            }
        }
        if (-not $Repo) {
            $Repo = "Starry-AXQG/WinLandPlugins-Dev"
        }
    }
}

# 解析分支名（用于拼装 Raw 下载直链）
if (-not $Branch) {
    if ($env:GITHUB_EVENT_NAME -ne "pull_request" -and $env:GITHUB_REF_NAME) {
        $Branch = $env:GITHUB_REF_NAME.Trim()
    } else {
        $gitHeadPath = Join-Path $Root ".git/HEAD"
        if (Test-Path -LiteralPath $gitHeadPath) {
            $headContent = [System.IO.File]::ReadAllText($gitHeadPath, [System.Text.Encoding]::UTF8).Trim()
            if ($headContent -match '^ref:\s*refs/heads/(.+)$') {
                $Branch = $Matches[1].Trim()
            }
        }
        if (-not $Branch) {
            $Branch = "main"
        }
    }
}

function Escape-MarkdownCell([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return "" }
    $normalized = ($text -replace "\r?\n", " ").Trim()
    return $normalized.Replace("|", "\|")
}

function Escape-HtmlAttr([string]$text) {
    if ([string]::IsNullOrEmpty($text)) { return "" }
    return $text.Replace("&", "&amp;").Replace('"', "&quot;").Replace("<", "&lt;").Replace(">", "&gt;")
}

function Encode-RelativeUrlPath([string]$relPath) {
    $segments = $relPath -split '/'
    $encoded = foreach ($seg in $segments) {
        [System.Uri]::EscapeDataString($seg)
    }
    return ($encoded -join '/')
}

function Get-DefaultEnglishName([string]$id, [string]$folderName) {
    if ($folderName -and ($folderName -cne $id) -and ($folderName -cmatch '[A-Z]')) {
        return $folderName
    }
    $words = ($id -split '[-_]+') | Where-Object { $_.Length -gt 0 } | ForEach-Object {
        if ($_.Length -eq 1) { $_.ToUpperInvariant() }
        else { $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1) }
    }
    return ($words -join ' ')
}

function Split-MarkdownRow([string]$line) {
    $trimmed = $line.Trim()
    if ($trimmed.StartsWith("|")) { $trimmed = $trimmed.Substring(1) }
    if ($trimmed.EndsWith("|") -and -not $trimmed.EndsWith("\|")) {
        $trimmed = $trimmed.Substring(0, $trimmed.Length - 1)
    }
    return @([regex]::Split($trimmed, '(?<!\\)\|') | ForEach-Object { $_.Trim() })
}

# 定位根目录 README 文件（兼容 README.MD / README.md 大小写）
$readmeFile = Get-ChildItem -LiteralPath $Root -File | Where-Object { $_.Name -ieq "README.md" } | Select-Object -First 1
if (-not $readmeFile) {
    Write-Error "未在根目录找到 README.md / README.MD：$Root"
    exit 1
}

$readmeText = [System.IO.File]::ReadAllText($readmeFile.FullName, [System.Text.Encoding]::UTF8)
$newline = if ($readmeText.Contains("`r`n")) { "`r`n" } else { "`n" }

$startMarker = "<!-- PLUGINS:START -->"
$endMarker = "<!-- PLUGINS:END -->"
$markerPattern = "(?s)" + [regex]::Escape($startMarker) + "(.*?)" + [regex]::Escape($endMarker)
$sectionTablePattern = "(?ms)(^##\s*插件列表\s*\r?\n(?:\r?\n)*)((?:\|[^\r\n]*(?:\r?\n|$))+)"

# 从现有表格中提取顺序与英文名以便保留
$existingTableText = ""
if ($readmeText -match $markerPattern) {
    $existingTableText = $Matches[1]
} elseif ($readmeText -match $sectionTablePattern) {
    $existingTableText = $Matches[2]
}

$existingOrder = @{}
$existingEnNames = @{}

if ($existingTableText) {
    $tableLines = @($existingTableText -split "\r?\n" | Where-Object { $_.Trim().StartsWith("|") })
    if ($tableLines.Count -ge 2) {
        $headerCells = Split-MarkdownRow $tableLines[0]
        $enCol = 2
        $idCol = 3
        for ($i = 0; $i -lt $headerCells.Count; $i++) {
            if ($headerCells[$i] -match 'English|英文') { $enCol = $i }
            elseif ($headerCells[$i] -match '插件\s*ID|^ID$') { $idCol = $i }
        }

        $orderIdx = 0
        for ($r = 2; $r -lt $tableLines.Count; $r++) {
            $cells = Split-MarkdownRow $tableLines[$r]
            if ($cells.Count -le [Math]::Max($enCol, $idCol)) { continue }
            $rowId = ($cells[$idCol] -replace '`', '').Trim()
            if (-not $rowId) { continue }

            if (-not $existingOrder.ContainsKey($rowId)) {
                $existingOrder[$rowId] = $orderIdx
                $orderIdx++
            }
            $rowEn = $cells[$enCol].Trim()
            if ($rowEn -and $rowEn -ne "-") {
                $existingEnNames[$rowId] = $rowEn
            }
        }
    }
}

# 枚举根目录下的所有 .lwp 包（大小写不敏感索引，保留真实文件名）
$rootPackages = @{}
foreach ($pkg in (Get-ChildItem -LiteralPath $Root -File -Filter "*.lwp")) {
    $rootPackages[$pkg.Name.ToLowerInvariant()] = $pkg.Name
}

$errors = @()
$plugins = @()

foreach ($dir in (Get-ChildItem -LiteralPath $Root -Directory | Sort-Object Name)) {
    $folderName = $dir.Name
    if ($folderName.StartsWith(".") -or $folderName.StartsWith("_")) { continue }

    $manifestFile = Get-ChildItem -LiteralPath $dir.FullName -File | Where-Object { $_.Name -ieq "plugin.json" } | Select-Object -First 1
    if (-not $manifestFile) { continue }

    $manifest = $null
    try {
        $rawJson = [System.IO.File]::ReadAllText($manifestFile.FullName, [System.Text.Encoding]::UTF8)
        $manifest = $rawJson | ConvertFrom-Json
    } catch {
        $errors += "$folderName/plugin.json 不是有效的 JSON：$($_.Exception.Message)"
        continue
    }

    $id = if ($null -ne $manifest.id) { "$($manifest.id)".Trim() } else { "" }
    $name = if ($null -ne $manifest.name) { "$($manifest.name)".Trim() } else { "" }
    $version = if ($null -ne $manifest.version) { "$($manifest.version)".Trim() } else { "" }
    $description = if ($null -ne $manifest.description) { "$($manifest.description)".Trim() } else { "" }

    if (-not $id) { $errors += "$folderName/plugin.json 缺少必填字段 id"; continue }
    if (-not $name) { $errors += "$folderName/plugin.json 缺少必填字段 name"; continue }
    if (-not $version) { $errors += "$folderName/plugin.json 缺少必填字段 version"; continue }

    # 解析英文名：plugin.json(name_en) -> 现有表格保留值 -> 目录名/ID 兜底
    $nameEn = ""
    if ($null -ne $manifest.PSObject.Properties["name_en"] -and "$($manifest.name_en)".Trim()) {
        $nameEn = "$($manifest.name_en)".Trim()
    } elseif ($null -ne $manifest.PSObject.Properties["nameEn"] -and "$($manifest.nameEn)".Trim()) {
        $nameEn = "$($manifest.nameEn)".Trim()
    } elseif ($existingEnNames.ContainsKey($id)) {
        $nameEn = $existingEnNames[$id]
    } else {
        $nameEn = Get-DefaultEnglishName -id $id -folderName $folderName
    }

    $dirFiles = @(Get-ChildItem -LiteralPath $dir.FullName -File)

    # 查找 Logo（优先 logo.png）
    $logoFile = $null
    foreach ($candidate in @("logo.png", "logo.svg", "logo.webp", "logo.jpg")) {
        $logoFile = $dirFiles | Where-Object { $_.Name -ieq $candidate } | Select-Object -First 1
        if ($logoFile) { break }
    }

    # 查找子目录 README
    $subReadmeFile = $dirFiles | Where-Object { $_.Name -ieq "README.md" } | Select-Object -First 1

    # 查找根目录 <id>.lwp 安装包
    $expectedPkgKey = "$id.lwp".ToLowerInvariant()
    $pkgFileName = if ($rootPackages.ContainsKey($expectedPkgKey)) { $rootPackages[$expectedPkgKey] } else { $null }

    $sortOrder = if ($existingOrder.ContainsKey($id)) { $existingOrder[$id] } else { [int]::MaxValue }

    $plugins += [PSCustomObject]@{
        Folder      = $folderName
        Id          = $id
        Name        = $name
        NameEn      = $nameEn
        Version     = $version
        Description = $description
        LogoFile    = if ($logoFile) { $logoFile.Name } else { $null }
        ReadmeFile  = if ($subReadmeFile) { $subReadmeFile.Name } else { $null }
        PackageFile = $pkgFileName
        SortOrder   = $sortOrder
    }

    $pkgStatus = if ($pkgFileName) { $pkgFileName } else { "无根目录安装包" }
    Write-Output "  ✓ $id v$version ($name / $nameEn) [$pkgStatus]"
}

if ($errors.Count -gt 0) {
    foreach ($err in $errors) { Write-Error $err -ErrorAction Continue }
    exit 1
}

$plugins = @($plugins | Sort-Object SortOrder, Id)

$rows = @()
$badgeImg = "https://img.shields.io/badge/%E4%B8%8B%E8%BD%BD-.lwp-0969da"

foreach ($p in $plugins) {
    $logoCell = if ($p.LogoFile) {
        $logoUrl = Encode-RelativeUrlPath "$($p.Folder)/$($p.LogoFile)"
        $alt = Escape-HtmlAttr $p.Name
        "<img src=`"$logoUrl`" width=`"64`" alt=`"$alt`">"
    } else {
        "-"
    }

    $nameCnCell = "**$(Escape-MarkdownCell $p.Name)**"
    $nameEnCell = if ($p.NameEn) { Escape-MarkdownCell $p.NameEn } else { "-" }
    $idCell = "``$($p.Id)``"
    $versionCell = "``$(Escape-MarkdownCell $p.Version)``"
    $descCell = if ($p.Description) { Escape-MarkdownCell $p.Description } else { "-" }

    $detailCell = if ($p.ReadmeFile) {
        $readmeUrl = Encode-RelativeUrlPath "$($p.Folder)/$($p.ReadmeFile)"
        "[README]($readmeUrl)"
    } else {
        "-"
    }

    $downloadCell = if ($p.PackageFile) {
        $encodedPkg = [System.Uri]::EscapeDataString($p.PackageFile)
        $rawUrl = "https://github.com/$Repo/raw/$Branch/$encodedPkg"
        "[![下载]($badgeImg)]($rawUrl)"
    } else {
        "-"
    }

    $rows += "| $logoCell | $nameCnCell | $nameEnCell | $idCell | $versionCell | $descCell | $detailCell | $downloadCell |"
}

if ($rows.Count -eq 0) {
    $rows += "| - | 暂无插件 | - | - | - | - | - | - |"
}

$tableLinesOut = @(
    "| Logo | 插件名（中文） | 插件名（English） | 插件 ID | 版本 | 说明 | 详情 | 下载 |",
    "| :---: | --- | --- | --- | :---: | --- | :---: | :---: |"
) + $rows

$newMarkedBlock = $startMarker + $newline + ($tableLinesOut -join $newline) + $newline + $endMarker

if ($CheckOnly) {
    Write-Output "校验通过（共 $($plugins.Count) 个插件），CheckOnly 模式下未修改 $($readmeFile.Name)。"
    exit 0
}

$updatedReadme = $null
if ($readmeText -match $markerPattern) {
    $updatedReadme = [regex]::Replace($readmeText, $markerPattern, { param($m) $newMarkedBlock }, 1)
} elseif ($readmeText -match $sectionTablePattern) {
    $updatedReadme = [regex]::Replace($readmeText, $sectionTablePattern, {
        param($m)
        $m.Groups[1].Value + $newMarkedBlock + $newline
    }, 1)
} else {
    Write-Error "$($readmeFile.Name) 中未找到 $startMarker 标记或「## 插件列表」表格，无法自动更新。"
    exit 1
}

if ($updatedReadme -cne $readmeText) {
    [System.IO.File]::WriteAllText($readmeFile.FullName, $updatedReadme, (New-Object System.Text.UTF8Encoding($false)))
    Write-Output "已更新 $($readmeFile.Name)（共 $($plugins.Count) 个插件）。"
} else {
    Write-Output "$($readmeFile.Name) 已是最新，无需改动。"
}
