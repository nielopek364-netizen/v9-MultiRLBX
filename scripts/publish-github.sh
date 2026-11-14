#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT_DIR"

REPO_URL=""
BRANCH="main"
COMMIT_MSG=""
TAG=""
NO_TAG=0
CREATE_REPO=0
VISIBILITY="--public"
DRY_RUN=0
AUTO_YES=0

usage() {
  cat <<'EOF'
Usage: ./scripts/publish-github.sh [options]

Options:
  -r, --repo <url>      GitHub remote URL (default: https://github.com/nielopek364-netizen/v9-MultiRLBX.git)
  -b, --branch <name>   Branch name (default: main)
  -m, --message <msg>   Commit message
  -t, --tag <tag>       Release tag (default: v<VERSION>)
      --no-tag          Skip creating/pushing a release tag
      --create          Create repository automatically with GitHub CLI (gh) if missing
      --private         Used with --create to make repository private
      --dry-run         Show what files would be staged without committing/pushing
  -y, --yes             Non-interactive mode (accept defaults)
  -h, --help            Show this help
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -r|--repo) REPO_URL="${2:-}"; shift 2 ;;
    -b|--branch) BRANCH="${2:-main}"; shift 2 ;;
    -m|--message) COMMIT_MSG="${2:-}"; shift 2 ;;
    -t|--tag) TAG="${2:-}"; shift 2 ;;
    --no-tag) NO_TAG=1; shift ;;
    --create) CREATE_REPO=1; shift ;;
    --private) VISIBILITY="--private"; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    -y|--yes) AUTO_YES=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "[ERROR] Unknown option: $1" >&2; usage; exit 1 ;;
  esac
done

if ! command -v git >/dev/null 2>&1; then
  echo "[ERROR] git is not installed." >&2
  exit 1
fi

VERSION="1.0.0"
if [[ -f "$ROOT_DIR/VERSION" ]]; then
  VERSION="$(tr -d '[:space:]' < "$ROOT_DIR/VERSION")"
fi
if [[ -z "$TAG" && "$NO_TAG" -eq 0 ]]; then
  TAG="v${VERSION}"
fi

DEFAULT_REPO="https://github.com/nielopek364-netizen/v9-MultiRLBX.git"
if existing="$(git remote get-url origin 2>/dev/null || true)"; then
  if [[ -n "$existing" ]]; then
    DEFAULT_REPO="$existing"
  fi
fi

echo "============================================================"
echo "  v9 MultiRLBX v${VERSION} - GitHub Publish Script"
echo "============================================================"

if [[ -z "$REPO_URL" ]]; then
  if [[ "$AUTO_YES" -eq 1 ]]; then
    REPO_URL="$DEFAULT_REPO"
  else
    read -r -p "GitHub repo URL [$DEFAULT_REPO]: " input_repo
    REPO_URL="${input_repo:-$DEFAULT_REPO}"
  fi
fi

DEFAULT_MSG="Release v9 MultiRLBX v${VERSION}"
if [[ -z "$COMMIT_MSG" ]]; then
  if [[ "$AUTO_YES" -eq 1 ]]; then
    COMMIT_MSG="$DEFAULT_MSG"
  else
    read -r -p "Commit message [$DEFAULT_MSG]: " input_msg
    COMMIT_MSG="${input_msg:-$DEFAULT_MSG}"
  fi
fi

if [[ -z "$(git config user.name || true)" ]]; then
  if [[ "$AUTO_YES" -eq 1 ]]; then
    git config user.name "v9pv"
  else
    read -r -p "Git user.name [v9pv]: " in_name
    git config user.name "${in_name:-v9pv}"
  fi
fi

if [[ -z "$(git config user.email || true)" ]]; then
  if [[ "$AUTO_YES" -eq 1 ]]; then
    git config user.email "v9pv@users.noreply.github.com"
  else
    read -r -p "Git user.email [v9pv@users.noreply.github.com]: " in_email
    git config user.email "${in_email:-v9pv@users.noreply.github.com}"
  fi
fi

if [[ ! -d "$ROOT_DIR/.git" ]]; then
  echo "==> Initializing Git repository ($BRANCH)"
  git init -b "$BRANCH" 2>/dev/null || { git init >/dev/null && git checkout -B "$BRANCH" >/dev/null; }
else
  git checkout -B "$BRANCH" >/dev/null
fi

if git remote get-url origin >/dev/null 2>&1; then
  git remote set-url origin "$REPO_URL"
else
  git remote add origin "$REPO_URL"
fi
echo "Remote origin -> $REPO_URL"

if [[ "$CREATE_REPO" -eq 1 ]] && command -v gh >/dev/null 2>&1; then
  if [[ "$REPO_URL" =~ github\.com[:/]([^/]+/[^/.]+)(\.git)?$ ]]; then
    SLUG="${BASH_REMATCH[1]}"
    if ! gh repo view "$SLUG" >/dev/null 2>&1; then
      if [[ "$DRY_RUN" -eq 1 ]]; then
        echo "[DRY-RUN] gh repo create $SLUG $VISIBILITY"
      else
        gh repo create "$SLUG" "$VISIBILITY"
      fi
    fi
  fi
fi

echo "==> Staging files (respecting .gitignore)"
if [[ "$DRY_RUN" -eq 1 ]]; then
  git add -n -A
  echo "[DRY-RUN] Done. No commit or push was made."
  exit 0
fi

git add -A

# Prevent accidental commit of user cookies, logs, or vault exports
while IFS= read -r path; do
  if [[ "$path" =~ (^|/)(accounts\.dat|settings\.json|\.env)$|\.(v9m|v9vault)$ ]]; then
    git reset -q HEAD -- "$path"
    echo "[WARN] Unstaged sensitive/user file: $path"
  fi
done < <(git diff --cached --name-only)

if git diff --cached --quiet; then
  if ! git rev-parse --verify HEAD >/dev/null 2>&1; then
    echo "[ERROR] Nothing to commit." >&2
    exit 1
  fi
  echo "Working tree clean - nothing new to commit."
else
  COUNT="$(git diff --cached --name-only | wc -l | tr -d ' ')"
  echo "Staged ${COUNT} files."
  echo "==> Committing: $COMMIT_MSG"
  git commit -m "$COMMIT_MSG"
fi

PUSH_TAG=0
if [[ "$NO_TAG" -eq 0 && -n "$TAG" ]]; then
  if [[ "$AUTO_YES" -eq 1 ]]; then
    PUSH_TAG=1
  else
    read -r -p "Create & push tag '$TAG' to trigger GitHub Actions .exe build? [Y/n]: " ans
    if [[ -z "$ans" || "$ans" =~ ^[YyTt] ]]; then
      PUSH_TAG=1
    fi
  fi
  if [[ "$PUSH_TAG" -eq 1 ]]; then
    git tag -f -a "$TAG" -m "v9 MultiRLBX $TAG"
  fi
fi

echo "==> Pushing branch '$BRANCH' to origin"
git push -u origin "$BRANCH"

if [[ "$PUSH_TAG" -eq 1 && -n "$TAG" ]]; then
  echo "==> Pushing tag '$TAG'"
  git push -f origin "$TAG"
fi

if command -v gh >/dev/null 2>&1 && [[ "$REPO_URL" =~ github\.com[:/]([^/]+/[^/.]+)(\.git)?$ ]]; then
  SLUG="${BASH_REMATCH[1]}"
  if gh auth status >/dev/null 2>&1; then
    echo "==> Updating GitHub repository metadata & topics ($SLUG)"
    gh repo edit "$SLUG" \
      --description "Lightweight C# 5 Roblox multi-instance launcher, Smart Eco optimizer, Game Picker & .v9m macro studio for Windows" \
      --add-topic "roblox,windows,csharp,dotnet,multi-instance,launcher,automation,macro" >/dev/null 2>&1 || true
  fi
fi

WEB_URL="${REPO_URL%.git}"
echo ""
echo "============================================================"
echo "  Done! Pushed to: $WEB_URL"
if [[ "$PUSH_TAG" -eq 1 ]]; then
  echo "  GitHub Actions Release CI: $WEB_URL/actions"
  echo "  GitHub Releases:           $WEB_URL/releases"
fi
echo "============================================================"
