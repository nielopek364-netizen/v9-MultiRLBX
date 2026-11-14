[CmdletBinding()]
param(
  [string]$RepoUrl = '',
  [string]$Branch = 'main',
  [string]$CommitMessage = '',
  [string]$Tag = '',
  [switch]$NoTag,
  [switch]$CreateRepo,
  [switch]$Private,
  [switch]$DryRun,
  [switch]$Yes
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Write-Step([string]$msg) {
  Write-Host ''
  Write-Host ('==> ' + $msg) -ForegroundColor Cyan
}

function Fail([string]$msg) {
  Write-Host ('[ERROR] ' + $msg) -ForegroundColor Red
  exit 1
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
  Fail 'Git is not installed or not on PATH. Install Git for Windows: https://git-scm.com/download/win'
}

$version = '1.0.0'
$versionPath = Join-Path $root 'VERSION'
if (Test-Path $versionPath) {
  $rawVer = (Get-Content -Raw -Path $versionPath).Trim()
  if ($rawVer) { $version = $rawVer }
}

if (-not $Tag -and -not $NoTag) {
  $Tag = 'v' + $version
}

$defaultRepo = 'https://github.com/nielopek364-netizen/v9-MultiRLBX.git'
if (-not $RepoUrl) {
  try {
    $existingOrigin = (git remote get-url origin 2>$null)
    if ($LASTEXITCODE -eq 0 -and $existingOrigin) {
      $defaultRepo = $existingOrigin.Trim()
    }
  } catch {}
}

Write-Host '============================================================' -ForegroundColor Green
Write-Host ('  v9 MultiRLBX v' + $version + ' - GitHub Publish Script') -ForegroundColor Green
Write-Host '============================================================' -ForegroundColor Green

if (-not $RepoUrl) {
  if ($Yes) {
    $RepoUrl = $defaultRepo
  } else {
    $inputUrl = Read-Host ('GitHub repo URL [' + $defaultRepo + ']')
    $RepoUrl = if ([string]::IsNullOrWhiteSpace($inputUrl)) { $defaultRepo } else { $inputUrl.Trim() }
  }
}

if (-not $CommitMessage) {
  $defaultMsg = 'Release v9 MultiRLBX v' + $version
  if ($Yes) {
    $CommitMessage = $defaultMsg
  } else {
    $inputMsg = Read-Host ('Commit message [' + $defaultMsg + ']')
    $CommitMessage = if ([string]::IsNullOrWhiteSpace($inputMsg)) { $defaultMsg } else { $inputMsg.Trim() }
  }
}

Write-Step 'Checking Git repository & identity'
$gitName = (git config user.name 2>$null)
if (-not $gitName) {
  if ($Yes) {
    git config user.name 'v9pv'
  } else {
    $inName = Read-Host 'Git user.name [v9pv]'
    git config user.name ($(if ([string]::IsNullOrWhiteSpace($inName)) { 'v9pv' } else { $inName.Trim() }))
  }
}

$gitEmail = (git config user.email 2>$null)
if (-not $gitEmail) {
  if ($Yes) {
    git config user.email 'v9pv@users.noreply.github.com'
  } else {
    $inEmail = Read-Host 'Git user.email [v9pv@users.noreply.github.com]'
    git config user.email ($(if ([string]::IsNullOrWhiteSpace($inEmail)) { 'v9pv@users.noreply.github.com' } else { $inEmail.Trim() }))
  }
}

if (-not (Test-Path (Join-Path $root '.git'))) {
  Write-Host ('Initializing new git repository on branch ' + $Branch + '...')
  git init -b $Branch 2>$null
  if ($LASTEXITCODE -ne 0) {
    git init | Out-Null
    git checkout -B $Branch | Out-Null
  }
} else {
  $curBranch = (git rev-parse --abbrev-ref HEAD 2>$null)
  if ($curBranch -ne $Branch) {
    git checkout -B $Branch | Out-Null
  }
}

$hasOrigin = $false
try {
  $null = git remote get-url origin 2>$null
  if ($LASTEXITCODE -eq 0) { $hasOrigin = $true }
} catch {}

if ($hasOrigin) {
  git remote set-url origin $RepoUrl
} else {
  git remote add origin $RepoUrl
}
Write-Host ('Remote origin -> ' + $RepoUrl)

if ($CreateRepo -and (Get-Command gh -ErrorAction SilentlyContinue)) {
  if ($RepoUrl -match 'github\.com[:/]([^/]+/[^/.]+)(?:\.git)?$') {
    $slug = $Matches[1]
    $visFlag = if ($Private) { '--private' } else { '--public' }
    Write-Step ('Ensuring GitHub repository exists: ' + $slug)
    $null = gh repo view $slug 2>$null
    if ($LASTEXITCODE -ne 0) {
      if ($DryRun) {
        Write-Host ('[DRY-RUN] gh repo create ' + $slug + ' ' + $visFlag)
      } else {
        gh repo create $slug $visFlag
      }
    }
  }
}

Write-Step 'Staging project files (respecting .gitignore)'
if ($DryRun) {
  git add -n -A
  Write-Host ''
  Write-Host '[DRY-RUN] Complete. No commit or push was executed.' -ForegroundColor Yellow
  exit 0
}

git add -A

# Guard against accidental secret or user data files
$staged = git diff --cached --name-only
$blocked = $staged | Where-Object { $_ -match '(^|/)(accounts\.dat|settings\.json|\.env)$|\.v9m$|\.v9vault$' }
if ($blocked) {
  git reset HEAD -- $blocked | Out-Null
  Write-Host ('Unstaged sensitive/user files: ' + ($blocked -join ', ')) -ForegroundColor Yellow
  $staged = git diff --cached --name-only
}

if (-not $staged) {
  $statusHead = (git rev-parse --verify HEAD 2>$null)
  if (-not $statusHead) {
    Fail 'Nothing to commit.'
  }
  Write-Host 'Working tree clean - nothing new to commit.' -ForegroundColor Gray
} else {
  $fileCount = ($staged | Measure-Object).Count
  Write-Host ('Staged ' + $fileCount + ' files:')
  $staged | Select-Object -First 25 | ForEach-Object { Write-Host ('  + ' + $_) -ForegroundColor DarkGray }
  if ($fileCount -gt 25) {
    Write-Host ('  ... and ' + ($fileCount - 25) + ' more files') -ForegroundColor DarkGray
  }

  Write-Step ('Creating commit: "' + $CommitMessage + '"')
  git commit -m $CommitMessage
  if ($LASTEXITCODE -ne 0) { Fail 'git commit failed.' }
}

$pushTag = $false
if (-not $NoTag -and $Tag) {
  if (-not $Yes) {
    $ans = Read-Host ('Create & push release tag "' + $Tag + '" to trigger GitHub Actions .exe build? [Y/n]')
    if ([string]::IsNullOrWhiteSpace($ans) -or $ans -match '^(y|yes|t|tak)') {
      $pushTag = $true
    }
  } else {
    $pushTag = $true
  }

  if ($pushTag) {
    Write-Step ('Tagging ' + $Tag)
    git tag -f -a $Tag -m ('v9 MultiRLBX ' + $Tag)
  }
}

Write-Step ('Pushing branch "' + $Branch + '" to origin')
git push -u origin $Branch
if ($LASTEXITCODE -ne 0) {
  Write-Host ''
  Write-Host 'Hint: If the remote repo was initialized on GitHub with a README/LICENSE,' -ForegroundColor Yellow
  Write-Host ('      run: git pull --rebase origin ' + $Branch + '  and re-run this script.') -ForegroundColor Yellow
  Fail 'git push failed.'
}

if ($pushTag -and $Tag) {
  Write-Step ('Pushing release tag ' + $Tag)
  git push -f origin $Tag
  if ($LASTEXITCODE -ne 0) { Fail 'Tag push failed.' }
}

if ((Get-Command gh -ErrorAction SilentlyContinue) -and ($RepoUrl -match 'github\.com[:/]([^/]+/[^/.]+)(?:\.git)?$')) {
  $slug = $Matches[1]
  try {
    $null = gh auth status 2>$null
    if ($LASTEXITCODE -eq 0) {
      Write-Step ('Updating GitHub repository settings & topics (' + $slug + ')')
      gh repo edit $slug --description 'Lightweight C# 5 Roblox multi-instance launcher, Smart Eco optimizer, Game Picker & .v9m macro studio for Windows' --add-topic 'roblox,windows,csharp,dotnet,multi-instance,launcher,automation,macro' 2>$null | Out-Null
    }
  } catch {}
}

$webUrl = $RepoUrl -replace '\.git$', ''
Write-Host ''
Write-Host '============================================================' -ForegroundColor Green
Write-Host ('  Done! Pushed to: ' + $webUrl) -ForegroundColor Green
if ($pushTag -and $Tag) {
  Write-Host ('  GitHub Actions Release CI: ' + $webUrl + '/actions') -ForegroundColor Green
  Write-Host ('  GitHub Releases:           ' + $webUrl + '/releases') -ForegroundColor Green
}
Write-Host '============================================================' -ForegroundColor Green

